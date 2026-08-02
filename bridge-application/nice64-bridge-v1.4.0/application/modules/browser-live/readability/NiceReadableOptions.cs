namespace Nice64.BrowserLive.Readability;

public sealed class NiceReadableOptions
{
    public int MaxSourceBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxNodeCount { get; init; } = 50_000;
    public int MaxDomDepth { get; init; } = 128;
    public int MaxBlocks { get; init; } = 120;
    public int MaxLinks { get; init; } = 24;
    public int MaxTextCharacters { get; init; } = 12_000;
    public int MaxCandidateContainers { get; init; } = 96;
    public TimeSpan MaxParseTime { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxExtractionTime { get; init; } = TimeSpan.FromSeconds(2);
}
