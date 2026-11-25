using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cent.Module;
using Microsoft.Extensions.Configuration;

namespace Cent.Tests;

public class EducationalReaderTests
{
    [Fact]
    public void Rollout_is_explicit_and_can_be_limited_to_one_subject()
    {
        var empty = new ConfigurationBuilder().Build();
        Assert.All(CentReaderRollout.Subjects, subject => Assert.False(CentReaderRollout.Subject(empty, subject)));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Cent:Reader:Enabled"] = "true", ["Cent:Reader:Subjects"] = "math, physics"
        }).Build();
        Assert.True(CentReaderRollout.Subject(config, "math")); Assert.True(CentReaderRollout.Subject(config, "physics"));
        Assert.False(CentReaderRollout.Subject(config, "biology")); Assert.False(CentReaderRollout.Subject(config, "other"));
    }

    [Fact]
    public void Every_bundled_chapter_preserves_sections_answers_tables_and_diagrams()
    {
        var assembly = typeof(CentEducationalContent).Assembly;
        var resources = assembly.GetManifestResourceNames().Where(n => n.StartsWith("Cent.Module.Content.Sources.") && n.EndsWith(".html")).ToArray();
        Assert.True(resources.Length >= 30);
        var failures = new List<string>();
        foreach (var resource in resources) {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream); var html = reader.ReadToEnd();
            var key = resource["Cent.Module.Content.Sources.".Length..]; key = key[..key.IndexOf('.')] + "/" + key[(key.IndexOf('.') + 1)..];
            try {
                var original = CentContentMarkup.Parse(html);
                var nodes = original.Descendants().Prepend(original).ToArray();
                foreach (var svg in nodes.Where(n => n.Name == "svg")) CentEducationalContent.SafeSvg(svg);
                var fragments = CentEducationalContent.ParseSource(html, key);
                var ordinalFragments = fragments.Where(f => f.Key.EndsWith("#intro") || Regex.IsMatch(f.Key, "#section-[0-9]+$")).Select(f => f.Value).ToArray();
                Assert.Equal(nodes.Count(n => n.Name == "h2") + 1, ordinalFragments.Length);
                var rendered = string.Concat(ordinalFragments.Select(f => f.Html));
                foreach (var tag in new[] { "details", "summary", "table", "sup", "sub" })
                    Assert.Equal(nodes.Count(n => n.Name == tag), Regex.Matches(rendered, "<" + tag + "(?:>| )").Count);
                Assert.Equal(nodes.Count(n => n.Name is "svg" or "img"), Regex.Matches(rendered, "<img ").Count);
                foreach (var asset in ordinalFragments.SelectMany(f => f.Assets)) {
                    Assert.NotEmpty(asset.Bytes);
                    if (asset.Mime == "image/svg+xml") Assert.Equal("svg", XDocument.Parse(Encoding.UTF8.GetString(asset.Bytes)).Root!.Name.LocalName);
                }
                Assert.DoesNotContain("<script", rendered); Assert.DoesNotContain(" style=", rendered);
            } catch (Exception error) { failures.Add(resource + ": " + error.Message); }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Every_imported_section_key_resolves_including_named_reading_sections()
    {
        var package = CentCurriculumImporter.Bundled();
        foreach (var section in package.Sections) Assert.NotEmpty(CentEducationalContent.Section(section.Key).Html);
    }

    [Fact]
    public void Sanitizer_preserves_literal_inequalities_and_removes_executable_markup()
    {
        var fragments = CentEducationalContent.ParseSource("<h2>Title</h2><p>a&lt;x&lt;b &amp; c</p><script>alert(1)</script><div onclick='bad()' style='color:red'><a href='javascript:bad()'>safe</a></div>", "math/test.html");
        var html = fragments["math/test.html#section-01"].Html;
        Assert.Contains("a&lt;x&lt;b &amp; c", html); Assert.Contains("safe", html);
        Assert.DoesNotContain("bad", html); Assert.DoesNotContain("script", html); Assert.DoesNotContain("style", html);
        Assert.Contains("a&lt;x&lt;b", CentEducationalContent.ParseSource("<h2>T</h2><p>a<x<b</p>", "math/test.html")["math/test.html#section-01"].Html);
    }

    [Theory]
    [InlineData("<svg><image href='https://example.com/x'/></svg>")]
    [InlineData("<svg><path onload='bad()'/></svg>")]
    [InlineData("<svg><path fill='url(https://example.com/x)'/></svg>")]
    public void Unsafe_diagrams_fail_explicitly(string html) =>
        Assert.Throws<CentException>(() => CentEducationalContent.ParseSource("<h2>T</h2>" + html, "math/test.html"));
}
