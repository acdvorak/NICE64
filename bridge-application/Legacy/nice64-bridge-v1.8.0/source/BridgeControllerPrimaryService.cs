namespace PicoSwitchController;

public sealed class BridgeControllerPrimaryService : IBridgeControllerPrimaryService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IBridgeSessionProvider _sessions;
    private readonly IBridgeDeviceStateProvider _device;
    private readonly IBridgeControllerEndpointProvider _endpoints;
    private readonly IBridgeControllerEndpointProtectionProvider _protection;
    private readonly IBridgeLocalControllerPrimaryPort _local;
    private BridgeControllerPrimarySnapshot _current;
    private bool _disposed;

    internal BridgeControllerPrimaryService(
        IBridgeSessionProvider sessions,
        IBridgeDeviceStateProvider device,
        IBridgeControllerEndpointProvider endpoints,
        IBridgeControllerEndpointProtectionProvider protection,
        IBridgeLocalControllerPrimaryPort local)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _current = BridgeSnapshot();
        _sessions.CurrentChanged += AuthorityChanged;
        _device.StateChanged += AuthorityChanged;
    }

    public BridgeControllerPrimarySnapshot Current
    {
        get { lock (_sync) return _current; }
    }

    public event EventHandler<BridgeControllerPrimaryChangedEventArgs>? CurrentChanged;

    public async Task<BridgeControllerPrimaryResult> SelectAsync(
        string sessionId,
        BridgeControllerPrimary primary,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Failure(BridgeControllerPrimaryFailure.Cancelled);
        BridgeControllerPrimaryFailure eligibility = ValidateSession(sessionId);
        if (eligibility != BridgeControllerPrimaryFailure.None) return Failure(eligibility);

        BridgeControllerEndpointSnapshot? endpoint = null;
        if (primary == BridgeControllerPrimary.NiceMobile)
        {
            if (_protection.Current != BridgeControllerEndpointProtection.None)
                return Failure(BridgeControllerPrimaryFailure.ProtectedOperation);
            try
            {
                endpoint = await _endpoints.ResolveCurrentAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return Failure(BridgeControllerPrimaryFailure.Cancelled); }
            catch { return Failure(BridgeControllerPrimaryFailure.EndpointUnavailable); }
            if (!endpoint.Available ||
                !string.Equals(endpoint.SessionId, sessionId, StringComparison.Ordinal))
                return Failure(MapEndpointFailure(endpoint));
        }

        try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { return Failure(BridgeControllerPrimaryFailure.Cancelled); }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            eligibility = ValidateSession(sessionId);
            if (eligibility != BridgeControllerPrimaryFailure.None) return Failure(eligibility);
            BridgeControllerPrimarySnapshot before = Current;
            try
            {
                if (primary == BridgeControllerPrimary.NiceMobile)
                {
                    if (_protection.Current != BridgeControllerEndpointProtection.None)
                        return Failure(BridgeControllerPrimaryFailure.ProtectedOperation);
                    if (before.Primary != BridgeControllerPrimary.NiceMobile)
                        await _local.SuspendLocalAsync(cancellationToken).ConfigureAwait(false);
                    Publish(new(sessionId, primary, endpoint!, true,
                        BridgeControllerPrimaryFailure.None));
                }
                else
                {
                    if (before.Primary != BridgeControllerPrimary.Bridge ||
                        before.LocalDeliverySuspended)
                        await _local.ResumeLocalAsync(true, cancellationToken)
                            .ConfigureAwait(false);
                    Publish(BridgeSnapshot());
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return Failure(BridgeControllerPrimaryFailure.Cancelled); }
            catch { return Failure(BridgeControllerPrimaryFailure.LocalDeliveryFailure); }
            return new(true, BridgeControllerPrimaryFailure.None, Current);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _sessions.CurrentChanged -= AuthorityChanged;
            _device.StateChanged -= AuthorityChanged;
        }
        finally { _gate.Release(); }
    }

    private void AuthorityChanged(object? sender, EventArgs e)
    {
        BridgeControllerPrimarySnapshot before = Current;
        BridgeControllerPrimaryFailure unavailable = ValidateCurrentSession();
        BridgeControllerEndpointSnapshot endpoint = before.Primary ==
            BridgeControllerPrimary.NiceMobile
            ? before.Endpoint with
            {
                Available = before.Endpoint.Available &&
                    unavailable == BridgeControllerPrimaryFailure.None,
                UnavailableReason = unavailable == BridgeControllerPrimaryFailure.None
                    ? before.Endpoint.UnavailableReason : MapUnavailableReason(unavailable)
            }
            : UnavailableEndpoint();
        Publish(before with
        {
            SessionId = _sessions.Current?.SessionId,
            Endpoint = endpoint,
            UnavailableReason = unavailable
        });
    }

    private BridgeControllerPrimarySnapshot BridgeSnapshot() => new(
        _sessions.Current?.SessionId, BridgeControllerPrimary.Bridge,
        UnavailableEndpoint(), false, ValidateCurrentSession());

    private BridgeControllerPrimaryFailure ValidateCurrentSession() =>
        _sessions.Current is { } session ? ValidateSession(session.SessionId) :
            BridgeControllerPrimaryFailure.SessionNotFound;

    private BridgeControllerPrimaryFailure ValidateSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BridgeControllerPrimaryFailure.SessionNotFound;
        BridgeSessionSnapshot? session = _sessions.Current;
        if (session is null) return BridgeControllerPrimaryFailure.SessionNotFound;
        if (!string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
            return BridgeControllerPrimaryFailure.StaleSession;
        if (session.State != BridgeSessionState.Connected)
            return BridgeControllerPrimaryFailure.SessionNotConnected;
        BridgeDeviceStateSnapshot device = _device.Current;
        if (device.ControlConnectionState != BridgeConnectionState.Connected ||
            device.FirmwarePersonality != FirmwarePersonality.Controller)
            return BridgeControllerPrimaryFailure.ControllerFirmwareUnavailable;
        return BridgeControllerPrimaryFailure.None;
    }

    private void Publish(BridgeControllerPrimarySnapshot current)
    {
        BridgeControllerPrimarySnapshot previous;
        lock (_sync)
        {
            previous = _current;
            if (previous == current) return;
            _current = current;
        }
        CurrentChanged?.Invoke(this,
            new BridgeControllerPrimaryChangedEventArgs(previous, current));
    }

    private BridgeControllerPrimaryResult Failure(BridgeControllerPrimaryFailure failure) =>
        new(false, failure, Current);

    private BridgeControllerEndpointSnapshot UnavailableEndpoint() =>
        BridgeControllerEndpointSnapshot.Unavailable(_sessions.Current?.SessionId,
            BridgeControllerEndpointUnavailableReason.NoSession);

    private static BridgeControllerPrimaryFailure MapEndpointFailure(
        BridgeControllerEndpointSnapshot endpoint) => endpoint.UnavailableReason ==
            BridgeControllerEndpointUnavailableReason.ProtectedOperation
            ? BridgeControllerPrimaryFailure.ProtectedOperation
            : BridgeControllerPrimaryFailure.EndpointUnavailable;

    private static string MapUnavailableReason(
        BridgeControllerPrimaryFailure failure) => failure switch
        {
            BridgeControllerPrimaryFailure.SessionNotConnected =>
                BridgeControllerEndpointUnavailableReason.SessionNotConnected,
            BridgeControllerPrimaryFailure.ControllerFirmwareUnavailable =>
                BridgeControllerEndpointUnavailableReason.ControllerFirmwareUnavailable,
            _ => BridgeControllerEndpointUnavailableReason.NoSession
        };
}
