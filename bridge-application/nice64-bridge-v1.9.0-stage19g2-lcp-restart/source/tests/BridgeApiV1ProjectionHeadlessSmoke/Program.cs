using PicoSwitchController;
using System.Collections;
using System.Reflection;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string Flatten(object? value)
{
    if (value is null) return string.Empty;
    if (value is string text) return text;
    if (value.GetType().IsPrimitive || value is decimal or DateTimeOffset or DateTime)
        return value.ToString() ?? string.Empty;
    if (value is IEnumerable items)
        return string.Join('|', items.Cast<object?>().Select(Flatten));
    return string.Join('|', value.GetType().GetProperties(BindingFlags.Public |
            BindingFlags.Instance).Select(property => Flatten(property.GetValue(value))));
}

static void RequireContractExclusions()
{
    Type[] contracts = typeof(BridgeApiV1State).Assembly.GetTypes()
        .Where(type => type.Namespace == "PicoSwitchController" &&
            type.Name.StartsWith("BridgeApiV1", StringComparison.Ordinal) &&
            type != typeof(BridgeApiV1StateProjector) &&
            type != typeof(BridgeApiV1SafeErrorMapper))
        .ToArray();
    string[] forbidden =
    [
        "Path", "RomPath", "SavePath", "LastError", "Error", "DomainResult",
        "DomainProgress", "Message", "Password", "Ssid", "IpAddress", "Netmask",
        "Gateway", "Dns", "DeviceFailure"
    ];
    foreach (Type contract in contracts)
    {
        foreach (PropertyInfo property in contract.GetProperties())
        {
            Require(!forbidden.Contains(property.Name, StringComparer.OrdinalIgnoreCase),
                $"Forbidden API property {contract.Name}.{property.Name} exists");
            Require(property.PropertyType != typeof(object),
                $"Arbitrary object property {contract.Name}.{property.Name} exists");
        }
    }
}

DateTimeOffset captured = new(2026, 8, 27, 18, 30, 0, TimeSpan.Zero);
DateTimeOffset operationStarted = captured.AddSeconds(-5);
DateTimeOffset progressTimestamp = captured.AddSeconds(-1);

BridgeCapabilitySet reported = new(BridgeCapabilityProvenance.DeviceReported,
[
    new(BridgeCapabilityId.FirmwareModeNetwork, true,
        BridgeCapabilityProvenance.DeviceReported)
]);
BridgeCapabilitySet implemented = new(BridgeCapabilityProvenance.BridgeImplemented,
[
    new(BridgeCapabilityId.BulkRead, true,
        BridgeCapabilityProvenance.BridgeImplemented, 8192, 4096)
]);
BridgeCapabilitySet negotiated = new(BridgeCapabilityProvenance.ApplicationNegotiated,
[
    new(BridgeCapabilityId.LegacyNetworkPakStream, true,
        BridgeCapabilityProvenance.ApplicationNegotiated)
]);
BridgeCapabilitySet effective = new(BridgeCapabilityProvenance.Effective,
[
    new(BridgeCapabilityId.BulkRead, true,
        BridgeCapabilityProvenance.Effective, 8192, 4096),
    new(BridgeCapabilityId.FirmwareModeNetwork, true,
        BridgeCapabilityProvenance.Effective),
    new(BridgeCapabilityId.FirmwareModeDiagnostics, false,
        BridgeCapabilityProvenance.Effective)
]);

const string rawSecret =
    "C:\\Users\\private-user\\secret COM77 192.168.88.1 BridgeSecretImplementation InvalidOperationException";
