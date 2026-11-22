using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cent.Module.Exams;
using Microsoft.EntityFrameworkCore;

namespace Cent.Module.Curriculum;

public sealed record SyllabusNode(string Key, string Subject, string Title, string SourceUrl, string Locator, string[] Competencies);
public sealed record TopicAlignment(string TopicKey, string[] NodeKeys, string Status, string Note, string? ParentTopicKey = null);
public sealed record ResourceAlignment(string Kind, string Key, string Subject, string TopicKey, string Revision, string Language, string ReviewStatus);
public sealed record QuestionReview(string Key, string Revision, string Status, string Reviewer, string Evidence, string Rights,
    string? GroupId = null, string? PassageId = null);
public sealed record SharedPassage(string Id, string Subject, string Genre, string Level, string Text, string Rights, string Source, string ReviewStatus);
public sealed record QuestionGroup(string Id, string Subject, string PassageId, string[] QuestionKeys);
public sealed record CurriculumRelease(string Id, string SyllabusRevisionId, string SourceUrl, DateTimeOffset RetrievedAt,
    SyllabusNode[] Nodes, TopicAlignment[] Topics, ResourceAlignment[] Resources, QuestionReview[] Reviews,
    SharedPassage[] Passages, QuestionGroup[] Groups);

// Companions over existing course/topic/source/document/question IDs, not a second CMS.
// JSON retains immutable mappings and passage/group revisions without touching legacy rows.
public sealed class CentCurriculumRevision
{
    public string Id { get; set; } = "";
    public string SyllabusRevisionId { get; set; } = "";
    public string PackageHash { get; set; } = "";
    public string PayloadJson { get; set; } = "";
}
public sealed class CentPracticeContent
{
    public Guid AttemptId { get; set; }
    public string ReleaseId { get; set; } = "";
    public string PassagesJson { get; set; } = "[]";
}
public sealed record PracticePassage(string Id, string Text);
public sealed record PracticeSelection(CentQuestion[] Questions, PracticePassage[] Passages, Dictionary<Guid, string> PassageIds);

