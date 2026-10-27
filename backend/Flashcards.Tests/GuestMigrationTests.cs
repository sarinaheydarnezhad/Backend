using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Tests;

public sealed partial class SyncTests
{
    [Fact]
    public async Task Atomic_guest_migration_supports_large_histories_and_lost_response_retries()
    {
        await LoginAsync("atomic-guest@example.com");
        var changes = GuestChanges(60);
        Assert.Equal(HttpStatusCode.BadRequest, (await Push(changes)).StatusCode);
        var response = await MigrateAsync(changes);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var first = (await response.Content.ReadFromJsonAsync<SyncPushResponse>())!;
        Assert.Equal(changes.Length, first.Cursor);
        var replay = (await (await MigrateAsync(changes)).Content.ReadFromJsonAsync<SyncPushResponse>())!;
        Assert.Equal(first.Cursor, replay.Cursor);
        Assert.All(replay.Changes, item => Assert.Equal("duplicate", item.Status));
        var state = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/me/cards/{changes[1].EntityId}/review-state");
        Assert.Equal(60, state.GetProperty("totalReviews").GetInt32());
        Assert.Equal(60, state.GetProperty("totalSuccesses").GetInt32());
        Assert.Equal(5, state.GetProperty("box").GetInt32());
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(1, await database.Decks.CountAsync());
        Assert.Equal(2, await database.Cards.CountAsync());
        Assert.Equal(60, await database.ReviewEvents.CountAsync());
        Assert.Equal(changes.Length, await database.SyncChanges.CountAsync());
    }

    [Fact]
    public async Task Atomic_guest_migration_storage_failure_rolls_back_everything_and_retries_safely()
    {
        await LoginAsync("retry-guest@example.com");
        var changes = GuestChanges(3);
        _failure.FailAfterChanges = 2;
        var failed = await MigrateAsync(changes);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("storage_unavailable", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.DoesNotContain("private storage details", await failed.Content.ReadAsStringAsync());
        await AssertEmptyLearningAsync();
        _failure.FailAfterChanges = 0;
        Assert.Equal(HttpStatusCode.OK, (await MigrateAsync(changes)).StatusCode);
        var modified = changes.ToArray();
        modified[0] = modified[0] with { ClientChangedAtUtc = modified[0].ClientChangedAtUtc.AddSeconds(1) };
        Assert.Equal(HttpStatusCode.Conflict, (await MigrateAsync(modified)).StatusCode);
    }

    [Fact]
    public async Task Atomic_guest_migration_cancellation_rolls_back_rows_receipts_and_cursor()
    {
        await LoginAsync("cancel-guest@example.com");
        var changes = GuestChanges(3);
        using var cancellation = new CancellationTokenSource();
        _failure.OnChangesSaved = cancellation.Cancel;
        using (var scope = _factory.Services.CreateScope())
        {
            var userId = await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Users.Select(item => item.Id).SingleAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<SyncService>()
                .MigrateGuestAsync(userId, new SyncPushRequest(changes), cancellation.Token));
        }
        _failure.OnChangesSaved = null;
        await AssertEmptyLearningAsync();
        Assert.Equal(HttpStatusCode.OK, (await MigrateAsync(changes)).StatusCode);
    }

