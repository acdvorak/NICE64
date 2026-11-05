namespace PicoSwitchController;

using System.Text.Json;

internal static class Program
{
    private static int _passed;

    private static async Task Main()
    {
        const string sessionId = "11111111-2222-3333-4444-555555555555";
        const string itemId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        MutableSessions sessions = new(new(sessionId, BridgeSessionState.Connected));
        MutableResolver resolver = new(Resolved(BridgeHistoryCategory.ControllerPak,
            @"D:\Private\controller.mpk", "controller.mpk", true));
        FakeStarter starter = new();
        BridgeApiV1HistoryActivationFacade facade = new(sessions, resolver, starter);

        BridgeApiV1HistoryActivationResult cp = await facade.StartAsync(
            sessionId, itemId, new(false));
        Check(cp.Disposition == BridgeApiV1HistoryActivationDisposition.Accepted &&
            cp.OperationId == starter.OperationId &&
            cp.OperationKind == "history-activation" &&
            starter.ControllerStarts == 1,
            "exact current session admits Controller Pak once with real operation ID");
        Check(starter.GuardCalls == 1 && resolver.ResolveCalls == 2,
            "current session and History item are revalidated immediately before admission");
        Check(facade.ActiveExecutionCount == 1,
            "accepted background completion is retained and observed");
        starter.Complete();
        await WaitUntilAsync(() => facade.ActiveExecutionCount == 0);
        Check(facade.ActiveExecutionCount == 0,
            "terminal completion is observed without retained task leakage");

        BridgeApiV1HistoryActivationResult irrelevant = await facade.StartAsync(
            sessionId, itemId, new(true));
        Check(irrelevant.Code == "invalid-confirmation" &&
            starter.ControllerStarts == 1,
            "Controller Pak true destructive confirmation is rejected");

        Check((await facade.StartAsync("stale-session", itemId, new(false))).Disposition ==
            BridgeApiV1HistoryActivationDisposition.SessionNotFound,
            "stale session is rejected");
        sessions.CurrentValue = null;
        Check((await facade.StartAsync(sessionId, itemId, new(false))).Disposition ==
            BridgeApiV1HistoryActivationDisposition.SessionNotFound,
            "missing session is rejected");
        sessions.CurrentValue = new(sessionId, BridgeSessionState.TemporarilyUnavailable);
        Check((await facade.StartAsync(sessionId, itemId, new(false))).Code ==
            "session-unavailable", "temporary-unavailable session is bounded");
        sessions.CurrentValue = new("replacement-session", BridgeSessionState.Connected);
        Check((await facade.StartAsync(sessionId, itemId, new(false))).Code ==
            "session-not-found", "genuine session replacement invalidates prior session ID");
        sessions.CurrentValue = new(sessionId, BridgeSessionState.Connected);

        resolver.Current = new(BridgeApiV1HistoryResolutionStatus.NotFound, null);
        Check((await facade.StartAsync(sessionId, itemId, new(false))).Code ==
            "history-item-not-found", "unknown current item is bounded not-found");
        resolver.Current = Resolved(BridgeHistoryCategory.TransferPak,
            @"D:\Private\missing.gb", "missing.gb", false);
        Check((await facade.StartAsync(sessionId, itemId, new(false))).Code ==
            "history-item-unavailable", "retained missing item is bounded unavailable");

        resolver.Current = Resolved(BridgeHistoryCategory.TransferPak,
            @"D:\Private\Pokemon.gb", "Pokemon.gb", true);
        starter.Reset();
        starter.TransferImmediate = Immediate(
            BridgeHistoryActivationFailure.ConfirmationRequired,
            new(@"D:\Private\Pokemon.gb", new("POKEMON")));
        BridgeApiV1HistoryActivationResult confirmation = await facade.StartAsync(
            sessionId, itemId, new(false));
        Check(confirmation.Disposition ==
                BridgeApiV1HistoryActivationDisposition.ConfirmationRequired &&
            confirmation.Code == "confirmation-required" &&
            confirmation.Confirmation is
                { Category: "transfer-pak", FileName: "Pokemon.gb",
                  ContentKind: "game-boy-rom", Title: "POKEMON" },
            "Transfer Pak false confirmation returns safe bounded metadata");
        Check(starter.TransferStarts == 1 && starter.AcceptedExecutions == 0,
            "Transfer Pak false flow performs preflight without activation admission");
        string confirmationJson = JsonSerializer.Serialize(confirmation);
        Check(!confirmationJson.Contains("D:\\", StringComparison.Ordinal) &&
            !confirmationJson.Contains("Private", StringComparison.Ordinal),
            "confirmation result exposes no Windows path");

        starter.Reset();
        starter.TransferImmediate = null;
        BridgeApiV1HistoryActivationResult confirmed = await facade.StartAsync(
            sessionId, itemId, new(true));
        Check(confirmed.Accepted && starter.TransferStarts == 1 &&
            starter.AcceptedExecutions == 1 && starter.GuardCalls == 1,
            "confirmed Transfer Pak revalidates and starts exactly once");
        starter.Complete();
        await WaitUntilAsync(() => facade.ActiveExecutionCount == 0);

        starter.Reset();
        starter.BeforeGuard = () => resolver.Current = Resolved(
            BridgeHistoryCategory.TransferPak, @"D:\Private\Pokemon.gb",
            "Pokemon.gb", false);
        BridgeApiV1HistoryActivationResult changed = await facade.StartAsync(
            sessionId, itemId, new(true));
        Check(changed.Code == "history-item-unavailable" &&
            starter.AcceptedExecutions == 0,
            "item becoming unavailable after preflight fails before F8 admission");

        resolver.Current = Resolved(BridgeHistoryCategory.ControllerPak,
            @"D:\Private\controller.mpk", "controller.mpk", true);
        starter.Reset();
        starter.NextDisposition = BridgeHistoryActivationStartDisposition.Busy;
        BridgeApiV1HistoryActivationResult busy = await facade.StartAsync(
            sessionId, itemId, new(false));
        Check(busy.Disposition == BridgeApiV1HistoryActivationDisposition.Busy &&
            busy.CurrentOperationId == 77 &&
            busy.CurrentOperationKind == "firmware-mode-switch" &&
            starter.AcceptedExecutions == 0,
            "existing F8 owner returns bounded busy information without starting core");

        starter.Reset();
        starter.Throw = true;
        BridgeApiV1HistoryActivationResult failure = await facade.StartAsync(
            sessionId, itemId, new(false));
        string failureJson = JsonSerializer.Serialize(failure);
        Check(failure.Code == "activation-unavailable" &&
            !failureJson.Contains("D:\\", StringComparison.Ordinal) &&
            !failureJson.Contains("secret", StringComparison.OrdinalIgnoreCase),
            "unexpected preparation failure is bounded without raw exception/path leak");

        Check(typeof(IBridgeApiV1HistoryActivationFacade).GetMethods().Single()
            .GetParameters().All(parameter =>
                !parameter.Name!.Contains("path", StringComparison.OrdinalIgnoreCase) &&
                !parameter.Name.Contains("file", StringComparison.OrdinalIgnoreCase)),
            "facade accepts no path or filename identity");
        string sourceRoot = FindSourceDirectory();
        string facadeSource = File.ReadAllText(Path.Combine(
            sourceRoot, "BridgeApiV1HistoryActivation.cs"));
        string hostSource = File.ReadAllText(Path.Combine(sourceRoot, "BridgeApiHost.cs"));
        Check(!hostSource.Contains("history/{itemId}/activate", StringComparison.Ordinal) &&
            !facadeSource.Contains("MapPost", StringComparison.Ordinal),
            "D6A adds no History HTTP route or body binding");
        Check(!facadeSource.Contains("Task.Run", StringComparison.Ordinal) &&
            !facadeSource.Contains("BridgeOperationCoordinator", StringComparison.Ordinal),
            "API facade observes the semantic owner's task and claims no outer F8 lease");
        Check(!facadeSource.Contains("IBridgeConnectionService", StringComparison.Ordinal) &&
            !facadeSource.Contains("SerialBridgeTransport", StringComparison.Ordinal) &&
            !facadeSource.Contains("IBridgeNetworkPak", StringComparison.Ordinal) &&
            !facadeSource.Contains("IBridgeWifiProvisioning", StringComparison.Ordinal),
            "D6A introduces no connection, transport, or Wi-Fi capability");

        Console.WriteLine(
            $"BridgeApiV1HistoryActivationHeadlessSmoke: {_passed} checks passed.");
    }

