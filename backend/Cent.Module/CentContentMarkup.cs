using System.Net;
using System.Text.RegularExpressions;

namespace Cent.Module;

// Bounded tokenizer for the bundled static educational vocabulary, never a browser or XML loader.
// Output is always rebuilt with allowlists and HTML encoding by CentEducationalContent.
public sealed class CentContentMarkup
{
    public string Name { get; init; } = "";
    public string? Text { get; init; }
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CentContentMarkup> Children { get; } = [];
    public int Section { get; set; }
    public string? Attr(string name) => Attributes.GetValueOrDefault(name);
    public string InnerText => Text ?? string.Concat(Children.Select(c => c.InnerText));
    public IEnumerable<CentContentMarkup> Descendants() => Children.SelectMany(c => new[] { c }.Concat(c.Descendants()));
    private static readonly Regex Tokens = new(@"<!--[\s\S]*?-->|<![^>]*>|</?[A-Za-z][\w:-]*(?:[^<>""']|""[^""]*""|'[^']*')*>|[^<]+|<", RegexOptions.Compiled, TimeSpan.FromSeconds(3));
    private static readonly Regex Attribute = new("([^\\s=/'\"<>]+)(?:\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+)))?", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> Void = new("area base br col embed hr img input link meta param source track wbr".Split(' '));
    public static CentContentMarkup Parse(string html)
    {
        CentModule.Check(html.Length <= 10_000_000, "Source too large.", 422, "unsupported_content");
        var root = new CentContentMarkup { Name = "root" }; var stack = new List<CentContentMarkup> { root };
        var count = 0; string? raw = null;
        foreach (Match match in Tokens.Matches(html)) {
            if (++count > 200000) throw new CentException(422, "unsupported_content", "Markup too complex.");
            var value = match.Value;
            if (raw is not null) { if (value.StartsWith("</" + raw, StringComparison.OrdinalIgnoreCase)) raw = null; continue; }
            if (value.StartsWith("<!", StringComparison.Ordinal)) continue;
            if (value.StartsWith("</", StringComparison.Ordinal)) {
                var name = value[2..^1].Trim().ToLowerInvariant(); var index = stack.FindLastIndex(n => n.Name == name);
                if (index > 0) stack.RemoveRange(index, stack.Count - index);
            } else if (value.Length > 1 && value[0] == '<' && char.IsAsciiLetter(value[1])) {
                var nameMatch = Regex.Match(value, @"^<([A-Za-z][\w:-]*)"); var name = nameMatch.Groups[1].Value.ToLowerInvariant();
                if (name is "script" or "style") { raw = name; continue; }
                // Standard optional end tags used by tables/lists in the original source.
                bool Close(CentContentMarkup n) => name == "li" && n.Name == "li" || name is "td" or "th" && n.Name is "td" or "th" || name == "tr" && n.Name == "tr" || name == "p" && n.Name == "p";
                if (stack.Count > 1 && Close(stack[^1])) stack.RemoveAt(stack.Count - 1);
                var node = new CentContentMarkup { Name = name };
                foreach (Match a in Attribute.Matches(value[nameMatch.Length..].TrimEnd('>', '/', ' '))) {
                    var attr = a.Groups[1].Value;
                    CentModule.Check(!node.Attributes.ContainsKey(attr), "Duplicate source attribute.", 422, "unsupported_content");
                    node.Attributes[attr] = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
                }
                stack[^1].Children.Add(node);
                if (!Void.Contains(name) && !value.EndsWith("/>", StringComparison.Ordinal)) { stack.Add(node); CentModule.Check(stack.Count <= 128, "Markup nesting too deep.", 422, "unsupported_content"); }
            } else stack[^1].Children.Add(new() { Text = WebUtility.HtmlDecode(value) });
        }
        return root.Descendants().FirstOrDefault(n => n.Name == "body") ?? root;
    }
}
