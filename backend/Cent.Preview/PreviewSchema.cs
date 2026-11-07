using System.Data;
using System.Text.RegularExpressions;
using Cent.Module;
using Microsoft.EntityFrameworkCore;

// SQLite is used by this local-only host; production uses CentDbContext's SQL Server migrations.
internal static class PreviewSchema
{
    public static async Task Upgrade(CentDbContext db, CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        var connection = db.Database.GetDbConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var additions = new Dictionary<string, Dictionary<string, string>> {
            ["cent_questions"] = new() {
                ["TopicId"] = "TEXT NULL REFERENCES cent_topics(Id)",
                ["SourceSectionId"] = "TEXT NULL REFERENCES cent_source_sections(Id)",
                ["SourceExerciseId"] = "TEXT NULL REFERENCES cent_source_exercises(Id)",
                ["ImportKey"] = "TEXT NULL", ["Origin"] = "TEXT NOT NULL DEFAULT 'manual'", ["FigureSvg"] = "TEXT NULL"
            },
            ["cent_documents"] = new() { ["SourceId"] = "TEXT NULL REFERENCES cent_sources(Id)" },
            ["cent_attempts"] = new() { ["CourseCodesJson"] = "TEXT NOT NULL DEFAULT '[]'", ["AssessmentMode"] = "TEXT NOT NULL DEFAULT 'overview'", ["DurationMinutes"] = "INTEGER NOT NULL DEFAULT 60" }
        };
        foreach (var (table, columns) in additions) {
            await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = $"PRAGMA table_info(\"{table}\")";
            var existing = new HashSet<string>();
            await using (var reader = await command.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) existing.Add(reader.GetString(1));
            if (existing.Count == 0) continue;
            foreach (var (column, definition) in columns.Where(c => !existing.Contains(c.Key))) {
                command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        var sql = db.Database.GenerateCreateScript();
        sql = Regex.Replace(sql, @"CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ");
        sql = Regex.Replace(sql, @"CREATE (UNIQUE )?INDEX ", "CREATE $1INDEX IF NOT EXISTS ");
        await using (var command = connection.CreateCommand()) {
            command.Transaction = transaction; command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
