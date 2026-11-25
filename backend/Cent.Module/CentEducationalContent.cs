using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;

namespace Cent.Module;

public sealed record ReaderAsset(string Id, string Mime, byte[] Bytes, string Alt);
public sealed record ReaderFragment(string Html, string Revision, IReadOnlyList<ReaderAsset> Assets, IReadOnlyList<string> Findings);

// Read only the existing bundled corpus. Never fetch a URL or read an arbitrary filesystem path.
public static class CentEducationalContent
{
    private const string Prefix = "Cent.Module.Content.Sources.";
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyDictionary<string, ReaderFragment>>> Cache = new();
    public static readonly string[] HtmlTags = "div span p b strong em i ol ul li h1 h2 h3 h4 h5 h6 details summary sup sub br hr table caption colgroup col tbody thead tfoot tr td th section main header figure figcaption small code pre dl dt dd label blockquote".Split(' ');
    // Semantic classes only; source CSS, handlers and arbitrary data attributes never cross the boundary.
    public static readonly string[] Classes = ("sec-head sec-badge sec-label card concept example mistake examtip exercise card-title formula-box ltr frac num den steplist steps result q-block q ans-steps final-ans " +
        "en en-block l fa m box bh bb lab lbl defcard dt node ins b-ex qbox final arr b-mis e sec tw h b-prac b-main b-cmp b-def b-tip b-trap b-mem b-must defs flow b-ce fig b-proc b-sf b-exp b-rev b-exc b-con gcard n rgrid b-src check b-cls b-quick b-chk guide legend " +
        "step step-num step-lbl box-label dot q-head diff-badge sol diag-item sub-title inner solid formula-cell badge hard simple easy medium diagrow section-title tip fill-note formula trap remember remember-cols cheat " +
        "q-head qh qn qt opts options ex-block ex-tag practice-item difficulty term arrow-final analysis box-tip box-mistake box-remember diff-easy diff-med diff-hard review review-grid box-quick box-trap cheat-card strategy-block square-corner form cheat-grid checklist square-wrap square-grid venn-wrap venn-svg " +
        "line card-label bul answer cap graph-box answer-final icon-list final-answer qitem qnum steps-list ex-title concept-item formula qtext ans topic example-block txt quiz practice mistakes tips grid2 desc-cell shape-sub shape-head shape-icon question anatomy-item swatch tip compare-row k v section sec-num kicker quick-check diagram-box chapter-section graph-row why-box topic-head formula-cell pos neg family-card compass anatomy-legend mistake-list tip-list section-head tag sec-intro part lbl arrow fa numberline-box cycle anatomy-panel anatomy-body how-step num-badge cstep und law-table anatomy-sublabel summary-box pills concept-grid grid3 two anatomy-header anatomy-eq link-box link-title compare-card sw cformula data-table-wrap data-table howto compare-section compare-intro summary pill-row diagram-row anatomy-head anatomy-expr anatomy-note angle-row steps-main cycle-wrap cycle-note cycle-steps toolbox recap sub usage table-demo preview-box family-grid " +
        "b card-head final-line c-green problem c-blue lead c-red c-teal plain c-gold c-orange q-text diff mcq matrix vector passage").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
    private static readonly HashSet<string> TagSet = new(HtmlTags);
    private static readonly HashSet<string> ClassSet = new(Classes);

