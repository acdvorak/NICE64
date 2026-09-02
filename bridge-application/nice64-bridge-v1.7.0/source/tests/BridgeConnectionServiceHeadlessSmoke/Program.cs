using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static FirmwareModeInfo Mode(FirmwarePersonality personality) =>
    new(personality,
        personality == FirmwarePersonality.Controller ? "CONTROLLER" : "STANDALONE",
        personality == FirmwarePersonality.Network ? "NETWORK" : "NONE");

static BridgeCapabilitySet Capabilities() => new(
    BridgeCapabilityProvenance.DeviceReported,
    [new(BridgeCapabilityId.BulkRead, true,
        BridgeCapabilityProvenance.DeviceReported, 65536, 65536)]);

static BridgeControlSession SerialSession(string endpoint = "COM7",
    FirmwareModeInfo? mode = null) =>
    new(BridgeControlConnectionKind.SerialUsb, endpoint, "NICE64", "test",
        mode, BridgeNetworkPakTransport.UsbSerial, Capabilities());

static BridgeControlSession TcpSession(
    FirmwarePersonality personality = FirmwarePersonality.Network) =>
    new(BridgeControlConnectionKind.WifiTcp, "NICE64 192.0.2.7:6464",
        "NICE64", "test", Mode(personality), BridgeNetworkPakTransport.Tcp,
        Capabilities());

static TestRig Rig()
{
    BridgeDeviceStateService state = new();
    FakePersistence persistence = new();
    FakeSerialAdapter serial = new();
    FakeTcpAdapter tcp = new();
    FakeCleanupAdapter cleanup = new();
    BridgeConnectionService service = new(state, persistence, serial, tcp, cleanup);
    return new(state, persistence, serial, tcp, cleanup, service);
}

static void ConnectState(TestRig rig,
    BridgeControlConnectionKind kind = BridgeControlConnectionKind.SerialUsb,
    FirmwarePersonality personality = FirmwarePersonality.Controller,
    BridgeAccessoryMode accessory = BridgeAccessoryMode.None)
{
    IBridgeDeviceStateUpdater updater = rig.State;
    updater.Connect(kind, "NICE64", "test", Mode(personality), Capabilities());
    updater.SetActiveAccessory(accessory);
}

static async Task TestSuccessfulGenuineConnectionsAsync()
{
    TestRig serial = Rig();
    BridgeConnectionResult serialResult = await serial.Service.ConnectAsync(
        new(BridgeControlConnectionKind.SerialUsb, "COM7"));
    Require(serialResult.Success &&
        serial.State.Current.ControlConnectionKind == BridgeControlConnectionKind.SerialUsb &&
        serial.State.Current.ControlConnectionState == BridgeConnectionState.Connected &&
        serial.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.UsbSerial,
        "Genuine serial connection did not apply authoritative state");

    TestRig tcp = Rig();
    BridgeConnectionResult tcpResult = await tcp.Service.ConnectAsync(
        new(BridgeControlConnectionKind.WifiTcp, "192.0.2.7", 6464,
            TimeSpan.FromSeconds(5)));
    Require(tcpResult.Success &&
        tcpResult.FirmwarePersonality == FirmwarePersonality.Network &&
        tcp.State.Current.ControlConnectionKind == BridgeControlConnectionKind.WifiTcp &&
        tcp.State.Current.DeviceReportedCapabilities.Supports(BridgeCapabilityId.BulkRead),
        "Genuine TCP connection did not apply its verified handshake");
}

