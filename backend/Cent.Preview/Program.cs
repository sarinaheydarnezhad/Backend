using Cent.Module;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
    Args = args,
    EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Development
});
if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Cent.Preview is for local development. Deploy Flashcards.Api for the shared production backend.");

builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5108");
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
    ["Cent:Enabled"] = "true",
    ["Cent:Payments:AllowDevelopmentMock"] = "true",
    ["Cent:ClientBaseUrl"] = builder.Configuration["Cent:ClientBaseUrl"] ?? "http://localhost:5173/cent-s",
    ["Cent:PublicApiBaseUrl"] = builder.Configuration["Cent:PublicApiBaseUrl"] ?? "http://127.0.0.1:5108"
});
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
builder.Services.AddCentModule(builder.Configuration);
builder.Services.Configure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(PreviewRateLimits.Configure);

// Keep local data inside this checkout, independent of the shell's working directory.
var sourceDirectory = new DirectoryInfo(AppContext.BaseDirectory);
while (sourceDirectory is not null && !File.Exists(Path.Combine(sourceDirectory.FullName, "Cent.Preview.csproj")))
    sourceDirectory = sourceDirectory.Parent;
var dataDirectory = Path.Combine(sourceDirectory?.FullName ?? AppContext.BaseDirectory, "App_Data");
Directory.CreateDirectory(dataDirectory);
var databasePath = Path.Combine(dataDirectory, "preview.db");
var isolatedPath = builder.Configuration["isolated-database"];
if (isolatedPath is not null) {
    databasePath = Path.GetFullPath(isolatedPath);
    var allowed = Path.GetFullPath(Path.Combine(dataDirectory, "isolated")) + Path.DirectorySeparatorChar;
    if (!databasePath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || File.Exists(databasePath))
        throw new InvalidOperationException("Use a new disposable database under Cent.Preview/App_Data/isolated. Existing files are never overwritten.");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
}
var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
// Keep the framework's background key warmup inside the disposable preview as
// well; never inspect or write the user's shared Windows key ring.
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(
    Path.Combine(Path.GetDirectoryName(databasePath)!, "preview-keys"))).UseEphemeralDataProtectionProvider();

// Optional, one-time online SQLite backup preserves an existing local preview without overwriting data.
var importPath = builder.Configuration["import-preview-db"];
if (!string.IsNullOrWhiteSpace(importPath)) {
    if (File.Exists(databasePath)) throw new InvalidOperationException("The destination preview database already exists; it will not be overwritten.");
    if (!File.Exists(importPath)) throw new FileNotFoundException("The preview database to import was not found.", importPath);
    using var source = new SqliteConnection(new SqliteConnectionStringBuilder {
        DataSource = Path.GetFullPath(importPath), Mode = SqliteOpenMode.ReadOnly
    }.ToString());
    using var destination = new SqliteConnection(connectionString);
    source.Open(); destination.Open(); source.BackupDatabase(destination);
}

builder.Services.RemoveAll<CentDbContext>();
builder.Services.RemoveAll<DbContextOptions<CentDbContext>>();
builder.Services.AddScoped<CentDbContext>(_ => new PreviewDb(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connectionString).Options));
builder.Services.AddOpenApi(options => {
    options.AddDocumentTransformer((document, _, _) => {
        document.Info.Title = "Cent Local Preview API";
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["CentSession"] = new OpenApiSecurityScheme {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "Sign in through the Cent SMS, password, or Google flow, then paste the returned Cent token here."
        };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) => {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Any()) {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("CentSession", context.Document)] = [] });
        }
        return Task.CompletedTask;
    });
});

