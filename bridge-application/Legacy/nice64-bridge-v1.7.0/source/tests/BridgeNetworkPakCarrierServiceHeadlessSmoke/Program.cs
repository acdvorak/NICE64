using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void RequireSubsequence(
    IReadOnlyList<string> actual,
    params string[] expected)
{
    int next = 0;
    foreach (string item in actual)
    {
        if (next < expected.Length && item == expected[next]) next++;
    }
    Require(next == expected.Length,
        $"Expected call sequence was missing. Expected: {string.Join(", ", expected)}; " +
        $"actual: {string.Join(", ", actual)}");
}

static async Task InitializeAsync(Harness harness)
{
    BridgeNetworkPakCarrierResult result = await harness.Service.InitializeAsync();
    Require(result.Success && result.Committed &&
            harness.State.Current.SelectedNetworkPakTransport == harness.Store.LoadSelection &&
            harness.State.Current.ActiveNetworkPakTransport ==
                BridgeNetworkPakTransport.None &&
            result.Snapshot.PersistenceState ==
                BridgeNetworkPakCarrierPersistenceState.Confirmed &&
            !harness.Calls.Any(call => call.StartsWith("line.begin:") ||
                call.StartsWith("quick.start:")),
        "Initialization did not apply persisted selection without activation.");
}

Harness tcp = new(loadSelection: BridgeNetworkPakTransport.Tcp, loadUsesDefault: true);
await InitializeAsync(tcp);
Require(tcp.State.Current.SelectedNetworkPakTransport ==
        BridgeNetworkPakCarrierSelectionDefaults.Selection,
    "Default initialization did not select TCP.");
Console.WriteLine("PASS typed default initialization selects TCP without activation");

tcp.Calls.Clear();
BridgeNetworkPakCarrierResult tcpPending = await tcp.Service.ActivateSelectedAsync();
long tcpGeneration = tcp.Service.PendingLineActivationGeneration ?? 0;
Require(tcpPending.Success && tcpPending.Committed &&
        tcpPending.Outcome ==
            BridgeNetworkPakCarrierOutcome.ActivationPendingHandshake &&
        tcpPending.Snapshot.TransitionPhase ==
            BridgeNetworkPakCarrierTransitionPhase.AwaitingHandshake &&
        tcp.State.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None &&
        tcpGeneration > 0 && tcp.Application.Connected &&
        !tcp.Application.HandshakeComplete,
    "TCP HELLO initiation falsely claimed an active carrier.");
RequireSubsequence(tcp.Calls,
    $"line.begin:Tcp:{tcpGeneration}", "app.connected:True",
    "app.handshake:False");
Console.WriteLine("PASS TCP activation initiates HELLO and remains AwaitingHandshake");

int beginCount = tcp.Calls.Count(call => call.StartsWith("line.begin:Tcp:"));
BridgeNetworkPakCarrierResult repeatedPendingActivation =
    await tcp.Service.ActivateSelectedAsync();
Require(repeatedPendingActivation.Success && !repeatedPendingActivation.Committed &&
        repeatedPendingActivation.Outcome ==
            BridgeNetworkPakCarrierOutcome.ActivationPendingHandshake &&
        tcp.Calls.Count(call => call.StartsWith("line.begin:Tcp:")) == beginCount,
    "Repeated activation sent a second HELLO while the first was pending.");
Console.WriteLine("PASS repeated activation preserves one pending line lifetime");

BridgeNetworkPakCarrierResult wrongAck =
    await tcp.Service.ObserveLineHandshakeAcknowledgedAsync(
        BridgeNetworkPakTransport.UsbSerial, tcpGeneration);
BridgeNetworkPakCarrierResult staleAck =
    await tcp.Service.ObserveLineHandshakeAcknowledgedAsync(
        BridgeNetworkPakTransport.Tcp, tcpGeneration + 1);
Require(!wrongAck.Success && !staleAck.Success &&
        tcp.State.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None &&
        tcp.Service.PendingLineActivationGeneration == tcpGeneration,
    "A stale or foreign line acknowledgement changed semantic ownership.");
Console.WriteLine("PASS stale and foreign line acknowledgements are rejected");

