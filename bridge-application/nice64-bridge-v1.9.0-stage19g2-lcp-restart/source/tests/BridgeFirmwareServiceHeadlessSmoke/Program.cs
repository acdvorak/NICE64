using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static FirmwareModeInfo Mode(FirmwarePersonality personality) => personality switch
{
    FirmwarePersonality.Controller => new(personality, "CONTROLLER", "NONE"),
    FirmwarePersonality.Diagnostics => new(personality, "STANDALONE", "DIAGNOSTICS"),
    FirmwarePersonality.Network => new(personality, "STANDALONE", "NETWORK"),
    _ => new(personality, string.Empty, string.Empty)
};

static void Connect(BridgeDeviceStateService state, FirmwarePersonality mode) =>
    ((IBridgeDeviceStateUpdater)state).Connect(
        BridgeControlConnectionKind.SerialUsb, "NICE64", "test", Mode(mode));

static SerialFirmwareModeControlPath SerialPath(
    Func<CancellationToken, Task<FirmwareModeInfo>> query,
    Func<FirmwarePersonality, CancellationToken, Task<FirmwareModeRequestResult>> request,
    Func<FirmwarePersonality, CancellationToken, Task<FirmwareModeInfo>> reconnect) =>
    new(() => true, query, request, reconnect);

static TcpFirmwareModeControlPath TcpPath(
    Func<CancellationToken, Task<FirmwareModeInfo>> query,
    Func<FirmwarePersonality, CancellationToken, Task<FirmwareModeRequestResult>> request,
    Func<FirmwarePersonality, CancellationToken, Task<FirmwareModeInfo>> reconnect) =>
    new(() => true, query, request, reconnect);

static async Task TestAlreadyActiveAsync()
{
    BridgeDeviceStateService state = new();
    Connect(state, FirmwarePersonality.Controller);
    await using AccessoryPersistenceService persistence = new(debounce: TimeSpan.FromDays(1));
    int requests = 0;
    IFirmwareModeControlPath path = SerialPath(
        _ => Task.FromResult(Mode(FirmwarePersonality.Controller)),
        (_, _) => { requests++; return Task.FromResult(new FirmwareModeRequestResult(
            FirmwareModeRequestKind.Ack, null)); },
        (target, _) => Task.FromResult(Mode(target)));
    IBridgeFirmwareService service = new BridgeFirmwareService(state, persistence, () => path);

    FirmwareModeTransitionResult result = await service.SelectModeAsync(
        FirmwarePersonality.Controller);
    Require(result.Success && !result.RequestSent && requests == 0,
        "already-active mode sent a request");
    Require(state.Current.FirmwareTransition.Phase == BridgeFirmwareTransitionPhase.Completed,
        "already-active mode did not publish completion");
}

static async Task TestPersistenceAndAcknowledgedTransitionAsync()
{
    List<string> order = [];
    BridgeDeviceStateService state = new();
    Connect(state, FirmwarePersonality.Controller);
    RecordingWriter writer = new(order);
    List<string> persistenceLog = [];
    await using AccessoryPersistenceService persistence = new(
        writer, TimeSpan.FromDays(1), persistenceLog.Add);
    string pathName = Path.Combine(Path.GetTempPath(), $"nice64-fw-{Guid.NewGuid():N}.sav");
    persistence.Activate(AccessoryPersistenceKind.GameBoySave, pathName, 4,
        _ => Task.FromResult(new byte[] { 1, 2, 3, 4 }), 0);
    IFirmwareModeControlPath path = SerialPath(
        _ => Task.FromResult(Mode(FirmwarePersonality.Controller)),
        (_, _) => { order.Add("request"); return Task.FromResult(new FirmwareModeRequestResult(
            FirmwareModeRequestKind.Ack, null)); },
        (target, _) => { order.Add("reconnect"); return Task.FromResult(Mode(target)); });
    IBridgeFirmwareService service = new BridgeFirmwareService(state, persistence, () => path);
    List<BridgeFirmwareTransitionPhase> phases = [];
    state.StateChanged += (_, args) => phases.Add(args.Current.FirmwareTransition.Phase);

    FirmwareModeTransitionResult result = await service.SelectModeAsync(
        FirmwarePersonality.Network);
    Require(result.Success && result.RequestSent && result.Reconnected,
        "acknowledged transition failed");
    Require(order.SequenceEqual(new[] { "flush", "request", "reconnect" }),
        "persistence did not precede request/reconnect");
    Require(persistenceLog.SequenceEqual(new[]
    {
        "TRANSFER SAVE FLUSH START reason=firmware-change",
        "TRANSFER SAVE FLUSH SUCCESS bytes=4 reason=firmware-change"
    }), "firmware transition did not force/log a clean associated Game Boy save flush");
    Require(phases.Contains(BridgeFirmwareTransitionPhase.RebootExpected) &&
            phases.Contains(BridgeFirmwareTransitionPhase.Reconnecting),
        "expected reboot/reconnect state was not published");
    Require(state.Current.FirmwarePersonality == FirmwarePersonality.Network &&
            state.Current.FirmwareTransition.Phase == BridgeFirmwareTransitionPhase.Completed,
        "authoritative state did not contain the verified mode");
    try { File.Delete(pathName); } catch { }
}

