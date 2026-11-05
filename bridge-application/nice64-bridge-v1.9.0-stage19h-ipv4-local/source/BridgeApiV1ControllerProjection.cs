namespace PicoSwitchController;

public sealed record BridgeApiV1ControllerEndpoint(
    bool Available, string? UnavailableReason, string? Address, int? Port,
    string? Transport, string? Protocol, string? ControllerType);

public sealed record BridgeApiV1ControllerPrimary(
    string ApiVersion, string SessionId, string Primary,
    BridgeApiV1ControllerEndpoint Endpoint);

public sealed record BridgeApiV1ControllerFailure(string Code, string Detail);

public static class BridgeApiV1ControllerProjector
{
    public const string ApiVersion = "v1";

    public static BridgeApiV1ControllerPrimary Project(
        string sessionId, BridgeControllerPrimarySnapshot source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(source);
        return new(ApiVersion, sessionId,
            source.Primary == BridgeControllerPrimary.NiceMobile
                ? "nice-mobile" : "bridge",
            Project(source.Endpoint));
    }

    public static BridgeApiV1ControllerEndpoint Project(
        BridgeControllerEndpointSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Available)
            return new(false, EndpointFailure(source.UnavailableReason),
                null, null, null, null, null);
        return new(true, null, source.Address, BridgeControllerEndpointConstants.Port,
            "udp", BridgeControllerEndpointConstants.Protocol,
            BridgeControllerEndpointConstants.ControllerType);
    }

    private static string EndpointFailure(string? reason) => reason switch
    {
        BridgeControllerEndpointUnavailableReason.NoSession => "no-session",
        BridgeControllerEndpointUnavailableReason.SessionNotConnected =>
            "session-not-connected",
        BridgeControllerEndpointUnavailableReason.ControllerFirmwareUnavailable =>
            "controller-firmware-unavailable",
        BridgeControllerEndpointUnavailableReason.PicoWifiEndpointUnverified =>
            "pico-wifi-endpoint-unverified",
        BridgeControllerEndpointUnavailableReason.ProtectedOperation =>
            "protected-operation",
        _ => "endpoint-unavailable"
    };
}