BridgeNetworkPakCarrierResult tcpActive =
    await tcp.Service.ObserveLineHandshakeAcknowledgedAsync(
        BridgeNetworkPakTransport.Tcp, tcpGeneration);
Require(tcpActive.Success && tcpActive.Committed &&
        tcp.State.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.Tcp &&
        tcp.Application.Connected && tcp.Application.HandshakeComplete &&
        tcp.Service.PendingLineActivationGeneration is null,
    "Matching TCP acknowledgement did not establish authoritative active state.");
Console.WriteLine("PASS matching TCP acknowledgement establishes the active carrier");

Harness pendingSwitch = new(BridgeNetworkPakTransport.Tcp);
await InitializeAsync(pendingSwitch);
await pendingSwitch.Service.ActivateSelectedAsync();
pendingSwitch.Calls.Clear();
BridgeNetworkPakCarrierResult pendingToQuick =
    await pendingSwitch.Service.SelectCarrierAsync(
        BridgeNetworkPakTransport.ReliableUdpQuick);
Require(pendingToQuick.Success && pendingToQuick.Committed &&
        pendingSwitch.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        pendingSwitch.Delay.Delays.SequenceEqual(new[] { TimeSpan.FromMilliseconds(50) }),
    "Switching away from a pending line carrier did not retire its ownership.");
RequireSubsequence(pendingSwitch.Calls,
    "line.release:Tcp", "app.handshake:False", "app.connected:False",
    "delay:50", "state.select:ReliableUdpQuick",
    "store.save:ReliableUdpQuick");
Console.WriteLine("PASS switching away from pending line ownership retires it before handoff");

tcp.Calls.Clear();
BridgeNetworkPakCarrierResult tcpToQuick = await tcp.Service.SelectCarrierAsync(
    BridgeNetworkPakTransport.ReliableUdpQuick);
long firstQuickGeneration = tcp.Service.ActiveQuickGeneration ?? 0;
Require(tcpToQuick.Success && tcpToQuick.Committed && firstQuickGeneration > 0 &&
        tcp.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        tcp.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        tcp.Delay.Delays.SequenceEqual(new[] { TimeSpan.FromMilliseconds(50) }),
    "TCP to Quick did not complete the required semantic transition.");
RequireSubsequence(tcp.Calls,
    "line.release:Tcp", "app.handshake:False", "app.connected:False",
    "state.deactivate:Tcp", "delay:50",
    "state.select:ReliableUdpQuick", "store.save:ReliableUdpQuick",
    $"quick.start:{firstQuickGeneration}", "app.connected:True",
    "app.handshake:True", "state.activate:ReliableUdpQuick");
Console.WriteLine("PASS TCP to Quick preserves release/50ms/select/persist/start ordering");

tcp.Calls.Clear();
tcp.Delay.Delays.Clear();
BridgeNetworkPakCarrierResult quickToTcp = await tcp.Service.SelectCarrierAsync(
    BridgeNetworkPakTransport.Tcp);
long secondTcpGeneration = tcp.Service.PendingLineActivationGeneration ?? 0;
Require(quickToTcp.Success &&
        quickToTcp.Outcome ==
            BridgeNetworkPakCarrierOutcome.ActivationPendingHandshake &&
        tcp.State.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None &&
        secondTcpGeneration > 0 &&
        tcp.Delay.Delays.SequenceEqual(new[] { TimeSpan.FromMilliseconds(50) }),
    "Quick to TCP did not remain pending until line acknowledgement.");
RequireSubsequence(tcp.Calls,
    $"quick.stop:{firstQuickGeneration}",
    "state.deactivate:ReliableUdpQuick", "delay:50", "state.select:Tcp",
    "store.save:Tcp", $"line.begin:Tcp:{secondTcpGeneration}");
await tcp.Service.ObserveLineHandshakeAcknowledgedAsync(
    BridgeNetworkPakTransport.Tcp, secondTcpGeneration);
Require(tcp.State.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.Tcp,
    "Quick to TCP acknowledgement did not complete activation.");
Console.WriteLine("PASS Quick to TCP preserves ordering and requires ACK before active");