static async Task TestPersistenceFailureBlocksRequestAsync()
{
    BridgeDeviceStateService state = new();
    Connect(state, FirmwarePersonality.Controller);
    await using AccessoryPersistenceService persistence =
        new(new FailingWriter(), TimeSpan.FromDays(1));
    string pathName = Path.Combine(Path.GetTempPath(), $"nice64-fw-{Guid.NewGuid():N}.mpk");
    persistence.Activate(AccessoryPersistenceKind.ControllerPak, pathName, 4,
        _ => Task.FromResult(new byte[] { 1, 2, 3, 4 }), 0);
    persistence.MarkDirty();
    int requests = 0;
    IFirmwareModeControlPath path = SerialPath(
        _ => Task.FromResult(Mode(FirmwarePersonality.Controller)),
        (_, _) => { requests++; return Task.FromResult(new FirmwareModeRequestResult(
            FirmwareModeRequestKind.Ack, null)); },
        (target, _) => Task.FromResult(Mode(target)));
    IBridgeFirmwareService service = new BridgeFirmwareService(state, persistence, () => path);

    FirmwareModeTransitionResult result = await service.SelectModeAsync(
        FirmwarePersonality.Network);
    Require(!result.Success && result.Failure == FirmwareModeTransitionFailure.Persistence,
        "persistence failure was not typed");
    Require(requests == 0, "persistence failure did not block REQUEST_MODE");
    Require(persistence.Current.Dirty, "persistence failure cleared dirty state");
}

static async Task TestSerialLostAckAsync()
{
    BridgeDeviceStateService state = new();
    Connect(state, FirmwarePersonality.Controller);
    await using AccessoryPersistenceService persistence = new(debounce: TimeSpan.FromDays(1));
    int requests = 0;
    int reconnects = 0;
    IFirmwareModeControlPath path = SerialPath(
        _ => Task.FromResult(Mode(FirmwarePersonality.Controller)),
        (_, _) => Task.FromResult(new FirmwareModeRequestResult(
            Interlocked.Increment(ref requests) == 1
                ? FirmwareModeRequestKind.Unknown
                : FirmwareModeRequestKind.Ack, null)),
        (target, _) => Task.FromResult(Mode(
            Interlocked.Increment(ref reconnects) == 1
                ? FirmwarePersonality.Controller
                : target)));
    IBridgeFirmwareService service = new BridgeFirmwareService(state, persistence, () => path);

    FirmwareModeTransitionResult result = await service.SelectModeAsync(
        FirmwarePersonality.Diagnostics);
    Require(result.Success && requests == 2 && reconnects == 2,
        "serial lost-ACK did not preserve reconnect/old-mode retry semantics");
}

