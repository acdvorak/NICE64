using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;

namespace Nice64.BrowserLive.Readability;

/// <summary>
/// Bounded, local-only HTML readability extraction for Browser Live. This has no network,
/// wire-format, or ROM dependency and is intentionally reusable by a future module integration.
/// </summary>
public sealed class NiceReadableExtractor
{
    private static readonly HashSet<string> IgnoredTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "iframe", "form", "canvas", "video", "audio", "embed", "object", "svg", "dialog", "template"
    };

    private static readonly string[] NegativeSignals =
    [
        "cookie", "consent", "advert", "ads", "sponsor", "social", "share", "account", "login", "signup", "newsletter",
        "modal", "dialog", "toolbar", "breadcrumb", "navigation", "footer", "sidebar", "pagination", "comment", "rating",
        "search", "cart", "language", "region", "utility"
    ];

    private static readonly string[] PositiveSignals =
    ["article", "story", "entry", "post", "content", "main", "body", "recipe", "feature", "featured", "hero", "news", "product", "game", "card", "tile", "summary", "description"];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    { "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "of", "on", "or", "the", "to", "with" };

    private readonly NiceReadableOptions _options;

    public NiceReadableExtractor(NiceReadableOptions? options = null)
    {
        _options = options ?? new NiceReadableOptions();
    }

    public ReadableExtractionResult Extract(string html, Uri requestedUrl, CancellationToken cancellationToken = default)
    {
        var diagnostics = new ReadableDiagnostics();
        var page = new ReadablePageModel { RequestedUrl = requestedUrl.AbsoluteUri, CanonicalUrl = requestedUrl.AbsoluteUri };
        var failures = new List<string>();
        if (Encoding.UTF8.GetByteCount(html) > _options.MaxSourceBytes)
        {
            diagnostics.LimitsHit.Add("MAX_SOURCE_BYTES");
            failures.Add("SOURCE_TOO_LARGE");
            return Finish(false, false, "SPARSE", page, diagnostics, failures, [], []);
        }

        diagnostics.SourceBytes = Encoding.UTF8.GetByteCount(html);
        var parseWatch = Stopwatch.StartNew();
        var document = new HtmlDocument { OptionFixNestedTags = true, OptionAutoCloseOnEnd = true };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            document.LoadHtml(html);
        }
        catch (OperationCanceledException)
        {
            diagnostics.LimitsHit.Add("CANCELLED");
            failures.Add("CANCELLED");
            return Finish(false, false, "SPARSE", page, diagnostics, failures, [], []);
        }
        catch (Exception exception)
        {
            failures.Add("HTML_PARSE_FAILED:" + exception.GetType().Name);
            return Finish(false, false, "SPARSE", page, diagnostics, failures, [], []);
        }
        parseWatch.Stop();
        diagnostics.ParseMilliseconds = parseWatch.ElapsedMilliseconds;
        if (parseWatch.Elapsed > _options.MaxParseTime)
        {
            diagnostics.LimitsHit.Add("MAX_PARSE_TIME");
            failures.Add("PARSE_TIMEOUT");
            return Finish(false, false, "SPARSE", page, diagnostics, failures, [], []);
        }

        var extractionWatch = Stopwatch.StartNew();
        try
        {
            var allNodes = document.DocumentNode.DescendantsAndSelf().ToList();
            diagnostics.NodesVisited = allNodes.Count;
            diagnostics.MaximumDepthObserved = allNodes.Count == 0 ? 0 : allNodes.Max(NodeDepth);
            if (allNodes.Count > _options.MaxNodeCount)
            {
                diagnostics.LimitsHit.Add("MAX_NODE_COUNT");
                failures.Add("NODE_LIMIT");
                return Finish(false, false, "SPARSE", page, diagnostics, failures, [], []);
            }
            if (diagnostics.MaximumDepthObserved > _options.MaxDomDepth)
            {
                diagnostics.LimitsHit.Add("MAX_DOM_DEPTH");
                failures.Add("DOM_DEPTH_LIMIT");
                return Finish(false, false, "SPARSE", page, diagnostics, failures, [], []);
            }

            page.Title = GetTitle(document);
            page.PrimaryHeading = GetPrimaryHeading(document);
            page.DateText = GetDate(document);
            var candidates = ScoreContainers(document, cancellationToken, extractionWatch);
            diagnostics.CandidateContainers = candidates.Count;
            var cards = FindCards(document, requestedUrl, cancellationToken, extractionWatch);
            diagnostics.CardsFound = cards.Count;
            var mode = Classify(document, candidates, cards, out var classifierReason);
            diagnostics.SelectedMode = mode;
            diagnostics.ClassifierReason = classifierReason;

            if (mode == "ARTICLE")
            {
                var root = candidates.OrderByDescending(x => x.Score).FirstOrDefault()?.Node;
                if (root is not null) AppendBlocks(page, root, requestedUrl, null, cancellationToken, extractionWatch);
            }
            else if (mode == "LANDING")
            {
                foreach (var card in cards.OrderBy(x => x.SourceOrder).Take(8))
                {
                    AppendBlocks(page, card.Node, requestedUrl, card.GroupId, cancellationToken, extractionWatch);
                }
            }
            else
            {
                failures.Add("INSUFFICIENT_STATIC_CONTENT");
            }

            Deduplicate(page);
            PopulateSummary(page);
            diagnostics.BlocksRetained = page.Blocks.Count;
            diagnostics.LinksRetained = page.Links.Count;
            diagnostics.TextRetained = page.Blocks.Sum(x => x.Text.Length);
            bool quality = PassesQualityGate(page, mode, failures);
            extractionWatch.Stop();
            diagnostics.ExtractionMilliseconds = extractionWatch.ElapsedMilliseconds;
            return Finish(quality, quality, mode, page, diagnostics, failures,
                candidates.Select(x => x.ToModel(x.Node == candidates.OrderByDescending(c => c.Score).FirstOrDefault()?.Node)).ToList(),
                cards.Select(x => x.ToModel()).ToList());
        }
        catch (OperationCanceledException)
        {
            diagnostics.LimitsHit.Add("CANCELLED");
            failures.Add("CANCELLED");
        }
        catch (TimeoutException)
        {
            diagnostics.LimitsHit.Add("MAX_EXTRACTION_TIME");
            failures.Add("EXTRACTION_TIMEOUT");
        }
        catch (Exception exception)
        {
            failures.Add("EXTRACTION_FAILED:" + exception.GetType().Name);
        }
        finally
        {
            extractionWatch.Stop();
            diagnostics.ExtractionMilliseconds = extractionWatch.ElapsedMilliseconds;
        }
        return Finish(false, false, "SPARSE", page, diagnostics, failures, [], []);
    }

    private ReadableExtractionResult Finish(bool success, bool passesQualityGate, string mode, ReadablePageModel page, ReadableDiagnostics diagnostics,
        List<string> failures, IReadOnlyList<ReadableCandidateContainer> candidates, IReadOnlyList<ReadableCardCandidate> cards)
    {
        page.Diagnostics.SelectedMode = diagnostics.SelectedMode;
        page.Diagnostics.ClassifierReason = diagnostics.ClassifierReason;
        page.Diagnostics.SourceBytes = diagnostics.SourceBytes;
        page.Diagnostics.NodesVisited = diagnostics.NodesVisited;
        page.Diagnostics.MaximumDepthObserved = diagnostics.MaximumDepthObserved;
        page.Diagnostics.CandidateContainers = diagnostics.CandidateContainers;
        page.Diagnostics.CardsFound = diagnostics.CardsFound;
        page.Diagnostics.BlocksRetained = diagnostics.BlocksRetained;
        page.Diagnostics.LinksRetained = diagnostics.LinksRetained;
        page.Diagnostics.TextRetained = diagnostics.TextRetained;
        page.Diagnostics.ParseMilliseconds = diagnostics.ParseMilliseconds;
        page.Diagnostics.ExtractionMilliseconds = diagnostics.ExtractionMilliseconds;
        page.Diagnostics.LimitsHit.AddRange(diagnostics.LimitsHit);
        page.Diagnostics.Notes.AddRange(diagnostics.Notes);
        return new ReadableExtractionResult { Success = success, PassesQualityGate = passesQualityGate, SelectedMode = mode, Page = page, FailureReasons = new ReadOnlyCollection<string>(failures), CandidateContainers = candidates, CandidateCards = cards };
    }

    private List<ContainerScore> ScoreContainers(HtmlDocument document, CancellationToken token, Stopwatch watch)
    {
        var candidates = new List<ContainerScore>();
        foreach (var node in document.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element && IsContainer(n)))
        {
            CheckBudget(token, watch);
            if (IsNoise(node)) continue;
            string text = CleanDisplay(node.InnerText);
            int paragraphs = node.Descendants("p").Count(p => CleanDisplay(p.InnerText).Length >= 40 && !IsNoise(p));
            int headings = node.Descendants().Count(IsHeading);
            var anchors = node.Descendants("a").Where(a => !IsNoise(a)).ToList();
            int linkChars = anchors.Sum(a => CleanDisplay(a.InnerText).Length);
            double density = text.Length == 0 ? 1 : (double)linkChars / text.Length;
            var positive = new List<string>();
            var negative = new List<string>();
            double score = paragraphs * 24 + Math.Min(text.Length, 6000) / 45d + headings * 9;
            if (node.Name.Equals("article", StringComparison.OrdinalIgnoreCase)) { score += 55; positive.Add("article-tag"); }
            if (node.Name.Equals("main", StringComparison.OrdinalIgnoreCase)) { score += 45; positive.Add("main-tag"); }
            score += SignalScore(node, PositiveSignals, 8, positive);
            score -= SignalScore(node, NegativeSignals, 25, negative);
            if (density > .55) { score -= 55; negative.Add("high-link-density"); }
            if (paragraphs == 0) { score -= 20; negative.Add("no-substantial-paragraphs"); }
            candidates.Add(new ContainerScore(node, score, text.Length, paragraphs, headings, anchors.Count, density, positive, negative));
        }
        return candidates.OrderByDescending(x => x.Score).Take(_options.MaxCandidateContainers).ToList();
    }

    private List<CardScore> FindCards(HtmlDocument document, Uri baseUri, CancellationToken token, Stopwatch watch)
    {
        var cards = new List<CardScore>();
        int sequence = 0;
        foreach (var node in document.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element && (n.Name is "article" or "section" or "li" or "div")))
        {
            CheckBudget(token, watch);
            if (IsNoise(node)) continue;
            var headingNode = node.Descendants().FirstOrDefault(IsHeading);
            var anchor = node.Descendants("a").FirstOrDefault(a => TryResolveLink(a.GetAttributeValue("href", string.Empty), baseUri, out _));
            if (headingNode is null || anchor is null) continue;
            string heading = CleanDisplay(headingNode.InnerText);
            string linkText = CleanDisplay(anchor.InnerText);
            if (heading.Length < 3 || linkText.Length < 2 || !TryResolveLink(anchor.GetAttributeValue("href", string.Empty), baseUri, out string destination)) continue;
            string summary = node.Descendants("p").Select(p => CleanDisplay(p.InnerText)).FirstOrDefault(x => x.Length >= 30) ?? string.Empty;
            double score = 30 + Math.Min(heading.Length, 100) / 8d + (summary.Length >= 30 ? 20 : 0);
            var reasons = new List<string> { "heading+destination" };
            if (HasSignal(node, PositiveSignals)) { score += 8; reasons.Add("positive-context"); }
            if (summary.Length >= 80) { score += 8; reasons.Add("summary"); }
            cards.Add(new CardScore(node, $"card-{sequence++:D3}", heading, summary, linkText, destination, score, reasons, GetSourceOrder(node)));
        }
        return cards.Where(card => !cards.Any(other => other != card && IsAncestor(card.Node, other.Node) && other.Score >= card.Score)).OrderByDescending(x => x.Score).Take(16).ToList();
    }

    private static string Classify(HtmlDocument document, List<ContainerScore> candidates, List<CardScore> cards, out string reason)
    {
        var best = candidates.OrderByDescending(x => x.Score).FirstOrDefault();
        if (cards.Count >= 2 && (best is null || !best.Node.Name.Equals("article", StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"repeated-coherent-cards count={cards.Count}";
            return "LANDING";
        }
        if (best is not null && (best.Node.Name.Equals("article", StringComparison.OrdinalIgnoreCase) || best.Paragraphs >= 3) && best.Score >= 75)
        {
            reason = $"article-root score={best.Score:F1} paragraphs={best.Paragraphs}";
            return "ARTICLE";
        }
        if (cards.Count >= 1)
        {
            reason = $"coherent-cards count={cards.Count}";
            return "LANDING";
        }
        reason = "no-article-root-or-coherent-cards";
        return "SPARSE";
    }

    private void AppendBlocks(ReadablePageModel page, HtmlNode root, Uri baseUri, string? groupId, CancellationToken token, Stopwatch watch)
    {
        foreach (var node in root.DescendantsAndSelf())
        {
            CheckBudget(token, watch);
            if (node.NodeType != HtmlNodeType.Element || IsNoise(node)) continue;
            ReadableBlockType? type = node.Name.ToLowerInvariant() switch
            {
                "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => ReadableBlockType.Heading,
                "p" => ReadableBlockType.Paragraph,
                "li" => ReadableBlockType.ListItem,
                "blockquote" => ReadableBlockType.Quote,
                "a" => ReadableBlockType.Link,
                _ => null
            };
            if (type is null || HasSemanticAncestor(node, root)) continue;
            string text = type == ReadableBlockType.Link ? CleanDisplay(node.InnerText) : CleanDisplay(OwnText(node));
            if (text.Length < (type == ReadableBlockType.Link ? 2 : 3)) continue;
            string? url = null;
            if (type == ReadableBlockType.Link)
            {
                if (!AddLink(page, node, text, baseUri, groupId, out url)) continue;
            }
            if (page.Blocks.Count >= _options.MaxBlocks || page.Blocks.Sum(x => x.Text.Length) + text.Length > _options.MaxTextCharacters) break;
            page.Blocks.Add(new ReadableBlock { Type = type.Value, Text = text, Url = url, SourceOrder = GetSourceOrder(node), SourcePath = NodePath(node), Confidence = groupId is null ? .9 : .8, GroupId = groupId });

            // Preserve meaningful inline anchors even though their parent paragraph/list block
            // owns the surrounding text. This keeps the semantic block walk non-duplicative
            // while retaining destinations for later NICE link emission and image matching.
            if (type != ReadableBlockType.Link)
            {
                foreach (var anchor in node.Descendants("a"))
                {
                    if (page.Blocks.Count >= _options.MaxBlocks || page.Links.Count >= _options.MaxLinks) break;
                    string label = CleanDisplay(anchor.InnerText);
                    if (label.Length < 2 || !AddLink(page, anchor, label, baseUri, groupId, out string? linkUrl)) continue;
                    page.Blocks.Add(new ReadableBlock { Type = ReadableBlockType.Link, Text = label, Url = linkUrl, SourceOrder = GetSourceOrder(anchor), SourcePath = NodePath(anchor), Confidence = groupId is null ? .9 : .8, GroupId = groupId });
                }
            }
        }
    }

    private bool AddLink(ReadablePageModel page, HtmlNode node, string label, Uri baseUri, string? groupId, out string? url)
    {
        url = null;
        if (!TryResolveLink(node.GetAttributeValue("href", string.Empty), baseUri, out string resolved)) return false;
        url = resolved;
        if (page.Links.Count < _options.MaxLinks)
            page.Links.Add(new ReadableLink { Label = label, Url = resolved, SourceOrder = GetSourceOrder(node), ParentHeading = page.PrimaryHeading, GroupId = groupId });
        return true;
    }

    private static bool PassesQualityGate(ReadablePageModel page, string mode, List<string> failures)
    {
        int bodyCharacters = page.Blocks.Where(b => b.Type is ReadableBlockType.Paragraph or ReadableBlockType.ListItem or ReadableBlockType.Quote).Sum(b => b.Text.Length);
        bool pass = mode switch
        {
            "ARTICLE" => (!string.IsNullOrWhiteSpace(page.Title) || !string.IsNullOrWhiteSpace(page.PrimaryHeading)) && bodyCharacters >= 80 && page.Blocks.Any(b => b.Type == ReadableBlockType.Paragraph),
            "LANDING" => !string.IsNullOrWhiteSpace(page.Title) && (page.Links.Count > 0 || bodyCharacters >= 80),
            _ => false
        };
        if (!pass && failures.Count == 0) failures.Add("QUALITY_GATE_FAILED");
        return pass;
    }

    private static void Deduplicate(ReadablePageModel page)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        page.Blocks.RemoveAll(block =>
        {
            string key = NormalizeComparison(block.Text);
            if (key.Length == 0) return true;
            if (block.Type == ReadableBlockType.Heading && Similar(page.Title, block.Text)) return true;
            if (!seen.Add($"{block.Type}:{key}")) return true;
            return false;
        });
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        page.Links.RemoveAll(link => !links.Add(NormalizeUrl(link.Url) + "|" + NormalizeComparison(link.Label)));
    }

    private static void PopulateSummary(ReadablePageModel page)
    {
        page.PrimaryHeading = page.Blocks.FirstOrDefault(b => b.Type == ReadableBlockType.Heading)?.Text ?? page.PrimaryHeading;
        page.Summary = page.Blocks.FirstOrDefault(b => b.Type == ReadableBlockType.Paragraph)?.Text ?? string.Empty;
        page.PrimaryLink = page.Links.FirstOrDefault()?.Url ?? string.Empty;
    }

    private static string GetTitle(HtmlDocument document) => CleanDisplay(document.DocumentNode.SelectSingleNode("//title")?.InnerText ?? document.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", string.Empty) ?? string.Empty);
    private static string GetPrimaryHeading(HtmlDocument document) => CleanDisplay(document.DocumentNode.Descendants().FirstOrDefault(IsHeading)?.InnerText ?? string.Empty);
    private static string GetDate(HtmlDocument document) => CleanDisplay(document.DocumentNode.Descendants("time").FirstOrDefault()?.GetAttributeValue("datetime", string.Empty) ?? string.Empty);
    private static bool IsContainer(HtmlNode node) => node.Name is "article" or "main" or "section" or "div";
    private static bool IsHeading(HtmlNode node) => node.NodeType == HtmlNodeType.Element && node.Name.Length == 2 && node.Name[0] == 'h' && node.Name[1] is >= '1' and <= '6';
    private static bool IsNoise(HtmlNode node) => IgnoredTags.Contains(node.Name) || HasSignal(node, NegativeSignals) || node.GetAttributeValue("hidden", null) is not null || node.GetAttributeValue("aria-hidden", string.Empty).Equals("true", StringComparison.OrdinalIgnoreCase) || node.GetAttributeValue("style", string.Empty).Contains("display:none", StringComparison.OrdinalIgnoreCase);
    private static bool HasSignal(HtmlNode node, IEnumerable<string> terms) => Terms(node).Any(term => terms.Any(value => term.Contains(value, StringComparison.OrdinalIgnoreCase)));
    private static double SignalScore(HtmlNode node, IEnumerable<string> terms, double weight, List<string> reasons) { double score = 0; foreach (string term in terms) if (Terms(node).Any(value => value.Contains(term, StringComparison.OrdinalIgnoreCase))) { score += weight; reasons.Add(term); } return score; }
    private static IEnumerable<string> Terms(HtmlNode node) { yield return node.GetAttributeValue("class", string.Empty); yield return node.GetAttributeValue("id", string.Empty); yield return node.GetAttributeValue("role", string.Empty); }
    private static int NodeDepth(HtmlNode node) { int depth = 0; for (var current = node.ParentNode; current is not null; current = current.ParentNode) depth++; return depth; }
    private static int GetSourceOrder(HtmlNode node) => node.StreamPosition;
    private static string NodePath(HtmlNode node) => node.XPath;
    private static bool IsAncestor(HtmlNode ancestor, HtmlNode node) { for (var current = node.ParentNode; current is not null; current = current.ParentNode) if (current == ancestor) return true; return false; }
    private static bool HasSemanticAncestor(HtmlNode node, HtmlNode root) { for (var parent = node.ParentNode; parent is not null && parent != root; parent = parent.ParentNode) if (parent.Name is "p" or "li" or "blockquote" or "a" || IsHeading(parent)) return true; return false; }
    private static string OwnText(HtmlNode node) => string.Concat(node.ChildNodes.Where(child => child.NodeType == HtmlNodeType.Text).Select(child => child.InnerText));
    private static string CleanDisplay(string text) => Regex.Replace(WebUtility.HtmlDecode(text ?? string.Empty).Replace('\u00a0', ' '), "\\s+", " ").Trim();
    private static string NormalizeComparison(string text) => string.Join(' ', Regex.Matches(CleanDisplay(text).ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value).Where(token => !StopWords.Contains(token)));
    private static bool Similar(string left, string right) { var a = NormalizeComparison(left).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(); var b = NormalizeComparison(right).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(); return a.Count > 0 && (double)a.Intersect(b).Count() / Math.Max(a.Count, b.Count) >= .85; }
    private static string NormalizeUrl(string url) { var uri = new Uri(url); return uri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant(); }
    private static bool TryResolveLink(string href, Uri baseUri, out string resolved) { resolved = string.Empty; href = WebUtility.HtmlDecode(href ?? string.Empty).Trim(); if (href.Length == 0 || href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return false; if (!Uri.TryCreate(baseUri, href, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false; var builder = new UriBuilder(uri) { Fragment = string.Empty }; resolved = builder.Uri.AbsoluteUri; return true; }
    private void CheckBudget(CancellationToken token, Stopwatch watch) { token.ThrowIfCancellationRequested(); if (watch.Elapsed > _options.MaxExtractionTime) throw new TimeoutException(); }

    private sealed record ContainerScore(HtmlNode Node, double Score, int TextChars, int Paragraphs, int Headings, int Links, double LinkDensity, IReadOnlyList<string> Positive, IReadOnlyList<string> Negative)
    { public ReadableCandidateContainer ToModel(bool selected) => new() { SourcePath = NodePath(Node), Tag = Node.Name, Id = Node.GetAttributeValue("id", string.Empty), Class = Node.GetAttributeValue("class", string.Empty), TextCharacters = TextChars, ParagraphCount = Paragraphs, HeadingCount = Headings, LinkCount = Links, LinkDensity = LinkDensity, Score = Score, Selected = selected, PositiveReasons = Positive, NegativeReasons = Negative }; }
    private sealed record CardScore(HtmlNode Node, string GroupId, string Heading, string Summary, string LinkText, string Destination, double Score, IReadOnlyList<string> Reasons, int SourceOrder)
    { public ReadableCardCandidate ToModel() => new() { GroupId = GroupId, Heading = Heading, Summary = Summary, LinkText = LinkText, DestinationUrl = Destination, SourceOrder = SourceOrder, Score = Score, ScoreReasons = Reasons, SourcePath = NodePath(Node) }; }
}