tcp.Calls.Clear();
BridgeNetworkPakCarrierResult sameActive = await tcp.Service.SelectCarrierAsync(
    BridgeNetworkPakTransport.Tcp);
Require(sameActive.Success && !sameActive.Committed &&
        sameActive.Outcome == BridgeNetworkPakCarrierOutcome.NoOp &&
        tcp.Calls.Count == 0,
    "Same-selection active carrier performed physical or persistence work.");
Console.WriteLine("PASS same-selection active carrier is a typed no-op");

Harness selectionOnly = new(BridgeNetworkPakTransport.Tcp);
await InitializeAsync(selectionOnly);
selectionOnly.Context.ActivationRequired = false;
selectionOnly.Quick.Available = false;
selectionOnly.Calls.Clear();
BridgeNetworkPakCarrierResult storedUnavailable =
    await selectionOnly.Service.SelectCarrierAsync(
        BridgeNetworkPakTransport.ReliableUdpQuick);
Require(storedUnavailable.Success && storedUnavailable.Committed &&
        storedUnavailable.Outcome == BridgeNetworkPakCarrierOutcome.SelectionOnly &&
        selectionOnly.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        selectionOnly.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.None &&
        selectionOnly.Store.Saved.SequenceEqual(
            new[] { BridgeNetworkPakTransport.ReliableUdpQuick }) &&
        !selectionOnly.Calls.Any(call => call.StartsWith("quick.start:")),
    "Inactive selection incorrectly required physical availability.");
Console.WriteLine("PASS unavailable carrier intent persists when activation is unnecessary");

Harness unavailable = new(BridgeNetworkPakTransport.Tcp);
await InitializeAsync(unavailable);
unavailable.Quick.Available = false;
unavailable.Calls.Clear();
BridgeNetworkPakCarrierResult unavailableResult =
    await unavailable.Service.SelectCarrierAsync(
        BridgeNetworkPakTransport.ReliableUdpQuick);
Require(!unavailableResult.Success && unavailableResult.Committed &&
        unavailableResult.Failure ==
            BridgeNetworkPakCarrierFailure.CarrierUnavailable &&
        unavailable.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        unavailable.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.None &&
        !unavailable.Calls.Any(call => call.StartsWith("line.begin:Tcp") ||
            call.StartsWith("quick.start:")),
    "Unavailable activation did not retain explicit selection truth.");
Console.WriteLine("PASS unavailable required carrier fails after commit without fallback");

Harness prerequisite = new(BridgeNetworkPakTransport.Tcp);
await InitializeAsync(prerequisite);
prerequisite.Quick.StartResult = new(false,
    BridgeNetworkPakCarrierFailure.PrerequisiteNotMet, "TCP control unavailable");
BridgeNetworkPakCarrierResult prerequisiteResult =
    await prerequisite.Service.SelectCarrierAsync(
        BridgeNetworkPakTransport.ReliableUdpQuick);
Require(!prerequisiteResult.Success && prerequisiteResult.Committed &&
        prerequisiteResult.Failure ==
            BridgeNetworkPakCarrierFailure.PrerequisiteNotMet &&
        prerequisite.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        prerequisite.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.None,
    "Quick prerequisite failure lost requested selection truth.");
Console.WriteLine("PASS Quick prerequisite failure is typed and does not fall back");

Harness persistenceFailure = new(BridgeNetworkPakTransport.Tcp);
await InitializeAsync(persistenceFailure);
persistenceFailure.Store.SaveResult = new(false,
    BridgeNetworkPakTransport.ReliableUdpQuick,
    BridgeNetworkPakCarrierFailure.PersistenceFailed, "synthetic persistence failure");
persistenceFailure.Calls.Clear();
BridgeNetworkPakCarrierResult persistenceResult =
    await persistenceFailure.Service.SelectCarrierAsync(
        BridgeNetworkPakTransport.ReliableUdpQuick);
Require(!persistenceResult.Success && persistenceResult.Committed &&
        persistenceResult.Failure ==
            BridgeNetworkPakCarrierFailure.PersistenceFailed &&
        persistenceResult.Snapshot.PersistenceState ==
            BridgeNetworkPakCarrierPersistenceState.FailedUnknown &&
        persistenceFailure.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        persistenceFailure.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.None &&
        !persistenceFailure.Calls.Any(call => call.StartsWith("quick.start:")),
    "Persistence failure did not retain truthful committed selection state.");
