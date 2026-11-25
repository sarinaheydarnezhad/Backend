using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cent.Module;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cent.Tests;

public class ContentPreviewTests
{
    [Fact]
    public async Task Preview_is_content_role_only_scoped_sanitized_and_does_not_publish()
    {
        using var factory = new CentFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
        await new CentCurriculumImporter(db).Import();
        var document = await db.Documents.FirstAsync(d => !d.Published && d.SourceId != null);
        var section = await db.SourceSections.FirstAsync(s => s.SourceId == document.SourceId);
        var unrelated = await db.SourceSections.FirstAsync(s => s.SourceId != document.SourceId);
        var path = $"/api/cent/v1/admin/documents/{document.Id}/preview";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path + "/")).StatusCode);
        var student = await (await TestAccounts.Create(factory, "preview-student@example.invalid")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", student.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path + "/")).StatusCode);
        var userId = student.GetProperty("user").GetProperty("id").GetGuid();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.Role = "ContentManager"; await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path + "/")).StatusCode);
        var response = await client.GetAsync(path + "/" + section.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.NotEmpty(payload.GetProperty("pilotHtml").GetString()!);
        Assert.DoesNotContain("<script", payload.GetProperty("pilotHtml").GetString()!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "/" + unrelated.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "/" + section.Id + "/assets/unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/cent/v1/documents/{document.Id}")).StatusCode);
        await db.Entry(document).ReloadAsync(); Assert.False(document.Published);
        Assert.Empty(await db.Notes.ToArrayAsync()); Assert.Empty(await db.Progress.ToArrayAsync());
        Assert.Contains(await db.Audit.ToArrayAsync(), a => a.Action == "ADMIN_CONTENT_PREVIEW");
        user.Role = "FinanceManager"; await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path + "/" + section.Id)).StatusCode);
    }
}