static async Task TestTcpSocketLossAsync()
{
    BridgeDeviceStateService state = new();
    Connect(state, FirmwarePersonality.Controller);
    await using AccessoryPersistenceService persistence = new(debounce: TimeSpan.FromDays(1));
    bool prepared = false;
    bool graceObserved = false;
    bool reconnected = false;
    IFirmwareModeControlPath path = new TcpFirmwareModeControlPath(
        () => true,
        _ => Task.FromResult(Mode(FirmwarePersonality.Controller)),
        (_, _) => throw new IOException("expected reboot socket loss"),
        _ => { prepared = true; return Task.CompletedTask; },
        (_, _) => { reconnected = true; return Task.FromResult(Mode(FirmwarePersonality.Network)); },
        () => throw new InvalidOperationException("successful reconnect retired the fresh session"),
        _ => { graceObserved = true; return Task.CompletedTask; },
        (attempt, token) => attempt(1, token));
    IBridgeFirmwareService service = new BridgeFirmwareService(state, persistence, () => path);

    FirmwareModeTransitionResult result = await service.SelectModeAsync(
        FirmwarePersonality.Network);
    Require(result.Success && prepared && graceObserved && reconnected,
        "TCP expected socket loss did not preserve prepare/grace/fresh-session verification");
}

static async Task TestReconnectFailuresAsync()
{
    BridgeDeviceStateService timeoutState = new();
    Connect(timeoutState, FirmwarePersonality.Controller);
    await using AccessoryPersistenceService timeoutPersistence =
        new(debounce: TimeSpan.FromDays(1));
    IFirmwareModeControlPath timeoutPath = TcpPath(
        _ => Task.FromResult(Mode(FirmwarePersonality.Controller)),
        (_, _) => Task.FromResult(new FirmwareModeRequestResult(FirmwareModeRequestKind.Ack, null)),
        (_, _) => throw new TimeoutException("bounded reconnect expired"));
    FirmwareModeTransitionResult timeout = await new BridgeFirmwareService(
        timeoutState, timeoutPersistence, () => timeoutPath).SelectModeAsync(
            FirmwarePersonality.Network);
    Require(!timeout.Success && timeout.Failure == FirmwareModeTransitionFailure.Reconnect,
        "reconnect timeout was not classified");

    BridgeDeviceStateService wrongState = new();
    Connect(wrongState, FirmwarePersonality.Controller);
    await using AccessoryPersistenceService wrongPersistence =
        new(debounce: TimeSpan.FromDays(1));
    IFirmwareModeControlPath wrongPath = SerialPath(
        _ => Task.FromResult(Mode(FirmwarePersonality.Controller)),
        (_, _) => Task.FromResult(new FirmwareModeRequestResult(FirmwareModeRequestKind.Ack, null)),
        (_, _) => Task.FromResult(Mode(FirmwarePersonality.Diagnostics)));
    FirmwareModeTransitionResult wrong = await new BridgeFirmwareService(
        wrongState, wrongPersistence, () => wrongPath).SelectModeAsync(
            FirmwarePersonality.Network);
    Require(!wrong.Success && wrong.Failure == FirmwareModeTransitionFailure.Verification,
        "wrong returned mode was not classified");
}

static async Task TestCancellationAsync()
{
    BridgeDeviceStateService state = new();
    Connect(state, FirmwarePersonality.Controller);
    await using AccessoryPersistenceService persistence = new(debounce: TimeSpan.FromDays(1));
    IFirmwareModeControlPath path = SerialPath(
        token => Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith(
            _ => Mode(FirmwarePersonality.Controller), token),
        (_, _) => Task.FromResult(new FirmwareModeRequestResult(FirmwareModeRequestKind.Ack, null)),
        (target, _) => Task.FromResult(Mode(target)));
    IBridgeFirmwareService service = new BridgeFirmwareService(state, persistence, () => path);
    using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(25));

    FirmwareModeTransitionResult result = await service.SelectModeAsync(
        FirmwarePersonality.Network, cancellation.Token);
    Require(!result.Success && result.Failure == FirmwareModeTransitionFailure.Cancelled &&
            state.Current.FirmwareTransition.Phase == BridgeFirmwareTransitionPhase.Failed,
        "cancellation did not return typed authoritative failure");

    using CancellationTokenSource alreadyCancelled = new();
    alreadyCancelled.Cancel();
    BridgeFirmwareTransitionSnapshot stateBeforePreCancelled =
        state.Current.FirmwareTransition;
    FirmwareModeTransitionResult preCancelled = await service.SelectModeAsync(
        FirmwarePersonality.Network, alreadyCancelled.Token);
    Require(!preCancelled.Success &&
            preCancelled.Failure == FirmwareModeTransitionFailure.Cancelled &&
            state.Current.FirmwareTransition == stateBeforePreCancelled,
        "pre-cancelled transition did not return typed failure without state mutation");
}

