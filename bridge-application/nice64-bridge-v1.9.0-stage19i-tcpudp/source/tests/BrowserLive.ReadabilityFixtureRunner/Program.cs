using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Nice64.BrowserLive.Readability;

var arguments = args.ToList();
string projectRoot = FindProjectRoot();
string fixtureRoot = Path.Combine(projectRoot, "Fixtures");
string outputRoot = Path.Combine(projectRoot, "Output");
string? fixtureName = GetOption("--fixture");
string mode = GetOption("--mode") ?? "auto";
string? outputOverride = GetOption("--output");
bool verify = arguments.Contains("--verify", StringComparer.OrdinalIgnoreCase);
if (outputOverride is not null) outputRoot = Path.GetFullPath(outputOverride);

if (arguments.Contains("--help", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("BrowserLive.ReadabilityFixtureRunner --all | --fixture <name> [--mode auto] [--output <path>] [--verify]");
    return;
}

var fixtureFiles = Directory.EnumerateFiles(fixtureRoot, "*.html").OrderBy(path => path).ToList();
if (fixtureName is not null)
    fixtureFiles = fixtureFiles.Where(path => Path.GetFileNameWithoutExtension(path).Equals(fixtureName, StringComparison.OrdinalIgnoreCase)).ToList();
if (fixtureFiles.Count == 0) throw new InvalidOperationException("No matching local HTML fixtures were found.");

Directory.CreateDirectory(outputRoot);
var options = new JsonSerializerOptions { WriteIndented = true };
var extractor = new NiceReadableExtractor();
var rows = new List<object>();
foreach (string fixture in fixtureFiles)
{
    string name = Path.GetFileNameWithoutExtension(fixture);
    string html = File.ReadAllText(fixture);
    Uri baseUri = FixtureUrl(name);
    var total = Stopwatch.StartNew();
    ReadableExtractionResult result = extractor.Extract(html, baseUri);
    total.Stop();
    string target = Path.Combine(outputRoot, name);
    Directory.CreateDirectory(target);
    WriteJson(Path.Combine(target, "readable-page-model.json"), result.Page, options);
    WriteJson(Path.Combine(target, "diagnostics.json"), result.Page.Diagnostics, options);
    WriteJson(Path.Combine(target, "candidate-containers.json"), result.CandidateContainers, options);
    WriteJson(Path.Combine(target, "candidate-cards.json"), result.CandidateCards, options);
    WriteJson(Path.Combine(target, "quality-result.json"), new { result.Success, result.PassesQualityGate, result.SelectedMode, result.FailureReasons }, options);
    WriteJson(Path.Combine(target, "image-match-input.json"), new
    {
        result.Page.PrimaryLink,
        cards = result.CandidateCards.Select(card => new { card.GroupId, card.Heading, card.Summary, card.DestinationUrl, card.SourceOrder }),
        links = result.Page.Links.Select(link => new { link.Label, link.Url, link.ParentHeading, link.GroupId, link.SourceOrder })
    }, options);
    File.WriteAllText(Path.Combine(target, "diagnostics.txt"), FormatDiagnostics(name, result, total.ElapsedMilliseconds), Encoding.UTF8);
    File.WriteAllText(Path.Combine(target, "preview.html"), BuildPreview(name, result), Encoding.UTF8);
    rows.Add(new { fixture = name, requestedMode = mode, result.SelectedMode, result.Success, result.PassesQualityGate, blocks = result.Page.Blocks.Count, links = result.Page.Links.Count, cards = result.CandidateCards.Count, parseMs = result.Page.Diagnostics.ParseMilliseconds, extractionMs = result.Page.Diagnostics.ExtractionMilliseconds, totalMs = total.ElapsedMilliseconds, failures = result.FailureReasons });
    Console.WriteLine($"{name,-28} {result.SelectedMode,-7} blocks={result.Page.Blocks.Count,3} links={result.Page.Links.Count,2} cards={result.CandidateCards.Count,2} total={total.ElapsedMilliseconds,4}ms quality={result.PassesQualityGate}");
}
WriteJson(Path.Combine(outputRoot, "summary.json"), rows, options);
if (verify)
{
    List<string> failures = Verify(fixtureFiles, outputRoot);
    if (failures.Count > 0) throw new InvalidOperationException("Fixture verification failed: " + string.Join("; ", failures));
    Console.WriteLine("Fixture verification passed.");
}

string? GetOption(string name)
{
    int index = arguments.FindIndex(value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
}

static string FindProjectRoot()
{
    foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Fixtures"))) return directory.FullName;
        }
    }
    throw new DirectoryNotFoundException("Could not locate the fixture runner project root.");
}

