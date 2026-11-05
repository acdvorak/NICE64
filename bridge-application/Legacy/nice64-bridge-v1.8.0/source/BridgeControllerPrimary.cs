namespace PicoSwitchController;

public enum BridgeControllerPrimary { Bridge, NiceMobile }

public enum BridgeControllerPrimaryFailure
{
    None, SessionNotFound, StaleSession, SessionNotConnected,
    ControllerFirmwareUnavailable, EndpointUnavailable, ProtectedOperation,
    Cancelled, LocalDeliveryFailure, Unexpected
}

public sealed record BridgeControllerPrimarySnapshot(
    string? SessionId,
    BridgeControllerPrimary Primary,
    BridgeControllerEndpointSnapshot Endpoint,
    bool LocalDeliverySuspended,
    BridgeControllerPrimaryFailure UnavailableReason);

public sealed record BridgeControllerPrimaryResult(
    bool Success,
    BridgeControllerPrimaryFailure Failure,
    BridgeControllerPrimarySnapshot Snapshot);

public sealed class BridgeControllerPrimaryChangedEventArgs(
    BridgeControllerPrimarySnapshot previous,
    BridgeControllerPrimarySnapshot current) : EventArgs
{
    public BridgeControllerPrimarySnapshot Previous { get; } = previous;
    public BridgeControllerPrimarySnapshot Current { get; } = current;
}

public interface IBridgeControllerPrimaryService : IAsyncDisposable
{
    BridgeControllerPrimarySnapshot Current { get; }
    event EventHandler<BridgeControllerPrimaryChangedEventArgs>? CurrentChanged;
    Task<BridgeControllerPrimaryResult> SelectAsync(
        string sessionId,
        BridgeControllerPrimary primary,
        CancellationToken cancellationToken = default);
}

internal interface IBridgeLocalControllerPrimaryPort
{
    ValueTask SuspendLocalAsync(CancellationToken cancellationToken);
    ValueTask ResumeLocalAsync(bool resendCurrent, CancellationToken cancellationToken);
}

internal sealed class DelegateBridgeLocalControllerPrimaryPort(
    Func<CancellationToken, ValueTask> suspendLocal,
    Func<bool, CancellationToken, ValueTask> resumeLocal) :
    IBridgeLocalControllerPrimaryPort
{
    public ValueTask SuspendLocalAsync(CancellationToken cancellationToken) =>
        suspendLocal(cancellationToken);
    public ValueTask ResumeLocalAsync(bool resendCurrent, CancellationToken cancellationToken) =>
        resumeLocal(resendCurrent, cancellationToken);
}
