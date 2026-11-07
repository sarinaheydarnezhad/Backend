using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Cent.Curriculum;

var sourceRoot = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("backend/Cent.Module/Content/Sources");
var output = Path.Combine(sourceRoot, "..", "Review");
Directory.CreateDirectory(output);
var corpus = SourceReader.Read(sourceRoot);
QuestionAuthoring.Build(corpus);
File.WriteAllText(Path.Combine(sourceRoot, "..", "curriculum.json"), JsonSerializer.Serialize(corpus, SourceReader.Json), new UTF8Encoding(false));
Console.WriteLine($"Corpus: {corpus.Sources.Count} sources, {corpus.Chapters.Count} chapters, {corpus.Topics.Count} topics, {corpus.Exercises.Count} exercises; {corpus.Exercises.Count(e => e.Status == "source-mcq")} multiple-choice questions.");
Console.WriteLine($"Assessment bank: {corpus.Questions.Count} questions ({corpus.Questions.Count(q => q.Origin == "authored")} authored), {corpus.Findings.Count} review findings.");
var parser = new HtmlParser();
var inventory = new List<object>();
foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.html", SearchOption.AllDirectories).OrderBy(x => x)) {
    var html = File.ReadAllText(file, Encoding.UTF8);
    using var document = parser.ParseDocument(html);
    foreach (var node in document.QuerySelectorAll("script,style,nav,header nav,footer")) node.Remove();
    var title = document.Title;
    var headings = document.QuerySelectorAll("h1,h2,h3,h4").Select(h => new { level = h.TagName, id = h.Id, title = Clean(h.TextContent) }).ToArray();
    var body = document.Body!;
    var details = body.QuerySelectorAll("details").Select(d => new { parent = d.ParentElement!.TagName + "." + d.ParentElement.ClassName,
        grandparent = d.ParentElement.ParentElement?.ClassName, preview = Clean(d.ParentElement.TextContent)[..Math.Min(180, Clean(d.ParentElement.TextContent).Length)] }).ToArray();
    var paragraphs = body.QuerySelectorAll("h1,h2,h3,h4,p,li,summary,td,th,.formula-box,.formula,.box-label,.bh,.card-title,.card-head,.box-title")
        .Where(e => !e.Ancestors().OfType<IElement>().Any(a => a.Matches("script,style,nav")))
        .Select(e => Clean(e.TextContent)).Where(x => x.Length > 0).ToArray();
    var text = string.Join("\n", paragraphs);
    var course = new DirectoryInfo(Path.GetDirectoryName(file)!).Name;
    var name = Path.GetFileNameWithoutExtension(file);
    File.WriteAllText(Path.Combine(output, course + "-" + name + ".txt"), title + "\n\n" + text, new UTF8Encoding(false));
    inventory.Add(new { course, file = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/'), title, characters = text.Length, headings,
        detailsCount = details.Length, detailParents = details.GroupBy(d => d.parent).Select(g => new { parent = g.Key, count = g.Count() }), sampleDetails = details.Take(2) });
}
File.WriteAllText(Path.Combine(output, "inventory.json"), JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"Inspected {inventory.Count} HTML sources; review files are in {Path.GetFullPath(output)}");
static string Clean(string value) => Regex.Replace(value, @"\s+", " ").Trim();
