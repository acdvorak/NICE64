using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static BridgeDeviceStateSnapshot Device(
    FirmwarePersonality firmware,
    BridgeFirmwareTransitionPhase transition,
    BridgeAccessoryMode accessory,
    BridgeConnectionState connection = BridgeConnectionState.Connected,
    BridgeNetworkPakTransport selectedCarrier = BridgeNetworkPakTransport.UsbSerial)
{
    BridgeCapabilitySet reported =
        BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.DeviceReported);
    BridgeCapabilitySet implemented =
        BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.BridgeImplemented);
    BridgeCapabilitySet negotiated =
        BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.ApplicationNegotiated);
    BridgeCapabilitySet effective =
        BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.Effective);
    return new("NICE64", "1.6.0",
        firmware == FirmwarePersonality.Controller
            ? BridgeDeviceRole.Controller : BridgeDeviceRole.Standalone,
        firmware switch
        {
            FirmwarePersonality.Diagnostics => BridgeDeviceService.Diagnostics,
            FirmwarePersonality.Network => BridgeDeviceService.Network,
            FirmwarePersonality.Controller => BridgeDeviceService.None,
            _ => BridgeDeviceService.Unknown
        },
        firmware,
        connection == BridgeConnectionState.Connected
            ? BridgeControlConnectionKind.SerialUsb : BridgeControlConnectionKind.None,
        connection,
        selectedCarrier,
        BridgeNetworkPakTransport.None,
        reported, implemented, negotiated, effective, 1, DateTimeOffset.UtcNow)
    {
        FirmwareTransition = new(transition, FirmwarePersonality.Network,
            transition == BridgeFirmwareTransitionPhase.Failed ? "safe failure" : string.Empty),
        ActiveAccessory = accessory,
        AccessoryTransition = BridgeAccessoryTransitionSnapshot.Idle
    };
}

static BridgeNetworkPakModuleSnapshot Module(string activeModuleId) => new(
    activeModuleId, activeModuleId, [new(activeModuleId, activeModuleId)],
    BridgeNetworkPakModulePersistenceState.Confirmed,
    BridgeNetworkPakModuleSelectionOutcome.RequestedModuleSelected, null);

static BridgeOperationSnapshot Operation(
    long id, BridgeOperationKind kind, BridgeOperationStatus status,
    object? domainResult = null) => new(
        id, kind, status, DateTimeOffset.UtcNow, null, null,
        BridgeOperationCancellationCapability.BeforeCommitOnly, false,
        "test", null, null, domainResult);

static BridgeDesktopPresentationConvergencePlan Dispatch(
    BridgeOperationKind kind,
    BridgeOperationStatus terminal,
    BridgeDeviceStateSnapshot device,
    BridgeNetworkPakModuleSnapshot module,
    object? domainResult = null)
{
    BridgeDesktopPresentationConvergencePlan? captured = null;
    int deviceReads = 0;
    int moduleReads = 0;
    int dispatches = 0;
    bool handled = BridgeDesktopPresentationConvergence.DispatchTerminal(
        Operation(1, kind, BridgeOperationStatus.Running),
        Operation(1, kind, terminal, domainResult),
        () => { deviceReads++; return device; },
        () => { moduleReads++; return module; },
        plan => { dispatches++; captured = plan; });
    Require(handled && dispatches == 1 && deviceReads == 1 && moduleReads == 1 &&
        captured is not null,
        "Relevant terminal transition did not capture once and invoke the UI-dispatch boundary");
    return captured!;
}

BridgeDesktopPresentationConvergencePlan firmwareSuccess = Dispatch(
    BridgeOperationKind.FirmwareModeSwitch,
    BridgeOperationStatus.Succeeded,
    Device(FirmwarePersonality.Network, BridgeFirmwareTransitionPhase.Completed,
        BridgeAccessoryMode.NetworkPak),
    Module("network-portal"),
    domainResult: new { Requested = FirmwarePersonality.Controller });
Require(firmwareSuccess.RefreshFirmware && firmwareSuccess.RefreshAccessory &&
    !firmwareSuccess.RefreshNetworkPakModule &&
    !firmwareSuccess.RefreshNetworkPakCarrier &&
    firmwareSuccess.Presentation.FirmwarePersonality == FirmwarePersonality.Network &&
    firmwareSuccess.Presentation.FirmwarePresentationState ==
        FirmwareModeSwitchState.ReadyNetwork,
    "Firmware success used requested/DomainResult state instead of authoritative device state");

