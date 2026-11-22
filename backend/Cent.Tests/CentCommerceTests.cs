using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cent.Module;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Cent.Tests;

public sealed class CentCommerceTests
{
    [Fact]
    public async Task Checkout_reports_paid_mock_status_and_order_history_supports_recovery()
    {
        using var factory = new CentFactory();
        using var client = factory.CreateClient();
        var auth = await Json(await client.PostAsJsonAsync("/api/cent/v1/auth/register", new { email = "commerce@example.com", password = "commerce-test-password", name = "Buyer" }));
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.GetProperty("token").GetString());
        Guid productId;
        using (var scope = factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
            var product = Subscription(); db.Products.Add(product); await db.SaveChangesAsync(); productId = product.Id;
        }
        var requestId = Guid.NewGuid();
        var result = await Json(await client.PostAsJsonAsync("/api/cent/v1/checkout", new { productId, requestId, priceRial = 1, currency = "IRT" }));
        Assert.Equal("PAID", result.GetProperty("status").GetString());
        Assert.Equal(250000, result.GetProperty("amountRial").GetInt64());
        var history = await Json(await client.GetAsync("/api/cent/v1/me/orders"));
        Assert.Equal(productId, history[0].GetProperty("productId").GetGuid());
        Assert.Equal(requestId, history[0].GetProperty("requestId").GetGuid());
        Assert.Equal(30, history[0].GetProperty("durationDays").GetInt32());
    }

    [Fact]
    public async Task Catalog_and_checkout_exclude_inactive_products_and_unpublished_trainings()
    {
        using var factory = new CentFactory(); using var client = factory.CreateClient();
        var auth = await Json(await client.PostAsJsonAsync("/api/cent/v1/auth/register", new { email = "availability@example.com", password = "commerce-test-password", name = "Buyer" }));
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.GetProperty("token").GetString());
        var hidden = new CentTraining { Title = "Hidden", Published = false };
        var inactive = Subscription(); inactive.Active = false;
        var trainingProduct = new CentProduct { Title = "Hidden product", TrainingId = hidden.Id, Active = true, PriceRial = 250000 };
        using (var scope = factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>(); db.AddRange(hidden, inactive, trainingProduct); await db.SaveChangesAsync();
        }
        var catalog = await Json(await client.GetAsync("/api/cent/v1/catalog"));
        Assert.Empty(catalog.GetProperty("products").EnumerateArray());
        foreach (var id in new[] { inactive.Id, trainingProduct.Id })
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/cent/v1/checkout", new { productId = id, requestId = Guid.NewGuid() })).StatusCode);
        Assert.Empty((await Json(await client.GetAsync("/api/cent/v1/me/orders"))).EnumerateArray());
    }

    [Fact]
    public async Task Spoofed_callback_cannot_grant_access_and_owner_can_recover_after_verification_failure()
    {
        using var factory = new CentFactory();
        var handler = new GatewayHandler(_ => "{\"data\":[],\"errors\":{\"code\":-51}}");
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddTransient(_ => Gateway(handler))));
        using var client = configured.CreateClient(new() { AllowAutoRedirect = false });
        var auth = await Json(await client.PostAsJsonAsync("/api/cent/v1/auth/register", new { email = "callback@example.com", password = "commerce-test-password", name = "Buyer" }));
        var userId = auth.GetProperty("user").GetProperty("id").GetGuid();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.GetProperty("token").GetString());
        var order = new CentOrder { UserId = userId, Title = "Monthly", Kind = "SUBSCRIPTION", DurationDays = 30, AmountRial = 250000, Authority = Authority, Provider = "SANDBOX", RequestId = Guid.NewGuid() };
        using (var scope = configured.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>(); var product = Subscription();
            order.ProductId = product.Id; db.AddRange(product, order); await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/api/cent/v1/payments/callback?Authority={Authority}&Status=NOK")).StatusCode);
        Assert.Empty(handler.Bodies);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/api/cent/v1/payments/callback?Authority={Authority}&Status=OK&amount=1&ref_id=123")).StatusCode);
        Assert.False((await Json(await client.PostAsJsonAsync($"/api/cent/v1/me/orders/{order.Id}/verify", new { }))).GetProperty("paid").GetBoolean());
        using (var scope = configured.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
            Assert.Empty(await db.Entitlements.ToListAsync()); Assert.Equal("PENDING", (await db.Orders.SingleAsync()).Status);
            Assert.All(await db.PaymentEvents.ToListAsync(), e => Assert.Equal(-51, e.Code));
        }
        handler.Payload = _ => "{\"data\":{\"code\":101,\"ref_id\":123456}}";
        Assert.True((await Json(await client.PostAsJsonAsync($"/api/cent/v1/me/orders/{order.Id}/verify", new { }))).GetProperty("paid").GetBoolean());
        await client.GetAsync($"/api/cent/v1/payments/callback?Authority={Authority}&Status=OK");
        Assert.Equal(3, handler.Bodies.Count); // two failures, then one successful recovery; paid replay does not call the provider.
        using (var scope = configured.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
            Assert.Single(await db.Entitlements.ToListAsync()); Assert.Equal("PAID", (await db.Orders.SingleAsync()).Status);
        }
    }

    [Fact]
    public async Task Interrupted_initiation_reuses_original_snapshot_and_rejects_unavailable_products()
    {
        using var store = new Store(); await store.Seed();
        using var db = store.Open();
        var handler = new GatewayHandler(_ => throw new HttpRequestException("Synthetic outage"));
        var payments = new CentPayments(db, Gateway(handler)); var requestId = Guid.NewGuid();
        await Assert.ThrowsAsync<HttpRequestException>(() => payments.Checkout(store.User.Id, store.Product.Id, requestId, "test@example.com", default));
        var order = await db.Orders.SingleAsync(); Assert.Null(order.Authority); Assert.Equal("PENDING", order.Status);
        var product = await db.Products.SingleAsync(); product.PriceRial = 990000; product.DurationDays = 7; product.Active = false; await db.SaveChangesAsync();
        handler.Payload = _ => RequestSuccess;
        var unavailable = await Assert.ThrowsAsync<CentException>(() => payments.Checkout(store.User.Id, product.Id, requestId, "test@example.com", default));
        Assert.Equal(404, unavailable.Status); Assert.Single(handler.Bodies);
        product.Active = true; await db.SaveChangesAsync();
        await payments.Checkout(store.User.Id, product.Id, requestId, "test@example.com", default);
        Assert.Equal(250000, JsonDocument.Parse(handler.Bodies.Last()).RootElement.GetProperty("amount").GetInt64());
        Assert.Equal(30, (await db.Orders.SingleAsync()).DurationDays);
        var conflict = await Assert.ThrowsAsync<CentException>(() => payments.Checkout(store.User.Id, Guid.NewGuid(), requestId, "test@example.com", default));
        Assert.Equal(409, conflict.Status);
    }

    [Fact]
    public async Task Concurrent_checkout_retries_create_one_authority_and_one_request_event()
    {
        using var store = new Store(); await store.Seed();
        var requestId = Guid.NewGuid();
        using (var db = store.Open()) {
            db.Orders.Add(new CentOrder { UserId = store.User.Id, ProductId = store.Product.Id, RequestId = requestId,
                Title = "Monthly", Kind = "SUBSCRIPTION", DurationDays = 30, AmountRial = 250000, Provider = "SANDBOX" });
            await db.SaveChangesAsync();
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = new OrdersReadSignal();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatewayHandler(_ => RequestSuccess) { BeforeResponse = async () => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); } };
        var first = Task.Run(async () => { using var db = store.Open(); return await new CentPayments(db, Gateway(handler)).Checkout(store.User.Id, store.Product.Id, requestId, "test@example.com", default); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Task.Run(async () => { using var db = store.Open(secondRead); return await new CentPayments(db, Gateway(handler)).Checkout(store.User.Id, store.Product.Id, requestId, "test@example.com", default); });
        try { await secondRead.Read.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(first, second);
        Assert.Equal(JsonSerializer.Serialize(results[0]), JsonSerializer.Serialize(results[1]));
        Assert.Single(handler.Bodies);
        using var verify = store.Open(); Assert.Single(await verify.Orders.ToListAsync()); Assert.Single(await verify.PaymentEvents.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_verification_extends_subscription_once()
    {
        using var store = new Store(); await store.Seed(); var order = await store.PendingOrder();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var handler = new GatewayHandler(_ => "{\"data\":{\"code\":100,\"ref_id\":123456}}") {
            BeforeResponse = async () => { if (Interlocked.Increment(ref calls) == 2) bothEntered.TrySetResult(); await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        };
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () => {
            using var db = store.Open(); Assert.True(await new CentPayments(db, Gateway(handler)).Confirm(order.Id, default));
        })));
        using var verify = store.Open(); var grant = await verify.Entitlements.SingleAsync();
        Assert.Equal(TimeSpan.FromDays(30), grant.ExpiresAt - grant.StartsAt);
        Assert.Equal("PAID", (await verify.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task Entitlement_failure_rolls_back_paid_state_and_verified_retry_recovers()
    {
        using var store = new Store(); await store.Seed(); var order = await store.PendingOrder();
        var handler = new GatewayHandler(_ => "{\"data\":{\"code\":101,\"ref_id\":123456}}");
        using (var db = store.Open(new RejectGrant())) {
            await Assert.ThrowsAsync<DbUpdateException>(() => new CentPayments(db, Gateway(handler)).Confirm(order.Id, default));
        }
        using (var db = store.Open()) {
            Assert.Equal("PENDING", (await db.Orders.SingleAsync()).Status); Assert.Empty(await db.Entitlements.ToListAsync()); Assert.Empty(await db.PaymentEvents.ToListAsync());
            Assert.True(await new CentPayments(db, Gateway(handler)).Confirm(order.Id, default));
            Assert.Single(await db.Entitlements.ToListAsync());
        }
    }

    [Fact]
    public async Task Reference_reuse_by_another_order_cannot_create_a_second_grant()
    {
        using var store = new Store(); await store.Seed(); var first = await store.PendingOrder(); var second = await store.PendingOrder();
        var handler = new GatewayHandler(_ => "{\"data\":{\"code\":100,\"ref_id\":123456}}");
        using (var db = store.Open()) Assert.True(await new CentPayments(db, Gateway(handler)).Confirm(first.Id, default));
        using (var db = store.Open()) {
            var conflict = await Assert.ThrowsAsync<CentException>(() => new CentPayments(db, Gateway(handler)).Confirm(second.Id, default));
            Assert.Equal(409, conflict.Status);
            Assert.Equal("payment_reference_reused", conflict.Code);
        }
        using var verify = store.Open();
        Assert.Single(await verify.Entitlements.ToListAsync()); Assert.Equal("PENDING", (await verify.Orders.SingleAsync(o => o.Id == second.Id)).Status);
    }

    [Fact]
    public async Task Permanent_purchase_survives_expired_subscription_and_revocation_removes_only_its_access()
    {
        using var store = new Store(); await store.Seed();
        using var db = store.Open(); var training = new CentTraining { Published = true, IncludedInSubscription = false };
        db.Trainings.Add(training); var order = await store.PendingOrder();
        var grant = new CentEntitlement { OrderId = order.Id, UserId = store.User.Id, Kind = "TRAINING", TrainingId = training.Id, StartsAt = DateTimeOffset.UtcNow.AddDays(-60) };
        var subscriptionOrder = await store.PendingOrder();
        db.AddRange(grant, new CentEntitlement { OrderId = subscriptionOrder.Id, UserId = store.User.Id, Kind = "SUBSCRIPTION", StartsAt = DateTimeOffset.UtcNow.AddDays(-31), ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync(); var access = new CentAccess(db);
        Assert.False(await access.HasSubscription(store.User.Id, default)); Assert.True(await access.CanRead(store.User.Id, training.Id, default));
        grant.RevokedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); Assert.False(await access.CanRead(store.User.Id, training.Id, default));
        grant.RevokedAt = null; grant.StartsAt = DateTimeOffset.UtcNow.AddDays(1); await db.SaveChangesAsync(); Assert.False(await access.CanRead(store.User.Id, training.Id, default));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"data\":{\"code\":\"100\"}}")]
    [InlineData("{\"data\":{\"code\":100,\"ref_id\":{}}}")]
    [InlineData("{\"data\":{\"code\":100,\"ref_id\":0}}")]
    [InlineData("{\"data\":{\"code\":101}}")]
    public async Task Invalid_verification_response_never_marks_order_paid(string payload)
    {
        using var store = new Store(); await store.Seed(); var order = await store.PendingOrder(); using var db = store.Open();
        var error = await Assert.ThrowsAsync<CentException>(() => new CentPayments(db, Gateway(new GatewayHandler(_ => payload))).Confirm(order.Id, default));
        Assert.Equal("gateway_invalid_response", error.Code); Assert.Equal(502, error.Status);
        Assert.Equal("PENDING", (await db.Orders.SingleAsync()).Status); Assert.Empty(await db.Entitlements.ToListAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"data\":{\"code\":\"100\"}}")]
    [InlineData("{\"data\":{\"code\":100,\"authority\":42}}")]
    public async Task Invalid_initiation_response_has_actionable_error(string payload)
    {
        var error = await Assert.ThrowsAsync<CentException>(() => Gateway(new GatewayHandler(_ => payload)).Request(new CentOrder { Provider = "SANDBOX" }, "test@example.com", default));
        Assert.Equal("gateway_invalid_response", error.Code);
    }

    [Fact]
    public async Task Stored_real_provider_is_used_even_when_development_mock_is_enabled()
    {
        var handler = new GatewayHandler(_ => RequestSuccess);
        var gateway = Gateway(handler, mock: true); Assert.True(gateway.IsMock);
        await gateway.Request(new CentOrder { Provider = "SANDBOX", AmountRial = 250000 }, "test@example.com", default);
        Assert.Single(handler.Bodies); Assert.Equal("sandbox.zarinpal.com", handler.Hosts.Single());
        var error = await Assert.ThrowsAsync<CentException>(() => Gateway(handler).Verify(new CentOrder { Provider = "MOCK" }, default));
        Assert.Equal("mock_payment_disabled", error.Code);
    }

    private const string Authority = "A000000000000000000000000000000000001";
    private const string RequestSuccess = "{\"data\":{\"code\":100,\"authority\":\"" + Authority + "\"}}";
    private static CentProduct Subscription() => new() { Title = "Monthly", Kind = "SUBSCRIPTION", DurationDays = 30, PriceRial = 250000, Active = true };
    private static CentGateway Gateway(GatewayHandler handler, bool mock = false) => new(new HttpClient(handler), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["Cent:Payments:MerchantId"] = "11111111-1111-1111-1111-111111111111", ["Cent:Payments:AllowDevelopmentMock"] = mock.ToString(),
        ["Cent:ClientBaseUrl"] = "https://cent.example", ["Cent:PublicApiBaseUrl"] = "https://api.example"
    }).Build(), new TestEnvironment());
    private static async Task<JsonElement> Json(HttpResponseMessage response) {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private sealed class TestEnvironment : IHostEnvironment {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Cent.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class GatewayHandler(Func<int, string> payload) : HttpMessageHandler {
        public Func<int, string> Payload { get; set; } = payload;
        public Func<Task>? BeforeResponse { get; init; }
        public ConcurrentQueue<string> Bodies { get; } = new();
        public ConcurrentQueue<string> Hosts { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Bodies.Enqueue(await request.Content!.ReadAsStringAsync(ct)); Hosts.Enqueue(request.RequestUri!.Host);
            if (BeforeResponse is not null) await BeforeResponse();
            return new(HttpStatusCode.OK) { Content = new StringContent(Payload(Bodies.Count), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Store : IDisposable {
        private readonly string path = Path.Combine(Path.GetTempPath(), "cent-commerce-" + Guid.NewGuid().ToString("N") + ".db");
        public CentUser User { get; } = new() { Email = "test@example.com", NormalizedEmail = "TEST@EXAMPLE.COM" };
        public CentProduct Product { get; } = Subscription();
        public SqliteCentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<CentDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 10 }.ToString()).AddInterceptors(interceptors).Options);
        public async Task Seed() { using var db = Open(); await db.Database.EnsureCreatedAsync(); db.AddRange(User, Product); await db.SaveChangesAsync(); }
        public async Task<CentOrder> PendingOrder() {
            using var db = Open(); var order = new CentOrder { UserId = User.Id, ProductId = Product.Id, RequestId = Guid.NewGuid(), Title = Product.Title,
                Kind = Product.Kind, DurationDays = Product.DurationDays, AmountRial = Product.PriceRial, Authority = Guid.NewGuid().ToString("N"), Provider = "SANDBOX" };
            db.Orders.Add(order); await db.SaveChangesAsync(); return order;
        }
        public void Dispose() { File.Delete(path); }
    }
    private sealed class OrdersReadSignal : DbCommandInterceptor {
        public TaskCompletionSource Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default) {
            if (command.CommandText.Contains("cent_orders")) Read.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
    private sealed class RejectGrant : SaveChangesInterceptor {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) {
            if (eventData.Context!.ChangeTracker.Entries<CentEntitlement>().Any(e => e.State == EntityState.Added)) throw new DbUpdateException("Synthetic entitlement persistence failure");
            return ValueTask.FromResult(result);
        }
    }
}
