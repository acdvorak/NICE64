namespace PicoSwitchController;

internal static class Program
{
    private static readonly string Root = Path.Combine(
        @"D:\NICE64\history-v1\.history-presentation-tests", Guid.NewGuid().ToString("N"));

    private static async Task Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            MutableTimeProvider time = new(
                new DateTimeOffset(2026, 9, 3, 2, 0, 0, TimeSpan.Zero));
            BridgeHistoryService history = new(
                new BridgeHistoryJsonStore(Path.Combine(Root, "history.json")),
                timeProvider: time);
            string cpOld = Path.Combine(Root, "old.mpk");
            string cpNew = Path.Combine(Root, "new.mpk");
            string tpOld = Path.Combine(Root, "old.gb");
            string tpNew = Path.Combine(Root, "new.gbc");
            await history.RecordControllerPakAsync(cpOld);
            time.Advance(TimeSpan.FromMinutes(1));
            await history.RecordControllerPakAsync(cpNew);
            await history.RecordTransferPakAsync(tpOld);
            time.Advance(TimeSpan.FromMinutes(1));
            await history.RecordTransferPakAsync(tpNew);

            BridgeHistoryDesktopMenuSnapshot menu =
                BridgeHistoryDesktopPresentation.Compose(history.Current,
                    FirmwarePersonality.Controller);
            Assert(menu.ControllerPak.Select(x => x.Path).SequenceEqual(
                new[] { Path.GetFullPath(cpNew), Path.GetFullPath(cpOld) }),
                "1 Controller Pak History renders most-recent-first");
            Assert(menu.TransferPak.Select(x => x.Path).SequenceEqual(
                new[] { Path.GetFullPath(tpNew), Path.GetFullPath(tpOld) }),
                "2 Transfer Pak History renders most-recent-first");

            BridgeHistorySnapshot empty = new(1, [], [], string.Empty);
            BridgeHistoryDesktopMenuSnapshot emptyMenu =
                BridgeHistoryDesktopPresentation.Compose(empty, FirmwarePersonality.Controller);
            Assert(emptyMenu.ControllerPak.Count == 0 &&
                BridgeHistoryDesktopPresentation.EmptyControllerPakText ==
                    "(No Controller Pak History)",
                "3 empty Controller Pak category supplies disabled empty-state label");
            Assert(emptyMenu.TransferPak.Count == 0 &&
                BridgeHistoryDesktopPresentation.EmptyTransferPakText ==
                    "(No Transfer Pak History)",
                "4 empty Transfer Pak category supplies disabled empty-state label");

            string form = File.ReadAllText(Path.Combine(FindSourceRoot(), "Form1.cs"));
            string cpHandler = Slice(form, "private async Task ActivateControllerPakHistoryAsync",
                "private async Task ActivateTransferPakHistoryAsync");
            Assert(cpHandler.Contains("_historyActivationService.ActivateControllerPakAsync(") &&
                !cpHandler.Contains("_controllerPakContentService.LoadAsync("),
                "5 Controller Pak selection calls activation service, not raw content");

            string tpHandler = Slice(form, "private async Task ActivateTransferPakHistoryAsync",
                "private IProgress<BridgeHistoryActivationProgress> CreateHistoryProgress");
            int inspect = tpHandler.IndexOf("destructiveInstallConfirmed: false",
                StringComparison.Ordinal);
            int warning = tpHandler.IndexOf("MessageBox.Show", StringComparison.Ordinal);
            int activate = tpHandler.IndexOf("destructiveInstallConfirmed: true",
                StringComparison.Ordinal);
            Assert(inspect >= 0 && inspect < warning,
                "6 Transfer Pak selection performs semantic inspection/preflight before confirmation");
            Assert(inspect < activate && warning < activate,
                "7 Transfer Pak activation requires explicit confirmation");
            Assert(tpHandler.Contains("if (confirm != DialogResult.OK)") &&
                tpHandler.IndexOf("return;", warning, StringComparison.Ordinal) < activate,
                "8 cancelled Transfer Pak warning returns before mutation");

            BridgeHistoryActivationResult missingCp = Failed(
                BridgeHistoryCategory.ControllerPak, cpOld,
                BridgeHistoryActivationFailure.MissingFile, "missing");
            BridgeHistoryActivationResult missingTp = Failed(
                BridgeHistoryCategory.TransferPak, tpOld,
                BridgeHistoryActivationFailure.MissingFile, "missing");
            Assert(BridgeHistoryDesktopPresentation.Classify(missingCp).Kind ==
                BridgeHistoryDesktopOutcomeKind.PromptRemoveMissingFile &&
                form.Contains("Remove this item from History?"),
                "9 missing MPK produces removal prompt intent");
            Assert(BridgeHistoryDesktopPresentation.Classify(missingTp).Kind ==
                BridgeHistoryDesktopOutcomeKind.PromptRemoveMissingFile,
                "10 missing ROM produces removal prompt intent");

