using System.Collections.ObjectModel;

namespace Nice64.BrowserLive.Readability;

public enum ReadableBlockType
{
    Heading,
    Paragraph,
    ListItem,
    Quote,
    Link,
    Separator
}

public sealed class ReadableBlock
{
    public ReadableBlockType Type { get; init; }
    public required string Text { get; init; }
    public string? Url { get; init; }
    public int SourceOrder { get; init; }
    public required string SourcePath { get; init; }
    public double Confidence { get; init; }
    public string? GroupId { get; init; }
}

public sealed class ReadableLink
{
    public required string Label { get; init; }
    public required string Url { get; init; }
    public int SourceOrder { get; init; }
    public string? ParentHeading { get; init; }
    public string? GroupId { get; init; }
}

public sealed class ReadableCardCandidate
{
    public required string GroupId { get; init; }
    public string Heading { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string LinkText { get; init; } = string.Empty;
    public string DestinationUrl { get; init; } = string.Empty;
    public int SourceOrder { get; init; }
    public double Score { get; init; }
    public IReadOnlyList<string> ScoreReasons { get; init; } = Array.Empty<string>();
    public string SourcePath { get; init; } = string.Empty;
}

public sealed class ReadableCandidateContainer
{
    public required string SourcePath { get; init; }
    public required string Tag { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Class { get; init; } = string.Empty;
    public int TextCharacters { get; init; }
    public int ParagraphCount { get; init; }
    public int HeadingCount { get; init; }
    public int LinkCount { get; init; }
    public double LinkDensity { get; init; }
    public double Score { get; init; }
    public bool Selected { get; init; }
    public IReadOnlyList<string> PositiveReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NegativeReasons { get; init; } = Array.Empty<string>();
}

public sealed class ReadableDiagnostics
{
    public string SelectedMode { get; set; } = "SPARSE";
    public string ClassifierReason { get; set; } = string.Empty;
    public long SourceBytes { get; set; }
    public int NodesVisited { get; set; }
    public int MaximumDepthObserved { get; set; }
    public int CandidateContainers { get; set; }
    public int CardsFound { get; set; }
    public int BlocksRetained { get; set; }
    public int LinksRetained { get; set; }
    public int TextRetained { get; set; }
    public long ParseMilliseconds { get; set; }
    public long ExtractionMilliseconds { get; set; }
    public List<string> LimitsHit { get; } = [];
    public List<string> Notes { get; } = [];
}

public sealed class ReadablePageModel
{
    public string SourceMode { get; set; } = "LOCAL";
    public required string RequestedUrl { get; init; }
    public required string CanonicalUrl { get; init; }
    public string Title { get; set; } = string.Empty;
    public string PrimaryHeading { get; set; } = string.Empty;
    public string DateText { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string PrimaryLink { get; set; } = string.Empty;
    public List<ReadableBlock> Blocks { get; } = [];
    public List<ReadableLink> Links { get; } = [];
    public ReadableDiagnostics Diagnostics { get; } = new();
}

public sealed class ReadableExtractionResult
{
    public bool Success { get; init; }
    public bool PassesQualityGate { get; init; }
    public string SelectedMode { get; init; } = "SPARSE";
    public required ReadablePageModel Page { get; init; }
    public IReadOnlyList<string> FailureReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ReadableCandidateContainer> CandidateContainers { get; init; } = Array.Empty<ReadableCandidateContainer>();
    public IReadOnlyList<ReadableCardCandidate> CandidateCards { get; init; } = Array.Empty<ReadableCardCandidate>();
}
