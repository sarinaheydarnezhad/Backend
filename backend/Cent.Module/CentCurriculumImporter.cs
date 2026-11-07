using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cent.Module.Curriculum;
using Microsoft.EntityFrameworkCore;

namespace Cent.Module;

public sealed record CurriculumImportReport(string PackageHash, int Sources, int Chapters, int Topics, int SourceExercises, int Questions, int AuthoredQuestions, bool AlreadyImported);

public sealed class CentCurriculumImporter(CentDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static Corpus Bundled()
    {
        var assembly = typeof(CentCurriculumImporter).Assembly;
        using var stream = assembly.GetManifestResourceStream("Cent.Module.Content.curriculum.json") ?? throw new InvalidOperationException("The curriculum package is missing.");
        return JsonSerializer.Deserialize<Corpus>(stream, Json) ?? throw new InvalidOperationException("The curriculum package is empty.");
    }

    public async Task<CurriculumImportReport> Import(CancellationToken ct = default)
    {
        var assembly = typeof(CentCurriculumImporter).Assembly;
        using var packageStream = assembly.GetManifestResourceStream("Cent.Module.Content.curriculum.json")!;
        using var packageBytes = new MemoryStream(); await packageStream.CopyToAsync(packageBytes, ct);
        var hash = Convert.ToHexString(SHA256.HashData(packageBytes.ToArray())).ToLowerInvariant();
        var package = JsonSerializer.Deserialize<Corpus>(packageBytes.ToArray(), Json)!;
        var report = new CurriculumImportReport(hash, package.Sources.Count, package.Chapters.Count, package.Topics.Count,
            package.Exercises.Count, package.Questions.Count, package.Questions.Count(q => q.Origin == "authored"), false);
        if (await db.CurriculumImports.AnyAsync(i => i.PackageHash == hash, ct)) return report with { AlreadyImported = true };
        Validate(package);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var trainingIds = await db.Trainings.Select(t => t.Id).ToListAsync(ct);
        foreach (var (code, title, english) in CurriculumIdentity.Courses) {
            var trainingId = CurriculumIdentity.Id("training:" + code);
            if (!trainingIds.Contains(trainingId)) db.Trainings.Add(new CentTraining {
                Id = trainingId, Title = title + " — منابع اصلی CEnT", Published = true, IncludedInSubscription = true,
                Description = "درس‌نامه‌ها و تمرین‌های منبع اصلی " + title + ". دسترسی خریداری‌شده در محیط مطالعه ارائه می‌شود."
            });
            var course = await db.Courses.FindAsync([code], ct);
            if (course is null) { course = new CentCourse { Code = code }; db.Courses.Add(course); }
            course.Title = title; course.EnglishTitle = english; course.SortOrder = Array.FindIndex(CurriculumIdentity.Courses, c => c.Code == code);
            course.TrainingId = trainingId; course.Active = true;
        }
        await db.SaveChangesAsync(ct);
        foreach (var source in package.Sources) {
            var id = CurriculumIdentity.Id("source:" + source.Key);
            var row = await db.Sources.FindAsync([id], ct);
            if (row is null) { row = new CentSource { Id = id, ImportKey = source.Key }; db.Sources.Add(row); }
            var resource = "Cent.Module.Content.Sources." + source.File.Replace('/', '.');
            using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Missing source: " + source.File);
            using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes, ct);
            CentModule.Check(Convert.ToHexString(SHA256.HashData(bytes.ToArray())).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase), "Source checksum mismatch: " + source.File);
            row.CourseCode = source.Course; row.FileName = source.File; row.Title = source.Title; row.OriginalTitle = source.OriginalTitle;
            row.Sha256 = source.Sha256; row.OriginalHtml = Encoding.UTF8.GetString(bytes.ToArray()); row.PlainText = source.Text; row.ImportedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        foreach (var chapter in package.Chapters) {
            var id = CurriculumIdentity.Id("chapter:" + chapter.Key);
            var row = await db.Chapters.FindAsync([id], ct);
            if (row is null) { row = new CentChapter { Id = id, ImportKey = chapter.Key }; db.Chapters.Add(row); }
            row.CourseCode = chapter.Course; row.SourceId = CurriculumIdentity.Id("source:" + chapter.Key.Split('#')[0]); row.Title = chapter.Title; row.SortOrder = chapter.SortOrder;
        }
        await db.SaveChangesAsync(ct);
        foreach (var topic in package.Topics) {
            var id = CurriculumIdentity.Id("topic:" + topic.Key);
            var row = await db.Topics.FindAsync([id], ct);
            if (row is null) { row = new CentTopic { Id = id, ImportKey = topic.Key }; db.Topics.Add(row); }
            row.ChapterId = CurriculumIdentity.Id("chapter:" + topic.ChapterKey); row.CourseCode = topic.Course;
            row.Title = topic.Title; row.EnglishTitle = topic.EnglishTitle; row.SortOrder = topic.SortOrder; row.Foundation = topic.Foundation;
        }
        await db.SaveChangesAsync(ct);
        foreach (var section in package.Sections) {
            var id = CurriculumIdentity.Id("section:" + section.Key);
            var row = await db.SourceSections.FindAsync([id], ct);
            if (row is null) { row = new CentSourceSection { Id = id, ImportKey = section.Key }; db.SourceSections.Add(row); }
            row.SourceId = CurriculumIdentity.Id("source:" + section.SourceKey); row.TopicId = section.TopicKey is null ? null : CurriculumIdentity.Id("topic:" + section.TopicKey);
            row.Heading = section.Heading; row.SortOrder = section.SortOrder; row.Text = section.Text; row.Html = section.Html;
        }
        await db.SaveChangesAsync(ct);
        foreach (var exercise in package.Exercises) {
            var id = CurriculumIdentity.Id("exercise:" + exercise.Key);
            var row = await db.SourceExercises.FindAsync([id], ct);
            if (row is null) { row = new CentSourceExercise { Id = id, ImportKey = exercise.Key }; db.SourceExercises.Add(row); }
            row.SourceSectionId = CurriculumIdentity.Id("section:" + exercise.SectionKey);
            row.TopicId = exercise.TopicKey is null ? null : CurriculumIdentity.Id("topic:" + exercise.TopicKey);
            row.Text = exercise.Text; row.OptionsJson = JsonSerializer.Serialize(exercise.Options); row.OriginalCorrectAnswer = exercise.CorrectAnswer;
            row.Solution = exercise.Solution; row.Status = exercise.Status;
            var finding = package.Findings.FirstOrDefault(f => f.Key == exercise.Key);
            row.ReviewNote = finding?.Reason;
            if (finding?.Action == "quarantined") row.Status = "quarantined";
        }
        await db.SaveChangesAsync(ct);
        foreach (var question in package.Questions) {
            var id = CurriculumIdentity.Id("question:" + question.Key);
            var row = await db.Questions.FindAsync([id], ct);
            if (row is null) { row = new CentQuestion { Id = id, ImportKey = question.Key }; db.Questions.Add(row); }
            row.Subject = question.Course; row.TopicId = CurriculumIdentity.Id("topic:" + question.TopicKey);
            row.Topic = package.Topics.Single(t => t.Key == question.TopicKey).Title;
            row.SourceSectionId = CurriculumIdentity.Id("section:" + question.SectionKey);
            row.SourceExerciseId = question.ExerciseKey is null ? null : CurriculumIdentity.Id("exercise:" + question.ExerciseKey);
            row.Text = question.Text; row.OptionsJson = JsonSerializer.Serialize(question.Options); row.CorrectAnswer = question.CorrectAnswer;
            row.Explanation = question.Explanation; row.Difficulty = question.Difficulty; row.Origin = question.Origin;
            row.FigureSvg = question.FigureSvg; row.Active = true; row.Kind = "both";
        }
        var keys = package.Questions.Select(q => q.Key).ToArray();
        foreach (var retired in await db.Questions.Where(q => q.ImportKey != null && !keys.Contains(q.ImportKey)).ToListAsync(ct)) retired.Active = false;
        foreach (var source in package.Sources) {
            var id = CurriculumIdentity.Id("document:" + source.Key);
            var document = await db.Documents.FindAsync([id], ct);
            if (document is null) { document = new CentDocument { Id = id }; db.Documents.Add(document); }
            document.SourceId = CurriculumIdentity.Id("source:" + source.Key); document.TrainingId = CurriculumIdentity.Id("training:" + source.Course);
            document.Title = source.Title.Length <= 200 ? source.Title : source.Title[..200];
            document.PageCount = package.Sections.Count(s => s.SourceKey == source.Key); document.Published = true;
            document.SortOrder = package.Chapters.Where(ch => ch.Key.Split('#')[0] == source.Key).Min(ch => ch.SortOrder);
        }
        db.CurriculumImports.Add(new CentCurriculumImport { PackageHash = hash, SourceCount = package.Sources.Count, TopicCount = package.Topics.Count, QuestionCount = package.Questions.Count });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return report;
    }

    public static void Validate(Corpus package)
    {
        if (package.Sources.Count != 39 || package.Sources.Select(s => s.Course).Distinct().Count() != 5 || package.Questions.Count == 0)
            throw new InvalidOperationException("The bundled curriculum is incomplete.");
        if (package.Questions.Select(q => q.Key).Distinct().Count() != package.Questions.Count) throw new InvalidOperationException("Duplicate question import keys.");
        foreach (var q in package.Questions) {
            if (q.Difficulty is not ("easy" or "medium" or "hard") || string.IsNullOrWhiteSpace(q.Explanation))
                throw new InvalidOperationException("Missing difficulty or explanation: " + q.Key);
            if (!package.Topics.Any(t => t.Key == q.TopicKey && t.Course == q.Course) || !package.Sections.Any(s => s.Key == q.SectionKey))
                throw new InvalidOperationException("Broken question source mapping: " + q.Key);
            if (q.Options.Length != 4 || q.Options.Distinct().Count() != 4 || q.CorrectAnswer is < 0 or > 3 || q.Text.Length > 8000 || q.Explanation.Length > 8000)
                throw new InvalidOperationException("Invalid question: " + q.Key);
        }
    }
}
