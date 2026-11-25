using System.Text.Json;
using Cent.Module;
using Cent.Module.Curriculum;
using Cent.Module.Exams;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Cent.Exam.Tests;

public sealed class SubjectCommerceTests
{
    [Fact]
    public void Commerce_migration_is_additive_and_confined_to_Cent_objects()
    {
        var operations = new Cent.Module.Migrations.SubjectCommerce().UpOperations;
        var tables = operations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.CreateTableOperation>().ToArray();
        Assert.Equal(6, tables.Length);
        Assert.All(tables, t => {
            Assert.StartsWith("cent_", t.Name);
            Assert.All(t.ForeignKeys, f => { Assert.StartsWith("cent_", f.PrincipalTable); Assert.Equal(Microsoft.EntityFrameworkCore.Migrations.ReferentialAction.NoAction, f.OnDelete); });
        });
        Assert.DoesNotContain(operations, op => op is Microsoft.EntityFrameworkCore.Migrations.Operations.DropTableOperation or
            Microsoft.EntityFrameworkCore.Migrations.Operations.DeleteDataOperation or Microsoft.EntityFrameworkCore.Migrations.Operations.UpdateDataOperation);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public SqliteConnection Connection = new("Data Source=:memory:");
        public ReleaseTestContext Db = null!;
        public CentSubjectCommerce Commerce = null!;
        public CentPayments Payments = null!;
        public CentUser Owner = new() { Email = "buyer@example.invalid", NormalizedEmail = "BUYER@EXAMPLE.INVALID" };
        public static async Task<Fixture> Create(CentGateway? testGateway = null)
        {
            var f = new Fixture(); await f.Connection.OpenAsync();
            f.Db = new(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(f.Connection).Options);
            await f.Db.Database.EnsureCreatedAsync(); await new CentCurriculumImporter(f.Db).Import(includeReviewed: false);
            f.Db.Users.Add(f.Owner); await f.Db.SaveChangesAsync(); await CentSubjectCatalog.PrepareOffers(f.Db);
            // A disposable test-only approval exercises payment mechanics. It is
            // never written to a normal catalog or used as academic evidence.
            foreach (var o in await f.Db.Set<CentSubjectOffer>().ToListAsync()) { o.Available = true; o.ReviewedBy = "isolated test fixture"; o.ReviewedAt = DateTimeOffset.UtcNow; }
            foreach (var p in await f.Db.Products.ToListAsync()) p.Active = true;
            await f.Db.SaveChangesAsync();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["Cent:Payments:AllowDevelopmentMock"] = "true", ["Cent:ClientBaseUrl"] = "http://localhost:5173/cent-s", ["Cent:PublicApiBaseUrl"] = "http://localhost:5108"
            }).Build();
            var gateway = testGateway ?? new CentGateway(new HttpClient(), config, new Development());
            f.Commerce = new(f.Db, gateway); f.Payments = new(f.Db, gateway); return f;
        }
        public async Task<CentOrder> Order(string[] subjects, bool renewal = false)
        {
            var q = await Commerce.Quote(Owner.Id, subjects, default);
            var response = AsJson(await Commerce.Checkout(Owner.Id, q.Id, Guid.NewGuid(), renewal, Owner.Email, default));
            return await Db.Orders.AsNoTracking().SingleAsync(o => o.Id == response.GetProperty("id").GetGuid());
        }
        public async Task Pay(CentOrder o)
        {
            await Db.Set<CentSubjectOrder>().Where(s => s.OrderId == o.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DevelopmentOutcome, "success"));
            Assert.True(await Payments.Confirm(o.Id, default));
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }
    private sealed class Development : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Cent.Exam.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private static JsonElement AsJson(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class Provider : HttpMessageHandler
    {
        public bool Success;
        public List<long> Amounts = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            Amounts.Add(body.GetProperty("amount").GetInt64());
            var json = request.RequestUri!.AbsolutePath.Contains("request")
                ? JsonSerializer.Serialize(new { data = new { code = 100, authority = "A" + body.GetProperty("metadata").GetProperty("order_id").GetGuid().ToString("N") } })
                : Success ? "{\"data\":{\"code\":101,\"ref_id\":123456}}" : "{\"errors\":{\"code\":-51}}";
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }
    [Fact]
    public async Task Provider_verification_uses_frozen_amount_and_reference_replay_cannot_grant_another_order()
    {
        var handler = new Provider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Cent:Payments:MerchantId"] = Guid.NewGuid().ToString(), ["Cent:Payments:Sandbox"] = "true",
            ["Cent:ClientBaseUrl"] = "http://localhost:5173/cent-s", ["Cent:PublicApiBaseUrl"] = "http://localhost:5108"
        }).Build();
        await using var f = await Fixture.Create(new CentGateway(new HttpClient(handler), config, new Development()));
        var first = await f.Order(["math", "reading"]);
        Assert.False(await f.Payments.Confirm(first.Id, default));
        Assert.Empty(await f.Db.Set<CentSubjectAccess>().ToListAsync());
        Assert.Equal("FAILED", (await f.Db.Orders.AsNoTracking().SingleAsync()).Status);
        handler.Success = true; Assert.True(await f.Payments.Confirm(first.Id, default));
        Assert.All(handler.Amounts, a => Assert.Equal(9_310_000, a));
        var second = await f.Order(["biology"]);
        Assert.Equal("payment_reference_reused", (await Assert.ThrowsAsync<CentException>(() => f.Payments.Confirm(second.Id, default))).Code);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(2, await f.Db.Set<CentSubjectAccess>().CountAsync()); Assert.Single(await f.Db.Entitlements.ToListAsync());
        Assert.Equal("PENDING", (await f.Db.Orders.AsNoTracking().SingleAsync(o => o.Id == second.Id)).Status);
    }
    [Fact]
    public async Task Paid_subject_sources_exclude_unreviewed_legacy_lessons_and_keep_revocation_isolated()
    {
        await using var f = await Fixture.Create(); var order = await f.Order(["math", "reading"]); await f.Pay(order);
        var access = new CentAccess(f.Db); var training = CurriculumIdentity.Id("training:math");
        var documents = await f.Db.Documents.Where(d => d.TrainingId == training).ToListAsync();
        var allowed = await access.ReviewedSources(f.Owner.Id, training, default); Assert.Equal(2, allowed.Count);
        foreach (var d in documents) {
            if (d.SourceId is { } source && allowed.Contains(source)) Assert.Equal(d.Id, (await access.RequireDocument(f.Owner.Id, d.Id, default)).Id);
            else Assert.Equal("content_review_pending", (await Assert.ThrowsAsync<CentException>(() => access.RequireDocument(f.Owner.Id, d.Id, default))).Code);
        }
        Assert.Empty(await access.ReviewedSources(Guid.NewGuid(), training, default));
    }

    [Theory]
    [InlineData(1, 0, 4_900_000)] [InlineData(2, 5, 9_310_000)] [InlineData(3, 10, 13_230_000)]
    [InlineData(4, 15, 16_660_000)] [InlineData(5, 20, 19_600_000)] [InlineData(6, 25, 22_050_000)]
    public void Exact_integer_tiers(int count, int percent, long total)
    {
        var p = CentSubjectPricing.Calculate(count); Assert.Equal(percent, p.Percent); Assert.Equal(total, p.Payable);
        Assert.Equal(p.Subtotal, p.Discount + p.Payable);
    }
    [Fact]
    public async Task Catalog_counts_database_rows_and_keeps_unapproved_offers_closed()
    {
        await using var f = await Fixture.Create();
        var local = new CentTraining { Title = "Local notes verification", Published = true };
        f.Db.Trainings.Add(local); await f.Db.SaveChangesAsync();
        var catalog = await CentSubjectCatalog.Read(f.Db, default);
        Assert.Equal(6, catalog.Length); Assert.DoesNotContain(catalog, s => s.TrainingId == local.Id);
        var corpus = CentCurriculumImporter.Bundled();
        foreach (var s in catalog) {
            Assert.True(s.Available); Assert.Equal(4_900_000, s.PriceRial);
            Assert.Equal(corpus.Sections.Count(l => corpus.Sources.Any(src => src.Key == l.SourceKey && src.Course == s.SubjectId)), s.Published.Lessons);
            Assert.Equal(corpus.Questions.Count(q => q.Course == s.SubjectId), s.Published.Questions);
            Assert.True(s.Reviewed.Lessons > 0); Assert.True(s.Reviewed.Lessons < s.Published.Lessons || s.SubjectId == "reading");
        }
        var math = await f.Db.Set<CentSubjectOffer>().FindAsync("math"); math!.ReviewedAt = null; await f.Db.SaveChangesAsync();
        Assert.False((await CentSubjectCatalog.Read(f.Db, default)).Single(s => s.SubjectId == "math").Available);
        Assert.Equal("subject_unavailable", (await Assert.ThrowsAsync<CentException>(() => f.Commerce.Quote(f.Owner.Id, ["math"], default))).Code);
    }
    [Fact]
    public async Task Quote_validates_selection_expiry_and_owner_and_recovers_consumed_quote()
    {
        await using var f = await Fixture.Create();
        Assert.Equal("duplicate_subject", (await Assert.ThrowsAsync<CentException>(() => f.Commerce.Quote(f.Owner.Id, ["math", "math"], default))).Code);
        Assert.Equal("unknown_subject", (await Assert.ThrowsAsync<CentException>(() => f.Commerce.Quote(f.Owner.Id, ["invalid"], default))).Code);
        var q = await f.Commerce.Quote(f.Owner.Id, ["math", "physics"], default);
        Assert.Equal(9_310_000, q.PayableRial); Assert.Equal(q.PayableRial, q.Lines.Sum(l => l.PayableRial));
        Assert.Equal(404, (await Assert.ThrowsAsync<CentException>(() => f.Commerce.Checkout(Guid.NewGuid(), q.Id, Guid.NewGuid(), false, "x", default))).Status);
        var request = Guid.NewGuid(); var first = AsJson(await f.Commerce.Checkout(f.Owner.Id, q.Id, request, false, f.Owner.Email, default));
        var row = await f.Db.Set<CentSubjectQuote>().FindAsync(q.Id); row!.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); await f.Db.SaveChangesAsync();
        var again = AsJson(await f.Commerce.Checkout(f.Owner.Id, q.Id, request, false, f.Owner.Email, default));
        Assert.Equal(first.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid()); Assert.Single(await f.Db.Orders.ToListAsync());
        var next = await f.Commerce.Quote(f.Owner.Id, ["reading"], default);
        Assert.Equal("request_conflict", (await Assert.ThrowsAsync<CentException>(() => f.Commerce.Checkout(f.Owner.Id, next.Id, request, false, f.Owner.Email, default))).Code);
        var nextRow = await f.Db.Set<CentSubjectQuote>().FindAsync(next.Id); nextRow!.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); await f.Db.SaveChangesAsync();
        Assert.Equal("quote_expired", (await Assert.ThrowsAsync<CentException>(() => f.Commerce.Checkout(f.Owner.Id, next.Id, Guid.NewGuid(), false, f.Owner.Email, default))).Code);
    }
    [Fact]
    public async Task Verification_is_required_and_bundle_settlement_is_idempotent_and_subject_scoped()
    {
        await using var f = await Fixture.Create(); var o = await f.Order(["reading", "math"]);
        Assert.False(await f.Payments.Confirm(o.Id, default)); Assert.Empty(await f.Db.Entitlements.ToListAsync());
        await f.Pay(o); await f.Payments.Confirm(o.Id, default);
        Assert.Single(await f.Db.Entitlements.ToListAsync()); Assert.Equal(2, await f.Db.Set<CentSubjectAccess>().CountAsync());
        var access = new CentAccess(f.Db);
        Assert.True(await access.HasSubject(f.Owner.Id, "reading", default)); Assert.False(await access.HasSubject(f.Owner.Id, "biology", default));
        Assert.False(await access.HasSubject(Guid.NewGuid(), "reading", default));
        var grant = await f.Db.Set<CentSubjectAccess>().SingleAsync(a => a.SubjectId == "reading");
        Assert.Equal(grant.StartsAt.AddMonths(12), grant.ExpiresAt);
        grant.RevokedAt = DateTimeOffset.UtcNow; await f.Db.SaveChangesAsync();
        Assert.False(await access.HasSubject(f.Owner.Id, "reading", default)); Assert.True(await access.HasSubject(f.Owner.Id, "math", default));
        var parent = await f.Db.Entitlements.SingleAsync(); parent.RevokedAt = DateTimeOffset.UtcNow; await f.Db.SaveChangesAsync();
        Assert.False(await access.HasSubject(f.Owner.Id, "math", default));
    }
    [Fact]
    public async Task Renewal_queues_calendar_months_without_changing_old_grants_and_expiry_is_exclusive()
    {
        await using var f = await Fixture.Create(); var order = await f.Order(["math"]); await f.Pay(order);
        var original = await f.Db.Set<CentSubjectAccess>().SingleAsync(); var originalEnd = original.ExpiresAt;
        var q = await f.Commerce.Quote(f.Owner.Id, ["math", "biology"], default);
        Assert.Equal(originalEnd, q.Lines.Single(l => l.SubjectId == "math").CurrentAccessEnd);
        Assert.Equal("renewal_confirmation_required", (await Assert.ThrowsAsync<CentException>(() => f.Commerce.Checkout(f.Owner.Id, q.Id, Guid.NewGuid(), false, f.Owner.Email, default))).Code);
        var result = AsJson(await f.Commerce.Checkout(f.Owner.Id, q.Id, Guid.NewGuid(), true, f.Owner.Email, default));
        var next = await f.Db.Orders.AsNoTracking().SingleAsync(o => o.Id == result.GetProperty("id").GetGuid()); await f.Pay(next);
        var rights = await f.Db.Set<CentSubjectAccess>().AsNoTracking().Where(a => a.SubjectId == "math").ToListAsync();
        Assert.Contains(rights, r => r.Id == original.Id && r.ExpiresAt == originalEnd);
        Assert.Contains(rights, r => r.Id != original.Id && r.StartsAt == originalEnd && r.ExpiresAt == originalEnd.AddMonths(12));
        Assert.Equal(new DateTimeOffset(2029, 2, 28, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2028, 2, 29, 12, 0, 0, TimeSpan.Zero).AddMonths(12));
        await f.Db.Set<CentSubjectAccess>().Where(a => a.SubjectId == "biology").ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresAt, DateTimeOffset.UtcNow));
        Assert.False(await new CentAccess(f.Db).HasSubject(f.Owner.Id, "biology", default));
    }
    [Fact]
    public async Task Pending_subject_orders_prevent_accidental_double_charge_and_uncertain_authorities_do_not_retry()
    {
        await using var f = await Fixture.Create(); var first = await f.Order(["reading"]);
        var q = await f.Commerce.Quote(f.Owner.Id, ["reading"], default);
        Assert.Equal("pending_subject_order", (await Assert.ThrowsAsync<CentException>(() => f.Commerce.Checkout(f.Owner.Id, q.Id, Guid.NewGuid(), false, f.Owner.Email, default))).Code);
        await f.Db.Orders.Where(o => o.Id == first.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Authority, (string?)null));
        await f.Db.Set<CentSubjectOrder>().Where(s => s.OrderId == first.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.InitiationState, "REQUESTING"));
        f.Db.ChangeTracker.Clear();
        Assert.Equal("initiation_uncertain", (await Assert.ThrowsAsync<CentException>(() => f.Commerce.Initiate(f.Owner.Id, first.Id, f.Owner.Email, default))).Code);
    }
    [Fact]
    public async Task Corrupt_line_rolls_back_paid_status_and_all_grants()
    {
        await using var f = await Fixture.Create(); var o = await f.Order(["reading", "math"]);
        var line = await f.Db.Set<CentSubjectOrderLine>().FirstAsync(); f.Db.Remove(line); await f.Db.SaveChangesAsync();
        await f.Db.Set<CentSubjectOrder>().Where(s => s.OrderId == o.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DevelopmentOutcome, "success"));
        Assert.Equal("order_integrity_error", (await Assert.ThrowsAsync<CentException>(() => f.Payments.Confirm(o.Id, default))).Code);
        f.Db.ChangeTracker.Clear(); Assert.Empty(await f.Db.Entitlements.ToListAsync()); Assert.Empty(await f.Db.Set<CentSubjectAccess>().ToListAsync());
        Assert.Equal("PENDING", (await f.Db.Orders.SingleAsync()).Status);
    }
    [Fact]
    public async Task New_Practice_denies_unowned_subject_and_keeps_legacy_diagnostic_available()
    {
        await using var f = await Fixture.Create(); var p = new CentPractice(f.Db, new CentAccess(f.Db));
        Assert.Equal("access_required", (await Assert.ThrowsAsync<CentException>(() => p.Start(f.Owner.Id, "practice", Guid.NewGuid(), default, "reading", null, "passage-v1"))).Code);
        Assert.NotNull(await p.Start(f.Owner.Id, "diagnostic", Guid.NewGuid(), default, "math"));
        var order = await f.Order(["reading"]); await f.Pay(order);
        Assert.NotNull(await p.Start(f.Owner.Id, "practice", Guid.NewGuid(), default, "reading", null, "passage-v1"));
    }
    [Fact]
    public async Task Mock_admission_requires_configured_all_subject_policy_and_preserves_started_attempts()
    {
        await using var f = await Fixture.Create(); var order = await f.Order(CurriculumIdentity.Courses.Select(c => c.Code).ToArray()); await f.Pay(order);
        var access = new CentAccess(f.Db);
        await Assert.ThrowsAsync<CentException>(() => new ExistingCentMockAdmission(access).AuthorizeStart(f.Owner.Id, CentExamBlueprints.Current, default));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Cent:Commerce:MockAdmissionPolicy"] = "all-subjects-v1" }).Build();
        var admission = new ExistingCentMockAdmission(access, config);
        Assert.Equal("all-subjects-at-start-v1", await admission.AuthorizeStart(f.Owner.Id, CentExamBlueprints.Current, default));
        await f.Db.Set<CentSubjectAccess>().Where(a => a.SubjectId == "reading").ExecuteUpdateAsync(s => s.SetProperty(a => a.RevokedAt, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<CentException>(() => admission.AuthorizeStart(f.Owner.Id, CentExamBlueprints.Current, default));
        await admission.AuthorizeReview(f.Owner.Id, Guid.NewGuid(), default);
    }
}
