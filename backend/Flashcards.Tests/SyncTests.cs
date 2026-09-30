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
            Change("deck", otherId, "upsert", new DeckPayload("Rolled back", "", "en", "ltr", "medium")),
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
