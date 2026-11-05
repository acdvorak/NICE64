namespace PicoSwitchController;

public enum BridgeNetworkPakCarrierTransitionPhase
{
    Idle,
    ReleasingPrevious,
    HandoffDelay,
    Selecting,
    Persisting,
    Activating,
    AwaitingHandshake,
    Active,
    Failed
}

public enum BridgeNetworkPakCarrierOutcome
{
    None,
    NoOp,
    SelectionOnly,
    ActivationPendingHandshake,
    Active,
    Failed
}

public enum BridgeNetworkPakCarrierFailure
{
    None,
    Cancelled,
    InvalidCarrier,
    CarrierUnavailable,
    PrerequisiteNotMet,
    ReleaseFailed,
    PersistenceFailed,
    ActivationFailed,
    HandshakeFailed,
    TransportFailed,
    Faulted
}

public enum BridgeNetworkPakCarrierPersistenceState
{
    Unverified,
    Confirmed,
    FailedUnknown
}

public enum BridgeNetworkPakCarrierAvailability
{
    Unknown,
    Available,
    Unavailable
}

public enum BridgeNetworkPakCarrierCommitBoundary
{
    None,
    BeforeRelease,
    BeforeSelectionMutation,
    BeforeActivation
}

public enum BridgeNetworkPakCarrierCancellationPolicy
{
    None,
    BeforeCommitOnly
}

public sealed record BridgeNetworkPakCarrierDescriptor(
    BridgeNetworkPakTransport Carrier,
    bool Supported,
    bool LocallySelectable,
    bool RemoteUseRecommended);

public sealed record BridgeNetworkPakCarrierSnapshot(
    BridgeNetworkPakTransport SelectedCarrier,
    BridgeNetworkPakTransport ActiveCarrier,
    BridgeNetworkPakCarrierTransitionPhase TransitionPhase,
    BridgeNetworkPakTransport TransitionTarget,
    BridgeNetworkPakCarrierPersistenceState PersistenceState,
    BridgeNetworkPakCarrierOutcome LastOutcome,
    BridgeNetworkPakCarrierFailure Failure,
    string? Diagnostic)
{
    public static BridgeNetworkPakCarrierSnapshot Capture(
        IBridgeDeviceStateProvider deviceState,
        BridgeNetworkPakCarrierTransitionPhase transitionPhase =
            BridgeNetworkPakCarrierTransitionPhase.Idle,
        BridgeNetworkPakTransport transitionTarget = BridgeNetworkPakTransport.None,
        BridgeNetworkPakCarrierPersistenceState persistenceState =
            BridgeNetworkPakCarrierPersistenceState.Unverified,
        BridgeNetworkPakCarrierOutcome lastOutcome = BridgeNetworkPakCarrierOutcome.None,
        BridgeNetworkPakCarrierFailure failure = BridgeNetworkPakCarrierFailure.None,
        string? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(deviceState);
        BridgeDeviceStateSnapshot authoritative = deviceState.Current;
        return new(authoritative.SelectedNetworkPakTransport,
            authoritative.ActiveNetworkPakTransport, transitionPhase, transitionTarget,
            persistenceState, lastOutcome, failure, diagnostic);
    }
}

public sealed record BridgeNetworkPakCarrierResult(
    bool Success,
    bool Committed,
    BridgeNetworkPakCarrierOutcome Outcome,
    BridgeNetworkPakCarrierFailure Failure,
    string? Diagnostic,
    BridgeNetworkPakCarrierSnapshot Snapshot);

public sealed record BridgeNetworkPakCarrierSelectionLoadResult(
    bool Success,
    BridgeNetworkPakTransport Selection,
    bool UsedDefault,
    BridgeNetworkPakCarrierFailure Failure,
    string? Diagnostic);

public sealed record BridgeNetworkPakCarrierSelectionSaveResult(
    bool Success,
    BridgeNetworkPakTransport Selection,
    BridgeNetworkPakCarrierFailure Failure,
    string? Diagnostic);

public interface IBridgeNetworkPakCarrierSelectionStore
{
    ValueTask<BridgeNetworkPakCarrierSelectionLoadResult> LoadAsync(
        CancellationToken cancellationToken = default);

    ValueTask<BridgeNetworkPakCarrierSelectionSaveResult> SaveAsync(
        BridgeNetworkPakTransport selection,
        CancellationToken cancellationToken = default);
}