BridgeDeviceStateSnapshot device = new(
    "NICE64", "1.6.0", BridgeDeviceRole.Standalone, BridgeDeviceService.Network,
    FirmwarePersonality.Network, BridgeControlConnectionKind.WifiTcp,
    BridgeConnectionState.Connected, BridgeNetworkPakTransport.Tcp,
    BridgeNetworkPakTransport.ReliableUdpQuick, reported, implemented, negotiated,
    effective, 41, captured.AddSeconds(-2))
{
    FirmwareTransition = new(BridgeFirmwareTransitionPhase.Failed,
        FirmwarePersonality.Network, rawSecret),
    ActiveAccessory = BridgeAccessoryMode.NetworkPak,
    AccessoryTransition = new(BridgeAccessoryTransitionPhase.Failed,
        BridgeAccessoryMode.NetworkPak, rawSecret)
};
BridgeControllerSnapshot controller = new(
    new(ConsoleControllerType.N64, 0x0003, 31, -22, true),
    new(ConsoleControllerType.N64, 0x0001, 30, -20, true),
    true, true);
BridgeControllerPakContentSnapshot controllerPak = new(
    "C:\\Users\\private-user\\Paks\\active.mpk", true, true, 32768,
    BridgeControllerPakContentOperation.Load, rawSecret);
GameBoyRomMetadata metadata = new("SAFE GAME", 0x10, "MBC3+TIMER+RAM+BATTERY",
    "MBC3", 1048576, 32768, true);
BridgeTransferPakContentSnapshot transferPak = new(
    "D:\\Private Roms\\safe-game.gbc", "D:\\Private Roms\\safe-game.sav",
    true, true, true, BridgeTransferPakInstallKind.FullFlash, 1048576, 32768,
    metadata, BridgeTransferPakContentOperation.InstallFullRom, rawSecret);
AccessoryPersistenceSnapshot persistence = new(
    AccessoryPersistenceKind.GameBoySave,
    "D:\\Private Roms\\safe-game.sav", 32768, true, true, false, 9,
    captured.AddMinutes(-1), rawSecret);
BridgeNetworkPakModuleSnapshot module = new(
    "network-portal", "Network Portal",
    [new("network-portal", "Network Portal"), new("diagnostics", "Diagnostics")],
    BridgeNetworkPakModulePersistenceState.Confirmed,
    BridgeNetworkPakModuleSelectionOutcome.RequestedModuleSelected, rawSecret);
BridgeWifiProvisioningSnapshot wifi = new(
    true, true, true, "CONNECTED", "DHCP", "PrivateLabSsid", "UP",
    "192.168.88.44", "255.255.255.0", "192.168.88.1", "192.168.88.2",
    "WIFI_CONFIG|PASSWORD_B64|c2VjcmV0",
    BridgeWifiProvisioningOperation.GetStatus,
    BridgeWifiProvisioningOutcome.Succeeded, rawSecret);
object domainProgress = new { Secret = rawSecret, Password = "credential-secret" };
object domainResult = new { Implementation = "BridgeSecretImplementation" };
BridgeOperationProgressEnvelope progress = new(
    7, BridgeOperationKind.AccessorySelection, "Reconnecting", 5, 10, 1, 2,
    "bytes", "arbitrary progress message " + rawSecret, domainProgress,
    progressTimestamp);
BridgeOperationSnapshot runningOperation = new(
    7, BridgeOperationKind.AccessorySelection, BridgeOperationStatus.Running,
    operationStarted, null, progress,
    BridgeOperationCancellationCapability.BeforeCommitOnly, false,
    "Accessory", null, null, domainResult);
BridgeStateSnapshot source = new(
    73, captured, device, controller, controllerPak, transferPak, persistence,
    module, wifi, runningOperation)
{
    Bridge = new("9d2a6d6e-8be7-4c01-a5dd-123456789abc", "Port 1")
};

BridgeStateSnapshot sourceBefore = source;
IReadOnlyList<BridgeNetworkPakModuleInfo> moduleListBefore =
    source.NetworkPakModule.AvailableModules;
BridgeApiV1State projected = BridgeApiV1StateProjector.Project(source);

Require(projected.ApiVersion == "v1" && projected.CaptureSequence == 73 &&
    projected.CapturedUtc == captured, "API version/capture metadata changed");
Require(projected.Bridge.InstanceId ==
        "9d2a6d6e-8be7-4c01-a5dd-123456789abc" &&
    projected.Bridge.Name == "Port 1" && projected.CaptureSequence == 73,
    "Bridge identity/name projection or captureSequence independence was incorrect");
