namespace PicoSwitchController;

public enum BrowserGenerationState
{
    Pending,
    Fetching,
    Converting,
    Encoding,
    Ready,
    Failed,
    Expired,
    Cancelled
}

/// <summary>
/// In-memory lifecycle record for one browser document generation. Jobs are deliberately
/// independent from the Network Pak stream protocol: polling only opens ordinary resources.
/// </summary>
public sealed class BrowserGenerationJob
{
    public required string JobId { get; init; }
    public required string ClientId { get; init; }
    public required string SessionId { get; init; }
    public required string NormalizedUrl { get; init; }
    public required string RequestDirectory { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public DateTime? CompletedUtc { get; set; }
    public BrowserGenerationState State { get; set; }
    public byte[]? WirePayload { get; set; }
    public byte[]? ErrorPayload { get; set; }
    public string? ErrorCode { get; set; }
    public int PollCount { get; set; }
    public bool Cancelled { get; set; }
}