static async Task TestConnectFailuresAsync()
{
    TestRig serial = Rig();
    serial.Serial.ConnectHandler = (_, _) =>
        Task.FromException<BridgeControlSession>(new IOException("open failed"));
    BridgeConnectionResult serialFailed = await serial.Service.ConnectAsync(
        new(BridgeControlConnectionKind.SerialUsb, "COM9"));
    Require(serialFailed.Failure == BridgeConnectionFailure.OpenFailed &&
        serial.State.Current.ControlConnectionState == BridgeConnectionState.Disconnected,
        "Serial open failure classification/state was not truthful");

    TestRig invalidTcp = Rig();
    invalidTcp.Tcp.ConnectHandler = (_, _, _, _) =>
        Task.FromException<BridgeControlSession>(new ArgumentException("invalid IPv4"));
    BridgeConnectionResult invalid = await invalidTcp.Service.ConnectAsync(
        new(BridgeControlConnectionKind.WifiTcp, "bad", 6464));
    Require(invalid.Failure == BridgeConnectionFailure.InvalidEndpoint,
        "TCP endpoint validation failure was not typed");

    TestRig handshake = Rig();
    handshake.Tcp.ConnectHandler = (_, _, _, _) =>
        Task.FromException<BridgeControlSession>(new IOException("WHO mismatch"));
    BridgeConnectionResult handshakeFailed = await handshake.Service.ConnectAsync(
        new(BridgeControlConnectionKind.WifiTcp, "192.0.2.7", 6464));
    Require(handshakeFailed.Failure == BridgeConnectionFailure.HandshakeFailed,
        "TCP handshake failure was not typed");

    TestRig timeout = Rig();
    timeout.Tcp.ConnectHandler = (_, _, _, _) =>
        Task.FromException<BridgeControlSession>(new TimeoutException("timed out"));
    BridgeConnectionResult timedOut = await timeout.Service.ConnectAsync(
        new(BridgeControlConnectionKind.WifiTcp, "192.0.2.7", 6464));
    Require(timedOut.Failure == BridgeConnectionFailure.Timeout,
        "TCP connection timeout was not typed");
}

static async Task TestFreshSemanticStateAsync()
{
    TestRig rig = Rig();
    IBridgeDeviceStateUpdater updater = rig.State;
    updater.Connect(BridgeControlConnectionKind.SerialUsb, "OLD", "old",
        Mode(FirmwarePersonality.Controller));
    updater.SetActiveAccessory(BridgeAccessoryMode.ControllerPak);
    updater.SetAccessoryTransition(new(BridgeAccessoryTransitionPhase.Failed,
        BridgeAccessoryMode.NetworkPak, "old"));
    updater.SetFirmwareTransition(new(BridgeFirmwareTransitionPhase.Failed,
        FirmwarePersonality.Network, "old"));
    updater.SetControlConnectionState(BridgeControlConnectionKind.SerialUsb,
        BridgeConnectionState.Disconnected);

    BridgeConnectionResult connected = await rig.Service.ConnectAsync(
        new(BridgeControlConnectionKind.SerialUsb, "COM7"));
    Require(connected.Success &&
        rig.State.Current.ActiveAccessory == BridgeAccessoryMode.None &&
        rig.State.Current.AccessoryTransition == BridgeAccessoryTransitionSnapshot.Idle &&
        rig.State.Current.FirmwareTransition == BridgeFirmwareTransitionSnapshot.Idle,
        "Genuine connection inherited stale semantic state");

    BridgeDisconnectResult disconnected = await rig.Service.DisconnectAsync(
        BridgeDisconnectReason.UserRequest);
    Require(disconnected.Success &&
        rig.State.Current.ControlConnectionState == BridgeConnectionState.Disconnected &&
        rig.State.Current.ActiveAccessory == BridgeAccessoryMode.None,
        "Genuine disconnect did not reset semantic state");
}

