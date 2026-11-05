namespace PicoSwitchController;

public sealed record BridgeNetworkPakCarrierPortResult(
    bool Success,
    BridgeNetworkPakCarrierFailure Failure,
    string? Diagnostic)
{
    public static BridgeNetworkPakCarrierPortResult Succeeded { get; } =
        new(true, BridgeNetworkPakCarrierFailure.None, null);
}

public interface IBridgeNetworkPakCarrierStatePort : IBridgeDeviceStateProvider
{
    void SelectCarrier(BridgeNetworkPakTransport carrier);
    void ActivateCarrier(BridgeNetworkPakTransport carrier);
    void DeactivateCarrier(BridgeNetworkPakTransport carrier);
}

public interface IBridgeNetworkPakCarrierActivationContext
{
    bool ActivationRequired { get; }
}

public interface IBridgeNetworkPakLineCarrierPort
{
    bool IsAvailable(BridgeNetworkPakTransport carrier);

    ValueTask<BridgeNetworkPakCarrierPortResult> BeginActivationAsync(
        BridgeNetworkPakTransport carrier,
        long generation,
        CancellationToken cancellationToken = default);

    ValueTask<BridgeNetworkPakCarrierPortResult> ReleaseAsync(
        BridgeNetworkPakTransport carrier,
        CancellationToken cancellationToken = default);
}

public interface IBridgeNetworkPakQuickCarrierPort
{
    bool IsAvailable { get; }

    ValueTask<BridgeNetworkPakCarrierPortResult> StartAsync(
        long generation,
        CancellationToken cancellationToken = default);

    ValueTask<BridgeNetworkPakCarrierPortResult> StopAsync(
        long generation,
        CancellationToken cancellationToken = default);
}

public interface IBridgeNetworkPakApplicationOwnerPort
{
    void SetConnected(bool connected);
    void SetHandshakeComplete(bool complete);
}

public interface IBridgeNetworkPakCarrierDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public interface IBridgeNetworkPakCarrierService
{
    BridgeNetworkPakCarrierSnapshot Current { get; }
    long? PendingLineActivationGeneration { get; }
    long? ActiveQuickGeneration { get; }

    Task<BridgeNetworkPakCarrierResult> InitializeAsync(
        CancellationToken cancellationToken = default);

    Task<BridgeNetworkPakCarrierResult> SelectCarrierAsync(
        BridgeNetworkPakTransport carrier,
        CancellationToken cancellationToken = default);

    Task<BridgeNetworkPakCarrierResult> ReconcileConnectionAsync(
        CancellationToken cancellationToken = default);

    Task<BridgeNetworkPakCarrierResult> ActivateSelectedAsync(
        CancellationToken cancellationToken = default);

    Task<BridgeNetworkPakCarrierResult> ReleaseActiveAsync(
        CancellationToken cancellationToken = default);

    Task<BridgeNetworkPakCarrierResult> ObserveLineHandshakeAcknowledgedAsync(
        BridgeNetworkPakTransport carrier,
        long generation,
        CancellationToken cancellationToken = default);

    Task<BridgeNetworkPakCarrierResult> ReportLineActivationFailureAsync(
        BridgeNetworkPakTransport carrier,
        long generation,
        BridgeNetworkPakCarrierFailure failure,
        string? diagnostic = null,
        CancellationToken cancellationToken = default);

    Task<BridgeNetworkPakCarrierResult> ReportQuickFaultAsync(
        long generation,
        string? diagnostic = null,
        CancellationToken cancellationToken = default);