            int beforeRemove = history.Current.ControllerPak.Count;
            await history.RemoveAsync(BridgeHistoryCategory.ControllerPak, cpOld);
            Assert(history.Current.ControllerPak.Count == beforeRemove - 1 &&
                history.Current.ControllerPak.All(x => !Same(x.Path, cpOld)),
                "11 missing-file Yes removes exactly selected entry");
            int beforeNo = history.Current.TransferPak.Count;
            Assert(history.Current.TransferPak.Count == beforeNo,
                "12 missing-file No preserves entry without service mutation");

            await history.ClearAsync(BridgeHistoryCategory.ControllerPak);
            Assert(history.Current.ControllerPak.Count == 0 &&
                history.Current.TransferPak.Count == 2,
                "13 Clear Controller Pak affects only Controller Pak History");
            await history.RecordControllerPakAsync(cpNew);
            await history.ClearAsync(BridgeHistoryCategory.TransferPak);
            Assert(history.Current.TransferPak.Count == 0 &&
                history.Current.ControllerPak.Count == 1,
                "14 Clear Transfer Pak affects only Transfer Pak History");
            await history.RecordTransferPakAsync(tpNew);
            await history.ClearAllAsync();
            Assert(history.Current.ControllerPak.Count == 0 &&
                history.Current.TransferPak.Count == 0,
                "15 Clear All clears both categories");

            SemanticState semantic = new(BridgeAccessoryMode.TransferPak,
                "active.mpk", "active.gb", "active.sav");
            SemanticState beforeClear = semantic;
            await history.ClearAllAsync();
            Assert(semantic == beforeClear,
                "16 clear operations do not alter active accessory/content state");

            BridgeHistorySnapshot one = new(1,
                [new(Path.GetFullPath(cpNew), time.GetUtcNow())],
                [new(Path.GetFullPath(tpNew), time.GetUtcNow())], string.Empty);
            foreach (FirmwarePersonality personality in new[]
                { FirmwarePersonality.Network, FirmwarePersonality.Diagnostics,
                  FirmwarePersonality.Unknown })
            {
                BridgeHistoryDesktopMenuSnapshot blocked =
                    BridgeHistoryDesktopPresentation.Compose(one, personality);
                Assert(blocked.ControllerPak.All(x => !x.ActivationEnabled) &&
                    blocked.TransferPak.All(x => !x.ActivationEnabled),
                    $"17 History activation disabled for {personality}");
            }

            string historyHandlers = Slice(form, "private async void HistoryEntryMenuItem_Click",
                "private async void SetNetworkPakWifiTransport");
            Assert(!historyHandlers.Contains("_firmwareService") &&
                !historyHandlers.Contains("SwitchFirmware"),
                "18 History UI makes no firmware switch request");

            BridgeHistoryService recency = new(
                new BridgeHistoryJsonStore(Path.Combine(Root, "recency.json")),
                timeProvider: time);
            await recency.RecordControllerPakAsync(cpOld);
            time.Advance(TimeSpan.FromMinutes(1));
            await recency.RecordControllerPakAsync(cpNew);
            time.Advance(TimeSpan.FromMinutes(1));
            await recency.RecordControllerPakAsync(cpOld);
            BridgeHistoryDesktopMenuSnapshot refreshed =
                BridgeHistoryDesktopPresentation.Compose(recency.Current,
                    FirmwarePersonality.Controller);
            Assert(Same(refreshed.ControllerPak[0].Path, cpOld) &&
                refreshed.ControllerPak.Count == 2,
                "19 successful activation-style record refreshes menu recency");

            SemanticState failedState = semantic;
            BridgeHistoryDesktopOutcome failure = BridgeHistoryDesktopPresentation.Classify(
                Failed(BridgeHistoryCategory.ControllerPak, cpNew,
                    BridgeHistoryActivationFailure.ContentFailure, "content failed"));
            Assert(failure.Kind == BridgeHistoryDesktopOutcomeKind.ContentActivationFailed &&
                semantic == failedState,
                "20 failed activation does not falsely alter displayed semantic authority");
            BridgeHistoryActivationResult persistenceFailure = Failed(
                BridgeHistoryCategory.TransferPak, tpNew,
                BridgeHistoryActivationFailure.AccessoryTransitionFailure,
                "flush failed") with
            {
                AccessoryResult = new(BridgeAccessoryTransitionFailure.Persistence)
            };
            Assert(BridgeHistoryDesktopPresentation.Classify(persistenceFailure).Kind ==
                BridgeHistoryDesktopOutcomeKind.PersistenceFailed,
                "20a outgoing persistence failure is presented distinctly");