static async Task TestConnectionSerializationAsync()
{
    TestRig rig = Rig();
    TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    rig.Serial.ConnectHandler = async (_, _) =>
    {
        entered.SetResult();
        await release.Task;
        return SerialSession();
    };
    Task<BridgeConnectionResult> first = rig.Service.ConnectAsync(
        new(BridgeControlConnectionKind.SerialUsb, "COM7"));
    await entered.Task;
    Task<BridgeConnectionResult> second = rig.Service.ConnectAsync(
        new(BridgeControlConnectionKind.SerialUsb, "COM8"));
    await Task.Delay(30);
    Require(rig.Serial.ConnectCount == 1,
        "Concurrent connects were not serialized");
    release.SetResult();
    Require((await first).Success &&
        (await second).Failure == BridgeConnectionFailure.AlreadyConnected,
        "Concurrent connect conflict was not typed");

    TestRig overlap = Rig();
    TaskCompletionSource overlapEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource overlapRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    overlap.Serial.ConnectHandler = async (_, _) =>
    {
        overlapEntered.SetResult();
        await overlapRelease.Task;
        return SerialSession();
    };
    Task<BridgeConnectionResult> connecting = overlap.Service.ConnectAsync(
        new(BridgeControlConnectionKind.SerialUsb, "COM7"));
    await overlapEntered.Task;
    Task<BridgeDisconnectResult> disconnecting = overlap.Service.DisconnectAsync(
        BridgeDisconnectReason.UserRequest);
    overlapRelease.SetResult();
    Require((await connecting).Success && (await disconnecting).Success &&
        overlap.Cleanup.CallCount == 1,
        "Connect/disconnect overlap did not serialize the complete lifecycles");
}

static async Task TestDisconnectPersistenceAsync()
{
    TestRig clean = Rig();
    ConnectState(clean);
    BridgeDisconnectResult cleanResult = await clean.Service.DisconnectAsync(
        BridgeDisconnectReason.UserRequest);
    Require(cleanResult.Success && clean.Persistence.FlushCount == 1 &&
        clean.Cleanup.CallCount == 1 && clean.Persistence.LastFlushReason ==
            AccessoryPersistenceFlushReason.Disconnect,
        "Clean user disconnect did not use persistence and cleanup lifecycle");

    TestRig dirty = Rig();
    ConnectState(dirty, accessory: BridgeAccessoryMode.ControllerPak);
    dirty.Persistence.SetDirty();
    BridgeDisconnectResult dirtyResult = await dirty.Service.DisconnectAsync(
        BridgeDisconnectReason.UserRequest);
    Require(dirtyResult.Success && dirty.Persistence.FlushCount == 1 &&
        !dirty.Persistence.Current.Active,
        "Dirty persistence did not flush/deactivate on disconnect");

    TestRig blocked = Rig();
    ConnectState(blocked, accessory: BridgeAccessoryMode.ControllerPak);
    blocked.Persistence.SetDirty();
    blocked.Persistence.FlushResult = new(false, true, "disk full");
    BridgeDisconnectResult blockedResult = await blocked.Service.DisconnectAsync(
        BridgeDisconnectReason.UserRequest);
    Require(!blockedResult.Success &&
        blockedResult.Failure == BridgeDisconnectFailure.BlockedByPersistence &&
        blocked.State.Current.ControlConnectionState == BridgeConnectionState.Connected &&
        blocked.Persistence.Current is { Active: true, Dirty: true } &&
        blocked.Cleanup.CallCount == 0,
        "Failed user persistence flush did not block disconnect");

    TestRig shutdown = Rig();
    ConnectState(shutdown, accessory: BridgeAccessoryMode.ControllerPak);
    shutdown.Persistence.SetDirty();
    shutdown.Persistence.FlushResult = new(false, true, "disk full");
    BridgeDisconnectResult shutdownResult = await shutdown.Service.DisconnectAsync(
        BridgeDisconnectReason.ApplicationShutdown);
    Require(shutdownResult.Success &&
        shutdownResult.Failure ==
            BridgeDisconnectFailure.ShutdownContinuedWithPersistenceFailure &&
        shutdown.State.Current.ControlConnectionState == BridgeConnectionState.Disconnected &&
        shutdown.Persistence.Current is { Active: true, Dirty: true } &&
        shutdown.Cleanup.CallCount == 1 && shutdown.Persistence.LastFlushReason ==
            AccessoryPersistenceFlushReason.ApplicationClose,
        "Shutdown persistence failure did not remain explicit while allowing cleanup");
}