var app = builder.Build();
app.UseRouting();
app.UseCentModule();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapCentModule();
app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Cent API v1"));
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
    await db.Database.EnsureCreatedAsync();
    if (builder.Configuration.GetValue<bool>("upgrade-google-auth"))
        await GoogleAuthUpgrade.Apply((PreviewDb)db, databasePath);
    if (builder.Configuration.GetValue<bool>("upgrade-sms-auth"))
        await SmsAuthUpgrade.Apply((PreviewDb)db, databasePath);
    if (builder.Configuration.GetValue<bool>("upgrade-curriculum"))
        await CurriculumUpgrade.Apply((PreviewDb)db, databasePath);
    // No automatic administrator or known password. Explicit disposable fixtures
    // are permitted only in a brand-new isolated development database.
    if (builder.Configuration.GetValue<bool>("admin-fixtures")) {
        if (isolatedPath is null) throw new InvalidOperationException("Admin fixtures require a new isolated database.");
        var fixtures = new List<object>();
        foreach (var role in CentAdminSecurity.Roles.Append("STUDENT")) {
            var user = new CentUser { Name = role == "STUDENT" ? "دانشجوی آزمایشی" : "مدیر آزمایشی", Email = role.ToLowerInvariant() + "@example.invalid", NormalizedEmail = role.ToUpperInvariant() + "@EXAMPLE.INVALID", Role = role, GoogleSubject = "DISPOSABLE-NOT-GOOGLE:" + Guid.NewGuid() };
            db.Users.Add(user);
            var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var expiresAt = DateTimeOffset.UtcNow.AddHours(4);
            db.Sessions.Add(new CentSession { UserId = user.Id, TokenHash = CentModule.Hash(token), ExpiresAt = expiresAt });
            fixtures.Add(new { role, user.Id, token, expiresAt });
        }
        await db.SaveChangesAsync();
        var fixtureOwner = await db.Users.SingleAsync(u => u.Role == "STUDENT");
        var fixtureTraining = new CentTraining { Title = "آموزش آزمایشی مدیریت", Description = "فقط داده ساختگی پایگاه جداگانه", Published = true };
        var fixturePlan = new CentProduct { Kind = "SUBSCRIPTION", Title = "اشتراک آزمایشی سی‌روزه", PriceRial = 1000000, DurationDays = 30, Active = false };
        db.Trainings.Add(fixtureTraining); db.Products.Add(fixturePlan); await db.SaveChangesAsync();
        for (var i = 0; i < 5; i++) {
            var paid = i < 4; var created = DateTimeOffset.UtcNow.AddDays(-i - 1);
            var order = new CentOrder { UserId = fixtureOwner.Id, ProductId = fixturePlan.Id, RequestId = Guid.NewGuid(), Title = fixturePlan.Title, Kind = "SUBSCRIPTION", DurationDays = 30, AmountRial = fixturePlan.PriceRial, Provider = "MOCK", Status = paid ? "PAID" : "PENDING", Reference = paid ? "DISPOSABLE-" + Guid.NewGuid() : null, CreatedAt = created, PaidAt = paid ? created : null };
            db.Orders.Add(order);
            if (paid) {
                var starts = i == 1 ? created.AddDays(-60) : i == 2 ? created.AddDays(35) : created;
                db.Entitlements.Add(new CentEntitlement { UserId = fixtureOwner.Id, OrderId = order.Id, Kind = "SUBSCRIPTION", StartsAt = starts, ExpiresAt = starts.AddDays(30), RevokedAt = i == 3 ? created : null });
                db.PaymentEvents.Add(new() { OrderId = order.Id, Action = "FIXTURE_ONLY", Code = 0, CreatedAt = created });
            }
        }
        await db.SaveChangesAsync();
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(databasePath)!, "admin-fixtures.json"), System.Text.Json.JsonSerializer.Serialize(fixtures));
        Console.WriteLine("Disposable admin fixture sessions written beside isolated database; never use as identity verification evidence.");
    }
    if (builder.Configuration.GetValue<bool>("import-curriculum")) {
        // SQLite online backup includes WAL state. Verify the copy before an
        // existing local content store can enter the reviewed import transaction.
        using (var backupSource = new SqliteConnection(connectionString)) {
            backupSource.Open();
            using var existingContent = backupSource.CreateCommand();
            existingContent.CommandText = "SELECT COUNT(*) FROM cent_sources";
            if (Convert.ToInt32(existingContent.ExecuteScalar()) > 0) {
                var backupPath = databasePath + ".before-reviewed-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak";
                using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString())) {
                    destination.Open(); backupSource.BackupDatabase(destination);
                    using var check = destination.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
                    if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.Ordinal)) throw new InvalidOperationException("Local content backup failed integrity verification.");
                }
                var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(backupPath)));
                await File.WriteAllTextAsync(backupPath + ".sha256", digest);
                Console.WriteLine("Verified private local content backup: " + Path.GetFileName(backupPath));
            }
        }
        var report = await new CentCurriculumImporter(db).Import();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { reviewedPackageHash = CentReviewedContent.Hash,
            sources = await db.Sources.CountAsync(), sections = await db.SourceSections.CountAsync(),
            exercises = await db.SourceExercises.CountAsync(), questions = await db.Questions.CountAsync() }));
    }
    if (builder.Configuration.GetValue<bool>("prepare-subject-offers")) await CentSubjectCatalog.PrepareOffers(db);
    if (builder.Configuration.GetValue<bool>("reviewed-reader-fixtures")) {
        if (isolatedPath is null) throw new InvalidOperationException("Reviewed reader fixtures require a new isolated database.");
        var ids = CentReviewedContent.Content.Changes.Where(c => c.Entity == "documents" && c.Before is null).Select(c => c.Id).ToArray();
        foreach (var document in await db.Documents.Where(d => ids.Contains(d.Id)).ToListAsync()) document.Published = true;
        await db.SaveChangesAsync();
    }
    if (builder.Configuration.GetValue<bool>("sandbox-subject-offers")) {
        if (isolatedPath is null || !scope.ServiceProvider.GetRequiredService<CentGateway>().IsMock)
            throw new InvalidOperationException("Sandbox offers require a new disposable database and the development fake gateway.");
        foreach (var offer in await db.Set<CentSubjectOffer>().ToListAsync()) {
            offer.Available = true; offer.ReviewedAt = DateTimeOffset.UtcNow;
            offer.ReviewedBy = "LOCAL PAYMENT TEST ONLY; independent academic acceptance remains pending";
            (await db.Products.FindAsync(offer.ProductId))!.Active = true;
        }
        await db.SaveChangesAsync();
        Console.WriteLine("LOCAL SANDBOX: subject offers are for fake payments only; this is not academic or production acceptance.");
    }
}
// One-shot maintenance never needs to bind an HTTP port or leave upgrade flags
// on a long-running development server command line.
if (builder.Configuration.GetValue<bool>("exit-after-setup")) return;
await app.RunAsync();

public sealed class PreviewDb(DbContextOptions<CentDbContext> options) : CentDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        var converter = new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        foreach (var property in builder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
            if ((Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(DateTimeOffset))
                property.SetValueConverter(converter);
    }
}
