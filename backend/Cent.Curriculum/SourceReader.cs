using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Cent.Module.Curriculum;

namespace Cent.Curriculum;

public static class SourceReader
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static Corpus Read(string root)
    {
        var corpus = new Corpus([], [], [], [], []);
        var parser = new HtmlParser();
        foreach (var path in Directory.EnumerateFiles(root, "*.html", SearchOption.AllDirectories).OrderBy(p => p)) {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var course = relative.Split('/')[0];
            var html = File.ReadAllText(path, Encoding.UTF8);
            using var doc = parser.ParseDocument(html);
            var title = Text(doc.QuerySelector("h1") ?? doc.QuerySelector("title")!);
            var sourceKey = relative;
            corpus.Sources.Add(new(sourceKey, course, relative, title, doc.Title ?? title,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), Text(doc.Body!)));
            var chapterKey = sourceKey + "#chapter";
            var chapterSort = ParseChapter(Path.GetFileName(path));
            if (course != "chemistry") corpus.Chapters.Add(new(chapterKey, course, title, chapterSort));
            var logicTopic = course == "logic" ? sourceKey + "#topic" : null;
            if (logicTopic is not null) corpus.Topics.Add(new(logicTopic, chapterKey, course, title, English(title), chapterSort, chapterSort <= 4));

            var headings = doc.QuerySelectorAll("h2").ToArray();
            var nodes = doc.Body!.QuerySelectorAll("*").ToArray();
            var nodeIndex = nodes.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
            var positions = headings.Select(h => nodeIndex[h]).ToArray();
            var sectionKeys = new Dictionary<IElement, string>();
            var topicKeys = new Dictionary<string, string?>();
            var leading = new SourceSection(sourceKey + "#intro", sourceKey, logicTopic, title, 0, "", "");
            var parts = new List<SourceSection> { leading };
            for (var i = 0; i < headings.Length; i++) {
                var heading = Text(headings[i]);
                var key = sourceKey + "#section-" + (i + 1).ToString("D2");
                string? topicKey = logicTopic;
                if (course == "chemistry") {
                    chapterKey = sourceKey + "#chapter-" + (i + 1);
                    corpus.Chapters.Add(new(chapterKey, course, heading, i + 1));
                    topicKey = key;
                } else if (course != "logic" && IsTeachingHeading(heading)) topicKey = key;
                if (course == "math" && heading.Contains("جملات هم")) topicKey = "math.algebraic-expressions";
                if (topicKey is not null && !corpus.Topics.Any(t => t.Key == topicKey)) {
                    var topicTitle = topicKey == "math.algebraic-expressions" ? "عبارت‌های جبری" : heading;
                    corpus.Topics.Add(new(topicKey, chapterKey, course, topicTitle,
                        topicKey == "math.algebraic-expressions" ? "Algebraic expressions" : English(heading), i + 1,
                        topicKey == "math.algebraic-expressions" || (chapterSort == 1 && i < 6)));
                }
                parts.Add(new(key, sourceKey, topicKey, heading, i + 1, "", ""));
                topicKeys[key] = topicKey;
            }
            foreach (var node in nodes) {
                var index = nodeIndex[node];
                var sectionNumber = Array.FindLastIndex(positions, p => p <= index) + 1;
                sectionKeys[node] = parts[sectionNumber].Key;
            }
            // A fragment containing the selected heading and all following siblings until the next heading.
            foreach (var part in parts) {
                var selected = nodes.Where(n => sectionKeys[n] == part.Key && !n.Ancestors().OfType<IElement>().Any(a => sectionKeys.GetValueOrDefault(a) == part.Key)).ToArray();
                // Use text-node ranges for coverage, as headings are often nested in decorative wrappers.
                var text = new StringBuilder();
                foreach (var element in nodes.Where(n => sectionKeys[n] == part.Key && !n.Matches("script,style,nav,footer"))) {
                    if (element.Ancestors().OfType<IElement>().Any(a => a.Matches("script,style,nav,footer"))) continue;
                    foreach (var child in element.ChildNodes.OfType<IText>()) if (!string.IsNullOrWhiteSpace(child.Data)) text.AppendLine(child.Data.Trim());
                }
                var index = parts.IndexOf(part);
                var fragment = SafeHtml((IElement)(CloneSection(doc.Body!, part.Key, sectionKeys) ?? doc.CreateElement("div")));
                corpus.Sections.Add(part with { Text = text.ToString().Trim(), Html = fragment });
            }

            var ordinal = 0;
            foreach (var detail in doc.QuerySelectorAll("details")) {
                if (detail.Ancestors().OfType<IElement>().Any(a => a.TagName == "DETAILS")) continue;
                var self = detail.Matches("details.mcq");
                var container = self ? detail : detail.ParentElement!;
                var optionsNode = container.QuerySelector(".opts,.options,ol[type=A],ol[type=a]");
                var options = optionsNode?.Children.Select(o => CleanOption(o)).ToArray() ?? [];
                var answerNode = self ? container.QuerySelector(".answer,.sol") : detail;
                var solution = Text(answerNode ?? detail);
                var prompt = container.Clone(true) as IElement;
                foreach (var item in prompt!.QuerySelectorAll("details,.opts,.options,ol[type=A],ol[type=a],.qh,.q-head,.difficulty,.badge,.diff")) item.Remove();
                if (self) foreach (var item in prompt.QuerySelectorAll("summary,.answer,.sol")) item.Remove();
                if (container.Matches(".card.exercise")) {
                    var previous = detail.PreviousElementSibling;
                    prompt = previous?.Clone(true) as IElement ?? prompt;
                }
                var promptText = Text(prompt);
                if (promptText.Length == 0 || promptText.Length > 7000) continue;
                var sectionKey = sectionKeys.GetValueOrDefault(detail, sourceKey + "#intro");
                var topicKey = corpus.Sections.First(s => s.Key == sectionKey).TopicKey;
                // Final mixed exercises are linked back to teaching sections using both prompt and solution.
                if (topicKey is null) topicKey = MatchTopic(corpus, sourceKey, promptText + " " + solution, options);
                int? correct = null;
                if (options.Length >= 2) {
                    var marked = optionsNode!.Children.Select((o, n) => (o, n)).Where(x => x.o.ClassList.Contains("correct")).ToArray();
                    if (marked.Length == 1) correct = marked[0].n;
                    var answerText = Text(container.QuerySelector(".final-line") ?? answerNode ?? detail);
                    var letter = Regex.Match(answerText, @"(?:پاسخ\s*(?:نهایی|درست)?\s*[:：]?\s*|^)([A-F])(?:[.)\s—]|$)");
                    if (letter.Success) correct = letter.Groups[1].Value[0] - 'A';
                    if (correct is null && course == "chemistry") {
                        var final = detail.QuerySelectorAll("p strong.en").LastOrDefault();
                        var match = Regex.Match(final?.TextContent ?? "", @"^\s*([A-F])\.");
                        if (match.Success) correct = match.Groups[1].Value[0] - 'A';
                    }
                }
                ordinal++;
                var difficultyText = container.ClassName + " " + Text(container.QuerySelector(".difficulty,.diff,.diff-badge,.badge") ?? detail.QuerySelector("summary") ?? detail);
                var difficulty = Regex.IsMatch(difficultyText, "HARD|hard|سخت") ? "hard" : Regex.IsMatch(difficultyText, "EASY|easy|آسان") ? "easy" : "medium";
                var figure = container.QuerySelector("svg");
                var status = options.Length < 2 ? "open-response" : correct >= 0 && correct < options.Length && options.Distinct().Count() == options.Length ? "source-mcq" : "needs-review";
                corpus.Exercises.Add(new(sourceKey + "#exercise-" + ordinal.ToString("D3"), sourceKey, sectionKey, topicKey, course,
                    promptText, options, correct, solution, difficulty, figure is null ? null : SafeSvg(figure), status));
            }
        }
        return corpus;
    }

    private static string? MatchTopic(Corpus corpus, string sourceKey, string prompt, string[] options)
    {
        var terms = Terms(prompt);
        var candidates = corpus.Sections.Where(s => s.SourceKey == sourceKey && s.TopicKey is not null).ToArray();
        return candidates.Select(s => new { s.TopicKey, Score = Terms(s.Text).Intersect(terms).Sum(term =>
            (term.Length > 5 ? 2d : 1d) / Math.Sqrt(candidates.Count(c => c.Text.Contains(term, StringComparison.OrdinalIgnoreCase)))) })
            .OrderByDescending(s => s.Score).FirstOrDefault()?.TopicKey;
    }
    private static HashSet<string> Terms(string text) => Regex.Matches(text.ToLowerInvariant(), @"[a-z]{3,}|[\p{IsArabic}]{3,}")
        .Select(m => m.Value).Where(t => !StopWords.Contains(t)).ToHashSet();
    private static readonly HashSet<string> StopWords = new("the and which what with that this from correct following statement most into only have more than when پاسخ گزینه برای است شود شودند شده دارد های این شود که بود شود یک آن هستند".Split(' '));
    private static bool IsTeachingHeading(string title) => !Regex.IsMatch(title, "راهنما|چگونه|اهداف|نقشه|مرور|آزمون نهایی|تمرین نهایی|جدول فرمول|چک.?لیست|برگه|جمع.?بندی|پایان فصل|جعبه.?ابزار|کنار هم|جدول جمع|مقایسه و تحلیل خانواده|از معادلهٔ خطی تا");
    private static INode? CloneSection(INode node, string key, Dictionary<IElement, string> keys) {
        if (node is IText) return node.ParentElement is { } parent && keys.GetValueOrDefault(parent) == key ? node.Clone() : null;
        if (node is not IElement element || element.Matches("script,style,nav,footer")) return null;
        var clone = node.Clone(false);
        foreach (var child in node.ChildNodes) if (CloneSection(child, key, keys) is { } copy) clone.AppendChild(copy);
        return clone.ChildNodes.Length > 0 ? clone : null;
    }
    private static int ParseChapter(string name) {
        var match = Regex.Match(name, @"(?:chapter|ch)(\d+)");
        if (match.Success) return int.Parse(match.Groups[1].Value);
        if (name.Contains("master")) return 1;
        if (name.Contains("توابع")) return 3;
        if (name.Contains("trig")) return 4;
        if (name.Contains("geometry")) return 5;
        return 20;
    }
    private static string English(string title) => Regex.Match(title, @"[A-Za-z][A-Za-z\s,&'’–—\-()]*").Value.Trim(' ', '(', ')');
    private static string CleanOption(IElement element) {
        var clone = (IElement)element.Clone(true);
        if (clone.FirstElementChild is { } first && Regex.IsMatch(first.TextContent.Trim(), "^[A-F]$")) first.Remove();
        return Regex.Replace(Text(clone), @"^[A-F][.)]\s*", "").Trim();
    }
    public static string Text(INode node) {
        var builder = new StringBuilder();
        void Walk(INode n) {
            if (n is IText text) { builder.Append(text.Data); return; }
            if (n is not IElement element) { foreach (var child in n.ChildNodes) Walk(child); return; }
            if (element.Matches("script,style,nav,footer")) return;
            if (element.ClassList.Contains("frac")) {
                builder.Append('(').Append(element.QuerySelector(".num")?.TextContent).Append(")/(")
                    .Append(element.QuerySelector(".den")?.TextContent).Append(')'); return;
            }
            if (element.TagName == "SUP") builder.Append('^');
            if (element.TagName == "SUB") builder.Append('_');
            foreach (var child in n.ChildNodes) Walk(child);
            if (element.Matches("p,li,tr,div,h1,h2,h3,h4,h5,h6,section,br,summary")) builder.AppendLine();
            else if (element.Matches("td,th")) builder.Append(" | ");
        }
        Walk(node);
        return Regex.Replace(Regex.Replace(builder.ToString(), @"[^\S\n]+", " "), @"\n\s*\n+", "\n").Trim();
    }
    public static string SafeHtml(IElement original) {
        var node = (IElement)original.Clone(true);
        foreach (var child in node.QuerySelectorAll("script,style,nav,footer,link,iframe,object,embed,form,input,button,meta,img")) child.Remove();
        foreach (var element in node.QuerySelectorAll("*").Prepend(node)) {
            foreach (var attribute in element.Attributes.ToArray()) {
                if (attribute.Name is "colspan" or "rowspan" && Regex.IsMatch(attribute.Value, "^[0-9]{1,2}$")) continue;
                if (attribute.Name == "dir" && attribute.Value is "ltr" or "rtl") continue;
                if (attribute.Name == "class") {
                    var classes = attribute.Value.Split(' ').Where(c => new[] { "frac", "num", "den", "ltr", "en", "l", "formula" }.Contains(c));
                    element.SetAttribute("class", string.Join(" ", classes.Select(c => "source-" + c))); continue;
                }
                element.RemoveAttribute(attribute.Name);
            }
            if (element.TagName == "A") element.Replace(element.Owner.CreateTextNode(element.TextContent));
        }
        foreach (var svg in node.QuerySelectorAll("svg").ToArray()) svg.Replace(node.Owner.CreateTextNode("[نمودار منبع] " + Text(svg)));
        return node.OuterHtml;
    }
    public static string SafeSvg(IElement original) {
        var svg = (IElement)original.Clone(true);
        foreach (var element in svg.QuerySelectorAll("script,foreignObject,iframe,image,use,animate,set")) element.Remove();
        var allowed = new HashSet<string>("xmlns viewBox width height x y x1 x2 y1 y2 cx cy r rx ry d points fill stroke stroke-width stroke-dasharray opacity fill-opacity stroke-opacity font-size font-family font-weight text-anchor transform dominant-baseline".Split(' '));
        foreach (var element in svg.QuerySelectorAll("*").Prepend(svg)) foreach (var attribute in element.Attributes.ToArray())
            if (!allowed.Contains(attribute.Name) || Regex.IsMatch(attribute.Value, @"(?i)url\(|javascript:|data:|https?:")) element.RemoveAttribute(attribute.Name);
        svg.SetAttribute("xmlns", "http://www.w3.org/2000/svg");
        return svg.OuterHtml;
    }
}
