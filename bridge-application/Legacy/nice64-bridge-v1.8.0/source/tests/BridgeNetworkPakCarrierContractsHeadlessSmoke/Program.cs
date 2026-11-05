using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static BridgeNetworkPakCarrierTransitionPlan Plan(
    BridgeNetworkPakTransport selected,
    BridgeNetworkPakTransport active,
    BridgeNetworkPakTransport requested,
    bool activationRequired = true,
    bool available = true) => BridgeNetworkPakCarrierTransitionPolicy.Plan(
        selected, active, requested, activationRequired, available);

BridgeNetworkPakCarrierDescriptor[] descriptors =
    BridgeNetworkPakCarrierTransitionPolicy.AvailableCarriers.ToArray();
Require(descriptors.Select(item => item.Carrier).SequenceEqual(
    new[]
    {
        BridgeNetworkPakTransport.UsbSerial,
        BridgeNetworkPakTransport.Tcp,
        BridgeNetworkPakTransport.ReliableUdpQuick
    }), "The semantic carrier descriptor set is incomplete or reordered.");
Require(descriptors.Single(item => item.Carrier == BridgeNetworkPakTransport.UsbSerial)
        is { Supported: true, LocallySelectable: true, RemoteUseRecommended: false },
    "USB must remain supported, locally selectable, and not remotely recommended.");
Require(BridgeNetworkPakCarrierSelectionDefaults.Selection ==
        BridgeNetworkPakTransport.Tcp,
    "The carrier selection-store default must remain TCP.");
Console.WriteLine("PASS USB/TCP/Quick use the accepted Bridge carrier identities");

BridgeNetworkPakCarrierTransitionPlan activeChange = Plan(
    BridgeNetworkPakTransport.Tcp, BridgeNetworkPakTransport.Tcp,
    BridgeNetworkPakTransport.ReliableUdpQuick);
Require(activeChange is
    {
        ReleaseRequired: true,
        HandoffDelayRequired: true,
        SelectionRequired: true,
        PersistenceRequired: true,
        ActivationRequested: true,
        ActivationRetry: false,
        CommitBoundary: BridgeNetworkPakCarrierCommitBoundary.BeforeRelease,
        CancellationPolicy: BridgeNetworkPakCarrierCancellationPolicy.BeforeCommitOnly
    } && activeChange.HandoffDelay == TimeSpan.FromMilliseconds(50),
    "A different active selection did not preserve release/handoff/select/persist/activate intent.");
Console.WriteLine("PASS different active selection plans release and 50 ms handoff before activation");

BridgeNetworkPakCarrierTransitionPlan inactiveChange = Plan(
    BridgeNetworkPakTransport.Tcp, BridgeNetworkPakTransport.None,
    BridgeNetworkPakTransport.UsbSerial);
Require(inactiveChange is
    {
        ReleaseRequired: false,
        HandoffDelayRequired: false,
        SelectionRequired: true,
        PersistenceRequired: true,
        ActivationRequested: true,
        CommitBoundary: BridgeNetworkPakCarrierCommitBoundary.BeforeSelectionMutation
    }, "An inactive different selection planned the wrong lifecycle.");
Console.WriteLine("PASS inactive different selection plans select/persist/activate without release");

BridgeNetworkPakCarrierTransitionPlan sameActive = Plan(
    BridgeNetworkPakTransport.Tcp, BridgeNetworkPakTransport.Tcp,
    BridgeNetworkPakTransport.Tcp);
Require(sameActive.IsNoOp,
    "The same selected and active carrier must be a no-op.");
Console.WriteLine("PASS same selection plus same active carrier is a no-op");

BridgeNetworkPakCarrierTransitionPlan retry = Plan(
    BridgeNetworkPakTransport.ReliableUdpQuick, BridgeNetworkPakTransport.None,
    BridgeNetworkPakTransport.ReliableUdpQuick);
Require(!retry.IsNoOp && retry is
    {
        SelectionRequired: false,
        PersistenceRequired: false,
        ActivationRequested: true,
        ActivationRetry: true,
        CommitBoundary: BridgeNetworkPakCarrierCommitBoundary.BeforeActivation,
        CancellationPolicy: BridgeNetworkPakCarrierCancellationPolicy.BeforeCommitOnly
    }, "Same-selection inactive recovery did not become a deliberate activation retry.");
Console.WriteLine("PASS same selection plus inactive carrier requests deliberate activation retry");

BridgeNetworkPakCarrierTransitionPlan inactiveNotRequired = Plan(
    BridgeNetworkPakTransport.ReliableUdpQuick, BridgeNetworkPakTransport.None,
    BridgeNetworkPakTransport.ReliableUdpQuick, activationRequired: false);
Require(inactiveNotRequired.IsNoOp,
    "Inactive same selection must be a no-op when Network Pak does not require activation.");
Console.WriteLine("PASS same selection plus inactive carrier is a no-op when activation is unnecessary");

BridgeNetworkPakCarrierTransitionPlan selectionOnly = Plan(
    BridgeNetworkPakTransport.Tcp, BridgeNetworkPakTransport.None,
    BridgeNetworkPakTransport.ReliableUdpQuick, activationRequired: false);
Require(selectionOnly is
    {
        SelectionRequired: true,
        PersistenceRequired: true,
        ActivationRequested: false
    }, "Selection-only intent was not preserved while activation was unnecessary.");
Console.WriteLine("PASS selection may be persisted without activating a carrier");