Console.WriteLine("PASS persistence failure retains new selected intent and stops activation");

Harness releaseFailure = Harness.WithActive(BridgeNetworkPakTransport.Tcp);
releaseFailure.Line.ReleaseResult = new(false,
    BridgeNetworkPakCarrierFailure.ReleaseFailed, "synthetic release failure");
releaseFailure.Calls.Clear();
BridgeNetworkPakCarrierResult releaseFailureResult =
    await releaseFailure.Service.SelectCarrierAsync(
        BridgeNetworkPakTransport.ReliableUdpQuick);
Require(!releaseFailureResult.Success && releaseFailureResult.Committed &&
        releaseFailureResult.Failure ==
            BridgeNetworkPakCarrierFailure.ReleaseFailed &&
        releaseFailure.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.Tcp &&
        releaseFailure.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.Tcp &&
        !releaseFailure.Calls.Any(call => call.StartsWith("quick.start:")),
    "Release failure began the new-carrier transition or fabricated release.");
Console.WriteLine("PASS release failure retains proven old ownership and blocks activation");

Harness quickActivationFailure = new(BridgeNetworkPakTransport.Tcp);
await InitializeAsync(quickActivationFailure);
quickActivationFailure.Quick.StartResult = new(false,
    BridgeNetworkPakCarrierFailure.ActivationFailed, "synthetic activation failure");
BridgeNetworkPakCarrierResult activationFailureResult =
    await quickActivationFailure.Service.SelectCarrierAsync(
        BridgeNetworkPakTransport.ReliableUdpQuick);
Require(!activationFailureResult.Success && activationFailureResult.Committed &&
        activationFailureResult.Failure ==
            BridgeNetworkPakCarrierFailure.ActivationFailed &&
        quickActivationFailure.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        quickActivationFailure.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.None &&
        !quickActivationFailure.Application.Connected,
    "Quick activation failure left false active/application ownership.");
Console.WriteLine("PASS Quick activation failure retains selection and clears ownership");

Harness quickFault = new(BridgeNetworkPakTransport.ReliableUdpQuick);
await InitializeAsync(quickFault);
BridgeNetworkPakCarrierResult quickStarted =
    await quickFault.Service.ActivateSelectedAsync();
long quickGenerationOne = quickFault.Service.ActiveQuickGeneration ?? 0;
Require(quickStarted.Success && quickGenerationOne > 0,
    "Quick test lifetime did not start.");
BridgeNetworkPakCarrierResult currentFault =
    await quickFault.Service.ReportQuickFaultAsync(
        quickGenerationOne, "synthetic current fault");
Require(!currentFault.Success && currentFault.Committed &&
        currentFault.Failure == BridgeNetworkPakCarrierFailure.Faulted &&
        quickFault.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        quickFault.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.None &&
        !quickFault.Application.Connected &&
        quickFault.Service.ActiveQuickGeneration is null,
    "Current Quick fault did not clear only runtime ownership.");
BridgeNetworkPakCarrierResult retryResult =
    await quickFault.Service.SelectCarrierAsync(
        BridgeNetworkPakTransport.ReliableUdpQuick);
long quickGenerationTwo = quickFault.Service.ActiveQuickGeneration ?? 0;
Require(retryResult.Success && retryResult.Committed &&
        quickGenerationTwo > quickGenerationOne &&
        quickFault.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick,
    "Same-selection retry after Quick fault did not start a new generation.");
BridgeNetworkPakCarrierResult staleFault =
    await quickFault.Service.ReportQuickFaultAsync(
        quickGenerationOne, "stale old lifetime");
Require(staleFault.Success && !staleFault.Committed &&
        staleFault.Outcome == BridgeNetworkPakCarrierOutcome.NoOp &&
        quickFault.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        quickFault.Service.ActiveQuickGeneration == quickGenerationTwo,
    "A stale Quick fault cleared a newer generation.");
Console.WriteLine("PASS current Quick fault, same-selection retry, and stale-fault rejection");

