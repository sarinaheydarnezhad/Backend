using System.Text;
using Cent.Module;
using Cent.Module.Curriculum;
using Cent.Module.Exams;

namespace Cent.Curriculum;

internal static class OfflineRelease
{
    public static void Verify(string? output)
    {
        var package = CentCurriculumImporter.Bundled();
        CentCurriculumImporter.Validate(package);
        var release = package.Release ?? throw new InvalidOperationException("Release missing.");
        var assembly = typeof(CentCurriculumImporter).Assembly;
        foreach (var source in package.Sources) {
            using var input = assembly.GetManifestResourceStream("Cent.Module.Content.Sources." + source.File.Replace('/', '.'))
                ?? throw new InvalidOperationException("Source missing: " + source.File);
            using var bytes = new MemoryStream(); input.CopyTo(bytes);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
            if (actual != source.Sha256) throw new InvalidOperationException("Source hash mismatch: " + source.File);
        }
        var manifest = CurriculumReleases.Manifest(package, CentExamBlueprints.Current.ExamVersionId);
        var definition = CentExamBlueprints.Current with { SyllabusRevisionId = release.SyllabusRevisionId,
            ContentReleaseId = release.Id + ":current", SubmissionPolicyId = "expired-feedback-v1" };
        // Synthetic assembly only. The chosen test profile does not approve runtime policy.
        var run = CentExamAssembly.Assemble(Guid.NewGuid(), Guid.NewGuid(), definition, manifest, "offline-validation-only", DateTimeOffset.UtcNow);
        if (run.Questions.Count != definition.TotalQuestions) throw new InvalidOperationException("Quota mismatch.");
        if (output is not null) {
            var directory = Path.GetFullPath(output); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "exam-manifest.json"), CentExamJson.Write(manifest), new UTF8Encoding(false));
        }
        Console.WriteLine($"Validated {package.Sources.Count} source hashes, {package.Topics.Count} mapped topics, {package.Questions.Count} bank questions; {manifest.Questions.Length} agent-reviewed candidates. Exact {run.Questions.Count}-question offline assembly passed. No publication, database or runtime activation.");
    }
}
