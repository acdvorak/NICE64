namespace PicoSwitchController;

public enum BridgeAccessoryMode
{
    None,
    ControllerPak,
    RumblePak,
    TransferPak,
    NetworkPak
}

public static class BridgeUiPolicy
{
    // The legacy first-64-KiB loader remains compiled for diagnostics, but has
    // no normal user-facing entry point in Bridge v1.6.0.
    public static bool ShowLegacyFirst64KGameBoyLoad => false;
    public const string FullGameBoyLoadMenuText = "Load full GB/GBC ROM...";
    public const string ControllerPakLoadMenuText = "Load .mpk...";
    public const string ControllerPakSaveMenuText = "Save .mpk...";

    public static FirmwarePersonality RequiredFirmwareForAccessory(BridgeAccessoryMode accessory) =>
        accessory == BridgeAccessoryMode.NetworkPak
            ? FirmwarePersonality.Network
            : FirmwarePersonality.Controller;

    public static bool RequiresFirmwareSwitch(BridgeAccessoryMode accessory, FirmwarePersonality activeFirmware) =>
        activeFirmware != RequiredFirmwareForAccessory(accessory);

    public static bool CanMarkFirmwareModeActive(FirmwareModeSwitchState state, FirmwarePersonality verified, FirmwarePersonality target) =>
        verified == target && state is
            FirmwareModeSwitchState.ReadyController or
            FirmwareModeSwitchState.ReadyDiagnostics or
            FirmwareModeSwitchState.ReadyNetwork;
}
