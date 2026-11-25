using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Cent.Module;

// Independent Cent switches. No environment or deployment defaults enable rollout.
public static class CentReaderRollout
{
    public static readonly string[] Subjects = ["math", "physics", "chemistry", "biology", "logic", "reading"];
    public static bool Subject(IConfiguration config, string? subject) => subject is not null && Subjects.Contains(subject) &&
        config.GetValue<bool>("Cent:Reader:Enabled") && (config.GetValue<bool>("Cent:Reader:AllSubjects") ||
        (config["Cent:Reader:Subjects"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(subject, StringComparer.OrdinalIgnoreCase));

    public static async Task<bool> General(CentDbContext db, IConfiguration config, CentDocument document, CancellationToken ct)
    {
        var course = document.SourceId is { } source
            ? await db.Sources.Where(s => s.Id == source).Select(s => s.CourseCode).SingleOrDefaultAsync(ct)
            : await db.Courses.Where(c => c.TrainingId == document.TrainingId).Select(c => c.Code).SingleOrDefaultAsync(ct);
        return Subject(config, course);
    }
    public static async Task<bool> Granular(CentDbContext db, IConfiguration config, CentDocument document, CancellationToken ct) =>
        await General(db, config, document, ct) || (CentReaderPilot.Enabled(config) && document.SourceId is { } source &&
        await db.SourceSections.AnyAsync(s => s.SourceId == source && s.ImportKey == CentReaderPilot.SectionKey, ct));
}