Require(projected.Device.Connected && projected.Device.ConnectionState == "connected" &&
    projected.Device.ConnectionKind == "wifi-tcp" &&
    projected.Device.FirmwarePersonality == "network" &&
    projected.Device.DeviceRole == "standalone" &&
    projected.Device.DeviceService == "network", "Device projection was incorrect");
Require(projected.Device.FirmwareTransition.Phase == "failed" &&
    projected.Device.FirmwareTransition.Target == "network" &&
    projected.Device.FirmwareTransition.Failure?.Code == "firmware-transition-failed" &&
    projected.Device.ActiveAccessory == "network-pak" &&
    projected.Device.AccessoryTransition.Failure?.Code == "accessory-transition-failed",
    "Firmware/accessory transition projection was incorrect");
Require(projected.Device.SelectedNetworkPakTransport == "tcp" &&
    projected.Device.ActiveNetworkPakTransport == "reliable-udp-quick",
    "Selected/active Network Pak carrier visibility was lost");
Require(projected.Device.EffectiveCapabilities.Count == 2 &&
    projected.Device.EffectiveCapabilities.Any(item => item.Id == "bulk-read" &&
        item.MaximumLogicalBlockBytes == 8192 &&
        item.PreferredLogicalBlockBytes == 4096) &&
    projected.Device.EffectiveCapabilities.All(item =>
        item.Id != "firmware-mode-diagnostics"),
    "Effective capability reduction was incorrect");

Require(projected.Controller.LastRequestedState.ControllerType == "n64" &&
    projected.Controller.LastRequestedState.Buttons == 0x0003 &&
    projected.Controller.LastRequestedState.AnalogX == 31 &&
    projected.Controller.LastRequestedState.AnalogY == -22 &&
    projected.Controller.LastRequestedState.AnalogActive &&
    projected.Controller.LastDeliveredState?.Buttons == 0x0001 &&
    projected.Controller.DeliverySuspended && projected.Controller.DeliveryPending,
    "Controller semantic projection was incorrect");

Require(projected.ControllerPak.ContentPresent && projected.ControllerPak.HasAssociation &&
    projected.ControllerPak.AssociatedFileName == "active.mpk" &&
    projected.ControllerPak.LastSuccessfulOperation == "load",
    "Controller Pak safe projection was incorrect");
Require(projected.TransferPak.ContentPresent && projected.TransferPak.HasAssociatedRom &&
    projected.TransferPak.HasAssociatedSave &&
    projected.TransferPak.RomFileName == "safe-game.gbc" &&
    projected.TransferPak.SaveFileName == "safe-game.sav" &&
    projected.TransferPak.InstallKind == "full-flash" &&
    projected.TransferPak.Metadata?.Title == "SAFE GAME" &&
    projected.TransferPak.Metadata.FirmwareFlagsAbsent(),
    "Transfer Pak safe projection was incorrect");
Require(projected.Persistence.Kind == "game-boy-save" && projected.Persistence.Active &&
    projected.Persistence.Dirty && projected.Persistence.AssociatedFileName == "safe-game.sav" &&
    projected.Persistence.LastSaveUtc == captured.AddMinutes(-1),
    "Persistence safe projection was incorrect");
Require(projected.NetworkPakModule.ActiveModuleId == "network-portal" &&
    projected.NetworkPakModule.AvailableModules.Count == 2 &&
    projected.NetworkPakModule.PersistenceState == "confirmed" &&
    projected.NetworkPakModule.LastSelectionOutcome == "requested-module-selected",
    "Module safe projection was incorrect");

Require(projected.Wifi.DeviceStatusKnown && projected.Wifi.Configured &&
    projected.Wifi.Connected && projected.Wifi.State == "connected" &&
    projected.Wifi.Mode == "dhcp" && projected.Wifi.Link == "up" &&
    projected.Wifi.LastOperation == "get-status" &&
    projected.Wifi.LastOperationOutcome == "succeeded",
    "Known Wi-Fi status projection was incorrect");
BridgeApiV1State unknownWifi = BridgeApiV1StateProjector.Project(
    source with { WiFi = BridgeWifiProvisioningSnapshot.Unknown });
