using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// Explicit development-only upgrade; production uses CentDbContext migrations.
internal static class GoogleAuthUpgrade
{
    public static async Task Apply(PreviewDb db, string databasePath)
    {
        await db.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var backupPath = databasePath + ".before-google-auth-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak";
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString())) {
            backup.Open(); connection.BackupDatabase(backup);
        }
        await using var transaction = await db.Database.BeginTransactionAsync();
        var hasSubject = false;
        using (var command = connection.CreateCommand()) {
            command.CommandText = "PRAGMA table_info(cent_users)";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) if (reader.GetString(1) == "GoogleSubject") hasSubject = true;
        }
        if (!hasSubject) await db.Database.ExecuteSqlRawAsync("ALTER TABLE cent_users ADD COLUMN GoogleSubject TEXT NULL;");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_cent_users_GoogleSubject ON cent_users(GoogleSubject) WHERE GoogleSubject IS NOT NULL;
            CREATE TABLE IF NOT EXISTS cent_google_attempts (
                StateHash TEXT NOT NULL PRIMARY KEY, VerifierHash TEXT NOT NULL, NonceHash TEXT NOT NULL, ExpiresAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_cent_google_attempts_ExpiresAt ON cent_google_attempts(ExpiresAt);
            """);
        await transaction.CommitAsync();
        Console.WriteLine("Preview Google authentication upgrade complete. Backup: " + backupPath);
    }
}
