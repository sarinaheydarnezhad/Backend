using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Backups;
using Flashcards.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flashcards.Tests;

public sealed partial class BackupTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly BackupClock _clock = new();
    private readonly BackupFailureInterceptor _failure = new();
    private readonly EphemeralDataProtectionProvider _protection = new();
    private BackupApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _connection.CreateFunction("ISJSON", (string value) =>
        {
            try { using var document = JsonDocument.Parse(value); return 1; }
            catch (JsonException) { return 0; }
        });
        await _connection.OpenAsync();
        _factory = new BackupApiFactory(_connection, _clock, _failure, _protection);
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
    public async Task Creation_preserves_owned_contents_progress_relationships_and_timestamps_without_secrets_or_live_changes()
    {
        var tokens = await LoginAsync("owner@example.com");
        Guid ownerId;
        Guid deckId;
        Guid cardId;
        DateTimeOffset savedTimestamp;
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var owner = await database.Users.SingleAsync();
            ownerId = owner.Id;
            var deck = new Deck { UserId = owner.Id, Name = "Private deck", SyncVersion = 3, ArchivedAtUtc = _clock.Now.AddDays(-1) };
            var card = new Card { Deck = deck, FrontText = "Private front", Meaning = "Meaning", Phonetic = "sound", Category = "noun", ExamplesJson = "[\"example\"]", SyncVersion = 4 };
            database.AddRange(deck, card,
                new CardReviewState { Card = card, Box = 3, DueDate = new DateOnly(2026, 11, 5), LastReviewedAtUtc = _clock.Now,
                    TotalReviews = 8, TotalSuccesses = 5, ConsecutiveSuccesses = 2, SyncVersion = 5 },
                new ReviewEvent { UserId = owner.Id, Deck = deck, Card = card, Result = "success", PreviousBox = 2, NewBox = 3,
                    ReviewedAtUtc = _clock.Now, StudySessionId = Guid.NewGuid() },
                new UserSettings { UserId = owner.Id, Theme = "dark", PreferredSpeechAccent = "uk", SyncVersion = 6 });
            var foreign = new User { DisplayName = "Foreign marker" };
            database.Add(new Deck { User = foreign, Name = "Foreign deck" });
            await database.SaveChangesAsync();
            await database.SyncHeads.Where(item => item.UserId == owner.Id).ExecuteUpdateAsync(update => update.SetProperty(item => item.Version, 6));
            deckId = deck.Id;
            cardId = card.Id;
            savedTimestamp = card.UpdatedAtUtc;
        }
        var response = await CreateAsync(Guid.NewGuid(), 6);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var metadata = (await response.Content.ReadFromJsonAsync<BackupMetadata>())!;
        Assert.Equal("Completed", metadata.Status);
        Assert.Equal(7, metadata.RetentionDays);
        Assert.Equal(_clock.Now.AddDays(7), metadata.ExpiresAtUtc);
        Assert.False(metadata.CanRestore);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var publicJson = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Private front", publicJson);
        Assert.DoesNotContain("payload", publicJson, StringComparison.OrdinalIgnoreCase);
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var backup = await stored.UserBackups.SingleAsync();
        Assert.Equal(ownerId, backup.UserId);
        Assert.Equal(backup.Payload!.LongLength, metadata.SizeBytes);
        Assert.DoesNotContain("Private front", Encoding.UTF8.GetString(backup.Payload));
        var plaintext = _protection.CreateProtector("Lerona.Backups", "1", ownerId.ToString("D"), backup.Id.ToString("D")).Unprotect(backup.Payload);
        var json = Encoding.UTF8.GetString(plaintext);
        using var snapshot = JsonDocument.Parse(json);
        var root = snapshot.RootElement;
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(ownerId, root.GetProperty("userId").GetGuid());
        Assert.Equal(6, root.GetProperty("syncVersion").GetInt64());
        var savedDeck = Assert.Single(root.GetProperty("decks").EnumerateArray());
        Assert.Equal(deckId, savedDeck.GetProperty("id").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, savedDeck.GetProperty("archivedAtUtc").ValueKind);
        var savedCard = Assert.Single(root.GetProperty("cards").EnumerateArray());
        Assert.Equal(deckId, savedCard.GetProperty("deckId").GetGuid());
        Assert.Equal(cardId, savedCard.GetProperty("id").GetGuid());
        Assert.Equal(savedTimestamp, savedCard.GetProperty("updatedAtUtc").GetDateTimeOffset());
        Assert.Equal("[\"example\"]", savedCard.GetProperty("examplesJson").GetString());
        var progress = Assert.Single(root.GetProperty("reviewStates").EnumerateArray());
        Assert.Equal(cardId, progress.GetProperty("cardId").GetGuid());
        Assert.Equal(3, progress.GetProperty("box").GetInt32());
        Assert.Equal(8, progress.GetProperty("totalReviews").GetInt32());
        Assert.Equal(5, progress.GetProperty("totalSuccesses").GetInt32());
        Assert.Equal(2, progress.GetProperty("consecutiveSuccesses").GetInt32());
        Assert.Equal("2026-11-05", progress.GetProperty("dueDate").GetString());
        Assert.Single(root.GetProperty("reviewEvents").EnumerateArray());
        Assert.Equal("dark", Assert.Single(root.GetProperty("settings").EnumerateArray()).GetProperty("theme").GetString());
        foreach (var forbidden in new[] { "Foreign marker", "Foreign deck", "passwordHash", "refreshToken", "accessToken", "codeHash", tokens.RefreshToken })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(savedTimestamp, (await stored.Cards.SingleAsync(item => item.Id == cardId)).UpdatedAtUtc);
        Assert.Equal(6, (await stored.SyncHeads.SingleAsync(item => item.UserId == ownerId)).Version);
        Assert.Empty(await stored.SyncChanges.ToListAsync());
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => _protection.CreateProtector("Lerona.Backups", "1",
            Guid.NewGuid().ToString("D"), backup.Id.ToString("D")).Unprotect(backup.Payload));
        var tampered = backup.Payload.ToArray();
        tampered[^1] ^= 1;
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => _protection.CreateProtector("Lerona.Backups", "1",
            ownerId.ToString("D"), backup.Id.ToString("D")).Unprotect(tampered));
        Assert.Equal(metadata, await _client.GetFromJsonAsync<BackupMetadata>($"/api/v1/backups/{metadata.Id}"));
        Assert.Equal(metadata.Id, (await _client.GetFromJsonAsync<BackupStatus>("/api/v1/backups/status"))!.LatestCompleted!.Id);
    }

    [Fact]
    public async Task All_routes_require_authentication_and_foreign_backups_are_not_visible()
    {
        await LoginAsync("first@example.com");
        var metadata = (await (await CreateAsync(Guid.NewGuid())).Content.ReadFromJsonAsync<BackupMetadata>())!;
        await LoginAsync("second@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/backups/{metadata.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/backups/{metadata.Id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RestoreAsync(metadata.Id)).StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<BackupPage>("/api/v1/backups/"))!.Items);
        Assert.Null((await _client.GetFromJsonAsync<BackupStatus>("/api/v1/backups/status"))!.LatestCompleted);
        using var anonymous = _factory.CreateClient();
        foreach (var path in new[] { "/api/v1/backups/", "/api/v1/backups/status", $"/api/v1/backups/{metadata.Id}", $"/api/v1/backups/{metadata.Id}/download" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/backups/", Request(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/v1/backups/{metadata.Id}/restore", new RestoreBackupRequest("mergeMissing", 0))).StatusCode);
    }

    [Fact]
    public async Task Client_owner_fields_are_ignored_and_request_ids_are_scoped_to_the_authenticated_owner()
    {
        await LoginAsync("one@example.com");
        var requestId = Guid.NewGuid();
        var first = (await (await CreateAsync(requestId)).Content.ReadFromJsonAsync<BackupMetadata>())!;
        await LoginAsync("two@example.com");
        var response = await _client.PostAsJsonAsync("/api/v1/backups/", new { requestId, formatVersion = 1, expectedSyncVersion = 0, userId = Guid.Empty });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var second = (await response.Content.ReadFromJsonAsync<BackupMetadata>())!;
        Assert.NotEqual(first.Id, second.Id);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(2, await database.UserBackups.Select(item => item.UserId).Distinct().CountAsync());
        Assert.False(await database.UserBackups.AnyAsync(item => item.UserId == Guid.Empty));
    }

    [Fact]
    public async Task Lost_response_retry_returns_the_original_artifact_and_changed_request_parameters_conflict()
    {
        await LoginAsync("one@example.com");
        var requestId = Guid.NewGuid();
        var first = (await (await CreateAsync(requestId)).Content.ReadFromJsonAsync<BackupMetadata>())!;
        _clock.Now = _clock.Now.AddHours(1);
        var retry = await CreateAsync(requestId);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(first, await retry.Content.ReadFromJsonAsync<BackupMetadata>());
        Assert.Equal(HttpStatusCode.Conflict, (await CreateAsync(requestId, 1)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().UserBackups.CountAsync());
    }

    [Fact]
    public async Task Expiration_is_utc_excludes_latest_completed_and_cannot_be_retried_as_a_new_artifact()
    {
        await LoginAsync("one@example.com");
        var requestId = Guid.NewGuid();
        var first = (await (await CreateAsync(requestId)).Content.ReadFromJsonAsync<BackupMetadata>())!;
        _clock.Now = first.ExpiresAtUtc;
        var expired = (await _client.GetFromJsonAsync<BackupMetadata>($"/api/v1/backups/{first.Id}"))!;
        Assert.Equal("Expired", expired.Status);
        Assert.True(expired.IsExpired);
        Assert.False(expired.CanRestore);
        Assert.Null((await _client.GetFromJsonAsync<BackupStatus>("/api/v1/backups/status"))!.LatestCompleted);
        Assert.Equal(HttpStatusCode.Gone, (await CreateAsync(requestId)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.NotNull((await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().UserBackups.SingleAsync()).Payload);
    }

    [Fact]
    public async Task Completion_storage_failure_rolls_back_payload_records_safe_failure_and_retries_once()
    {
        await LoginAsync("one@example.com");
        _failure.FailCompletion = true;
        var requestId = Guid.NewGuid();
        var response = await CreateAsync(requestId);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("sensitive database marker", await response.Content.ReadAsStringAsync());
        Guid backupId;
        using (var scope = _factory.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().UserBackups.SingleAsync();
            backupId = row.Id;
            Assert.Equal("Failed", row.Status);
            Assert.Equal("backup_storage_failed", row.FailureCode);
            Assert.Null(row.Payload);
            Assert.Null(row.CompletedAtUtc);
        }
        _failure.FailCompletion = false;
        var retry = (await (await CreateAsync(requestId)).Content.ReadFromJsonAsync<BackupMetadata>())!;
        Assert.Equal(backupId, retry.Id);
        Assert.Equal("Completed", retry.Status);
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(1, await stored.UserBackups.CountAsync());
        await Assert.ThrowsAsync<SqliteException>(() => stored.UserBackups.ExecuteUpdateAsync(update => update.SetProperty(item => item.SizeBytes, (long?)null)));
        Assert.NotNull((await stored.UserBackups.AsNoTracking().SingleAsync()).SizeBytes);
    }

    [Fact]
    public async Task Interrupted_creation_never_commits_completed_or_partial_payload_and_can_be_retried()
    {
        await LoginAsync("one@example.com");
        using var cancellation = new CancellationTokenSource();
        _failure.OnPendingSaved = cancellation.Cancel;
        var request = Request(Guid.NewGuid());
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var userId = (await database.Users.SingleAsync()).Id;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<BackupService>()
                .CreateAsync(userId, request, cancellation.Token));
        }
        _failure.OnPendingSaved = null;
        using (var scope = _factory.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().UserBackups.SingleAsync();
            Assert.Equal("Failed", row.Status);
            Assert.Equal("backup_interrupted", row.FailureCode);
            Assert.Null(row.Payload);
        }
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/v1/backups/", request)).StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Unsupported_format_is_rejected_without_writing_a_backup(int formatVersion)
    {
        await LoginAsync("one@example.com");
        var response = await _client.PostAsJsonAsync("/api/v1/backups/", new CreateBackupRequest(Guid.NewGuid(), formatVersion, 0));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().UserBackups.ToListAsync());
    }

    [Fact]
    public async Task Size_limit_never_saves_a_truncated_completed_snapshot()
    {
        await LoginAsync("one@example.com");
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var user = await database.Users.SingleAsync();
            var deck = new Deck { UserId = user.Id, Name = "Keep live data" };
            database.Add(new Card { Deck = deck, FrontText = new string('x', 4000), Meaning = new string('y', 4000) });
            await database.SaveChangesAsync();
            var limited = new BackupService(database, new BackupSettings(7, 1024, 20, "C:\\keys"),
                new BackupPayloadProtector(_protection), _clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupService>.Instance);
            var error = await Assert.ThrowsAsync<BackupException>(() => limited.CreateAsync(user.Id, Request(Guid.NewGuid())));
            Assert.Equal(413, error.StatusCode);
        }
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var backup = await stored.UserBackups.SingleAsync();
        Assert.Equal("Failed", backup.Status);
        Assert.Equal("backup_too_large", backup.FailureCode);
        Assert.Null(backup.Payload);
        Assert.Equal("Keep live data", (await stored.Decks.SingleAsync()).Name);
        Assert.Equal(4000, (await stored.Cards.SingleAsync()).FrontText.Length);
    }

    [Fact]
    public async Task Missing_durable_keys_fails_closed_and_ahead_of_server_sync_is_not_completed()
    {
        await LoginAsync("one@example.com");
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var user = await database.Users.SingleAsync();
            var unavailable = new BackupService(database, new BackupSettings(7, 10000, 20, null),
                new BackupPayloadProtector(_protection), _clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupService>.Instance);
            Assert.Equal(503, (await Assert.ThrowsAsync<BackupException>(() => unavailable.CreateAsync(user.Id, Request(Guid.NewGuid())))).StatusCode);
            Assert.Empty(await database.UserBackups.ToListAsync());
        }
        Assert.Equal(HttpStatusCode.Conflict, (await CreateAsync(Guid.NewGuid(), 1)).StatusCode);
        Assert.Null((await _client.GetFromJsonAsync<BackupStatus>("/api/v1/backups/status"))!.LatestCompleted);
    }

    [Fact]
    public async Task Metadata_is_paginated_and_create_capacity_does_not_delete_existing_backups()
    {
        await LoginAsync("one@example.com");
        await CreateAsync(Guid.NewGuid());
        _clock.Now = _clock.Now.AddMinutes(1);
        await CreateAsync(Guid.NewGuid());
        var page = (await _client.GetFromJsonAsync<BackupPage>("/api/v1/backups/?page=1&limit=1"))!;
        Assert.Single(page.Items);
        Assert.Equal(2, page.NextPage);
        Assert.Null((await _client.GetFromJsonAsync<BackupPage>("/api/v1/backups/?page=2&limit=1"))!.NextPage);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/backups/?page=0&limit=1")).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var user = await database.Users.SingleAsync();
        var limited = new BackupService(database, new BackupSettings(7, 10000, 2, "C:\\keys"),
            new BackupPayloadProtector(_protection), _clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupService>.Instance);
        Assert.Equal("backup_capacity_reached", (await Assert.ThrowsAsync<BackupException>(() => limited.CreateAsync(user.Id, Request(Guid.NewGuid())))).Code);
        Assert.Equal(2, await database.UserBackups.CountAsync());
    }

    private static CreateBackupRequest Request(Guid requestId, long version = 0) => new(requestId, 1, version);
    private Task<HttpResponseMessage> CreateAsync(Guid requestId, long version = 0) => _client.PostAsJsonAsync("/api/v1/backups/", Request(requestId, version));

    private async Task<AuthTokens> LoginAsync(string email)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "A private password for tests!", displayName = "Student" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return tokens;
    }

    private sealed class BackupClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class BackupFailureInterceptor : SaveChangesInterceptor
    {
        public bool FailCompletion { get; set; }
        public bool FailRestoreAfterSave { get; set; }
        public Action? OnRestoreSaved { get; set; }
        public Action? OnPendingSaved { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailCompletion && eventData.Context!.ChangeTracker.Entries<UserBackup>().Any(entry => entry.Entity.Status == "Completed"))
                throw new DbUpdateException("sensitive database marker");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<UserBackup>().Any(entry => entry.Entity.Status == "Pending")) OnPendingSaved?.Invoke();
            if (FailRestoreAfterSave && eventData.Context!.ChangeTracker.Entries<SyncChange>().Any())
                throw new DbUpdateException("sensitive restore marker");
            if (eventData.Context!.ChangeTracker.Entries<SyncChange>().Any()) OnRestoreSaved?.Invoke();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BackupModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            var backup = modelBuilder.Entity<UserBackup>();
            var converter = new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            backup.Property(item => item.CreatedAtUtc).HasConversion(converter).HasColumnType("bigint");
            backup.Property(item => item.CompletedAtUtc).HasConversion(converter).HasColumnType("bigint");
            backup.Property(item => item.ExpiresAtUtc).HasConversion(converter).HasColumnType("bigint");
            backup.Property(item => item.Payload).HasColumnType("BLOB");
        }
    }

    private sealed class BackupApiFactory(SqliteConnection connection, BackupClock clock, BackupFailureInterceptor failure,
        IDataProtectionProvider protection) : TestApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<FlashcardsDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<FlashcardsDbContext>>();
                services.AddDbContext<FlashcardsDbContext>(options => options.UseSqlite(connection)
                    .ReplaceService<IModelCustomizer, BackupModelCustomizer>().AddInterceptors(failure));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.RemoveAll<BackupSettings>();
                services.AddSingleton(new BackupSettings(7, 1048576, 20, "C:\\test-backup-keys"));
                services.RemoveAll<IDataProtectionProvider>();
                services.AddSingleton(protection);
            });
        }
    }
}
