namespace PicoSwitchController;

using System.Collections.ObjectModel;

public sealed record BridgeApiV1SessionDevice(
    string Identity,
    string FirmwarePersonality,
    string Accessory);

public sealed record BridgeApiV1Session(
    string SessionId,
    string State,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastConnectedUtc,
    string ConnectionKind,
    BridgeApiV1SessionDevice Device);

public sealed record BridgeApiV1Sessions(
    string ApiVersion,
    IReadOnlyList<BridgeApiV1Session> Sessions);

public sealed record BridgeApiV1SessionFailure(string Code, string Detail);

public static class BridgeApiV1SessionProjector
{
    public const string ApiVersion = "v1";

    public static BridgeApiV1Sessions ProjectList(IBridgeSessionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        BridgeSessionSnapshot? current = provider.Current;
        ReadOnlyCollection<BridgeApiV1Session> sessions = current is null
            ? Array.AsReadOnly(Array.Empty<BridgeApiV1Session>())
            : Array.AsReadOnly([Project(current)]);
        return new(ApiVersion, sessions);
    }

    public static bool TryProjectCurrent(
        IBridgeSessionProvider provider,
        string sessionId,
        out BridgeApiV1Session? session)
    {
        ArgumentNullException.ThrowIfNull(provider);
        BridgeSessionSnapshot? current = provider.Current;
        if (current is null || !string.Equals(
                current.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            session = null;
            return false;
        }

        session = Project(current);
        return true;
    }

    public static BridgeApiV1Session Project(BridgeSessionSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(
            source.SessionId,
            State(source.State),
            source.CreatedUtc.ToUniversalTime(),
            source.LastConnectedUtc.ToUniversalTime(),
            ConnectionKind(source.ConnectionKind),
            new(source.DeviceIdentity,
                Firmware(source.FirmwarePersonality),
                Accessory(source.ActiveAccessory)));
    }

    private static string State(BridgeSessionState value) => value switch
    {
        BridgeSessionState.Connected => "connected",
        BridgeSessionState.TemporarilyUnavailable => "temporarily-unavailable",
        _ => "unknown"
    };

    private static string ConnectionKind(BridgeControlConnectionKind value) => value switch
    {
        BridgeControlConnectionKind.SerialUsb => "usb",
        BridgeControlConnectionKind.WifiTcp => "wifi-tcp",
        _ => "unknown"
    };

    private static string Firmware(FirmwarePersonality value) => value switch
    {
        FirmwarePersonality.Controller => "controller",
        FirmwarePersonality.Diagnostics => "diagnostics",
        FirmwarePersonality.Network => "network",
        _ => "unknown"
    };

    private static string Accessory(BridgeAccessoryMode value) => value switch
    {
        BridgeAccessoryMode.None => "none",
        BridgeAccessoryMode.ControllerPak => "controller-pak",
        BridgeAccessoryMode.RumblePak => "rumble-pak",
        BridgeAccessoryMode.TransferPak => "transfer-pak",
        BridgeAccessoryMode.NetworkPak => "network-pak",
        _ => "unknown"
    };
}
