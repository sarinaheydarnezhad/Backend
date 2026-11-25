using System.Diagnostics;
using System.Text.Json;
using Cent.Module;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Cent.Tests;

public class ReviewedContentTests
{
    [Fact]
    public async Task Upgrading_legacy_content_preserves_purchases_notes_progress_and_assessment_snapshots()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new SqliteCentContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(); await new CentCurriculumImporter(db).Import(includeReviewed:false);
        var document=await db.Documents.FirstAsync(d=>db.Sources.Any(s=>s.Id==d.SourceId&&s.CourseCode=="chemistry"));
        var user=new CentUser { Name="Historical reader",Email="history@example.invalid",NormalizedEmail="HISTORY@EXAMPLE.INVALID" };db.Add(user);
        var product=new CentProduct {Title="Historical fixture",Kind="TRAINING",PriceRial=10000,TrainingId=document.TrainingId};db.Add(product);
        var order=new CentOrder {UserId=user.Id,ProductId=product.Id,RequestId=Guid.NewGuid(),Status="PAID"};db.Add(order);
        var entitlement=new CentEntitlement {UserId=user.Id,OrderId=order.Id,Kind="TRAINING",TrainingId=document.TrainingId,StartsAt=DateTimeOffset.UtcNow.AddDays(-1),ExpiresAt=DateTimeOffset.UtcNow.AddDays(365)};db.Add(entitlement);
        var note=new CentNote {Id=Guid.NewGuid(),UserId=user.Id,DocumentId=document.Id,Page=2,Body="Historical private note",Revision=7,AnchorY=.4,AnchorJson="{\"kind\":\"html\",\"block\":\"historical\",\"quote\":\"historical text\",\"x\":0,\"y\":0.4,\"revision\":\"old\"}"};db.Add(note);
        db.Add(new CentProgress {UserId=user.Id,DocumentId=document.Id,LastPage=2,FurthestPage=3});
        var question=await db.Questions.FirstAsync(q=>q.Subject=="chemistry");
        var attempt=new CentAttempt {UserId=user.Id,RequestId=Guid.NewGuid(),Kind="practice",SubmittedAt=DateTimeOffset.UtcNow,ResultJson="{\"historical\":true}"};db.Add(attempt);
        var snapshot=JsonSerializer.Serialize(new QuestionSnapshot(question.Id,question.Subject,question.Topic,question.Difficulty,question.Text,JsonSerializer.Deserialize<string[]>(question.OptionsJson)!,question.CorrectAnswer,question.Explanation,question.TopicId,question.SourceSectionId));
        db.Add(new CentAttemptQuestion {AttemptId=attempt.Id,QuestionId=question.Id,Position=0,SnapshotJson=snapshot,Answer=question.CorrectAnswer});await db.SaveChangesAsync();
        var anchor=note.AnchorJson;var expires=entitlement.ExpiresAt;
        await new CentCurriculumImporter(db).Import();db.ChangeTracker.Clear();
        var saved=await db.Notes.SingleAsync(n=>n.Id==note.Id);Assert.Equal(7,saved.Revision);Assert.Equal(anchor,saved.AnchorJson);Assert.Equal(2,saved.Page);Assert.Equal(note.Body,saved.Body);
        Assert.Equal(3,(await db.Progress.SingleAsync(p=>p.UserId==user.Id)).FurthestPage);
        Assert.Equal(snapshot,(await db.AttemptQuestions.SingleAsync(q=>q.AttemptId==attempt.Id)).SnapshotJson);
        Assert.Equal("{\"historical\":true}",(await db.Attempts.SingleAsync(a=>a.Id==attempt.Id)).ResultJson);
        Assert.Equal("PAID",(await db.Orders.SingleAsync(o=>o.Id==order.Id)).Status);Assert.Equal(expires,(await db.Entitlements.SingleAsync(e=>e.OrderId==order.Id)).ExpiresAt);
    }
    [Fact]
    public async Task Import_is_repeatable_and_all_reviewed_sections_and_assets_sanitize()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new SqliteCentContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var watch = Stopwatch.StartNew();
        await new CentCurriculumImporter(db).Import();
        Assert.True(await CentReviewedContent.Imported(db, default));
        Assert.Equal(1471, await db.Questions.CountAsync());
        var counts = (await db.SourceSections.CountAsync(), await db.SourceExercises.CountAsync(), await db.Documents.CountAsync());
        var html = await db.SourceSections.OrderBy(s => s.Id).Select(s => s.Html).ToArrayAsync();
        await new CentCurriculumImporter(db).Import();
        Assert.Equal(counts, (await db.SourceSections.CountAsync(), await db.SourceExercises.CountAsync(), await db.Documents.CountAsync()));
        Assert.Equal(html, await db.SourceSections.OrderBy(s => s.Id).Select(s => s.Html).ToArrayAsync());
        var sources = await db.Sources.ToDictionaryAsync(s => s.Id);
        var assets = 0;
        var mathAssets = 0;
        foreach (var section in await db.SourceSections.ToListAsync()) {
            var fragment = CentEducationalContent.Fragment(section.Html, sources[section.SourceId].ImportKey);
            Assert.NotEmpty(fragment.Html);
            Assert.DoesNotContain("<script", fragment.Html); Assert.DoesNotContain("onclick=", fragment.Html);
            foreach (var asset in fragment.Assets) { Assert.NotEmpty(asset.Bytes); assets++; }
            if (sources[section.SourceId].CourseCode == "math") mathAssets += System.Text.RegularExpressions.Regex.Matches(fragment.Html, "<img ").Count;
        }
        Console.WriteLine($"Reviewed import + repeat + whole-corpus sanitization: {watch.ElapsedMilliseconds}ms; assets={assets}; sections={counts.Item1}");
        Assert.True(assets >= 200);
        Assert.Equal(121, mathAssets);
    }

    [Fact]
    public async Task Conflicts_roll_back_and_never_rewrite_private_user_data()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new SqliteCentContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(); await new CentCurriculumImporter(db).Import();
        var doc = await db.Documents.FirstAsync();
        var user = new CentUser { Name = "Private", Email = "private@example.invalid", NormalizedEmail = "PRIVATE@EXAMPLE.INVALID" }; db.Add(user);
        var note = new CentNote { Id = Guid.NewGuid(), UserId = user.Id, DocumentId = doc.Id, Page = 1, Body = "private note", AnchorJson = "{\"kind\":\"html\",\"block\":\"old\",\"quote\":\"old quote\",\"x\":0,\"y\":0}" }; db.Add(note);
        var progress = new CentProgress { UserId = user.Id, DocumentId = doc.Id, LastPage = 1, FurthestPage = 2 }; db.Add(progress); await db.SaveChangesAsync();
        var originalAnchor = note.AnchorJson;
        var receipt = await db.CurriculumImports.SingleAsync(i => i.PackageHash == CentReviewedContent.Hash); db.Remove(receipt);
        var change = CentReviewedContent.Content.Changes.First(c => c.Entity == "sections" && c.Before != null);
        var section = await db.SourceSections.SingleAsync(s => s.Id == change.Id); section.Html = "local independent edit"; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CentCurriculumImporter(db).Import());
        db.ChangeTracker.Clear();
        Assert.False(await CentReviewedContent.Imported(db, default));
        Assert.Equal("local independent edit", (await db.SourceSections.SingleAsync(s => s.Id == change.Id)).Html);
        var kept = await db.Notes.SingleAsync(n => n.Id == note.Id);
        Assert.Equal("private note", kept.Body); Assert.Equal(originalAnchor, kept.AnchorJson); Assert.Equal(1, kept.Revision);
        Assert.Equal(2, (await db.Progress.SingleAsync(p => p.UserId == user.Id)).FurthestPage);
    }

    [Fact]
    public async Task Cleanup_is_bounded_and_preserves_active_leases_and_nonreader_audit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new SqliteCentContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(); var old = DateTimeOffset.UtcNow.AddDays(-100);
        for (var i = 0; i < 5; i++) db.Add(new CentReaderBudget { Key = "expired" + i, UpdatedAt = old });
        db.Add(new CentReaderBudget { Key = "active", UpdatedAt = old.AddDays(1), StateJson = JsonSerializer.Serialize(new CentReaderProtection.State { Leases = [new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1))] }) });
        db.Audit.Add(new CentAuditEvent { Action = "PAYMENT", CreatedAt = old });
        db.Audit.Add(new CentAuditEvent { Action = "READER_SECTION_DELIVERY", CreatedAt = old }); await db.SaveChangesAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Cent:Reader:Protection:CleanupBatchSize"] = "2", ["Cent:Reader:AuditRetentionDays"] = "90" }).Build();
        Assert.Equal(2, await new CentReaderCleanup(db, config, TimeProvider.System).Run(default));
        Assert.Equal(4, await db.Set<CentReaderBudget>().CountAsync()); Assert.True(await db.Set<CentReaderBudget>().AnyAsync(r => r.Key == "active"));
        Assert.Equal("PAYMENT", (await db.Audit.SingleAsync()).Action);
    }

    [Fact]
    public async Task Reviewed_reading_assessment_snapshots_passages_hides_keys_and_preserves_history()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new SqliteCentContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(); await new CentCurriculumImporter(db).Import();
        var user = new CentUser { Name = "Reader", Email = "reader@example.invalid", NormalizedEmail = "READER@EXAMPLE.INVALID" }; db.Add(user);
        var training = (await db.Courses.SingleAsync(c => c.Code == "reading")).TrainingId;
        var product = new CentProduct { Title="Isolated fixture",Kind="TRAINING",PriceRial=10000,TrainingId=training }; db.Add(product);
        var order = new CentOrder { UserId=user.Id,ProductId=product.Id,RequestId=Guid.NewGuid() }; db.Add(order);
        db.Add(new CentEntitlement { UserId=user.Id,OrderId=order.Id,Kind="TRAINING",TrainingId=training,StartsAt=DateTimeOffset.UtcNow.AddDays(-1) }); await db.SaveChangesAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Cent:Reader:Enabled"]="true", ["Cent:Reader:AllSubjects"]="true" }).Build();
        var definition = CentReviewedContent.Content.Assessments.First(a => a.Subject == "reading");
        var request = Guid.NewGuid();
        var json = JsonSerializer.SerializeToElement(await CentReviewedAssessments.Start(db,new CentAccess(db),config,user.Id,definition.Id,request,default),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(10,json.GetProperty("questions").GetArrayLength()); Assert.Equal(2,json.GetProperty("passages").GetArrayLength());
        foreach (var forbidden in new[] { "correctAnswer", "correctOptionText", "explanation", "solution" }) Assert.DoesNotContain("\""+forbidden+"\":",json.GetRawText(),StringComparison.OrdinalIgnoreCase);
        var id=json.GetProperty("id").GetGuid();
        var repeated = JsonSerializer.SerializeToElement(await CentReviewedAssessments.Start(db,new CentAccess(db),config,user.Id,definition.Id,request,default),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(id,repeated.GetProperty("id").GetGuid());
        var practice = new CentPractice(db,new CentAccess(db));
        Assert.Equal(404,(await Assert.ThrowsAsync<CentException>(()=>practice.Result(user.Id,id,default))).Status);
        var snapshots = await db.AttemptQuestions.Where(q=>q.AttemptId==id).Select(q=>q.SnapshotJson).ToArrayAsync();
        var qid=definition.QuestionIds[0]; (await db.Questions.SingleAsync(q=>q.Id==qid)).Explanation="later edit"; await db.SaveChangesAsync();
        await practice.Submit(user.Id,id,new(),default);
        var result=JsonSerializer.SerializeToElement(await practice.Result(user.Id,id,default),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(2,result.GetProperty("passages").GetArrayLength()); Assert.Equal(10,result.GetProperty("review").GetArrayLength());
        Assert.DoesNotContain("later edit",result.GetRawText());
        Assert.Equal(snapshots,await db.AttemptQuestions.Where(q=>q.AttemptId==id).Select(q=>q.SnapshotJson).ToArrayAsync());
    }
}
