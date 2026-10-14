using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flashcards.Tests;

public sealed class SyncTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private SyncApiFactory _factory = null!;
    private HttpClient _client = null!;
    private static readonly Guid DeviceId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _connection.CreateFunction("ISJSON", (string value) =>
        {
            try { using var document = JsonDocument.Parse(value); return 1; }
            catch (JsonException) { return 0; }
        });
        await _connection.OpenAsync();
        _factory = new SyncApiFactory(_connection);
        _client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Push_and_pull_are_authenticated_and_cursors_are_paginated()
    {
        var deckId = Guid.NewGuid();
        var deck = Change("deck", deckId, "upsert", new DeckPayload("Words", "Study", "en", "ltr", "medium"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Push(deck)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/sync/pull?cursor=0")).StatusCode);
        await LoginAsync("first@example.com");

        var cardId = Guid.NewGuid();
        var card = Change("card", cardId, "upsert", new CardPayload(deckId, "hello", "greeting", null, null,
            JsonSerializer.SerializeToElement(Array.Empty<object>())));
        var eventId = Guid.NewGuid();
        var review = Change("reviewEvent", eventId, "create", new ReviewEventPayload(deckId, cardId, 1, 2,
            "success", DateTimeOffset.UtcNow, null));
        var state = Change("reviewState", cardId, "upsert", new ReviewStatePayload(1, DateOnly.FromDateTime(DateTime.UtcNow),
            null, 0, 0, 0));
        var pushed = await Push(deck, card, state, review);
        Assert.Equal(HttpStatusCode.OK, pushed.StatusCode);
        Assert.Equal(4, (await pushed.Content.ReadFromJsonAsync<SyncPushResponse>())!.Cursor);
        var firstPage = await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0&limit=2");
        Assert.Equal([1L, 2L], firstPage!.Changes.Select(change => change.ServerVersion));
        Assert.True(firstPage.HasMore);
        Assert.Equal(2, firstPage.Cursor);
        var secondPage = await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=2");
        Assert.Equal([3L, 4L], secondPage!.Changes.Select(change => change.ServerVersion));
        Assert.False(secondPage.HasMore);
        Assert.Equal("reviewEvent", secondPage.Changes[1].EntityType);
        Assert.Equal(eventId, secondPage.Changes[1].EntityId);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/sync/pull?cursor=5")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(4, await database.SyncChanges.CountAsync());
        Assert.Single(await database.ReviewEvents.ToListAsync());
        Assert.Equal(2, (await database.CardReviewStates.SingleAsync()).Box);
    }

    [Fact]
    public async Task Retry_and_duplicate_review_event_do_not_create_new_versions()
    {
        await LoginAsync("first@example.com");
        var deckId = Guid.NewGuid();
        var cardId = Guid.NewGuid();
        var deck = Change("deck", deckId, "upsert", new DeckPayload("Words", "", "en", "ltr", "medium"));
        var card = Change("card", cardId, "upsert", new CardPayload(deckId, "hi", "hey", null, null,
            JsonSerializer.SerializeToElement(Array.Empty<object>())));
        Assert.Equal(HttpStatusCode.OK, (await Push(deck, card)).StatusCode);
        var eventId = Guid.NewGuid();
        var payload = new ReviewEventPayload(deckId, cardId, 1, 2, "success", DateTimeOffset.UtcNow, null);
        var review = Change("reviewEvent", eventId, "create", payload);
        Assert.Equal(HttpStatusCode.OK, (await Push(review)).StatusCode);
        Assert.Equal("duplicate", (await (await Push(review)).Content.ReadFromJsonAsync<SyncPushResponse>())!.Changes[0].Status);
        Assert.Equal("duplicate", (await (await Push(Change("reviewEvent", eventId, "create", payload))).Content
            .ReadFromJsonAsync<SyncPushResponse>())!.Changes[0].Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Push(review with { Payload = JsonSerializer.SerializeToElement(payload with { Result = "failure" }) })).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(3, await database.SyncChanges.CountAsync());
        Assert.Equal(3, (await database.SyncHeads.SingleAsync()).Version);
        Assert.Single(await database.ReviewEvents.ToListAsync());
    }

    [Fact]
    public async Task Conflicts_roll_back_entire_batch_and_include_both_values()
    {
        await LoginAsync("first@example.com");
        var deckId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("deck", deckId, "upsert",
            new DeckPayload("Original", "", "en", "ltr", "medium")))).StatusCode);
        var otherId = Guid.NewGuid();
        var attempted = await Push(
            Change("card", otherId, "upsert", new CardPayload(deckId, "Rolled back", "Meaning", null, null, JsonSerializer.SerializeToElement(Array.Empty<object>()))),
            Change("deck", deckId, "upsert", new DeckPayload("Stale", "", "en", "ltr", "medium")));
        Assert.Equal(HttpStatusCode.Conflict, attempted.StatusCode);
        var body = await attempted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Original", body.GetProperty("conflict").GetProperty("serverValue").GetProperty("name").GetString());
        Assert.Equal("Stale", body.GetProperty("conflict").GetProperty("clientValue").GetProperty("name").GetString());
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(1, (await database.SyncHeads.SingleAsync()).Version);
        Assert.Equal(1, await database.Decks.CountAsync());
        Assert.Equal(1, await database.SyncChanges.CountAsync());
        Assert.Equal(0, await database.Cards.CountAsync());
    }

    [Fact]
    public async Task Other_users_cannot_pull_or_overwrite_private_entities()
    {
        await LoginAsync("first@example.com");
        var deckId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("deck", deckId, "upsert",
            new DeckPayload("Private", "", "en", "ltr", "medium")))).StatusCode);
        var ownerId = (await _client.GetFromJsonAsync<JsonElement>("/api/v1/me/" )).GetProperty("id").GetGuid();
        await LoginAsync("second@example.com");
        Assert.Empty((await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0"))!.Changes);
        Assert.Equal(HttpStatusCode.Conflict, (await Push(Change("deck", deckId, "upsert",
            new DeckPayload("Attack", "", "en", "ltr", "medium")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Push(Change("settings", ownerId, "upsert",
            new SettingsPayload("system", true, "en", false, null, "en", null)))).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal("Private", (await database.Decks.SingleAsync()).Name);
    }

    [Fact]
    public async Task Invalid_changes_and_oversized_bodies_are_rejected_without_writes()
    {
        await LoginAsync("first@example.com");
        var deck = Change("deck", Guid.NewGuid(), "upsert", new DeckPayload("Valid", "", "en", "ltr", "medium"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Push(deck, Change("deck", Guid.NewGuid(), "upsert",
            new DeckPayload("", "", "en", "ltr", "medium")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Push(deck, deck)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/sync/push",
            new { changes = new[] { new { entityType = "deck", userId = Guid.NewGuid() } } })).StatusCode);
        using var large = new StringContent(new string('a', 131_073), System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _client.PostAsync("/api/v1/sync/push", large)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().SyncChanges.ToListAsync());
    }

    [Fact]
    public async Task Mutable_updates_require_exact_base_version_and_archives_are_terminal()
    {
        await LoginAsync("first@example.com");
        var deckId = Guid.NewGuid();
        var original = Change("deck", deckId, "upsert", new DeckPayload("First", "", "en", "ltr", "medium"));
        Assert.Equal(HttpStatusCode.OK, (await Push(original)).StatusCode);
        var updated = Change("deck", deckId, "upsert", new DeckPayload("Second", "", "en", "ltr", "medium"), 1);
        Assert.Equal(HttpStatusCode.OK, (await Push(updated)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Push(Change("deck", deckId, "upsert",
            new DeckPayload("Stale", "", "en", "ltr", "medium"), 1))).StatusCode);
        var archive = Change("deck", deckId, "archive", (object?)null, 2);
        Assert.Equal(HttpStatusCode.OK, (await Push(archive)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Push(Change("deck", deckId, "archive", (object?)null, 3))).StatusCode);
        Assert.Equal("duplicate", (await (await Push(archive)).Content.ReadFromJsonAsync<SyncPushResponse>())!.Changes[0].Status);
        var pulled = await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=2");
        Assert.Single(pulled!.Changes);
        Assert.Equal("archive", pulled.Changes[0].Operation);
        Assert.NotEqual(JsonValueKind.Null, pulled.Changes[0].Payload.GetProperty("archivedAtUtc").ValueKind);
    }

    [Fact]
    public async Task Separate_devices_append_reviews_and_server_projects_every_result()
    {
        await LoginAsync("first@example.com");
        var deckId = Guid.NewGuid();
        var cardId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Push(
            Change("deck", deckId, "upsert", new DeckPayload("Words", "", "en", "ltr", "medium")),
            Change("card", cardId, "upsert", new CardPayload(deckId, "hi", "hello", null, null,
                JsonSerializer.SerializeToElement(Array.Empty<object>()))))).StatusCode);
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var first = Change("reviewEvent", Guid.NewGuid(), "create",
            new ReviewEventPayload(deckId, cardId, 1, 2, "success", at, null)) with { DeviceId = deviceA };
        var second = Change("reviewEvent", Guid.NewGuid(), "create",
            new ReviewEventPayload(deckId, cardId, 1, 1, "failure", at.AddMinutes(-1), null)) with { DeviceId = deviceB };
        Assert.Equal(HttpStatusCode.OK, (await Push(first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Push(second)).StatusCode);
        var feed = await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=2");
        Assert.Equal([first.EntityId, second.EntityId], feed!.Changes.Select(change => change.EntityId));

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(2, await database.ReviewEvents.CountAsync());
        var state = await database.CardReviewStates.SingleAsync();
        Assert.Equal(1, state.Box);
        Assert.Equal(2, state.TotalReviews);
        Assert.Equal(1, state.TotalSuccesses);
        Assert.Equal(HttpStatusCode.BadRequest, (await Push(Change("reviewState", cardId, "upsert",
            new ReviewStatePayload(5, DateOnly.FromDateTime(DateTime.UtcNow), at, 1, 2, 2)))).StatusCode);
        Assert.Equal(2, await database.ReviewEvents.CountAsync());
    }

    [Fact]
    public async Task Free_deck_limit_is_enforced_for_rest_and_sync_and_counts_only_the_owner()
    {
        await LoginAsync("free@example.com");
        var created = await _client.PostAsJsonAsync("/api/v1/decks", new { name = "One deck" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var rest = await _client.PostAsJsonAsync("/api/v1/decks", new { name = "Second deck" });
        Assert.Equal(HttpStatusCode.Forbidden, rest.StatusCode);
        Assert.Equal("free_tier_deck_limit", (await rest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Push(Change("deck", Guid.NewGuid(), "upsert",
            new DeckPayload("Bypass", "", "en", "ltr", "medium")))).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Equal(1, await database.Decks.CountAsync());
            Assert.Equal(1, await database.SyncChanges.CountAsync());
            Assert.Equal(1, (await database.SyncHeads.SingleAsync()).Version);
        }
        await LoginAsync("other-free@example.com");
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/v1/decks", new { name = "Other owner" })).StatusCode);
    }

    [Fact]
    public async Task Fifty_cards_are_allowed_but_fifty_first_and_second_deck_batch_roll_back_without_data_loss()
    {
        await LoginAsync("free@example.com");
        var deckId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("deck", deckId, "upsert",
            new DeckPayload("Words", "", "en", "ltr", "medium")))).StatusCode);
        var cards = Enumerable.Range(0, 50).Select(index => Change("card", Guid.NewGuid(), "upsert",
            new CardPayload(deckId, $"Word {index}", "Meaning", null, null, JsonSerializer.SerializeToElement(Array.Empty<object>())))).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await Push(cards)).StatusCode);
        var rejected = await Push(Change("card", Guid.NewGuid(), "upsert",
            new CardPayload(deckId, "Fifty first", "Meaning", null, null, JsonSerializer.SerializeToElement(Array.Empty<object>()))));
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal("free_tier_card_limit", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var batch = await Push(Change("deck", deckId, "upsert", new DeckPayload("Must roll back", "", "en", "ltr", "medium"), 1),
            Change("deck", Guid.NewGuid(), "upsert", new DeckPayload("Second", "", "en", "ltr", "medium")));
        Assert.Equal(HttpStatusCode.Forbidden, batch.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(50, await database.Cards.CountAsync());
        Assert.Equal("Words", (await database.Decks.SingleAsync()).Name);
        Assert.Equal(51, await database.SyncChanges.CountAsync());
        Assert.Equal(51, (await database.SyncHeads.SingleAsync()).Version);
        var edit = Change("card", cards[0].EntityId, "upsert", new CardPayload(deckId, "Edited", "Meaning", null, null,
            JsonSerializer.SerializeToElement(Array.Empty<object>())), 2);
        Assert.Equal(HttpStatusCode.OK, (await Push(edit)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("reviewEvent", Guid.NewGuid(), "create",
            new ReviewEventPayload(deckId, cards[0].EntityId, 1, 2, "success", DateTimeOffset.UtcNow, null)))).StatusCode);
    }

    [Fact]
    public async Task Oversized_existing_accounts_remain_editable_but_cannot_grow_and_batch_card_limit_is_atomic()
    {
        await LoginAsync("legacy-free@example.com");
        var deckId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("deck", deckId, "upsert", new DeckPayload("Legacy", "", "en", "ltr", "medium")))).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            for (var index = 0; index < 51; index++) database.Cards.Add(new Flashcards.Domain.Card { DeckId = deckId, FrontText = $"Legacy {index}", Meaning = "Meaning" });
            await database.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("deck", deckId, "upsert",
            new DeckPayload("Edited legacy", "", "en", "ltr", "medium"), 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Push(Change("card", Guid.NewGuid(), "upsert",
            new CardPayload(deckId, "No growth", "Meaning", null, null, JsonSerializer.SerializeToElement(Array.Empty<object>()))))).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Equal(51, await database.Cards.CountAsync());
            Assert.Equal("Edited legacy", (await database.Decks.SingleAsync()).Name);
        }
    }

    [Fact]
    public async Task A_batch_crossing_the_card_limit_rolls_back_all_inserts_and_versions()
    {
        await LoginAsync("batch-free@example.com");
        var deckId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Push(
            Change("deck", deckId, "upsert", new DeckPayload("Words", "", "en", "ltr", "medium")),
            Change("card", Guid.NewGuid(), "upsert", new CardPayload(deckId, "Original one", "Meaning", null, null, JsonSerializer.SerializeToElement(Array.Empty<object>()))),
            Change("card", Guid.NewGuid(), "upsert", new CardPayload(deckId, "Original two", "Meaning", null, null, JsonSerializer.SerializeToElement(Array.Empty<object>()))))).StatusCode);
        var changes = Enumerable.Range(0, 49).Select(index => Change("card", Guid.NewGuid(), "upsert",
            new CardPayload(deckId, $"Word {index}", "Meaning", null, null, JsonSerializer.SerializeToElement(Array.Empty<object>())))).ToArray();
        Assert.Equal(HttpStatusCode.Forbidden, (await Push(changes)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(1, await database.Decks.CountAsync());
        Assert.Equal(2, await database.Cards.CountAsync());
        Assert.Equal(3, await database.SyncChanges.CountAsync());
        Assert.Equal(3, (await database.SyncHeads.SingleAsync()).Version);
    }

    [Fact]
    public async Task Guest_copy_retries_preserve_distinct_cards_and_historical_progress_with_owner_only_reads()
    {
        await LoginAsync("guest-copy@example.com");
        var ownerId = (await _client.GetFromJsonAsync<JsonElement>("/api/v1/me/")).GetProperty("id").GetGuid();
        var deckId = Guid.NewGuid();
        var cardId = Guid.NewGuid();
        var otherCardId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var deck = Change("deck", deckId, "upsert", new DeckPayload("Guest words", "", "en", "ltr", "medium"));
        var cards = new[] { cardId, otherCardId }.Select(id => Change("card", id, "upsert",
            new CardPayload(deckId, "Identical text", "Different identities", null, null,
                JsonSerializer.SerializeToElement(Array.Empty<object>())))).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await Push(deck)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Push(cards)).StatusCode);
        var history = new[]
        {
            Change("reviewEvent", Guid.NewGuid(), "create", new ReviewEventPayload(deckId, cardId, 1, 2, "success", at, null)),
            Change("reviewEvent", Guid.NewGuid(), "create", new ReviewEventPayload(deckId, cardId, 2, 1, "failure", at.AddDays(2), null)),
            Change("reviewEvent", Guid.NewGuid(), "create", new ReviewEventPayload(deckId, cardId, 1, 2, "success", at.AddDays(3), null))
        };
        foreach (var change in history) Assert.Equal(HttpStatusCode.OK, (await Push(change)).StatusCode);
        foreach (var change in history)
            Assert.Equal("duplicate", (await (await Push(change)).Content.ReadFromJsonAsync<SyncPushResponse>())!.Changes[0].Status);
        Assert.Equal("duplicate", (await (await Push(deck)).Content.ReadFromJsonAsync<SyncPushResponse>())!.Changes[0].Status);
        var state = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/me/cards/{cardId}/review-state");
        Assert.Equal(2, state.GetProperty("box").GetInt32());
        Assert.Equal("2026-10-06", state.GetProperty("dueDate").GetString());
        Assert.Equal(3, state.GetProperty("totalReviews").GetInt32());
        Assert.Equal(2, state.GetProperty("totalSuccesses").GetInt32());
        Assert.Equal(1, state.GetProperty("consecutiveSuccesses").GetInt32());
        Assert.Equal(at.AddDays(3), state.GetProperty("lastReviewedAtUtc").GetDateTimeOffset());
        var feed = await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0");
        Assert.Equal(6, feed!.LatestVersion);
        Assert.Equal(3, feed.Changes.Count(change => change.EntityType == "reviewEvent"));
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Equal(ownerId, (await database.Decks.SingleAsync()).UserId);
            Assert.Equal(2, await database.Cards.CountAsync());
            Assert.Equal(3, await database.ReviewEvents.CountAsync());
        }
        await LoginAsync("foreign-copy@example.com");
        Assert.Empty((await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0"))!.Changes);
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>($"/api/v1/me/decks/{deckId}/cards")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/me/cards/{cardId}/review-state")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Push(Change("reviewEvent", Guid.NewGuid(), "create",
            new ReviewEventPayload(deckId, cardId, 2, 3, "success", at.AddDays(4), null)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Push(cards[0])).StatusCode);
        using var verificationScope = _factory.Services.CreateScope();
        Assert.Equal(3, await verificationScope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().ReviewEvents.CountAsync());
    }

    [Fact]
    public async Task Same_name_guest_deck_is_not_merged_or_replaced_when_free_quota_rejects_the_copy()
    {
        await LoginAsync("same-name-copy@example.com");
        var originalId = Guid.NewGuid();
        var original = Change("deck", originalId, "upsert", new DeckPayload("Words", "Account original", "en", "ltr", "medium"));
        Assert.Equal(HttpStatusCode.OK, (await Push(original)).StatusCode);
        var incoming = Change("deck", Guid.NewGuid(), "upsert", new DeckPayload("Words", "Guest different contents", "en", "ltr", "medium"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Push(incoming)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var remaining = await database.Decks.SingleAsync();
        Assert.Equal(originalId, remaining.Id);
        Assert.Equal("Account original", remaining.Description);
        Assert.Equal(1, await database.SyncChanges.CountAsync());
        Assert.Equal(1, (await database.SyncHeads.SingleAsync()).Version);
    }

    private static SyncPushChange Change<T>(string type, Guid entityId, string operation, T payload, long expectedVersion = 0) =>
        new(Guid.NewGuid(), DeviceId, type, entityId, operation, DateTimeOffset.UtcNow, expectedVersion,
            JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private Task<HttpResponseMessage> Push(params SyncPushChange[] changes) =>
        _client.PostAsJsonAsync("/api/v1/sync/push", new SyncPushRequest(changes));

    private async Task LoginAsync(string email)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, password = "My long private passphrase 2026!", displayName = "Student"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
    }

    private sealed class SyncApiFactory(SqliteConnection connection) : TestApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<FlashcardsDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<FlashcardsDbContext>>();
                services.AddDbContext<FlashcardsDbContext>(options => options.UseSqlite(connection));
            });
        }
    }
}
