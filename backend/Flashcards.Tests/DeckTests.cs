using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flashcards.Api.Endpoints;
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

public sealed class DeckTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DeckApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _connection.CreateFunction("ISJSON", (string value) =>
        {
            try { using var document = JsonDocument.Parse(value); return 1; }
            catch (JsonException) { return 0; }
        });
        await _connection.OpenAsync();
        _factory = new DeckApiFactory(_connection);
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
    public async Task Creation_persists_default_deck_initial_version_and_feed_entry()
    {
        await SignInAsync("owner@example.com");
        var ownerId = (await _client.GetFromJsonAsync<JsonElement>("/api/v1/me/" )).GetProperty("id").GetGuid();
        var response = await _client.PostAsJsonAsync("/api/v1/decks", new { name = "  Vocabulary  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/v1/me/decks", response.Headers.Location?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var created = (await response.Content.ReadFromJsonAsync<CreateDeckResponse>())!;
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("Vocabulary", created.Name);
        Assert.Equal(1L, created.SyncVersion);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var deck = await database.Decks.SingleAsync();
        Assert.Equal(created.Id, deck.Id);
        Assert.Equal(ownerId, deck.UserId);
        Assert.Equal(created.Name, deck.Name);
        Assert.Equal(created.SyncVersion, deck.SyncVersion);
        Assert.Equal(string.Empty, deck.Description);
        Assert.Equal("en", deck.Language);
        Assert.Equal("ltr", deck.TextAlignment);
        Assert.Equal("medium", deck.TypographySize);
        Assert.Null(deck.ArchivedAtUtc);
        Assert.True(deck.CreatedAtUtc > DateTimeOffset.MinValue);
        Assert.True(deck.UpdatedAtUtc >= deck.CreatedAtUtc);
        var head = await database.SyncHeads.SingleAsync();
        Assert.Equal(ownerId, head.UserId);
        Assert.Equal(created.SyncVersion, head.Version);
        var change = await database.SyncChanges.SingleAsync();
        Assert.Equal(ownerId, change.UserId);
        Assert.Equal(created.Id, change.EntityId);
        Assert.Equal(created.SyncVersion, change.Version);
        Assert.Equal(0L, change.ExpectedVersion);
        Assert.Equal("deck", change.EntityType);
        Assert.Equal("upsert", change.Operation);
        Assert.NotEqual(Guid.Empty, change.DeviceId);
        Assert.NotEqual(Guid.Empty, change.ClientChangeId);
        Assert.Equal("Vocabulary", JsonSerializer.Deserialize<JsonElement>(change.PayloadJson).GetProperty("name").GetString());
        var listed = await _client.GetFromJsonAsync<JsonElement>("/api/v1/me/decks");
        Assert.Equal(created.Id, Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Creation_ignores_client_owner_ids_and_is_isolated_from_other_users()
    {
        var owner = await SignInAsync("owner@example.com");
        var ownerId = (await _client.GetFromJsonAsync<JsonElement>("/api/v1/me/" )).GetProperty("id").GetGuid();
        var other = await SignInAsync("other@example.com");
        var otherId = (await _client.GetFromJsonAsync<JsonElement>("/api/v1/me/" )).GetProperty("id").GetGuid();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);

        var response = await _client.PostAsJsonAsync("/api/v1/decks", new
        {
            name = "Private", userId = otherId, ownerId = otherId
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateDeckResponse>())!;
        var ownerFeed = (await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0"))!;
        Assert.Equal(created.Id, Assert.Single(ownerFeed.Changes).EntityId);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>("/api/v1/me/decks")).EnumerateArray());
        var otherFeed = (await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0"))!;
        Assert.Empty(otherFeed.Changes);
        Assert.Equal(0L, otherFeed.LatestVersion);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(ownerId, (await database.Decks.SingleAsync()).UserId);
        Assert.Equal(ownerId, (await database.SyncChanges.SingleAsync()).UserId);
    }

    [Fact]
    public async Task Created_decks_share_versions_with_sync_push_and_are_visible_after_pull_cursor()
    {
        await SignInAsync("owner@example.com");
        var first = await CreateAsync("First");
        var update = new SyncPushChange(Guid.NewGuid(), Guid.NewGuid(), "deck", first.Id, "upsert",
            DateTimeOffset.UtcNow, first.SyncVersion,
            JsonSerializer.SerializeToElement(new DeckPayload("Updated", "", "en", "ltr", "medium"),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync("/api/v1/sync/push", new SyncPushRequest([update]))).StatusCode);
        await ArchiveAsync(first.Id, 2);
        var second = await CreateAsync("Second");
        Assert.Equal(4L, second.SyncVersion);

        var feed = (await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=1"))!;
        Assert.Equal(4L, feed.Cursor);
        Assert.Equal(4L, feed.LatestVersion);
        Assert.False(feed.HasMore);
        Assert.Equal([2L, 3L, 4L], feed.Changes.Select(change => change.ServerVersion));
        Assert.Equal("Updated", feed.Changes[0].Payload.GetProperty("name").GetString());
        var createdChange = feed.Changes[2];
        Assert.Equal(second.Id, createdChange.EntityId);
        Assert.Equal("deck", createdChange.EntityType);
        Assert.Equal("upsert", createdChange.Operation);
        Assert.Equal(0L, createdChange.ExpectedVersion);
        Assert.Equal("Second", createdChange.Payload.GetProperty("name").GetString());
        Assert.Equal("en", createdChange.Payload.GetProperty("language").GetString());
        Assert.Equal(JsonValueKind.Null, createdChange.Payload.GetProperty("archivedAtUtc").ValueKind);
    }

    [Theory]
    [InlineData("Decks")]
    [InlineData("SyncChanges")]
    public async Task Persistence_failure_rolls_back_deck_version_and_feed(string table)
    {
        await SignInAsync("owner@example.com");
        var existing = await CreateAsync("Existing");
        await ArchiveAsync(existing.Id, existing.SyncVersion);
        existing = existing with { SyncVersion = 2 };
        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = $"""
                CREATE TRIGGER FailDeckCreation BEFORE INSERT ON "{table}"
                BEGIN
                    SELECT RAISE(ABORT, 'Deck persistence test failure');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var response = await _client.PostAsJsonAsync("/api/v1/decks", new { name = "Must roll back" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("storage_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.DoesNotContain("Deck persistence test failure", await response.Content.ReadAsStringAsync());
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Equal(existing.Id, (await database.Decks.SingleAsync()).Id);
            Assert.Equal(existing.SyncVersion, (await database.SyncHeads.SingleAsync()).Version);
            Assert.Equal(existing.Id, (await database.SyncChanges.OrderByDescending(item => item.Version).FirstAsync()).EntityId);
        }
        var feed = (await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=2"))!;
        Assert.Empty(feed.Changes);
        Assert.Equal(existing.SyncVersion, feed.LatestVersion);

        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER FailDeckCreation";
            await command.ExecuteNonQueryAsync();
        }
        var retry = await CreateAsync("Retry");
        Assert.Equal(3L, retry.SyncVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Invalid_names_are_rejected_without_persisting_changes(string? name)
    {
        await SignInAsync("owner@example.com");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/v1/decks", new { name })).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Empty(await database.Decks.ToListAsync());
        Assert.Empty(await database.SyncChanges.ToListAsync());
        Assert.Equal(0L, (await database.SyncHeads.SingleAsync()).Version);
    }

    private async Task ArchiveAsync(Guid deckId, long version)
    {
        var archive = new SyncPushChange(Guid.NewGuid(), Guid.NewGuid(), "deck", deckId, "archive", DateTimeOffset.UtcNow,
            version, JsonSerializer.SerializeToElement<object?>(null));
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/v1/sync/push", new SyncPushRequest([archive]))).StatusCode);
    }

    private async Task<CreateDeckResponse> CreateAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/decks", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateDeckResponse>())!;
    }

    private async Task<AuthTokens> SignInAsync(string email)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, password = "My long private passphrase 2026!", displayName = "Student"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return tokens;
    }

    private sealed class DeckApiFactory(SqliteConnection connection) : TestApiFactory
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
