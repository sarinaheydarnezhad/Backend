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
    public static Corpus LegacyBundled()
    {
        var assembly = typeof(CentCurriculumImporter).Assembly;
        using var stream = assembly.GetManifestResourceStream("Cent.Module.Content.curriculum.json") ?? throw new InvalidOperationException("The curriculum package is missing.");
        return JsonSerializer.Deserialize<Corpus>(stream, Json) ?? throw new InvalidOperationException("The curriculum package is empty.");
    }

    public static Corpus Bundled()
    {
        var legacy = LegacyBundled();
        using var stream = typeof(CentCurriculumImporter).Assembly.GetManifestResourceStream("Cent.Module.Content.curriculum-2026.json");
        if (stream is null) return legacy;
        var addition = JsonSerializer.Deserialize<Corpus>(stream, Json) ?? throw new InvalidOperationException("Empty content release.");
        var first = Merge(legacy, addition);
        using var expansion = typeof(CentCurriculumImporter).Assembly.GetManifestResourceStream("Cent.Module.Content.curriculum-2026-r2.json");
        return expansion is null ? first : Merge(first, JsonSerializer.Deserialize<Corpus>(expansion, Json) ?? throw new InvalidOperationException("Empty expansion."));
    }
    public static Corpus Merge(Corpus original, Corpus addition) => new(
        [.. original.Sources, .. addition.Sources], [.. original.Chapters, .. addition.Chapters],
        [.. original.Topics, .. addition.Topics], [.. original.Sections, .. addition.Sections], [.. original.Exercises, .. addition.Exercises])
        { Questions = [.. original.Questions, .. addition.Questions], Findings = [.. original.Findings, .. addition.Findings], Release = addition.Release };

    public async Task<CurriculumImportReport> Import(CancellationToken ct = default)
    {
        var assembly = typeof(CentCurriculumImporter).Assembly;
        var package = Bundled();
        var hash = CurriculumReleases.Hash(JsonSerializer.Serialize(package, Json));
        var report = new CurriculumImportReport(hash, package.Sources.Count, package.Chapters.Count, package.Topics.Count,
            package.Exercises.Count, package.Questions.Count, package.Questions.Count(q => q.Origin == "authored"), false);
        if (await db.CurriculumImports.AnyAsync(i => i.PackageHash == hash, ct)) return report with { AlreadyImported = true };
        Validate(package);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var trainingIds = await db.Trainings.Select(t => t.Id).ToListAsync(ct);
        foreach (var (code, title, english) in CurriculumIdentity.Courses) {
            var trainingId = CurriculumIdentity.Id("training:" + code);
            if (!trainingIds.Contains(trainingId)) db.Trainings.Add(new CentTraining {
                Id = trainingId, Title = title + " — منابع اصلی CEnT", Published = true, IncludedInSubscription = code != "reading",
                Description = "درس‌نامه‌ها و تمرین‌های منبع اصلی " + title + ". دسترسی خریداری‌شده در محیط مطالعه ارائه می‌شود."
            });
            var course = await db.Courses.FindAsync([code], ct);
            if (course is null) db.Courses.Add(new CentCourse { Code = code, Title = title, EnglishTitle = english,
                SortOrder = code == "reading" ? -1 : Array.FindIndex(CurriculumIdentity.Courses, c => c.Code == code) - 1,
                TrainingId = trainingId, Active = true }); // Never overwrite existing publication/access choices.
        }
        await db.SaveChangesAsync(ct);
        foreach (var source in package.Sources) {
            var id = CurriculumIdentity.Id("source:" + source.Key);
            var row = await db.Sources.FindAsync([id], ct);
            if (row is not null) { RequireSame(row.Sha256, source.Sha256, source.Key); continue; }
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
            if (row is not null) { RequireSame(new { row.Title, row.CourseCode, row.SortOrder }, new { chapter.Title, CourseCode = chapter.Course, chapter.SortOrder }, chapter.Key); continue; }
            if (row is null) { row = new CentChapter { Id = id, ImportKey = chapter.Key }; db.Chapters.Add(row); }
            row.CourseCode = chapter.Course; row.SourceId = CurriculumIdentity.Id("source:" + chapter.Key.Split('#')[0]); row.Title = chapter.Title; row.SortOrder = chapter.SortOrder;
        }
        await db.SaveChangesAsync(ct);
        foreach (var topic in package.Topics) {
            var id = CurriculumIdentity.Id("topic:" + topic.Key);
            var row = await db.Topics.FindAsync([id], ct);
            if (row is not null) { RequireSame(new { row.Title, row.EnglishTitle, row.CourseCode, row.ChapterId, row.Foundation, row.SortOrder },
                new { topic.Title, topic.EnglishTitle, CourseCode = topic.Course, ChapterId = CurriculumIdentity.Id("chapter:" + topic.ChapterKey), topic.Foundation, topic.SortOrder }, topic.Key); continue; }
            if (row is null) { row = new CentTopic { Id = id, ImportKey = topic.Key }; db.Topics.Add(row); }
            row.ChapterId = CurriculumIdentity.Id("chapter:" + topic.ChapterKey); row.CourseCode = topic.Course;
            row.Title = topic.Title; row.EnglishTitle = topic.EnglishTitle; row.SortOrder = topic.SortOrder; row.Foundation = topic.Foundation;
        }
        await db.SaveChangesAsync(ct);
        foreach (var section in package.Sections) {
            var id = CurriculumIdentity.Id("section:" + section.Key);
            var row = await db.SourceSections.FindAsync([id], ct);
            if (row is not null) { RequireSame(new { row.Text, row.Html, row.Heading, row.SortOrder, row.TopicId, row.SourceId },
                new { section.Text, section.Html, section.Heading, section.SortOrder, TopicId = section.TopicKey is null ? (Guid?)null : CurriculumIdentity.Id("topic:" + section.TopicKey), SourceId = CurriculumIdentity.Id("source:" + section.SourceKey) }, section.Key); continue; }
            if (row is null) { row = new CentSourceSection { Id = id, ImportKey = section.Key }; db.SourceSections.Add(row); }
            row.SourceId = CurriculumIdentity.Id("source:" + section.SourceKey); row.TopicId = section.TopicKey is null ? null : CurriculumIdentity.Id("topic:" + section.TopicKey);
            row.Heading = section.Heading; row.SortOrder = section.SortOrder; row.Text = section.Text; row.Html = section.Html;
        }
        await db.SaveChangesAsync(ct);
        foreach (var exercise in package.Exercises) {
            var id = CurriculumIdentity.Id("exercise:" + exercise.Key);
            var row = await db.SourceExercises.FindAsync([id], ct);
            if (row is not null) { RequireSame(new { row.Text, row.OptionsJson, row.OriginalCorrectAnswer, row.Solution },
                new { exercise.Text, OptionsJson = JsonSerializer.Serialize(exercise.Options), OriginalCorrectAnswer = exercise.CorrectAnswer, exercise.Solution }, exercise.Key); continue; }
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
            if (row is not null) { RequireSame(new { row.Text, row.OptionsJson, row.CorrectAnswer, row.Explanation, row.Subject, row.TopicId, row.FigureSvg, row.Difficulty },
                new { question.Text, OptionsJson = JsonSerializer.Serialize(question.Options), question.CorrectAnswer, question.Explanation,
                    Subject = question.Course, TopicId = (Guid?)CurriculumIdentity.Id("topic:" + question.TopicKey), question.FigureSvg, question.Difficulty }, question.Key); continue; }
            if (row is null) { row = new CentQuestion { Id = id, ImportKey = question.Key }; db.Questions.Add(row); }
            row.Subject = question.Course; row.TopicId = CurriculumIdentity.Id("topic:" + question.TopicKey);
            row.Topic = package.Topics.Single(t => t.Key == question.TopicKey).Title;
            row.SourceSectionId = CurriculumIdentity.Id("section:" + question.SectionKey);
            row.SourceExerciseId = question.ExerciseKey is null ? null : CurriculumIdentity.Id("exercise:" + question.ExerciseKey);
            row.Text = question.Text; row.OptionsJson = JsonSerializer.Serialize(question.Options); row.CorrectAnswer = question.CorrectAnswer;
            row.Explanation = question.Explanation; row.Difficulty = question.Difficulty; row.Origin = question.Origin;
            row.FigureSvg = question.FigureSvg; row.Active = true;
            // New content enters only the explicit passage-aware Practice contract.
            // Legacy diagnostic/simulator selection and snapshots retain the original bank.
            row.Kind = question.Origin == "authored-2026-reviewed" ? "practice" : "both";
        }
        foreach (var source in package.Sources) {
            var id = CurriculumIdentity.Id("document:" + source.Key);
            var document = await db.Documents.FindAsync([id], ct);
            if (document is not null) continue; // Preserve published pages, notes, ordering and flags.
            if (document is null) { document = new CentDocument { Id = id }; db.Documents.Add(document); }
            document.SourceId = CurriculumIdentity.Id("source:" + source.Key); document.TrainingId = CurriculumIdentity.Id("training:" + source.Course);
            document.Title = source.Title.Length <= 200 ? source.Title : source.Title[..200];
            document.PageCount = package.Sections.Count(s => s.SourceKey == source.Key); document.Published = true;
            document.SortOrder = package.Chapters.Where(ch => ch.Key.Split('#')[0] == source.Key).Min(ch => ch.SortOrder);
        }
        if (package.Release is { } release) {
            var evidence = JsonSerializer.Serialize(new { release.Nodes, release.SourceUrl, release.RetrievedAt }, Json);
            var syllabus = await db.SyllabusRevisions.FindAsync([release.SyllabusRevisionId], ct);
            if (syllabus is null) db.SyllabusRevisions.Add(new Exams.CentSyllabusRevision { Id = release.SyllabusRevisionId,
                SourceUrl = release.SourceUrl, RetrievedAt = release.RetrievedAt, EvidenceJson = evidence });
            else RequireSame(syllabus.EvidenceJson, evidence, release.SyllabusRevisionId);
            var content = await db.CurriculumRevisions.FindAsync([release.Id], ct);
            var payload = JsonSerializer.Serialize(release, Json);
            if (content is null) db.CurriculumRevisions.Add(new CentCurriculumRevision { Id = release.Id,
                SyllabusRevisionId = release.SyllabusRevisionId, PackageHash = hash, PayloadJson = payload });
            else RequireSame(content.PayloadJson, payload, release.Id);
            // Import-ready engine companion. No publication/activation or administrator
            // sign-off is fabricated: ReviewedAt/ReviewedBy deliberately stay null.
            var definition = Exams.CentExamBlueprints.ForContent(release.Id, release.SyllabusRevisionId);
            var manifest = CurriculumReleases.Manifest(package, definition);
            var manifestId = release.Id + ":current";
            var existingManifest = await db.ExamContentReleases.FindAsync([manifestId], ct);
            var manifestJson = Exams.CentExamJson.Write(manifest);
            if (existingManifest is null) db.ExamContentReleases.Add(new Exams.CentExamContentRelease {
                Id = manifestId, ExamVersionId = manifest.ExamVersionId, SyllabusRevisionId = manifest.SyllabusRevisionId,
                ManifestJson = manifestJson });
            else RequireSame(existingManifest.ManifestJson, manifestJson, manifestId);
            var version = await db.ExamVersions.FindAsync([definition.ExamVersionId], ct);
            var definitionJson = Exams.CentExamJson.Write(definition);
            if (version is null) db.ExamVersions.Add(new Exams.CentExamVersion { Id = definition.ExamVersionId,
                ConfigurationRevision = definition.ConfigurationRevision, DefinitionJson = definitionJson,
                SyllabusRevisionId = release.SyllabusRevisionId, ContentReleaseId = manifestId, Status = "DRAFT", AvailableForNewStarts = false });
            else RequireSame(version.DefinitionJson, definitionJson, definition.ExamVersionId);
        }
        db.CurriculumImports.Add(new CentCurriculumImport { PackageHash = hash, SourceCount = package.Sources.Count, TopicCount = package.Topics.Count, QuestionCount = package.Questions.Count });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return report;
    }

    // Pure preflight rule, also used by offline import tests. Changing published payloads
    // requires additive IDs/revisions; repeated imports never overwrite/retire old content.
    public static void RequireSame<T>(T existing, T incoming, string key)
    {
        if (JsonSerializer.Serialize(existing, Json) != JsonSerializer.Serialize(incoming, Json))
            throw new InvalidOperationException("Published content conflict; create an additive revision: " + key);
    }

    public static void Validate(Corpus package)
    {
        if (package.Sources.Count == 0 || package.Questions.Count == 0) throw new InvalidOperationException("The bundled curriculum is empty.");
        void Unique(IEnumerable<string> keys, string kind) { var a = keys.ToArray(); if (a.Any(string.IsNullOrWhiteSpace) || a.Distinct().Count() != a.Length) throw new InvalidOperationException("Duplicate/empty " + kind + " keys."); }
        Unique(package.Sources.Select(x => x.Key), "source"); Unique(package.Sources.Select(x => x.File), "filename");
        Unique(package.Chapters.Select(x => x.Key), "chapter"); Unique(package.Topics.Select(x => x.Key), "topic");
        Unique(package.Sections.Select(x => x.Key), "section"); Unique(package.Exercises.Select(x => x.Key), "exercise");
        Unique(package.Questions.Select(x => x.Key), "question");
        foreach (var s in package.Sources)
            if (!CurriculumIdentity.Courses.Any(c => c.Code == s.Course) || s.File.Contains("..") || s.File.Contains('\\') || Path.IsPathRooted(s.File) || s.Sha256.Length != 64)
                throw new InvalidOperationException("Invalid subject/source path: " + s.Key);
        foreach (var ch in package.Chapters)
            if (!package.Sources.Any(s => s.Key == ch.Key.Split('#')[0] && s.Course == ch.Course)) throw new InvalidOperationException("Broken chapter source: " + ch.Key);
        foreach (var t in package.Topics)
            if (!package.Chapters.Any(ch => ch.Key == t.ChapterKey && ch.Course == t.Course)) throw new InvalidOperationException("Broken topic chapter: " + t.Key);
        foreach (var s in package.Sections)
            if (!package.Sources.Any(x => x.Key == s.SourceKey) || (s.TopicKey is not null && !package.Topics.Any(t => t.Key == s.TopicKey && package.Sources.Any(x => x.Key == s.SourceKey && x.Course == t.Course))))
                throw new InvalidOperationException("Broken section mapping: " + s.Key);
        if (package.Sections.GroupBy(s => (s.SourceKey, s.SortOrder)).Any(g => g.Count() > 1)) throw new InvalidOperationException("Duplicate section position.");
        foreach (var e in package.Exercises)
            if (!package.Sections.Any(s => s.Key == e.SectionKey && s.SourceKey == e.SourceKey && (e.TopicKey is null || s.TopicKey is null || s.TopicKey == e.TopicKey)) ||
                (e.TopicKey is not null && !package.Topics.Any(t => t.Key == e.TopicKey && t.Course == e.Course)) ||
                (e.CorrectAnswer is int answer && (answer < 0 || answer >= e.Options.Length)))
                throw new InvalidOperationException("Invalid exercise: " + e.Key);
        foreach (var q in package.Questions) {
            if (q.Difficulty is not ("easy" or "medium" or "hard") || string.IsNullOrWhiteSpace(q.Explanation))
                throw new InvalidOperationException("Missing difficulty or explanation: " + q.Key);
            if (!package.Topics.Any(t => t.Key == q.TopicKey && t.Course == q.Course) || !package.Sections.Any(s => s.Key == q.SectionKey && package.Sources.Any(x => x.Key == s.SourceKey && x.Course == q.Course)) ||
                (q.ExerciseKey is not null && !package.Exercises.Any(e => e.Key == q.ExerciseKey && e.Course == q.Course)))
                throw new InvalidOperationException("Broken question source mapping: " + q.Key);
            if (q.Options.Length is < 4 or > 5 || q.Options.Any(string.IsNullOrWhiteSpace) || q.Options.Select(x => x.Trim()).Distinct(StringComparer.Ordinal).Count() != q.Options.Length ||
                q.CorrectAnswer < 0 || q.CorrectAnswer >= q.Options.Length || q.Text.Length > 8000 || q.Explanation.Length > 8000)
                throw new InvalidOperationException("Invalid question: " + q.Key);
        }
        if (package.Questions.GroupBy(q => string.Join(" ", q.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Duplicate question stems require explicit editorial resolution.");
        CurriculumReleases.Validate(package);
    }
}
