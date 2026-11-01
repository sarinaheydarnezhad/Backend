using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flashcards.Infrastructure.Backups;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flashcards.Tests;

public sealed partial class BackupTests
{
    [Fact]
    public async Task Persisted_backup_is_readable_after_reopening_the_durable_key_ring()
    {
        await LoginAsync("durable-key-regression@example.com");
        var keys = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "lerona-backup-keys-" + Guid.NewGuid().ToString("N")));
        try
        {
            Guid ownerId;
            Guid backupId;
            using (var scope = _factory.Services.CreateScope())
            {
                var provider = DataProtectionProvider.Create(keys, options => options.SetApplicationName("Lerona.Backups"));
                var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
                ownerId = (await database.Users.SingleAsync()).Id;
                database.Decks.Add(new Deck { UserId = ownerId, Name = "Durable contents" });
                await database.SaveChangesAsync();
                var service = new BackupService(database, new BackupSettings(7, 1048576, 20, keys.FullName),
                    new BackupPayloadProtector(provider), _clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupService>.Instance);
                var created = await service.CreateAsync(ownerId, Request(Guid.NewGuid()));
                backupId = created.Backup.Id;
                Assert.Equal("Completed", (await database.UserBackups.AsNoTracking().SingleAsync()).Status);
            }
            Assert.NotEmpty(keys.EnumerateFiles("key-*.xml"));
            var reopened = DataProtectionProvider.Create(keys, options => options.SetApplicationName("Lerona.Backups"));
            using var inspect = _factory.Services.CreateScope();
            var restoredService = new BackupService(inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>(),
                new BackupSettings(7, 1048576, 20, keys.FullName), new BackupPayloadProtector(reopened), _clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupService>.Instance);
            using var snapshot = JsonDocument.Parse(await restoredService.DownloadAsync(ownerId, backupId));
            Assert.Equal("Durable contents", Assert.Single(snapshot.RootElement.GetProperty("decks").EnumerateArray()).GetProperty("name").GetString());
        }
        finally
        {
            // This unique directory contains only this test's generated key files; no recursive deletion.
            foreach (var file in keys.EnumerateFiles()) file.Delete();
            keys.Delete();
        }
    }

    [Fact]
    public async Task Missing_live_key_ring_configuration_returns_actionable_503_without_writing_or_logging_out()
    {
        await LoginAsync("backup-config-regression@example.com");
        using var unavailable = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<BackupSettings>();
            services.AddSingleton(new BackupSettings(30, 10485760, 20, null));
        }));
        using var client = unavailable.CreateClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var response = await client.PostAsJsonAsync("/api/v1/backups/", Request(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("backups_unavailable", error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("traceId").GetString()));
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me/")).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Empty(await database.UserBackups.ToListAsync());
        // The same account can retry successfully against correctly configured storage.
        var retry = await CreateAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var metadata = (await retry.Content.ReadFromJsonAsync<BackupMetadata>())!;
        Assert.Equal("Completed", (await database.UserBackups.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(metadata.Id, (await _client.GetFromJsonAsync<BackupStatus>("/api/v1/backups/status"))!.LatestCompleted!.Id);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/backups/{metadata.Id}/download")).StatusCode);
    }
}
