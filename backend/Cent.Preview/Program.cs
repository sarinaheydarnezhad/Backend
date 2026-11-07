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
    ["Cent:ClientBaseUrl"] = "http://localhost:5173/cent-s",
    ["Cent:PublicApiBaseUrl"] = "http://127.0.0.1:5108"
});
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
builder.Services.AddCentModule(builder.Configuration);
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();

// Keep local data inside this checkout, independent of the shell's working directory.
var sourceDirectory = new DirectoryInfo(AppContext.BaseDirectory);
while (sourceDirectory is not null && !File.Exists(Path.Combine(sourceDirectory.FullName, "Cent.Preview.csproj")))
    sourceDirectory = sourceDirectory.Parent;
var dataDirectory = Path.Combine(sourceDirectory?.FullName ?? AppContext.BaseDirectory, "App_Data");
Directory.CreateDirectory(dataDirectory);
var databasePath = Path.Combine(dataDirectory, "preview.db");
var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

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
            Description = "Log in with /api/cent/v1/auth/login, then paste the returned token here."
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
    if (builder.Configuration.GetValue<bool>("upgrade-curriculum"))
        await CurriculumUpgrade.Apply((PreviewDb)db, databasePath);
    if (!await db.Users.AnyAsync()) {
        var admin = new CentUser {
            Name = "مدیر آزمایش", Email = "admin@cent.local", NormalizedEmail = "ADMIN@CENT.LOCAL", Role = "ADMIN"
        };
        admin.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<CentUser>>()
            .HashPassword(admin, "cent-local-preview-only");
        db.Users.Add(admin);
        await db.SaveChangesAsync();
    }
    if (builder.Configuration.GetValue<bool>("import-curriculum")) {
        var report = await new CentCurriculumImporter(db).Import();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
    }
}
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