static async Task TestFirmwareReplacementContinuityAsync()
{
    foreach (BridgeControlConnectionKind kind in new[]
    {
        BridgeControlConnectionKind.SerialUsb,
        BridgeControlConnectionKind.WifiTcp
    })
    {
        TestRig rig = Rig();
        ConnectState(rig, kind, FirmwarePersonality.Controller,
            BridgeAccessoryMode.ControllerPak);
        IBridgeDeviceStateUpdater updater = rig.State;
        updater.SelectNetworkPakTransport(BridgeNetworkPakTransport.ReliableUdpQuick);
        updater.ActivateNetworkPakTransport(BridgeNetworkPakTransport.ReliableUdpQuick);
        updater.SetAccessoryTransition(new(
            BridgeAccessoryTransitionPhase.TransitionPending,
            BridgeAccessoryMode.NetworkPak, string.Empty));
        updater.SetFirmwareTransition(new(BridgeFirmwareTransitionPhase.Reconnecting,
            FirmwarePersonality.Network, string.Empty));
        rig.Persistence.SetDirty();
        IBridgeFirmwareConnectionReplacement replacement = rig.Service;

        if (kind == BridgeControlConnectionKind.WifiTcp)
        {
            await replacement.PrepareTcpFirmwareReplacementAsync(CancellationToken.None);
            Require(rig.Tcp.PrepareCount == 1,
                "TCP replacement did not retire the old control session");
            await replacement.ReplaceTcpControlSessionAsync(1, CancellationToken.None);
        }
        else
        {
            await replacement.ReplaceSerialControlSessionAsync(
                FirmwarePersonality.Network, CancellationToken.None);
        }

        BridgeDeviceStateSnapshot state = rig.State.Current;
        Require(state.ActiveAccessory == BridgeAccessoryMode.ControllerPak &&
            state.AccessoryTransition.Phase ==
                BridgeAccessoryTransitionPhase.TransitionPending &&
            state.FirmwareTransition.Phase == BridgeFirmwareTransitionPhase.Reconnecting &&
            state.SelectedNetworkPakTransport ==
                BridgeNetworkPakTransport.ReliableUdpQuick &&
            state.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None &&
            state.ApplicationNegotiatedCapabilities.Items.Count == 0 &&
            rig.Persistence.Current is { Active: true, Dirty: true },
            $"{kind} replacement violated semantic/session continuity boundaries");
    }
}

static void TestStaleTcpCleanupAsync()
{
    BridgeTransportSessionOwner<TrackedDisposable> owner = new();
    TrackedDisposable oldSession = new();
    owner.InstallFresh(oldSession);
    long retirement = owner.BeginTransitionRetirement();
    TrackedDisposable fresh = new();
    owner.InstallFresh(fresh);
    bool retired = owner.TryRetireObserved(oldSession, out _);
    Require(!retired && !fresh.Disposed && ReferenceEquals(owner.Current, fresh) &&
        !owner.IsStillRetired(retirement),
        "Stale TCP cleanup destroyed the fresh replacement");
}