Require(!unknownWifi.Wifi.DeviceStatusKnown && !unknownWifi.Wifi.Configured &&
    !unknownWifi.Wifi.Connected && unknownWifi.Wifi.State is null &&
    unknownWifi.Wifi.Mode is null && unknownWifi.Wifi.Link is null,
    "Wi-Fi unknown state was fabricated or collapsed into known false detail");

Require(projected.Operation.OperationId == 7 &&
    projected.Operation.Kind == "accessory-selection" &&
    projected.Operation.Status == "running" &&
    projected.Operation.CancellationCapability == "before-commit-only" &&
    projected.Operation.Domain == "accessory" &&
    projected.Operation.FailureCode is null && projected.Operation.SafeDetail is null &&
    projected.Operation.Progress is not null &&
    projected.Operation.Progress.Phase == "reconnecting" &&
    projected.Operation.Progress.CompletedUnits == 5 &&
    projected.Operation.Progress.TotalUnits == 10 &&
    projected.Operation.Progress.ItemIndex == 1 &&
    projected.Operation.Progress.ItemCount == 2 &&
    projected.Operation.Progress.Unit == "bytes" &&
    projected.Operation.Progress.TimestampUtc == progressTimestamp,
    "Running operation/progress scalar projection was incorrect");

BridgeOperationSnapshot failedOperation = runningOperation with
{
    Status = BridgeOperationStatus.Failed,
    CompletedUtc = captured,
    CancellationCapability = BridgeOperationCancellationCapability.None,
    FailureCode = "PersistenceFailure",
    Error = rawSecret,
    DomainResult = new { Password = "terminal-secret" }
};
BridgeApiV1State terminal = BridgeApiV1StateProjector.Project(
    source with { Operation = failedOperation });
Require(terminal.Operation.Status == "failed" &&
    terminal.Operation.CompletedUtc == captured &&
    terminal.Operation.FailureCode == "persistence-failure" &&
    terminal.Operation.SafeDetail == "Required persistence did not complete safely.",
    "Terminal operation safe failure projection was incorrect");

BridgeApiV1Error? mappedA = BridgeApiV1SafeErrorMapper.MapOperation(
    "UnexpectedFailure", rawSecret);
BridgeApiV1Error? mappedB = BridgeApiV1SafeErrorMapper.MapOperation(
    "UnexpectedFailure", "different raw exception text");
Require(mappedA == mappedB && mappedA?.Code == "operation-failed",
    "Safe error mapping depended on raw service error text");

foreach ((string failure, string expected) in new[]
{
    ("CarrierUnavailable", "bridge-unavailable"),
    ("PrerequisiteNotMet", "bridge-unavailable"),
    ("PersistenceFailed", "persistence-failure"),
    ("ReleaseFailed", "operation-failed"),
    ("ActivationFailed", "operation-failed"),
    ("HandshakeFailed", "operation-failed"),
    ("TransportFailed", "operation-failed"),
    ("Faulted", "operation-failed")
})
{
    BridgeOperationSnapshot carrierOperation = runningOperation with
    {
        Kind = BridgeOperationKind.NetworkPakCarrierSelection,
        Status = BridgeOperationStatus.Failed,
        CompletedUtc = captured,
        CancellationCapability = BridgeOperationCancellationCapability.None,
        Domain = "NetworkPakCarrier",
        FailureCode = failure,
        Error = rawSecret,
        DomainResult = new { Diagnostic = rawSecret }
    };
    BridgeApiV1OperationState projectedCarrier = BridgeApiV1StateProjector.Project(
        source with { Operation = carrierOperation }).Operation;
    Require(projectedCarrier.Kind == "network-pak-carrier-selection" &&
        projectedCarrier.Domain == "network-pak-carrier" &&
        projectedCarrier.FailureCode == expected &&
        projectedCarrier.SafeDetail is not null &&
        !Flatten(projectedCarrier).Contains(rawSecret, StringComparison.OrdinalIgnoreCase),
        $"Carrier operation failure {failure} did not project safely as {expected}");
}