BridgeNetworkPakCarrierTransitionPlan unavailableQuick = Plan(
    BridgeNetworkPakTransport.ReliableUdpQuick, BridgeNetworkPakTransport.None,
    BridgeNetworkPakTransport.ReliableUdpQuick, available: false);
Require(unavailableQuick.Requested == BridgeNetworkPakTransport.ReliableUdpQuick &&
        unavailableQuick.CurrentSelected == BridgeNetworkPakTransport.ReliableUdpQuick &&
        unavailableQuick.CurrentActive == BridgeNetworkPakTransport.None &&
        unavailableQuick.ActivationRequested && !unavailableQuick.CarrierAvailable,
    "Unavailable Quick intent was replaced or silently redirected.");
Require(!BridgeNetworkPakCarrierTransitionPolicy.AvailableCarriers.Any(item =>
        item.Carrier == BridgeNetworkPakTransport.None),
    "None was exposed as a fallback carrier.");
Console.WriteLine("PASS unavailable Quick remains selected with no automatic fallback");

BridgeNetworkPakCarrierSnapshot quickActivationFailure = new(
    BridgeNetworkPakTransport.ReliableUdpQuick,
    BridgeNetworkPakTransport.None,
    BridgeNetworkPakCarrierTransitionPhase.Failed,
    BridgeNetworkPakTransport.ReliableUdpQuick,
    BridgeNetworkPakCarrierPersistenceState.Confirmed,
    BridgeNetworkPakCarrierOutcome.Failed,
    BridgeNetworkPakCarrierFailure.ActivationFailed,
    "synthetic activation diagnostic");
Require(quickActivationFailure.SelectedCarrier ==
            BridgeNetworkPakTransport.ReliableUdpQuick &&
        quickActivationFailure.ActiveCarrier == BridgeNetworkPakTransport.None &&
        quickActivationFailure.Failure ==
            BridgeNetworkPakCarrierFailure.ActivationFailed,
    "Quick activation failure could not retain selected intent without claiming active.");
Console.WriteLine("PASS Quick activation failure remains selected Quick and active None");

Require(BridgeNetworkPakCarrierTransitionPolicy.RequiresAcknowledgedHandshake(
            BridgeNetworkPakTransport.UsbSerial) &&
        BridgeNetworkPakCarrierTransitionPolicy.RequiresAcknowledgedHandshake(
            BridgeNetworkPakTransport.Tcp) &&
        !BridgeNetworkPakCarrierTransitionPolicy.RequiresAcknowledgedHandshake(
            BridgeNetworkPakTransport.ReliableUdpQuick) &&
        BridgeNetworkPakCarrierTransitionPolicy.PhaseAfterActivationInitiated(
            BridgeNetworkPakTransport.Tcp) ==
            BridgeNetworkPakCarrierTransitionPhase.AwaitingHandshake &&
        BridgeNetworkPakCarrierTransitionPolicy.PhaseAfterActivationInitiated(
            BridgeNetworkPakTransport.ReliableUdpQuick) ==
            BridgeNetworkPakCarrierTransitionPhase.Activating,
    "Line and Quick activation completion semantics were conflated.");
Console.WriteLine("PASS line activation awaits ACK while Quick awaits negotiated-session success");

BridgeNetworkPakCarrierSnapshot persistenceFailure = new(
    BridgeNetworkPakTransport.ReliableUdpQuick,
    BridgeNetworkPakTransport.None,
    BridgeNetworkPakCarrierTransitionPhase.Failed,
    BridgeNetworkPakTransport.ReliableUdpQuick,
    BridgeNetworkPakCarrierPersistenceState.FailedUnknown,
    BridgeNetworkPakCarrierOutcome.Failed,
    BridgeNetworkPakCarrierFailure.PersistenceFailed,
    "synthetic persistence diagnostic");
Require(persistenceFailure.SelectedCarrier == BridgeNetworkPakTransport.ReliableUdpQuick &&
        persistenceFailure.ActiveCarrier == BridgeNetworkPakTransport.None &&
        persistenceFailure.PersistenceState ==
            BridgeNetworkPakCarrierPersistenceState.FailedUnknown,
    "Persistence failure could not represent new selected intent with unknown disk truth.");
Console.WriteLine("PASS persistence failure preserves truthful selected/active/disk state");

BridgeNetworkPakCarrierTransitionPlan invalid = Plan(
    BridgeNetworkPakTransport.Tcp, BridgeNetworkPakTransport.Tcp,
    BridgeNetworkPakTransport.None);
Require(!invalid.IsValid && invalid.Failure ==
        BridgeNetworkPakCarrierFailure.InvalidCarrier &&
        invalid.CommitBoundary == BridgeNetworkPakCarrierCommitBoundary.None,
    "Invalid carrier input reached a commit boundary.");
Console.WriteLine("PASS invalid input fails before commit");

Require(activeChange.CancellationPolicy ==
            BridgeNetworkPakCarrierCancellationPolicy.BeforeCommitOnly &&
        inactiveChange.CancellationPolicy ==
            BridgeNetworkPakCarrierCancellationPolicy.BeforeCommitOnly &&
        retry.CancellationPolicy ==
            BridgeNetworkPakCarrierCancellationPolicy.BeforeCommitOnly,
    "Actionable plans did not expose BeforeCommitOnly cancellation semantics.");
Console.WriteLine("PASS release, selection mutation, and activation retry have explicit commit boundaries");

Console.WriteLine("ALL BRIDGE NETWORK PAK CARRIER CONTRACT TESTS PASSED");