static async Task TestAutoReconnectAsync()
{
    TestRig ordinary = Rig();
    ConnectState(ordinary, accessory: BridgeAccessoryMode.ControllerPak);
    BridgeTransportFaultDisposition disposition =
        ordinary.Service.ReportSerialControlLoss("COM7");
    Require(disposition == BridgeTransportFaultDisposition.AutoReconnectScheduled &&
        ordinary.Service.AutoReconnect is { Pending: true, AttemptCount: 0 } &&
        ordinary.State.Current.ControlConnectionState == BridgeConnectionState.Disconnected &&
        ordinary.State.Current.ActiveAccessory == BridgeAccessoryMode.None,
        "Ordinary serial fault did not enter fresh-session auto reconnect lifecycle");
    BridgeAutoReconnectAttemptResult success = await ordinary.Service.TryAutoReconnectAsync(
        ordinary.Service.AutoReconnect.NextAttemptUtc);
    Require(success.Attempted && success.Connection?.Success == true &&
        !ordinary.Service.AutoReconnect.Pending &&
        ordinary.State.Current.ControlConnectionState == BridgeConnectionState.Connected,
        "Automatic reconnect did not apply a successful genuine session");

    TestRig expected = Rig();
    ConnectState(expected, accessory: BridgeAccessoryMode.ControllerPak);
    IBridgeDeviceStateUpdater updater = expected.State;
    updater.SetAccessoryTransition(new(
        BridgeAccessoryTransitionPhase.TransitionPending,
        BridgeAccessoryMode.NetworkPak, string.Empty));
    updater.SetFirmwareTransition(new(BridgeFirmwareTransitionPhase.RebootExpected,
        FirmwarePersonality.Network, string.Empty));
    BridgeTransportFaultDisposition expectedDisposition =
        expected.Service.ReportSerialControlLoss("COM7");
    Require(expectedDisposition ==
            BridgeTransportFaultDisposition.ExpectedFirmwareReplacement &&
        !expected.Service.AutoReconnect.Pending &&
        expected.State.Current.ActiveAccessory == BridgeAccessoryMode.ControllerPak &&
        expected.State.Current.AccessoryTransition.Phase ==
            BridgeAccessoryTransitionPhase.TransitionPending,
        "Firmware reboot loss incorrectly entered ordinary auto reconnect");

    TestRig unavailable = Rig();
    ConnectState(unavailable);
    unavailable.Service.ReportSerialControlLoss("COM7");
    unavailable.Serial.ConnectHandler = (_, _) =>
        Task.FromException<BridgeControlSession>(
            new BridgeSerialPortUnavailableException("missing"));
    DateTimeOffset due = unavailable.Service.AutoReconnect.NextAttemptUtc;
    BridgeAutoReconnectAttemptResult missing =
        await unavailable.Service.TryAutoReconnectAsync(due);
    Require(!missing.Attempted && unavailable.Service.AutoReconnect.AttemptCount == 0 &&
        unavailable.Service.AutoReconnect.NextAttemptUtc == due.AddSeconds(1),
        "Missing COM port altered bounded retry-attempt semantics");

    unavailable.Serial.ConnectHandler = (_, _) =>
        Task.FromException<BridgeControlSession>(new IOException("open failed"));
    BridgeAutoReconnectAttemptResult failed = await unavailable.Service.TryAutoReconnectAsync(
        unavailable.Service.AutoReconnect.NextAttemptUtc);
    Require(failed.Attempted && unavailable.Service.AutoReconnect.AttemptCount == 1,
        "Failed auto reconnect did not record one bounded attempt");
}

static async Task TestCancellationAndCleanupAsync()
{
    TestRig preCancelled = Rig();
    using CancellationTokenSource cancelled = new();
    cancelled.Cancel();
    BridgeConnectionResult before = await preCancelled.Service.ConnectAsync(
        new(BridgeControlConnectionKind.SerialUsb, "COM7"), cancelled.Token);
    Require(before.Failure == BridgeConnectionFailure.Cancelled &&
        preCancelled.Serial.ConnectCount == 0,
        "Pre-ownership cancellation performed connection work");

    TestRig during = Rig();
    TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    during.Tcp.ConnectHandler = async (_, _, _, token) =>
    {
        entered.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return TcpSession();
    };
    using CancellationTokenSource duringCts = new();
    Task<BridgeConnectionResult> task = during.Service.ConnectAsync(
        new(BridgeControlConnectionKind.WifiTcp, "192.0.2.7", 6464),
        duringCts.Token);
    await entered.Task;
    duringCts.Cancel();
    BridgeConnectionResult duringResult = await task;
    Require(duringResult.Failure == BridgeConnectionFailure.Cancelled &&
        during.State.Current.ControlConnectionState == BridgeConnectionState.Disconnected,
        "Cancellation during TCP handshake left ambiguous state");

    TestRig cleanup = Rig();
    ConnectState(cleanup);
    BridgeDisconnectResult disconnected = await cleanup.Service.DisconnectAsync(
        BridgeDisconnectReason.UserRequest);
    Require(disconnected.Success && disconnected.NetworkPakCleanupInvoked &&
        disconnected.ControllerCleanupInvoked && cleanup.Cleanup.CallCount == 1,
        "Disconnect did not invoke Network Pak/controller cleanup seams");

    TestRig failedCleanup = Rig();
    ConnectState(failedCleanup);
    failedCleanup.Service.ReportSerialControlLoss("COM7");
    Require(failedCleanup.Service.AutoReconnect.Pending,
        "Cleanup-failure fixture did not establish pending auto reconnect state");
    ConnectState(failedCleanup);
    failedCleanup.Cleanup.Error = new IOException("cleanup aborted");
    BridgeDisconnectResult cleanupFailure = await failedCleanup.Service.DisconnectAsync(
        BridgeDisconnectReason.UserRequest);
    Require(!cleanupFailure.Success &&
        cleanupFailure.Failure == BridgeDisconnectFailure.CleanupFailure &&
        !cleanupFailure.NetworkPakCleanupInvoked &&
        !cleanupFailure.ControllerCleanupInvoked &&
        failedCleanup.State.Current.ControlConnectionState ==
            BridgeConnectionState.Disconnected &&
        !failedCleanup.Service.AutoReconnect.Pending &&
        failedCleanup.Persistence.DeactivateCount == 1,
        "Cleanup exception reported unproven stages or left state/reconnect ambiguous");
}