Harness faultRetirement = new(BridgeNetworkPakTransport.ReliableUdpQuick);
await InitializeAsync(faultRetirement);
await faultRetirement.Service.ActivateSelectedAsync();
long faultedPhysicalGeneration =
    faultRetirement.Service.ActiveQuickGeneration ?? 0;
await faultRetirement.Service.ReportQuickFaultAsync(
    faultedPhysicalGeneration, "retained physical lifetime");
faultRetirement.Calls.Clear();
BridgeNetworkPakCarrierResult noActiveRelease =
    await faultRetirement.Service.ReleaseActiveAsync();
Require(noActiveRelease.Success && !noActiveRelease.Committed &&
        noActiveRelease.Outcome == BridgeNetworkPakCarrierOutcome.NoOp &&
        !faultRetirement.Calls.Any(call => call.StartsWith("quick.stop:")),
    "ReleaseActive used retired physical ownership instead of authoritative Active.");
faultRetirement.Calls.Clear();
BridgeNetworkPakCarrierResult faultedRetirement =
    await faultRetirement.Service.RetirePhysicalCarrierAsync("replacement after fault");
Require(faultedRetirement.Success && faultedRetirement.Committed &&
        faultRetirement.Calls.Contains(
            $"quick.stop:{faultedPhysicalGeneration}") &&
        faultRetirement.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        faultRetirement.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.None,
    "A faulted Quick physical lifetime could not be retired under the service gate.");
Console.WriteLine("PASS faulted Quick physical lifetime remains available for gated retirement");

quickFault.Calls.Clear();
BridgeNetworkPakCarrierResult retired =
    await quickFault.Service.RetirePhysicalCarrierAsync("firmware replacement");
Require(retired.Success && retired.Committed &&
        quickFault.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        quickFault.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.None &&
        !quickFault.Application.Connected &&
        !quickFault.Application.HandshakeComplete,
    "Firmware replacement retirement did not preserve only selected intent.");
BridgeNetworkPakCarrierResult reactivated =
    await quickFault.Service.ActivateSelectedAsync();
Require(reactivated.Success &&
        quickFault.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick,
    "Explicit post-replacement activation did not restore selected carrier.");
Console.WriteLine("PASS firmware replacement retires ownership and explicit reactivation restores it");

Harness activeNotSelected = Harness.WithActive(
    BridgeNetworkPakTransport.Tcp,
    selected: BridgeNetworkPakTransport.ReliableUdpQuick);
activeNotSelected.Calls.Clear();
BridgeNetworkPakCarrierResult activeRelease =
    await activeNotSelected.Service.ReleaseActiveAsync();
Require(activeRelease.Success &&
        activeNotSelected.Calls.Contains("line.release:Tcp") &&
        !activeNotSelected.Calls.Any(call => call.StartsWith("quick.stop:")) &&
        activeNotSelected.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        activeNotSelected.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.None,
    "Release used selected rather than authoritative active carrier.");
activeNotSelected.Calls.Clear();
BridgeNetworkPakCarrierResult repeatedRelease =
    await activeNotSelected.Service.ReleaseActiveAsync();
Require(repeatedRelease.Success && !repeatedRelease.Committed &&
        repeatedRelease.Outcome == BridgeNetworkPakCarrierOutcome.NoOp &&
        !activeNotSelected.Calls.Any(call => call.StartsWith("line.release:") ||
            call.StartsWith("quick.stop:")),
    "Repeated release with no ownership was not idempotent.");
Console.WriteLine("PASS release uses authoritative Active and repeated release is idempotent");

Harness lineFailure = new(BridgeNetworkPakTransport.UsbSerial);
await InitializeAsync(lineFailure);
await lineFailure.Service.ActivateSelectedAsync();
long pendingUsbGeneration = lineFailure.Service.PendingLineActivationGeneration ?? 0;
BridgeNetworkPakCarrierResult lineFailed =
    await lineFailure.Service.ReportLineActivationFailureAsync(
        BridgeNetworkPakTransport.UsbSerial, pendingUsbGeneration,
        BridgeNetworkPakCarrierFailure.HandshakeFailed, "ACK timeout");
