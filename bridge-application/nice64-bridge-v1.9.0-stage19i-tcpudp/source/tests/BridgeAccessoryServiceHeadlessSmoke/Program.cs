using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static FirmwareModeInfo Mode(FirmwarePersonality personality) =>
    new(personality,
        personality == FirmwarePersonality.Controller ? "CONTROLLER" : "STANDALONE",
        personality == FirmwarePersonality.Network ? "NETWORK" : "NONE");

static BridgeDeviceStateService Connected(
    FirmwarePersonality firmware = FirmwarePersonality.Controller,
    BridgeAccessoryMode accessory = BridgeAccessoryMode.None)
{
    BridgeDeviceStateService state = new();
    IBridgeDeviceStateUpdater updater = state;
    updater.Connect(BridgeControlConnectionKind.SerialUsb, "NICE64", "test", Mode(firmware));
    updater.SetActiveAccessory(accessory);
    return state;
}

static TestRig Rig(
    FirmwarePersonality firmware = FirmwarePersonality.Controller,
    BridgeAccessoryMode accessory = BridgeAccessoryMode.None)
{
    BridgeDeviceStateService state = Connected(firmware, accessory);
    List<string> order = [];
    FakePersistence persistence = new(order);
    FakeFirmware firmwareService = new(state, order);
    FakeControllerPort controller = new(order);
    FakeNetworkPort network = new(order);
    BridgeAccessoryService service = new(
        state, persistence, firmwareService, controller, network);
    return new(state, persistence, firmwareService, controller, network, service, order);
}

static async Task TestAlreadyActiveAsync()
{
    TestRig rig = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    BridgeAccessoryTransitionResult result = await rig.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.ControllerPak);
    Require(result.Success && result.ActiveAccessory == BridgeAccessoryMode.ControllerPak,
        "Already-active accessory did not succeed");
    Require(rig.Firmware.Calls.Count == 0 && rig.Controller.Commands.Count == 0 &&
        rig.Persistence.FlushCount == 0, "Already-active accessory performed transition work");
}

static async Task TestControllerTransitionsAndMappingAsync()
{
    TestRig rig = Rig();
    foreach (BridgeAccessoryMode requested in new[]
    {
        BridgeAccessoryMode.ControllerPak,
        BridgeAccessoryMode.RumblePak,
        BridgeAccessoryMode.TransferPak
    })
    {
        BridgeAccessoryTransitionResult result =
            await rig.Service.SelectAccessoryAsync(requested);
        Require(result.Success && result.AccessoryCommandSent,
            $"Controller transition to {requested} failed");
        Require(rig.State.Current.ActiveAccessory == requested,
            $"{requested} was published before/without verified success");
    }
    Require(rig.Controller.Commands.SequenceEqual(new[]
    {
        "PAKMODE MEM", "PAKMODE RUMBLE", "PAKMODE TRANSFER"
    }), "Controller PAKMODE mapping changed");
    Require(rig.Firmware.Calls.All(x => x == FirmwarePersonality.Controller),
        "Controller accessories requested the wrong firmware personality");
}

static async Task TestNetworkEntryExitAndOrderingAsync()
{
    TestRig rig = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    BridgeAccessoryTransitionResult entered = await rig.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.NetworkPak);
    Require(entered.Success && entered.NetworkPakActivated &&
        rig.State.Current.ActiveAccessory == BridgeAccessoryMode.NetworkPak,
        "Network Pak entry did not complete");
    Require(rig.Firmware.Calls[^1] == FirmwarePersonality.Network,
        "Network Pak did not request Network firmware");
    Require(rig.Order.IndexOf("firmware:Network") < rig.Order.IndexOf("network:activate"),
        "Network carrier activated before firmware verification");

    rig.Order.Clear();
    BridgeAccessoryTransitionResult exited = await rig.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.RumblePak);
    Require(exited.Success && rig.State.Current.ActiveAccessory == BridgeAccessoryMode.RumblePak,
        "Network Pak exit did not complete");
    Require(rig.Firmware.Calls[^1] == FirmwarePersonality.Controller,
        "Network Pak exit did not request Controller firmware");
    Require(rig.Order.SequenceEqual(new[]
    {
        "firmware:Controller", "controller:PAKMODE RUMBLE", "network:release"
    }), "Network Pak exit ownership ordering changed");
}