    private static BridgeApiV1HistoryResolution Resolved(
        BridgeHistoryCategory category, string path, string fileName, bool available) =>
        new(BridgeApiV1HistoryResolutionStatus.Resolved,
            new(category, path, fileName, available));

    private static BridgeHistoryActivationStart Immediate(
        BridgeHistoryActivationFailure failure,
        BridgeTransferPakRomInspection? inspection = null) => new(
            BridgeHistoryActivationStartDisposition.Immediate, null, null,
            new(failure, inspection), new(0, BridgeOperationKind.None));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(5);
        if (!condition()) throw new TimeoutException();
    }

    private static string FindSourceDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName,
                    "BridgeApiV1HistoryActivation.cs"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException();
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _passed++;
        Console.WriteLine($"PASS {name}");
    }
}

internal sealed class MutableSessions(BridgeSessionSnapshot? current) :
    IBridgeSessionProvider
{
    public BridgeSessionSnapshot? CurrentValue { get; set; } = current;
    public BridgeSessionSnapshot? Current => CurrentValue;
}

internal sealed class MutableResolver(BridgeApiV1HistoryResolution current) :
    IBridgeApiV1HistoryItemResolver
{
    public BridgeApiV1HistoryResolution Current { get; set; } = current;
    public int ResolveCalls { get; private set; }
    public BridgeApiV1HistoryResolution Resolve(string? itemId)
    {
        ResolveCalls++;
        return Current;
    }
}