Require(!lineFailed.Success && lineFailed.Committed &&
        lineFailed.Failure == BridgeNetworkPakCarrierFailure.HandshakeFailed &&
        lineFailure.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.UsbSerial &&
        lineFailure.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.None &&
        lineFailure.Service.PendingLineActivationGeneration is null &&
        !lineFailure.Application.Connected,
    "Pending line failure did not retire semantic ownership.");
Console.WriteLine("PASS pending line activation can be retired without a timeout worker");

Harness disconnected = new(BridgeNetworkPakTransport.ReliableUdpQuick);
await InitializeAsync(disconnected);
await disconnected.Service.ActivateSelectedAsync();
disconnected.Calls.Clear();
BridgeNetworkPakCarrierResult disconnectedResult =
    await disconnected.Service.ConvergeDisconnectedAsync("control lost");
Require(disconnectedResult.Success &&
        disconnected.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        disconnected.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.None &&
        !disconnected.Application.Connected &&
        !disconnected.Calls.Any(call => call.StartsWith("quick.stop:")),
    "Genuine-disconnect convergence attempted physical ownership or lost selection.");
Console.WriteLine("PASS genuine disconnect clears semantic ownership without redefining connection cleanup");

Harness shutdown = Harness.WithActive(BridgeNetworkPakTransport.Tcp);
shutdown.Line.ReleaseResult = new(false,
    BridgeNetworkPakCarrierFailure.ReleaseFailed, "shutdown release failed");
BridgeNetworkPakCarrierResult shutdownResult = await shutdown.Service.ShutdownAsync();
Require(!shutdownResult.Success && shutdownResult.Committed &&
        shutdownResult.Failure == BridgeNetworkPakCarrierFailure.ReleaseFailed &&
        shutdown.State.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None &&
        shutdown.State.Current.SelectedNetworkPakTransport == BridgeNetworkPakTransport.Tcp &&
        !shutdown.Application.Connected && !shutdown.Application.HandshakeComplete,
    "Shutdown failure preserved false semantic/application ownership.");
Console.WriteLine("PASS shutdown converges ownership despite physical release failure");

foreach ((string name, Harness harness, BridgeNetworkPakTransport request) in new[]
{
    ("release", Harness.WithActive(BridgeNetworkPakTransport.Tcp),
        BridgeNetworkPakTransport.ReliableUdpQuick),
    ("selection", new Harness(BridgeNetworkPakTransport.Tcp),
        BridgeNetworkPakTransport.ReliableUdpQuick),
    ("retry", new Harness(BridgeNetworkPakTransport.ReliableUdpQuick),
        BridgeNetworkPakTransport.ReliableUdpQuick)
})
{
    if (name != "release") await InitializeAsync(harness);
    harness.Calls.Clear();
    BridgeDeviceStateSnapshot before = harness.State.Current;
    using CancellationTokenSource cancelled = new();
    cancelled.Cancel();
    BridgeNetworkPakCarrierResult cancelledResult =
        await harness.Service.SelectCarrierAsync(request, cancelled.Token);
    Require(!cancelledResult.Success && !cancelledResult.Committed &&
            cancelledResult.Failure == BridgeNetworkPakCarrierFailure.Cancelled &&
            harness.State.Current.SelectedNetworkPakTransport ==
                before.SelectedNetworkPakTransport &&
            harness.State.Current.ActiveNetworkPakTransport ==
                before.ActiveNetworkPakTransport && harness.Calls.Count == 0,
        $"Cancellation before {name} commit mutated lifecycle state.");
}
Console.WriteLine("PASS cancellation before release/selection/retry commit performs no mutation");

Harness postCommit = Harness.WithActive(BridgeNetworkPakTransport.Tcp);
using CancellationTokenSource cancelAfterRelease = new();
postCommit.Line.OnRelease = _ => cancelAfterRelease.Cancel();
postCommit.Calls.Clear();
BridgeNetworkPakCarrierResult postCommitResult =
    await postCommit.Service.SelectCarrierAsync(
        BridgeNetworkPakTransport.ReliableUdpQuick,
        cancelAfterRelease.Token);
