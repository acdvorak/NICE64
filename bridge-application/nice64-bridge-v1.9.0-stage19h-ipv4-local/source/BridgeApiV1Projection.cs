namespace PicoSwitchController;

public sealed record BridgeApiV1State(
    string ApiVersion,
    long CaptureSequence,
    DateTimeOffset CapturedUtc,
    BridgeApiV1BridgeState Bridge,
    BridgeApiV1DeviceState Device,
    BridgeApiV1ControllerState Controller,
    BridgeApiV1ControllerPakState ControllerPak,
    BridgeApiV1TransferPakState TransferPak,
    BridgeApiV1PersistenceState Persistence,
    BridgeApiV1ModuleState NetworkPakModule,
    BridgeApiV1WifiState Wifi,
    BridgeApiV1OperationState Operation);

public sealed record BridgeApiV1BridgeState(string InstanceId, string Name);

public sealed record BridgeApiV1DeviceState(
    bool Connected,
    string ConnectionState,
    string ConnectionKind,
    string FirmwarePersonality,
    string DeviceRole,
    string DeviceService,
    BridgeApiV1FirmwareTransitionState FirmwareTransition,
    string ActiveAccessory,
    BridgeApiV1AccessoryTransitionState AccessoryTransition,
    string SelectedNetworkPakTransport,
    string ActiveNetworkPakTransport,
    IReadOnlyList<BridgeApiV1Capability> EffectiveCapabilities);

public sealed record BridgeApiV1FirmwareTransitionState(
    string Phase,
    string Target,
    BridgeApiV1Error? Failure);

public sealed record BridgeApiV1AccessoryTransitionState(
    string Phase,
    string Target,
    BridgeApiV1Error? Failure);

public sealed record BridgeApiV1Capability(
    string Id,
    int? MaximumLogicalBlockBytes,
    int? PreferredLogicalBlockBytes);

public sealed record BridgeApiV1ControllerState(
    BridgeApiV1ControllerInputState LastRequestedState,
    BridgeApiV1ControllerInputState? LastDeliveredState,
    bool DeliverySuspended,
    bool DeliveryPending);

public sealed record BridgeApiV1ControllerInputState(
    string ControllerType,
    uint Buttons,
    int AnalogX,
    int AnalogY,
    bool AnalogActive,
    bool Neutral);

public sealed record BridgeApiV1ControllerPakState(
    bool ContentPresent,
    bool HasAssociation,
    string? AssociatedFileName,
    int ImageSize,
    string LastSuccessfulOperation);

public sealed record BridgeApiV1TransferPakState(
    bool ContentPresent,
    bool HasAssociatedRom,
    bool HasAssociatedSave,
    string? RomFileName,
    string? SaveFileName,
    string InstallKind,
    int RomLength,
    int ExpectedSaveLength,
    BridgeApiV1GameBoyMetadata? Metadata,
    string LastSuccessfulOperation);

public sealed record BridgeApiV1GameBoyMetadata(
    string Title,
    byte CartType,
    string CartTypeName,
    string Mapper,
    int DecodedRomSize,
    int DecodedRamSize,
    bool RtcRequired);

public sealed record BridgeApiV1PersistenceState(
    string Kind,
    bool Active,
    bool Dirty,
    bool FlushInProgress,
    int ExpectedLength,
    long DirtyGeneration,
    DateTimeOffset? LastSaveUtc,
    string? AssociatedFileName);

public sealed record BridgeApiV1ModuleInfo(string Id, string DisplayName);

public sealed record BridgeApiV1ModuleState(
    string ActiveModuleId,
    string ActiveModuleName,
    IReadOnlyList<BridgeApiV1ModuleInfo> AvailableModules,
    string PersistenceState,
    string LastSelectionOutcome);

public sealed record BridgeApiV1WifiState(
    bool DeviceStatusKnown,
    bool Configured,
    bool Connected,
    string? State,
    string? Mode,
    string? Link,
    string LastOperation,
    string LastOperationOutcome);

