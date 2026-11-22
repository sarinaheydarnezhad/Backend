using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cent.Module;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cent.Exam.Tests;

public sealed class AdminApiTests
{
    private sealed class Host : IAsyncDisposable
    {
        public SqliteConnection Connection = new("Data Source=:memory:");
        public WebApplication App = null!;
        public HttpClient Client = null!;
        public Dictionary<string, (Guid Id, string Token)> Accounts = [];
        public IConfiguration Configuration = null!;
        public static async Task<Host> Create(bool initialSuper = true)
        {
            var f = new Host(); await f.Connection.OpenAsync();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" }); builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> {
                ["Cent:Enabled"] = "true", ["Cent:ClientBaseUrl"] = "http://localhost/cent-s", ["Cent:PublicApiBaseUrl"] = "http://localhost",
                ["Cent:Payments:AllowDevelopmentMock"] = "true", ["Cent:BootstrapToken"] = new string('s', 64)
            });
            f.Configuration = builder.Configuration;
            builder.Services.AddCentModule(builder.Configuration);
            builder.Services.RemoveAll<CentDbContext>(); builder.Services.RemoveAll<DbContextOptions<CentDbContext>>();
            builder.Services.AddScoped<CentDbContext>(_ => new ReleaseTestContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(f.Connection).Options));
            f.App = builder.Build(); f.App.UseRouting(); f.App.UseCentModule(); f.App.UseAuthentication(); f.App.UseRateLimiter(); f.App.UseAuthorization(); f.App.MapCentModule();
            using (var scope = f.App.Services.CreateScope()) {
                var db = scope.ServiceProvider.GetRequiredService<CentDbContext>(); await db.Database.EnsureCreatedAsync();
                foreach (var role in CentAdminSecurity.Roles.Concat(["STUDENT", "ADMIN"])) {
                    if (!initialSuper && role is "SuperAdmin" or "ADMIN") continue;
                    var u = new CentUser { Role = role, Name = role + " Fixture", Email = role + "@example.invalid", NormalizedEmail = role.ToUpperInvariant() + "@EXAMPLE.INVALID", GoogleSubject = role == "STUDENT" ? null : "test:" + role };
                    db.Users.Add(u); var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                    db.Sessions.Add(new() { UserId = u.Id, TokenHash = CentModule.Hash(token), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) }); f.Accounts[role] = (u.Id, token);
                }
                await db.SaveChangesAsync();
            }
            await f.App.StartAsync(); f.Client = f.App.GetTestClient(); return f;
        }
        public void As(string? role) => Client.DefaultRequestHeaders.Authorization = role is null ? null : new AuthenticationHeaderValue("Bearer", Accounts[role].Token);
        public async Task WithDb(Func<CentDbContext, Task> action) { using var scope = App.Services.CreateScope(); await action(scope.ServiceProvider.GetRequiredService<CentDbContext>()); }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); await Connection.DisposeAsync(); }
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response) { response.EnsureSuccessStatusCode(); return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone(); }

    [Fact]
    public async Task Every_admin_route_denies_anonymous_and_students_and_never_leaks_secrets()
    {
        await using var f = await Host.Create();
        string[] paths = ["access", "overview", "users", "orders", "subscriptions", "products", "resources", "question-bank", "assessments", "curriculum", "reports", "roles", "settings", "audit", "trainings", "questions/"];
        foreach (var path in paths) {
            f.As(null); Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.GetAsync("/api/cent/v1/admin/" + path)).StatusCode);
            f.As("STUDENT"); Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync("/api/cent/v1/admin/" + path)).StatusCode);
        }
        f.As("SuperAdmin");
        foreach (var path in paths) {
            var response = await f.Client.GetAsync("/api/cent/v1/admin/" + path); response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("passwordHash", text); Assert.DoesNotContain("tokenHash", text); Assert.DoesNotContain("googleSubject", text);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }
    [Theory]
    [InlineData("UserManager", "users", "orders")]
    [InlineData("ContentManager", "resources", "users")]
    [InlineData("ExamManager", "question-bank", "resources")]
    [InlineData("FinanceManager", "orders", "question-bank")]
    public async Task Granular_roles_are_enforced_by_actual_session_policy(string role, string allowed, string forbidden)
    {
        await using var f = await Host.Create(); f.As(role);
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync("/api/cent/v1/admin/" + allowed)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync("/api/cent/v1/admin/" + forbidden)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.PutAsJsonAsync($"/api/cent/v1/admin/users/{f.Accounts["STUDENT"].Id}/role", new { role = "SuperAdmin", confirm = true })).StatusCode);
        var overview = await Json(await f.Client.GetAsync("/api/cent/v1/admin/overview"));
        Assert.Equal(role == "FinanceManager" ? JsonValueKind.Object : JsonValueKind.Null, overview.GetProperty("finance").ValueKind);
        Assert.Equal(role == "FinanceManager" ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
            (await f.Client.PostAsJsonAsync("/api/cent/v1/admin/subject-offers/prepare", new { })).StatusCode);
    }
    [Fact]
    public async Task Roles_cannot_be_self_assigned_unverified_or_silently_stale_and_changes_are_audited()
    {
        await using var f = await Host.Create(); f.As("SuperAdmin");
        var target = f.Accounts["ContentManager"].Id;
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PutAsJsonAsync($"/api/cent/v1/admin/users/{f.Accounts["SuperAdmin"].Id}/role", new { role = "STUDENT", confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PutAsJsonAsync($"/api/cent/v1/admin/users/{f.Accounts["STUDENT"].Id}/role", new { role = "SuperAdmin", confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PutAsJsonAsync($"/api/cent/v1/admin/users/{target}/role", new { role = "FinanceManager", confirm = false })).StatusCode);
        (await f.Client.PutAsJsonAsync($"/api/cent/v1/admin/users/{target}/role", new { role = "FinanceManager", confirm = true })).EnsureSuccessStatusCode();
        f.As("ContentManager"); Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync("/api/cent/v1/admin/resources")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync("/api/cent/v1/admin/orders")).StatusCode);
        await f.WithDb(async db => Assert.Contains(await db.Audit.ToListAsync(), a => a.ActorId == f.Accounts["SuperAdmin"].Id && a.EntityId == target && a.Action == "ROLE_OK:ContentManager>FinanceManager"));
    }
    [Fact]
    public async Task Bootstrap_requires_secret_exact_verified_identity_and_no_existing_superadmin()
    {
        await using var f = await Host.Create(initialSuper: false); var target = f.Accounts["ExamManager"].Id;
        f.Configuration["Cent:BootstrapUserId"] = target.ToString();
        f.As(null); Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.PostAsJsonAsync("/api/cent/v1/auth/bootstrap", new { })).StatusCode);
        f.As("ExamManager"); Assert.Equal(HttpStatusCode.NotFound, (await f.Client.PostAsJsonAsync("/api/cent/v1/auth/bootstrap", new { })).StatusCode);
        f.Client.DefaultRequestHeaders.Add("X-Cent-Bootstrap", new string('s',64));
        f.As("ContentManager"); Assert.Equal(HttpStatusCode.NotFound, (await f.Client.PostAsJsonAsync("/api/cent/v1/auth/bootstrap", new { })).StatusCode);
        f.As("ExamManager"); (await f.Client.PostAsJsonAsync("/api/cent/v1/auth/bootstrap", new { })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PostAsJsonAsync("/api/cent/v1/auth/bootstrap", new { })).StatusCode);
        await f.WithDb(async db => { Assert.Equal("SuperAdmin", (await db.Users.SingleAsync(u => u.Id == target)).Role); Assert.Single(await db.Audit.Where(a => a.Action == "BOOTSTRAP_SUPERADMIN_OK").ToListAsync()); });
    }
    [Fact]
    public async Task Lists_are_bounded_metrics_are_persisted_and_question_publication_is_immutable()
    {
        await using var f = await Host.Create(); f.As("SuperAdmin"); var training = new CentTraining { Title = "Resource", Published = true };
        await f.WithDb(async db => { db.Trainings.Add(training); db.Documents.Add(new() { TrainingId = training.Id, Title = "Published document", PageCount = 1, Published = true });
            for (var i=0; i<55; i++) db.Users.Add(new() { Name = "Paged", Email = $"paged{i}@example.invalid", NormalizedEmail = $"PAGED{i}@EXAMPLE.INVALID" }); await db.SaveChangesAsync(); });
        var users = await Json(await f.Client.GetAsync("/api/cent/v1/admin/users?size=1000&search=paged")); Assert.Equal(50, users.GetProperty("items").GetArrayLength()); Assert.Equal(55, users.GetProperty("total").GetInt32());
        var next = await Json(await f.Client.GetAsync("/api/cent/v1/admin/users?size=50&page=2&search=paged")); Assert.Equal(5,next.GetProperty("items").GetArrayLength());
        var overview = await Json(await f.Client.GetAsync("/api/cent/v1/admin/overview")); Assert.Equal(1,overview.GetProperty("publishedResources").GetInt32());
        var id = Guid.NewGuid(); var input = new { subject="math", topic="Manual", topicId=(Guid?)null, difficulty="easy", text="Question", options=new[]{"A","B"}, correctAnswer=0, explanation="Explanation", kind="both", active=true };
        (await f.Client.PutAsJsonAsync($"/api/cent/v1/admin/questions/{id}", input)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PutAsJsonAsync($"/api/cent/v1/admin/questions/{id}", input with { text="Changed" })).StatusCode);
        var report = await Json(await f.Client.GetAsync("/api/cent/v1/admin/reports?days=900")); Assert.Equal(90,report.GetProperty("days").GetInt32());
        await f.WithDb(async db => await db.Sessions.Where(s => s.UserId == f.Accounts["SuperAdmin"].Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1))));
        Assert.Equal(HttpStatusCode.Unauthorized,(await f.Client.GetAsync("/api/cent/v1/admin/overview")).StatusCode);
    }
}