Require(postCommitResult.Success && postCommitResult.Committed &&
        cancelAfterRelease.IsCancellationRequested &&
        postCommit.State.Current.SelectedNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        postCommit.State.Current.ActiveNetworkPakTransport ==
            BridgeNetworkPakTransport.ReliableUdpQuick,
    "Cancellation after commit stranded the lifecycle transition.");
Console.WriteLine("PASS cancellation after commit completes convergence with non-cancellable ports");

Console.WriteLine("PASS service is F8-neutral and the smoke has no Form or real-transport dependency");
Console.WriteLine("ALL BRIDGE NETWORK PAK CARRIER SERVICE TESTS PASSED");

sealed class Harness
{
    public Harness(
        BridgeNetworkPakTransport loadSelection,
        bool loadUsesDefault = false,
        BridgeNetworkPakTransport selected = BridgeNetworkPakTransport.None,
        BridgeNetworkPakTransport active = BridgeNetworkPakTransport.None)
    {
        State = new FakeStatePort(Calls, selected, active);
        Context = new FakeActivationContext();
        Store = new FakeSelectionStore(Calls, loadSelection, loadUsesDefault);
        Line = new FakeLineCarrierPort(Calls);
        Quick = new FakeQuickCarrierPort(Calls);
        Application = new FakeApplicationOwnerPort(Calls)
        {
            Connected = active != BridgeNetworkPakTransport.None,
            HandshakeComplete = active != BridgeNetworkPakTransport.None
        };
        Delay = new FakeDelay(Calls);
        Service = new(State, Context, Store, Line, Quick, Application, Delay);
    }

    public List<string> Calls { get; } = [];
    public FakeStatePort State { get; }
    public FakeActivationContext Context { get; }
    public FakeSelectionStore Store { get; }
    public FakeLineCarrierPort Line { get; }
    public FakeQuickCarrierPort Quick { get; }
    public FakeApplicationOwnerPort Application { get; }
    public FakeDelay Delay { get; }
    public BridgeNetworkPakCarrierService Service { get; }

    public static Harness WithActive(
        BridgeNetworkPakTransport active,
        BridgeNetworkPakTransport? selected = null) =>
        new(selected ?? active, selected: selected ?? active, active: active);
}

sealed class FakeActivationContext : IBridgeNetworkPakCarrierActivationContext
{
    public bool ActivationRequired { get; set; } = true;
}

sealed class FakeStatePort : IBridgeNetworkPakCarrierStatePort
{
    private readonly List<string> _calls;
    private BridgeDeviceStateSnapshot _current;

    public FakeStatePort(List<string> calls,
        BridgeNetworkPakTransport selected,
        BridgeNetworkPakTransport active)
    {
        _calls = calls;
        _current = new("FAKE", "1", BridgeDeviceRole.Standalone,
            BridgeDeviceService.Network, FirmwarePersonality.Network,
            BridgeControlConnectionKind.WifiTcp, BridgeConnectionState.Connected,
            selected, active,
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.DeviceReported),
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.BridgeImplemented),
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.ApplicationNegotiated),
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.Effective),
            1, DateTimeOffset.UtcNow);
    }

    public BridgeDeviceStateSnapshot Current => _current;

    public event EventHandler<BridgeDeviceStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }

    public void SelectCarrier(BridgeNetworkPakTransport carrier)
    {
        _calls.Add($"state.select:{carrier}");
        _current = _current with
        {
            SelectedNetworkPakTransport = carrier,
            ActiveNetworkPakTransport =
                _current.ActiveNetworkPakTransport == carrier
                    ? carrier : BridgeNetworkPakTransport.None
        };
    }

    public void ActivateCarrier(BridgeNetworkPakTransport carrier)
    {
        _calls.Add($"state.activate:{carrier}");
        _current = _current with { ActiveNetworkPakTransport = carrier };
    }

    public void DeactivateCarrier(BridgeNetworkPakTransport carrier)
    {
        _calls.Add($"state.deactivate:{carrier}");
        if (_current.ActiveNetworkPakTransport == carrier)
        {
            _current = _current with
            {
                ActiveNetworkPakTransport = BridgeNetworkPakTransport.None
            };
        }
    }
}