await TestSuccessfulGenuineConnectionsAsync();
await TestConnectFailuresAsync();
await TestFreshSemanticStateAsync();
await TestConnectionSerializationAsync();
await TestDisconnectPersistenceAsync();
await TestFirmwareReplacementContinuityAsync();
TestStaleTcpCleanupAsync();
await TestAutoReconnectAsync();
await TestCancellationAndCleanupAsync();
Console.WriteLine("PASS Bridge connection service headless semantics");

sealed record TestRig(
    BridgeDeviceStateService State,
    FakePersistence Persistence,
    FakeSerialAdapter Serial,
    FakeTcpAdapter Tcp,
    FakeCleanupAdapter Cleanup,
    BridgeConnectionService Service);

sealed class FakeSerialAdapter : IBridgeSerialConnectionAdapter
{
    public int ConnectCount { get; private set; }
    public Func<string, CancellationToken, Task<BridgeControlSession>>? ConnectHandler { get; set; }
    public Func<string?, FirmwarePersonality, CancellationToken,
        Task<BridgeControlSession>>? ReplaceHandler { get; set; }

    public Task<BridgeControlSession> ConnectAsync(
        string portName, CancellationToken cancellationToken)
    {
        ConnectCount++;
        return ConnectHandler?.Invoke(portName, cancellationToken) ??
            Task.FromResult(SerialSession(portName,
                selected: BridgeNetworkPakTransport.UsbSerial));
    }

    public Task<BridgeControlSession> ReplaceAfterFirmwareRebootAsync(
        string? preferredPort, FirmwarePersonality target,
        CancellationToken cancellationToken) =>
        ReplaceHandler?.Invoke(preferredPort, target, cancellationToken) ??
        Task.FromResult(SerialSession(preferredPort ?? "COM7", Mode(target),
            BridgeNetworkPakTransport.ReliableUdpQuick));

    private static BridgeControlSession SerialSession(string endpoint,
        FirmwareModeInfo? mode = null,
        BridgeNetworkPakTransport selected = BridgeNetworkPakTransport.UsbSerial) =>
        new(BridgeControlConnectionKind.SerialUsb, endpoint, "NICE64", "test",
            mode, selected, Capabilities());

    private static FirmwareModeInfo Mode(FirmwarePersonality personality) =>
        new(personality, personality == FirmwarePersonality.Controller
            ? "CONTROLLER" : "STANDALONE",
            personality == FirmwarePersonality.Network ? "NETWORK" : "NONE");

    private static BridgeCapabilitySet Capabilities() => new(
        BridgeCapabilityProvenance.DeviceReported,
        [new(BridgeCapabilityId.BulkRead, true,
            BridgeCapabilityProvenance.DeviceReported)]);
}

