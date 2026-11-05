namespace PicoSwitchController;

internal static class Program
{
    private static readonly string Root = Path.Combine(
        @"D:\NICE64\history-v1\.history-activation-tests", Guid.NewGuid().ToString("N"));

    private static async Task Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            await RecordingTestsAsync();
            await ActivationTestsAsync();
            Console.WriteLine("PASS: BridgeHistoryActivationService headless smoke (43 requirements)");
        }
        finally
        {
            string full = Path.GetFullPath(Root);
            string allowed = Path.GetFullPath(@"D:\NICE64\history-v1\.history-activation-tests") +
                Path.DirectorySeparatorChar;
            if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task RecordingTestsAsync()
    {
        MutableTimeProvider time = new(new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero));
        BridgeHistoryService history = NewHistory("recording", time);
        FakeControllerPakContent cp = new();
        BridgeHistoryRecordingControllerPakContentService recordedCp = new(cp, history);
        string create = Touch("create.mpk");
        cp.NextPath = create;
        await recordedCp.CreateAsync(create);
        Assert(history.Current.ControllerPak.Any(x => Same(x.Path, create)),
            "1 successful Controller Pak Create records History");

        string load = Touch("load.mpk");
        cp.NextPath = load;
        await recordedCp.LoadAsync(load);
        Assert(history.Current.ControllerPak[0].Path == Path.GetFullPath(load),
            "2 successful Controller Pak Load records History");

        DateTimeOffset old = history.Current.ControllerPak[0].LastUsedUtc;
        time.Advance(TimeSpan.FromMinutes(1));
        cp.NextPath = load;
        await recordedCp.SaveAsync(load);
        Assert(history.Current.ControllerPak.Count(x => Same(x.Path, load)) == 1 &&
            history.Current.ControllerPak[0].LastUsedUtc > old,
            "3 successful Controller Pak Save refreshes association");

        int beforeCpFailure = history.Current.ControllerPak.Count;
        cp.FailNext = true;
        await recordedCp.LoadAsync(Touch("failed.mpk"));
        Assert(history.Current.ControllerPak.Count == beforeCpFailure &&
            !recordedCp.LastRecording.ContentSucceeded,
            "4 failed Controller Pak operation does not record");

        FakeTransferPakContent tp = new();
        BridgeHistoryRecordingTransferPakContentService recordedTp = new(tp, history);
        string rom = Touch("full.gb");
        tp.NextRomPath = rom;
        await recordedTp.InstallRomAsync(rom);
        Assert(history.Current.TransferPak.Any(x => Same(x.RomPath, rom)),
            "5 successful full Transfer Pak install records ROM");

        int beforeTpFailure = history.Current.TransferPak.Count;
        tp.FailNextInstall = true;
        await recordedTp.InstallRomAsync(Touch("failed.gbc"));
        Assert(history.Current.TransferPak.Count == beforeTpFailure,
            "6 failed Transfer Pak install does not record");

        Assert((object)recordedTp is not IBridgeTransferPakLegacyContentService,
            "7 legacy Transfer Pak loader is not exposed through recording decorator");
        Assert(history.Current.TransferPak.All(x =>
                Path.GetExtension(x.RomPath).Equals(".gb", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(x.RomPath).Equals(".gbc", StringComparison.OrdinalIgnoreCase)),
            "8 Transfer Pak History contains ROM paths only, never SAV paths");

        int beforeRepeat = history.Current.TransferPak.Count;
        DateTimeOffset oldRomTime = history.Current.TransferPak[0].LastUsedUtc;
        time.Advance(TimeSpan.FromMinutes(1));
        tp.NextRomPath = rom;
        await recordedTp.InstallRomAsync(rom);
        Assert(history.Current.TransferPak.Count == beforeRepeat &&
            history.Current.TransferPak[0].LastUsedUtc > oldRomTime,
            "9 repeated successful association refreshes without duplication");

        FakeHistory failingHistory = new() { FailWrites = true };
        FakeControllerPakContent successfulCp = new() { NextPath = create };
        BridgeHistoryRecordingControllerPakContentService failingRecorder =
            new(successfulCp, failingHistory);
        BridgeControllerPakContentResult content = await failingRecorder.LoadAsync(create);
        Assert(content.Success && failingRecorder.LastRecording.ContentSucceeded &&
            !failingRecorder.LastRecording.HistoryRecorded,
            "10 History failure does not falsify successful content result");
    }

    private static async Task ActivationTestsAsync()
    {
        Rig cpNone = Rig.Create(BridgeAccessoryMode.None);
        string cp1 = cpNone.Add("none-cp.mpk");
        BridgeHistoryActivationResult cpNoneResult =
            await cpNone.Service.ActivateControllerPakAsync(cp1);
        Assert(cpNoneResult.ActivationSucceeded &&
            cpNone.Device.Current.ActiveAccessory == BridgeAccessoryMode.ControllerPak,
            "11 Normal + None activates Controller Pak History");

        Rig tpNone = Rig.Create(BridgeAccessoryMode.None);
        string tp1 = tpNone.Add("none-tp.gb");
        BridgeHistoryActivationResult tpNoneResult =
            await tpNone.Service.ActivateTransferPakAsync(tp1, true);
        Assert(tpNoneResult.ActivationSucceeded &&
            tpNone.Device.Current.ActiveAccessory == BridgeAccessoryMode.TransferPak,
            "12 Normal + None activates Transfer Pak History");

        Rig cpSame = Rig.Create(BridgeAccessoryMode.ControllerPak);
        string cp2 = cpSame.Add("another-cp.mpk");
        Assert((await cpSame.Service.ActivateControllerPakAsync(cp2)).ActivationSucceeded &&
            Same(cpSame.Controller.Current.Path!, cp2),
            "13 Normal + Controller Pak loads another Controller Pak entry");

        Rig tpSame = Rig.Create(BridgeAccessoryMode.TransferPak);
        string tp2 = tpSame.Add("another-tp.gbc");
        Assert((await tpSame.Service.ActivateTransferPakAsync(tp2, true)).ActivationSucceeded &&
            Same(tpSame.Transfer.Current.RomPath!, tp2),
            "14 Normal + Transfer Pak installs another ROM entry");

        Rig cpToTp = Rig.Create(BridgeAccessoryMode.ControllerPak);
        string crossTp = cpToTp.Add("cross.gbc");
        Assert((await cpToTp.Service.ActivateTransferPakAsync(crossTp, true)).ActivationSucceeded,
            "15 Controller Pak to Transfer Pak succeeds");

        Rig tpToCp = Rig.Create(BridgeAccessoryMode.TransferPak);
        string crossCp = tpToCp.Add("cross.mpk");
        Assert((await tpToCp.Service.ActivateControllerPakAsync(crossCp)).ActivationSucceeded,
            "16 Transfer Pak to Controller Pak succeeds");
        Assert(IndexOf(tpToCp.Events, "flush:TransferPak") <
            IndexOf(tpToCp.Events, "accessory:ControllerPak"),
            "17 TP persistence flush precedes Controller Pak transition");
        Assert(IndexOf(cpToTp.Events, "flush:ControllerPak") <
            IndexOf(cpToTp.Events, "accessory:TransferPak"),
            "18 Controller Pak persistence flush precedes Transfer Pak transition");

        Rig flushFail = Rig.Create(BridgeAccessoryMode.TransferPak);
        flushFail.Accessory.FailFlush = true;
        string blocked = flushFail.Add("blocked.mpk");
        BridgeHistoryActivationResult blockedResult =
            await flushFail.Service.ActivateControllerPakAsync(blocked);
        Assert(!blockedResult.ActivationSucceeded &&
            flushFail.Device.Current.ActiveAccessory == BridgeAccessoryMode.TransferPak,
            "19 flush failure blocks accessory mutation");
        Assert(flushFail.Controller.LoadCalls == 0,
            "20 flush failure blocks target content operation");

        Rig missing = Rig.Create(BridgeAccessoryMode.None);
        BridgeHistoryActivationResult missingCp = await missing.Service
            .ActivateControllerPakAsync(Path.Combine(Root, "missing.mpk"));
        Assert(missingCp.Failure == BridgeHistoryActivationFailure.MissingFile &&
            missing.Accessory.SelectCalls == 0,
            "21 missing MPK returns MissingFile and mutates nothing");
        BridgeHistoryActivationResult missingTp = await missing.Service
            .ActivateTransferPakAsync(Path.Combine(Root, "missing.gb"), true);
        Assert(missingTp.Failure == BridgeHistoryActivationFailure.MissingFile &&
            missing.Transfer.InspectCalls == 0,
            "22 missing ROM returns MissingFile and mutates nothing");

        Rig network = Rig.Create(BridgeAccessoryMode.NetworkPak, FirmwarePersonality.Network);
        BridgeHistoryActivationResult networkResult = await network.Service
            .ActivateControllerPakAsync(network.Add("network.mpk"));
        Assert(networkResult.Failure == BridgeHistoryActivationFailure.NormalModeRequired,
            "23 Network personality requires Normal mode");
        Rig unknown = Rig.Create(BridgeAccessoryMode.None, FirmwarePersonality.Unknown);
        Assert((await unknown.Service.ActivateControllerPakAsync(
                unknown.Add("unknown.mpk"))).Failure ==
            BridgeHistoryActivationFailure.NormalModeRequired,
            "24 Diagnostics/unknown personality requires Normal mode");
        Assert(network.Accessory.FirmwareSwitchCalls == 0 &&
            unknown.Accessory.FirmwareSwitchCalls == 0,
            "25 History activation never requests a firmware switch");

        Assert(cpNone.Controller.LoadCalls == 1 && Same(cpNone.Controller.LastLoadPath!, cp1),
            "26 Controller Pak activation uses existing LoadAsync path");
        Assert(tpNone.Transfer.InspectCalls == 1 && tpNone.Transfer.InstallCalls == 1,
            "27 Transfer Pak activation uses existing inspect/install paths");
        Rig unconfirmed = Rig.Create(BridgeAccessoryMode.None);
        BridgeHistoryActivationResult confirmation = await unconfirmed.Service
            .ActivateTransferPakAsync(unconfirmed.Add("confirm.gb"), false);
        Assert(confirmation.Failure == BridgeHistoryActivationFailure.ConfirmationRequired &&
            confirmation.Inspection is not null && unconfirmed.Accessory.SelectCalls == 0 &&
            unconfirmed.Transfer.InstallCalls == 0,
            "27a Transfer Pak inspection requires explicit destructive-install confirmation");
        Assert(tpNoneResult.TransferPakResult?.Snapshot.SavePath ==
            Path.ChangeExtension(Path.GetFullPath(tp1), ".sav"),
            "28 Transfer Pak content service remains save-association authority");

        DateTimeOffset prior = cpSame.History.Current.ControllerPak.First().LastUsedUtc;
        cpSame.Time.Advance(TimeSpan.FromMinutes(1));
        await cpSame.Service.ActivateControllerPakAsync(cp2);
        Assert(cpSame.History.Current.ControllerPak[0].LastUsedUtc > prior &&
            cpSame.History.Current.ControllerPak.Count == 1,
            "29 successful activation refreshes History recency");

        Rig failed = Rig.Create(BridgeAccessoryMode.ControllerPak);
        string failedPath = failed.Add("failed-activation.mpk");
        await failed.History.RecordControllerPakAsync(failedPath);
        DateTimeOffset unchanged = failed.History.Current.ControllerPak[0].LastUsedUtc;
        failed.Time.Advance(TimeSpan.FromMinutes(1));
        failed.Controller.FailNext = true;
        await failed.Service.ActivateControllerPakAsync(failedPath);
        Assert(failed.History.Current.ControllerPak[0].LastUsedUtc == unchanged,
            "30 failed activation does not update History recency");

        Rig historyFailure = Rig.Create(BridgeAccessoryMode.None, failingHistory: true);
        BridgeHistoryActivationResult historyFailureResult = await historyFailure.Service
            .ActivateControllerPakAsync(historyFailure.Add("history-fail.mpk"));
        Assert(historyFailureResult.ActivationSucceeded &&
            !historyFailureResult.HistoryRecorded &&
            historyFailure.Device.Current.ActiveAccessory == BridgeAccessoryMode.ControllerPak,
            "31 History failure preserves truthful successful device activation");

        Assert(tpNone.RunningHistoryAdmissions == 1 &&
            tpNone.Operations.Current.Kind == BridgeOperationKind.HistoryActivation &&
            tpNone.Operations.Current.Status == BridgeOperationStatus.Succeeded,
            "32 exactly one F8 HistoryActivation owns the compound operation");
        Assert(tpNone.Accessory.NestedAdmissionAttempts == 0 &&
            tpNone.Transfer.NestedAdmissionAttempts == 0,
            "33 semantic services create no nested F8 lease");

        Rig cancelled = Rig.Create(BridgeAccessoryMode.None);
        using CancellationTokenSource stop = new();
        stop.Cancel();
        BridgeHistoryActivationResult cancelledResult = await cancelled.Service
            .ActivateControllerPakAsync(cancelled.Add("cancelled.mpk"), cancellationToken: stop.Token);
        Assert(cancelledResult.Failure == BridgeHistoryActivationFailure.Cancelled &&
            cancelled.Accessory.SelectCalls == 0,
            "34 cancellation before destructive transition is truthful");

        Rig postCommit = Rig.Create(BridgeAccessoryMode.None);
        postCommit.Controller.CancelAfterMutation = true;
        BridgeHistoryActivationResult postCommitResult = await postCommit.Service
            .ActivateControllerPakAsync(postCommit.Add("post-commit.mpk"));
        Assert(!postCommitResult.ActivationSucceeded && postCommitResult.StateChanged &&
            postCommit.Operations.Current.Status == BridgeOperationStatus.Failed,
            "35 post-commit state is never reported as untouched or cleanly cancelled");

        Rig begunCp = Rig.Create(BridgeAccessoryMode.None);
        string begunCpPath = begunCp.Add("begin-cp.mpk");
        BridgeHistoryActivationStart cpStart = begunCp.Service.BeginControllerPak(
            begunCpPath);
        Assert(cpStart.Accepted && cpStart.OperationId is > 0 &&
            cpStart.OperationId == begunCp.Operations.Current.OperationId &&
            begunCp.Operations.Current.Status == BridgeOperationStatus.Running,
            "36 begin seam returns the real running HistoryActivation operation ID");
        Assert(begunCp.RunningHistoryAdmissions == 1,
            "37 begin seam admits exactly one semantic History operation");
        BridgeHistoryActivationResult begunCpResult = await cpStart.Completion!;
        Assert(begunCpResult.ActivationSucceeded &&
            begunCp.Controller.LoadCalls == 1 &&
            begunCp.Operations.Current.Status == BridgeOperationStatus.Succeeded,
            "38 begun Controller Pak core runs once and terminates truthfully");

        Rig guarded = Rig.Create(BridgeAccessoryMode.None);
        string guardedPath = guarded.Add("guarded.mpk");
        int guardCalls = 0;
        BridgeHistoryActivationStart guardedStart = guarded.Service.BeginControllerPak(
            guardedPath, admissionGuard: () => { guardCalls++; return false; });
        Assert(guardedStart.Disposition ==
                BridgeHistoryActivationStartDisposition.AdmissionGuardRejected &&
            guardCalls == 1 && guarded.RunningHistoryAdmissions == 0 &&
            guarded.Controller.LoadCalls == 0,
            "39 rejected admission guard creates no F8 lease or activation");

        Rig busy = Rig.Create(BridgeAccessoryMode.None);
        string busyPath = busy.Add("busy.mpk");
        BridgeOperationAdmission other = busy.Operations.TryBegin(
            BridgeOperationKind.FirmwareModeSwitch, "Firmware");
        BridgeHistoryActivationStart busyStart = busy.Service.BeginControllerPak(busyPath);
        Assert(busyStart.Disposition == BridgeHistoryActivationStartDisposition.Busy &&
            busyStart.Operation.OperationId == other.OperationId &&
            busy.RunningHistoryAdmissions == 0 && busy.Controller.LoadCalls == 0,
            "40 existing F8 owner returns busy without nested lease or activation");
        other.Lease!.Fail("test-complete", null);
        Assert(busy.Operations.Current.Status == BridgeOperationStatus.Failed,
            "41 busy rejection does not corrupt current operation ownership");

        Rig begunTp = Rig.Create(BridgeAccessoryMode.None);
        string begunTpPath = begunTp.Add("begin-tp.gb");
        BridgeHistoryActivationStart tpPreflight = await begunTp.Service
            .BeginTransferPakAsync(begunTpPath, destructiveInstallConfirmed: false);
        Assert(tpPreflight.Disposition == BridgeHistoryActivationStartDisposition.Immediate &&
            tpPreflight.ImmediateResult?.Failure ==
                BridgeHistoryActivationFailure.ConfirmationRequired &&
            begunTp.Transfer.InspectCalls == 1 && begunTp.Transfer.InstallCalls == 0 &&
            begunTp.RunningHistoryAdmissions == 0 && begunTp.Accessory.SelectCalls == 0,
            "42 unconfirmed Transfer Pak preflight inspects without F8 or mutation");
        BridgeHistoryActivationStart tpStart = await begunTp.Service
            .BeginTransferPakAsync(begunTpPath, destructiveInstallConfirmed: true);
        Assert(tpStart.Accepted && tpStart.OperationId ==
            begunTp.Operations.Current.OperationId && begunTp.Transfer.InspectCalls == 2 &&
            begunTp.RunningHistoryAdmissions == 1,
            "43 confirmed Transfer Pak re-inspects and admits exactly once");
        BridgeHistoryActivationResult begunTpResult = await tpStart.Completion!;
        Assert(begunTpResult.ActivationSucceeded && begunTp.Transfer.InstallCalls == 1,
            "43a confirmed Transfer Pak activation core runs exactly once");
    }

    private static BridgeHistoryService NewHistory(string name, TimeProvider? time = null) =>
        new(new BridgeHistoryJsonStore(Path.Combine(Root, name, "history.json")),
            timeProvider: time);

    private static string Touch(string name)
    {
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private static bool Same(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(List<string> events, string value) => events.IndexOf(value);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class Rig
    {
        private Rig(BridgeAccessoryMode accessory, FirmwarePersonality firmware,
            bool failingHistory)
        {
            Device = new FakeDevice(firmware, accessory);
            Operations = new BridgeOperationCoordinator();
            Time = new MutableTimeProvider(
                new DateTimeOffset(2026, 9, 3, 1, 0, 0, TimeSpan.Zero));
            IBridgeHistoryService history = failingHistory
                ? new FakeHistory { FailWrites = true }
                : NewHistory(Guid.NewGuid().ToString("N"), Time);
            History = history;
            Controller = new FakeControllerPakContent(Operations, Events);
            Transfer = new FakeTransferPakContent(Operations, Events);
            Accessory = new FakeAccessory(Device, Operations, Events);
            Service = new BridgeHistoryActivationService(History, Device, Accessory,
                Controller, Transfer, Operations, new FakeActivationFiles());
            Operations.CurrentChanged += (_, e) =>
            {
                if (e.Previous.Status != BridgeOperationStatus.Running &&
                    e.Current.Status == BridgeOperationStatus.Running &&
                    e.Current.Kind == BridgeOperationKind.HistoryActivation)
                    RunningHistoryAdmissions++;
            };
        }

        public FakeDevice Device { get; }
        public BridgeOperationCoordinator Operations { get; }
        public MutableTimeProvider Time { get; }
        public IBridgeHistoryService History { get; }
        public FakeControllerPakContent Controller { get; }
        public FakeTransferPakContent Transfer { get; }
        public FakeAccessory Accessory { get; }
        public BridgeHistoryActivationService Service { get; }
        public List<string> Events { get; } = [];
        public int RunningHistoryAdmissions { get; private set; }

        public string Add(string name)
        {
            string path = Touch(Guid.NewGuid().ToString("N") + "-" + name);
            FakeActivationFiles.Existing.Add(Path.GetFullPath(path));
            return path;
        }

        public static Rig Create(BridgeAccessoryMode accessory,
            FirmwarePersonality firmware = FirmwarePersonality.Controller,
            bool failingHistory = false) => new(accessory, firmware, failingHistory);
    }
}

public enum FirmwarePersonality { Unknown, Controller, Diagnostics, Network }
public enum BridgeAccessoryMode { None, ControllerPak, RumblePak, TransferPak, NetworkPak }
public enum BridgeAccessoryTransitionFailure
{
    None, Persistence, Cancelled, Unexpected
}
public sealed record BridgeAccessoryTransitionResult(bool Success,
    BridgeAccessoryMode RequestedAccessory, BridgeAccessoryMode ActiveAccessory,
    object? FirmwareResult, bool AccessoryCommandSent, bool NetworkPakActivated,
    string Error)
{
    public BridgeAccessoryTransitionFailure Failure { get; init; }
}
public interface IBridgeAccessoryService
{
    Task<BridgeAccessoryTransitionResult> SelectAccessoryAsync(
        BridgeAccessoryMode accessory, CancellationToken cancellationToken = default);
}
public sealed record BridgeDeviceStateSnapshot(FirmwarePersonality FirmwarePersonality,
    BridgeAccessoryMode ActiveAccessory);
public interface IBridgeDeviceStateProvider
{
    BridgeDeviceStateSnapshot Current { get; }
}

public enum BridgeControllerPakContentOperation { None, Create, Load, Save }
public enum BridgeControllerPakContentFailure { None, Cancelled, TransferFailure }
public enum BridgeControllerPakContentPhase { Preparing, Uploading, Completed }
public sealed record BridgeControllerPakContentProgress(BridgeControllerPakContentOperation Operation,
    BridgeControllerPakContentPhase Phase, int CompletedBytes, int TotalBytes,
    int ItemIndex, int ItemCount);
public sealed record BridgeControllerPakContentSnapshot(string? Path, bool HasAssociatedFile,
    bool ContentPresent, int ImageSize, BridgeControllerPakContentOperation LastSuccessfulOperation,
    string LastError);
public sealed record BridgeControllerPakContentResult(bool Success,
    BridgeControllerPakContentFailure Failure, BridgeControllerPakContentOperation Operation,
    BridgeControllerPakContentSnapshot Snapshot, string Error);
public interface IBridgeControllerPakContentService
{
    BridgeControllerPakContentSnapshot Current { get; }
    Task<BridgeControllerPakContentResult> CreateAsync(string? path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<BridgeControllerPakContentResult> LoadAsync(string path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<BridgeControllerPakContentResult> SaveAsync(string path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public enum BridgeTransferPakInstallKind { None, FullFlash, LegacyFirst64K }
public enum BridgeTransferPakContentOperation { None, InspectRom, InstallFullRom, LoadLegacyRom, SaveCurrent }
public enum BridgeTransferPakContentFailure { None, Cancelled, InvalidRom, TransferFailure }
public enum BridgeTransferPakContentPhase { ReadingRom, InstallingRom, Completed }
public sealed record BridgeTransferPakContentProgress(BridgeTransferPakContentOperation Operation,
    BridgeTransferPakContentPhase Phase, int CompletedBytes, int TotalBytes,
    int ItemIndex, int ItemCount);
public sealed record BridgeTransferPakRomInspection(string RomPath, string SavePath,
    int RomLength, long? ExistingSaveLength, object Metadata);
public sealed record BridgeTransferPakContentSnapshot(string? RomPath, string? SavePath,
    bool HasAssociatedRom, bool HasAssociatedSave, bool ContentPresent,
    BridgeTransferPakInstallKind InstallKind, int RomLength, int ExpectedSaveLength,
    object? Metadata, BridgeTransferPakContentOperation LastSuccessfulOperation, string LastError);
public sealed record BridgeTransferPakContentResult(bool Success,
    BridgeTransferPakContentFailure Failure, BridgeTransferPakContentOperation Operation,
    BridgeTransferPakContentSnapshot Snapshot, BridgeTransferPakRomInspection? Inspection,
    string Error);
public interface IBridgeTransferPakContentService
{
    BridgeTransferPakContentSnapshot Current { get; }
    Task<BridgeTransferPakContentResult> InspectRomAsync(string path,
        CancellationToken cancellationToken = default);
    Task<BridgeTransferPakContentResult> InstallRomAsync(string path,
        IProgress<BridgeTransferPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<BridgeTransferPakContentResult> SaveCurrentAsync(string? path = null,
        IProgress<BridgeTransferPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
public interface IBridgeTransferPakLegacyContentService { }

internal sealed class FakeDevice(FirmwarePersonality firmware, BridgeAccessoryMode accessory) :
    IBridgeDeviceStateProvider
{
    public BridgeDeviceStateSnapshot Current { get; private set; } = new(firmware, accessory);
    public void SetAccessory(BridgeAccessoryMode value) => Current = Current with { ActiveAccessory = value };
}

internal sealed class FakeAccessory(FakeDevice device, BridgeOperationCoordinator operations,
    List<string> events) : IBridgeAccessoryService
{
    public int SelectCalls { get; private set; }
    public int FirmwareSwitchCalls { get; private set; }
    public int NestedAdmissionAttempts { get; private set; }
    public bool FailFlush { get; set; }

    public Task<BridgeAccessoryTransitionResult> SelectAccessoryAsync(
        BridgeAccessoryMode accessory, CancellationToken cancellationToken = default)
    {
        SelectCalls++;
        if (operations.Current.Kind != BridgeOperationKind.HistoryActivation ||
            operations.Current.Status != BridgeOperationStatus.Running)
            NestedAdmissionAttempts++;
        BridgeAccessoryMode previous = device.Current.ActiveAccessory;
        if (previous != accessory && previous != BridgeAccessoryMode.None)
        {
            events.Add("flush:" + previous);
            if (FailFlush)
                return Task.FromResult(new BridgeAccessoryTransitionResult(false, accessory,
                    previous, null, false, false, "flush failed")
                    { Failure = BridgeAccessoryTransitionFailure.Persistence });
        }
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new BridgeAccessoryTransitionResult(false, accessory,
                previous, null, false, false, "cancelled")
                { Failure = BridgeAccessoryTransitionFailure.Cancelled });
        events.Add("accessory:" + accessory);
        device.SetAccessory(accessory);
        return Task.FromResult(new BridgeAccessoryTransitionResult(true, accessory,
            accessory, null, true, false, string.Empty));
    }
}

internal sealed class FakeControllerPakContent : IBridgeControllerPakContentService
{
    private readonly BridgeOperationCoordinator? _operations;
    private readonly List<string>? _events;
    public FakeControllerPakContent(BridgeOperationCoordinator? operations = null,
        List<string>? events = null) { _operations = operations; _events = events; }
    public string? NextPath { get; set; }
    public bool FailNext { get; set; }
    public bool CancelAfterMutation { get; set; }
    public int LoadCalls { get; private set; }
    public int NestedAdmissionAttempts { get; private set; }
    public string? LastLoadPath { get; private set; }
    public BridgeControllerPakContentSnapshot Current { get; private set; } =
        new(null, false, false, 32768, BridgeControllerPakContentOperation.None, string.Empty);

    public Task<BridgeControllerPakContentResult> CreateAsync(string? path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) => Complete(BridgeControllerPakContentOperation.Create,
            path ?? NextPath, countLoad: false);
    public Task<BridgeControllerPakContentResult> LoadAsync(string path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LoadCalls++; LastLoadPath = path;
        return Complete(BridgeControllerPakContentOperation.Load, NextPath ?? path, countLoad: true);
    }
    public Task<BridgeControllerPakContentResult> SaveAsync(string path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) => Complete(BridgeControllerPakContentOperation.Save,
            NextPath ?? path, countLoad: false);

    private Task<BridgeControllerPakContentResult> Complete(
        BridgeControllerPakContentOperation operation, string? path, bool countLoad)
    {
        if (_operations is not null && (_operations.Current.Kind != BridgeOperationKind.HistoryActivation ||
            _operations.Current.Status != BridgeOperationStatus.Running)) NestedAdmissionAttempts++;
        _events?.Add("cp.load");
        if (FailNext) { FailNext = false; return Task.FromResult(
            new BridgeControllerPakContentResult(false,
                BridgeControllerPakContentFailure.TransferFailure, operation,
                Current, "failed")); }
        if (CancelAfterMutation)
        {
            Current = new(Path.GetFullPath(path!), true, true, 32768, operation, "cancelled");
            return Task.FromResult(new BridgeControllerPakContentResult(false,
                BridgeControllerPakContentFailure.Cancelled, operation, Current, "cancelled"));
        }
        string? full = path is null ? null : Path.GetFullPath(path);
        Current = new(full, full is not null, true, 32768, operation, string.Empty);
        return Task.FromResult(new BridgeControllerPakContentResult(true,
            BridgeControllerPakContentFailure.None, operation, Current, string.Empty));
    }
}

internal sealed class FakeTransferPakContent : IBridgeTransferPakContentService
{
    private readonly BridgeOperationCoordinator? _operations;
    private readonly List<string>? _events;
    public FakeTransferPakContent(BridgeOperationCoordinator? operations = null,
        List<string>? events = null) { _operations = operations; _events = events; }
    public string? NextRomPath { get; set; }
    public bool FailNextInstall { get; set; }
    public int InspectCalls { get; private set; }
    public int InstallCalls { get; private set; }
    public int NestedAdmissionAttempts { get; private set; }
    public BridgeTransferPakContentSnapshot Current { get; private set; } = Empty();

    public Task<BridgeTransferPakContentResult> InspectRomAsync(string path,
        CancellationToken cancellationToken = default)
    {
        InspectCalls++;
        string full = Path.GetFullPath(path);
        BridgeTransferPakRomInspection inspection = new(full,
            Path.ChangeExtension(full, ".sav"), 65536, null, new object());
        return Task.FromResult(new BridgeTransferPakContentResult(true,
            BridgeTransferPakContentFailure.None, BridgeTransferPakContentOperation.InspectRom,
            Current, inspection, string.Empty));
    }

    public Task<BridgeTransferPakContentResult> InstallRomAsync(string path,
        IProgress<BridgeTransferPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        InstallCalls++;
        if (_operations is not null && (_operations.Current.Kind != BridgeOperationKind.HistoryActivation ||
            _operations.Current.Status != BridgeOperationStatus.Running)) NestedAdmissionAttempts++;
        _events?.Add("tp.install");
        if (FailNextInstall) { FailNextInstall = false; return Task.FromResult(
            new BridgeTransferPakContentResult(false,
                BridgeTransferPakContentFailure.TransferFailure,
                BridgeTransferPakContentOperation.InstallFullRom, Current, null,
                "failed")); }
        string full = Path.GetFullPath(NextRomPath ?? path);
        Current = new(full, Path.ChangeExtension(full, ".sav"), true, true, true,
            BridgeTransferPakInstallKind.FullFlash, 65536, 32768, new object(),
            BridgeTransferPakContentOperation.InstallFullRom, string.Empty);
        return Task.FromResult(new BridgeTransferPakContentResult(true,
            BridgeTransferPakContentFailure.None,
            BridgeTransferPakContentOperation.InstallFullRom, Current, null, string.Empty));
    }

    public Task<BridgeTransferPakContentResult> SaveCurrentAsync(string? path = null,
        IProgress<BridgeTransferPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.FromResult(new BridgeTransferPakContentResult(
            true, BridgeTransferPakContentFailure.None,
            BridgeTransferPakContentOperation.SaveCurrent, Current, null, string.Empty));

    private static BridgeTransferPakContentSnapshot Empty() => new(null, null, false,
        false, false, BridgeTransferPakInstallKind.None, 0, 32768, null,
        BridgeTransferPakContentOperation.None, string.Empty);
}

internal sealed class FakeActivationFiles : IBridgeHistoryActivationFileSystem
{
    public static HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string NormalizePath(string path) => Path.GetFullPath(path);
    public bool FileExists(string path) => Existing.Contains(Path.GetFullPath(path));
}

internal sealed class FakeHistory : IBridgeHistoryService
{
    private BridgeHistorySnapshot _current = new(1, [], [], string.Empty);
    public bool FailWrites { get; set; }
    public BridgeHistorySnapshot Current => _current;
    public BridgeHistoryResult InitialLoad => new(true, BridgeHistoryFailure.None,
        BridgeHistoryOperation.Load, _current, string.Empty);
    public Task<BridgeHistoryResult> RecordControllerPakAsync(string path,
        CancellationToken cancellationToken = default) => Record(path, true);
    public Task<BridgeHistoryResult> RecordTransferPakAsync(string romPath,
        CancellationToken cancellationToken = default) => Record(romPath, false);
    public Task<BridgeHistoryResult> RemoveAsync(BridgeHistoryCategory category, string path,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BridgeHistoryResult> ClearAsync(BridgeHistoryCategory category,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BridgeHistoryResult> ClearAllAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    private Task<BridgeHistoryResult> Record(string path, bool cp)
    {
        if (FailWrites) return Task.FromResult(new BridgeHistoryResult(false,
            BridgeHistoryFailure.PersistenceFailure,
            cp ? BridgeHistoryOperation.RecordControllerPak : BridgeHistoryOperation.RecordTransferPak,
            _current, "history write failed"));
        string full = Path.GetFullPath(path);
        _current = cp ? _current with { ControllerPak = [new(full, DateTimeOffset.UtcNow)] }
            : _current with { TransferPak = [new(full, DateTimeOffset.UtcNow)] };
        return Task.FromResult(new BridgeHistoryResult(true, BridgeHistoryFailure.None,
            cp ? BridgeHistoryOperation.RecordControllerPak : BridgeHistoryOperation.RecordTransferPak,
            _current, string.Empty));
    }
}

internal sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
{
    private DateTimeOffset _current = current;
    public override DateTimeOffset GetUtcNow() => _current;
    public void Advance(TimeSpan duration) => _current += duration;
}
