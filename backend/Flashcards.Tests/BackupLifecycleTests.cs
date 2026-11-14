using Flashcards.Domain;
using Flashcards.Infrastructure.Backups;
using Flashcards.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flashcards.Tests;

public sealed partial class BackupTests
{
    private BackupService LifecycleService(FlashcardsDbContext database, int capacity = 2) => new(database,
        new BackupSettings(7, 1048576, capacity, "C:\\test-backup-keys"),
        new BackupPayloadProtector(_protection), _clock, NullLogger<BackupService>.Instance);

    private UserBackup LifecycleRow(Guid owner, string status, DateTimeOffset expiry) => new()
    {
        UserId = owner, RequestId = Guid.NewGuid(), FormatVersion = 1, Status = status,
        CreatedAtUtc = _clock.Now.AddDays(-8), ExpiresAtUtc = expiry, RetentionDays = 7,
        Payload = status == "Completed" ? new byte[] { 1, 2, 3 } : null,
        SizeBytes = status == "Completed" ? 3 : null,
        SyncVersion = status == "Completed" ? 0 : null,
        CompletedAtUtc = status == "Completed" ? _clock.Now.AddDays(-7) : null,
        FailureCode = status == "Failed" ? "backup_storage_failed" : null
    };

    [Fact]
    public async Task Lifecycle_original_two_expired_failed_rows_recover_capacity_transactionally()
    {
        await LoginAsync("lifecycle@example.com");
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var owner = (await database.Users.SingleAsync()).Id;
        database.AddRange(LifecycleRow(owner, "Failed", _clock.Now.AddTicks(-1)),
            LifecycleRow(owner, "Failed", _clock.Now.AddDays(-1)));
        await database.SaveChangesAsync();
        var result = await LifecycleService(database).CreateAsync(owner, Request(Guid.NewGuid()));
        Assert.Equal("Completed", result.Backup.Status);
        database.ChangeTracker.Clear();
        var row = Assert.Single(await database.UserBackups.ToListAsync());
        Assert.Equal(result.Backup.Id, row.Id);
        Assert.Equal(row.Payload!.LongLength, row.SizeBytes);
        Assert.NotEmpty(await LifecycleService(database).DownloadAsync(owner, row.Id));
    }

