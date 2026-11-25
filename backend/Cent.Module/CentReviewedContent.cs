using System.Data;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cent.Module;

// Private, compiled handoff. No ZIP, provenance, keys or lesson HTML is a web resource.
public static class CentReviewedContent
{
    public sealed record Change(string Entity, Guid Id, Dictionary<string, JsonElement>? Before, Dictionary<string, JsonElement> After);
    public sealed record Assessment(string Id, string Subject, string Title, int DurationMinutes, Guid[] QuestionIds, string[] PassageIds);
    public sealed record Passage(string Id, string Text, Guid[] QuestionIds, string[] QuestionKeys);
    public sealed record Mapping(string Subject, Guid OldId, Guid[] NewIds);
    public sealed record Catalog(string Version, Change[] Changes, Assessment[] Assessments, Passage[] Passages, Mapping[] SectionMappings, CentQuestion[] QuestionBank, Guid[][] VariantGroups, Guid[] ArchivedChapterIds, Guid[] ArchivedTopicIds, Guid[] ArchivedSourceIds);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<(Catalog Catalog, string Hash)> Package = new(() => {
        using var stream = typeof(CentReviewedContent).Assembly.GetManifestResourceStream("Cent.Module.Content.reviewed-v4.json")!;
        using var reader = new StreamReader(stream); var body = reader.ReadToEnd();
        return (JsonSerializer.Deserialize<Catalog>(body, Json)!, CentModule.Hash(body));
    });
    public static Catalog Content => Package.Value.Catalog;
    public static string Hash => Package.Value.Hash;
    internal static async Task LockImport(CentDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer()) return;
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='Cent.ReviewedContent.Import',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=30000; SELECT @r;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) < 0) throw new InvalidOperationException("Another Cent content import is running; retry later.");
    }
    private static readonly Dictionary<string, Type> Types = new() {
        ["sources"] = typeof(CentSource), ["chapters"] = typeof(CentChapter), ["topics"] = typeof(CentTopic),
        ["sections"] = typeof(CentSourceSection), ["exercises"] = typeof(CentSourceExercise),
        ["questions"] = typeof(CentQuestion), ["documents"] = typeof(CentDocument)
    };

    // Caller owns the transaction. Strict comparison refuses local editorial changes;
    // field equality accepts the exact old or exact reviewed value for safe recovery.
    public static async Task Apply(CentDbContext db, CancellationToken ct)
    {
        if (await db.CurriculumImports.AnyAsync(i => i.PackageHash == Hash, ct)) return;
        foreach (var kind in Types.Keys) {
            foreach (var change in Content.Changes.Where(c => c.Entity == kind)) {
                var type = Types[kind];
                var row = await db.FindAsync(type, [change.Id], ct);
                if (row is null) {
                    if (change.Before is not null) throw new InvalidOperationException($"Missing reviewed-content target: {kind}/{change.Id}");
                    row = Activator.CreateInstance(type)!; type.GetProperty("Id")!.SetValue(row, change.Id); db.Add(row);
                } else if (change.Before is null) {
                    foreach (var field in change.After)
                        RequireEqual(type.GetProperty(field.Key)!, row, field.Value, kind, change.Id);
                    continue;
                }
                foreach (var field in change.After) {
                    var property = type.GetProperty(field.Key) ?? throw new InvalidOperationException("Unknown content field: " + field.Key);
                    if (change.Before is not null && !Equal(property, row, field.Value))
                        RequireEqual(property, row, change.Before[field.Key], kind, change.Id);
                    property.SetValue(row, field.Value.Deserialize(property.PropertyType, Json));
                }
            }
            await db.SaveChangesAsync(ct); // Foreign-key dependency order; one transaction.
        }
        db.CurriculumImports.Add(new CentCurriculumImport { PackageHash = Hash, SourceCount = Content.Changes.Count(c => c.Entity == "sources"),
            TopicCount = Content.Changes.Count(c => c.Entity == "topics"), QuestionCount = Content.Changes.Count(c => c.Entity == "questions") });
        db.Audit.Add(new CentAuditEvent { Action = "REVIEWED_CONTENT_IMPORT", EntityId = Guid.Empty, ActorId = Guid.Empty });
        await db.SaveChangesAsync(ct);
    }
    private static bool Equal(PropertyInfo property, object row, JsonElement expected)
    {
        var current = property.GetValue(row);
        var incoming = expected.Deserialize(property.PropertyType, Json);
        if (property.Name == "OptionsJson" && current is string a && incoming is string b)
            return JsonSerializer.Deserialize<string[]>(a)!.SequenceEqual(JsonSerializer.Deserialize<string[]>(b)!);
        return Equals(current, incoming);
    }
    private static void RequireEqual(PropertyInfo property, object row, JsonElement expected, string kind, Guid id)
    {
        if (!Equal(property, row, expected)) throw new InvalidOperationException($"Reviewed-content conflict: {kind}/{id}/{property.Name}; transaction rolled back.");
    }
    public static async Task<bool> Imported(CentDbContext db, CancellationToken ct) => await db.CurriculumImports.AnyAsync(i => i.PackageHash == Hash, ct);
}
