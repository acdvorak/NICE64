using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static BridgeDeviceStateSnapshot Device(
    BridgeConnectionState connection,
    FirmwarePersonality firmware,
    BridgeAccessoryMode accessory,
    long generation,
    BridgeFirmwareTransitionSnapshot? firmwareTransition = null,
    BridgeAccessoryTransitionSnapshot? accessoryTransition = null)
{
    BridgeCapabilitySet deviceCapabilities = new(
        BridgeCapabilityProvenance.DeviceReported,
        [new(BridgeCapabilityId.BulkRead, true,
            BridgeCapabilityProvenance.DeviceReported)]);
    BridgeCapabilitySet implemented = new(
        BridgeCapabilityProvenance.BridgeImplemented,
        [new(BridgeCapabilityId.BulkRead, true,
            BridgeCapabilityProvenance.BridgeImplemented)]);
    BridgeCapabilitySet negotiated = new(
        BridgeCapabilityProvenance.ApplicationNegotiated,
        [new(BridgeCapabilityId.BulkRead, true,
            BridgeCapabilityProvenance.ApplicationNegotiated)]);
    BridgeCapabilitySet effective = new(
        BridgeCapabilityProvenance.Effective,
        [new(BridgeCapabilityId.BulkRead, true,
            BridgeCapabilityProvenance.Effective)]);
    return new("NICE64", "1.6.0", BridgeDeviceRole.Controller,
        firmware == FirmwarePersonality.Network
            ? BridgeDeviceService.Network
            : BridgeDeviceService.None,
        firmware,
        connection == BridgeConnectionState.Connected
            ? BridgeControlConnectionKind.SerialUsb
            : BridgeControlConnectionKind.None,
        connection,
        BridgeNetworkPakTransport.UsbSerial,
        accessory == BridgeAccessoryMode.NetworkPak
            ? BridgeNetworkPakTransport.UsbSerial
            : BridgeNetworkPakTransport.None,
        deviceCapabilities, implemented, negotiated, effective,
        generation, DateTimeOffset.UtcNow)
    {
        FirmwareTransition = firmwareTransition ?? BridgeFirmwareTransitionSnapshot.Idle,
        ActiveAccessory = accessory,
        AccessoryTransition = accessoryTransition ?? BridgeAccessoryTransitionSnapshot.Idle
    };
}

FakeDeviceState device = new(BridgeDeviceStateProviders.Disconnected.Current);
FakeController controller = new(new(
    BridgeControllerState.CreateNeutral(ConsoleControllerType.N64), null, false, false));
FakeControllerPakContent controllerPak = new(new(
    null, false, false, 32768, BridgeControllerPakContentOperation.None, string.Empty));
FakeTransferPakContent transferPak = new(new(
    null, null, false, false, false, BridgeTransferPakInstallKind.None,
    0, 0, null, BridgeTransferPakContentOperation.None, string.Empty));
FakePersistence persistence = new(new(
    AccessoryPersistenceKind.None, string.Empty, 0, false, false, false,
    0, null, string.Empty));
FakeModule module = new(new("network-portal", "Network Portal",
    [new("network-portal", "Network Portal")],
    BridgeNetworkPakModulePersistenceState.Confirmed,
    BridgeNetworkPakModuleSelectionOutcome.None, null));
FakeWifi wifi = new(BridgeWifiProvisioningSnapshot.Unknown);
BridgeOperationCoordinator operations = new();
BridgeInstanceIdentity bridge = new();

BridgeStateAggregator aggregator = new(device, controller, controllerPak, transferPak,
    persistence, module, wifi, operations, bridge);

BridgeStateSnapshot initial = aggregator.GetSnapshot();
Require(initial.CaptureSequence == 1 && initial.CapturedUtc != default &&
    Guid.TryParseExact(initial.Bridge.InstanceId, "D", out Guid initialInstanceId) &&
    initialInstanceId != Guid.Empty &&
    initial.Bridge.Name == BridgeInstanceNamingPolicy.DefaultUnconnectedName &&
    initial.Device.ControlConnectionState == BridgeConnectionState.Disconnected &&
    initial.WiFi == BridgeWifiProvisioningSnapshot.Unknown &&
    initial.Operation.Status == BridgeOperationStatus.Idle,
    "Initial disconnected/unknown/idle aggregate projection was incorrect");