sealed class FakeSelectionStore(
    List<string> calls,
    BridgeNetworkPakTransport loadSelection,
    bool loadUsesDefault) : IBridgeNetworkPakCarrierSelectionStore
{
    public BridgeNetworkPakTransport LoadSelection { get; } = loadSelection;
    public List<BridgeNetworkPakTransport> Saved { get; } = [];
    public BridgeNetworkPakCarrierSelectionSaveResult? SaveResult { get; set; }

    public ValueTask<BridgeNetworkPakCarrierSelectionLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add($"store.load:{LoadSelection}");
        return ValueTask.FromResult(new BridgeNetworkPakCarrierSelectionLoadResult(
            true, LoadSelection, loadUsesDefault,
            BridgeNetworkPakCarrierFailure.None, null));
    }

    public ValueTask<BridgeNetworkPakCarrierSelectionSaveResult> SaveAsync(
        BridgeNetworkPakTransport selection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add($"store.save:{selection}");
        Saved.Add(selection);
        return ValueTask.FromResult(SaveResult ?? new(
            true, selection, BridgeNetworkPakCarrierFailure.None, null));
    }
}

sealed class FakeLineCarrierPort(List<string> calls) : IBridgeNetworkPakLineCarrierPort
{
    public bool UsbAvailable { get; set; } = true;
    public bool TcpAvailable { get; set; } = true;
    public BridgeNetworkPakCarrierPortResult BeginResult { get; set; } =
        BridgeNetworkPakCarrierPortResult.Succeeded;
    public BridgeNetworkPakCarrierPortResult ReleaseResult { get; set; } =
        BridgeNetworkPakCarrierPortResult.Succeeded;
    public Action<BridgeNetworkPakTransport>? OnRelease { get; set; }

    public bool IsAvailable(BridgeNetworkPakTransport carrier) => carrier switch
    {
        BridgeNetworkPakTransport.UsbSerial => UsbAvailable,
        BridgeNetworkPakTransport.Tcp => TcpAvailable,
        _ => false
    };

    public ValueTask<BridgeNetworkPakCarrierPortResult> BeginActivationAsync(
        BridgeNetworkPakTransport carrier,
        long generation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add($"line.begin:{carrier}:{generation}");
        return ValueTask.FromResult(BeginResult);
    }

    public ValueTask<BridgeNetworkPakCarrierPortResult> ReleaseAsync(
        BridgeNetworkPakTransport carrier,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add($"line.release:{carrier}");
        OnRelease?.Invoke(carrier);
        return ValueTask.FromResult(ReleaseResult);
    }
}

sealed class FakeQuickCarrierPort(List<string> calls) : IBridgeNetworkPakQuickCarrierPort
{
    public bool Available { get; set; } = true;
    public BridgeNetworkPakCarrierPortResult StartResult { get; set; } =
        BridgeNetworkPakCarrierPortResult.Succeeded;
    public BridgeNetworkPakCarrierPortResult StopResult { get; set; } =
        BridgeNetworkPakCarrierPortResult.Succeeded;

    public bool IsAvailable => Available;

    public ValueTask<BridgeNetworkPakCarrierPortResult> StartAsync(
        long generation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add($"quick.start:{generation}");
        return ValueTask.FromResult(StartResult);
    }

    public ValueTask<BridgeNetworkPakCarrierPortResult> StopAsync(
        long generation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add($"quick.stop:{generation}");
        return ValueTask.FromResult(StopResult);
    }
}

sealed class FakeApplicationOwnerPort(List<string> calls) :
    IBridgeNetworkPakApplicationOwnerPort
{
    public bool Connected { get; set; }
    public bool HandshakeComplete { get; set; }

    public void SetConnected(bool connected)
    {
        Connected = connected;
        calls.Add($"app.connected:{connected}");
    }

    public void SetHandshakeComplete(bool complete)
    {
        HandshakeComplete = complete;
        calls.Add($"app.handshake:{complete}");
    }
}

sealed class FakeDelay(List<string> calls) : IBridgeNetworkPakCarrierDelay
{
    public List<TimeSpan> Delays { get; } = [];

    public ValueTask DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        calls.Add($"delay:{delay.TotalMilliseconds:0}");
        return ValueTask.CompletedTask;
    }
}