static async Task TestNetworkReselectRevalidatesAsync()
{
    TestRig rig = Rig(FirmwarePersonality.Network, BridgeAccessoryMode.NetworkPak);
    BridgeAccessoryTransitionResult result = await rig.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.NetworkPak);
    Require(result.Success && result.NetworkPakActivated &&
        rig.Network.ActivateCount == 1 && rig.Firmware.Calls.Count == 0,
        "Reselecting an already selected Network Pak did not revalidate its carrier.");

    rig.Network.ActivateHandler = _ => Task.FromResult(
        new BridgeAccessoryPortResult(false, "stale carrier"));
    BridgeAccessoryTransitionResult failed = await rig.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.NetworkPak);
    Require(!failed.Success &&
        failed.Failure == BridgeAccessoryTransitionFailure.NetworkPakActivation &&
        rig.State.Current.ActiveAccessory == BridgeAccessoryMode.NetworkPak,
        "Failed Network Pak revalidation falsely replaced the last verified accessory.");
    Console.WriteLine("PASS Network Pak reselection revalidates and reports stale activation");
}

static async Task TestFirmwareAndCommandFailuresAsync()
{
    TestRig firmwareRig = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    firmwareRig.Firmware.Handler = (target, _) => Task.FromResult(
        new FirmwareModeTransitionResult(false, Mode(FirmwarePersonality.Controller),
            "firmware rejected", true, false)
        { Failure = FirmwareModeTransitionFailure.RequestRejected });
    BridgeAccessoryTransitionResult firmwareFailure =
        await firmwareRig.Service.SelectAccessoryAsync(BridgeAccessoryMode.NetworkPak);
    Require(!firmwareFailure.Success &&
        firmwareFailure.Failure == BridgeAccessoryTransitionFailure.FirmwareTransition &&
        firmwareRig.State.Current.ActiveAccessory == BridgeAccessoryMode.ControllerPak &&
        firmwareRig.Network.ActivateCount == 0,
        "Firmware failure did not block accessory activation truthfully");

    TestRig commandRig = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    commandRig.Controller.Handler = (_, _) => Task.FromResult(
        new BridgeAccessoryPortResult(false, "missing ACK"));
    BridgeAccessoryTransitionResult commandFailure =
        await commandRig.Service.SelectAccessoryAsync(BridgeAccessoryMode.RumblePak);
    Require(!commandFailure.Success &&
        commandFailure.Failure == BridgeAccessoryTransitionFailure.AccessoryCommand &&
        commandRig.State.Current.ActiveAccessory == BridgeAccessoryMode.ControllerPak,
        "PAKMODE ACK failure replaced the authoritative accessory");
}

static async Task TestPersistenceOrderingAsync()
{
    TestRig rig = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    rig.Persistence.SetDirty();
    BridgeAccessoryTransitionResult result = await rig.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.NetworkPak);
    Require(result.Success && rig.Persistence.FlushCount == 1,
        "Dirty persistence was not flushed exactly once");
    Require(rig.Persistence.LastFlushReason ==
            AccessoryPersistenceFlushReason.AccessoryChange,
        "Accessory transition did not identify the event-driven flush reason");
    Require(rig.Order.SequenceEqual(new[]
    {
        "persistence:flush", "firmware:Network", "network:activate"
    }), "Persistence did not precede all transition work");

    TestRig failed = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    failed.Persistence.SetDirty();
    failed.Persistence.FlushResult = new(false, true, "disk full");
    BridgeAccessoryTransitionResult blocked = await failed.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.NetworkPak);
    Require(!blocked.Success &&
        blocked.Failure == BridgeAccessoryTransitionFailure.Persistence &&
        failed.Firmware.Calls.Count == 0 && failed.Network.ActivateCount == 0 &&
        failed.State.Current.ActiveAccessory == BridgeAccessoryMode.ControllerPak &&
        failed.Persistence.Current.Dirty,
        "Persistence failure did not block every subsequent stage");
}