Require(device.ReadCount == 1 && controller.ReadCount == 1 &&
    controllerPak.ReadCount == 1 && transferPak.ReadCount == 1 &&
    persistence.ReadCount == 1 && module.ReadCount == 1 && wifi.ReadCount == 1,
    "A source was not read exactly once for the aggregate capture");

string instanceId = initial.Bridge.InstanceId;
bridge.SetName("Port 1");
BridgeInstanceIdentity anotherProcess = new();
Require(anotherProcess.Current.InstanceId != instanceId,
    "Separate Bridge instances reused a process-lifetime identity");
Require(BridgeInstanceNamingPolicy.EndpointDefault(
            BridgeControlConnectionKind.WifiTcp, "192.168.2.81") ==
        "NICE64 - 192.168.2.81" &&
    BridgeInstanceNamingPolicy.EndpointDefault(
            BridgeControlConnectionKind.SerialUsb, "COM24") ==
        "NICE64 - COM24" &&
    BridgeInstanceNamingPolicy.ResolveConnectName(
            BridgeControlConnectionKind.WifiTcp, "192.168.2.81", false, _ =>
                throw new InvalidOperationException("disabled prompt was invoked")) ==
        "NICE64 - 192.168.2.81" &&
    BridgeInstanceNamingPolicy.ResolveConnectName(
            BridgeControlConnectionKind.SerialUsb, "COM24", true, _ => "Player 3") ==
        "Player 3" &&
    BridgeInstanceNamingPolicy.ResolveConnectName(
            BridgeControlConnectionKind.SerialUsb, "COM24", true, _ => string.Empty) ==
        "NICE64 - COM24",
    "Bridge endpoint-default, disabled-prompt, or custom-name policy was incorrect");

BridgeFirmwareTransitionSnapshot firmwareTransition = new(
    BridgeFirmwareTransitionPhase.Reconnecting, FirmwarePersonality.Network, string.Empty);
BridgeAccessoryTransitionSnapshot accessoryTransition = new(
    BridgeAccessoryTransitionPhase.TransitionPending,
    BridgeAccessoryMode.NetworkPak, string.Empty);
device.Value = Device(BridgeConnectionState.Connected, FirmwarePersonality.Network,
    BridgeAccessoryMode.ControllerPak, 42, firmwareTransition, accessoryTransition);
controller.Value = new(new(ConsoleControllerType.N64, 0x0003, 31, -22, true),
    new(ConsoleControllerType.N64, 0x0001, 31, -22, true), true, true);
controllerPak.Value = new("C:\\Paks\\active.mpk", true, true, 32768,
    BridgeControllerPakContentOperation.Load, string.Empty);
transferPak.Value = new("C:\\Roms\\game.gb", "C:\\Roms\\game.sav", true, true,
    true, BridgeTransferPakInstallKind.FullFlash, 1048576, 32768, null,
    BridgeTransferPakContentOperation.InstallFullRom, string.Empty);
persistence.Value = new(AccessoryPersistenceKind.ControllerPak,
    "C:\\Paks\\active.mpk", 32768, true, true, false, 7,
    DateTimeOffset.UtcNow, string.Empty);
module.Value = new("diagnostics", "Diagnostics",
    [new("network-portal", "Network Portal"), new("diagnostics", "Diagnostics")],
    BridgeNetworkPakModulePersistenceState.Confirmed,
    BridgeNetworkPakModuleSelectionOutcome.RequestedModuleSelected, null);
wifi.Value = new(true, true, true, "CONNECTED", "DHCP", "Lab", "UP",
    "192.0.2.10", "255.255.255.0", "192.0.2.1", "1.1.1.1", "NONE",
    BridgeWifiProvisioningOperation.GetStatus,
    BridgeWifiProvisioningOutcome.Succeeded, null);

BridgeOperationAdmission admission = operations.TryBegin(
    BridgeOperationKind.AccessorySelection, "Accessory",
    BridgeOperationCancellationCapability.BeforeCommitOnly);