public static class BridgeNetworkPakCarrierSelectionDefaults
{
    public const BridgeNetworkPakTransport Selection = BridgeNetworkPakTransport.Tcp;
}

public sealed record BridgeNetworkPakConnectionPolicy(
    BridgeControlConnectionKind ConnectionKind,
    bool Connected,
    BridgeNetworkPakTransport DefaultCarrier,
    IReadOnlySet<BridgeNetworkPakTransport> AllowedCarriers)
{
    public bool IsAllowed(BridgeNetworkPakTransport carrier) =>
        Connected && AllowedCarriers.Contains(carrier);

    public BridgeNetworkPakTransport Normalize(BridgeNetworkPakTransport carrier) =>
        !Connected || IsAllowed(carrier) ||
        !BridgeNetworkPakCarrierTransitionPolicy.IsSelectable(carrier)
            ? carrier : DefaultCarrier;
}

public static class BridgeNetworkPakConnectionPolicyResolver
{
    private static readonly IReadOnlySet<BridgeNetworkPakTransport> SerialCarriers =
        new HashSet<BridgeNetworkPakTransport>
        {
            BridgeNetworkPakTransport.UsbSerial
        };

    private static readonly IReadOnlySet<BridgeNetworkPakTransport> WirelessCarriers =
        new HashSet<BridgeNetworkPakTransport>
        {
            BridgeNetworkPakTransport.Tcp,
            BridgeNetworkPakTransport.ReliableUdpQuick
        };

    private static readonly IReadOnlySet<BridgeNetworkPakTransport> NoCarriers =
        new HashSet<BridgeNetworkPakTransport>();

    public static BridgeNetworkPakConnectionPolicy Resolve(
        BridgeControlConnectionKind connectionKind,
        BridgeConnectionState connectionState) =>
        connectionState != BridgeConnectionState.Connected
            ? new(connectionKind, false, BridgeNetworkPakTransport.None, NoCarriers)
            : connectionKind switch
            {
                BridgeControlConnectionKind.SerialUsb => new(connectionKind, true,
                    BridgeNetworkPakTransport.UsbSerial, SerialCarriers),
                BridgeControlConnectionKind.WifiTcp => new(connectionKind, true,
                    BridgeNetworkPakTransport.Tcp, WirelessCarriers),
                _ => new(connectionKind, false, BridgeNetworkPakTransport.None,
                    NoCarriers)
            };

    public static BridgeNetworkPakConnectionPolicy Resolve(
        BridgeDeviceStateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Resolve(state.ControlConnectionKind, state.ControlConnectionState);
    }
}

public static class BridgeNetworkPakActivationRefreshPolicy
{
    public static bool RequiresAccessoryRetry(BridgeDeviceStateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.ControlConnectionState == BridgeConnectionState.Connected &&
            state.ActiveAccessory != BridgeAccessoryMode.NetworkPak &&
            state.AccessoryTransition.Phase == BridgeAccessoryTransitionPhase.Failed &&
            state.AccessoryTransition.Target == BridgeAccessoryMode.NetworkPak;
    }
}

public sealed record BridgeNetworkPakCarrierTransitionPlan(
    BridgeNetworkPakTransport CurrentSelected,
    BridgeNetworkPakTransport CurrentActive,
    BridgeNetworkPakTransport Requested,
    bool ReleaseRequired,
    bool HandoffDelayRequired,
    TimeSpan HandoffDelay,
    bool SelectionRequired,
    bool PersistenceRequired,
    bool ActivationRequested,
    bool ActivationRetry,
    BridgeNetworkPakCarrierAvailability Availability,
    BridgeNetworkPakCarrierCommitBoundary CommitBoundary,
    BridgeNetworkPakCarrierCancellationPolicy CancellationPolicy,
    BridgeNetworkPakCarrierFailure Failure)
{
    public bool IsValid => Failure == BridgeNetworkPakCarrierFailure.None;
    public bool IsNoOp => IsValid && !ReleaseRequired && !SelectionRequired &&
        !ActivationRequested;
    public bool CarrierAvailable =>
        Availability == BridgeNetworkPakCarrierAvailability.Available;
}

public static class BridgeNetworkPakCarrierTransitionPolicy
{
    public static readonly TimeSpan HandoffDelay = TimeSpan.FromMilliseconds(50);

