using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flashcards.Infrastructure.Backups;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Tests;

public sealed partial class BackupTests
{
    [Theory]
    [InlineData("[{\"sentence\":\"valid\",\"translation\":42,\"notes\":[\"array\"]}]")]
    [InlineData("[{\"sentence\":\"valid\",\"notes\":null}]")]
    [InlineData("{}")]
    public async Task Card_contract_invalid_backup_restore_retains_original_artifact_and_writes_no_learning_data(string examples)
    {
        await LoginAsync("card-backup-invalid@example.test");
        var fixture = await SeedBackupAsync();
        var json = JsonNode.Parse(await _client.GetStringAsync($"/api/v1/backups/{fixture.BackupId}/download"))!;
        json["cards"]![0]!["examplesJson"] = examples;
        await RewritePayloadAsync(fixture, Encoding.UTF8.GetBytes(json.ToJsonString()));
        await EraseLearningAsync(fixture);
        Assert.Equal(HttpStatusCode.BadRequest, (await RestoreAsync(fixture.BackupId)).StatusCode);
        await AssertNothingRestoredAsync();
        using var scope = _factory.Services.CreateScope();
        var backup = await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().UserBackups.SingleAsync();
        Assert.NotNull(backup.Payload);
        Assert.Equal("Completed", backup.Status);
    }

    [Fact]
    public async Task Card_contract_invalid_existing_cards_cannot_be_certified_as_a_complete_backup()
    {
        await LoginAsync("card-backup-source@example.test");
        var fixture = await SeedBackupAsync();
        const string malformed = "[{\"sentence\":\"valid\",\"translation\":42,\"notes\":[\"array\"]}]";
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Cards.ExecuteUpdateAsync(update => update.SetProperty(card => card.ExamplesJson, malformed));
        var response = await CreateAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(malformed, (await stored.Cards.SingleAsync()).ExamplesJson);
        Assert.Equal(1, await stored.ReviewEvents.CountAsync());
        Assert.Equal(1, (await stored.CardReviewStates.SingleAsync()).TotalReviews);
        Assert.True(await stored.UserBackups.AnyAsync(row => row.Id == fixture.BackupId && row.Payload != null));
    }

    [Fact]
    public async Task Card_contract_maximum_logical_card_survives_backup_restore_and_fresh_pull_without_history_loss()
    {
        await LoginAsync("card-backup-large@example.test");
        var fixture = await SeedBackupAsync();
        var text = new string('a', 4000);
        var examples = JsonSerializer.Serialize(Enumerable.Repeat(new { sentence = text, translation = text, notes = text }, 20));
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Cards.ExecuteUpdateAsync(update => update
                .SetProperty(card => card.ExamplesJson, examples).SetProperty(card => card.FrontText, text)
                .SetProperty(card => card.Meaning, text).SetProperty(card => card.Phonetic, new string('p', 500))
                .SetProperty(card => card.Category, new string('c', 120)));
        var created = await CreateAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var backup = (await created.Content.ReadFromJsonAsync<BackupMetadata>())!;
        await EraseLearningAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, (await RestoreAsync(backup.Id)).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var stored = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Equal(examples, (await stored.Cards.SingleAsync()).ExamplesJson);
            Assert.Equal(1, (await stored.CardReviewStates.SingleAsync()).TotalReviews);
            Assert.Equal(1, await stored.ReviewEvents.CountAsync());
        }
        var pull = (await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0"))!;
        Assert.Equal(2, pull.SchedulingContractVersion);
        Assert.Equal(examples, pull.Changes.Single(change => change.EntityType == "card").Payload.GetProperty("examples").GetRawText());
    }
}