using BridgeOperationLease operation = admission.Lease!;
operation.ReportProgress("Reconnecting", domainProgress: firmwareTransition);

BridgeStateSnapshot running = aggregator.GetSnapshot();
Require(running.CaptureSequence == 2 && running.CapturedUtc >= initial.CapturedUtc,
    "Capture sequence/timestamp did not advance");
Require(running.Bridge.InstanceId == instanceId && running.Bridge.Name == "Port 1",
    "Friendly-name mutation changed the process identity or was not retained");
Require(running.Device.FirmwarePersonality == FirmwarePersonality.Network &&
    running.Device.FirmwareTransition == firmwareTransition &&
    running.Device.ActiveAccessory == BridgeAccessoryMode.ControllerPak &&
    running.Device.AccessoryTransition == accessoryTransition &&
    running.Device.TransitionGeneration == 42,
    "Firmware/accessory/device-generation projection was not preserved");
Require(running.Controller == controller.Value &&
    running.ControllerPakContent == controllerPak.Value &&
    running.TransferPakContent == transferPak.Value &&
    running.Persistence == persistence.Value &&
    running.NetworkPakModule == module.Value && running.WiFi == wifi.Value,
    "A domain snapshot was not projected unchanged");
Require(running.NetworkPakModule.ActiveModuleId == "diagnostics",
    "Module identity did not come from the module service");
Require(running.Operation.OperationId == operation.OperationId &&
    running.Operation.Status == BridgeOperationStatus.Running,
    "Running operation identity/state was not projected unchanged");
Require(running.Device.EffectiveCapabilities.Supports(BridgeCapabilityId.BulkRead),
    "Existing capability model was not preserved inside Device");

BridgeDeviceStateSnapshot runningDevice = running.Device;
BridgeWifiProvisioningSnapshot runningWifi = running.WiFi;
BridgeControllerPakContentSnapshot runningControllerPak = running.ControllerPakContent;
operation.Succeed(new { Verified = true });
BridgeStateSnapshot terminal = aggregator.GetSnapshot();
Require(terminal.Operation.OperationId == operation.OperationId &&
    terminal.Operation.Status == BridgeOperationStatus.Succeeded &&
    terminal.Device == runningDevice && terminal.WiFi == runningWifi &&
    terminal.ControllerPakContent == runningControllerPak,
    "Terminal operation state replaced authoritative domain truth");

wifi.Value = BridgeWifiProvisioningSnapshot.Unknown;
BridgeStateSnapshot unknownWifi = aggregator.GetSnapshot();
Require(unknownWifi.WiFi == BridgeWifiProvisioningSnapshot.Unknown &&
    unknownWifi.Operation.Status == BridgeOperationStatus.Succeeded,
    "Wi-Fi source change was inferred from retained operation state");
Require(running.WiFi == runningWifi,
    "A prior aggregate snapshot changed after its source was updated");

BridgeControllerPakContentSnapshot retainedControllerPak = controllerPak.Value;
BridgeTransferPakContentSnapshot retainedTransferPak = transferPak.Value;
AccessoryPersistenceSnapshot retainedPersistence = persistence.Value;
device.Value = BridgeDeviceStateProviders.Disconnected.Current;
BridgeStateSnapshot disconnected = aggregator.GetSnapshot();
Require(disconnected.Device.ControlConnectionState == BridgeConnectionState.Disconnected &&
    disconnected.ControllerPakContent == retainedControllerPak &&
    disconnected.TransferPakContent == retainedTransferPak &&
    disconnected.Persistence == retainedPersistence,
    "Disconnected projection inferred content or persistence clearing");

Require(controller.MutationCount == 0 && controllerPak.MutationCount == 0 &&
    transferPak.MutationCount == 0 && persistence.MutationCount == 0 &&
    module.MutationCount == 0 && wifi.MutationCount == 0,
    "Aggregate capture invoked a source mutation or Wi-Fi status query");

device.Value = Device(BridgeConnectionState.Connected, FirmwarePersonality.Network,
    BridgeAccessoryMode.None, 77);
