using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Subscriptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Flashcards.Tests;

public sealed class MyketPaymentTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly PaymentClock clock = new();
    private readonly FakeVerifier verifier = new();
    private readonly User owner = new() { DisplayName = "Buyer" };
    private FlashcardsDbContext database = null!;
    private MyketSettings settings = null!;
    private MyketPaymentService service = null!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        connection.CreateFunction("ISJSON", (string value) => { try { using var json = JsonDocument.Parse(value); return 1; } catch { return 0; } });
        database = new(new DbContextOptionsBuilder<FlashcardsDbContext>().UseSqlite(connection)
            .ReplaceService<IModelCustomizer, PaymentModelCustomizer>().Options);
        await database.Database.EnsureCreatedAsync();
        database.Users.Add(owner);
        await database.SaveChangesAsync();
        using var rsa = RSA.Create(2048);
        settings = new() { Enabled = true, PackageName = "com.example.lerona", PublicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()),
            AccessToken = "test-server-only-token", Products = new() { [1] = "plus1", [6] = "plus6", [12] = "plus12" } };
        var catalog = new PlanCatalog(settings);
        service = new(database, settings, catalog, new EntitlementService(database, catalog, clock), verifier, clock);
    }

    public async Task DisposeAsync() { await database.DisposeAsync(); await connection.DisposeAsync(); }

    [Fact]
    public async Task Verified_purchase_activates_once_and_replay_never_extends_the_period()
    {
        var order = await service.BeginAsync(owner.Id, 6);
        verifier.Result = new(clock.Now.ToUnixTimeMilliseconds(), order.DeveloperPayload, 0, 0);
        var first = await service.VerifyAsync(owner.Id, new(order.Sku, "verified-token"));
        Assert.True(first.Entitlements.IsActive);
        Assert.Equal(clock.Now.AddMonths(6), first.Subscription!.ExpiresAtUtc);
        clock.Now = clock.Now.AddDays(1);
        var second = await service.VerifyAsync(owner.Id, new(order.Sku, "verified-token"));
        Assert.Equal(first.Subscription.Id, second.Subscription!.Id);
        Assert.Equal(first.Subscription.ExpiresAtUtc, second.Subscription.ExpiresAtUtc);
        Assert.Single(await database.Subscriptions.ToListAsync());
        Assert.Equal(1, verifier.Calls);
        Assert.NotEqual("verified-token", (await database.MyketOrders.SingleAsync()).TokenHash);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task Failed_or_already_consumed_provider_receipts_cannot_activate(int purchaseState, int consumptionState)
    {
        var order = await service.BeginAsync(owner.Id, 1);
        verifier.Result = new(clock.Now.ToUnixTimeMilliseconds(), order.DeveloperPayload, purchaseState, consumptionState);
        var error = await Assert.ThrowsAsync<PaymentException>(() => service.VerifyAsync(owner.Id, new(order.Sku, "bad")));
        Assert.Equal("invalid_receipt", error.Code);
        Assert.Empty(await database.Subscriptions.ToListAsync());
    }

    [Fact]
    public async Task Provider_failure_keeps_order_recoverable_without_activation()
    {
        var order = await service.BeginAsync(owner.Id, 1);
        verifier.Failure = new PaymentException(503, "verification_unavailable", "Unavailable");
        await Assert.ThrowsAsync<PaymentException>(() => service.VerifyAsync(owner.Id, new(order.Sku, "pending")));
        Assert.Null((await database.MyketOrders.SingleAsync()).SubscriptionId);
        Assert.Empty(await database.Subscriptions.ToListAsync());
        verifier.Failure = null;
        verifier.Result = new(clock.Now.ToUnixTimeMilliseconds(), order.DeveloperPayload, 0, 0);
        Assert.True((await service.VerifyAsync(owner.Id, new(order.Sku, "pending"))).Entitlements.IsActive);
    }

    [Fact]
    public async Task Other_accounts_cannot_claim_a_purchase_before_or_after_activation()
    {
        var foreign = new User { DisplayName = "Other buyer" };
        database.Users.Add(foreign);
        await database.SaveChangesAsync();
        var order = await service.BeginAsync(owner.Id, 1);
        verifier.Result = new(clock.Now.ToUnixTimeMilliseconds(), order.DeveloperPayload, 0, 0);
        await Assert.ThrowsAsync<PaymentException>(() => service.VerifyAsync(foreign.Id, new(order.Sku, "owned-token")));
        Assert.Empty(await database.Subscriptions.ToListAsync());
        await service.VerifyAsync(owner.Id, new(order.Sku, "owned-token"));
        await Assert.ThrowsAsync<PaymentException>(() => service.VerifyAsync(foreign.Id, new(order.Sku, "owned-token")));
    }

    [Fact]
    public async Task Checkout_retries_are_idempotent_and_another_period_is_blocked_until_cancellation()
    {
        var first = await service.BeginAsync(owner.Id, 1);
        Assert.Equal(first, await service.BeginAsync(owner.Id, 1));
        Assert.Equal("checkout_pending", (await Assert.ThrowsAsync<PaymentException>(() => service.BeginAsync(owner.Id, 6))).Code);
        await service.CancelAsync(owner.Id, first.Id);
        Assert.Equal(6, (await service.BeginAsync(owner.Id, 6)).DurationMonths);
    }

    [Fact]
    public async Task Active_subscription_blocks_checkout_and_later_paid_overlap()
    {
        var order = await service.BeginAsync(owner.Id, 1);
        database.Subscriptions.Add(new() { UserId = owner.Id, Plan = SubscriptionPlan.Plus, Status = SubscriptionStatus.Active,
            StartsAtUtc = clock.Now, ExpiresAtUtc = clock.Now.AddMonths(1) });
        await database.SaveChangesAsync();
        Assert.Equal("subscription_active", (await Assert.ThrowsAsync<PaymentException>(() => service.BeginAsync(owner.Id, 1))).Code);
        verifier.Result = new(clock.Now.ToUnixTimeMilliseconds(), order.DeveloperPayload, 0, 0);
        Assert.Equal("subscription_active", (await Assert.ThrowsAsync<PaymentException>(() => service.VerifyAsync(owner.Id, new(order.Sku, "overlap")))).Code);
        Assert.Single(await database.Subscriptions.ToListAsync());
    }

    [Fact]
    public async Task Expired_active_row_is_retired_atomically_before_the_new_subscription()
    {
        database.Subscriptions.Add(new() { UserId = owner.Id, Plan = SubscriptionPlan.Plus, Status = SubscriptionStatus.Active,
            StartsAtUtc = clock.Now.AddMonths(-1), ExpiresAtUtc = clock.Now });
        await database.SaveChangesAsync();
        var order = await service.BeginAsync(owner.Id, 1);
        verifier.Result = new(clock.Now.ToUnixTimeMilliseconds(), order.DeveloperPayload, 0, 0);
        Assert.True((await service.VerifyAsync(owner.Id, new(order.Sku, "new-token"))).Entitlements.IsActive);
        Assert.Equal(2, await database.Subscriptions.CountAsync());
        Assert.Equal(1, await database.Subscriptions.CountAsync(item => item.Status == SubscriptionStatus.Active));
    }

    [Fact]
    public async Task Late_recovery_uses_original_purchase_time_not_a_new_free_period()
    {
        var order = await service.BeginAsync(owner.Id, 1);
        verifier.Result = new(clock.Now.ToUnixTimeMilliseconds(), order.DeveloperPayload, 0, 0);
        var expires = clock.Now.AddMonths(1);
        clock.Now = clock.Now.AddMonths(2);
        var result = await service.VerifyAsync(owner.Id, new(order.Sku, "old-token"));
        Assert.False(result.Entitlements.IsActive);
        Assert.Equal(expires, result.Subscription!.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(61)]
    public async Task Purchase_time_before_checkout_or_in_the_future_is_rejected(int minutes)
    {
        var order = await service.BeginAsync(owner.Id, 1);
        verifier.Result = new(clock.Now.AddMinutes(minutes).ToUnixTimeMilliseconds(), order.DeveloperPayload, 0, 0);
        await Assert.ThrowsAsync<PaymentException>(() => service.VerifyAsync(owner.Id, new(order.Sku, "time-token")));
        Assert.Empty(await database.Subscriptions.ToListAsync());
    }

    [Fact]
    public async Task Wrong_sku_payload_and_unsupported_duration_fail_closed()
    {
        await Assert.ThrowsAsync<PaymentException>(() => service.BeginAsync(owner.Id, 3));
        var order = await service.BeginAsync(owner.Id, 1);
        verifier.Result = new(clock.Now.ToUnixTimeMilliseconds(), order.DeveloperPayload, 0, 0);
        await Assert.ThrowsAsync<PaymentException>(() => service.VerifyAsync(owner.Id, new("Full", "token")));
        await Assert.ThrowsAsync<PaymentException>(() => service.VerifyAsync(owner.Id, new("plus6", "token")));
        verifier.Result = verifier.Result with { DeveloperPayload = "forged" };
        await Assert.ThrowsAsync<PaymentException>(() => service.VerifyAsync(owner.Id, new(order.Sku, "token")));
        Assert.Empty(await database.Subscriptions.ToListAsync());
    }

    [Fact]
    public async Task Disabled_configuration_never_exposes_checkout_or_purchasable_catalog()
    {
        var disabled = new MyketSettings();
        var catalog = new PlanCatalog(disabled);
        Assert.False(catalog.Get().PurchaseSupported);
        var unavailable = new MyketPaymentService(database, disabled, catalog, new EntitlementService(database, catalog, clock), verifier, clock);
        Assert.Equal(503, (await Assert.ThrowsAsync<PaymentException>(() => unavailable.BeginAsync(owner.Id, 1))).StatusCode);
        Assert.Throws<PaymentException>(() => unavailable.Configuration());
        Assert.DoesNotContain(settings.AccessToken, JsonSerializer.Serialize(service.Configuration()));
        var legacy = new PlanCatalog(settings).Get(false);
        Assert.False(legacy.PurchaseSupported);
        Assert.All(legacy.Plans, plan => Assert.False(plan.IsPurchasable));
    }

    [Fact]
    public async Task Payment_routes_require_authentication()
    {
        await using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/payments/myket/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/payments/myket/orders", new { durationMonths = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/payments/myket/verify", new { sku = "plus1", token = "token" })).StatusCode);
    }

    private sealed class FakeVerifier : IMyketVerifier
    {
        public VerifiedMyketPurchase Result { get; set; } = new(0, "", 0, 0);
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public Task<VerifiedMyketPurchase> VerifyAsync(string sku, string token, CancellationToken cancellationToken)
        { Calls++; return Failure is null ? Task.FromResult(Result) : Task.FromException<VerifiedMyketPurchase>(Failure); }
    }

    private sealed class PaymentClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class PaymentModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            var converter = new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            foreach (var entity in new[] { typeof(MyketOrder), typeof(Subscription) })
                foreach (var property in modelBuilder.Entity(entity).Metadata.GetProperties().Where(item => item.ClrType == typeof(DateTimeOffset)))
                { property.SetValueConverter(converter); property.SetColumnType("bigint"); }
        }
    }
}