static async Task TestRejectedRequestsDoNotStealActiveStateAsync()
{
    BridgeDeviceStateService state = new();
    Connect(state, FirmwarePersonality.Controller);
    await using AccessoryPersistenceService persistence = new(debounce: TimeSpan.FromDays(1));
    TaskCompletionSource requestEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource releaseRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
    IFirmwareModeControlPath activePath = SerialPath(
        _ => Task.FromResult(Mode(FirmwarePersonality.Controller)),
        async (_, token) =>
        {
            requestEntered.SetResult();
            await releaseRequest.Task.WaitAsync(token);
            return new FirmwareModeRequestResult(FirmwareModeRequestKind.Ack, null);
        },
        (target, _) => Task.FromResult(Mode(target)));
    IFirmwareModeControlPath unavailablePath = new SerialFirmwareModeControlPath(
        () => false,
        _ => throw new InvalidOperationException("unavailable path queried"),
        (_, _) => throw new InvalidOperationException("unavailable path requested"),
        (_, _) => throw new InvalidOperationException("unavailable path reconnected"));
    bool selectUnavailable = false;
    IBridgeFirmwareService service = new BridgeFirmwareService(
        state, persistence, () => selectUnavailable ? unavailablePath : activePath);

    Task<FirmwareModeTransitionResult> active = service.SelectModeAsync(
        FirmwarePersonality.Network);
    await requestEntered.Task;
    BridgeFirmwareTransitionSnapshot owned = state.Current.FirmwareTransition;
    Require(owned is { Phase: BridgeFirmwareTransitionPhase.TransitionPending,
                       Target: FirmwarePersonality.Network },
        "valid transition did not own authoritative state while blocked");

    FirmwareModeTransitionResult invalid = await service.SelectModeAsync(
        FirmwarePersonality.Unknown);
    Require(!invalid.Success &&
            invalid.Failure == FirmwareModeTransitionFailure.InvalidTarget &&
            state.Current.FirmwareTransition == owned,
        "invalid request overwrote active transition state");

    using CancellationTokenSource preCancelled = new();
    preCancelled.Cancel();
    FirmwareModeTransitionResult cancelled = await service.SelectModeAsync(
        FirmwarePersonality.Diagnostics, preCancelled.Token);
    Require(!cancelled.Success &&
            cancelled.Failure == FirmwareModeTransitionFailure.Cancelled &&
            state.Current.FirmwareTransition == owned,
        "pre-cancelled request overwrote active transition state");

    selectUnavailable = true;
    FirmwareModeTransitionResult unavailable = await service.SelectModeAsync(
        FirmwarePersonality.Diagnostics);
    Require(!unavailable.Success &&
            unavailable.Failure == FirmwareModeTransitionFailure.ControlPathUnavailable &&
            state.Current.FirmwareTransition == owned,
        "unavailable-path request overwrote active transition state");

    releaseRequest.SetResult();
    FirmwareModeTransitionResult completed = await active;
    Require(completed.Success &&
            state.Current.FirmwareTransition is
            { Phase: BridgeFirmwareTransitionPhase.Completed,
              Target: FirmwarePersonality.Network },
        "active transition did not complete after rejected overlapping requests");
}

