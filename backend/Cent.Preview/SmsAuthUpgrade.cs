using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// Explicit, backed-up, development-only upgrade. Production uses CentDbContext migrations.
internal static class SmsAuthUpgrade
{
    public static async Task Apply(PreviewDb db, string databasePath)
    {
        await db.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var backupPath = databasePath + ".before-sms-auth-" + Guid.NewGuid().ToString("N") + ".bak";
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString())) {
            backup.Open(); connection.BackupDatabase(backup);
        }
        await using var tx = await db.Database.BeginTransactionAsync();
        var hasTable = false; var hasLink = false;
        using (var command = connection.CreateCommand()) {
            command.CommandText = "PRAGMA table_info(cent_google_attempts)";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) { hasTable = true; if (reader.GetString(1) == "LinkUserId") hasLink = true; }
        }
        if (!hasTable) throw new InvalidOperationException("Run the existing --upgrade-google-auth true upgrade before the SMS upgrade.");
        if (!hasLink) await db.Database.ExecuteSqlRawAsync("ALTER TABLE cent_google_attempts ADD COLUMN LinkUserId TEXT NULL;");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS cent_mobile_identities (
                Phone TEXT NOT NULL PRIMARY KEY, UserId TEXT NOT NULL, VerifiedAt INTEGER NOT NULL,
                FOREIGN KEY(UserId) REFERENCES cent_users(Id));
            CREATE UNIQUE INDEX IF NOT EXISTS IX_cent_mobile_identities_UserId ON cent_mobile_identities(UserId);
            CREATE TABLE IF NOT EXISTS cent_sms_budgets (
                Key TEXT NOT NULL PRIMARY KEY, ResetAt INTEGER NOT NULL, NextAt INTEGER NOT NULL, Count INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_cent_sms_budgets_ResetAt ON cent_sms_budgets(ResetAt);
            CREATE TABLE IF NOT EXISTS cent_sms_challenges (
                IdHash TEXT NOT NULL PRIMARY KEY, Phone TEXT NOT NULL, Verifier TEXT NOT NULL, LinkUserId TEXT NULL,
                ExpiresAt INTEGER NOT NULL, ResendAt INTEGER NOT NULL, Attempts INTEGER NOT NULL,
                Delivered INTEGER NOT NULL, Consumed INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_cent_sms_challenges_Phone ON cent_sms_challenges(Phone);
            CREATE INDEX IF NOT EXISTS IX_cent_sms_challenges_ExpiresAt ON cent_sms_challenges(ExpiresAt);
            """);
        await tx.CommitAsync();
        Console.WriteLine("Preview SMS authentication schema upgrade complete. Backup: " + backupPath);
    }
}
