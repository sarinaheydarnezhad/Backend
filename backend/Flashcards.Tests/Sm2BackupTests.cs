using System.Net;
using System.Net.Http.Json;
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
    [Fact]
    public async Task Sm2_deck_and_reviews_sync_backup_restore_and_continue_without_becoming_Leitner()
    {
        await LoginAsync("sm2-backup@example.com");
        var deckId = Guid.NewGuid();
        var cardId = Guid.NewGuid();
        var device = Guid.NewGuid();
        SyncPushChange Change(string type, Guid id, object payload) => new(Guid.NewGuid(), device, type, id,
            type == "reviewEvent" ? "create" : "upsert", _clock.Now, 0,
            JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var state = new SchedulerStatePayload("sm2", "new", new Sm2Data(0, 0, 2.5));
        var initial = new SyncPushRequest([
            Change("deck", deckId, new DeckPayload("SM-2", "", "en", "ltr", "medium", "sm2")),
            Change("card", cardId, new CardPayload(deckId, "hello", "greeting", null, null, JsonSerializer.SerializeToElement(Array.Empty<object>()))),
            Change("reviewState", cardId, new ReviewStatePayload(1, DateOnly.FromDateTime(_clock.Now.UtcDateTime), null, 0, 0, 0, state))
        ]);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/v1/sync/push", initial)).StatusCode);
        // A newly saved, unreviewed SM-2 deck must already be eligible for backup.
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(Guid.NewGuid(), 3)).StatusCode);
        var reviews = new List<SyncPushChange>();
        foreach (var previous in new[] { "new", "learning", "review" })
            reviews.Add(Change("reviewEvent", Guid.NewGuid(), new ReviewEventPayload(deckId, cardId, 1, 1,
                "success", _clock.Now, null, new SchedulerEventPayload("sm2", previous, previous == "new" ? "learning" : "review"))));
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/v1/sync/push", new SyncPushRequest(reviews))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/v1/sync/push", new SyncPushRequest(reviews))).StatusCode);
        var decks = await _client.GetFromJsonAsync<JsonElement>("/api/v1/me/decks");
        Assert.Equal("sm2", decks[0].GetProperty("reviewSystem").GetString());
        var progress = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/me/cards/{cardId}/review-state");
        using (var scheduler = JsonDocument.Parse(progress.GetProperty("schedulerStateJson").GetString()!))
        {
            Assert.Equal(3, scheduler.RootElement.GetProperty("data").GetProperty("repetitions").GetInt32());
            Assert.Equal(17, scheduler.RootElement.GetProperty("data").GetProperty("intervalDays").GetInt32());
            Assert.Equal(2.8, scheduler.RootElement.GetProperty("data").GetProperty("easeFactor").GetDouble());
        }
        var response = await CreateAsync(Guid.NewGuid(), 6);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var backup = (await response.Content.ReadFromJsonAsync<BackupMetadata>())!;
        using var snapshot = JsonDocument.Parse(await _client.GetByteArrayAsync($"/api/v1/backups/{backup.Id}/download"));
        Assert.Equal("sm2", snapshot.RootElement.GetProperty("decks")[0].GetProperty("reviewSystem").GetString());
        var savedState = snapshot.RootElement.GetProperty("reviewStates")[0].GetProperty("schedulerStateJson").GetString();
        Assert.Equal(3, snapshot.RootElement.GetProperty("reviewEvents").GetArrayLength());
        Guid userId;
        using (var scope = _factory.Services.CreateScope())
            userId = (await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Users.SingleAsync()).Id;
        await EraseLearningAsync(new BackupFixture(backup.Id, userId, deckId, [cardId]));
        var restored = await RestoreAsync(backup.Id, 6);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Equal("sm2", (await database.Decks.SingleAsync()).ReviewSystem);
            Assert.Equal(savedState, (await database.CardReviewStates.SingleAsync()).SchedulerStateJson);
            Assert.All(await database.ReviewEvents.ToListAsync(), item => Assert.Contains("sm2", item.SchedulerJson!));
        }
        var pull = (await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=6"))!;
        Assert.Equal("sm2", pull.Changes.Single(item => item.EntityType == "deck").Payload.GetProperty("reviewSystem").GetString());
        Assert.Equal("sm2", pull.Changes.Single(item => item.EntityType == "reviewState").Payload.GetProperty("schedulerState").GetProperty("schedulerId").GetString());
        var failure = Change("reviewEvent", Guid.NewGuid(), new ReviewEventPayload(deckId, cardId, 1, 1, "failure", _clock.Now,
            null, new SchedulerEventPayload("sm2", "review", "learning")));
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/v1/sync/push", new SyncPushRequest([failure]))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(Guid.NewGuid(), pull.Cursor + 1)).StatusCode);
    }

    [Fact]
    public async Task Backups_from_before_scheduler_support_still_restore_as_Leitner()
    {
        await LoginAsync("legacy-scheduler-backup@example.com");
        var fixture = await SeedBackupAsync();
        var document = JsonNode.Parse(await _client.GetByteArrayAsync($"/api/v1/backups/{fixture.BackupId}/download"))!;
        foreach (var deck in document["decks"]!.AsArray()) deck!.AsObject().Remove("reviewSystem");
        foreach (var state in document["reviewStates"]!.AsArray()) state!.AsObject().Remove("schedulerStateJson");
        foreach (var review in document["reviewEvents"]!.AsArray()) review!.AsObject().Remove("schedulerJson");
        await RewritePayloadAsync(fixture, System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()));
        await EraseLearningAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, (await RestoreAsync(fixture.BackupId)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Equal("leitner", (await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Decks.SingleAsync()).ReviewSystem);
    }

    [Fact]
    public async Task Restore_rejects_a_deck_with_a_different_review_system()
    {
        await LoginAsync("scheduler-restore-conflict@example.com");
        var fixture = await SeedBackupAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            (await database.Decks.SingleAsync()).ReviewSystem = "sm2";
            await database.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await RestoreAsync(fixture.BackupId)).StatusCode);
    }

    [Fact]
    public async Task Sm2_review_cannot_be_saved_as_Leitner_or_with_invalid_scheduler_data()
    {
        await LoginAsync("sm2-invalid@example.com");
        var device = Guid.NewGuid();
        var deckId = Guid.NewGuid();
        var cardId = Guid.NewGuid();
        SyncPushChange Change(string type, Guid id, object payload) => new(Guid.NewGuid(), device, type, id,
            "upsert", _clock.Now, 0, JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/v1/sync/push", new SyncPushRequest([
            Change("deck", deckId, new DeckPayload("SM-2", "", "en", "ltr", "medium", "sm2")),
            Change("card", cardId, new CardPayload(deckId, "hi", "hello", null, null, JsonSerializer.SerializeToElement(Array.Empty<object>())))
        ]))).StatusCode);
        var invalid = new ReviewStatePayload(1, DateOnly.FromDateTime(_clock.Now.UtcDateTime), null, 0, 0, 0);
        foreach (var payload in new[] { invalid, invalid with { SchedulerState = new("sm2", "new", new(-1, 0, 2.5)) } })
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/sync/push", new SyncPushRequest([
                Change("reviewState", cardId, payload)
            ]))).StatusCode);
    }
}