    Task<BridgeNetworkPakCarrierResult> RetirePhysicalCarrierAsync(
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<BridgeNetworkPakCarrierResult> ConvergeDisconnectedAsync(
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<BridgeNetworkPakCarrierResult> ShutdownAsync(
        CancellationToken cancellationToken = default);
}

internal sealed class BridgeNetworkPakCarrierStatePort :
    IBridgeNetworkPakCarrierStatePort
{
    private readonly IBridgeDeviceStateProvider _provider;
    private readonly IBridgeDeviceStateUpdater _updater;

    internal BridgeNetworkPakCarrierStatePort(IBridgeDeviceStateProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _updater = provider as IBridgeDeviceStateUpdater
            ?? throw new ArgumentException(
                "The device-state provider does not expose semantic mutation authority.",
                nameof(provider));
    }

    public BridgeDeviceStateSnapshot Current => _provider.Current;

    public event EventHandler<BridgeDeviceStateChangedEventArgs>? StateChanged
    {
        add => _provider.StateChanged += value;
        remove => _provider.StateChanged -= value;
    }

    public void SelectCarrier(BridgeNetworkPakTransport carrier) =>
        _updater.SelectNetworkPakTransport(carrier);

    public void ActivateCarrier(BridgeNetworkPakTransport carrier) =>
        _updater.ActivateNetworkPakTransport(carrier);

    public void DeactivateCarrier(BridgeNetworkPakTransport carrier) =>
        _updater.DeactivateNetworkPakTransport(carrier);
}

public sealed class BridgeNetworkPakCarrierService : IBridgeNetworkPakCarrierService
{
    private sealed record TransitionMetadata(
        BridgeNetworkPakCarrierTransitionPhase Phase,
        BridgeNetworkPakTransport Target,
        BridgeNetworkPakCarrierPersistenceState Persistence,
        BridgeNetworkPakCarrierOutcome Outcome,
        BridgeNetworkPakCarrierFailure Failure,
        string? Diagnostic);

    private readonly IBridgeNetworkPakCarrierStatePort _state;
    private readonly IBridgeNetworkPakCarrierActivationContext _activationContext;
    private readonly IBridgeNetworkPakCarrierSelectionStore _store;
    private readonly IBridgeNetworkPakLineCarrierPort _line;
    private readonly IBridgeNetworkPakQuickCarrierPort _quick;
    private readonly IBridgeNetworkPakApplicationOwnerPort _application;
    private readonly IBridgeNetworkPakCarrierDelay _delay;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private TransitionMetadata _metadata = new(
        BridgeNetworkPakCarrierTransitionPhase.Idle,
        BridgeNetworkPakTransport.None,
        BridgeNetworkPakCarrierPersistenceState.Unverified,
        BridgeNetworkPakCarrierOutcome.None,
        BridgeNetworkPakCarrierFailure.None,
        null);
    private long _nextGeneration;
    private long _pendingLineGeneration;
    private BridgeNetworkPakTransport _pendingLineCarrier;
    private long _activeQuickGeneration;
    private long _retiredQuickGeneration;

    public BridgeNetworkPakCarrierService(
        IBridgeNetworkPakCarrierStatePort state,
        IBridgeNetworkPakCarrierActivationContext activationContext,
        IBridgeNetworkPakCarrierSelectionStore store,
        IBridgeNetworkPakLineCarrierPort line,
        IBridgeNetworkPakQuickCarrierPort quick,
        IBridgeNetworkPakApplicationOwnerPort application,
        IBridgeNetworkPakCarrierDelay delay)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _activationContext = activationContext ??
            throw new ArgumentNullException(nameof(activationContext));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _line = line ?? throw new ArgumentNullException(nameof(line));
        _quick = quick ?? throw new ArgumentNullException(nameof(quick));
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public BridgeNetworkPakCarrierSnapshot Current
    {
        get
        {
            TransitionMetadata metadata = Volatile.Read(ref _metadata);
            return BridgeNetworkPakCarrierSnapshot.Capture(_state, metadata.Phase,
                metadata.Target, metadata.Persistence, metadata.Outcome,
                metadata.Failure, metadata.Diagnostic);
        }
    }

    public long? PendingLineActivationGeneration
    {
        get
        {
            long generation = Interlocked.Read(ref _pendingLineGeneration);
            return generation == 0 ? null : generation;
        }
    }

    public long? ActiveQuickGeneration
    {
        get
        {
            long generation = Interlocked.Read(ref _activeQuickGeneration);
            return generation == 0 ? null : generation;
        }
    }

    public async Task<BridgeNetworkPakCarrierResult> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
            return CancelledBeforeCommit("Carrier initialization was cancelled.");

        try
        {
            BridgeNetworkPakCarrierSelectionLoadResult loaded;
            try
            {
                loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return CancelledBeforeCommit("Carrier initialization was cancelled.");
            }
            catch (Exception ex)
            {
                return CompleteFailure(false,
                    BridgeNetworkPakCarrierFailure.PersistenceFailed, ex.Message,
                    BridgeNetworkPakCarrierPersistenceState.FailedUnknown);
            }

            if (!loaded.Success)
            {
                return CompleteFailure(false, NormalizeFailure(loaded.Failure,
                        BridgeNetworkPakCarrierFailure.PersistenceFailed),
                    loaded.Diagnostic,
                    BridgeNetworkPakCarrierPersistenceState.FailedUnknown);
            }

            if (!BridgeNetworkPakCarrierTransitionPolicy.IsSelectable(loaded.Selection))
            {
                return CompleteFailure(false,
                    BridgeNetworkPakCarrierFailure.InvalidCarrier,
                    "The persisted Network Pak carrier is not selectable.",
                    BridgeNetworkPakCarrierPersistenceState.FailedUnknown);
            }

            if (cancellationToken.IsCancellationRequested)
                return CancelledBeforeCommit("Carrier initialization was cancelled.");

            SetMetadata(BridgeNetworkPakCarrierTransitionPhase.Selecting,
                loaded.Selection, BridgeNetworkPakCarrierPersistenceState.Confirmed,
                BridgeNetworkPakCarrierOutcome.None,
                BridgeNetworkPakCarrierFailure.None, null);
            _state.SelectCarrier(loaded.Selection);
            return CompleteSuccess(true, BridgeNetworkPakCarrierOutcome.SelectionOnly,
                BridgeNetworkPakCarrierTransitionPhase.Idle,
                BridgeNetworkPakCarrierPersistenceState.Confirmed);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<BridgeNetworkPakCarrierResult> SelectCarrierAsync(
        BridgeNetworkPakTransport carrier,
        CancellationToken cancellationToken = default)
    {
        BridgeDeviceStateSnapshot beforeGate = _state.Current;
        BridgeNetworkPakConnectionPolicy connectionPolicy =
            BridgeNetworkPakConnectionPolicyResolver.Resolve(beforeGate);
        if (connectionPolicy.Connected)
            carrier = connectionPolicy.Normalize(carrier);

        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
            return CancelledBeforeCommit("Carrier selection was cancelled.");

        try
        {
            if (!BridgeNetworkPakCarrierTransitionPolicy.IsSelectable(carrier))
            {
                return ResultWithoutMutation(false, false,
                    BridgeNetworkPakCarrierOutcome.Failed,
                    BridgeNetworkPakCarrierFailure.InvalidCarrier,
                    "The requested Network Pak carrier is not selectable.");
            }

            BridgeDeviceStateSnapshot authoritative = _state.Current;
            bool committed = false;
            BridgeNetworkPakTransport pendingLine =
                Interlocked.Read(ref _pendingLineGeneration) == 0
                    ? BridgeNetworkPakTransport.None
                    : _pendingLineCarrier;
            if (pendingLine != BridgeNetworkPakTransport.None)
            {
                if (pendingLine == carrier &&
                    authoritative.SelectedNetworkPakTransport == carrier)
                {
                    return CompleteSuccess(false,
                        BridgeNetworkPakCarrierOutcome.ActivationPendingHandshake,
                        BridgeNetworkPakCarrierTransitionPhase.AwaitingHandshake);
                }

                if (cancellationToken.IsCancellationRequested)
                    return CancelledBeforeCommit("Carrier selection was cancelled.");

                committed = true;
                SetMetadata(BridgeNetworkPakCarrierTransitionPhase.ReleasingPrevious,
                    carrier);
                BridgeNetworkPakCarrierPortResult pendingReleased =
                    await ReleasePhysicalAsync(pendingLine).ConfigureAwait(false);
                if (!pendingReleased.Success)
                {
                    return CompleteFailure(true,
                        BridgeNetworkPakCarrierFailure.ReleaseFailed,
                        pendingReleased.Diagnostic);
                }

                ClearSemanticOwnership(pendingLine);
                SetMetadata(BridgeNetworkPakCarrierTransitionPhase.HandoffDelay, carrier);
                try
                {
                    await _delay.DelayAsync(
                            BridgeNetworkPakCarrierTransitionPolicy.HandoffDelay,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return CompleteFailure(true,
                        BridgeNetworkPakCarrierFailure.TransportFailed, ex.Message);
                }

                authoritative = _state.Current;
            }

            long retiredQuick = Interlocked.Read(ref _retiredQuickGeneration);
            if (retiredQuick > 0 &&
                carrier != BridgeNetworkPakTransport.ReliableUdpQuick)
            {
                if (!committed && cancellationToken.IsCancellationRequested)
                    return CancelledBeforeCommit("Carrier selection was cancelled.");

                committed = true;
                SetMetadata(BridgeNetworkPakCarrierTransitionPhase.ReleasingPrevious,
                    carrier);
                BridgeNetworkPakCarrierPortResult retiredReleased =
                    await ReleasePhysicalAsync(
                            BridgeNetworkPakTransport.ReliableUdpQuick)
                        .ConfigureAwait(false);
                if (!retiredReleased.Success)
                {
                    return CompleteFailure(true,
                        BridgeNetworkPakCarrierFailure.ReleaseFailed,
                        retiredReleased.Diagnostic);
                }

                ClearSemanticOwnership(
                    BridgeNetworkPakTransport.ReliableUdpQuick);
                SetMetadata(BridgeNetworkPakCarrierTransitionPhase.HandoffDelay, carrier);
                try
                {
                    await _delay.DelayAsync(
                            BridgeNetworkPakCarrierTransitionPolicy.HandoffDelay,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return CompleteFailure(true,
                        BridgeNetworkPakCarrierFailure.TransportFailed, ex.Message);
                }

                authoritative = _state.Current;
            }

            bool available = IsAvailable(carrier);
            BridgeNetworkPakCarrierTransitionPlan plan =
                BridgeNetworkPakCarrierTransitionPolicy.Plan(
                    authoritative.SelectedNetworkPakTransport,
                    authoritative.ActiveNetworkPakTransport,
                    carrier,
                    _activationContext.ActivationRequired,
                    available);

            if (!plan.IsValid)
            {
                return ResultWithoutMutation(false, false,
                    BridgeNetworkPakCarrierOutcome.Failed, plan.Failure,
                    "The requested Network Pak carrier is not selectable.");
            }

            if (plan.IsNoOp)
            {
                BridgeNetworkPakCarrierTransitionPhase phase =
                    authoritative.ActiveNetworkPakTransport == carrier
                        ? BridgeNetworkPakCarrierTransitionPhase.Active
                        : BridgeNetworkPakCarrierTransitionPhase.Idle;
                return CompleteSuccess(false, BridgeNetworkPakCarrierOutcome.NoOp, phase);
            }

            if (!committed && cancellationToken.IsCancellationRequested)
                return CancelledBeforeCommit("Carrier selection was cancelled.");

            committed = true;
            if (plan.ReleaseRequired)
            {
                SetMetadata(BridgeNetworkPakCarrierTransitionPhase.ReleasingPrevious,
                    carrier);
                BridgeNetworkPakCarrierPortResult released =
                    await ReleasePhysicalAsync(authoritative.ActiveNetworkPakTransport)
                        .ConfigureAwait(false);
                if (!released.Success)
                {
                    return CompleteFailure(committed,
                        BridgeNetworkPakCarrierFailure.ReleaseFailed,
                        released.Diagnostic);
                }

                ClearSemanticOwnership(authoritative.ActiveNetworkPakTransport);

                SetMetadata(BridgeNetworkPakCarrierTransitionPhase.HandoffDelay, carrier);
                try
                {
                    await _delay.DelayAsync(plan.HandoffDelay, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return CompleteFailure(committed,
                        BridgeNetworkPakCarrierFailure.TransportFailed, ex.Message);
                }
            }

            if (plan.SelectionRequired)
            {
                SetMetadata(BridgeNetworkPakCarrierTransitionPhase.Selecting, carrier);
                try
                {
                    _state.SelectCarrier(carrier);
                }
                catch (Exception ex)
                {
                    ClearApplicationOwnership();
                    return CompleteFailure(committed,
                        BridgeNetworkPakCarrierFailure.TransportFailed, ex.Message);
                }

                SetMetadata(BridgeNetworkPakCarrierTransitionPhase.Persisting, carrier);
                BridgeNetworkPakCarrierSelectionSaveResult saved =
                    await SaveSelectionAsync(carrier).ConfigureAwait(false);
                if (!saved.Success || saved.Selection != carrier)
                {
                    ClearApplicationOwnership();
                    return CompleteFailure(committed,
                        BridgeNetworkPakCarrierFailure.PersistenceFailed,
                        saved.Diagnostic,
                        BridgeNetworkPakCarrierPersistenceState.FailedUnknown);
                }

                SetMetadata(BridgeNetworkPakCarrierTransitionPhase.Selecting, carrier,
                    BridgeNetworkPakCarrierPersistenceState.Confirmed);
            }

            if (!plan.ActivationRequested)
            {
                return CompleteSuccess(committed,
                    _state.Current.ActiveNetworkPakTransport == carrier
                        ? BridgeNetworkPakCarrierOutcome.Active
                        : BridgeNetworkPakCarrierOutcome.SelectionOnly,
                    _state.Current.ActiveNetworkPakTransport == carrier
                        ? BridgeNetworkPakCarrierTransitionPhase.Active
                        : BridgeNetworkPakCarrierTransitionPhase.Idle,
                    plan.PersistenceRequired
                        ? BridgeNetworkPakCarrierPersistenceState.Confirmed
                        : null);
            }

            return await ActivateCarrierCommittedAsync(carrier, committed)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<BridgeNetworkPakCarrierResult> ReconcileConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        BridgeDeviceStateSnapshot authoritative = _state.Current;
        BridgeNetworkPakConnectionPolicy policy =
            BridgeNetworkPakConnectionPolicyResolver.Resolve(authoritative);
        if (!policy.Connected)
        {
            return Task.FromResult(ResultWithoutMutation(true, false,
                BridgeNetworkPakCarrierOutcome.NoOp,
                BridgeNetworkPakCarrierFailure.None,
                "Network Pak carrier policy is waiting for a control connection."));
        }

        BridgeNetworkPakTransport normalized = policy.Normalize(
            authoritative.SelectedNetworkPakTransport);
        return SelectCarrierAsync(normalized, cancellationToken);
    }

    public async Task<BridgeNetworkPakCarrierResult> ActivateSelectedAsync(
        CancellationToken cancellationToken = default)
    {
        BridgeDeviceStateSnapshot beforeGate = _state.Current;
        BridgeNetworkPakConnectionPolicy connectionPolicy =
            BridgeNetworkPakConnectionPolicyResolver.Resolve(beforeGate);
        if (connectionPolicy.Connected)
        {
            BridgeNetworkPakTransport normalized = connectionPolicy.Normalize(
                beforeGate.SelectedNetworkPakTransport);
            if (normalized != beforeGate.SelectedNetworkPakTransport)
            {
                BridgeNetworkPakCarrierResult correction =
                    await SelectCarrierAsync(normalized, cancellationToken)
                        .ConfigureAwait(false);
                if (!correction.Success) return correction;
            }
        }

        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
            return CancelledBeforeCommit("Carrier activation was cancelled.");

        try
        {
            BridgeDeviceStateSnapshot authoritative = _state.Current;
            BridgeNetworkPakTransport selected =
                authoritative.SelectedNetworkPakTransport;
            if (!BridgeNetworkPakCarrierTransitionPolicy.IsSelectable(selected))
            {
                return ResultWithoutMutation(false, false,
                    BridgeNetworkPakCarrierOutcome.Failed,
                    BridgeNetworkPakCarrierFailure.InvalidCarrier,
                    "No selectable Network Pak carrier is selected.");
            }

            if (authoritative.ActiveNetworkPakTransport == selected)
            {
                return CompleteSuccess(false, BridgeNetworkPakCarrierOutcome.NoOp,
                    BridgeNetworkPakCarrierTransitionPhase.Active);
            }

            if (IsExpectedLineGeneration(selected,
                    Interlocked.Read(ref _pendingLineGeneration)))
            {
                return CompleteSuccess(false,
                    BridgeNetworkPakCarrierOutcome.ActivationPendingHandshake,
                    BridgeNetworkPakCarrierTransitionPhase.AwaitingHandshake);
            }

            if (cancellationToken.IsCancellationRequested)
                return CancelledBeforeCommit("Carrier activation was cancelled.");

            return await ActivateCarrierCommittedAsync(selected, committed: true)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<BridgeNetworkPakCarrierResult> ReleaseActiveAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
            return CancelledBeforeCommit("Carrier release was cancelled.");

        try
        {
            BridgeNetworkPakTransport owned = ActiveOrPendingCarrier();
            if (owned == BridgeNetworkPakTransport.None)
            {
                ClearApplicationOwnership();
                return CompleteSuccess(false, BridgeNetworkPakCarrierOutcome.NoOp,
                    BridgeNetworkPakCarrierTransitionPhase.Idle);
            }

            if (cancellationToken.IsCancellationRequested)
                return CancelledBeforeCommit("Carrier release was cancelled.");

            SetMetadata(BridgeNetworkPakCarrierTransitionPhase.ReleasingPrevious, owned);
            BridgeNetworkPakCarrierPortResult released =
                await ReleasePhysicalAsync(owned).ConfigureAwait(false);
            if (!released.Success)
            {
                return CompleteFailure(true,
                    BridgeNetworkPakCarrierFailure.ReleaseFailed,
                    released.Diagnostic);
            }

            ClearSemanticOwnership(owned);
            return CompleteSuccess(true, BridgeNetworkPakCarrierOutcome.SelectionOnly,
                BridgeNetworkPakCarrierTransitionPhase.Idle);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<BridgeNetworkPakCarrierResult>
        ObserveLineHandshakeAcknowledgedAsync(
            BridgeNetworkPakTransport carrier,
            long generation,
            CancellationToken cancellationToken = default)
    {
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
            return CancelledBeforeCommit("Line handshake observation was cancelled.");

        try
        {
            if (!IsExpectedLineGeneration(carrier, generation) ||
                _state.Current.SelectedNetworkPakTransport != carrier)
            {
                return ResultWithoutMutation(false, false,
                    BridgeNetworkPakCarrierOutcome.Failed,
                    BridgeNetworkPakCarrierFailure.HandshakeFailed,
                    "The line handshake acknowledgement is stale or belongs to another carrier.");
            }

            try
            {
                _application.SetConnected(true);
                _application.SetHandshakeComplete(true);
                _state.ActivateCarrier(carrier);
                ClearPendingLine();
                return CompleteSuccess(true, BridgeNetworkPakCarrierOutcome.Active,
                    BridgeNetworkPakCarrierTransitionPhase.Active);
            }
            catch (Exception ex)
            {
                ClearSemanticOwnership(carrier);
                return CompleteFailure(true,
                    BridgeNetworkPakCarrierFailure.TransportFailed, ex.Message);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<BridgeNetworkPakCarrierResult> ReportLineActivationFailureAsync(
        BridgeNetworkPakTransport carrier,
        long generation,
        BridgeNetworkPakCarrierFailure failure,
        string? diagnostic = null,
        CancellationToken cancellationToken = default)
    {
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
            return CancelledBeforeCommit("Line failure reporting was cancelled.");

        try
        {
            if (!IsExpectedLineGeneration(carrier, generation))
            {
                return ResultWithoutMutation(false, false,
                    BridgeNetworkPakCarrierOutcome.NoOp,
                    BridgeNetworkPakCarrierFailure.None, null);
            }

            BridgeNetworkPakCarrierFailure semanticFailure = failure is
                BridgeNetworkPakCarrierFailure.HandshakeFailed or
                BridgeNetworkPakCarrierFailure.TransportFailed or
                BridgeNetworkPakCarrierFailure.ActivationFailed or
                BridgeNetworkPakCarrierFailure.CarrierUnavailable
                    ? failure
                    : BridgeNetworkPakCarrierFailure.HandshakeFailed;

            try
            {
                await _line.ReleaseAsync(carrier, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The physical path already reported failure. Semantic retirement below
                // must still clear ownership rather than preserve a false active claim.
            }

            ClearSemanticOwnership(carrier);
            return CompleteFailure(true, semanticFailure, diagnostic);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<BridgeNetworkPakCarrierResult> ReportQuickFaultAsync(
        long generation,
        string? diagnostic = null,
        CancellationToken cancellationToken = default)
    {
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
            return CancelledBeforeCommit("Quick fault reporting was cancelled.");

        try
        {
            if (generation <= 0 || generation != _activeQuickGeneration ||
                _state.Current.ActiveNetworkPakTransport !=
                    BridgeNetworkPakTransport.ReliableUdpQuick)
            {
                return ResultWithoutMutation(true, false,
                    BridgeNetworkPakCarrierOutcome.NoOp,
                    BridgeNetworkPakCarrierFailure.None, null);
            }

            ClearApplicationOwnership();
            _state.DeactivateCarrier(
                BridgeNetworkPakTransport.ReliableUdpQuick);
            Interlocked.Exchange(ref _activeQuickGeneration, 0);
            Interlocked.Exchange(ref _retiredQuickGeneration, generation);
            return CompleteFailure(true, BridgeNetworkPakCarrierFailure.Faulted,
                diagnostic);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<BridgeNetworkPakCarrierResult> RetirePhysicalCarrierAsync(
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        RetirePhysicalCarrierCoreAsync(reason, cancellationToken);

    public async Task<BridgeNetworkPakCarrierResult> ConvergeDisconnectedAsync(
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
            return CancelledBeforeCommit("Disconnect convergence was cancelled.");

        try
        {
            BridgeNetworkPakTransport owned = OwnedCarrier();
            if (owned != BridgeNetworkPakTransport.None)
                _state.DeactivateCarrier(owned);
            ClearApplicationOwnership();
            if (owned == BridgeNetworkPakTransport.ReliableUdpQuick)
            {
                long activeGeneration =
                    Interlocked.Read(ref _activeQuickGeneration);
                if (activeGeneration > 0)
                {
                    Interlocked.Exchange(
                        ref _retiredQuickGeneration, activeGeneration);
                }
                Interlocked.Exchange(ref _activeQuickGeneration, 0);
            }
            ClearPendingLine();
            return CompleteSuccess(owned != BridgeNetworkPakTransport.None,
                owned == BridgeNetworkPakTransport.None
                    ? BridgeNetworkPakCarrierOutcome.NoOp
                    : BridgeNetworkPakCarrierOutcome.SelectionOnly,
                BridgeNetworkPakCarrierTransitionPhase.Idle,
                diagnostic: reason);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<BridgeNetworkPakCarrierResult> ShutdownAsync(
        CancellationToken cancellationToken = default) =>
        RetirePhysicalCarrierCoreAsync("Network Pak carrier shutdown.",
            cancellationToken);

    private async Task<BridgeNetworkPakCarrierResult> RetirePhysicalCarrierCoreAsync(
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
            return CancelledBeforeCommit("Carrier retirement was cancelled.");

        try
        {
            BridgeNetworkPakTransport owned = OwnedCarrier();
            if (owned == BridgeNetworkPakTransport.None)
            {
                ClearApplicationOwnership();
                ClearAllGenerations();
                return CompleteSuccess(false, BridgeNetworkPakCarrierOutcome.NoOp,
                    BridgeNetworkPakCarrierTransitionPhase.Idle, diagnostic: reason);
            }

            if (cancellationToken.IsCancellationRequested)
                return CancelledBeforeCommit("Carrier retirement was cancelled.");

            SetMetadata(BridgeNetworkPakCarrierTransitionPhase.ReleasingPrevious, owned);
            BridgeNetworkPakCarrierPortResult released =
                await ReleasePhysicalAsync(owned).ConfigureAwait(false);

            // Firmware replacement and shutdown are destructive convergence paths.
            // They must never preserve a semantic active claim after physical retirement.
            ClearSemanticOwnership(owned);
            if (!released.Success)
            {
                return CompleteFailure(true,
                    BridgeNetworkPakCarrierFailure.ReleaseFailed,
                    CombineDiagnostics(reason, released.Diagnostic));
            }

            return CompleteSuccess(true, BridgeNetworkPakCarrierOutcome.SelectionOnly,
                BridgeNetworkPakCarrierTransitionPhase.Idle, diagnostic: reason);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<BridgeNetworkPakCarrierResult> ActivateCarrierCommittedAsync(
        BridgeNetworkPakTransport carrier,
        bool committed)
    {
        if (!IsAvailable(carrier))
        {
            ClearApplicationOwnership();
            _state.DeactivateCarrier(carrier);
            return CompleteFailure(committed,
                BridgeNetworkPakCarrierFailure.CarrierUnavailable,
                "The selected Network Pak carrier is unavailable.");
        }

        long generation = checked(++_nextGeneration);
        SetMetadata(BridgeNetworkPakCarrierTransitionPhase.Activating, carrier);

        if (BridgeNetworkPakCarrierTransitionPolicy.RequiresAcknowledgedHandshake(carrier))
        {
            BridgeNetworkPakCarrierPortResult started;
            try
            {
                started = await _line.BeginActivationAsync(
                        carrier, generation, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                started = new(false, BridgeNetworkPakCarrierFailure.TransportFailed,
                    ex.Message);
            }

            if (!started.Success)
            {
                ClearApplicationOwnership();
                _state.DeactivateCarrier(carrier);
                return CompleteFailure(committed,
                    NormalizeActivationFailure(started.Failure), started.Diagnostic);
            }

            _pendingLineCarrier = carrier;
            Interlocked.Exchange(ref _pendingLineGeneration, generation);
            _application.SetConnected(true);
            _application.SetHandshakeComplete(false);
            return CompleteSuccess(committed,
                BridgeNetworkPakCarrierOutcome.ActivationPendingHandshake,
                BridgeNetworkPakCarrierTransitionPhase.AwaitingHandshake);
        }

        if (carrier != BridgeNetworkPakTransport.ReliableUdpQuick)
        {
            return CompleteFailure(committed,
                BridgeNetworkPakCarrierFailure.InvalidCarrier,
                "The selected Network Pak carrier cannot be activated.");
        }

        BridgeNetworkPakCarrierPortResult quickStarted;
        Interlocked.Exchange(ref _retiredQuickGeneration, 0);
        try
        {
            quickStarted = await _quick.StartAsync(generation, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            quickStarted = new(false,
                BridgeNetworkPakCarrierFailure.TransportFailed, ex.Message);
        }

        if (!quickStarted.Success)
        {
            ClearApplicationOwnership();
            _state.DeactivateCarrier(carrier);
            Interlocked.Exchange(ref _activeQuickGeneration, 0);
            Interlocked.Exchange(ref _retiredQuickGeneration, 0);
            return CompleteFailure(committed,
                NormalizeActivationFailure(quickStarted.Failure),
                quickStarted.Diagnostic);
        }

        Interlocked.Exchange(ref _activeQuickGeneration, generation);
        _application.SetConnected(true);
        _application.SetHandshakeComplete(true);
        _state.ActivateCarrier(carrier);
        return CompleteSuccess(committed, BridgeNetworkPakCarrierOutcome.Active,
            BridgeNetworkPakCarrierTransitionPhase.Active);
    }

    private async ValueTask<BridgeNetworkPakCarrierPortResult> ReleasePhysicalAsync(
        BridgeNetworkPakTransport carrier)
    {
        try
        {
            if (carrier == BridgeNetworkPakTransport.ReliableUdpQuick)
            {
                return await _quick.StopAsync(
                        CurrentOwnedQuickGeneration(),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (BridgeNetworkPakCarrierTransitionPolicy
                .RequiresAcknowledgedHandshake(carrier))
            {
                return await _line.ReleaseAsync(carrier, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return new(false, BridgeNetworkPakCarrierFailure.InvalidCarrier,
                "The owned Network Pak carrier is invalid.");
        }
        catch (Exception ex)
        {
            return new(false, BridgeNetworkPakCarrierFailure.ReleaseFailed, ex.Message);
        }
    }

    private async ValueTask<BridgeNetworkPakCarrierSelectionSaveResult>
        SaveSelectionAsync(BridgeNetworkPakTransport carrier)
    {
        try
        {
            return await _store.SaveAsync(carrier, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new(false, carrier,
                BridgeNetworkPakCarrierFailure.PersistenceFailed, ex.Message);
        }
    }

    private bool IsAvailable(BridgeNetworkPakTransport carrier)
    {
        try
        {
            return carrier switch
            {
                BridgeNetworkPakTransport.UsbSerial or BridgeNetworkPakTransport.Tcp =>
                    _line.IsAvailable(carrier),
                BridgeNetworkPakTransport.ReliableUdpQuick => _quick.IsAvailable,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private BridgeNetworkPakTransport OwnedCarrier()
    {
        BridgeNetworkPakTransport active = ActiveOrPendingCarrier();
        if (active != BridgeNetworkPakTransport.None) return active;
        if (Interlocked.Read(ref _retiredQuickGeneration) > 0)
            return BridgeNetworkPakTransport.ReliableUdpQuick;
        return BridgeNetworkPakTransport.None;
    }

    private BridgeNetworkPakTransport ActiveOrPendingCarrier()
    {
        BridgeNetworkPakTransport active = _state.Current.ActiveNetworkPakTransport;
        if (active != BridgeNetworkPakTransport.None) return active;
        return Interlocked.Read(ref _pendingLineGeneration) != 0
            ? _pendingLineCarrier
            : BridgeNetworkPakTransport.None;
    }

    private bool IsExpectedLineGeneration(
        BridgeNetworkPakTransport carrier,
        long generation) =>
        generation > 0 &&
        generation == Interlocked.Read(ref _pendingLineGeneration) &&
        carrier == _pendingLineCarrier;

    private void ClearSemanticOwnership(BridgeNetworkPakTransport carrier)
    {
        ClearApplicationOwnership();
        _state.DeactivateCarrier(carrier);
        if (carrier == BridgeNetworkPakTransport.ReliableUdpQuick)
        {
            Interlocked.Exchange(ref _activeQuickGeneration, 0);
            Interlocked.Exchange(ref _retiredQuickGeneration, 0);
        }
        if (carrier == _pendingLineCarrier)
            ClearPendingLine();
    }

    private void ClearApplicationOwnership()
    {
        _application.SetHandshakeComplete(false);
        _application.SetConnected(false);
    }

    private void ClearPendingLine()
    {
        _pendingLineCarrier = BridgeNetworkPakTransport.None;
        Interlocked.Exchange(ref _pendingLineGeneration, 0);
    }

    private void ClearAllGenerations()
    {
        ClearPendingLine();
        Interlocked.Exchange(ref _activeQuickGeneration, 0);
        Interlocked.Exchange(ref _retiredQuickGeneration, 0);
    }

    private long CurrentOwnedQuickGeneration()
    {
        long active = Interlocked.Read(ref _activeQuickGeneration);
        return active > 0
            ? active
            : Interlocked.Read(ref _retiredQuickGeneration);
    }

    private async ValueTask<bool> EnterGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private BridgeNetworkPakCarrierResult CompleteSuccess(
        bool committed,
        BridgeNetworkPakCarrierOutcome outcome,
        BridgeNetworkPakCarrierTransitionPhase phase,
        BridgeNetworkPakCarrierPersistenceState? persistence = null,
        string? diagnostic = null)
    {
        SetMetadata(phase,
            phase is BridgeNetworkPakCarrierTransitionPhase.Idle or
                BridgeNetworkPakCarrierTransitionPhase.Active
                ? BridgeNetworkPakTransport.None
                : _state.Current.SelectedNetworkPakTransport,
            persistence, outcome, BridgeNetworkPakCarrierFailure.None, diagnostic);
        BridgeNetworkPakCarrierSnapshot snapshot = Current;
        return new(true, committed, outcome, BridgeNetworkPakCarrierFailure.None,
            diagnostic, snapshot);
    }

    private BridgeNetworkPakCarrierResult CompleteFailure(
        bool committed,
        BridgeNetworkPakCarrierFailure failure,
        string? diagnostic,
        BridgeNetworkPakCarrierPersistenceState? persistence = null)
    {
        SetMetadata(BridgeNetworkPakCarrierTransitionPhase.Failed,
            _state.Current.SelectedNetworkPakTransport,
            persistence, BridgeNetworkPakCarrierOutcome.Failed, failure, diagnostic);
        BridgeNetworkPakCarrierSnapshot snapshot = Current;
        return new(false, committed, BridgeNetworkPakCarrierOutcome.Failed,
            failure, diagnostic, snapshot);
    }

    private BridgeNetworkPakCarrierResult CancelledBeforeCommit(string diagnostic) =>
        ResultWithoutMutation(false, false, BridgeNetworkPakCarrierOutcome.Failed,
            BridgeNetworkPakCarrierFailure.Cancelled, diagnostic);

    private BridgeNetworkPakCarrierResult ResultWithoutMutation(
        bool success,
        bool committed,
        BridgeNetworkPakCarrierOutcome outcome,
        BridgeNetworkPakCarrierFailure failure,
        string? diagnostic)
    {
        BridgeNetworkPakCarrierSnapshot snapshot = Current with
        {
            LastOutcome = outcome,
            Failure = failure,
            Diagnostic = diagnostic
        };
        return new(success, committed, outcome, failure, diagnostic, snapshot);
    }

    private void SetMetadata(
        BridgeNetworkPakCarrierTransitionPhase phase,
        BridgeNetworkPakTransport target,
        BridgeNetworkPakCarrierPersistenceState? persistence = null,
        BridgeNetworkPakCarrierOutcome outcome = BridgeNetworkPakCarrierOutcome.None,
        BridgeNetworkPakCarrierFailure failure = BridgeNetworkPakCarrierFailure.None,
        string? diagnostic = null)
    {
        TransitionMetadata current = Volatile.Read(ref _metadata);
        Volatile.Write(ref _metadata, new(phase, target,
            persistence ?? current.Persistence, outcome, failure, diagnostic));
    }

    private static BridgeNetworkPakCarrierFailure NormalizeFailure(
        BridgeNetworkPakCarrierFailure failure,
        BridgeNetworkPakCarrierFailure fallback) =>
        failure == BridgeNetworkPakCarrierFailure.None ? fallback : failure;

    private static BridgeNetworkPakCarrierFailure NormalizeActivationFailure(
        BridgeNetworkPakCarrierFailure failure) => failure is
            BridgeNetworkPakCarrierFailure.PrerequisiteNotMet or
            BridgeNetworkPakCarrierFailure.CarrierUnavailable or
            BridgeNetworkPakCarrierFailure.ActivationFailed or
            BridgeNetworkPakCarrierFailure.TransportFailed
                ? failure
                : BridgeNetworkPakCarrierFailure.ActivationFailed;

    private static string? CombineDiagnostics(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return $"{first} {second}";
    }
}