BridgeDesktopPresentationConvergencePlan firmwareFailure = Dispatch(
    BridgeOperationKind.FirmwareModeSwitch,
    BridgeOperationStatus.Failed,
    Device(FirmwarePersonality.Controller, BridgeFirmwareTransitionPhase.Failed,
        BridgeAccessoryMode.ControllerPak),
    Module("network-portal"),
    domainResult: new { ClaimedMode = FirmwarePersonality.Network });
Require(firmwareFailure.Presentation.FirmwarePersonality ==
        FirmwarePersonality.Controller &&
    firmwareFailure.Presentation.FirmwarePresentationState ==
        FirmwareModeSwitchState.ModeSwitchFailed,
    "Firmware failure did not retain authoritative settled personality/failure state");

BridgeDesktopPresentationConvergencePlan firmwareCancelled = Dispatch(
    BridgeOperationKind.FirmwareModeSwitch,
    BridgeOperationStatus.Cancelled,
    Device(FirmwarePersonality.Diagnostics, BridgeFirmwareTransitionPhase.Completed,
        BridgeAccessoryMode.None),
    Module("network-portal"));
Require(firmwareCancelled.Presentation.FirmwarePersonality ==
        FirmwarePersonality.Diagnostics &&
    firmwareCancelled.Presentation.FirmwarePresentationState ==
        FirmwareModeSwitchState.ReadyDiagnostics,
    "Cancelled operation status overrode authoritative settled firmware state");

BridgeDesktopPresentationConvergencePlan accessory = Dispatch(
    BridgeOperationKind.AccessorySelection,
    BridgeOperationStatus.Succeeded,
    Device(FirmwarePersonality.Controller, BridgeFirmwareTransitionPhase.Completed,
        BridgeAccessoryMode.TransferPak),
    Module("network-portal"));
Require(accessory.Presentation.ActiveAccessory == BridgeAccessoryMode.TransferPak &&
    accessory.Presentation.FirmwarePersonality == FirmwarePersonality.Controller,
    "Accessory terminal convergence did not use authoritative accessory/firmware state");

BridgeDesktopPresentationConvergencePlan accessoryFirmware = Dispatch(
    BridgeOperationKind.AccessorySelection,
    BridgeOperationStatus.Failed,
    Device(FirmwarePersonality.Network, BridgeFirmwareTransitionPhase.Completed,
        BridgeAccessoryMode.None),
    Module("network-portal"));
Require(accessoryFirmware.Presentation.ActiveAccessory == BridgeAccessoryMode.None &&
    accessoryFirmware.Presentation.FirmwarePersonality == FirmwarePersonality.Network &&
    accessoryFirmware.Presentation.FirmwarePresentationState ==
        FirmwareModeSwitchState.ReadyNetwork,
    "Failed accessory operation did not preserve authoritative partial firmware change");

foreach ((BridgeOperationStatus status, string activeModule) in new[]
{
    (BridgeOperationStatus.Succeeded, "requested-module"),
    (BridgeOperationStatus.Succeeded, "fallback-module"),
    (BridgeOperationStatus.Failed, "reconciled-module")
})
{
    BridgeDesktopPresentationConvergencePlan module = Dispatch(
        BridgeOperationKind.NetworkPakModuleSelection,
        status,
        Device(FirmwarePersonality.Network, BridgeFirmwareTransitionPhase.Completed,
            BridgeAccessoryMode.NetworkPak),
        Module(activeModule),
        domainResult: new { RequestedModuleId = "wrong-module" });
    Require(module.RefreshNetworkPakModule && !module.RefreshFirmware &&
        !module.RefreshAccessory && !module.RefreshNetworkPakCarrier &&
        module.Presentation.ActiveNetworkPakModuleId == activeModule,
        "Module convergence derived selection from outcome/request rather than Current.ActiveModuleId");
}