foreach ((string phase, string expected) in new[]
{
    ("Inspecting", "inspecting"),
    ("SelectingAccessory", "selecting-accessory"),
    ("LoadingContent", "loading-content"),
    ("RecordingHistory", "recording-history"),
    ("Completed", "completed")
})
{
    BridgeOperationSnapshot historyOperation = runningOperation with
    {
        Kind = BridgeOperationKind.HistoryActivation,
        Domain = "HistoryActivation",
        LatestProgress = progress with
        {
            OperationKind = BridgeOperationKind.HistoryActivation,
            Phase = phase,
            DomainProgress = new { Secret = rawSecret }
        }
    };
    BridgeApiV1OperationState projectedHistory = BridgeApiV1StateProjector.Project(
        source with { Operation = historyOperation }).Operation;
    Require(projectedHistory.Kind == "history-activation" &&
        projectedHistory.Domain == "history" &&
        projectedHistory.Progress?.Phase == expected &&
        !Flatten(projectedHistory).Contains(rawSecret, StringComparison.OrdinalIgnoreCase),
        $"History operation phase {phase} was not projected safely as {expected}");
}

foreach ((string failure, string expected) in new[]
{
    ("NormalModeRequired", "normal-mode-required"),
    ("InspectionFailure", "inspection-failed"),
    ("AccessoryTransitionFailure", "accessory-transition-failed"),
    ("ContentFailure", "activation-failed"),
    ("MissingFile", "history-item-unavailable")
})
{
    BridgeOperationSnapshot historyOperation = runningOperation with
    {
        Kind = BridgeOperationKind.HistoryActivation,
        Status = BridgeOperationStatus.Failed,
        CompletedUtc = captured,
        Domain = "HistoryActivation",
        FailureCode = failure,
        Error = rawSecret,
        DomainResult = new { Secret = rawSecret }
    };
    BridgeApiV1OperationState projectedHistory = BridgeApiV1StateProjector.Project(
        source with { Operation = historyOperation }).Operation;
    Require(projectedHistory.Kind == "history-activation" &&
        projectedHistory.Domain == "history" &&
        projectedHistory.FailureCode == expected &&
        projectedHistory.SafeDetail is not null &&
        !Flatten(projectedHistory).Contains(rawSecret, StringComparison.OrdinalIgnoreCase),
        $"History operation failure {failure} was not projected safely as {expected}");
}

RequireContractExclusions();
string flattened = Flatten(projected) + '|' + Flatten(terminal);
string[] forbiddenText =
[
    "C:\\", "D:\\", "private-user", "Private Roms", "COM77", "192.168.88",
    "BridgeSecretImplementation", "InvalidOperationException", "PrivateLabSsid",
    "WIFI_CONFIG", "PASSWORD_B64", "credential-secret", "terminal-secret",
    "arbitrary progress message"
];
foreach (string forbidden in forbiddenText)
    Require(!flattened.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
        $"Sensitive/internal text survived projection: {forbidden}");

Require(source == sourceBefore &&
    ReferenceEquals(source.NetworkPakModule.AvailableModules, moduleListBefore) &&
    source.ControllerPakContent.Path == "C:\\Users\\private-user\\Paks\\active.mpk" &&
    source.WiFi.Ssid == "PrivateLabSsid" &&
    ReferenceEquals(source.Operation.DomainResult, domainResult) &&
    ReferenceEquals(source.Operation.LatestProgress?.DomainProgress, domainProgress),
    "Pure projection mutated or replaced source state");

Console.WriteLine("PASS Bridge API v1 pure projection and redaction semantics");

static class ProjectionTestExtensions
{
    public static bool FirmwareFlagsAbsent(this BridgeApiV1GameBoyMetadata metadata) =>
        metadata.GetType().GetProperty("FirmwareFlags") is null;
}

namespace PicoSwitchController
{
    public sealed record BridgeBulkProgress(
        string Phase, int CompletedBytes, int TotalBytes, int ItemIndex, int ItemCount);

    public sealed record GameBoyTransferProfile(
        byte CartType, int RomSize, int RamSize, byte FirmwareFlags);
}