public sealed record BridgeApiV1OperationState(
    long OperationId,
    string Kind,
    string Status,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string CancellationCapability,
    bool CancellationRequested,
    string Domain,
    string? FailureCode,
    string? SafeDetail,
    BridgeApiV1OperationProgress? Progress);

public sealed record BridgeApiV1OperationProgress(
    string Phase,
    long? CompletedUnits,
    long? TotalUnits,
    int? ItemIndex,
    int? ItemCount,
    string? Unit,
    DateTimeOffset TimestampUtc);

public sealed record BridgeApiV1Error(string Code, string Detail);

public static class BridgeApiV1SafeErrorMapper
{
    public static BridgeApiV1Error? MapOperation(string? failureCode, string? rawError)
    {
        if (string.IsNullOrWhiteSpace(failureCode) && string.IsNullOrWhiteSpace(rawError))
            return null;

        string canonical = Canonical(failureCode);
        return canonical switch
        {
            "busy" or "alreadyconnected" or "alreadydisconnected" =>
                new("busy", "Another Bridge operation prevents this request."),
            "cancelled" => new("cancelled", "The operation was cancelled."),
            "invalidrequest" or "invalidendpoint" or "invalidtarget" or
            "invalidaccessory" or "invalidpath" or "invalidimage" or "invalidrom" or
            "validationfailure" => new("invalid-request", "The request is invalid."),
            "notconnected" or "controlpathunavailable" or "transportunavailable" or
            "carrierunavailable" or "prerequisitenotmet" =>
                new("bridge-unavailable", "The required Bridge connection is unavailable."),
            "persistence" or "persistencefailure" or "persistencefailed" or
            "blockedbypersistence" =>
                new("persistence-failure", "Required persistence did not complete safely."),
            "normalmoderequired" => new("normal-mode-required",
                "History activation requires Controller/Normal firmware."),
            "inspectionfailure" => new("inspection-failed",
                "The History item could not be inspected safely."),
            "accessorytransitionfailure" => new("accessory-transition-failed",
                "The History accessory transition did not complete successfully."),
            "contentfailure" => new("activation-failed",
                "The History content activation did not complete successfully."),
            "missingfile" => new("history-item-unavailable",
                "The requested History item is currently unavailable."),
            "timeout" => new("timeout", "The device operation timed out."),
            _ => new("operation-failed", "The Bridge operation did not complete successfully.")
        };
    }

    internal static BridgeApiV1Error FirmwareTransitionFailure() =>
        new("firmware-transition-failed", "The firmware transition did not complete successfully.");

    internal static BridgeApiV1Error AccessoryTransitionFailure() =>
        new("accessory-transition-failed", "The accessory transition did not complete successfully.");

    private static string Canonical(string? value) => new(
        (value ?? string.Empty).Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant).ToArray());
}

public static class BridgeApiV1StateProjector
{
    public const string ApiVersion = "v1";

    public static BridgeApiV1State Project(BridgeStateSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(
            ApiVersion,
            source.CaptureSequence,
            source.CapturedUtc,
            ProjectBridge(source.Bridge),
            ProjectDevice(source.Device),
            ProjectController(source.Controller),
            ProjectControllerPak(source.ControllerPakContent),
            ProjectTransferPak(source.TransferPakContent),
            ProjectPersistence(source.Persistence),
            ProjectModule(source.NetworkPakModule),
            ProjectWifi(source.WiFi),
            ProjectOperation(source.Operation));
    }

    private static BridgeApiV1BridgeState ProjectBridge(BridgeInstanceSnapshot source)
    {
        string instanceId = Guid.TryParseExact(source.InstanceId, "D", out Guid parsed) &&
            parsed != Guid.Empty
            ? parsed.ToString("D")
            : string.Empty;
        return new(instanceId, SafeText(source.Name, 80));
    }

