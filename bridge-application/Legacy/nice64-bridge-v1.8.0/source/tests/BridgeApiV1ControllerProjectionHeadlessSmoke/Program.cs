using PicoSwitchController;

BridgeControllerEndpointSnapshot endpoint =
    BridgeControllerEndpointSnapshot.AvailableAt("session-1", "192.168.2.81");
BridgeControllerPrimarySnapshot snapshot = new("session-1",
    BridgeControllerPrimary.NiceMobile, endpoint, true,
    BridgeControllerPrimaryFailure.None);
BridgeApiV1ControllerPrimary dto = BridgeApiV1ControllerProjector.Project(
    "session-1", snapshot);
if (dto.ApiVersion != "v1" || dto.SessionId != "session-1" ||
    dto.Primary != "nice-mobile" || !dto.Endpoint.Available ||
    dto.Endpoint.Address != "192.168.2.81" || dto.Endpoint.Port != 6465 ||
    dto.Endpoint.Transport != "udp")
    throw new InvalidOperationException("Mobile primary projection mismatch");

BridgeApiV1ControllerPrimary bridge = BridgeApiV1ControllerProjector.Project(
    "session-1", snapshot with
    {
        Primary = BridgeControllerPrimary.Bridge,
        Endpoint = BridgeControllerEndpointSnapshot.Unavailable("session-1",
            BridgeControllerEndpointUnavailableReason.NoSession),
        LocalDeliverySuspended = false
    });
if (bridge.Primary != "bridge" || bridge.Endpoint.Available ||
    bridge.Endpoint.UnavailableReason != "no-session")
    throw new InvalidOperationException("Bridge primary projection mismatch");

Console.WriteLine("BridgeApiV1ControllerProjectionHeadlessSmoke passed");