static Uri FixtureUrl(string name) => name switch
{
    "milk-home" => new Uri("https://www.milk.org/"),
    "nintendo-home" => new Uri("https://www.nintendo.com/us/"),
    "fox-article" => new Uri("https://www.foxnews.com/news/example"),
    "cnn-lite-index" => new Uri("https://lite.cnn.com/"),
    "wikipedia-article" => new Uri("https://en.wikipedia.org/wiki/Nintendo_64"),
    "toms-hardware-article" => new Uri("https://www.tomshardware.com/example"),
    _ => new Uri("https://fixtures.nice64.local/" + name + "/")
};

static void WriteJson(string path, object value, JsonSerializerOptions options) => File.WriteAllText(path, JsonSerializer.Serialize(value, options), Encoding.UTF8);

static string FormatDiagnostics(string fixture, ReadableExtractionResult result, long totalMilliseconds) => string.Join(Environment.NewLine,
[
    $"FIXTURE={fixture}", $"MODE={result.SelectedMode}", $"QUALITY={result.PassesQualityGate}", $"SOURCE_BYTES={result.Page.Diagnostics.SourceBytes}",
    $"NODES={result.Page.Diagnostics.NodesVisited}", $"CANDIDATES={result.CandidateContainers.Count}", $"CARDS={result.CandidateCards.Count}",
    $"BLOCKS={result.Page.Blocks.Count}", $"LINKS={result.Page.Links.Count}", $"PARSE_MS={result.Page.Diagnostics.ParseMilliseconds}",
    $"EXTRACTION_MS={result.Page.Diagnostics.ExtractionMilliseconds}", $"TOTAL_MS={totalMilliseconds}",
    "FAILURES=" + string.Join(",", result.FailureReasons), "LIMITS=" + string.Join(",", result.Page.Diagnostics.LimitsHit)
]);

static string BuildPreview(string fixture, ReadableExtractionResult result)
{
    var body = new StringBuilder();
    foreach (var block in result.Page.Blocks)
    {
        string text = WebUtility.HtmlEncode(block.Text);
        string link = block.Url is null ? text : $"<a href=\"{WebUtility.HtmlEncode(block.Url)}\">{text}</a>";
        body.Append(block.Type switch
        {
            ReadableBlockType.Heading => $"<h2>{link}</h2>",
            ReadableBlockType.Paragraph => $"<p>{link}</p>",
            ReadableBlockType.ListItem => $"<li>{link}</li>",
            ReadableBlockType.Quote => $"<blockquote>{link}</blockquote>",
            ReadableBlockType.Link => $"<p class=\"link\">{link}</p>",
            _ => "<hr>"
        });
    }
    return $"<!doctype html><meta charset=utf-8><title>NICE64 {WebUtility.HtmlEncode(fixture)}</title><style>body{{font:16px system-ui;background:#101522;color:#edf3ff;margin:0}}main{{max-width:640px;margin:24px auto;padding:24px}}aside{{background:#202b43;padding:14px;border-left:4px solid #7ee7ff;font-size:13px}}a{{color:#8de9ff}}.link{{font-weight:700}}blockquote{{border-left:3px solid #7185a8;padding-left:14px}}</style><main><aside><b>NICE64 Local Readability Preview</b><br>Fixture: {WebUtility.HtmlEncode(fixture)}<br>Mode: {result.SelectedMode}<br>Blocks: {result.Page.Blocks.Count}; Links: {result.Page.Links.Count}; Quality: {result.PassesQualityGate}</aside><h1>{WebUtility.HtmlEncode(result.Page.Title)}</h1>{body}</main>";
}

