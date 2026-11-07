using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// Explicit, additive development-only upgrade. SQL Server uses the reviewed EF migration.
internal static class CurriculumUpgrade
{
    public static async Task Apply(PreviewDb db, string databasePath)
    {
        await db.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var backupPath = databasePath + ".before-curriculum-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak";
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString())) {
            backup.Open(); connection.BackupDatabase(backup);
        }
        await using var transaction = await db.Database.BeginTransactionAsync();
        var additions = new Dictionary<string, string[]> {
            ["cent_documents"] = ["SourceId TEXT NULL"],
            ["cent_questions"] = ["TopicId TEXT NULL", "SourceSectionId TEXT NULL", "SourceExerciseId TEXT NULL", "ImportKey TEXT NULL", "Origin TEXT NOT NULL DEFAULT 'manual'", "FigureSvg TEXT NULL"],
            ["cent_attempts"] = ["CourseCodesJson TEXT NOT NULL DEFAULT '[]'", "AssessmentMode TEXT NOT NULL DEFAULT 'overview'", "DurationMinutes INTEGER NOT NULL DEFAULT 60"]
        };
        foreach (var (table, columns) in additions) {
            var present = new HashSet<string>();
            using (var command = connection.CreateCommand()) {
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
        var script = db.Database.GenerateCreateScript().Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ")
            .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ").Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ");
        await db.Database.ExecuteSqlRawAsync(script);
        await transaction.CommitAsync();
        Console.WriteLine("Preview curriculum upgrade complete. Backup: " + backupPath);
    }
}