    public static ReaderFragment Section(string importKey, string? originalHtml = null)
    {
        var split = importKey.LastIndexOf('#');
        CentModule.Check(split > 0, "Unsupported source identifier.", 422, "unsupported_content");
        var source = importKey[..split];
        var resource = Prefix + source.Replace('/', '.');
        CentModule.Check(!string.IsNullOrWhiteSpace(originalHtml) || typeof(CentEducationalContent).Assembly.GetManifestResourceNames().Contains(resource), "Bundled source unavailable.", 422, "unsupported_content");
        var html = string.IsNullOrWhiteSpace(originalHtml) ? Read(resource) : originalHtml;
        CentModule.Check(html.Length <= 10_000_000, "Source too large.", 422, "unsupported_content");
        if (Cache.Count > 128) Cache.Clear();
        var sections = Cache.GetOrAdd(source + ":" + CentModule.Hash(html), _ => new(() => ParseSource(html, source))).Value;
        CentModule.Check(sections.ContainsKey(importKey), "Source section unavailable.", 422, "unsupported_content");
        return sections[importKey];
    }
    private static string Read(string resource)
    {
        using var stream = typeof(CentEducationalContent).Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    public static IReadOnlyDictionary<string, ReaderFragment> ParseSource(string html, string sourceKey)
    {
        var findings = new List<string>();
        // Original functions source contains unescaped inequality tokens, e.g. a<x<b.
        // Escape only tokens that cannot be legitimate HTML tags; retain their original visible characters.
        html = Regex.Replace(html, @"<([A-Za-z\u2212][^\s<>/]*)(?=<)", match => {
            findings.Add("Recovered unescaped inequality: " + match.Value);
            return "&lt;" + match.Groups[1].Value;
        });
        var doc = CentContentMarkup.Parse(html);
        var current = 0;
        void Index(CentContentMarkup node) {
            if (node.Name == "h2") current++;
            node.Section = current;
            foreach (var child in node.Children) Index(child);
        }
        Index(doc);
        var result = new Dictionary<string, ReaderFragment>();
        for (var i = 0; i <= current; i++) {
            var key = sourceKey + (i == 0 ? "#intro" : "#section-" + i.ToString("D2"));
            var fragment = new CentContentMarkup { Name = "root" };
            void Clone(CentContentMarkup original, CentContentMarkup target, bool inSvg = false) {
                if (original.Text is not null) { if (original.Section == i) target.Children.Add(original); return; }
                if (original.Name is "script" or "style" or "nav" or "footer" or "link" or "meta" or "input" ||
                    (original.Attr("class") ?? "").Split(' ').Any(c => c is "navbar" or "toc")) return;
                var clone = new CentContentMarkup { Name = original.Name, Section = original.Section };
                foreach (var a in original.Attributes) clone.Attributes.Add(a.Key, a.Value);
                foreach (var child in original.Children) Clone(child, clone, inSvg || original.Name == "svg");
                if (clone.Children.Count > 0 || original.Section == i && (inSvg || original.Name is "svg" or "img" or "br" or "hr" or "col")) target.Children.Add(clone);
            }
            Clone(doc, fragment);
            result[key] = Sanitize(fragment, sourceKey, findings);
        }
        // The reviewed 2026 additions use named section IDs, while the legacy
        // importer uses ordinal keys. Both resolve to the same original section.
        foreach (var node in doc.Descendants().Prepend(doc).Where(n => n.Name is "section" or "h2" or "div" && n.Attr("id") is not null)) {
            var heading = node.Name == "h2" ? node : node.Descendants().FirstOrDefault(n => n.Name == "h2");
            if (heading is null) continue;
            var ordinal = sourceKey + "#section-" + heading.Section.ToString("D2");
            result.TryAdd(sourceKey + "#" + node.Attr("id"), result[ordinal]);
        }
        return result;
    }
    private static string Enc(string value) => WebUtility.HtmlEncode(value);
    // Reviewed JSON defines exact section boundaries. Nested h2 headings belong to
    // that lesson; reparsing a source by heading ordinal would leak the next title.
    public static ReaderFragment Fragment(string html, string sourceKey) => Sanitize(CentContentMarkup.Parse(html), sourceKey, []);
    private static ReaderFragment Sanitize(CentContentMarkup source, string sourceKey, List<string> sourceFindings)
    {
        var findings = new List<string>(sourceFindings); var assets = new List<ReaderAsset>();
        var output = new StringBuilder();
        void Copy(CentContentMarkup e) {
            if (e.Text is not null) { output.Append(Enc(e.Text)); return; }
            // Private authoring/provenance blocks are retained in the canonical
            // archive but never included in learner HTML or text labels.
            var authoringClasses = (e.Attr("class") ?? "").Split(' ');
            if (authoringClasses.Any(c => c is "author-note" or "editorial-note" or "production-metadata" or "source-review" or "b-src")) return;
            if (e.Name is "p" or "small" or "aside" && Regex.IsMatch(e.InnerText.Trim(), @"^(?:یادداشت (?:نویسنده|ویراستار|تولید)|دستور (?:بازبینی|تولید)|(?:Internal|Editorial|Production) (?:note|metadata)|AI-generated editorial)", RegexOptions.IgnoreCase)) return;
            if (e.Name is "script" or "style" or "iframe" or "object" or "embed" or "form" or "input" or "button" or "link" or "meta" or "nav" or "footer") return;
            if (e.Name == "svg" || e.Name == "img") {
                ReaderAsset asset;
                try {
                    if (e.Name == "svg") {
                        var bytes = Encoding.UTF8.GetBytes(SafeSvg(e));
                        asset = new(CentModule.Hash(Convert.ToBase64String(bytes)), "image/svg+xml", bytes, e.Attr("aria-label") ?? e.InnerText.Trim());
                    } else asset = LocalImage(e.Attr("src") ?? "", sourceKey, e.Attr("alt") ?? "");
                } catch (CentException) {
                    // Fail explicitly: a diagram must never disappear silently from a lesson.
                    throw new CentException(422, "unsupported_asset", "Unsafe or unsupported educational asset in " + sourceKey);
                }
                assets.Add(asset);
                output.Append("<img data-asset=\"").Append(asset.Id).Append("\" alt=\"").Append(Enc(asset.Alt)).Append("\">"); return;
            }
            var supported = TagSet.Contains(e.Name);
            if (!supported && e.Name is not ("a" or "body" or "root")) findings.Add("Unwrapped unsupported element: " + e.Name);
            if (supported) {
                output.Append('<').Append(e.Name);
                var classes = (e.Attr("class") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(c => c == "source-ltr" ? "ltr" : c).Where(ClassSet.Contains).ToArray();
                if (classes.Length > 0) output.Append(" class=\"").Append(string.Join(' ', classes)).Append('"');
                foreach (var name in new[] { "dir", "lang", "colspan", "rowspan", "type" }) {
                    var value = e.Attr(name);
                    if (value is not null && (name == "dir" && value is "ltr" or "rtl" or "auto" || name == "lang" && value is "en" or "fa" || name is "colspan" or "rowspan" && Regex.IsMatch(value, "^[1-9][0-9]?$" ) || name == "type" && e.Name == "ol" && value is "a" or "A" or "1" or "i" or "I")) output.Append(' ').Append(name).Append("=\"").Append(Enc(value)).Append('"');
                }
                output.Append('>');
            }
            foreach (var child in e.Children) Copy(child);
            if (supported && e.Name is not ("br" or "hr" or "col")) output.Append("</").Append(e.Name).Append('>');
        }
        Copy(source);
        var safe = output.ToString();
        return new(safe, CentModule.Hash(safe)[..24], assets.DistinctBy(a => a.Id).ToArray(), findings.Distinct().ToArray());
    }
    private static ReaderAsset LocalImage(string reference, string sourceKey, string alt)
    {
        CentModule.Check(Regex.IsMatch(reference, @"^[\w./-]+\.(svg|png|jpg|webp)$") && !reference.StartsWith('/') && !reference.Split('/').Contains(".."), "Invalid asset reference.", 422);
        var path = sourceKey[..(sourceKey.LastIndexOf('/') + 1)] + reference;
        var resource = Prefix + path.Replace('/', '.');
        using var stream = typeof(CentEducationalContent).Assembly.GetManifestResourceStream(resource);
        CentModule.Check(stream is not null && stream.Length <= 8_000_000, "Missing/oversized asset.", 422);
        using var memory = new MemoryStream(); stream!.CopyTo(memory); var bytes = memory.ToArray();
        var extension = Path.GetExtension(reference);
        if (extension == ".svg") {
            var doc = CentContentMarkup.Parse(Encoding.UTF8.GetString(bytes));
            var svg = doc.Descendants().Prepend(doc).FirstOrDefault(n => n.Name == "svg");
            CentModule.Check(svg is not null, "Invalid SVG.", 422);
            bytes = Encoding.UTF8.GetBytes(SafeSvg(svg!));
        } else {
            var valid = extension == ".png" && bytes.AsSpan().StartsWith(new byte[] {137,80,78,71,13,10,26,10}) ||
                extension == ".jpg" && bytes.AsSpan().StartsWith(new byte[] {255,216,255}) ||
                extension == ".webp" && bytes.Length > 12 && Encoding.ASCII.GetString(bytes,0,4) == "RIFF" && Encoding.ASCII.GetString(bytes,8,4) == "WEBP";
            CentModule.Check(valid, "Asset media type mismatch.", 422);
        }
        return new(CentModule.Hash(Convert.ToBase64String(bytes)), extension == ".svg" ? "image/svg+xml" : extension == ".jpg" ? "image/jpeg" : "image/" + extension[1..], bytes, alt);
    }

    private static readonly HashSet<string> SvgTags = new("svg g defs marker linearGradient radialGradient stop circle ellipse line path polyline polygon rect text tspan title desc".Split(' '));
    private static readonly HashSet<string> SvgAttrs = new("viewBox width height x y x1 x2 y1 y2 cx cy r rx ry d points fill stroke stroke-width stroke-dasharray opacity fill-opacity stroke-opacity font-size font-family font-weight font-style text-anchor transform dominant-baseline baseline-shift stroke-linecap stroke-linejoin marker-start marker-mid marker-end markerWidth markerHeight refX refY orient offset stop-color stop-opacity gradientUnits gradientTransform".Split(' '));
    public static string SafeSvg(CentContentMarkup svg)
    {
        var nodes = svg.Descendants().Prepend(svg).Where(n => n.Text is null).ToArray();
        CentModule.Check(nodes.Length <= 5000, "SVG too complex.", 422);
        var ids = nodes.Select(e => e.Attr("id")).Where(id => id is not null).ToHashSet();
        var tags = SvgTags.ToDictionary(t => t.ToLowerInvariant(), t => t);
        var attrs = SvgAttrs.ToDictionary(a => a.ToLowerInvariant(), a => a);
        var output = new StringBuilder();
        void Write(CentContentMarkup e) {
            if (e.Text is not null) { output.Append(Enc(e.Text)); return; }
            CentModule.Check(tags.ContainsKey(e.Name), "Unsafe SVG element.", 422);
            output.Append('<').Append(tags[e.Name]);
            if (e == svg) output.Append(" xmlns=\"http://www.w3.org/2000/svg\"");
            foreach (var a in e.Attributes) {
                // Source layout styles are discarded. Diagram coordinates and presentation
                // attributes are rebuilt below; the reader controls responsive dimensions.
                if (a.Key is "xmlns" or "role" or "aria-label" or "class" or "style") continue;
                if (a.Key == "id") CentModule.Check(Regex.IsMatch(a.Value, @"^[A-Za-z][\w-]{0,80}$"), "Invalid SVG ID.", 422);
                else CentModule.Check(attrs.ContainsKey(a.Key.ToLowerInvariant()), "Unsafe SVG attribute: " + a.Key, 422);
                var local = Regex.Match(a.Value, @"^url\(#([A-Za-z][\w-]{0,80})\)$");
                if (local.Success) CentModule.Check(a.Key is "fill" or "stroke" or "marker-start" or "marker-mid" or "marker-end" && ids.Contains(local.Groups[1].Value), "Invalid SVG reference.", 422);
                else CentModule.Check(!Regex.IsMatch(a.Value, @"(?i)url|javascript|data:|https?:|[/\\]{2}|[<>;&]"), "Unsafe SVG value.", 422);
                CentModule.Check(a.Value.Length < 50000, "Oversized SVG attribute.", 422);
                output.Append(' ').Append(a.Key == "id" ? "id" : attrs[a.Key.ToLowerInvariant()]).Append("=\"").Append(Enc(a.Value)).Append('"');
            }
            output.Append('>');
            foreach (var child in e.Children) Write(child);
            output.Append("</").Append(tags[e.Name]).Append('>');
        }
        Write(svg);
        CentModule.Check(output.Length < 1_000_000, "SVG too complex.", 422);
        return output.ToString();
    }
}
