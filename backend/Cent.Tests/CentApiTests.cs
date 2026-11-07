using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cent.Module;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cent.Tests;

public sealed class CentApiTests
{
    [Fact]
    public async Task Disabled_module_preserves_Lerona_routes_and_exposes_no_Cent_routes()
    {
        using var factory = new CentFactory(enabled: false);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/cent/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Accounts_are_separate_and_students_cannot_assign_admin_or_access_admin_routes()
    {
        using var factory = new CentFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/cent/v1/admin/trainings")).StatusCode);
        var response = await client.PostAsJsonAsync("/api/cent/v1/auth/register", new { email = "student@example.com", password = "long-test-password", name = "Student", role = "ADMIN" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = await Json(response);
        Assert.Equal("STUDENT", auth.GetProperty("user").GetProperty("role").GetString());
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/cent/v1/admin/trainings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/cent/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/cent/v1/auth/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/cent/v1/me")).StatusCode);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
        Assert.DoesNotContain("long-test-password", (await db.Users.SingleAsync()).PasswordHash);
        Assert.NotEqual(auth.GetProperty("token").GetString(), (await db.Sessions.SingleAsync()).TokenHash);
    }

    [Fact]
    public async Task Verified_purchase_is_permanent_idempotent_and_owner_scoped()
    {
        using var factory = new CentFactory();
        using var student = await Register(factory, "buyer@example.com");
        using var other = await Register(factory, "other@example.com");
        var content = await SeedContent(factory);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync($"/api/cent/v1/documents/{content.DocId}/pages/1")).StatusCode);
        var request = Guid.NewGuid();
        var purchase = await Json(await student.PostAsJsonAsync("/api/cent/v1/checkout", new { productId = content.ProductId, requestId = request, amountRial = 1, userId = Guid.NewGuid() }));
        var orderId = purchase.GetProperty("id").GetGuid();
        var retry = await Json(await student.PostAsJsonAsync("/api/cent/v1/checkout", new { productId = content.ProductId, requestId = request }));
        Assert.Equal(orderId, retry.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await student.GetAsync($"/api/cent/v1/documents/{content.DocId}/pages/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/cent/v1/documents/{content.DocId}/pages/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/cent/v1/me/orders/{orderId}/verify", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await student.PostAsJsonAsync($"/api/cent/v1/me/orders/{orderId}/verify", new { })).StatusCode);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
        Assert.Equal(250000, (await db.Orders.SingleAsync()).AmountRial);
        Assert.Single(await db.Entitlements.ToListAsync()); Assert.Null((await db.Entitlements.SingleAsync()).ExpiresAt);
        var page = await student.GetAsync($"/api/cent/v1/documents/{content.DocId}/pages/1");
        Assert.Equal("image/webp", page.Content.Headers.ContentType!.MediaType);
        Assert.True(page.Headers.CacheControl!.NoStore); Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await student.GetAsync($"/api/cent/v1/documents/{content.DocId}/original.pdf")).StatusCode);
    }

    [Fact]
    public async Task Subscription_renewals_stack_and_expiry_blocks_pages_but_preserves_private_notes()
    {
        using var factory = new CentFactory();
        using var client = await Register(factory, "subscriber@example.com");
        using var stranger = await Register(factory, "stranger@example.com");
        var content = await SeedContent(factory);
        Guid subId;
        using (var scope = factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
            var product = new CentProduct { Title = "Monthly", Kind = "SUBSCRIPTION", PriceRial = 100000, DurationDays = 30, Active = true };
            db.Products.Add(product); await db.SaveChangesAsync(); subId = product.Id;
        }
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/cent/v1/checkout", new { productId = subId, requestId = Guid.NewGuid() })).StatusCode);
        var noteId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/cent/v1/notes/{noteId}", new { documentId = content.DocId, page = 1, anchorY = 0.3, body = "My private study note", revision = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/cent/v1/notes/{noteId}", new { documentId = content.DocId, page = 1, anchorY = 0.3, body = "stale edit", revision = 0 })).StatusCode);
        Assert.Empty((await Json(await stranger.GetAsync("/api/cent/v1/me/notes"))).EnumerateArray());
        using (var scope = factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
            var entitlements = await db.Entitlements.OrderBy(e => e.StartsAt).ToListAsync();
            Assert.Equal(entitlements[0].ExpiresAt, entitlements[1].StartsAt);
            Assert.True(entitlements[1].ExpiresAt > DateTimeOffset.UtcNow.AddDays(59));
            foreach (var grant in entitlements) { grant.StartsAt = DateTimeOffset.UtcNow.AddDays(-60); grant.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1); }
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/cent/v1/documents/{content.DocId}/pages/1")).StatusCode);
        var notes = await Json(await client.GetAsync("/api/cent/v1/me/notes"));
        Assert.Equal("My private study note", notes[0].GetProperty("body").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/cent/v1/notes/{noteId}", new { documentId = content.DocId, page = 1, anchorY = 0.3, body = "updated", revision = 1 })).StatusCode);
    }

    [Fact]
    public async Task Notes_cannot_be_read_modified_or_deleted_by_another_account()
    {
        using var factory = new CentFactory(); using var first = await Register(factory, "first@example.com"); using var second = await Register(factory, "second@example.com");
        var content = await SeedContent(factory);
        foreach (var client in new[] { first, second }) await client.PostAsJsonAsync("/api/cent/v1/checkout", new { productId = content.ProductId, requestId = Guid.NewGuid() });
        var id = Guid.NewGuid();
        var input = new { documentId = content.DocId, page = 1, anchorY = 0.2, body = "only mine", revision = 0 };
        Assert.Equal(HttpStatusCode.OK, (await first.PutAsJsonAsync($"/api/cent/v1/notes/{id}", input)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await second.PutAsJsonAsync($"/api/cent/v1/notes/{id}", input)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await second.DeleteAsync($"/api/cent/v1/notes/{id}?revision=1")).StatusCode);
        Assert.Equal("only mine", (await Json(await first.GetAsync("/api/cent/v1/me/notes")))[0].GetProperty("body").GetString());
    }

    [Fact]
    public async Task Answers_are_hidden_until_submission_and_snapshot_controls_scoring()
    {
        using var factory = new CentFactory(); using var client = await Register(factory, "exam@example.com");
        Guid questionId;
        using (var scope = factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>(); var q = new CentQuestion { Subject = "math", Topic = "Algebra", Text = "2 + 2?", OptionsJson = "[\"3\",\"4\"]", CorrectAnswer = 1, Explanation = "Four", Active = true };
            db.Questions.Add(q); await db.SaveChangesAsync(); questionId = q.Id;
        }
        var started = await client.PostAsJsonAsync("/api/cent/v1/attempts/", new { kind = "diagnostic", requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var text = await started.Content.ReadAsStringAsync(); Assert.DoesNotContain("correctAnswer", text); Assert.DoesNotContain("explanation", text);
        var id = JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
        using (var scope = factory.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<CentDbContext>(); (await db.Questions.SingleAsync()).CorrectAnswer = 0; await db.SaveChangesAsync(); }
        var invalid = await client.PostAsJsonAsync($"/api/cent/v1/attempts/{id}/submit", new { answers = new Dictionary<Guid, int?> { [Guid.NewGuid()] = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var result = await Json(await client.PostAsJsonAsync($"/api/cent/v1/attempts/{id}/submit", new { answers = new Dictionary<Guid, int?> { [questionId] = 1 }, percentage = 0 }));
        Assert.Equal(100, result.GetProperty("result").GetProperty("percentage").GetInt32());
        var repeat = await Json(await client.PostAsJsonAsync($"/api/cent/v1/attempts/{id}/submit", new { answers = new Dictionary<Guid, int?> { [questionId] = 0 } }));
        Assert.Equal(100, repeat.GetProperty("result").GetProperty("percentage").GetInt32());
    }

    [Fact]
    public async Task Production_cannot_use_development_mock_or_create_paid_orders_without_gateway_configuration()
    {
        using var factory = new CentFactory(production: true); using var client = await Register(factory, "production@example.com");
        var content = await SeedContent(factory);
        var response = await client.PostAsJsonAsync("/api/cent/v1/checkout", new { productId = content.ProductId, requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
        Assert.Empty(await db.Orders.ToListAsync()); Assert.Empty(await db.Entitlements.ToListAsync());
    }

    [Fact]
    public async Task Administrator_upload_requires_all_pages_and_published_pages_are_immutable()
    {
        using var factory = new CentFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Cent-Bootstrap", "cent-test-bootstrap-token-with-32-characters");
        var auth = await Json(await client.PostAsJsonAsync("/api/cent/v1/auth/bootstrap", new { email = "admin@example.com", password = "long-test-password", name = "Admin" }));
        client.DefaultRequestHeaders.Remove("X-Cent-Bootstrap"); client.DefaultRequestHeaders.Authorization = new("Bearer", auth.GetProperty("token").GetString());
        var trainingId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/cent/v1/admin/trainings/{trainingId}", new { title = "Algebra", description = "Training", includedInSubscription = true, published = true })).StatusCode);
        var document = await Json(await client.PostAsJsonAsync("/api/cent/v1/admin/documents", new { trainingId, title = "Lesson one", pageCount = 2, sortOrder = 0 }));
        var id = document.GetProperty("id").GetGuid();
        async Task<HttpResponseMessage> Upload(int number) {
            var body = new ByteArrayContent("RIFF0000WEBP0000000000"u8.ToArray());
            body.Headers.ContentType = new("image/webp");
            return await client.PutAsync($"/api/cent/v1/admin/documents/{id}/pages/{number}?width=100&height=100", body);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await Upload(1)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/cent/v1/admin/documents/{id}/publish", new { sections = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Upload(2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/cent/v1/admin/documents/{id}/publish", new { sections = new[] { new { title = "Introduction", startPage = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(1)).StatusCode);
    }

    [Fact]
    public async Task Cent_has_its_own_Cors_policy_and_authenticated_preflight_succeeds()
    {
        using var factory = new CentFactory(); using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/cent/v1/me");
        var policy = await factory.Services.GetRequiredService<Microsoft.AspNetCore.Cors.Infrastructure.ICorsPolicyProvider>()
            .GetPolicyAsync(new Microsoft.AspNetCore.Http.DefaultHttpContext(), "CentOrigins");
        Assert.Contains("https://cent.example", policy!.Origins);
        request.Headers.Add("Origin", "https://cent.example"); request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("https://cent.example", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        using var untrusted = new HttpRequestMessage(HttpMethod.Options, "/api/cent/v1/me");
        untrusted.Headers.Add("Origin", "https://untrusted.example"); untrusted.Headers.Add("Access-Control-Request-Method", "GET");
        Assert.False((await client.SendAsync(untrusted)).Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public void All_Cent_tables_foreign_keys_and_migration_history_are_separate()
    {
        using var db = new CentDbContextFactory().CreateDbContext([]);
        Assert.All(db.Model.GetEntityTypes(), e => {
            Assert.StartsWith("cent_", e.GetTableName());
            Assert.All(e.GetForeignKeys(), fk => Assert.StartsWith("cent_", fk.PrincipalEntityType.GetTableName()));
        });
        var sql = db.Database.GenerateCreateScript();
        Assert.DoesNotContain("CREATE TABLE [Users]", sql);
        Assert.Contains("CREATE TABLE [cent_users]", sql);
    }

    private static async Task<HttpClient> Register(CentFactory factory, string email)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/cent/v1/auth/register", new { email, password = "long-test-password", name = "Student" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await Json(response)).GetProperty("token").GetString());
        return client;
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }
    private static async Task<(Guid DocId, Guid ProductId)> SeedContent(CentFactory factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
        var training = new CentTraining { Title = "Algebra", Published = true, IncludedInSubscription = true };
        var doc = new CentDocument { TrainingId = training.Id, Title = "Chapter one", Published = true, PageCount = 1 };
        var product = new CentProduct { Title = "Algebra purchase", TrainingId = training.Id, PriceRial = 250000, Active = true };
        db.AddRange(training, doc, product, new CentDocumentPage { DocumentId = doc.Id, Number = 1, Width = 100, Height = 100, Image = "RIFF0000WEBPtest"u8.ToArray() });
        await db.SaveChangesAsync(); return (doc.Id, product.Id);
    }
}

public sealed class CentFactory : WebApplicationFactory<Program>
{
    private readonly bool enabled;
    private readonly bool production;
    private readonly Dictionary<string, string?> previous = new();
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    public CentFactory(bool enabled = true, bool production = false)
    {
        this.enabled = enabled; this.production = production;
        // Minimal hosting reads these before WebApplicationFactory applies ConfigureAppConfiguration.
        var values = new Dictionary<string, string?> {
            ["Auth__Issuer"] = "Flashcards.Api", ["Auth__Audience"] = "Flashcards.Mobile",
            ["Auth__SigningKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x5a, 32).ToArray()),
            ["Cent__Enabled"] = enabled.ToString(), ["Cent__Payments__AllowDevelopmentMock"] = "true",
            ["Cent__ClientBaseUrl"] = "https://cent.example", ["Cent__PublicApiBaseUrl"] = "https://api.example",
            ["Cent__AllowedOrigins__0"] = "https://cent.example",
            ["Cent__Payments__MerchantId"] = "", ["Cent__BootstrapToken"] = "cent-test-bootstrap-token-with-32-characters"
        };
        foreach (var pair in values) { previous[pair.Key] = Environment.GetEnvironmentVariable(pair.Key); Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(production ? "Production" : "Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Auth:SigningKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x5a, 32).ToArray()),
            ["Cent:Enabled"] = enabled.ToString(), ["Cent:Payments:AllowDevelopmentMock"] = "true",
            ["Cent:ClientBaseUrl"] = "https://cent.example", ["Cent:PublicApiBaseUrl"] = "https://api.example",
            ["Cent:Payments:MerchantId"] = "", ["Cent:BootstrapToken"] = "cent-test-bootstrap-token-with-32-characters"
        }));
        builder.ConfigureTestServices(services => {
            if (!enabled) return;
            connection.Open();
            services.RemoveAll<CentDbContext>(); services.RemoveAll<DbContextOptions<CentDbContext>>();
            services.AddScoped<CentDbContext>(_ => new SqliteCentContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options));
            using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<CentDbContext>().Database.EnsureCreated();
        });
    }
    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);
        if (disposing) { connection.Dispose(); foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }
}
public sealed class SqliteCentContext(DbContextOptions<CentDbContext> options) : CentDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        var converter = new ValueConverter<DateTimeOffset, long>(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        foreach (var property in builder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
            if ((Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(DateTimeOffset)) property.SetValueConverter(converter);
    }
}