FirmwarePersonality staleFormPresentation = FirmwarePersonality.Controller;
Require(staleFormPresentation == FirmwarePersonality.Controller &&
    BridgeNetworkPakQuickActivationPolicy.CanActivate(device, tcpControlConnected: true),
    "Quick activation consulted stale Form firmware instead of authoritative device state");
device.Value = Device(BridgeConnectionState.Connected, FirmwarePersonality.Controller,
    BridgeAccessoryMode.None, 78);
staleFormPresentation = FirmwarePersonality.Network;
Require(staleFormPresentation == FirmwarePersonality.Network &&
    !BridgeNetworkPakQuickActivationPolicy.CanActivate(device, tcpControlConnected: true) &&
    !BridgeNetworkPakQuickActivationPolicy.CanActivate(device, tcpControlConnected: false),
    "Quick activation accepted stale presentation firmware or missing TCP control");

Console.WriteLine("PASS Bridge state aggregate and Quick firmware precedence semantics");

sealed class FakeDeviceState(BridgeDeviceStateSnapshot value) : IBridgeHostStateProvider
{
    private BridgeDeviceStateSnapshot _value = value;
    public int ReadCount { get; private set; }
    public BridgeDeviceStateSnapshot Value { get => _value; set => _value = value; }
    public BridgeDeviceStateSnapshot Current { get { ReadCount++; return _value; } }
    public BridgeCapabilitySet Capabilities => _value.EffectiveCapabilities;
    public bool Supports(BridgeCapabilityId capability) => Capabilities.Supports(capability);
    public BridgeCapabilityDescriptor? Get(BridgeCapabilityId capability) =>
        Capabilities.Get(capability);
    public event EventHandler<BridgeDeviceStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }
}

sealed class FakeController(BridgeControllerSnapshot value) : IBridgeControllerService
{
    private BridgeControllerSnapshot _value = value;
    public int ReadCount { get; private set; }
    public int MutationCount { get; private set; }
    public BridgeControllerSnapshot Value { get => _value; set => _value = value; }
    public BridgeControllerSnapshot Current { get { ReadCount++; return _value; } }
    public Task<BridgeControllerResult> SetStateAsync(BridgeControllerState state,
        CancellationToken cancellationToken = default)
    {
        MutationCount++;
        throw new InvalidOperationException("Aggregate mutated controller state.");
    }
    public Task<BridgeControllerResult> SetNeutralAsync(
        CancellationToken cancellationToken = default)
    {
        MutationCount++;
        throw new InvalidOperationException("Aggregate neutralized controller state.");
    }
}

sealed class FakeControllerPakContent(BridgeControllerPakContentSnapshot value)
    : IBridgeControllerPakContentService
{
    private BridgeControllerPakContentSnapshot _value = value;
    public int ReadCount { get; private set; }
    public int MutationCount { get; private set; }
    public BridgeControllerPakContentSnapshot Value { get => _value; set => _value = value; }
    public BridgeControllerPakContentSnapshot Current { get { ReadCount++; return _value; } }
    public Task<BridgeControllerPakContentResult> CreateAsync(string? path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) => Mutated();
    public Task<BridgeControllerPakContentResult> LoadAsync(string path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) => Mutated();
    public Task<BridgeControllerPakContentResult> SaveAsync(string path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) => Mutated();
    private Task<BridgeControllerPakContentResult> Mutated()
    {
        MutationCount++;
        throw new InvalidOperationException("Aggregate mutated Controller Pak content.");
    }
}

sealed class FakeTransferPakContent(BridgeTransferPakContentSnapshot value)
    : IBridgeTransferPakContentService
{
    private BridgeTransferPakContentSnapshot _value = value;
    public int ReadCount { get; private set; }
    public int MutationCount { get; private set; }
    public BridgeTransferPakContentSnapshot Value { get => _value; set => _value = value; }
    public BridgeTransferPakContentSnapshot Current { get { ReadCount++; return _value; } }
    public Task<BridgeTransferPakContentResult> InspectRomAsync(string path,
        CancellationToken cancellationToken = default) => Mutated();
    public Task<BridgeTransferPakContentResult> InstallRomAsync(string path,
        IProgress<BridgeTransferPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) => Mutated();
    public Task<BridgeTransferPakContentResult> SaveCurrentAsync(string? path = null,
        IProgress<BridgeTransferPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) => Mutated();
    private Task<BridgeTransferPakContentResult> Mutated()
    {
        MutationCount++;
        throw new InvalidOperationException("Aggregate mutated Transfer Pak content.");
    }
}

