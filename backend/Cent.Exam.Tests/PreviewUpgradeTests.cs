using Cent.Module;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cent.Exam.Tests;

public sealed class PreviewUpgradeTests
{
    [Fact]
    public async Task Legacy_upgrade_preserves_rows_and_references_allows_subjects_and_can_repeat()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cent-preview-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "legacy.db");
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        using var db = new ReleaseTestContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options);
        var legacy = db.Database.GenerateCreateScript().Replace("[Kind] IN ('TRAINING', 'SUBJECT')", "[Kind] = 'TRAINING'");
        using (var command = connection.CreateCommand()) {
            command.CommandText = legacy; await command.ExecuteNonQueryAsync();
        }
        var owner = new CentUser { Email = "preserved@example.invalid", NormalizedEmail = "PRESERVED@EXAMPLE.INVALID", GoogleSubject = "preserve-subject", PasswordHash = "preserve-historical-hash" };
        var training = new CentTraining { Title = "Preserved training", Published = true };
        var product = new CentProduct { TrainingId = training.Id, Kind = "TRAINING", Title = "Preserved product", PriceRial = 10000 };
        db.AddRange(owner, training, product); await db.SaveChangesAsync();
        var order = new CentOrder { UserId = owner.Id, ProductId = product.Id, TrainingId = training.Id, Kind = "TRAINING", Status = "PAID", AmountRial = product.PriceRial, RequestId = Guid.NewGuid() };
        var document = new CentDocument { TrainingId = training.Id, Title = "Preserved document", PageCount = 1 };
        db.AddRange(order, document); await db.SaveChangesAsync();
        db.AddRange(new CentEntitlement { UserId = owner.Id, OrderId = order.Id, TrainingId = training.Id, Kind = "TRAINING" },
            new CentNote { UserId = owner.Id, DocumentId = document.Id, Body = "Preserved note", Page = 1 },
            new CentProgress { UserId = owner.Id, DocumentId = document.Id, LastPage = 1, FurthestPage = 1 });
        await db.SaveChangesAsync();
        // Simulate the real legacy preview, without modifying an operator database.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE cent_notes DROP COLUMN AnchorJson;");
        foreach (var table in new[] { "cent_subject_access", "cent_subject_order_lines", "cent_subject_orders", "cent_subject_quotes", "cent_subject_offers", "cent_recommended_bundles", "cent_practice_content", "cent_curriculum_revisions", "cent_exam_command_receipts", "cent_attempt_sections", "cent_mock_attempts", "cent_exam_versions", "cent_exam_content_releases", "cent_syllabus_revisions" })
        {
            var sql = $"DROP TABLE \"{table}\";"; // Fixed disposable-fixture table list above.
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE cent_products ADD COLUMN LocalMetadata TEXT NULL; UPDATE cent_products SET LocalMetadata='preserve-extra-column'; CREATE INDEX local_product_title ON cent_products(Title);");
        await CurriculumUpgrade.Apply(db, path);
        var backupPath = Assert.Single(Directory.GetFiles(folder, "*.bak"));
        var backupBytes = await File.ReadAllBytesAsync(backupPath);
        using (var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False")) {
            await backup.OpenAsync();
            using var command = backup.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", await command.ExecuteScalarAsync());
            command.CommandText = "SELECT COUNT(*) FROM cent_users"; Assert.Equal(1L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='cent_subject_offers'"; Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        db.ChangeTracker.Clear();
        Assert.Equal("preserve-subject", (await db.Users.SingleAsync()).GoogleSubject);
        Assert.Equal("preserve-historical-hash", (await db.Users.SingleAsync()).PasswordHash);
        Assert.Equal(order.Id, (await db.Orders.SingleAsync()).Id);
        Assert.Equal(order.Id, (await db.Entitlements.SingleAsync()).OrderId);
        Assert.Equal("Preserved note", (await db.Notes.SingleAsync()).Body);
        Assert.Equal(1, (await db.Progress.SingleAsync()).FurthestPage);
        using (var command = connection.CreateCommand()) {
            command.CommandText = "SELECT LocalMetadata FROM cent_products"; Assert.Equal("preserve-extra-column", await command.ExecuteScalarAsync());
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='local_product_title'"; Assert.Equal(1L, await command.ExecuteScalarAsync());
            command.CommandText = "PRAGMA foreign_keys"; Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        db.Products.Add(new CentProduct { Kind = "SUBJECT", Title = "Subject product", TrainingId = training.Id, PriceRial = 4_900_000 });
        await db.SaveChangesAsync();
        var blueprint = await db.ExamVersions.FirstAsync();
        blueprint.Status = "ARCHIVED"; await db.SaveChangesAsync();
        await CurriculumUpgrade.Apply(db, path);
        Assert.Equal(2, Directory.GetFiles(folder, "*.bak").Length);
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backupPath));
        db.ChangeTracker.Clear(); Assert.Equal("ARCHIVED", (await db.ExamVersions.FindAsync(blueprint.Id))!.Status);
        Assert.Equal(2, await db.Products.CountAsync());
        Assert.Empty(await db.Set<CentSubjectOffer>().ToListAsync()); // Never approve offers as part of a schema upgrade.
        using (var command = connection.CreateCommand()) {
            command.CommandText = "PRAGMA foreign_key_check"; using var reader = await command.ExecuteReaderAsync(); Assert.False(await reader.ReadAsync());
        }
    }

    [Fact]
    public async Task Unrecognized_constraint_rolls_back_and_keeps_verified_backup()
    {
        var path = Path.Combine(Path.GetTempPath(), "cent-preview-unknown-" + Guid.NewGuid().ToString("N") + ".db");
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False"); await connection.OpenAsync();
        using var db = new ReleaseTestContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE cent_documents(Id TEXT); CREATE TABLE cent_questions(Id TEXT); CREATE TABLE cent_attempts(Id TEXT); CREATE TABLE cent_products(Id TEXT, Kind TEXT CHECK(Kind='CUSTOM'));");
        await Assert.ThrowsAsync<InvalidOperationException>(() => CurriculumUpgrade.Apply(db, path));
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('cent_documents')";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        command.CommandText = "PRAGMA foreign_keys"; Assert.Equal(1L, await command.ExecuteScalarAsync());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".before-*.bak"));
    }
}