foreach (BridgeOperationStatus terminal in new[]
    { BridgeOperationStatus.Succeeded, BridgeOperationStatus.Failed,
        BridgeOperationStatus.Cancelled })
{
    BridgeDesktopPresentationConvergencePlan carrier = Dispatch(
        BridgeOperationKind.NetworkPakCarrierSelection,
        terminal,
        Device(FirmwarePersonality.Network, BridgeFirmwareTransitionPhase.Completed,
            BridgeAccessoryMode.NetworkPak,
            selectedCarrier: BridgeNetworkPakTransport.ReliableUdpQuick),
        Module("network-portal"),
        domainResult: new { RequestedCarrier = BridgeNetworkPakTransport.Tcp });
    Require(carrier.RefreshNetworkPakCarrier && !carrier.RefreshFirmware &&
        !carrier.RefreshAccessory && !carrier.RefreshNetworkPakModule &&
        carrier.Presentation.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick,
        $"Carrier {terminal} convergence did not use authoritative selected carrier");
}

BridgeOperationSnapshot running = Operation(
    9, BridgeOperationKind.FirmwareModeSwitch, BridgeOperationStatus.Running);
int ignoredReads = 0;
int ignoredDispatches = 0;
bool progressHandled = BridgeDesktopPresentationConvergence.DispatchTerminal(
    running,
    running with { LatestProgress = new(9, BridgeOperationKind.FirmwareModeSwitch,
        "Reconnecting", null, null, null, null, null, null, null,
        DateTimeOffset.UtcNow) },
    () => { ignoredReads++; return Device(FirmwarePersonality.Network,
        BridgeFirmwareTransitionPhase.Reconnecting, BridgeAccessoryMode.None); },
    () => { ignoredReads++; return Module("network-portal"); },
    _ => ignoredDispatches++);
Require(!progressHandled && ignoredReads == 0 && ignoredDispatches == 0,
    "Running-to-running progress triggered terminal convergence");

BridgeOperationSnapshot carrierRunning = Operation(
    12, BridgeOperationKind.NetworkPakCarrierSelection,
    BridgeOperationStatus.Running);
bool carrierProgressHandled = BridgeDesktopPresentationConvergence.DispatchTerminal(
    carrierRunning,
    carrierRunning with { LatestProgress = new(12,
        BridgeOperationKind.NetworkPakCarrierSelection, "Selecting",
        null, null, null, null, null, null, null, DateTimeOffset.UtcNow) },
    () => { ignoredReads++; return Device(FirmwarePersonality.Network,
        BridgeFirmwareTransitionPhase.Completed, BridgeAccessoryMode.NetworkPak,
        selectedCarrier: BridgeNetworkPakTransport.Tcp); },
    () => { ignoredReads++; return Module("network-portal"); },
    _ => ignoredDispatches++);
Require(!carrierProgressHandled && ignoredReads == 0 && ignoredDispatches == 0,
    "Carrier Running-to-Running progress triggered terminal convergence");

foreach (BridgeOperationStatus terminal in new[]
    { BridgeOperationStatus.Succeeded, BridgeOperationStatus.Failed,
        BridgeOperationStatus.Cancelled })
{
    Require(BridgeDesktopPresentationConvergence.IsRelevantTerminalTransition(
        Operation(10, BridgeOperationKind.AccessorySelection,
            BridgeOperationStatus.Running),
        Operation(10, BridgeOperationKind.AccessorySelection, terminal)),
        $"Running-to-{terminal} was not recognized as terminal convergence");
}

bool unrelatedHandled = BridgeDesktopPresentationConvergence.DispatchTerminal(
    Operation(11, BridgeOperationKind.ControllerPakSave, BridgeOperationStatus.Running),
    Operation(11, BridgeOperationKind.ControllerPakSave, BridgeOperationStatus.Succeeded),
    () => { ignoredReads++; return Device(FirmwarePersonality.Controller,
        BridgeFirmwareTransitionPhase.Completed, BridgeAccessoryMode.ControllerPak); },
    () => { ignoredReads++; return Module("network-portal"); },
    _ => ignoredDispatches++);
Require(!unrelatedHandled && ignoredReads == 0 && ignoredDispatches == 0,
    "Unrelated terminal operation triggered F11 convergence");

BridgeDesktopPresentationSnapshot disconnected =
    BridgeDesktopPresentationComposer.Compose(
        Device(FirmwarePersonality.Unknown, BridgeFirmwareTransitionPhase.Idle,
            BridgeAccessoryMode.None, BridgeConnectionState.Disconnected),
        Module("network-portal"));
Require(disconnected.FirmwarePresentationState == FirmwareModeSwitchState.Disconnected,
    "Disconnected authoritative state did not win presentation composition");

Console.WriteLine("PASS F11 terminal presentation convergence semantics");