sealed class FakeTcpAdapter : IBridgeTcpConnectionAdapter
{
    public int PrepareCount { get; private set; }
    public Func<string, int, TimeSpan, CancellationToken,
        Task<BridgeControlSession>>? ConnectHandler { get; set; }
    public Func<int, CancellationToken, Task<BridgeControlSession>>? ReplaceHandler { get; set; }

    public Task<BridgeControlSession> ConnectAsync(
        string address, int port, TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ConnectHandler?.Invoke(address, port, timeout, cancellationToken) ??
        Task.FromResult(Session(FirmwarePersonality.Network));

    public Task PrepareFirmwareReplacementAsync(CancellationToken cancellationToken)
    {
        PrepareCount++;
        return Task.CompletedTask;
    }

    public Task<BridgeControlSession> ReplaceAfterFirmwareRebootAsync(
        int attempt, CancellationToken cancellationToken) =>
        ReplaceHandler?.Invoke(attempt, cancellationToken) ??
        Task.FromResult(Session(FirmwarePersonality.Network));

    public void EndFirmwareReplacementWithoutSession() { }

    private static BridgeControlSession Session(FirmwarePersonality personality) =>
        new(BridgeControlConnectionKind.WifiTcp, "NICE64 192.0.2.7:6464",
            "NICE64", "test", Mode(personality),
            BridgeNetworkPakTransport.ReliableUdpQuick, Capabilities());

    private static FirmwareModeInfo Mode(FirmwarePersonality personality) =>
        new(personality, personality == FirmwarePersonality.Controller
            ? "CONTROLLER" : "STANDALONE",
            personality == FirmwarePersonality.Network ? "NETWORK" : "NONE");

    private static BridgeCapabilitySet Capabilities() => new(
        BridgeCapabilityProvenance.DeviceReported,
        [new(BridgeCapabilityId.BulkRead, true,
            BridgeCapabilityProvenance.DeviceReported)]);
}

sealed class FakeCleanupAdapter : IBridgeDisconnectCleanupAdapter
{
    public int CallCount { get; private set; }
    public Exception? Error { get; set; }
    public Task<BridgeDisconnectCleanupResult> CleanupAsync(
        bool sendNeutralControllerState,
        CancellationToken cancellationToken)
    {
        CallCount++;
        if (Error is not null) throw Error;
        return Task.FromResult(new BridgeDisconnectCleanupResult(true, true));
    }
}

sealed class FakePersistence : IAccessoryPersistenceService
{
    public int FlushCount { get; private set; }
    public int DeactivateCount { get; private set; }
    public AccessoryPersistenceFlushReason LastFlushReason { get; private set; }
    public AccessoryPersistenceResult FlushResult { get; set; } =
        AccessoryPersistenceResult.Clean;
    public AccessoryPersistenceSnapshot Current { get; private set; } = Empty();

    public void SetDirty() => Current = new(AccessoryPersistenceKind.ControllerPak,
        "test.mpk", 32768, true, true, false, 1, null, string.Empty);

    public Task<AccessoryPersistenceResult> FlushAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FlushCount++;
        if (FlushResult.Success) Current = Current with { Dirty = false };
        return Task.FromResult(FlushResult);
    }

    public Task<AccessoryPersistenceResult> FlushAsync(
        AccessoryPersistenceFlushReason reason,
        CancellationToken cancellationToken = default)
    {
        LastFlushReason = reason;
        return FlushAsync(cancellationToken);
    }

    public bool TryDeactivateClean()
    {
        DeactivateCount++;
        if (Current.Dirty) return false;
        Current = Empty();
        return true;
    }

    public void Activate(AccessoryPersistenceKind kind, string path, int expectedLength,
        Func<CancellationToken, Task<byte[]>> readCurrent, uint initialWriteCounter) =>
        throw new NotSupportedException();
    public void ObserveAccessoryWriteCounter(uint counter) { }
    public void MarkDirty() => SetDirty();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private static AccessoryPersistenceSnapshot Empty() =>
        new(AccessoryPersistenceKind.None, string.Empty, 0, false, false, false,
            0, null, string.Empty);
}

sealed class TrackedDisposable : IDisposable
{
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}
