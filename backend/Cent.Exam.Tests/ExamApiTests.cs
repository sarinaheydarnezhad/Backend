using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cent.Module;
using Cent.Module.Exams;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace Cent.Exam.Tests;

public sealed class ExamApiTests
{
    private static async Task<(WebApplication App, HttpClient Client)> Host(ExamFixture f)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ICentExamStore>(f.Store); builder.Services.AddSingleton<ICentMockAdmission>(f.Admission);
        builder.Services.AddScoped<CentExamService>();
        builder.Services.AddDbContext<CentDbContext>(o => o.UseSqlServer("Server=offline.invalid;Database=NeverConnect;Integrated Security=true;TrustServerCertificate=true"));
        builder.Services.AddAuthentication("TestCent").AddScheme<AuthenticationSchemeOptions, TestAuth>("TestCent", _ => { });
        builder.Services.AddAuthorization(o => {
            o.AddPolicy("CentUser", p => p.RequireAuthenticatedUser());
            o.AddPolicy("CentAdmin", p => p.RequireAuthenticatedUser().RequireRole("ADMIN"));
        });
        var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization();
        var group = app.MapGroup("/api/cent/v1");
        group.AddEndpointFilter(async (c, next) => {
            c.HttpContext.Response.Headers.CacheControl = "private, no-store, max-age=0";
            try { return await next(c); }
            catch (CentException e) { return Results.Json(new { code = e.Code, message = e.Message }, statusCode: e.Status); }
        });
        group.MapCentExams();
        // Admin routes bind CentDbContext, but no test calls them or opens a database.
        await app.StartAsync(); return (app, app.GetTestClient());
    }
    [Fact]
    public async Task Public_configuration_and_authenticated_start_match_contract_without_private_keys()
    {
        var f = new ExamFixture(); var (app, client) = await Host(f); await using var host = app;
        using var http = client;
        var versions = await Json(await http.GetAsync("/api/cent/v1/assessment/exam-versions"));
        Assert.Equal(65, versions.GetProperty("versions")[0].GetProperty("totalQuestions").GetInt32());
        var config = await Json(await http.GetAsync($"/api/cent/v1/assessment/exam-versions/{f.Definition.ExamVersionId}/configuration"));
        Assert.Equal(6, config.GetProperty("sections").GetArrayLength()); Assert.Equal("-0.25", config.GetProperty("scoring").GetProperty("incorrect").GetString());
        var input = new StartMockExam(Guid.NewGuid(), f.Definition.ExamVersionId, f.Definition.ConfigurationRevision);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/cent/v1/assessment/mock-attempts/", input)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", f.Owner.ToString());
        var response = await http.PostAsJsonAsync("/api/cent/v1/assessment/mock-attempts/", input);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var start = await Json(response); var id = start.GetProperty("attemptId").GetGuid();
        var retry = await http.PostAsJsonAsync("/api/cent/v1/assessment/mock-attempts/", input);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode); Assert.Equal(id, (await Json(retry)).GetProperty("attemptId").GetGuid());
        var active = await http.GetStringAsync($"/api/cent/v1/assessment/mock-attempts/{id}/active-section");
        Assert.DoesNotContain("correctOptionId", active); Assert.DoesNotContain("explanation", active);
        Assert.DoesNotContain("Synthetic math item", active);
    }
    [Fact]
    public async Task Answer_ack_clear_and_timeout_reconnection_use_server_time()
    {
        var f = new ExamFixture(); var (app, client) = await Host(f); await using var host = app; using var http = client;
        var id = await f.Start(); var run = f.Store.Runs[id]; var section = run.Sections[0].Id; var q = run.Questions[0].QuestionId;
        http.DefaultRequestHeaders.Authorization = new("Bearer", f.Owner.ToString());
        var path = $"/api/cent/v1/assessment/mock-attempts/{id}";
        var saved = await Json(await http.PutAsJsonAsync(path + "/answers", new { commandId = Guid.NewGuid(), sectionId = section, expectedRevision = 1, questionId = q, selectedOptionId = "b" }));
        Assert.True(saved.GetProperty("receipt").GetProperty("accepted").GetBoolean());
        var missing = await http.PutAsJsonAsync(path + "/answers", new { commandId = Guid.NewGuid(), sectionId = section, expectedRevision = 2, questionId = q });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("invalid_answer", (await Json(missing)).GetProperty("code").GetString());
        Assert.Equal(1, f.Store.Runs[id].Questions[0].Answer);
        var cleared = await http.PutAsJsonAsync(path + "/answers", new { commandId = Guid.NewGuid(), sectionId = section, expectedRevision = 2, questionId = q, selectedOptionId = (string?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode); Assert.Null(f.Store.Runs[id].Questions[0].Answer);
        f.Clock.Now = f.Clock.Now.AddMinutes(95);
        var state = await Json(await http.GetAsync(path));
        Assert.Equal("biology", state.GetProperty("activeSection").GetProperty("subjectId").GetString());
        http.DefaultRequestHeaders.Authorization = new("Bearer", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(path)).StatusCode);
    }
    [Fact]
    public async Task Results_are_terminal_only_and_timeout_zero_answer_outcome_is_explicit()
    {
        var f = new ExamFixture(); var (app, client) = await Host(f); await using var host = app; using var http = client;
        var id = await f.Start(); http.DefaultRequestHeaders.Authorization = new("Bearer", f.Owner.ToString());
        var path = $"/api/cent/v1/assessment/mock-attempts/{id}";
        Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync(path + "/result")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync(path + "/review")).StatusCode);
        f.Clock.Now = f.Clock.Now.AddMinutes(141);
        var result = await Json(await http.GetAsync(path + "/result"));
        Assert.Equal("EXPIRED", result.GetProperty("status").GetString()); Assert.Equal("0.00", result.GetProperty("rawScore").GetString());
        Assert.Equal(65, result.GetProperty("unansweredCount").GetInt32()); Assert.Equal(JsonValueKind.Null, result.GetProperty("officialNormalisedScore").ValueKind);
        var review = await Json(await http.GetAsync(path + "/review"));
        Assert.Equal(65, review.GetProperty("questions").GetArrayLength()); Assert.Equal(1, review.GetProperty("passages").GetArrayLength());
        Assert.True(review.GetProperty("questions")[0].TryGetProperty("correctOptionId", out _));
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ") || !Guid.TryParse(header[7..], out var owner)) return Task.FromResult(AuthenticateResult.NoResult());
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(
                new ClaimsIdentity([new(ClaimTypes.NameIdentifier, owner.ToString())], "TestCent")), "TestCent")));
        }
    }
}