    private static BridgeApiV1DeviceState ProjectDevice(BridgeDeviceStateSnapshot source) =>
        new(
            source.ControlConnectionState == BridgeConnectionState.Connected,
            Symbols.ConnectionState(source.ControlConnectionState),
            Symbols.ConnectionKind(source.ControlConnectionKind),
            Symbols.Firmware(source.FirmwarePersonality),
            Symbols.DeviceRole(source.DeviceRole),
            Symbols.DeviceService(source.DeviceService),
            new(
                Symbols.FirmwareTransition(source.FirmwareTransition.Phase),
                Symbols.Firmware(source.FirmwareTransition.Target),
                source.FirmwareTransition.Phase == BridgeFirmwareTransitionPhase.Failed
                    ? BridgeApiV1SafeErrorMapper.FirmwareTransitionFailure()
                    : null),
            Symbols.Accessory(source.ActiveAccessory),
            new(
                Symbols.AccessoryTransition(source.AccessoryTransition.Phase),
                Symbols.Accessory(source.AccessoryTransition.Target),
                source.AccessoryTransition.Phase == BridgeAccessoryTransitionPhase.Failed
                    ? BridgeApiV1SafeErrorMapper.AccessoryTransitionFailure()
                    : null),
            Symbols.NetworkPakTransport(source.SelectedNetworkPakTransport),
            Symbols.NetworkPakTransport(source.ActiveNetworkPakTransport),
            source.EffectiveCapabilities.Items
                .Where(item => item.Supported)
                .Select(item => new BridgeApiV1Capability(
                    Symbols.Capability(item.Id),
                    PositiveOrNull(item.MaximumLogicalBlockBytes),
                    PositiveOrNull(item.PreferredLogicalBlockBytes)))
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToArray());

    private static BridgeApiV1ControllerState ProjectController(
        BridgeControllerSnapshot source) =>
        new(ProjectControllerInput(source.LastRequestedState),
            source.LastDeliveredState is BridgeControllerState delivered
                ? ProjectControllerInput(delivered)
                : null,
            source.DeliverySuspended,
            source.DeliveryPending);

    private static BridgeApiV1ControllerInputState ProjectControllerInput(
        BridgeControllerState source) =>
        new(Symbols.ControllerType(source.ControllerType), source.Buttons,
            source.AnalogX, source.AnalogY, source.AnalogActive, source.Neutral);

    private static BridgeApiV1ControllerPakState ProjectControllerPak(
        BridgeControllerPakContentSnapshot source) =>
        new(source.ContentPresent, source.HasAssociatedFile,
            source.HasAssociatedFile ? FileNameOnly(source.Path) : null,
            source.ImageSize, Symbols.ControllerPakOperation(source.LastSuccessfulOperation));

    private static BridgeApiV1TransferPakState ProjectTransferPak(
        BridgeTransferPakContentSnapshot source) =>
        new(source.ContentPresent, source.HasAssociatedRom, source.HasAssociatedSave,
            source.HasAssociatedRom ? FileNameOnly(source.RomPath) : null,
            source.HasAssociatedSave ? FileNameOnly(source.SavePath) : null,
            Symbols.TransferPakInstallKind(source.InstallKind), source.RomLength,
            source.ExpectedSaveLength,
            source.Metadata is null ? null : new(
                SafeText(source.Metadata.Title), source.Metadata.CartType,
                SafeText(source.Metadata.CartTypeName), SafeText(source.Metadata.Mapper),
                source.Metadata.DecodedRomSize, source.Metadata.DecodedRamSize,
                source.Metadata.RtcRequired),
            Symbols.TransferPakOperation(source.LastSuccessfulOperation));

    private static BridgeApiV1PersistenceState ProjectPersistence(
        AccessoryPersistenceSnapshot source) =>
        new(Symbols.PersistenceKind(source.Kind), source.Active, source.Dirty,
            source.FlushInProgress, source.ExpectedLength, source.DirtyGeneration,
            source.LastSavedUtc, source.Active ? FileNameOnly(source.Path) : null);

