using Cent.Module.Curriculum;
using Microsoft.EntityFrameworkCore;

namespace Cent.Module;

public sealed record ContentCounts(int Chapters, int Topics, int Lessons, int Exercises, int Questions);
public sealed record SubjectCatalogItem(string SubjectId, string Title, string EnglishTitle, string Description,
    Guid? TrainingId, long PriceRial, string Currency, int AccessMonths, bool Available, string Status,
    ContentCounts Published, ContentCounts Reviewed, ContentCounts Draft, string ContentReleaseId);

public static class CentSubjectCatalog
{
    public static string Title(string id) => id switch {
        "reading" => "درک مطلب انگلیسی", "math" => "ریاضی", "logic" => "استدلال متنی و تحلیل داده",
        "biology" => "زیست‌شناسی", "chemistry" => "شیمی", "physics" => "فیزیک", _ => id
    };
    // Counts are always computed from imported rows and their publication/review
    // companions, never from authoring totals or marketing constants.
    public static async Task<SubjectCatalogItem[]> Read(CentDbContext db, CancellationToken ct)
    {
        var corpus = CentCurriculumImporter.Bundled();
        var release = corpus.Release!;
        var stored = await db.Set<CentCurriculumRevision>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == release.Id, ct);
        var verifiedRelease = stored?.PayloadJson == System.Text.Json.JsonSerializer.Serialize(release, CurriculumReleases.Json);
        var courses = await db.Courses.AsNoTracking().ToListAsync(ct);
        var offers = await db.Set<CentSubjectOffer>().AsNoTracking().ToListAsync(ct);
        var products = await db.Products.AsNoTracking().ToListAsync(ct);
        var trainings = await db.Trainings.AsNoTracking().ToListAsync(ct);
        var docs = await db.Documents.AsNoTracking().Where(d => d.SourceId != null).ToListAsync(ct);
        var sources = await db.Sources.AsNoTracking().Select(s => new { s.Id, s.CourseCode }).ToListAsync(ct);
        var chapters = await db.Chapters.AsNoTracking().ToListAsync(ct);
        var topics = await db.Topics.AsNoTracking().ToListAsync(ct);
        var lessons = await db.SourceSections.AsNoTracking().Select(s => new { s.Id, s.SourceId, s.TopicId, s.ImportKey }).ToListAsync(ct);
        var exercises = await db.SourceExercises.AsNoTracking().Select(e => new { e.Id, e.SourceSectionId, e.ImportKey, e.Status }).ToListAsync(ct);
        var questions = await db.Questions.AsNoTracking().Select(q => new { q.Id, q.Subject, q.SourceSectionId, q.Active, q.ImportKey }).ToListAsync(ct);
        var reviewedLessons = release.Resources.Where(r => r.Kind == "lesson" && r.ReviewStatus is "academically-reviewed" or "agent-reviewed").Select(r => CurriculumIdentity.Id("section:" + r.Key)).ToHashSet();
        var reviewedExercises = release.Resources.Where(r => r.Kind == "exercise" && r.ReviewStatus is "academically-reviewed" or "agent-reviewed").Select(r => CurriculumIdentity.Id("exercise:" + r.Key)).ToHashSet();
        var reviewedQuestions = release.Reviews.Where(r => r.Status == "academically-reviewed").Select(r => CurriculumIdentity.Id("question:" + r.Key)).ToHashSet();
        return CurriculumIdentity.Courses.Select(subject => {
            var course = courses.SingleOrDefault(c => c.Code == subject.Code);
            var offer = offers.SingleOrDefault(o => o.SubjectId == subject.Code);
            var product = products.SingleOrDefault(p => p.Id == offer?.ProductId);
            var sourceIds = sources.Where(s => s.CourseCode == subject.Code).Select(s => s.Id).ToHashSet();
            var publishedSources = docs.Where(d => d.Published && d.TrainingId == course?.TrainingId &&
                trainings.Any(t => t.Id == d.TrainingId && t.Published)).Select(d => d.SourceId!.Value).ToHashSet();
            var pubLessons = lessons.Where(l => sourceIds.Contains(l.SourceId) && publishedSources.Contains(l.SourceId)).ToArray();
            var pubIds = pubLessons.Select(l => l.Id).ToHashSet();
            var rs = verifiedRelease ? pubLessons.Where(l => reviewedLessons.Contains(l.Id)).ToArray() : [];
            var rt = rs.Where(l => l.TopicId != null).Select(l => l.TopicId!.Value).ToHashSet();
            var pubCh = chapters.Where(c => c.CourseCode == subject.Code && publishedSources.Contains(c.SourceId)).ToArray();
            var pubChIds = pubCh.Select(c => c.Id).ToHashSet();
            var pubTopics = topics.Where(t => t.CourseCode == subject.Code && pubChIds.Contains(t.ChapterId)).ToArray();
            var published = new ContentCounts(pubCh.Length, pubTopics.Length, pubLessons.Length,
                exercises.Count(e => pubIds.Contains(e.SourceSectionId) && e.Status != "quarantined"),
                questions.Count(q => q.Subject == subject.Code && q.Active && q.SourceSectionId is { } id && pubIds.Contains(id)));
            var reviewed = new ContentCounts(pubCh.Count(ch => pubTopics.Any(t => t.ChapterId == ch.Id && rt.Contains(t.Id))),
                pubTopics.Count(t => rt.Contains(t.Id)), rs.Length,
                verifiedRelease ? exercises.Count(e => pubIds.Contains(e.SourceSectionId) && e.Status != "quarantined" && reviewedExercises.Contains(e.Id)) : 0,
                verifiedRelease ? questions.Count(q => q.Subject == subject.Code && q.Active && q.SourceSectionId is { } id && pubIds.Contains(id) && reviewedQuestions.Contains(q.Id)) : 0);
            var allIds = lessons.Where(l => sourceIds.Contains(l.SourceId)).Select(l => l.Id).ToHashSet();
            var draft = new ContentCounts(chapters.Count(c => c.CourseCode == subject.Code) - published.Chapters,
                topics.Count(t => t.CourseCode == subject.Code) - published.Topics, allIds.Count - published.Lessons,
                exercises.Count(e => allIds.Contains(e.SourceSectionId)) - published.Exercises,
                questions.Count(q => q.Subject == subject.Code) - published.Questions);
            var available = course?.Active == true && offer is { Available: true, ReviewedAt: not null, ReviewedBy: not null } &&
                offer.ContentReleaseId == release.Id && product is { Active: true, Kind: "SUBJECT" } && product.TrainingId == course.TrainingId && product.PriceRial == CentSubjectPricing.BaseRial && reviewed.Lessons > 0 && reviewed.Questions > 0;
            return new SubjectCatalogItem(subject.Code, Title(subject.Code),
                subject.Code == "logic" ? "Reasoning on Texts and Data" : subject.EnglishTitle,
                "درس‌نامه‌های فارسی و تمرین‌های انگلیسی " + Title(subject.Code) + "؛ دامنهٔ قابل خرید فقط منابع بازبینی‌شدهٔ منتشرشده است و پوشش کامل سرفصل تضمین نمی‌شود.",
                course?.TrainingId, CentSubjectPricing.BaseRial, "IRR", 12, available,
                available ? "reviewed-published" : course is null ? "unavailable" : "review-pending", published, reviewed, draft, release.Id);
        }).ToArray();
    }

    public static async Task PrepareOffers(CentDbContext db, CancellationToken ct = default)
    {
        foreach (var (code, title, _) in CurriculumIdentity.Courses) {
            var course = await db.Courses.FindAsync([code], ct);
            if (course?.TrainingId is null || await db.Set<CentSubjectOffer>().AnyAsync(o => o.SubjectId == code, ct)) continue;
            var product = new CentProduct { Id = CurriculumIdentity.Id("subject-offer-v1:" + code), Title = Title(code) + " · ۱۲ ماه", Kind = "SUBJECT", TrainingId = course.TrainingId, PriceRial = CentSubjectPricing.BaseRial };
            db.Products.Add(product);
            db.Set<CentSubjectOffer>().Add(new() { SubjectId = code, ProductId = product.Id, ContentReleaseId = CurriculumReleases.BundledReleaseId });
        }
        if (!await db.Set<CentRecommendedBundle>().AnyAsync(b => b.Id == "complete-six-v1", ct)) db.Set<CentRecommendedBundle>().Add(new() {
            Id = "complete-six-v1", Title = "آمادگی هر شش درس", SubjectsJson = System.Text.Json.JsonSerializer.Serialize(CurriculumIdentity.Courses.Select(c => c.Code)),
            Rationale = "انتخاب هر شش درسِ ساختار فعلی آزمون؛ قابل شخصی‌سازی، بدون تخفیف اضافه.", Evidence = "Existing current six-section CEnT blueprint; no degree-specific admission claim.", Active = true
        });
        await db.SaveChangesAsync(ct);
    }
}
