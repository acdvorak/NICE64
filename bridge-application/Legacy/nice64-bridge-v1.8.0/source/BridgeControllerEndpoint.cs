namespace PicoSwitchController;

public static class BridgeControllerEndpointConstants
{
    public const int Port = 6465;
    public const string Protocol = "latest-state-v1";
    public const string ControllerType = "n64";
}

public static class BridgeControllerEndpointUnavailableReason
{
    public const string None = "";
    public const string NoSession = "no-session";
    public const string SessionNotConnected = "session-not-connected";
    public const string ControllerFirmwareUnavailable =
        "controller-firmware-unavailable";
    public const string PicoWifiEndpointUnverified =
        "pico-wifi-endpoint-unverified";
    public const string ProtectedOperation = "protected-operation";
}

public sealed record BridgeControllerEndpointSnapshot(
    string? SessionId,
    bool Available,
    string UnavailableReason,
    string? Address,
    int? Port,
    string? Protocol,
    string? ControllerType)
{
    public static BridgeControllerEndpointSnapshot Unavailable(
        string? sessionId,
        string reason) =>
        new(sessionId, false, reason, null, null, null, null);

    public static BridgeControllerEndpointSnapshot AvailableAt(
        string sessionId,
        string address) =>
        new(sessionId, true, BridgeControllerEndpointUnavailableReason.None,
            address, BridgeControllerEndpointConstants.Port,
            BridgeControllerEndpointConstants.Protocol,
            BridgeControllerEndpointConstants.ControllerType);
}

public interface IBridgeControllerEndpointProvider
{
    Task<BridgeControllerEndpointSnapshot> ResolveCurrentAsync(
        CancellationToken cancellationToken = default);
}

internal enum BridgeControllerEndpointProtection
{
    None,
    LineOnly,
    All
}

internal interface IBridgeControllerEndpointProtectionProvider
{
    BridgeControllerEndpointProtection Current { get; }
}

internal sealed class DelegateBridgeControllerEndpointProtectionProvider(
    Func<BridgeControllerEndpointProtection> current) :
    IBridgeControllerEndpointProtectionProvider
{
    private readonly Func<BridgeControllerEndpointProtection> _current =
        current ?? throw new ArgumentNullException(nameof(current));

    public BridgeControllerEndpointProtection Current => _current();
}

internal interface IBridgeControllerTcpEndpointAuthority
{
    string? GetVerifiedIpv4(BridgeSessionSnapshot session);
}

internal sealed class DelegateBridgeControllerTcpEndpointAuthority(
    Func<BridgeSessionSnapshot, string?> getVerifiedIpv4) :
    IBridgeControllerTcpEndpointAuthority
{
    private readonly Func<BridgeSessionSnapshot, string?> _getVerifiedIpv4 =
        getVerifiedIpv4 ?? throw new ArgumentNullException(nameof(getVerifiedIpv4));

    public string? GetVerifiedIpv4(BridgeSessionSnapshot session) =>
        _getVerifiedIpv4(session);
}

internal enum BridgeControllerUsbWifiStatusOutcome
{
    Confirmed,
    Unavailable,
    ProtectedOperation
}

internal sealed record BridgeControllerUsbWifiStatusResult(
    BridgeControllerUsbWifiStatusOutcome Outcome,
    bool Connected,
    string? IpAddress)
{
    public static BridgeControllerUsbWifiStatusResult ConfirmedStatus(
        bool connected,
        string? ipAddress) =>
        new(BridgeControllerUsbWifiStatusOutcome.Confirmed, connected, ipAddress);

    public static BridgeControllerUsbWifiStatusResult Unavailable { get; } =
        new(BridgeControllerUsbWifiStatusOutcome.Unavailable, false, null);

    public static BridgeControllerUsbWifiStatusResult Protected { get; } =
        new(BridgeControllerUsbWifiStatusOutcome.ProtectedOperation, false, null);
}

internal interface IBridgeControllerUsbWifiStatusAuthority
{
    Task<BridgeControllerUsbWifiStatusResult> ReadFreshAsync(
        CancellationToken cancellationToken);
}

internal sealed class DelegateBridgeControllerUsbWifiStatusAuthority(
    Func<CancellationToken, Task<BridgeControllerUsbWifiStatusResult>> readFresh) :
    IBridgeControllerUsbWifiStatusAuthority
{
    private readonly Func<CancellationToken,
        Task<BridgeControllerUsbWifiStatusResult>> _readFresh =
        readFresh ?? throw new ArgumentNullException(nameof(readFresh));

    public Task<BridgeControllerUsbWifiStatusResult> ReadFreshAsync(
        CancellationToken cancellationToken) => _readFresh(cancellationToken);
}