internal sealed class FakeStarter : IBridgeHistoryActivationStarter
{
    private TaskCompletionSource<BridgeHistoryActivationResult> _completion = NewCompletion();
    public long OperationId { get; } = 42;
    public int ControllerStarts { get; private set; }
    public int TransferStarts { get; private set; }
    public int AcceptedExecutions { get; private set; }
    public int GuardCalls { get; private set; }
    public bool Throw { get; set; }
    public Action? BeforeGuard { get; set; }
    public BridgeHistoryActivationStartDisposition NextDisposition { get; set; } =
        BridgeHistoryActivationStartDisposition.Accepted;
    public BridgeHistoryActivationStart? TransferImmediate { get; set; }

    public BridgeHistoryActivationStart BeginControllerPak(string path,
        IProgress<BridgeHistoryActivationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<bool>? admissionGuard = null)
    {
        ControllerStarts++;
        return Start(admissionGuard);
    }

    public Task<BridgeHistoryActivationStart> BeginTransferPakAsync(string romPath,
        bool destructiveInstallConfirmed,
        IProgress<BridgeHistoryActivationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<bool>? admissionGuard = null)
    {
        TransferStarts++;
        if (Throw) throw new InvalidOperationException(@"D:\secret\private.gb");
        if (TransferImmediate is not null)
            return Task.FromResult(TransferImmediate);
        return Task.FromResult(Start(admissionGuard));
    }

    private BridgeHistoryActivationStart Start(Func<bool>? admissionGuard)
    {
        if (Throw) throw new InvalidOperationException(@"D:\secret\private.mpk");
        if (NextDisposition == BridgeHistoryActivationStartDisposition.Busy)
            return new(NextDisposition, null, null,
                new(BridgeHistoryActivationFailure.Busy, null),
                new(77, BridgeOperationKind.FirmwareModeSwitch));
        BeforeGuard?.Invoke();
        GuardCalls++;
        if (admissionGuard?.Invoke() == false)
            return new(BridgeHistoryActivationStartDisposition.AdmissionGuardRejected,
                null, null, null, new(0, BridgeOperationKind.None));
        AcceptedExecutions++;
        return new(BridgeHistoryActivationStartDisposition.Accepted,
            OperationId, _completion.Task, null,
            new(OperationId, BridgeOperationKind.HistoryActivation));
    }

