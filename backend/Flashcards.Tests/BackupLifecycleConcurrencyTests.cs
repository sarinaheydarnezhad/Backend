using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Backups;
using Flashcards.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Tests;

public sealed partial class BackupTests
{
    // Separate file-backed connections exercise actual SQLite locking, not a shared in-memory
    // connection pretending to be concurrent. These tests do not certify SQL Server locking.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_concurrent_creation_and_failed_finalization_do_not_exceed_capacity(bool failFirst)
    {
        await using var store = await LifecycleStore.CreateAsync();
        var owner = await store.SeedOwnerAsync();
        var gate = new LifecycleGate { Fail = failFirst };
        var firstDb = await store.OpenAsync(gate);
        var secondDb = await store.OpenAsync();
        var first = Task.Run(async () =>
        {
            try { return (await LifecycleService(firstDb, 1).CreateAsync(owner, Request(Guid.NewGuid()))).Backup; }
            catch (BackupException) when (failFirst) { return null; }
        });
        await gate.Pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(async () =>
        {
            started.SetResult();
            try { return (await LifecycleService(secondDb, 1).CreateAsync(owner, Request(Guid.NewGuid()))).Backup; }
            catch (BackupException e) when (e.Code is "backup_capacity_reached" or "backup_storage_failed") { return null; }
        });
        await started.Task;
        gate.Release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));
        var inspect = await store.OpenAsync();
        Assert.Single(await inspect.UserBackups.ToListAsync());
        if (!failFirst) Assert.Single(results, result => result is not null);
        Assert.True(await inspect.UserBackups.AllAsync(row => row.Status != "Pending"));
    }

    [Fact]
    public async Task Lifecycle_cleanup_waits_for_finalization_and_keeps_the_new_completed_payload()
    {
        await using var store = await LifecycleStore.CreateAsync();
        var owner = await store.SeedOwnerAsync();
        var seed = await store.OpenAsync();
        seed.Add(LifecycleRow(owner, "Failed", _clock.Now));
        await seed.SaveChangesAsync();
        var gate = new LifecycleGate();
        var firstDb = await store.OpenAsync(gate);
        var secondDb = await store.OpenAsync();
        var first = Task.Run(() => LifecycleService(firstDb).CreateAsync(owner, Request(Guid.NewGuid())));
        await gate.Pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Task.Run(() => LifecycleService(secondDb).CreateAsync(owner, Request(Guid.NewGuid())));
        gate.Release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));
        var inspect = await store.OpenAsync();
        Assert.Equal(2, await inspect.UserBackups.CountAsync());
        Assert.True(await inspect.UserBackups.AllAsync(row => row.Status == "Completed" && row.Payload != null));
        foreach (var result in results) Assert.NotEmpty(await LifecycleService(inspect).DownloadAsync(owner, result.Backup.Id));
    }

    [Fact]
    public async Task Lifecycle_cleanup_and_restore_preserve_completed_backup_and_existing_data()
    {
        await using var store = await LifecycleStore.CreateAsync();
        var owner = await store.SeedOwnerAsync();
        var seed = await store.OpenAsync();
        var original = await LifecycleService(seed, 3).CreateAsync(owner, Request(Guid.NewGuid()));
        var ciphertext = await seed.UserBackups.AsNoTracking().Where(row => row.Id == original.Backup.Id).Select(row => row.Payload).SingleAsync();
        seed.Add(LifecycleRow(owner, "Failed", _clock.Now));
        await seed.SaveChangesAsync();
        var cleanupDb = await store.OpenAsync();
        var restoreDb = await store.OpenAsync();
        using var scope = _factory.Services.CreateScope();
        var entitlements = new Flashcards.Infrastructure.Subscriptions.EntitlementService(restoreDb,
            scope.ServiceProvider.GetRequiredService<Flashcards.Infrastructure.Subscriptions.PlanCatalog>(), _clock);
        var restore = new BackupRestoreService(restoreDb, LifecycleService(restoreDb, 3), entitlements, _clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupRestoreService>.Instance);
        await Task.WhenAll(
            Task.Run(() => LifecycleService(cleanupDb, 3).CreateAsync(owner, Request(Guid.NewGuid()))),
            Task.Run(() => restore.RestoreAsync(owner, original.Backup.Id, new RestoreBackupRequest("mergeMissing", 0)))
        ).WaitAsync(TimeSpan.FromSeconds(20));
        var inspect = await store.OpenAsync();
        Assert.Equal(ciphertext, await inspect.UserBackups.AsNoTracking().Where(row => row.Id == original.Backup.Id).Select(row => row.Payload).SingleAsync());
        Assert.Equal(2, await inspect.UserBackups.CountAsync());
        Assert.Single(await inspect.Users.ToListAsync());
        Assert.NotEmpty(await LifecycleService(inspect).DownloadAsync(owner, original.Backup.Id));
    }

    private sealed class LifecycleGate : SaveChangesInterceptor
    {
        public TaskCompletionSource Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; init; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<UserBackup>().Any(entry => entry.Entity.Status == "Pending"))
            {
                Pending.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                if (Fail) throw new DbUpdateException("Synthetic interrupted finalization");
            }
            return result;
        }
    }

    private sealed class LifecycleStore : IAsyncDisposable
    {
        private readonly string file = Path.Combine(Path.GetTempPath(), $"lerona-phase6-{Guid.NewGuid():N}.db");
        private readonly List<(FlashcardsDbContext Database, SqliteConnection Connection)> opened = [];
        public static async Task<LifecycleStore> CreateAsync()
        {
            var store = new LifecycleStore();
            await (await store.OpenAsync()).Database.EnsureCreatedAsync();
            return store;
        }
        public async Task<FlashcardsDbContext> OpenAsync(params IInterceptor[] interceptors)
        {
            var connection = new SqliteConnection($"Data Source={file};Pooling=False;Default Timeout=5");
            connection.CreateFunction("ISJSON", (string value) => { try { using var parsed = JsonDocument.Parse(value); return 1; } catch (JsonException) { return 0; } });
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<FlashcardsDbContext>().UseSqlite(connection)
                .ReplaceService<IModelCustomizer, BackupModelCustomizer>().AddInterceptors(interceptors).Options;
            var database = new FlashcardsDbContext(options);
            opened.Add((database, connection));
            return database;
        }
        public async Task<Guid> SeedOwnerAsync()
        {
            var database = await OpenAsync();
            var owner = new User { DisplayName = "Synthetic lifecycle owner" };
            database.Add(owner);
            await database.SaveChangesAsync();
            return owner.Id;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var item in opened) { await item.Database.DisposeAsync(); await item.Connection.DisposeAsync(); }
            // Only these explicitly created synthetic files are removed, never a directory tree.
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(file + suffix);
        }
    }
}
