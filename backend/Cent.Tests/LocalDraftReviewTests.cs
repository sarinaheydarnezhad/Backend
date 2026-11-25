using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cent.Module;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;

namespace Cent.Tests;

public class LocalDraftReviewTests
{
    [Theory]
    [InlineData("Development", true, true, 2)]
    [InlineData("Development", false, true, 0)]
    [InlineData("Development", true, false, 0)]
    [InlineData("Production", true, true, 0)]
    [InlineData("Staging", true, true, 0)]
    public void Review_is_explicit_owner_scoped_and_impossible_outside_development(string environment, bool enabled, bool matching, int expected)
    {
        var owner = Guid.NewGuid();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Cent:Reader:LocalReview:Enabled"] = enabled.ToString(), ["Cent:Reader:LocalReview:UserId"] = owner.ToString()
        }).Build();
        Assert.Equal(expected, CentLocalDraftReview.Documents(matching ? owner : Guid.NewGuid(), config,
            new HostingEnvironment { EnvironmentName = environment }).Length);
    }

    [Fact]
    public async Task Local_student_can_review_additive_drafts_and_take_private_notes_without_publication_or_entitlement_bypass()
    {
        using var factory = new CentFactory(environmentVariables: new Dictionary<string, string?> {
            ["Cent__Reader__Enabled"] = "true", ["Cent__Reader__AllSubjects"] = "true", ["Cent__Reader__LocalReview__Enabled"] = "true"
        });
        using var client = factory.CreateClient(); using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
        await new CentCurriculumImporter(db).Import();
        var auth = await (await TestAccounts.Create(factory, "local-review@example.invalid")).Content.ReadFromJsonAsync<JsonElement>();
        var owner = auth.GetProperty("user").GetProperty("id").GetGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.GetProperty("token").GetString());
        var doc = await db.Documents.FirstAsync(d => !d.Published && d.SourceId != null);
        var product = new CentProduct { Title = "Existing local access", Kind = "TRAINING", TrainingId = doc.TrainingId, PriceRial = 10000 };
        var order = new CentOrder { UserId = owner, ProductId = product.Id, RequestId = Guid.NewGuid(), Kind = "TRAINING", Status = "PAID" };
        var grant = new CentEntitlement { UserId = owner, OrderId = order.Id, Kind = "TRAINING", TrainingId = doc.TrainingId, StartsAt = DateTimeOffset.UtcNow.AddDays(-1) };
        db.AddRange(product, order, grant); await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/cent/v1/documents/{doc.Id}")).StatusCode);
        var config = factory.Services.GetRequiredService<IConfiguration>(); config["Cent:Reader:LocalReview:UserId"] = owner.ToString();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/cent/v1/documents/{doc.Id}")).StatusCode);
        var library = await client.GetFromJsonAsync<JsonElement>("/api/cent/v1/me/library");
        Assert.Contains(library.GetProperty("documents").EnumerateArray(), d => d.GetProperty("id").GetGuid() == doc.Id && !d.GetProperty("published").GetBoolean());
        var noteId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/cent/v1/notes/{noteId}", new { documentId = doc.Id, page = 1, anchorY = .2, body = "Private local draft review", revision = 0 })).StatusCode);
        var curriculum = await client.GetFromJsonAsync<JsonElement>("/api/cent/v1/curriculum");
        Assert.DoesNotContain(curriculum.GetProperty("documents").EnumerateArray(), d => d.GetProperty("id").GetGuid() == doc.Id);
        await db.Entry(doc).ReloadAsync(); Assert.False(doc.Published);
        grant.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/cent/v1/documents/{doc.Id}")).StatusCode);
        Assert.Equal("Private local draft review", (await db.Notes.SingleAsync(n => n.Id == noteId)).Body);
        config["Cent:Reader:LocalReview:Enabled"] = "false";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/cent/v1/documents/{doc.Id}")).StatusCode);
    }
}
