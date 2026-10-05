namespace PicoSwitchController;

public static class BridgeNetworkPakQuickActivationPolicy
{
    public static bool CanActivate(
        IBridgeDeviceStateProvider deviceState,
        bool tcpControlConnected)
    {
        ArgumentNullException.ThrowIfNull(deviceState);
        return deviceState.Current.FirmwarePersonality == FirmwarePersonality.Network &&
            tcpControlConnected;
    }
}