sealed class FakePersistence(AccessoryPersistenceSnapshot value)
    : IAccessoryPersistenceService
{
    private AccessoryPersistenceSnapshot _value = value;
    public int ReadCount { get; private set; }
    public int MutationCount { get; private set; }
    public AccessoryPersistenceSnapshot Value { get => _value; set => _value = value; }
    public AccessoryPersistenceSnapshot Current { get { ReadCount++; return _value; } }
    public void Activate(AccessoryPersistenceKind kind, string path, int expectedLength,
        Func<CancellationToken, Task<byte[]>> readCurrent, uint initialWriteCounter) => Mutated();
    public void ObserveAccessoryWriteCounter(uint counter) => Mutated();
    public void MarkDirty() => Mutated();
    public Task<AccessoryPersistenceResult> FlushAsync(
        CancellationToken cancellationToken = default)
    {
        Mutated();
        throw new InvalidOperationException();
    }
    public bool TryDeactivateClean()
    {
        Mutated();
        return false;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private void Mutated()
    {
        MutationCount++;
        throw new InvalidOperationException("Aggregate mutated persistence.");
    }
}

sealed class FakeModule(BridgeNetworkPakModuleSnapshot value)
    : IBridgeNetworkPakModuleService
{
    private BridgeNetworkPakModuleSnapshot _value = value;
    public int ReadCount { get; private set; }
    public int MutationCount { get; private set; }
    public BridgeNetworkPakModuleSnapshot Value { get => _value; set => _value = value; }
    public BridgeNetworkPakModuleSnapshot Current { get { ReadCount++; return _value; } }
    public IReadOnlyList<BridgeNetworkPakModuleInfo> ListModules()
    {
        MutationCount++;
        throw new InvalidOperationException("Aggregate enumerated modules separately.");
    }
    public Task<BridgeNetworkPakModuleSelectionResult> SelectModuleAsync(string? moduleId,
        CancellationToken cancellationToken = default)
    {
        MutationCount++;
        throw new InvalidOperationException("Aggregate selected a module.");
    }
}

sealed class FakeWifi(BridgeWifiProvisioningSnapshot value)
    : IBridgeWifiProvisioningService
{
    private BridgeWifiProvisioningSnapshot _value = value;
    public int ReadCount { get; private set; }
    public int MutationCount { get; private set; }
    public BridgeWifiProvisioningSnapshot Value { get => _value; set => _value = value; }
    public BridgeWifiProvisioningSnapshot Current { get { ReadCount++; return _value; } }
    public Task<BridgeWifiProvisioningResult> GetStatusAsync(
        CancellationToken cancellationToken = default) => Mutated();
    public Task<BridgeWifiProvisioningResult> ConfigureAndConnectAsync(string ssid,
        string password, CancellationToken cancellationToken = default) => Mutated();
    public Task<BridgeWifiProvisioningResult> ConnectRetainedAsync(
        CancellationToken cancellationToken = default) => Mutated();
    public Task<BridgeWifiProvisioningResult> DisconnectAsync(
        CancellationToken cancellationToken = default) => Mutated();
    public Task<BridgeWifiProvisioningResult> ForgetAsync(
        CancellationToken cancellationToken = default) => Mutated();
    private Task<BridgeWifiProvisioningResult> Mutated()
    {
        MutationCount++;
        throw new InvalidOperationException("Aggregate called a Wi-Fi operation.");
    }
}

namespace PicoSwitchController
{
    public sealed record BridgeBulkProgress(
        string Phase, int CompletedBytes, int TotalBytes, int ItemIndex, int ItemCount);

    public sealed record GameBoyTransferProfile(
        byte CartType, int RomSize, int RamSize, byte FirmwareFlags);
}