public static class CurriculumReleases
{
    public const string BundledReleaseId = "cent-content-2026-r1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static void Configure(ModelBuilder b)
    {
        b.Entity<CentCurriculumRevision>(e => {
            e.ToTable("cent_curriculum_revisions"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(100); e.Property(x => x.SyllabusRevisionId).HasMaxLength(100);
            e.Property(x => x.PackageHash).HasMaxLength(64).IsUnicode(false); e.HasIndex(x => x.PackageHash).IsUnique();
            e.HasOne<CentSyllabusRevision>().WithMany().HasForeignKey(x => x.SyllabusRevisionId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentPracticeContent>(e => {
            e.ToTable("cent_practice_content"); e.HasKey(x => x.AttemptId);
            e.Property(x => x.ReleaseId).HasMaxLength(100);
            e.HasOne<CentCurriculumRevision>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentAttempt>().WithOne().HasForeignKey<CentPracticeContent>(x => x.AttemptId).OnDelete(DeleteBehavior.NoAction);
        });
    }
    public static void Validate(Corpus p)
    {
        var release = p.Release;
        if (release is null) return; // Original four-option packages remain valid.
        void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        Need(!string.IsNullOrWhiteSpace(release.Id) && !string.IsNullOrWhiteSpace(release.SyllabusRevisionId), "Missing release identity.");
        Need(Uri.TryCreate(release.SourceUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https" && release.RetrievedAt != default, "Missing syllabus provenance.");
        Need(release.Nodes.Select(n => n.Key).Distinct().Count() == release.Nodes.Length, "Duplicate syllabus node.");
        var topics = p.Topics.ToDictionary(t => t.Key);
        var nodes = release.Nodes.ToDictionary(n => n.Key);
        Need(release.Topics.Select(t => t.TopicKey).Distinct().Count() == release.Topics.Length, "Duplicate topic mapping.");
        foreach (var t in release.Topics) {
            Need(topics.ContainsKey(t.TopicKey), "Unknown topic: " + t.TopicKey);
            Need(t.NodeKeys.All(k => nodes.TryGetValue(k, out var n) && n.Subject == topics[t.TopicKey].Course), "Wrong subject in syllabus mapping.");
            Need(t.Status is "valid" or "requires-correction" or "requires-expansion" or "missing" or "out-of-scope" or "requires-source-verification", "Unknown coverage status.");
            Need(t.ParentTopicKey is null || (topics.TryGetValue(t.ParentTopicKey, out var parent) && parent.Course == topics[t.TopicKey].Course && parent.Key != t.TopicKey), "Invalid subtopic parent.");
            var visited = new HashSet<string> { t.TopicKey }; var parentKey = t.ParentTopicKey;
            while (parentKey is not null) {
                Need(visited.Add(parentKey), "Cyclic subtopic relationship.");
                parentKey = release.Topics.SingleOrDefault(x => x.TopicKey == parentKey)?.ParentTopicKey;
            }
        }
        Need(release.Topics.Length == p.Topics.Count, "Every topic needs an explicit coverage classification.");
        var qs = p.Questions.ToDictionary(q => q.Key);
        Need(release.Reviews.Select(r => r.Key).Distinct().Count() == release.Reviews.Length, "Duplicate review.");
        Need(release.Passages.Select(x => x.Id).Distinct().Count() == release.Passages.Length, "Duplicate passage.");
        Need(release.Groups.Select(x => x.Id).Distinct().Count() == release.Groups.Length, "Duplicate group.");
        foreach (var resource in release.Resources) {
            Need(topics.TryGetValue(resource.TopicKey, out var topic) && topic.Course == resource.Subject, "Invalid resource topic.");
            Need(resource.Kind switch {
                "lesson" => p.Sections.Any(s => s.Key == resource.Key && s.TopicKey == resource.TopicKey),
                "question" => qs.TryGetValue(resource.Key, out var q) && q.TopicKey == resource.TopicKey,
                "exercise" => p.Exercises.Any(x => x.Key == resource.Key && x.TopicKey == resource.TopicKey),
                _ => false
            }, "Unknown resource mapping: " + resource.Key);
            Need(!string.IsNullOrWhiteSpace(resource.Revision) && resource.Language is "fa" or "en", "Missing resource revision/language.");
            if (resource.Kind == "lesson") Need(resource.Language == "fa" && ContainsPersian(p.Sections.Single(s => s.Key == resource.Key).Text), "Teaching must be Persian.");
            if (resource.Kind == "exercise") {
                var ex = p.Exercises.Single(e => e.Key == resource.Key);
                Need(resource.Language == "en" && !ContainsPersian(ex.Text + string.Join(" ", ex.Options) + ex.Solution), "Assessment language must be English.");
            }
        }
        Need(release.Resources.Select(r => (r.Kind, r.Key)).Distinct().Count() == release.Resources.Length, "Duplicate resource mapping.");
        Need(release.Reviews.Length == p.Questions.Count, "Every question needs an explicit review status.");
        foreach (var review in release.Reviews) {
            Need(qs.ContainsKey(review.Key), "Review references missing question.");
            Need((review.GroupId is null) == (review.PassageId is null), "Incomplete passage dependency.");
            if (review.Status == "academically-reviewed") {
                var q = qs[review.Key];
                Need(q.Options.Length == ExamDefinition.OptionCount && !string.IsNullOrWhiteSpace(review.Evidence) &&
                    !string.IsNullOrWhiteSpace(review.Reviewer) && !string.IsNullOrWhiteSpace(review.Rights), "Incomplete academic review.");
                Need(!ContainsPersian(q.Text + string.Join(" ", q.Options) + q.Explanation), "Assessment language must be English.");
                Need(release.Topics.Any(t => t.TopicKey == q.TopicKey && t.Status == "valid" && t.NodeKeys.Length > 0) &&
                    release.Resources.Any(r => r.Kind == "question" && r.Key == q.Key && r.Revision == review.Revision), "Reviewed question lacks valid syllabus/revision mapping.");
            }
            if (review.GroupId is not null) Need(release.Groups.Any(g => g.Id == review.GroupId && g.PassageId == review.PassageId && g.QuestionKeys.Contains(review.Key)), "Orphan dependent question.");
        }
        var used = new HashSet<string>();
        foreach (var g in release.Groups) {
            Need(g.QuestionKeys.Length > 0 && g.QuestionKeys.Distinct().Count() == g.QuestionKeys.Length, "Empty/duplicate group.");
            Need(release.Passages.Any(p => p.Id == g.PassageId && p.Subject == g.Subject && p.Text.Length > 100 &&
                !ContainsPersian(p.Text) && !string.IsNullOrWhiteSpace(p.Rights) && !string.IsNullOrWhiteSpace(p.Source)), "Missing/unreviewed group passage.");
            foreach (var key in g.QuestionKeys) Need(used.Add(key) && qs.TryGetValue(key, out var q) && q.Course == g.Subject &&
                release.Reviews.Any(r => r.Key == key && r.GroupId == g.Id && r.PassageId == g.PassageId), "Invalid group member.");
        }
    }
    public static bool ContainsPersian(string s) => s.Any(c => c is >= '\u0600' and <= '\u06ff');

    public static ExamContentManifest Manifest(Corpus p, string examVersionId)
    {
        CentCurriculumImporter.Validate(p);
        var r = p.Release ?? throw new InvalidOperationException("Versioned release required.");
        var reviews = r.Reviews.Where(x => x.Status == "academically-reviewed").ToDictionary(x => x.Key);
        var eligible = p.Questions.Where(q => reviews.ContainsKey(q.Key)).ToArray();
        var questions = eligible.Select(q => {
            var review = reviews[q.Key];
            return new ExamQuestion(CurriculumIdentity.Id("question:" + q.Key), review.Revision, q.Course,
                CurriculumIdentity.Id("topic:" + q.TopicKey), q.Difficulty, q.Text,
                q.Options.Select((text, i) => new ExamOption(((char)('a' + i)).ToString(), text)).ToArray(),
                ((char)('a' + q.CorrectAnswer)).ToString(), q.Explanation, review.GroupId, review.PassageId, q.FigureSvg);
        }).ToArray();
        var groups = r.Groups.Where(g => g.QuestionKeys.All(reviews.ContainsKey)).Select(g =>
            new ExamGroup(g.Id, g.Subject, g.PassageId, g.QuestionKeys.Select(k => reviews[k].Revision).ToArray())).ToArray();
        var passages = r.Passages.Where(p => groups.Any(g => g.PassageRevisionId == p.Id)).Select(p => new ExamPassage(p.Id, p.Text)).ToArray();
        var definition = examVersionId == CentExamBlueprints.Current.ExamVersionId ? CentExamBlueprints.Current :
            examVersionId == CentExamBlueprints.Historical.ExamVersionId ? CentExamBlueprints.Historical : throw new InvalidOperationException("Unknown blueprint.");
        var subjects = definition.Sections.Select(s => s.SubjectId).ToHashSet();
        questions = questions.Where(q => subjects.Contains(q.SubjectId)).ToArray();
        groups = groups.Where(g => subjects.Contains(g.SubjectId)).ToArray();
        passages = passages.Where(p => groups.Any(g => g.PassageRevisionId == p.PassageRevisionId)).ToArray();
        return new(examVersionId, r.SyllabusRevisionId, questions, passages, groups,
            questions.Select(q => new ExamEligibility(q.QuestionRevisionId, definition.Sections.Single(s => s.SubjectId == q.SubjectId).SectionKey, "eligible")).ToArray());
    }

    // Flat legacy candidates remain round-robin selected by CentPractice. Dependent groups
    // are indivisible and snapshotted with a passage exactly once; topic filters cannot orphan siblings.
    public static PracticeSelection SelectPractice(CentQuestion[] ordered, CurriculumRelease? r, int limit = 20)
    {
        var ids = ordered.Select(q => q.Id).ToHashSet();
        var groupByQuestion = r?.Groups.SelectMany(g => g.QuestionKeys.Select(k => (Id: CurriculumIdentity.Id("question:" + k), Group: g)))
            .ToDictionary(x => x.Id, x => x.Group) ?? [];
        var selected = new List<CentQuestion>(); var seen = new HashSet<Guid>(); var passages = new Dictionary<string, PracticePassage>();
        var dependencies = new Dictionary<Guid, string>();
        foreach (var q in ordered) {
            if (seen.Contains(q.Id)) continue;
            if (!groupByQuestion.TryGetValue(q.Id, out var group)) {
                if (selected.Count < limit) { seen.Add(q.Id); selected.Add(q); }
                continue;
            }
            var members = group.QuestionKeys.Select(k => CurriculumIdentity.Id("question:" + k)).ToArray();
            if (members.Any(id => !ids.Contains(id)) || selected.Count + members.Length > limit) continue;
            var passage = r!.Passages.Single(p => p.Id == group.PassageId);
            passages.TryAdd(passage.Id, new(passage.Id, passage.Text));
            foreach (var id in members) { seen.Add(id); selected.Add(ordered.Single(q => q.Id == id)); dependencies[id] = passage.Id; }
        }
        return new(selected.ToArray(), passages.Values.ToArray(), dependencies);
    }
}