    [Fact]
    public async Task Lifecycle_original_capacity_two_http_request_creates_backup_after_cleanup()
    {
        await LoginAsync("lifecycle-http@example.com");
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var owner = (await database.Users.SingleAsync()).Id;
            database.AddRange(LifecycleRow(owner, "Failed", _clock.Now.AddDays(-1)), LifecycleRow(owner, "Failed", _clock.Now.AddDays(-1)));
            await database.SaveChangesAsync();
        }
        using var capped = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<BackupSettings>();
            services.AddSingleton(new BackupSettings(7, 1048576, 2, "C:\\test-backup-keys"));
        }));
        using var client = capped.CreateClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var response = await client.PostAsJsonAsync("/api/v1/backups/", Request(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Completed", (await response.Content.ReadFromJsonAsync<BackupMetadata>())!.Status);
    }

    [Fact]
    public async Task Lifecycle_spreadsheet_like_content_is_encrypted_and_restored_without_rewriting()
    {
        await LoginAsync("lifecycle-formulas@example.com");
        var fixture = await SeedBackupAsync();
        const string examples = "[{\"sentence\":\"=1+1\",\"translation\":\"+word\",\"notes\":\"@note\"}]";
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            await database.Decks.ExecuteUpdateAsync(update => update.SetProperty(deck => deck.Name, "=deck"));
            await database.Cards.ExecuteUpdateAsync(update => update.SetProperty(card => card.FrontText, "=1+1")
                .SetProperty(card => card.Meaning, "-42").SetProperty(card => card.Phonetic, "+sound")
                .SetProperty(card => card.Category, "@tag").SetProperty(card => card.ExamplesJson, examples));
        }
        var created = await CreateAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var backup = (await created.Content.ReadFromJsonAsync<BackupMetadata>())!;
        using var snapshot = JsonDocument.Parse(await _client.GetStringAsync($"/api/v1/backups/{backup.Id}/download"));
        Assert.Equal("=1+1", snapshot.RootElement.GetProperty("cards")[0].GetProperty("frontText").GetString());
        await EraseLearningAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, (await RestoreAsync(backup.Id)).StatusCode);
        using var inspect = _factory.Services.CreateScope();
        var restored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal("=deck", (await restored.Decks.SingleAsync()).Name);
        var card = await restored.Cards.SingleAsync();
        Assert.Equal("=1+1", card.FrontText);
        Assert.Equal("-42", card.Meaning);
        Assert.Equal("+sound", card.Phonetic);
        Assert.Equal("@tag", card.Category);
        Assert.Equal(examples, card.ExamplesJson);
        Assert.Single(await restored.ReviewEvents.ToListAsync());
    }

    [Theory]
    [InlineData("Failed", -1, true)]
    [InlineData("Failed", 0, true)]
    [InlineData("Failed", 1, false)]
    [InlineData("Pending", -1, false)]
    [InlineData("Pending", 1, false)]
    [InlineData("Completed", -1, false)]
    [InlineData("Completed", 1, false)]
    public async Task Lifecycle_only_expired_failed_rows_are_reclaimed(string status, int ticks, bool reclaim)
    {
        await LoginAsync("boundary@example.com");
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var owner = (await database.Users.SingleAsync()).Id;
        var original = LifecycleRow(owner, status, _clock.Now.AddTicks(ticks));
        database.Add(original);
        await database.SaveChangesAsync();
        var service = LifecycleService(database, 1);
        if (reclaim) Assert.Equal("Completed", (await service.CreateAsync(owner, Request(Guid.NewGuid()))).Backup.Status);
        else Assert.Equal("backup_capacity_reached", (await Assert.ThrowsAsync<BackupException>(() =>
            service.CreateAsync(owner, Request(Guid.NewGuid())))).Code);
        database.ChangeTracker.Clear();
        Assert.Equal(!reclaim, await database.UserBackups.AnyAsync(row => row.Id == original.Id));
        Assert.Single(await database.UserBackups.ToListAsync());
    }

    [Fact]
    public async Task Lifecycle_cleanup_is_owner_scoped_and_repeated_creation_keeps_valid_ciphertext()
    {
        await LoginAsync("isolation@example.com");
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var owner = (await database.Users.SingleAsync()).Id;
        var foreign = new User { DisplayName = "Synthetic foreign owner" };
        var foreignRow = LifecycleRow(foreign.Id, "Failed", _clock.Now);
        database.AddRange(foreign, foreignRow, LifecycleRow(owner, "Failed", _clock.Now));
        await database.SaveChangesAsync();
        var service = LifecycleService(database);
        var request = Request(Guid.NewGuid());
        var result = await service.CreateAsync(owner, request);
        var ciphertext = await database.UserBackups.AsNoTracking().Where(row => row.Id == result.Backup.Id).Select(row => row.Payload).SingleAsync();
        Assert.True((await service.CreateAsync(owner, request)).Replayed);
        await service.CreateAsync(owner, Request(Guid.NewGuid()));
        Assert.Equal("backup_capacity_reached", (await Assert.ThrowsAsync<BackupException>(() =>
            service.CreateAsync(owner, Request(Guid.NewGuid())))).Code);
        Assert.Equal(ciphertext, await database.UserBackups.AsNoTracking().Where(row => row.Id == result.Backup.Id).Select(row => row.Payload).SingleAsync());
        Assert.Null((await database.UserBackups.AsNoTracking().SingleAsync(row => row.Id == foreignRow.Id)).Payload);
        Assert.Equal("backup_not_found", (await Assert.ThrowsAsync<BackupException>(() => service.DownloadAsync(foreign.Id, result.Backup.Id))).Code);
        Assert.NotEmpty(await service.DownloadAsync(owner, result.Backup.Id));
    }

    [Fact]
    public async Task Lifecycle_failed_finalization_rolls_back_cleanup_and_restart_recovers()
    {
        await LoginAsync("rollback@example.com");
        Guid owner, originalId;
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            owner = (await database.Users.SingleAsync()).Id;
            var original = LifecycleRow(owner, "Failed", _clock.Now);
            originalId = original.Id;
            database.Add(original);
            await database.SaveChangesAsync();
            _failure.FailCompletion = true;
            Assert.Equal("backup_storage_failed", (await Assert.ThrowsAsync<BackupException>(() =>
                LifecycleService(database).CreateAsync(owner, Request(Guid.NewGuid())))).Code);
            database.ChangeTracker.Clear();
            Assert.Null((await database.UserBackups.SingleAsync(row => row.Id == originalId)).Payload);
            Assert.DoesNotContain(await database.UserBackups.ToListAsync(), row => row.Status == "Completed");
        }
        _failure.FailCompletion = false;
        using var restarted = _factory.Services.CreateScope();
        var fresh = restarted.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal("Completed", (await LifecycleService(fresh).CreateAsync(owner, Request(Guid.NewGuid()))).Backup.Status);
        Assert.False(await fresh.UserBackups.AnyAsync(row => row.Id == originalId));
    }

    [Fact]
    public async Task Lifecycle_interruption_rolls_back_cleanup_and_preserves_existing_records()
    {
        await LoginAsync("interrupt@example.com");
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var owner = (await database.Users.SingleAsync()).Id;
        var original = LifecycleRow(owner, "Failed", _clock.Now);
        database.Add(original);
        await database.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        _failure.OnPendingSaved = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LifecycleService(database).CreateAsync(owner, Request(Guid.NewGuid()), cancellation.Token));
        database.ChangeTracker.Clear();
        Assert.Null((await database.UserBackups.SingleAsync(row => row.Id == original.Id)).Payload);
        _failure.OnPendingSaved = null;
        Assert.Equal("Completed", (await LifecycleService(database).CreateAsync(owner, Request(Guid.NewGuid()))).Backup.Status);
    }

    [Fact]
    public async Task Lifecycle_cleanup_is_bounded_and_capacity_rejection_commits_only_eligible_cleanup()
    {
        await LoginAsync("bounded@example.com");
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var owner = (await database.Users.SingleAsync()).Id;
        database.AddRange(Enumerable.Range(0, 130).Select(_ => LifecycleRow(owner, "Failed", _clock.Now)));
        await database.SaveChangesAsync();
        var service = LifecycleService(database, 1);
        Assert.Equal("backup_capacity_reached", (await Assert.ThrowsAsync<BackupException>(() => service.CreateAsync(owner, Request(Guid.NewGuid())))).Code);
        Assert.Equal(2, await database.UserBackups.CountAsync());
        Assert.Equal("Completed", (await service.CreateAsync(owner, Request(Guid.NewGuid()))).Backup.Status);
        Assert.Single(await database.UserBackups.ToListAsync());
    }
}