static async Task TestNetworkActivationFailureAsync()
{
    TestRig rig = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    rig.Network.ActivateHandler = _ => Task.FromResult(
        new BridgeAccessoryPortResult(false, "carrier unavailable"));
    BridgeAccessoryTransitionResult result = await rig.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.NetworkPak);
    Require(!result.Success &&
        result.Failure == BridgeAccessoryTransitionFailure.NetworkPakActivation &&
        rig.State.Current.FirmwarePersonality == FirmwarePersonality.Network &&
        rig.State.Current.ActiveAccessory == BridgeAccessoryMode.None,
        "Network activation failure did not preserve truthful post-firmware state");
}

static async Task TestConcurrentSerializationAndRejectedOwnershipAsync()
{
    TestRig rig = Rig();
    TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int commandNumber = 0;
    rig.Controller.Handler = async (_, _) =>
    {
        if (Interlocked.Increment(ref commandNumber) == 1)
        {
            entered.SetResult();
            await release.Task;
        }
        return BridgeAccessoryPortResult.Succeeded;
    };

    Task<BridgeAccessoryTransitionResult> first = rig.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.ControllerPak);
    await entered.Task;
    BridgeAccessoryTransitionSnapshot owned = rig.State.Current.AccessoryTransition;

    BridgeAccessoryTransitionResult invalid = await rig.Service.SelectAccessoryAsync(
        (BridgeAccessoryMode)999);
    using CancellationTokenSource cancelled = new();
    cancelled.Cancel();
    BridgeAccessoryTransitionResult rejected = await rig.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.TransferPak, cancelled.Token);
    Require(invalid.Failure == BridgeAccessoryTransitionFailure.InvalidAccessory &&
        rejected.Failure == BridgeAccessoryTransitionFailure.Cancelled &&
        rig.State.Current.AccessoryTransition == owned,
        "Rejected request stole active accessory transition state");

    Task<BridgeAccessoryTransitionResult> second = rig.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.RumblePak);
    await Task.Delay(30);
    Require(commandNumber == 1, "Concurrent valid accessory requests were not serialized");
    release.SetResult();
    Require((await first).Success && (await second).Success &&
        rig.State.Current.ActiveAccessory == BridgeAccessoryMode.RumblePak,
        "Serialized transitions did not complete authoritatively");
}

static async Task TestCancellationAsync()
{
    TestRig duringFirmware = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    duringFirmware.Firmware.Handler = (_, _) => Task.FromResult(
        new FirmwareModeTransitionResult(false, Mode(FirmwarePersonality.Controller),
            "cancelled", false, false)
        { Failure = FirmwareModeTransitionFailure.Cancelled });
    BridgeAccessoryTransitionResult firmwareCancelled =
        await duringFirmware.Service.SelectAccessoryAsync(BridgeAccessoryMode.NetworkPak);
    Require(firmwareCancelled.Failure == BridgeAccessoryTransitionFailure.Cancelled &&
        duringFirmware.State.Current.ActiveAccessory == BridgeAccessoryMode.ControllerPak &&
        duringFirmware.Network.ActivateCount == 0,
        "Cancellation during firmware transition was ambiguous");

    TestRig beforeCommand = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    using CancellationTokenSource beforeCommandCts = new();
    beforeCommand.Firmware.Handler = (target, _) =>
    {
        ((IBridgeDeviceStateUpdater)beforeCommand.State).UpdateFirmwareMode(Mode(target));
        beforeCommandCts.Cancel();
        return Task.FromResult(new FirmwareModeTransitionResult(
            true, Mode(target), string.Empty, true, true));
    };
    BridgeAccessoryTransitionResult beforeCommandResult =
        await beforeCommand.Service.SelectAccessoryAsync(
            BridgeAccessoryMode.NetworkPak, beforeCommandCts.Token);
    Require(beforeCommandResult.Failure == BridgeAccessoryTransitionFailure.Cancelled &&
        beforeCommand.Network.ActivateCount == 0 &&
        beforeCommand.State.Current.ActiveAccessory == BridgeAccessoryMode.None,
        "Cancellation before activation did not report the last verified state");

    TestRig duringNetwork = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    duringNetwork.Network.ActivateHandler = token =>
        Task.FromCanceled<BridgeAccessoryPortResult>(token);
    using CancellationTokenSource networkCts = new();
    networkCts.Cancel();
    BridgeAccessoryTransitionResult preCancelled =
        await duringNetwork.Service.SelectAccessoryAsync(
            BridgeAccessoryMode.NetworkPak, networkCts.Token);
    Require(preCancelled.Failure == BridgeAccessoryTransitionFailure.Cancelled &&
        duringNetwork.State.Current.ActiveAccessory == BridgeAccessoryMode.ControllerPak,
        "Pre-ownership cancellation changed accessory state");

    TestRig nonCancellableCommand = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    TaskCompletionSource commandEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource commandRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    nonCancellableCommand.Controller.Handler = async (_, token) =>
    {
        Require(!token.CanBeCanceled,
            "Accepted PAKMODE command was made falsely cancellable");
        commandEntered.SetResult();
        await commandRelease.Task;
        return BridgeAccessoryPortResult.Succeeded;
    };
    using CancellationTokenSource commandCts = new();
    Task<BridgeAccessoryTransitionResult> commandTask =
        nonCancellableCommand.Service.SelectAccessoryAsync(
            BridgeAccessoryMode.RumblePak, commandCts.Token);
    await commandEntered.Task;
    commandCts.Cancel();
    commandRelease.SetResult();
    Require((await commandTask).Success &&
        nonCancellableCommand.State.Current.ActiveAccessory == BridgeAccessoryMode.RumblePak,
        "Cancellation made an issued, verified PAKMODE result ambiguous");
}