    [Fact]
    public async Task Atomic_guest_migration_requires_authorization_and_never_reuses_another_owners_entities()
    {
        var changes = GuestChanges(3);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MigrateAsync(changes)).StatusCode);
        await LoginAsync("owner-guest@example.com");
        Assert.Equal(HttpStatusCode.OK, (await MigrateAsync(changes)).StatusCode);
        await LoginAsync("other-guest@example.com");
        Assert.Equal(HttpStatusCode.Conflict, (await MigrateAsync(changes)).StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0"))!.Changes);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/me/cards/{changes[1].EntityId}/review-state")).StatusCode);
    }

    [Fact]
    public async Task Atomic_guest_migration_quota_rejection_does_not_leave_the_first_deck_committed()
    {
        await LoginAsync("quota-guest@example.com");
        var changes = GuestChanges(0).Concat(new[]
        {
            Change("deck", Guid.NewGuid(), "upsert", new DeckPayload("Second", "", "en", "ltr", "medium"))
        }).ToArray();
        Assert.Equal(HttpStatusCode.Forbidden, (await MigrateAsync(changes)).StatusCode);
        await AssertEmptyLearningAsync();
    }

    [Fact]
    public async Task Atomic_guest_migration_rejects_incomplete_history_and_existing_account_updates()
    {
        await LoginAsync("invalid-guest@example.com");
        var changes = GuestChanges(3);
        var review = changes[3].Payload.Deserialize<ReviewEventPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        changes[3] = changes[3] with { Payload = JsonSerializer.SerializeToElement(review with { PreviousBox = 3 }) };
        Assert.Equal(HttpStatusCode.BadRequest, (await MigrateAsync(changes)).StatusCode);
        await AssertEmptyLearningAsync();
        var original = GuestChanges(0);
        Assert.Equal(HttpStatusCode.OK, (await MigrateAsync(original)).StatusCode);
        var update = Change("deck", original[0].EntityId, "upsert", new DeckPayload("Do not replace", "", "en", "ltr", "medium"), 1);
        Assert.Equal(HttpStatusCode.BadRequest, (await MigrateAsync([update])).StatusCode);
    }

    [Fact]
    public async Task Guest_request_limits_and_strict_sync_payload_validation_remain_enforced()
    {
        await LoginAsync("bounded-guest@example.com");
        using var oversized = new StringContent(new string(' ', 10_485_761), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _client.PostAsync("/api/v1/sync/guest-migration", oversized)).StatusCode);
        using var nonJson = new StringContent("{}");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _client.PostAsync("/api/v1/sync/guest-migration", nonJson)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MigrateAsync([])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/sync/guest-migration", new { changes = Array.Empty<object>(), userId = Guid.NewGuid() })).StatusCode);
        await AssertEmptyLearningAsync();
    }

    [Fact]
    public async Task Legacy_zero_head_account_is_backfilled_once_without_changing_learning_data_or_timestamps()
    {
        await LoginAsync("legacy-bootstrap@example.com");
        Guid deckId;
        DateTimeOffset updated;
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var userId = await database.Users.Select(item => item.Id).SingleAsync();
            var deck = new Deck { UserId = userId, Name = "Legacy" };
            var card = new Card { Deck = deck, FrontText = "Legacy front", Meaning = "Meaning" };
            database.AddRange(deck, card, new CardReviewState { Card = card, DueDate = new DateOnly(2026, 10, 27) });
            await database.SaveChangesAsync();
            deckId = deck.Id;
            updated = deck.UpdatedAtUtc;
        }
        var feed = (await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0"))!;
        Assert.Equal(new[] { "deck", "card", "reviewState" }, feed.Changes.Select(item => item.EntityType));
        Assert.Equal(3, feed.Cursor);
        Assert.False(feed.HasMore);
        Assert.Equal(3, (await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0"))!.Cursor);
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Equal(updated, (await database.Decks.SingleAsync()).UpdatedAtUtc);
            Assert.Equal(3, await database.SyncChanges.CountAsync());
        }
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("deck", deckId, "upsert", new DeckPayload("Editable", "", "en", "ltr", "medium"), 1))).StatusCode);
    }

    [Fact]
    public async Task Damaged_feed_returns_an_explicit_conflict_instead_of_non_progressing_pages()
    {
        await LoginAsync("gap-feed@example.com");
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().SyncHeads.ExecuteUpdateAsync(update => update.SetProperty(item => item.Version, 2));
        var response = await _client.GetAsync("/api/v1/sync/pull?cursor=0");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("sync_history_incomplete", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Sync_read_storage_failure_has_a_safe_retryable_response()
    {
        await LoginAsync("read-failure@example.com");
        Assert.Equal(HttpStatusCode.OK, (await MigrateAsync(GuestChanges(0))).StatusCode);
        _failure.ReadFailure.Fail = true;
        var response = await _client.GetAsync("/api/v1/sync/pull?cursor=0");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("private storage details", await response.Content.ReadAsStringAsync());
        Assert.NotNull(response.Headers.RetryAfter);
        _failure.ReadFailure.Fail = false;
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/sync/pull?cursor=0")).StatusCode);
    }

    private static SyncPushChange[] GuestChanges(int reviews)
    {
        var deckId = Guid.NewGuid();
        var cardId = Guid.NewGuid();
        var changes = new List<SyncPushChange>
        {
            Change("deck", deckId, "upsert", new DeckPayload("Guest", "", "en", "ltr", "medium")),
            Change("card", cardId, "upsert", new CardPayload(deckId, "Identical text", "Meaning", null, null, JsonSerializer.SerializeToElement(Array.Empty<object>()))),
            Change("card", Guid.NewGuid(), "upsert", new CardPayload(deckId, "Identical text", "Meaning", null, null, JsonSerializer.SerializeToElement(Array.Empty<object>())))
        };
        for (var index = 0; index < reviews; index++)
            changes.Add(Change("reviewEvent", Guid.NewGuid(), "create", new ReviewEventPayload(deckId, cardId,
                Math.Min(index + 1, 5), Math.Min(index + 2, 5), "success", DateTimeOffset.UtcNow.AddDays(-reviews + index), null)));
        return changes.ToArray();
    }

    [Fact]
    public async Task Concurrent_guest_migrations_on_separate_connections_never_duplicate_rows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lerona-sync-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var factory = new SyncApiFactory(_connection, new SyncFailureInterceptor(),
                $"Data Source={path};Pooling=False;Default Timeout=2");
            using var client = factory.CreateClient();
            using (var scope = factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Database.EnsureCreatedAsync();
            var registered = await client.PostAsJsonAsync("/api/v1/auth/register", new
            {
                email = "concurrent-guest@example.com", password = "Synthetic private test passphrase", displayName = "Learner"
            });
            Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
            var tokens = (await registered.Content.ReadFromJsonAsync<AuthTokens>())!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            var changes = GuestChanges(3);
            var request = new SyncPushRequest(changes);
            var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(attempt =>
                client.PostAsJsonAsync("/api/v1/sync/guest-migration", request)));
            foreach (var response in responses)
                Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync());
            var retry = await client.PostAsJsonAsync("/api/v1/sync/guest-migration", request);
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            using var verify = factory.Services.CreateScope();
            var database = verify.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Equal(1, await database.Decks.CountAsync());
            Assert.Equal(2, await database.Cards.CountAsync());
            Assert.Equal(3, await database.ReviewEvents.CountAsync());
            Assert.Equal(changes.Length, await database.SyncChanges.CountAsync());
            Assert.Equal(changes.Length, (await database.SyncHeads.SingleAsync()).Version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Interrupted_legacy_bootstrap_rolls_back_versions_and_can_be_retried()
    {
        await LoginAsync("legacy-retry@example.com");
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var userId = await database.Users.Select(item => item.Id).SingleAsync();
            database.Decks.Add(new Deck { UserId = userId, Name = "Legacy" });
            await database.SaveChangesAsync();
        }
        _failure.FailAfterChanges = 1;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.GetAsync("/api/v1/sync/pull?cursor=0")).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Equal(0, (await database.Decks.SingleAsync()).SyncVersion);
            Assert.Equal(0, (await database.SyncHeads.SingleAsync()).Version);
            Assert.Equal(0, await database.SyncChanges.CountAsync());
        }
        _failure.FailAfterChanges = 0;
        Assert.Equal(1, (await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0"))!.Cursor);
    }

    private Task<HttpResponseMessage> MigrateAsync(SyncPushChange[] changes) =>
        _client.PostAsJsonAsync("/api/v1/sync/guest-migration", new SyncPushRequest(changes));

    [Fact]
    public async Task Sync_rejects_default_last_review_timestamp_before_writing_unbackuppable_data()
    {
        await LoginAsync("invalid-date@example.com");
        var changes = GuestChanges(0);
        var state = Change("reviewState", changes[1].EntityId, "upsert", new ReviewStatePayload(1,
            new DateOnly(2026, 10, 27), default(DateTimeOffset), 0, 0, 0));
        Assert.Equal(HttpStatusCode.BadRequest, (await Push(changes.Concat(new[] { state }).ToArray())).StatusCode);
        await AssertEmptyLearningAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Legacy_history_is_only_bootstrapped_when_replay_preserves_saved_progress(bool consistent)
    {
        await LoginAsync("legacy-progress@example.com");
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var userId = await database.Users.Select(item => item.Id).SingleAsync();
            var deck = new Deck { UserId = userId, Name = "Legacy reviewed" };
            var card = new Card { Deck = deck, FrontText = "Front", Meaning = "Meaning" };
            database.AddRange(deck, card, new CardReviewState
            {
                Card = card, Box = consistent ? 2 : 1, DueDate = new DateOnly(2026, 10, 3),
                LastReviewedAtUtc = at, TotalReviews = 1, TotalSuccesses = 1, ConsecutiveSuccesses = 1
            }, new ReviewEvent
            {
                UserId = userId, Deck = deck, Card = card, PreviousBox = 1, NewBox = 2,
                Result = "success", ReviewedAtUtc = at
            });
            await database.SaveChangesAsync();
        }
        var response = await _client.GetAsync("/api/v1/sync/pull?cursor=0");
        Assert.Equal(consistent ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var stored = verify.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(1, await stored.ReviewEvents.CountAsync());
        Assert.Equal(consistent ? 4 : 0, await stored.SyncChanges.CountAsync());
        Assert.Equal(consistent ? 2 : 1, (await stored.CardReviewStates.SingleAsync()).Box);
    }

    private async Task AssertEmptyLearningAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(0, await database.Decks.CountAsync());
        Assert.Equal(0, await database.Cards.CountAsync());
        Assert.Equal(0, await database.CardReviewStates.CountAsync());
        Assert.Equal(0, await database.ReviewEvents.CountAsync());
        Assert.Equal(0, await database.SyncChanges.CountAsync());
        Assert.Equal(0, (await database.SyncHeads.SingleAsync()).Version);
    }

    private sealed class SyncFailureInterceptor : SaveChangesInterceptor
    {
        public int FailAfterChanges { get; set; }
        public Action? OnChangesSaved { get; set; }
        public SyncReadFailure ReadFailure { get; } = new();

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            var count = eventData.Context!.ChangeTracker.Entries<SyncChange>().Count();
            if (count > 0) OnChangesSaved?.Invoke();
            if (FailAfterChanges > 0 && count >= FailAfterChanges) throw new DbUpdateException("private storage details");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SyncReadFailure : DbCommandInterceptor
    {
        public bool Fail { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Fail && command.CommandText.Contains("\"SyncChanges\"", StringComparison.Ordinal)) throw new TimeoutException("private storage details");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SyncSqliteFunctions : DbConnectionInterceptor
    {
        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            ((Microsoft.Data.Sqlite.SqliteConnection)connection).CreateFunction("ISJSON", (string value) =>
            {
                try { using var document = JsonDocument.Parse(value); return 1; }
                catch (JsonException) { return 0; }
            });
            return Task.CompletedTask;
        }
    }
}
