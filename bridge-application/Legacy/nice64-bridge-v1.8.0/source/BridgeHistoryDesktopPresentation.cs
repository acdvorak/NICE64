namespace PicoSwitchController;

public sealed record BridgeHistoryDesktopMenuEntry(
    BridgeHistoryCategory Category,
    string Label,
    string Path,
    bool ActivationEnabled);

public sealed record BridgeHistoryDesktopMenuSnapshot(
    IReadOnlyList<BridgeHistoryDesktopMenuEntry> ControllerPak,
    IReadOnlyList<BridgeHistoryDesktopMenuEntry> TransferPak,
    bool NormalModeActive);

public enum BridgeHistoryDesktopOutcomeKind
{
    Activated,
    ActivatedWithHistoryWarning,
    PromptRemoveMissingFile,
    NormalModeRequired,
    Cancelled,
    InspectionFailed,
    PersistenceFailed,
    AccessoryTransitionFailed,
    ContentActivationFailed,
    Failed
}

public sealed record BridgeHistoryDesktopOutcome(
    BridgeHistoryDesktopOutcomeKind Kind,
    string Message,
    BridgeHistoryCategory Category,
    string Path);

public static class BridgeHistoryDesktopPresentation
{
    public const string EmptyControllerPakText = "(No Controller Pak History)";
    public const string EmptyTransferPakText = "(No Transfer Pak History)";

    public static BridgeHistoryDesktopMenuSnapshot Compose(
        BridgeHistorySnapshot history,
        FirmwarePersonality firmwarePersonality)
    {
        ArgumentNullException.ThrowIfNull(history);
        bool enabled = firmwarePersonality == FirmwarePersonality.Controller;
        return new(
            history.ControllerPak.Select(entry => new BridgeHistoryDesktopMenuEntry(
                BridgeHistoryCategory.ControllerPak, Label(entry.Path), entry.Path,
                enabled)).ToArray(),
            history.TransferPak.Select(entry => new BridgeHistoryDesktopMenuEntry(
                BridgeHistoryCategory.TransferPak, Label(entry.RomPath), entry.RomPath,
                enabled)).ToArray(),
            enabled);
    }

    public static BridgeHistoryDesktopOutcome Classify(
        BridgeHistoryActivationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ActivationSucceeded)
        {
            return result.HistoryRecorded
                ? new(BridgeHistoryDesktopOutcomeKind.Activated,
                    $"Activated {Label(result.Path)} from History.",
                    result.Category, result.Path)
                : new(BridgeHistoryDesktopOutcomeKind.ActivatedWithHistoryWarning,
                    $"Activated {Label(result.Path)}, but History could not be updated: " +
                    (result.HistoryResult?.Error ?? "History persistence failed."),
                    result.Category, result.Path);
        }

        BridgeHistoryDesktopOutcomeKind kind = result.Failure switch
        {
            BridgeHistoryActivationFailure.MissingFile =>
                BridgeHistoryDesktopOutcomeKind.PromptRemoveMissingFile,
            BridgeHistoryActivationFailure.NormalModeRequired =>
                BridgeHistoryDesktopOutcomeKind.NormalModeRequired,
            BridgeHistoryActivationFailure.Cancelled =>
                BridgeHistoryDesktopOutcomeKind.Cancelled,
            BridgeHistoryActivationFailure.InspectionFailure =>
                BridgeHistoryDesktopOutcomeKind.InspectionFailed,
            BridgeHistoryActivationFailure.AccessoryTransitionFailure
                when result.AccessoryResult?.Failure ==
                    BridgeAccessoryTransitionFailure.Persistence =>
                BridgeHistoryDesktopOutcomeKind.PersistenceFailed,
            BridgeHistoryActivationFailure.AccessoryTransitionFailure =>
                BridgeHistoryDesktopOutcomeKind.AccessoryTransitionFailed,
            BridgeHistoryActivationFailure.ContentFailure =>
                BridgeHistoryDesktopOutcomeKind.ContentActivationFailed,
            _ => BridgeHistoryDesktopOutcomeKind.Failed
        };
        return new(kind, result.Error, result.Category, result.Path);
    }

    private static string Label(string path)
    {
        string label = System.IO.Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(label) ? path : label;
    }
}
