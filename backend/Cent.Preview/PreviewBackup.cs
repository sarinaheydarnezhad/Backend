using Microsoft.Data.Sqlite;

// Explicit preview upgrades must have a readable, verified backup before any DDL.
internal static class PreviewBackup
{
    public static async Task<string> Create(SqliteConnection source, string databasePath, string reason)
    {
        var path = databasePath + ".before-" + reason + "-" + Guid.NewGuid().ToString("N") + ".bak";
        // Reserve the name exclusively. SQLite's default Create mode would overwrite a collision.
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        await backup.OpenAsync();
        source.BackupDatabase(backup);
        using var command = backup.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        if ((string?)await command.ExecuteScalarAsync() != "ok")
            throw new InvalidOperationException("Preview backup integrity verification failed: " + path);
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            throw new InvalidOperationException("Preview backup contains foreign-key violations: " + path);
        return path;
    }
}
