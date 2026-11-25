using System.Text.Json;
namespace Cent.Module;

// No content import or public asset: this is the existing embedded source.
public static class CentReaderPilot
{
    public const string SectionKey = "math/chapter2.html#section-02";
    public static bool Enabled(Microsoft.Extensions.Configuration.IConfiguration config) =>
        string.Equals(config["Cent:ReaderPilot:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    public static string Revision => CentModule.Hash(Html())[..24];
    public static string Html()
    {
        using var stream = typeof(CentReaderPilot).Assembly.GetManifestResourceStream("Cent.Module.Content.Sources.math.chapter2.html")!;
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();
        var start = source.IndexOf("<div id=\"s1\">", StringComparison.Ordinal);
        var end = source.IndexOf("<div id=\"s2\">", start, StringComparison.Ordinal);
        return source[start..end];
    }
    public static void ValidateAnchor(string? value)
    {
        if (value is null) return;
        CentModule.Check(value.Length <= 2000, "Invalid note anchor.");
        try {
            using var json = JsonDocument.Parse(value);
            var root = json.RootElement;
            var kind = root.GetProperty("kind").GetString();
            CentModule.Check(kind is "html" or "pdf", "Invalid note anchor.");
            foreach (var axis in new[] { "x", "y" }) {
                var n = root.GetProperty(axis).GetDouble();
                CentModule.Check(double.IsFinite(n) && n >= 0 && n <= 1, "Invalid note anchor.");
            }
            if (root.TryGetProperty("revision", out var revision)) CentModule.Check(revision.GetString() is { Length: <= 128 }, "Invalid content revision.");
            if (kind == "html") {
                CentModule.Check(root.GetProperty("block").GetString() is { Length: > 0 and <= 160 } &&
                    root.GetProperty("quote").GetString() is { Length: <= 500 }, "Invalid note anchor.");
            }
        } catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) {
            throw new CentException(400, "invalid_anchor", "Invalid note anchor.");
        }
    }
}

