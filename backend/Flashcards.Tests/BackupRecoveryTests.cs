using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flashcards.Tests;

public sealed partial class BackupTests
{
    [Theory]
    [InlineData("/api/v1/backups/")]
    [InlineData("/api/v1/backups/status")]
    [InlineData("/api/v1/backups/00000000-0000-0000-0000-000000000001")]
    public async Task Backup_metadata_storage_failures_are_safe_retryable_and_keep_the_existing_error_contract(string path)
    {
        await LoginAsync("backup-read-recovery@example.com");
        _failure.ReadFailure.Fail = true;
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("backup_storage_failed", error.GetProperty("code").GetString());
        Assert.True(error.TryGetProperty("backupId", out _));
        Assert.DoesNotContain("private backup details", await response.Content.ReadAsStringAsync());
        Assert.NotNull(response.Headers.RetryAfter);
        _failure.ReadFailure.Fail = false;
        Assert.Equal(path.EndsWith("000000000001", StringComparison.Ordinal) ? HttpStatusCode.NotFound : HttpStatusCode.OK,
            (await _client.GetAsync(path)).StatusCode);
    }
}