    private static readonly IReadOnlyList<BridgeNetworkPakCarrierDescriptor> Descriptors =
    [
        new(BridgeNetworkPakTransport.UsbSerial, Supported: true,
            LocallySelectable: true, RemoteUseRecommended: false),
        new(BridgeNetworkPakTransport.Tcp, Supported: true,
            LocallySelectable: true, RemoteUseRecommended: true),
        new(BridgeNetworkPakTransport.ReliableUdpQuick, Supported: true,
            LocallySelectable: true, RemoteUseRecommended: true)
    ];

    public static IReadOnlyList<BridgeNetworkPakCarrierDescriptor> AvailableCarriers =>
        Descriptors;

    public static BridgeNetworkPakCarrierTransitionPlan Plan(
        BridgeNetworkPakTransport currentSelected,
        BridgeNetworkPakTransport currentActive,
        BridgeNetworkPakTransport requested,
        bool activationRequired,
        bool carrierAvailable)
    {
        if (!IsSelectable(requested))
        {
            return new(currentSelected, currentActive, requested,
                ReleaseRequired: false, HandoffDelayRequired: false, TimeSpan.Zero,
                SelectionRequired: false, PersistenceRequired: false,
                ActivationRequested: false, ActivationRetry: false,
                carrierAvailable
                    ? BridgeNetworkPakCarrierAvailability.Available
                    : BridgeNetworkPakCarrierAvailability.Unavailable,
                BridgeNetworkPakCarrierCommitBoundary.None,
                BridgeNetworkPakCarrierCancellationPolicy.None,
                BridgeNetworkPakCarrierFailure.InvalidCarrier);
        }

        bool selectionRequired = currentSelected != requested;
        bool releaseRequired = currentActive != BridgeNetworkPakTransport.None &&
            currentActive != requested;
        bool activationRequested = activationRequired && currentActive != requested;
        bool activationRetry = !selectionRequired &&
            currentActive == BridgeNetworkPakTransport.None && activationRequested;

        BridgeNetworkPakCarrierCommitBoundary commitBoundary = releaseRequired
            ? BridgeNetworkPakCarrierCommitBoundary.BeforeRelease
            : selectionRequired
                ? BridgeNetworkPakCarrierCommitBoundary.BeforeSelectionMutation
                : activationRequested
                    ? BridgeNetworkPakCarrierCommitBoundary.BeforeActivation
                    : BridgeNetworkPakCarrierCommitBoundary.None;

        return new(currentSelected, currentActive, requested,
            releaseRequired, releaseRequired,
            releaseRequired ? HandoffDelay : TimeSpan.Zero,
            selectionRequired, selectionRequired, activationRequested, activationRetry,
            carrierAvailable
                ? BridgeNetworkPakCarrierAvailability.Available
                : BridgeNetworkPakCarrierAvailability.Unavailable,
            commitBoundary,
            commitBoundary == BridgeNetworkPakCarrierCommitBoundary.None
                ? BridgeNetworkPakCarrierCancellationPolicy.None
                : BridgeNetworkPakCarrierCancellationPolicy.BeforeCommitOnly,
            BridgeNetworkPakCarrierFailure.None);
    }

    public static bool IsSelectable(BridgeNetworkPakTransport carrier) => carrier is
        BridgeNetworkPakTransport.UsbSerial or
        BridgeNetworkPakTransport.Tcp or
        BridgeNetworkPakTransport.ReliableUdpQuick;

    public static bool RequiresAcknowledgedHandshake(
        BridgeNetworkPakTransport carrier) => carrier is
        BridgeNetworkPakTransport.UsbSerial or BridgeNetworkPakTransport.Tcp;

    public static BridgeNetworkPakCarrierTransitionPhase PhaseAfterActivationInitiated(
        BridgeNetworkPakTransport carrier) => RequiresAcknowledgedHandshake(carrier)
            ? BridgeNetworkPakCarrierTransitionPhase.AwaitingHandshake
            : BridgeNetworkPakCarrierTransitionPhase.Activating;

    public static BridgeNetworkPakCarrierTransitionPhase PhaseAfterActivationSucceeded(
        BridgeNetworkPakTransport carrier) => IsSelectable(carrier)
            ? BridgeNetworkPakCarrierTransitionPhase.Active
            : BridgeNetworkPakCarrierTransitionPhase.Failed;
}
