namespace PicoSwitchController;

public enum BridgeSessionState
{
    Connected,
    TemporarilyUnavailable
}

public sealed record BridgeSessionSnapshot(
    string SessionId,
    BridgeSessionState State,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastConnectedUtc,
    BridgeControlConnectionKind ConnectionKind,
    string DisplayEndpoint,
    string DeviceIdentity,
    FirmwarePersonality FirmwarePersonality,
    BridgeAccessoryMode ActiveAccessory);

public sealed class BridgeSessionChangedEventArgs(
    BridgeSessionSnapshot? previous,
    BridgeSessionSnapshot? current) : EventArgs
{
    public BridgeSessionSnapshot? Previous { get; } = previous;
    public BridgeSessionSnapshot? Current { get; } = current;
}

public interface IBridgeSessionProvider
{
    BridgeSessionSnapshot? Current { get; }
    event EventHandler<BridgeSessionChangedEventArgs>? CurrentChanged;
}

public interface IBridgeSessionService : IBridgeSessionProvider, IDisposable
{
}

internal sealed record BridgeSessionConnectionFacts(
    BridgeControlConnectionKind ConnectionKind,
    string DisplayEndpoint,
    string DeviceIdentity);

internal interface IBridgeSessionLifecycle
{
    void FreshConnected(BridgeSessionConnectionFacts connection);
    void TemporarilyUnavailable();
    void Restored(BridgeSessionConnectionFacts connection);
    void Ended();
}
