using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Cent.Module;
using System.Text.RegularExpressions;

// Explicit, additive development-only upgrade. SQL Server uses the reviewed EF migration.
internal static class CurriculumUpgrade
{
    public static async Task Apply(CentDbContext db, string databasePath)
    {
        await db.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var backupPath = await PreviewBackup.Create(connection, databasePath, "curriculum");
        // SQLite cannot ALTER a CHECK constraint. Disable FK enforcement outside the
        // transaction for the product-table copy, then validate every FK before commit.
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;");
        try {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var additions = new Dictionary<string, string[]> {
                ["cent_documents"] = ["SourceId TEXT NULL"],
                ["cent_questions"] = ["TopicId TEXT NULL", "SourceSectionId TEXT NULL", "SourceExerciseId TEXT NULL", "ImportKey TEXT NULL", "Origin TEXT NOT NULL DEFAULT 'manual'", "FigureSvg TEXT NULL"],
                ["cent_attempts"] = ["CourseCodesJson TEXT NOT NULL DEFAULT '[]'", "AssessmentMode TEXT NOT NULL DEFAULT 'overview'", "DurationMinutes INTEGER NOT NULL DEFAULT 60"]
            };
            foreach (var (table, columns) in additions) {
                var present = new HashSet<string>();
                using (var command = connection.CreateCommand()) {
                    command.Transaction = (SqliteTransaction)transaction.GetDbTransaction();
                    command.CommandText = $"PRAGMA table_info(\"{table}\")";
                    using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) present.Add(reader.GetString(1));
                }
                foreach (var column in columns)
                    if (!present.Contains(column.Split(' ')[0])) {
                        // Identifiers and definitions come exclusively from the fixed dictionary above.
                        var sql = $"ALTER TABLE \"{table}\" ADD COLUMN {column}";
                        await db.Database.ExecuteSqlRawAsync(sql);
                    }
            }
            await UpgradeProductConstraint(db, connection);
            var script = db.Database.GenerateCreateScript().Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ")
                .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ").Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ")
                // Keep existing blueprint identities, publication and administrator choices.
                .Replace("INSERT INTO ", "INSERT OR IGNORE INTO ");
            // Seeded exam definitions contain JSON braces. Execute the provider script
            // directly; EF's composite SQL formatter interprets those braces as parameters.
            using (var command = connection.CreateCommand()) {
                command.Transaction = (SqliteTransaction)transaction.GetDbTransaction();
                command.CommandText = script;
                await command.ExecuteNonQueryAsync();
            }
            using (var command = connection.CreateCommand()) {
                command.Transaction = (SqliteTransaction)transaction.GetDbTransaction();
                command.CommandText = "PRAGMA foreign_key_check";
                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync()) throw new InvalidOperationException("Preview upgrade would violate foreign keys; rolled back.");
            }
            await transaction.CommitAsync();
        } finally {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;");
        }
        Console.WriteLine("Preview curriculum upgrade complete. Backup: " + backupPath);
    }

    private static async Task UpgradeProductConstraint(CentDbContext db, SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='cent_products'";
        var original = (string?)await command.ExecuteScalarAsync();
        if (original is null || original.Contains("'SUBJECT'", StringComparison.Ordinal)) return;
        var widened = original.Replace("[Kind] = 'TRAINING'", "[Kind] IN ('TRAINING', 'SUBJECT')", StringComparison.Ordinal);
        if (widened == original) throw new InvalidOperationException("Unrecognized preview product constraint; no data changed.");
        var create = Regex.Replace(widened, "^CREATE TABLE (?:\"cent_products\"|cent_products)", "CREATE TABLE \"cent_products_upgrade\"");
        if (create == widened) throw new InvalidOperationException("Unrecognized preview product table definition.");
        var objects = new List<string>();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE tbl_name='cent_products' AND type IN ('index','trigger') AND sql IS NOT NULL";
        using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) objects.Add(reader.GetString(0));
        // SELECT * also preserves extra local columns. Both directions and row count
        // are checked before replacing the old table, in the same transaction.
        await db.Database.ExecuteSqlRawAsync(create);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO cent_products_upgrade SELECT * FROM cent_products;");
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM cent_products) = (SELECT COUNT(*) FROM cent_products_upgrade)
            AND NOT EXISTS (SELECT * FROM cent_products EXCEPT SELECT * FROM cent_products_upgrade)
            AND NOT EXISTS (SELECT * FROM cent_products_upgrade EXCEPT SELECT * FROM cent_products);
            """;
        if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 1)
            throw new InvalidOperationException("Product copy verification failed; no data changed.");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE cent_products; ALTER TABLE cent_products_upgrade RENAME TO cent_products;");
        foreach (var sql in objects) await db.Database.ExecuteSqlRawAsync(sql);
    }
}