    public void Complete() => _completion.TrySetResult(
        new(BridgeHistoryActivationFailure.None, null));

    public void Reset()
    {
        _completion = NewCompletion();
        ControllerStarts = 0;
        TransferStarts = 0;
        AcceptedExecutions = 0;
        GuardCalls = 0;
        Throw = false;
        BeforeGuard = null;
        NextDisposition = BridgeHistoryActivationStartDisposition.Accepted;
        TransferImmediate = null;
    }

    private static TaskCompletionSource<BridgeHistoryActivationResult> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal enum BridgeHistoryCategory { ControllerPak, TransferPak }
internal enum BridgeApiV1HistoryResolutionStatus
{
    Resolved, MalformedItemId, NotFound, Ambiguous, UnsafeAuthority
}
internal sealed record BridgeApiV1ResolvedHistoryItem(
    BridgeHistoryCategory Category, string Path, string FileName, bool Available);
internal sealed record BridgeApiV1HistoryResolution(
    BridgeApiV1HistoryResolutionStatus Status, BridgeApiV1ResolvedHistoryItem? Item);
internal interface IBridgeApiV1HistoryItemResolver
{
    BridgeApiV1HistoryResolution Resolve(string? itemId);
}

internal enum BridgeSessionState { Connected, TemporarilyUnavailable }
internal sealed record BridgeSessionSnapshot(string SessionId, BridgeSessionState State);
internal interface IBridgeSessionProvider { BridgeSessionSnapshot? Current { get; } }

internal enum BridgeHistoryActivationStartDisposition
{
    Accepted, Immediate, Busy, AdmissionGuardRejected
}
internal sealed record BridgeHistoryActivationStart(
    BridgeHistoryActivationStartDisposition Disposition, long? OperationId,
    Task<BridgeHistoryActivationResult>? Completion,
    BridgeHistoryActivationResult? ImmediateResult, BridgeOperationSnapshot Operation)
{
    public bool Accepted => Disposition == BridgeHistoryActivationStartDisposition.Accepted;
}
internal interface IBridgeHistoryActivationStarter
{
    BridgeHistoryActivationStart BeginControllerPak(string path,
        IProgress<BridgeHistoryActivationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<bool>? admissionGuard = null);
    Task<BridgeHistoryActivationStart> BeginTransferPakAsync(string romPath,
        bool destructiveInstallConfirmed,
        IProgress<BridgeHistoryActivationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<bool>? admissionGuard = null);
}
internal sealed record BridgeHistoryActivationProgress;
internal enum BridgeHistoryActivationFailure
{
    None, Busy, Cancelled, InvalidPath, NormalModeRequired, MissingFile,
    ConfirmationRequired, InspectionFailure, AccessoryTransitionFailure,
    ContentFailure, UnexpectedFailure
}
internal sealed record BridgeHistoryActivationResult(
    BridgeHistoryActivationFailure Failure,
    BridgeTransferPakRomInspection? Inspection);
internal sealed record BridgeTransferPakRomInspection(
    string RomPath, GameBoyRomMetadata Metadata);
internal sealed record GameBoyRomMetadata(string Title);

internal enum BridgeOperationKind
{
    None, Connect, Disconnect, FirmwareQuery, FirmwareModeSwitch,
    AccessorySelection, ControllerPakCreate, ControllerPakLoad,
    ControllerPakSave, TransferPakInstall, TransferPakSave,
    TransferPakLegacyLoad, WifiConfigureAndConnect, WifiConnectRetained,
    WifiDisconnect, WifiForget, NetworkPakModuleSelection,
    NetworkPakCarrierSelection, HistoryActivation
}
internal sealed record BridgeOperationSnapshot(long OperationId, BridgeOperationKind Kind);