static async Task TestRejectedRequestsWithoutActiveTransitionAsync()
{
    BridgeDeviceStateService state = new();
    Connect(state, FirmwarePersonality.Controller);
    await using AccessoryPersistenceService persistence = new(debounce: TimeSpan.FromDays(1));
    IFirmwareModeControlPath unavailablePath = new SerialFirmwareModeControlPath(
        () => false,
        _ => throw new InvalidOperationException(),
        (_, _) => throw new InvalidOperationException(),
        (_, _) => throw new InvalidOperationException());
    IBridgeFirmwareService service = new BridgeFirmwareService(
        state, persistence, () => unavailablePath);
    BridgeFirmwareTransitionSnapshot idle = state.Current.FirmwareTransition;

    FirmwareModeTransitionResult invalid = await service.SelectModeAsync(
        FirmwarePersonality.Unknown);
    using CancellationTokenSource cancelledToken = new();
    cancelledToken.Cancel();
    FirmwareModeTransitionResult cancelled = await service.SelectModeAsync(
        FirmwarePersonality.Network, cancelledToken.Token);
    FirmwareModeTransitionResult unavailable = await service.SelectModeAsync(
        FirmwarePersonality.Network);

    Require(invalid.Failure == FirmwareModeTransitionFailure.InvalidTarget &&
            cancelled.Failure == FirmwareModeTransitionFailure.Cancelled &&
            unavailable.Failure == FirmwareModeTransitionFailure.ControlPathUnavailable,
        "preflight requests did not retain typed results without an active transition");
    Require(state.Current.FirmwareTransition == idle,
        "preflight rejection mutated idle authoritative transition state");
}

static async Task TestStageAwareFailureClassificationAsync()
{
    FirmwareModeTransitionRequest Request(
        Func<CancellationToken, Task<FirmwareModeInfo>> query,
        Func<CancellationToken, Task<AccessoryPersistenceResult>>? flush = null,
        Func<FirmwarePersonality, CancellationToken,
            Task<FirmwareModeRequestResult>>? send = null,
        Func<FirmwarePersonality, CancellationToken,
            Task<FirmwareModeInfo>>? reconnect = null) => new(
                FirmwarePersonality.Network,
                flush ?? (_ => Task.FromResult(AccessoryPersistenceResult.Clean)),
                query,
                send ?? ((_, _) => Task.FromResult(new FirmwareModeRequestResult(
                    FirmwareModeRequestKind.Ack, null))),
                reconnect ?? ((target, _) => Task.FromResult(Mode(target))));

    BridgeDeviceStateService queryState = new();
    Connect(queryState, FirmwarePersonality.Controller);
    FirmwareModeTransitionResult queryFailure = await new FirmwareModeTransitionService(
        queryState).ExecuteAsync(Request(_ => Task.FromException<FirmwareModeInfo>(
            new IOException("query failure"))));
    Require(queryFailure.Failure == FirmwareModeTransitionFailure.QueryCurrent,
        "QueryCurrent exception classification was not truthful");

    BridgeDeviceStateService flushState = new();
    Connect(flushState, FirmwarePersonality.Controller);
    FirmwareModeTransitionResult flushFailure = await new FirmwareModeTransitionService(
        flushState).ExecuteAsync(Request(
            _ => Task.FromResult(Mode(FirmwarePersonality.Controller)),
            _ => Task.FromException<AccessoryPersistenceResult>(
                new IOException("flush failure"))));
    Require(flushFailure.Failure == FirmwareModeTransitionFailure.Persistence,
        "FlushPersistence exception classification was not truthful");

    BridgeDeviceStateService requestState = new();
    Connect(requestState, FirmwarePersonality.Controller);
    FirmwareModeTransitionResult requestFailure = await new FirmwareModeTransitionService(
        requestState).ExecuteAsync(Request(
            _ => Task.FromResult(Mode(FirmwarePersonality.Controller)),
            send: (_, _) => Task.FromException<FirmwareModeRequestResult>(
                new IOException("request failure"))));
    Require(requestFailure.Failure == FirmwareModeTransitionFailure.Request,
        "SendRequest exception classification was not truthful");

    BridgeDeviceStateService alreadyState = new();
    Connect(alreadyState, FirmwarePersonality.Controller);
    int queryCount = 0;
    FirmwareModeTransitionResult alreadyFailure = await new FirmwareModeTransitionService(
        alreadyState).ExecuteAsync(Request(
            _ => Interlocked.Increment(ref queryCount) == 1
                ? Task.FromResult(Mode(FirmwarePersonality.Controller))
                : Task.FromException<FirmwareModeInfo>(
                    new IOException("already-active verification failure")),
            send: (_, _) => Task.FromResult(new FirmwareModeRequestResult(
                FirmwareModeRequestKind.AlreadyActive, null))));
    Require(alreadyFailure.Failure == FirmwareModeTransitionFailure.Verification,
        "AlreadyActive verification-query exception was mislabeled");

    BridgeDeviceStateService reconnectState = new();
    Connect(reconnectState, FirmwarePersonality.Controller);
    FirmwareModeTransitionResult reconnectFailure = await new FirmwareModeTransitionService(
        reconnectState).ExecuteAsync(Request(
            _ => Task.FromResult(Mode(FirmwarePersonality.Controller)),
            reconnect: (_, _) => Task.FromException<FirmwareModeInfo>(
                new TimeoutException("reconnect failure"))));
    Require(reconnectFailure.Failure == FirmwareModeTransitionFailure.Reconnect,
        "ReconnectAndVerify exception classification was not truthful");
}