            BridgeHistoryActivationResult warningResult = new(true, false, true,
                BridgeHistoryCategory.ControllerPak, cpNew,
                BridgeHistoryActivationFailure.None, string.Empty)
            {
                HistoryResult = new(false, BridgeHistoryFailure.PersistenceFailure,
                    BridgeHistoryOperation.RecordControllerPak, recency.Current,
                    "disk failed")
            };
            BridgeHistoryDesktopOutcome historyWarning =
                BridgeHistoryDesktopPresentation.Classify(warningResult);
            Assert(historyWarning.Kind ==
                BridgeHistoryDesktopOutcomeKind.ActivatedWithHistoryWarning &&
                historyWarning.Message.Contains("Activated") &&
                historyWarning.Message.Contains("disk failed"),
                "21 activation success plus History failure is presented truthfully");

            BridgeHistoryDesktopMenuSnapshot rebuilt =
                BridgeHistoryDesktopPresentation.Compose(recency.Current,
                    FirmwarePersonality.Controller);
            Assert(refreshed == rebuilt ||
                refreshed.ControllerPak.SequenceEqual(rebuilt.ControllerPak),
                "22 authoritative menu rebuild is idempotent");
            Assert(Count(form, "categoryMenu.DropDownItems.Clear();") == 1 &&
                Count(form, "item.Click += HistoryEntryMenuItem_Click;") == 1,
                "23 rebuild replaces runtime entries and adds one handler per new item");

            Assert(form.Contains("NewControllerPakMenuItem_Click") &&
                form.Contains("LoadControllerPakMenuItem_Click") &&
                form.Contains("SaveControllerPakMenuItem_Click") &&
                form.Contains("InstallTransferRomFlashMenuItem_Click"),
                "24 existing manual Controller Pak/Transfer Pak workflows remain present");
            Assert(Count(form, "TransferPakInstallWarning") == 3 &&
                Count(form, "\"Install Transfer Pak ROM\"") == 2,
                "25 manual and History Transfer Pak flows share the unchanged warning");

            Console.WriteLine("PASS: BridgeHistoryDesktopPresentation smoke (25 requirements)");
        }
        finally
        {
            string full = Path.GetFullPath(Root);
            string allowed = Path.GetFullPath(@"D:\NICE64\history-v1\.history-presentation-tests") +
                Path.DirectorySeparatorChar;
            if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static BridgeHistoryActivationResult Failed(BridgeHistoryCategory category,
        string path, BridgeHistoryActivationFailure failure, string error) =>
        new(false, false, false, category, path, failure, error);

    private static string FindSourceRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            DirectoryInfo? cursor = new(start);
            while (cursor is not null)
            {
                if (File.Exists(Path.Combine(cursor.FullName, "Form1.cs")))
                    return cursor.FullName;
                cursor = cursor.Parent;
            }
        }
        throw new FileNotFoundException("Could not locate active Form1.cs.");
    }

    private static string Slice(string text, string startMarker, string endMarker)
    {
        int start = text.IndexOf(startMarker, StringComparison.Ordinal);
        int end = text.IndexOf(endMarker, start + startMarker.Length,
            StringComparison.Ordinal);
        if (start < 0 || end < 0) throw new InvalidOperationException(
            $"Could not find source slice {startMarker} -> {endMarker}.");
        return text[start..end];
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        for (int index = 0; (index = text.IndexOf(value, index,
                 StringComparison.Ordinal)) >= 0; index += value.Length) count++;
        return count;
    }

    private static bool Same(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}

public enum FirmwarePersonality { Unknown, Controller, Diagnostics, Network }
public enum BridgeAccessoryMode { None, ControllerPak, RumblePak, TransferPak, NetworkPak }
public enum BridgeHistoryActivationFailure
{
    None, Busy, Cancelled, InvalidPath, NormalModeRequired, MissingFile,
    ConfirmationRequired, InspectionFailure, AccessoryTransitionFailure,
    ContentFailure, UnexpectedFailure
}
public sealed record BridgeHistoryActivationResult(bool ActivationSucceeded,
    bool HistoryRecorded, bool StateChanged, BridgeHistoryCategory Category,
    string Path, BridgeHistoryActivationFailure Failure, string Error)
{
    public BridgeHistoryResult? HistoryResult { get; init; }
    public BridgeAccessoryTransitionResult? AccessoryResult { get; init; }
}
public enum BridgeAccessoryTransitionFailure { None, Persistence, Unexpected }
public sealed record BridgeAccessoryTransitionResult(
    BridgeAccessoryTransitionFailure Failure);
internal sealed record SemanticState(BridgeAccessoryMode Accessory,
    string ControllerPakPath, string RomPath, string SavePath);
internal sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
{
    private DateTimeOffset _current = current;
    public override DateTimeOffset GetUtcNow() => _current;
    public void Advance(TimeSpan duration) => _current += duration;
}