    private static BridgeApiV1ModuleState ProjectModule(
        BridgeNetworkPakModuleSnapshot source) =>
        new(SafeText(source.ActiveModuleId), SafeText(source.ActiveModuleName),
            source.AvailableModules.Select(item => new BridgeApiV1ModuleInfo(
                SafeText(item.Id), SafeText(item.DisplayName))).ToArray(),
            Symbols.ModulePersistence(source.PersistenceState),
            Symbols.ModuleOutcome(source.LastSelectionOutcome));

    private static BridgeApiV1WifiState ProjectWifi(BridgeWifiProvisioningSnapshot source) =>
        new(source.DeviceStatusKnown, source.Configured, source.Connected,
            Symbols.WifiState(source.State), Symbols.WifiMode(source.Mode),
            Symbols.WifiLink(source.Link), Symbols.WifiOperation(source.LastOperation),
            Symbols.WifiOutcome(source.LastOperationOutcome));

    private static BridgeApiV1OperationState ProjectOperation(BridgeOperationSnapshot source)
    {
        BridgeApiV1Error? failure = source.Status is BridgeOperationStatus.Failed or
            BridgeOperationStatus.Cancelled
            ? BridgeApiV1SafeErrorMapper.MapOperation(source.FailureCode, source.Error)
            : null;
        return new(source.OperationId, Symbols.OperationKind(source.Kind),
            Symbols.OperationStatus(source.Status), source.StartedUtc, source.CompletedUtc,
            Symbols.Cancellation(source.CancellationCapability),
            source.CancellationRequested, Symbols.Domain(source.Domain),
            failure?.Code, failure?.Detail,
            source.LatestProgress is null ? null : new(
                Symbols.ProgressPhase(source.LatestProgress.Phase),
                source.LatestProgress.CompletedUnits, source.LatestProgress.TotalUnits,
                source.LatestProgress.ItemIndex, source.LatestProgress.ItemCount,
                Symbols.ProgressUnit(source.LatestProgress.Unit),
                source.LatestProgress.TimestampUtc));
    }

    private static int? PositiveOrNull(int value) => value > 0 ? value : null;