static List<string> Verify(IEnumerable<string> fixtureFiles, string outputRoot)
{
    var failures = new List<string>();
    var files = fixtureFiles.ToDictionary(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);
    foreach (string fixture in fixtureFiles)
    {
        string name = Path.GetFileNameWithoutExtension(fixture);
        string qualityPath = Path.Combine(outputRoot, name, "quality-result.json");
        if (!File.Exists(qualityPath)) failures.Add(name + ":missing-output");
    }
    foreach (string required in new[] { "milk-home", "nintendo-home", "fox-article", "wikipedia-article", "sparse-js-heavy", "malformed-html", "external-links", "relative-links", "nested-cards" })
        if (!files.ContainsKey(required)) failures.Add("missing-fixture:" + required);
    if (failures.Count > 0) return failures;

    var extractor = new NiceReadableExtractor();
    ReadableExtractionResult Extract(string name) => extractor.Extract(File.ReadAllText(files[name]), FixtureUrl(name));
    void Check(bool condition, string name) { if (!condition) failures.Add(name); }
    var article = Extract("fox-article");
    var milk = Extract("milk-home");
    var nintendo = Extract("nintendo-home");
    var cards = Extract("nested-cards");
    var relative = Extract("relative-links");
    var external = Extract("external-links");
    var duplicate = Extract("duplicate-title");
    var malformed = Extract("malformed-html");
    var sparse = Extract("sparse-js-heavy");
    var nav = Extract("many-navigation-links");
    var wiki = Extract("wikipedia-article");
    var textOnly = Extract("no-useful-image");

    Check(article.SelectedMode == "ARTICLE", "article-root-selection");
    Check(milk.SelectedMode == "LANDING", "landing-classification");
    Check(cards.CandidateCards.Count >= 3, "repeated-card-extraction");
    Check(article.Page.Links.Any(link => link.Label == "Storm safety guidance"), "nested-anchor-preservation");
    Check(relative.Page.Links.Any(link => link.Url == "https://fixtures.nice64.local/relative-links/guides/start"), "relative-link-resolution");
    Check(external.Page.Links.Count == 1 && external.Page.Links[0].Url == "https://example.org/report", "unsafe-link-rejection");
    Check(duplicate.Page.Blocks.Count(block => block.Type == ReadableBlockType.Heading && block.Text == "Project Aurora") == 0, "duplicate-title-suppression");
    Check(duplicate.Page.Blocks.Count(block => block.Text.StartsWith("Project Aurora is", StringComparison.Ordinal)) == 1, "adjacent-duplicate-suppression");
    Check(article.Page.Title != article.Page.PrimaryHeading, "title-h1-different");
    Check(malformed.PassesQualityGate, "malformed-html");
    Check(!sparse.PassesQualityGate && sparse.FailureReasons.Contains("INSUFFICIENT_STATIC_CONTENT"), "sparse-page-failure");
    Check(nav.Page.Links.Count == 1, "navigation-clutter-removal");
    Check(wiki.Page.Blocks.Any(block => block.Type == ReadableBlockType.ListItem), "article-list-preservation");
    Check(textOnly.PassesQualityGate, "no-image-required");
    Check(nintendo.CandidateCards.Any(card => card.DestinationUrl.EndsWith("/store/products/madden-nfl-27")), "destination-context-for-image-matcher");

    var sourceLimited = new NiceReadableExtractor(new NiceReadableOptions { MaxSourceBytes = 32 }).Extract("<article><p>This source is deliberately larger than the configured limit.</p></article>", FixtureUrl("fox-article"));
    Check(sourceLimited.FailureReasons.Contains("SOURCE_TOO_LARGE"), "source-size-limit");
    var nodeLimited = new NiceReadableExtractor(new NiceReadableOptions { MaxNodeCount = 4 }).Extract("<main><div><p>One.</p></div><div><p>Two.</p></div></main>", FixtureUrl("fox-article"));
    Check(nodeLimited.FailureReasons.Contains("NODE_LIMIT"), "node-count-limit");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    var cancelledResult = extractor.Extract(File.ReadAllText(files["fox-article"]), FixtureUrl("fox-article"), cancelled.Token);
    Check(cancelledResult.FailureReasons.Contains("CANCELLED"), "cancellation");
    var timeout = new NiceReadableExtractor(new NiceReadableOptions { MaxExtractionTime = TimeSpan.Zero }).Extract(File.ReadAllText(files["fox-article"]), FixtureUrl("fox-article"));
    Check(timeout.FailureReasons.Contains("EXTRACTION_TIMEOUT"), "extraction-timeout");
    Check(cards.Page.Blocks.Select(block => block.SourceOrder).SequenceEqual(cards.Page.Blocks.Select(block => block.SourceOrder).OrderBy(value => value)), "source-order-preservation");
    return failures;
}