static async Task TestNetworkCancellationAndRestoreAsync()
{
    TestRig cancelled = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    TaskCompletionSource activationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    cancelled.Network.ActivateHandler = async token =>
    {
        activationEntered.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return BridgeAccessoryPortResult.Succeeded;
    };
    using CancellationTokenSource cts = new();
    Task<BridgeAccessoryTransitionResult> task = cancelled.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.NetworkPak, cts.Token);
    await activationEntered.Task;
    cts.Cancel();
    BridgeAccessoryTransitionResult result = await task;
    Require(result.Failure == BridgeAccessoryTransitionFailure.Cancelled &&
        cancelled.State.Current.ActiveAccessory == BridgeAccessoryMode.None,
        "Cancellation around Network Pak activation was not truthful");

    TestRig restore = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    await restore.Service.ApplyVerifiedFirmwareAsync(FirmwarePersonality.Network);
    Require(restore.State.Current.ActiveAccessory == BridgeAccessoryMode.NetworkPak,
        "Verified Network firmware did not adopt Network Pak");
    ((IBridgeDeviceStateUpdater)restore.State).UpdateFirmwareMode(
        Mode(FirmwarePersonality.Controller));
    await restore.Service.ApplyVerifiedFirmwareAsync(FirmwarePersonality.Controller);
    Require(restore.State.Current.ActiveAccessory == BridgeAccessoryMode.ControllerPak &&
        restore.Controller.Commands[^1] == "PAKMODE MEM",
        "Previous Controller accessory restore semantics were not preserved below WinForms");
}