    private static string? FileNameOnly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        int separator = path.LastIndexOfAny(['\\', '/']);
        string name = separator >= 0 ? path[(separator + 1)..] : path;
        return string.IsNullOrWhiteSpace(name) ? null : SafeText(name);
    }

    private static string SafeText(string value, int maximumLength = 256)
    {
        string safe = new string(value.Where(character => !char.IsControl(character)).ToArray())
            .Trim();
        return safe.Length <= maximumLength ? safe : safe[..maximumLength];
    }

    private static class Symbols
    {
        internal static string ConnectionState(BridgeConnectionState value) => value switch
        {
            BridgeConnectionState.Disconnected => "disconnected",
            BridgeConnectionState.Connected => "connected",
            _ => "unknown"
        };

        internal static string ConnectionKind(BridgeControlConnectionKind value) => value switch
        {
            BridgeControlConnectionKind.None => "none",
            BridgeControlConnectionKind.SerialUsb => "serial-usb",
            BridgeControlConnectionKind.WifiTcp => "wifi-tcp",
            _ => "unknown"
        };

        internal static string Firmware(FirmwarePersonality value) => value switch
        {
            FirmwarePersonality.Controller => "controller",
            FirmwarePersonality.Diagnostics => "diagnostics",
            FirmwarePersonality.Network => "network",
            FirmwarePersonality.Keyboard => "keyboard",
            FirmwarePersonality.Mouse => "mouse",
            _ => "unknown"
        };

        internal static string DeviceRole(BridgeDeviceRole value) => value switch
        {
            BridgeDeviceRole.Controller => "controller",
            BridgeDeviceRole.Standalone => "standalone",
            _ => "unknown"
        };

        internal static string DeviceService(BridgeDeviceService value) => value switch
        {
            BridgeDeviceService.None => "none",
            BridgeDeviceService.Diagnostics => "diagnostics",
            BridgeDeviceService.Network => "network",
            BridgeDeviceService.KeyboardMouse => "keyboard-mouse",
            BridgeDeviceService.Other => "other",
            _ => "unknown"
        };

        internal static string FirmwareTransition(BridgeFirmwareTransitionPhase value) => value switch
        {
            BridgeFirmwareTransitionPhase.Idle => "idle",
            BridgeFirmwareTransitionPhase.TransitionPending => "transition-pending",
            BridgeFirmwareTransitionPhase.RebootExpected => "reboot-expected",
            BridgeFirmwareTransitionPhase.Reconnecting => "reconnecting",
            BridgeFirmwareTransitionPhase.Completed => "completed",
            BridgeFirmwareTransitionPhase.Failed => "failed",
            _ => "unknown"
        };

        internal static string AccessoryTransition(BridgeAccessoryTransitionPhase value) => value switch
        {
            BridgeAccessoryTransitionPhase.Idle => "idle",
            BridgeAccessoryTransitionPhase.TransitionPending => "transition-pending",
            BridgeAccessoryTransitionPhase.Completed => "completed",
            BridgeAccessoryTransitionPhase.Failed => "failed",
            _ => "unknown"
        };

        internal static string Accessory(BridgeAccessoryMode value) => value switch
        {
            BridgeAccessoryMode.None => "none",
            BridgeAccessoryMode.ControllerPak => "controller-pak",
            BridgeAccessoryMode.RumblePak => "rumble-pak",
            BridgeAccessoryMode.TransferPak => "transfer-pak",
            BridgeAccessoryMode.NetworkPak => "network-pak",
            _ => "unknown"
        };

        internal static string NetworkPakTransport(BridgeNetworkPakTransport value) => value switch
        {
            BridgeNetworkPakTransport.None => "none",
            BridgeNetworkPakTransport.UsbSerial => "usb",
            BridgeNetworkPakTransport.Tcp => "tcp",
            BridgeNetworkPakTransport.ReliableUdpQuick => "reliable-udp-quick",
            _ => "unknown"
        };

        internal static string Capability(BridgeCapabilityId value) => value switch
        {
            BridgeCapabilityId.FirmwareModeController => "firmware-mode-controller",
            BridgeCapabilityId.FirmwareModeDiagnostics => "firmware-mode-diagnostics",
            BridgeCapabilityId.FirmwareModeNetwork => "firmware-mode-network",
            BridgeCapabilityId.FirmwareModeKeyboard => "firmware-mode-keyboard",
            BridgeCapabilityId.FirmwareModeMouse => "firmware-mode-mouse",
            BridgeCapabilityId.LegacyNetworkPakStream => "legacy-network-pak-stream",
            BridgeCapabilityId.BulkRead => "bulk-read",
            _ => "unknown"
        };

        internal static string ControllerType(ConsoleControllerType value) => value switch
        {
            ConsoleControllerType.Snes => "snes",
            ConsoleControllerType.N64 => "n64",
            _ => "unknown"
        };

        internal static string ControllerPakOperation(BridgeControllerPakContentOperation value) => value switch
        {
            BridgeControllerPakContentOperation.None => "none",
            BridgeControllerPakContentOperation.Create => "create",
            BridgeControllerPakContentOperation.Load => "load",
            BridgeControllerPakContentOperation.Save => "save",
            _ => "unknown"
        };

        internal static string TransferPakInstallKind(BridgeTransferPakInstallKind value) => value switch
        {
            BridgeTransferPakInstallKind.None => "none",
            BridgeTransferPakInstallKind.FullFlash => "full-flash",
            BridgeTransferPakInstallKind.LegacyFirst64K => "legacy-first-64k",
            _ => "unknown"
        };

        internal static string TransferPakOperation(BridgeTransferPakContentOperation value) => value switch
        {
            BridgeTransferPakContentOperation.None => "none",
            BridgeTransferPakContentOperation.InspectRom => "inspect-rom",
            BridgeTransferPakContentOperation.InstallFullRom => "install-full-rom",
            BridgeTransferPakContentOperation.LoadLegacyRom => "load-legacy-rom",
            BridgeTransferPakContentOperation.SaveCurrent => "save-current",
            _ => "unknown"
        };

        internal static string PersistenceKind(AccessoryPersistenceKind value) => value switch
        {
            AccessoryPersistenceKind.None => "none",
            AccessoryPersistenceKind.GameBoySave => "game-boy-save",
            AccessoryPersistenceKind.ControllerPak => "controller-pak",
            _ => "unknown"
        };

        internal static string ModulePersistence(BridgeNetworkPakModulePersistenceState value) => value switch
        {
            BridgeNetworkPakModulePersistenceState.Unverified => "unverified",
            BridgeNetworkPakModulePersistenceState.Confirmed => "confirmed",
            BridgeNetworkPakModulePersistenceState.FailedUnknown => "failed-unknown",
            _ => "unknown"
        };

        internal static string ModuleOutcome(BridgeNetworkPakModuleSelectionOutcome value) => value switch
        {
            BridgeNetworkPakModuleSelectionOutcome.None => "none",
            BridgeNetworkPakModuleSelectionOutcome.RequestedModuleSelected => "requested-module-selected",
            BridgeNetworkPakModuleSelectionOutcome.FallbackSelected => "fallback-selected",
            BridgeNetworkPakModuleSelectionOutcome.Failed => "failed",
            _ => "unknown"
        };

        internal static string WifiOperation(BridgeWifiProvisioningOperation value) => value switch
        {
            BridgeWifiProvisioningOperation.None => "none",
            BridgeWifiProvisioningOperation.GetStatus => "get-status",
            BridgeWifiProvisioningOperation.ConfigureAndConnect => "configure-and-connect",
            BridgeWifiProvisioningOperation.ConnectRetained => "connect-retained",
            BridgeWifiProvisioningOperation.Disconnect => "disconnect",
            BridgeWifiProvisioningOperation.Forget => "forget",
            _ => "unknown"
        };

        internal static string WifiOutcome(BridgeWifiProvisioningOutcome value) => value switch
        {
            BridgeWifiProvisioningOutcome.None => "none",
            BridgeWifiProvisioningOutcome.Succeeded => "succeeded",
            BridgeWifiProvisioningOutcome.Failed => "failed",
            BridgeWifiProvisioningOutcome.Cancelled => "cancelled",
            _ => "unknown"
        };

        internal static string? WifiState(string? value) => Token(value,
            "configured", "associating", "waiting-for-address", "connected", "failed",
            "disconnected", "unknown");

        internal static string? WifiMode(string? value) => Token(value,
            "dhcp", "static", "unknown");

        internal static string? WifiLink(string? value) => Token(value,
            "up", "down", "unknown");

        internal static string OperationKind(BridgeOperationKind value) => value switch
        {
            BridgeOperationKind.None => "none",
            BridgeOperationKind.Connect => "connect",
            BridgeOperationKind.Disconnect => "disconnect",
            BridgeOperationKind.FirmwareQuery => "firmware-query",
            BridgeOperationKind.FirmwareModeSwitch => "firmware-mode-switch",
            BridgeOperationKind.AccessorySelection => "accessory-selection",
            BridgeOperationKind.ControllerPakCreate => "controller-pak-create",
            BridgeOperationKind.ControllerPakLoad => "controller-pak-load",
            BridgeOperationKind.ControllerPakSave => "controller-pak-save",
            BridgeOperationKind.TransferPakInstall => "transfer-pak-install",
            BridgeOperationKind.TransferPakSave => "transfer-pak-save",
            BridgeOperationKind.TransferPakLegacyLoad => "transfer-pak-legacy-load",
            BridgeOperationKind.WifiConfigureAndConnect => "wifi-configure-and-connect",
            BridgeOperationKind.WifiConnectRetained => "wifi-connect-retained",
            BridgeOperationKind.WifiDisconnect => "wifi-disconnect",
            BridgeOperationKind.WifiForget => "wifi-forget",
            BridgeOperationKind.NetworkPakModuleSelection => "network-pak-module-selection",
            BridgeOperationKind.NetworkPakCarrierSelection => "network-pak-carrier-selection",
            BridgeOperationKind.HistoryActivation => "history-activation",
            _ => "unknown"
        };

        internal static string OperationStatus(BridgeOperationStatus value) => value switch
        {
            BridgeOperationStatus.Idle => "idle",
            BridgeOperationStatus.Running => "running",
            BridgeOperationStatus.Succeeded => "succeeded",
            BridgeOperationStatus.Failed => "failed",
            BridgeOperationStatus.Cancelled => "cancelled",
            _ => "unknown"
        };

        internal static string Cancellation(BridgeOperationCancellationCapability value) => value switch
        {
            BridgeOperationCancellationCapability.None => "none",
            BridgeOperationCancellationCapability.Cooperative => "cooperative",
            BridgeOperationCancellationCapability.BeforeCommitOnly => "before-commit-only",
            BridgeOperationCancellationCapability.StopWaitingOnly => "stop-waiting-only",
            _ => "unknown"
        };

        internal static string Domain(string value) => value switch
        {
            "Connection" => "connection",
            "Firmware" => "firmware",
            "Accessory" => "accessory",
            "ControllerPakContent" => "controller-pak-content",
            "TransferPakContent" => "transfer-pak-content",
            "WiFi" => "wifi",
            "NetworkPakModule" => "network-pak-module",
            "NetworkPakCarrier" => "network-pak-carrier",
            "HistoryActivation" => "history",
            "" => "none",
            _ => "bridge"
        };

        internal static string ProgressPhase(string value) => value switch
        {
            "TransitionPending" => "transition-pending",
            "Querying" => "querying",
            "Verified" => "verified",
            "Preparing" => "preparing",
            "ReadingFile" => "reading-file",
            "ReadingRom" => "reading-rom",
            "ParsingMetadata" => "parsing-metadata",
            "PreparingSave" => "preparing-save",
            "NormalizingSave" => "normalizing-save",
            "QueryingCapacity" => "querying-capacity",
            "FlushingPersistence" => "flushing-persistence",
            "Uploading" => "uploading",
            "InstallingRom" => "installing-rom",
            "UploadingSave" => "uploading-save",
            "DownloadingSave" => "downloading-save",
            "Downloading" => "downloading",
            "WritingFile" => "writing-file",
            "WritingSave" => "writing-save",
            "AdoptingAccessory" => "adopting-accessory",
            "Inspecting" => "inspecting",
            "SelectingAccessory" => "selecting-accessory",
            "LoadingContent" => "loading-content",
            "RecordingHistory" => "recording-history",
            "ActivatingPersistence" => "activating-persistence",
            "Reconnecting" => "reconnecting",
            "Completed" => "completed",
            _ => "in-progress"
        };

        internal static string? ProgressUnit(string? value) => value switch
        {
            null or "" => null,
            "bytes" => "bytes",
            "items" => "items",
            "percent" => "percent",
            _ => null
        };

        private static string? Token(string? value, params string[] allowed)
        {
            if (value is null) return null;
            string token = value.Trim().Replace('_', '-').ToLowerInvariant();
            return allowed.Contains(token, StringComparer.Ordinal) ? token : "unknown";
        }
    }
}
