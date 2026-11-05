namespace PicoSwitchController;

internal sealed record BridgeDesktopPresentationSnapshot(
    FirmwarePersonality FirmwarePersonality,
    FirmwareModeSwitchState FirmwarePresentationState,
    FirmwarePersonality FirmwareTransitionTarget,
    BridgeAccessoryMode ActiveAccessory,
    BridgeNetworkPakTransport SelectedNetworkPakTransport,
    string ActiveNetworkPakModuleId);

internal sealed record BridgeDesktopPresentationConvergencePlan(
    BridgeDesktopPresentationSnapshot Presentation,
    bool RefreshFirmware,
    bool RefreshAccessory,
    bool RefreshNetworkPakModule,
    bool RefreshNetworkPakCarrier);

internal static class BridgeDesktopPresentationComposer
{
    internal static BridgeDesktopPresentationSnapshot Compose(
        BridgeDeviceStateSnapshot device,
        BridgeNetworkPakModuleSnapshot module)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(module);

        return new(
            device.FirmwarePersonality,
            FirmwarePresentationState(device),
            device.FirmwareTransition.Target,
            device.ActiveAccessory,
            device.SelectedNetworkPakTransport,
            module.ActiveModuleId);
    }

    private static FirmwareModeSwitchState FirmwarePresentationState(
        BridgeDeviceStateSnapshot device)
    {
        if (device.ControlConnectionState == BridgeConnectionState.Disconnected)
            return FirmwareModeSwitchState.Disconnected;

        return device.FirmwareTransition.Phase switch
        {
            BridgeFirmwareTransitionPhase.TransitionPending =>
                FirmwareModeSwitchState.RequestingMode,
            BridgeFirmwareTransitionPhase.RebootExpected =>
                FirmwareModeSwitchState.WaitingForDisconnect,
            BridgeFirmwareTransitionPhase.Reconnecting =>
                FirmwareModeSwitchState.WaitingForReconnect,
            BridgeFirmwareTransitionPhase.Failed =>
                FirmwareModeSwitchState.ModeSwitchFailed,
            _ => device.FirmwarePersonality switch
            {
                FirmwarePersonality.Controller => FirmwareModeSwitchState.ReadyController,
                FirmwarePersonality.Diagnostics => FirmwareModeSwitchState.ReadyDiagnostics,
                FirmwarePersonality.Network => FirmwareModeSwitchState.ReadyNetwork,
                FirmwarePersonality.Keyboard => FirmwareModeSwitchState.ReadyKeyboard,
                FirmwarePersonality.Mouse => FirmwareModeSwitchState.ReadyMouse,
                _ => FirmwareModeSwitchState.Connected
            }
        };
    }
}

internal static class BridgeDesktopPresentationConvergence
{
    internal static bool DispatchTerminal(
        BridgeOperationSnapshot previous,
        BridgeOperationSnapshot current,
        Func<BridgeDeviceStateSnapshot> readDevice,
        Func<BridgeNetworkPakModuleSnapshot> readModule,
        Action<BridgeDesktopPresentationConvergencePlan> dispatch)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(readDevice);
        ArgumentNullException.ThrowIfNull(readModule);
        ArgumentNullException.ThrowIfNull(dispatch);

        if (!IsRelevantTerminalTransition(previous, current)) return false;

        BridgeDesktopPresentationSnapshot presentation =
            BridgeDesktopPresentationComposer.Compose(readDevice(), readModule());
        bool firmwareOrAccessory = current.Kind is
            BridgeOperationKind.FirmwareModeSwitch or
            BridgeOperationKind.AccessorySelection;
        dispatch(new(
            presentation,
            RefreshFirmware: firmwareOrAccessory,
            RefreshAccessory: firmwareOrAccessory,
            RefreshNetworkPakModule:
                current.Kind == BridgeOperationKind.NetworkPakModuleSelection,
            RefreshNetworkPakCarrier:
                current.Kind == BridgeOperationKind.NetworkPakCarrierSelection));
        return true;
    }

    internal static bool IsRelevantTerminalTransition(
        BridgeOperationSnapshot previous,
        BridgeOperationSnapshot current) =>
        previous.OperationId == current.OperationId &&
        previous.Kind == current.Kind &&
        previous.Status == BridgeOperationStatus.Running &&
        current.Status is BridgeOperationStatus.Succeeded or
            BridgeOperationStatus.Failed or BridgeOperationStatus.Cancelled &&
        current.Kind is BridgeOperationKind.FirmwareModeSwitch or
            BridgeOperationKind.AccessorySelection or
            BridgeOperationKind.NetworkPakModuleSelection or
            BridgeOperationKind.NetworkPakCarrierSelection;
}