static void TestControlSessionReplacementContract()
{
    foreach (BridgeControlConnectionKind replacementKind in new[]
    {
        BridgeControlConnectionKind.SerialUsb,
        BridgeControlConnectionKind.WifiTcp
    })
    {
        BridgeDeviceStateService state = Connected(
            FirmwarePersonality.Controller, BridgeAccessoryMode.ControllerPak);
        IBridgeDeviceStateUpdater updater = state;
        updater.SelectNetworkPakTransport(BridgeNetworkPakTransport.ReliableUdpQuick);
        updater.SetFirmwareTransition(new(BridgeFirmwareTransitionPhase.Reconnecting,
            FirmwarePersonality.Network, string.Empty));
        updater.SetAccessoryTransition(new(
            BridgeAccessoryTransitionPhase.TransitionPending,
            BridgeAccessoryMode.NetworkPak, string.Empty));

        updater.SetControlConnectionState(replacementKind,
            BridgeConnectionState.Disconnected);
        updater.ReplaceControlSession(replacementKind, "NICE64", "replacement",
            Mode(FirmwarePersonality.Network));

        BridgeDeviceStateSnapshot replaced = state.Current;
        Require(replaced.ActiveAccessory == BridgeAccessoryMode.ControllerPak &&
            replaced.AccessoryTransition.Phase ==
                BridgeAccessoryTransitionPhase.TransitionPending &&
            replaced.AccessoryTransition.Target == BridgeAccessoryMode.NetworkPak &&
            replaced.FirmwareTransition.Phase == BridgeFirmwareTransitionPhase.Reconnecting &&
            replaced.FirmwareTransition.Target == FirmwarePersonality.Network &&
            replaced.SelectedNetworkPakTransport ==
                BridgeNetworkPakTransport.ReliableUdpQuick,
            $"{replacementKind} replacement reset semantic transition state");

        updater.Disconnect();
        updater.Connect(replacementKind, "OTHER", "new", Mode(FirmwarePersonality.Controller));
        BridgeDeviceStateSnapshot fresh = state.Current;
        Require(fresh.ActiveAccessory == BridgeAccessoryMode.None &&
            fresh.AccessoryTransition == BridgeAccessoryTransitionSnapshot.Idle &&
            fresh.FirmwareTransition == BridgeFirmwareTransitionSnapshot.Idle,
            $"{replacementKind} genuine new connection inherited stale semantic state");
    }
}

