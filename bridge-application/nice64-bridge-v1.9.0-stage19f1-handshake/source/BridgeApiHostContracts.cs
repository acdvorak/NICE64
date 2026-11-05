namespace PicoSwitchController;

public interface IBridgeApiTokenProvider
{
    string GetToken();
}

public enum BridgeApiHostStartDisposition
{
    Started,
    AlreadyRunning,
    Disabled,
    Failed
}

public sealed record BridgeApiHostStartResult(
    BridgeApiHostStartDisposition Disposition,
    bool Running,
    string? FailureCode,
    string? SafeDetail)
{
    public bool Success => Disposition is BridgeApiHostStartDisposition.Started or
        BridgeApiHostStartDisposition.AlreadyRunning;
}

public interface IBridgeApiHost : IAsyncDisposable
{
    bool IsRunning { get; }
    Task<BridgeApiHostStartResult> StartAsync(
        CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