static async Task TestConcurrentSerializationAsync()
{
    BridgeDeviceStateService state = new();
    Connect(state, FirmwarePersonality.Controller);
    await using AccessoryPersistenceService persistence = new(debounce: TimeSpan.FromDays(1));
    TaskCompletionSource firstRequestEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int queries = 0;
    int requests = 0;
    IFirmwareModeControlPath path = SerialPath(
        _ => { Interlocked.Increment(ref queries); return Task.FromResult(Mode(FirmwarePersonality.Controller)); },
        async (_, token) =>
        {
            int number = Interlocked.Increment(ref requests);
            if (number == 1)
            {
                firstRequestEntered.SetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
            return new FirmwareModeRequestResult(FirmwareModeRequestKind.Ack, null);
        },
        (target, _) => Task.FromResult(Mode(target)));
    IBridgeFirmwareService service = new BridgeFirmwareService(state, persistence, () => path);

    Task<FirmwareModeTransitionResult> first = service.SelectModeAsync(FirmwarePersonality.Network);
    await firstRequestEntered.Task;
    Task<FirmwareModeTransitionResult> second = service.SelectModeAsync(FirmwarePersonality.Diagnostics);
    await Task.Delay(25);
    Require(Volatile.Read(ref queries) == 1,
        "concurrent transition entered before the existing serialization gate released");
    releaseFirst.SetResult();
    FirmwareModeTransitionResult[] results = await Task.WhenAll(first, second);
    Require(results.All(result => result.Success) && queries == 2,
        "serialized transitions did not both complete");
}

await TestAlreadyActiveAsync();
await TestPersistenceAndAcknowledgedTransitionAsync();
await TestPersistenceFailureBlocksRequestAsync();
await TestSerialLostAckAsync();
await TestTcpSocketLossAsync();
await TestReconnectFailuresAsync();
await TestCancellationAsync();
await TestRejectedRequestsDoNotStealActiveStateAsync();
await TestRejectedRequestsWithoutActiveTransitionAsync();
await TestStageAwareFailureClassificationAsync();
await TestConcurrentSerializationAsync();
Console.WriteLine("PASS Bridge firmware service headless semantics");

sealed class RecordingWriter(List<string> order) : IAtomicPersistenceWriter
{
    public Task WriteVerifiedAsync(string path, byte[] data, int expectedLength,
        CancellationToken cancellationToken)
    {
        if (data.Length != expectedLength)
            throw new InvalidOperationException("recording writer length mismatch");
        order.Add("flush");
        return Task.CompletedTask;
    }
}

sealed class FailingWriter : IAtomicPersistenceWriter
{
    public Task WriteVerifiedAsync(string path, byte[] data, int expectedLength,
        CancellationToken cancellationToken) =>
        throw new IOException("injected persistence failure");
}