static async Task TestAccessoryTransitionReconnectContinuityAsync()
{
    TestRig successful = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    successful.Firmware.Handler = (target, _) =>
    {
        Require(successful.State.Current.ActiveAccessory ==
                BridgeAccessoryMode.ControllerPak &&
            successful.State.Current.AccessoryTransition.Phase ==
                BridgeAccessoryTransitionPhase.TransitionPending,
            "ControllerPak ownership was lost before reconnect state application");
        IBridgeDeviceStateUpdater updater = successful.State;
        updater.SetControlConnectionState(BridgeControlConnectionKind.WifiTcp,
            BridgeConnectionState.Disconnected);
        updater.ReplaceControlSession(BridgeControlConnectionKind.WifiTcp,
            "NICE64", "reconnected", Mode(target));
        Require(successful.State.Current.ActiveAccessory ==
                BridgeAccessoryMode.ControllerPak &&
            successful.State.Current.AccessoryTransition.Phase ==
                BridgeAccessoryTransitionPhase.TransitionPending,
            "TCP reconnect silently reset accessory ownership");
        return Task.FromResult(new FirmwareModeTransitionResult(
            true, Mode(target), string.Empty, true, true));
    };
    BridgeAccessoryTransitionResult success = await successful.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.NetworkPak);
    Require(success.Success &&
        successful.State.Current.ActiveAccessory == BridgeAccessoryMode.NetworkPak &&
        successful.State.Current.AccessoryTransition.Phase ==
            BridgeAccessoryTransitionPhase.Completed,
        "Successful reconnect transition did not finish NetworkPak/Completed");

    TestRig wrongMode = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    wrongMode.Firmware.Handler = (_, _) =>
    {
        IBridgeDeviceStateUpdater updater = wrongMode.State;
        updater.SetControlConnectionState(BridgeControlConnectionKind.WifiTcp,
            BridgeConnectionState.Disconnected);
        updater.ReplaceControlSession(BridgeControlConnectionKind.WifiTcp,
            "NICE64", "wrong", Mode(FirmwarePersonality.Controller));
        Require(wrongMode.State.Current.ActiveAccessory ==
                BridgeAccessoryMode.ControllerPak &&
            wrongMode.State.Current.AccessoryTransition.Phase ==
                BridgeAccessoryTransitionPhase.TransitionPending,
            "Wrong-mode TCP reconnect reset the previous semantic accessory");
        return Task.FromResult(new FirmwareModeTransitionResult(false,
            Mode(FirmwarePersonality.Controller), "wrong firmware", true, true)
        { Failure = FirmwareModeTransitionFailure.Verification });
    };
    BridgeAccessoryTransitionResult failed = await wrongMode.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.NetworkPak);
    Require(!failed.Success &&
        wrongMode.State.Current.ActiveAccessory == BridgeAccessoryMode.ControllerPak &&
        wrongMode.State.Current.AccessoryTransition.Phase ==
            BridgeAccessoryTransitionPhase.Failed,
        "Failed reconnect did not preserve the last verified accessory");

    TestRig reconnectFailure = Rig(accessory: BridgeAccessoryMode.ControllerPak);
    ((IBridgeDeviceStateUpdater)reconnectFailure.State).SelectNetworkPakTransport(
        BridgeNetworkPakTransport.Tcp);
    reconnectFailure.Persistence.SetDirty();
    reconnectFailure.Firmware.Handler = (_, _) =>
    {
        ((IBridgeDeviceStateUpdater)reconnectFailure.State).SetControlConnectionState(
            BridgeControlConnectionKind.SerialUsb, BridgeConnectionState.Disconnected);
        return Task.FromResult(new FirmwareModeTransitionResult(false, null,
            "reconnect timed out", true, false)
        { Failure = FirmwareModeTransitionFailure.Reconnect });
    };
    BridgeAccessoryTransitionResult reconnectFailed =
        await reconnectFailure.Service.SelectAccessoryAsync(BridgeAccessoryMode.NetworkPak);
    Require(!reconnectFailed.Success &&
        reconnectFailure.State.Current.ActiveAccessory == BridgeAccessoryMode.ControllerPak &&
        reconnectFailure.Persistence.Current is { Active: true, Dirty: false } &&
        reconnectFailure.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.Tcp,
        "Failed serial reconnect lost accessory or persistence ownership state");

    TestRig leavingNetwork = Rig(FirmwarePersonality.Network,
        BridgeAccessoryMode.NetworkPak);
    leavingNetwork.Firmware.Handler = (target, _) =>
    {
        IBridgeDeviceStateUpdater updater = leavingNetwork.State;
        updater.SetControlConnectionState(BridgeControlConnectionKind.SerialUsb,
            BridgeConnectionState.Disconnected);
        updater.ReplaceControlSession(BridgeControlConnectionKind.SerialUsb,
            "NICE64", "reconnected", Mode(target));
        Require(leavingNetwork.State.Current.ActiveAccessory ==
                BridgeAccessoryMode.NetworkPak &&
            leavingNetwork.State.Current.AccessoryTransition.Phase ==
                BridgeAccessoryTransitionPhase.TransitionPending,
            "Serial reconnect reset NetworkPak before Controller activation");
        return Task.FromResult(new FirmwareModeTransitionResult(
            true, Mode(target), string.Empty, true, true));
    };
    leavingNetwork.Controller.Handler = (_, _) =>
    {
        Require(leavingNetwork.State.Current.ActiveAccessory == BridgeAccessoryMode.None &&
            leavingNetwork.State.Current.AccessoryTransition.Phase ==
                BridgeAccessoryTransitionPhase.TransitionPending,
            "Post-firmware, pre-PAKMODE state was not truthful");
        return Task.FromResult(BridgeAccessoryPortResult.Succeeded);
    };
    BridgeAccessoryTransitionResult left = await leavingNetwork.Service.SelectAccessoryAsync(
        BridgeAccessoryMode.ControllerPak);
    Require(left.Success &&
        leavingNetwork.State.Current.ActiveAccessory == BridgeAccessoryMode.ControllerPak &&
        leavingNetwork.State.Current.AccessoryTransition.Phase ==
            BridgeAccessoryTransitionPhase.Completed,
        "NetworkPak to ControllerPak reconnect did not complete truthfully");
}

await TestAlreadyActiveAsync();
await TestControllerTransitionsAndMappingAsync();
await TestNetworkEntryExitAndOrderingAsync();
await TestNetworkReselectRevalidatesAsync();
await TestFirmwareAndCommandFailuresAsync();
await TestPersistenceOrderingAsync();
await TestNetworkActivationFailureAsync();
await TestConcurrentSerializationAndRejectedOwnershipAsync();
await TestCancellationAsync();
await TestNetworkCancellationAndRestoreAsync();
TestControlSessionReplacementContract();
await TestAccessoryTransitionReconnectContinuityAsync();
Console.WriteLine("PASS Bridge accessory service headless semantics");

