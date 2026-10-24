using System.Net;
using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Subscriptions;
using Flashcards.Infrastructure.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flashcards.Tests;

public sealed class SubscriptionTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SubscriptionClock _clock = new();
    private readonly QueryCounter _queries = new();
    private SubscriptionApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _connection.CreateFunction("ISJSON", (string value) =>
        {
            try { using var document = JsonDocument.Parse(value); return 1; }
            catch (JsonException) { return 0; }
        });
        await _connection.OpenAsync();
        _factory = new SubscriptionApiFactory(_connection, _clock, _queries);
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
    public async Task Catalog_is_centralized_and_prices_are_toman_without_enabling_any_purchase_flow()
    {
        var catalog = (await _client.GetFromJsonAsync<PlanCatalogResponse>("/api/v1/plans"))!;
        Assert.Equal("Toman", catalog.Currency);
        Assert.False(catalog.PurchaseSupported);
        Assert.Equal(new PlanLimits(1, 50), catalog.FreeLimits);
        Assert.Equal(2, catalog.Plans.Count);
        var plus = Assert.Single(catalog.Plans, plan => plan.Plan == "Plus");
        Assert.Equal(new[] { new PlanPrice(1, 70000), new PlanPrice(6, 294000), new PlanPrice(12, 420000) }, plus.Prices);
        Assert.Equal(49000, plus.Prices[1].PriceToman / plus.Prices[1].DurationMonths);
        Assert.Equal(35000, plus.Prices[2].PriceToman / plus.Prices[2].DurationMonths);
        Assert.Equal(0.7m, (decimal)plus.Prices[1].PriceToman / (plus.Prices[0].PriceToman * 6));
        Assert.Equal(0.5m, (decimal)plus.Prices[2].PriceToman / (plus.Prices[0].PriceToman * 12));
        Assert.Equal(new PlanLimits(null, null), plus.Limits);
        Assert.Equal(new PlanFeatures(true, true), plus.Features);
        Assert.False(plus.IsPurchasable);
        Assert.Equal("payment_provider_not_configured", plus.UnavailableReason);
        var full = Assert.Single(catalog.Plans, plan => plan.Plan == "Full");
        Assert.False(full.IsPurchasable);
        Assert.Equal("coming_soon", full.UnavailableReason);
        Assert.Null(full.Limits);
        Assert.Null(full.Features);
        Assert.Empty(full.Prices);
    }

    [Theory]
    [InlineData(-1, 1, SubscriptionStatus.Active, SubscriptionPlan.Plus, "Active", true)]
    [InlineData(0, 1, SubscriptionStatus.Active, SubscriptionPlan.Plus, "Active", true)]
    [InlineData(-1, 0, SubscriptionStatus.Active, SubscriptionPlan.Plus, "Expired", false)]
    [InlineData(-2, -1, SubscriptionStatus.Active, SubscriptionPlan.Plus, "Expired", false)]
    [InlineData(1, 2, SubscriptionStatus.Active, SubscriptionPlan.Plus, "Scheduled", false)]
    [InlineData(-1, 1, SubscriptionStatus.Cancelled, SubscriptionPlan.Plus, "Cancelled", false)]
    [InlineData(-1, 1, SubscriptionStatus.Expired, SubscriptionPlan.Plus, "Expired", false)]
    [InlineData(-1, 1, SubscriptionStatus.Active, SubscriptionPlan.Full, "Unavailable", false)]
    public async Task Access_uses_status_start_expiry_and_supported_plan_not_only_the_stored_active_flag(
        int startHours, int expiryHours, SubscriptionStatus storedStatus, SubscriptionPlan plan, string effectiveStatus, bool active)
    {
        var userId = await LoginAsync("owner@example.com");
        await SeedAsync(userId, storedStatus, plan, _clock.Now.AddHours(startHours), _clock.Now.AddHours(expiryHours));
        var result = await CurrentAsync();
        Assert.Equal(effectiveStatus, result.Subscription!.Status);
        Assert.Equal(storedStatus.ToString(), result.Subscription.StoredStatus);
        Assert.Equal(plan.ToString(), result.Subscription.Plan);
        Assert.Equal(active, result.Entitlements.IsActive);
        Assert.Equal(active ? "Plus" : "Free", result.Entitlements.CurrentPlan);
        Assert.Equal(active ? new PlanLimits(null, null) : PlanCatalog.FreeLimits, result.Entitlements.Limits);
        Assert.Equal(active ? new PlanFeatures(true, true) : PlanCatalog.FreeFeatures, result.Entitlements.Features);
        Assert.Equal(_clock.Now, result.Entitlements.ValidatedAtUtc);
        Assert.Equal(_clock.Now.AddHours(expiryHours), result.Entitlements.ExpiresAtUtc);
        Assert.True(result.Entitlements.WithinFreeTierLimits);
        Assert.False(result.Entitlements.IsPurchasable);
        Assert.False(result.Entitlements.PurchaseSupported);
    }

    [Fact]
    public async Task Expiration_is_exclusive_at_the_exact_tick_without_rewriting_or_extending_stale_active_rows()
    {
        var userId = await LoginAsync("owner@example.com");
        var expires = _clock.Now.AddHours(1);
        await SeedAsync(userId, SubscriptionStatus.Active, SubscriptionPlan.Plus, _clock.Now, expires);
        _clock.Now = expires.AddTicks(-1);
        Assert.True((await CurrentAsync()).Entitlements.IsActive);
        _clock.Now = expires;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await CurrentAsync();
            Assert.False(result.Entitlements.IsActive);
            Assert.Equal("Expired", result.Subscription!.Status);
            Assert.Equal(expires, result.Subscription.ExpiresAtUtc);
            Assert.Equal("Active", result.Subscription.StoredStatus);
        }
        using var scope = _factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Subscriptions.SingleAsync();
        Assert.Equal(SubscriptionStatus.Active, saved.Status);
        Assert.Equal(expires, saved.ExpiresAtUtc);
    }

    [Fact]
    public async Task User_specific_routes_require_active_auth_and_never_trust_a_query_user_id_or_expose_foreign_metadata()
    {
        foreach (var path in new[] { "/api/v1/me/subscription", "/api/v1/me/entitlements" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(path)).StatusCode);
        var owner = await LoginAsync("owner@example.com");
        await SeedAsync(owner, SubscriptionStatus.Active, SubscriptionPlan.Plus, _clock.Now.AddHours(-1), _clock.Now.AddHours(1));
        Assert.True((await CurrentAsync()).Entitlements.IsActive);
        var foreign = await LoginAsync("foreign@example.com");
        var result = (await _client.GetFromJsonAsync<SubscriptionResponse>($"/api/v1/me/subscription?userId={owner}"))!;
        Assert.Null(result.Subscription);
        Assert.Equal("Free", result.Entitlements.CurrentPlan);
        Assert.NotEqual(owner, foreign);
        var entitlementResponse = await _client.GetAsync($"/api/v1/me/entitlements?userId={owner}");
        Assert.Equal("no-store", entitlementResponse.Headers.CacheControl?.ToString());
        Assert.Equal(result.Entitlements, await entitlementResponse.Content.ReadFromJsonAsync<EntitlementResponse>());
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/me/subscription")).StatusCode);
    }

    [Fact]
    public async Task Repeated_reads_are_read_only_and_no_purchase_or_activation_endpoint_exists()
    {
        var userId = await LoginAsync("owner@example.com");
        await SeedAsync(userId, SubscriptionStatus.Active, SubscriptionPlan.Plus, _clock.Now.AddHours(-1), _clock.Now.AddHours(1));
        var first = await CurrentAsync();
        for (var attempt = 0; attempt < 3; attempt++) Assert.Equal(first, await CurrentAsync());
        foreach (var path in new[] { "/api/v1/me/subscription", "/api/v1/me/entitlements", "/api/v1/subscriptions", "/api/v1/plans/Full/purchase", "/api/v1/plans/Plus/purchase" })
        {
            var response = await _client.PostAsJsonAsync(path, new { userId, plan = "Full", status = "Active", expiresAtUtc = "2099-01-01" });
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        }
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Subscriptions.CountAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await PushAsync("subscription", Guid.NewGuid(),
            new { userId, plan = "Full", status = "Active", expiresAtUtc = "2099-01-01" })).StatusCode);
        Assert.Equal(first, await CurrentAsync());
    }

    [Fact]
    public async Task Unique_active_index_rejects_future_overlaps_and_stale_expired_active_rows_until_atomic_retirement()
    {
        var userId = await LoginAsync("owner@example.com");
        await SeedAsync(userId, SubscriptionStatus.Active, SubscriptionPlan.Plus, _clock.Now.AddHours(-2), _clock.Now);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        database.Subscriptions.Add(new Subscription { UserId = userId, Status = SubscriptionStatus.Active,
            StartsAtUtc = _clock.Now.AddHours(1), ExpiresAtUtc = _clock.Now.AddHours(2) });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        database.ChangeTracker.Clear();
        await using (var transaction = await database.Database.BeginTransactionAsync())
        {
            var expired = await database.Subscriptions.SingleAsync();
            expired.Status = SubscriptionStatus.Expired;
            await database.SaveChangesAsync();
            database.Subscriptions.Add(new Subscription { UserId = userId, Status = SubscriptionStatus.Active,
                StartsAtUtc = _clock.Now, ExpiresAtUtc = _clock.Now.AddHours(1) });
            await database.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        database.ChangeTracker.Clear();
        database.Subscriptions.Add(new Subscription { UserId = userId, Status = SubscriptionStatus.Active,
            StartsAtUtc = _clock.Now, ExpiresAtUtc = _clock.Now.AddHours(3) });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        database.ChangeTracker.Clear();
        Assert.Equal(2, await database.Subscriptions.CountAsync());
        Assert.Equal(1, await database.Subscriptions.CountAsync(item => item.Status == SubscriptionStatus.Active));
        var latest = await CurrentAsync();
        Assert.True(latest.Entitlements.IsActive);
        Assert.Equal(_clock.Now.AddHours(1), latest.Subscription!.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(99, 0, 1)]
    [InlineData(0, 99, 1)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0, -1)]
    public async Task Database_constraints_reject_invalid_plan_status_and_non_positive_periods(int plan, int status, int expiryHours)
    {
        var userId = await LoginAsync("owner@example.com");
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        database.Subscriptions.Add(new Subscription { UserId = userId, Plan = (SubscriptionPlan)plan,
            Status = (SubscriptionStatus)status, StartsAtUtc = _clock.Now, ExpiresAtUtc = _clock.Now.AddHours(expiryHours) });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
    }

    [Fact]
    public async Task Periods_are_normalized_to_utc_and_timestamps_are_not_changed_by_status_reads()
    {
        var userId = await LoginAsync("owner@example.com");
        await SeedAsync(userId, SubscriptionStatus.Active, SubscriptionPlan.Plus,
            _clock.Now.AddHours(-1).ToOffset(TimeSpan.FromHours(3.5)), _clock.Now.AddHours(1).ToOffset(TimeSpan.FromHours(3.5)));
        var first = await CurrentAsync();
        Assert.Equal(TimeSpan.Zero, first.Subscription!.StartsAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, first.Subscription.ExpiresAtUtc.Offset);
        Assert.True(first.Subscription.CreatedAtUtc > DateTimeOffset.MinValue);
        Assert.True(first.Subscription.UpdatedAtUtc >= first.Subscription.CreatedAtUtc);
        Assert.Equal(first, await CurrentAsync());
    }

    [Fact]
    public async Task No_subscription_is_free_and_usage_counts_only_the_authenticated_owners_non_archived_rows()
    {
        var owner = await LoginAsync("owner@example.com");
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var deck = new Deck { UserId = owner, Name = "Owner" };
            var foreign = new Deck { User = new User { DisplayName = "Foreign" }, Name = "Foreign" };
            database.AddRange(deck, new Card { Deck = foreign, FrontText = "Foreign card", Meaning = "meaning" },
                new Card { Deck = deck, FrontText = "archived", Meaning = "meaning", ArchivedAtUtc = _clock.Now },
                new Deck { UserId = owner, Name = "Archived", ArchivedAtUtc = _clock.Now });
            database.Cards.AddRange(Enumerable.Range(0, 50).Select(number => new Card { Deck = deck, FrontText = $"word {number}", Meaning = "meaning" }));
            await database.SaveChangesAsync();
        }
        var result = await CurrentAsync();
        Assert.Null(result.Subscription);
        Assert.Equal("Free", result.Entitlements.CurrentPlan);
        Assert.Equal(new LearningUsage(1, 50), result.Entitlements.Usage);
        Assert.True(result.Entitlements.WithinFreeTierLimits);
        Assert.Equal(PlanCatalog.FreeLimits, result.Entitlements.Limits);
    }

    [Fact]
    public async Task Plus_applies_to_real_deck_and_sync_card_writes_then_expiration_blocks_growth_but_preserves_existing_data_and_edits()
    {
        var owner = await LoginAsync("owner@example.com");
        await SeedAsync(owner, SubscriptionStatus.Active, SubscriptionPlan.Plus, _clock.Now.AddHours(-1), _clock.Now.AddHours(1));
        var first = await _client.PostAsJsonAsync("/api/v1/decks", new { name = "First" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var deck = await first.Content.ReadFromJsonAsync<JsonElement>();
        var deckId = deck.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/v1/decks", new { name = "Second" })).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            database.Cards.AddRange(Enumerable.Range(0, 50).Select(number => new Card { DeckId = deckId, FrontText = $"word {number}", Meaning = "meaning" }));
            await database.SaveChangesAsync();
        }
        var card = new CardPayload(deckId, "Fifty-first", "meaning", null, null, JsonSerializer.SerializeToElement(Array.Empty<string>()));
        Assert.Equal(HttpStatusCode.OK, (await PushAsync("card", Guid.NewGuid(), card)).StatusCode);
        var active = await CurrentAsync();
        Assert.True(active.Entitlements.IsActive);
        Assert.False(active.Entitlements.WithinFreeTierLimits);
        Assert.Equal(new LearningUsage(2, 51), active.Entitlements.Usage);
        _clock.Now = _clock.Now.AddHours(1);
        var denied = await _client.PostAsJsonAsync("/api/v1/decks", new { name = "Third" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("free_tier_deck_limit", await denied.Content.ReadAsStringAsync());
        var deniedCard = await PushAsync("card", Guid.NewGuid(), card);
        Assert.Equal(HttpStatusCode.Forbidden, deniedCard.StatusCode);
        Assert.Contains("free_tier_card_limit", await deniedCard.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await PushAsync("deck", deckId, new DeckPayload("Edited", "", "en", "ltr", "medium"), deck.GetProperty("syncVersion").GetInt64())).StatusCode);
        var expired = await CurrentAsync();
        Assert.False(expired.Entitlements.IsActive);
        Assert.False(expired.Entitlements.WithinFreeTierLimits);
        Assert.Equal(new LearningUsage(2, 51), expired.Entitlements.Usage);
        using var inspect = _factory.Services.CreateScope();
        var saved = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(2, await saved.Decks.CountAsync());
        Assert.Equal(51, await saved.Cards.CountAsync());
        Assert.Equal("Edited", (await saved.Decks.FindAsync(deckId))!.Name);
    }

    [Theory]
    [InlineData(SubscriptionPlan.Full, SubscriptionStatus.Active, -1, 1)]
    [InlineData(SubscriptionPlan.Plus, SubscriptionStatus.Active, 1, 2)]
    [InlineData(SubscriptionPlan.Plus, SubscriptionStatus.Active, -1, 0)]
    [InlineData(SubscriptionPlan.Plus, SubscriptionStatus.Cancelled, -1, 1)]
    public async Task Reserved_future_expired_and_cancelled_subscriptions_cannot_bypass_server_free_limits(
        SubscriptionPlan plan, SubscriptionStatus status, int startHours, int expiryHours)
    {
        var owner = await LoginAsync("owner@example.com");
        await SeedAsync(owner, status, plan, _clock.Now.AddHours(startHours), _clock.Now.AddHours(expiryHours));
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/v1/decks", new { name = "First" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/v1/decks", new { name = "Second" })).StatusCode);
    }

    [Theory]
    [InlineData(1, 50, false, true)]
    [InlineData(2, 1, false, false)]
    [InlineData(1, 51, false, false)]
    [InlineData(2, 51, true, true)]
    public async Task Study_authorization_and_all_review_sync_paths_use_current_account_entitlements(
        int deckCount, int cardCount, bool plus, bool allowed)
    {
        var owner = await LoginAsync("owner@example.com");
        if (plus) await SeedAsync(owner, SubscriptionStatus.Active, SubscriptionPlan.Plus, _clock.Now.AddHours(-1), _clock.Now.AddHours(1));
        var deckId = Guid.NewGuid();
        var cardId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            database.Decks.Add(new Deck { Id = deckId, UserId = owner, Name = "First" });
            database.Decks.AddRange(Enumerable.Range(1, deckCount - 1).Select(number => new Deck { UserId = owner, Name = $"Deck {number}" }));
            database.Cards.Add(new Card { Id = cardId, DeckId = deckId, FrontText = "First", Meaning = "Meaning" });
            database.Cards.AddRange(Enumerable.Range(1, cardCount - 1).Select(number => new Card { DeckId = deckId, FrontText = $"Card {number}", Meaning = "Meaning" }));
            await database.SaveChangesAsync();
        }
        var authorization = await _client.PostAsync("/api/v1/me/study/authorize", null);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, authorization.StatusCode);
        var initial = new ReviewStatePayload(1, new DateOnly(2026, 10, 15), null, 0, 0, 0);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await PushAsync("reviewState", cardId, initial)).StatusCode);
        var reviewId = Guid.NewGuid();
        var request = new SyncPushRequest([new SyncPushChange(Guid.NewGuid(), Guid.NewGuid(), "reviewEvent", reviewId, "create", _clock.Now, 0,
            JsonSerializer.SerializeToElement(new ReviewEventPayload(deckId, cardId, 1, 2, "success", _clock.Now, null), new JsonSerializerOptions(JsonSerializerDefaults.Web)))]);
        var review = await _client.PostAsJsonAsync("/api/v1/sync/push", request);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, review.StatusCode);
        if (!allowed)
        {
            var error = await review.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("free_tier_dataset_limit", error.GetProperty("code").GetString());
            Assert.Equal(1, error.GetProperty("limits").GetProperty("decks").GetInt32());
            Assert.Equal(50, error.GetProperty("limits").GetProperty("cards").GetInt32());
            Assert.Equal(cardCount, error.GetProperty("usage").GetProperty("cards").GetInt32());
        }
        if (plus)
        {
            _clock.Now = _clock.Now.AddHours(1);
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync("/api/v1/me/study/authorize", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/v1/sync/push", request)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await PushAsync("reviewState", cardId, initial, 1)).StatusCode);
            var newReview = request with { Changes = [request.Changes[0] with { ClientChangeId = Guid.NewGuid(), EntityId = Guid.NewGuid() }] };
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/v1/sync/push", newReview)).StatusCode);
        }
        using var inspect = _factory.Services.CreateScope();
        var saved = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(deckCount, await saved.Decks.CountAsync());
        Assert.Equal(cardCount, await saved.Cards.CountAsync());
        Assert.Equal(allowed ? 1 : 0, await saved.ReviewEvents.CountAsync());
        Assert.Equal(allowed ? 2 : 0, await saved.SyncChanges.CountAsync());
        Assert.Equal(allowed ? 1 : 0, await saved.CardReviewStates.CountAsync());
    }

    [Fact]
    public async Task Cross_dimension_growth_and_bulk_sync_are_rejected_atomically_but_explicit_archiving_restores_free_study()
    {
        var owner = await LoginAsync("owner@example.com");
        var deckId = Guid.NewGuid();
        var extraDeck = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            database.Decks.AddRange(new Deck { Id = deckId, UserId = owner, Name = "First", SyncVersion = 1 },
                new Deck { Id = extraDeck, UserId = owner, Name = "Extra", SyncVersion = 1 });
            await database.SaveChangesAsync();
        }
        var card = new CardPayload(deckId, "New", "meaning", null, null, JsonSerializer.SerializeToElement(Array.Empty<string>()));
        Assert.Equal(HttpStatusCode.Forbidden, (await PushAsync("card", Guid.NewGuid(), card)).StatusCode);
        var archive = new SyncPushChange(Guid.NewGuid(), Guid.NewGuid(), "deck", extraDeck, "archive", _clock.Now, 1, JsonSerializer.SerializeToElement<object?>(null));
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/v1/sync/push", new SyncPushRequest([archive]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/v1/me/study/authorize", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PushAsync("card", Guid.NewGuid(), card)).StatusCode);
        var changes = Enumerable.Range(0, 50).Select(number => new SyncPushChange(Guid.NewGuid(), Guid.NewGuid(), "card", Guid.NewGuid(), "upsert", _clock.Now, 0,
            JsonSerializer.SerializeToElement(card, new JsonSerializerOptions(JsonSerializerDefaults.Web)))).ToArray();
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/v1/sync/push", new SyncPushRequest(changes))).StatusCode);
        using var inspect = _factory.Services.CreateScope();
        var saved = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Single(await saved.Cards.ToListAsync());
        Assert.Equal(2, await saved.SyncChanges.CountAsync());
        Assert.Equal(2, await saved.Decks.CountAsync());
        Assert.NotNull((await saved.Decks.FindAsync(extraDeck))!.ArchivedAtUtc);
    }

    [Fact]
    public async Task Excess_cards_also_block_deck_creation_and_plus_of_another_account_does_not_authorize_study()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync("/api/v1/me/study/authorize", null)).StatusCode);
        var paidOwner = await LoginAsync("paid@example.com");
        await SeedAsync(paidOwner, SubscriptionStatus.Active, SubscriptionPlan.Plus, _clock.Now.AddHours(-1), _clock.Now.AddHours(1));
        var owner = await LoginAsync("owner@example.com");
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var archived = new Deck { UserId = owner, Name = "Archived", ArchivedAtUtc = _clock.Now };
            database.Cards.AddRange(Enumerable.Range(0, 51).Select(number => new Card { Deck = archived, FrontText = $"Card {number}", Meaning = "Meaning" }));
            await database.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/v1/decks", new { name = "New", userId = paidOwner })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync($"/api/v1/me/study/authorize?userId={paidOwner}", null)).StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>("/api/v1/me/decks")).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/sync/pull?cursor=0")).StatusCode);
    }

    [Fact]
    public async Task Entitlement_reads_combine_usage_counts_and_unlimited_write_guards_skip_them()
    {
        var owner = await LoginAsync("queries@example.com");
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<EntitlementService>();
        _queries.Commands.Clear();
        var free = await service.AuthorizeStudyAsync(owner);
        Assert.Equal(new LearningUsage(0, 0), free.Usage);
        Assert.Equal(3, _queries.Commands.Count);
        var usage = Assert.Single(_queries.Commands, command => command.Contains("COUNT(*)"));
        Assert.Contains("Decks", usage);
        Assert.Contains("Cards", usage);

        await SeedAsync(owner, SubscriptionStatus.Active, SubscriptionPlan.Plus, _clock.Now.AddHours(-1), _clock.Now.AddHours(1));
        _queries.Commands.Clear();
        await service.EnsureDeckCreationAllowedAsync(owner);
        Assert.Single(_queries.Commands);
        Assert.DoesNotContain("COUNT(*)", _queries.Commands[0]);
        _queries.Commands.Clear();
        Assert.True((await service.AuthorizeStudyAsync(owner)).IsActive);
        Assert.Equal(2, _queries.Commands.Count);
    }

    [Fact]
    public async Task Caught_up_sync_pull_only_reads_the_user_head_and_preserves_cursor_validation()
    {
        var owner = await LoginAsync("idle@example.com");
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SyncService>();
        _queries.Commands.Clear();
        var response = await service.PullAsync(owner, 0);
        Assert.Empty(response.Changes);
        Assert.False(response.HasMore);
        Assert.Equal(0, response.Cursor);
        Assert.Single(_queries.Commands);
        Assert.Contains("SyncHeads", _queries.Commands[0]);
        await Assert.ThrowsAsync<SyncException>(() => service.PullAsync(owner, 1));
        await Assert.ThrowsAsync<SyncException>(() => service.PullAsync(owner, -1));
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private async Task<Guid> LoginAsync(string email)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "A private password for tests!", displayName = "Student" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return (await _client.GetFromJsonAsync<JsonElement>("/api/v1/me/")).GetProperty("id").GetGuid();
    }

    private async Task SeedAsync(Guid userId, SubscriptionStatus status, SubscriptionPlan plan, DateTimeOffset starts, DateTimeOffset expires)
    {
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        database.Subscriptions.Add(new Subscription { UserId = userId, Plan = plan, Status = status, StartsAtUtc = starts, ExpiresAtUtc = expires });
        await database.SaveChangesAsync();
    }

    private async Task<SubscriptionResponse> CurrentAsync() =>
        (await _client.GetFromJsonAsync<SubscriptionResponse>("/api/v1/me/subscription"))!;

    private Task<HttpResponseMessage> PushAsync(string entityType, Guid id, object payload, long expectedVersion = 0) =>
        _client.PostAsJsonAsync("/api/v1/sync/push", new SyncPushRequest([
            new SyncPushChange(Guid.NewGuid(), Guid.NewGuid(), entityType, id, "upsert", _clock.Now,
                expectedVersion, JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)))]));

    private sealed class SubscriptionClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SubscriptionModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            var entity = modelBuilder.Entity<Subscription>();
            var converter = new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            entity.Property(item => item.StartsAtUtc).HasConversion(converter).HasColumnType("bigint");
            entity.Property(item => item.ExpiresAtUtc).HasConversion(converter).HasColumnType("bigint");
            entity.Property(item => item.CreatedAtUtc).HasConversion(converter).HasColumnType("bigint");
            entity.Property(item => item.UpdatedAtUtc).HasConversion(converter).HasColumnType("bigint");
        }
    }

    private sealed class SubscriptionApiFactory(SqliteConnection connection, SubscriptionClock clock, QueryCounter queries) : TestApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<FlashcardsDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<FlashcardsDbContext>>();
                services.AddDbContext<FlashcardsDbContext>(options => options.UseSqlite(connection)
                    .ReplaceService<IModelCustomizer, SubscriptionModelCustomizer>().AddInterceptors(queries));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        }
    }
}