sealed record TestRig(
    BridgeDeviceStateService State,
    FakePersistence Persistence,
    FakeFirmware Firmware,
    FakeControllerPort Controller,
    FakeNetworkPort Network,
    BridgeAccessoryService Service,
    List<string> Order);

sealed class FakePersistence(List<string> order) : IAccessoryPersistenceService
{
    public int FlushCount { get; private set; }
    public AccessoryPersistenceResult FlushResult { get; set; } =
        new(true, true, string.Empty);
    public AccessoryPersistenceFlushReason LastFlushReason { get; private set; }
    public AccessoryPersistenceSnapshot Current { get; private set; } = Empty();

    public void SetDirty() => Current = new(AccessoryPersistenceKind.ControllerPak,
        "test.mpk", 32768, true, true, false, 1, null, string.Empty);

    public Task<AccessoryPersistenceResult> FlushAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FlushCount++;
        order.Add("persistence:flush");
        if (FlushResult.Success)
            Current = Current with { Dirty = false };
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

sealed class FakeFirmware(
    BridgeDeviceStateService state,
    List<string> order) : IBridgeFirmwareServiceAfterPersistence
{
    public List<FirmwarePersonality> Calls { get; } = [];
    public Func<FirmwarePersonality, CancellationToken,
        Task<FirmwareModeTransitionResult>>? Handler { get; set; }

    public Task<FirmwareModeTransitionResult> SelectModeAsync(
        FirmwarePersonality target,
        CancellationToken cancellationToken = default) =>
        SelectModeAfterPersistenceAsync(target, cancellationToken);

    public async Task<FirmwareModeTransitionResult> SelectModeAfterPersistenceAsync(
        FirmwarePersonality target,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(target);
        order.Add($"firmware:{target}");
        if (Handler is not null) return await Handler(target, cancellationToken);
        FirmwareModeInfo mode = Mode(target);
        ((IBridgeDeviceStateUpdater)state).ReplaceControlSession(
            state.Current.ControlConnectionKind, "NICE64", "reconnected", mode);
        return new(true, mode, string.Empty, true, true);
    }

    private static FirmwareModeInfo Mode(FirmwarePersonality personality) =>
        new(personality,
            personality == FirmwarePersonality.Controller ? "CONTROLLER" : "STANDALONE",
            personality == FirmwarePersonality.Network ? "NETWORK" : "NONE");
}

sealed class FakeControllerPort(List<string> order) : IControllerAccessoryCommandPort
{
    public bool IsAvailable { get; set; } = true;
    public List<string> Commands { get; } = [];
    public Func<string, CancellationToken, Task<BridgeAccessoryPortResult>>? Handler { get; set; }

    public Task<BridgeAccessoryPortResult> SendPakModeAsync(
        string command,
        CancellationToken cancellationToken)
    {
        Commands.Add(command);
        order.Add($"controller:{command}");
        return Handler?.Invoke(command, cancellationToken) ??
            Task.FromResult(BridgeAccessoryPortResult.Succeeded);
    }
}

sealed class FakeNetworkPort(List<string> order) : INetworkPakAccessoryPort
{
    public int ActivateCount { get; private set; }
    public int ReleaseCount { get; private set; }
    public Func<CancellationToken, Task<BridgeAccessoryPortResult>>? ActivateHandler { get; set; }
    public Func<CancellationToken, Task<BridgeAccessoryPortResult>>? ReleaseHandler { get; set; }

    public Task<BridgeAccessoryPortResult> ActivateSelectedCarrierAsync(
        CancellationToken cancellationToken)
    {
        ActivateCount++;
        order.Add("network:activate");
        return ActivateHandler?.Invoke(cancellationToken) ??
            Task.FromResult(BridgeAccessoryPortResult.Succeeded);
    }

    public Task<BridgeAccessoryPortResult> ReleaseActiveCarrierAsync(
        CancellationToken cancellationToken)
    {
        ReleaseCount++;
        order.Add("network:release");
        return ReleaseHandler?.Invoke(cancellationToken) ??
            Task.FromResult(BridgeAccessoryPortResult.Succeeded);
    }
}
