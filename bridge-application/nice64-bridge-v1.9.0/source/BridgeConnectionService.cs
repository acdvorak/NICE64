namespace PicoSwitchController;

public sealed record BridgeConnectionRequest(
    BridgeControlConnectionKind ConnectionKind,
    string Endpoint,
    int Port = 0,
    TimeSpan? Timeout = null);

public enum BridgeConnectionFailure
{
    None,
    InvalidRequest,
    AlreadyConnected,
    Busy,
    InvalidEndpoint,
    OpenFailed,
    HandshakeFailed,
    VerificationFailed,
    Cancelled,
    Timeout,
    Unexpected
}

public sealed record BridgeConnectionResult(
    bool Success,
    BridgeControlConnectionKind ConnectionKind,
    string DisplayEndpoint,
    string DeviceIdentity,
    FirmwarePersonality FirmwarePersonality,
    BridgeCapabilitySet DeviceReportedCapabilities,
    string Error)
{
    public BridgeConnectionFailure Failure { get; init; }
}

public enum BridgeDisconnectReason
{
    UserRequest,
    ApplicationShutdown
}

public enum BridgeDisconnectFailure
{
    None,
    AlreadyDisconnected,
    Busy,
    BlockedByPersistence,
    CleanupFailure,
    ShutdownContinuedWithPersistenceFailure,
    Cancelled,
    Unexpected
}

public sealed record BridgeDisconnectResult(
    bool Success,
    BridgeDisconnectReason Reason,
    AccessoryPersistenceResult Persistence,
    bool NetworkPakCleanupInvoked,
    bool ControllerCleanupInvoked,
    string Error)
{
    public BridgeDisconnectFailure Failure { get; init; }
}

public interface IBridgeConnectionService
{
    Task<BridgeConnectionResult> ConnectAsync(
        BridgeConnectionRequest request,
        CancellationToken cancellationToken = default);
    Task<BridgeDisconnectResult> DisconnectAsync(
        BridgeDisconnectReason reason,
        CancellationToken cancellationToken = default);
}

internal sealed record BridgeControlSession(
    BridgeControlConnectionKind ConnectionKind,
    string DisplayEndpoint,
    string DeviceIdentity,
    string DeviceVersion,
    FirmwareModeInfo? Mode,
    BridgeNetworkPakTransport SelectedNetworkPakTransport,
    BridgeCapabilitySet DeviceReportedCapabilities);

internal sealed record BridgeDisconnectCleanupResult(
    bool NetworkPakCleanupInvoked,
    bool ControllerCleanupInvoked);

internal interface IBridgeSerialConnectionAdapter
{
    Task<BridgeControlSession> ConnectAsync(
        string portName,
        CancellationToken cancellationToken);
    Task<BridgeControlSession> ReplaceAfterFirmwareRebootAsync(
        string? preferredPort,
        FirmwarePersonality target,
        CancellationToken cancellationToken);
}

internal interface IBridgeTcpConnectionAdapter
{
    Task<BridgeControlSession> ConnectAsync(
        string address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken);
    Task PrepareFirmwareReplacementAsync(CancellationToken cancellationToken);
    Task<BridgeControlSession> ReplaceAfterFirmwareRebootAsync(
        int attempt,
        CancellationToken cancellationToken);
    void EndFirmwareReplacementWithoutSession();
}

internal interface IBridgeDisconnectCleanupAdapter
{
    Task<BridgeDisconnectCleanupResult> CleanupAsync(
        bool sendNeutralControllerState,
        CancellationToken cancellationToken);
}

internal interface IBridgeFirmwareConnectionReplacement
{
    Task PrepareTcpFirmwareReplacementAsync(CancellationToken cancellationToken);
    Task<FirmwareModeInfo> ReplaceTcpControlSessionAsync(
        int attempt,
        CancellationToken cancellationToken);
    void EndTcpFirmwareReplacementWithoutSession();
    Task<FirmwareModeInfo> ReplaceSerialControlSessionAsync(
        FirmwarePersonality target,
        CancellationToken cancellationToken);
}

internal enum BridgeTransportFaultDisposition
{
    GenuineDisconnect,
    AutoReconnectScheduled,
    ExpectedFirmwareReplacement
}

internal sealed record BridgeSerialAutoReconnectSnapshot(
    bool Pending,
    int AttemptCount,
    DateTimeOffset NextAttemptUtc,
    string PreferredPort);

internal sealed record BridgeAutoReconnectAttemptResult(
    bool Attempted,
    BridgeConnectionResult? Connection,
    string Error);

internal sealed class BridgeSerialPortUnavailableException(string message) :
    IOException(message);

internal sealed class DelegateSerialConnectionAdapter(
    Func<string, CancellationToken, Task<BridgeControlSession>> connect,
    Func<string?, FirmwarePersonality, CancellationToken,
        Task<BridgeControlSession>> replace) : IBridgeSerialConnectionAdapter
{
    public Task<BridgeControlSession> ConnectAsync(
        string portName, CancellationToken cancellationToken) =>
        connect(portName, cancellationToken);

    public Task<BridgeControlSession> ReplaceAfterFirmwareRebootAsync(
        string? preferredPort, FirmwarePersonality target,
        CancellationToken cancellationToken) =>
        replace(preferredPort, target, cancellationToken);
}

internal sealed class DelegateTcpConnectionAdapter(
    Func<string, int, TimeSpan, CancellationToken, Task<BridgeControlSession>> connect,
    Func<CancellationToken, Task> prepareReplacement,
    Func<int, CancellationToken, Task<BridgeControlSession>> replace,
    Action endReplacement) : IBridgeTcpConnectionAdapter
{
    public Task<BridgeControlSession> ConnectAsync(
        string address, int port, TimeSpan timeout,
        CancellationToken cancellationToken) =>
        connect(address, port, timeout, cancellationToken);

    public Task PrepareFirmwareReplacementAsync(CancellationToken cancellationToken) =>
        prepareReplacement(cancellationToken);

    public Task<BridgeControlSession> ReplaceAfterFirmwareRebootAsync(
        int attempt, CancellationToken cancellationToken) =>
        replace(attempt, cancellationToken);

    public void EndFirmwareReplacementWithoutSession() => endReplacement();
}

internal sealed class DelegateDisconnectCleanupAdapter(
    Func<bool, CancellationToken, Task<BridgeDisconnectCleanupResult>> cleanup) :
    IBridgeDisconnectCleanupAdapter
{
    public Task<BridgeDisconnectCleanupResult> CleanupAsync(
        bool sendNeutralControllerState,
        CancellationToken cancellationToken) =>
        cleanup(sendNeutralControllerState, cancellationToken);
}

public sealed class BridgeConnectionService :
    IBridgeConnectionService, IBridgeFirmwareConnectionReplacement
{
    private static readonly TimeSpan DefaultTcpConnectTimeout = TimeSpan.FromSeconds(5);
    private readonly IBridgeDeviceStateProvider _state;
    private readonly IBridgeDeviceStateUpdater _updater;
    private readonly IAccessoryPersistenceService _persistence;
    private readonly IBridgeSerialConnectionAdapter _serial;
    private readonly IBridgeTcpConnectionAdapter _tcp;
    private readonly IBridgeDisconnectCleanupAdapter _cleanup;
    private readonly IBridgeSessionLifecycle? _sessions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _autoSync = new();
    private BridgeSerialAutoReconnectSnapshot _autoReconnect =
        new(false, 0, DateTimeOffset.MinValue, string.Empty);
    private bool _firmwareReplacementActive;

    internal BridgeConnectionService(
        IBridgeDeviceStateProvider state,
        IAccessoryPersistenceService persistence,
        IBridgeSerialConnectionAdapter serial,
        IBridgeTcpConnectionAdapter tcp,
        IBridgeDisconnectCleanupAdapter cleanup,
        IBridgeSessionLifecycle? sessions = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _updater = state as IBridgeDeviceStateUpdater
            ?? throw new ArgumentException(
                "The host state provider is not mutable by the service layer.", nameof(state));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _serial = serial ?? throw new ArgumentNullException(nameof(serial));
        _tcp = tcp ?? throw new ArgumentNullException(nameof(tcp));
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        _sessions = sessions;
    }

    internal BridgeSerialAutoReconnectSnapshot AutoReconnect
    {
        get { lock (_autoSync) return _autoReconnect; }
    }

    public async Task<BridgeConnectionResult> ConnectAsync(
        BridgeConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
            return RejectConnection(request.ConnectionKind,
                BridgeConnectionFailure.Cancelled, "Connection was cancelled.");
        if (request.ConnectionKind is not (BridgeControlConnectionKind.SerialUsb or
            BridgeControlConnectionKind.WifiTcp) ||
            string.IsNullOrWhiteSpace(request.Endpoint))
            return RejectConnection(request.ConnectionKind,
                BridgeConnectionFailure.InvalidRequest,
                "A supported connection kind and endpoint are required.");

        try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            return RejectConnection(request.ConnectionKind,
                BridgeConnectionFailure.Cancelled,
                "Connection was cancelled before ownership was acquired.");
        }

        try
        {
            if (_firmwareReplacementActive || FirmwareTransitionIsActive())
                return RejectConnection(request.ConnectionKind,
                    BridgeConnectionFailure.Busy,
                    "A firmware control-session replacement is active.");
            if (_state.Current.ControlConnectionState == BridgeConnectionState.Connected)
                return RejectConnection(request.ConnectionKind,
                    BridgeConnectionFailure.AlreadyConnected,
                    "The NICE64 Bridge is already connected.");

            BridgeControlSession session = request.ConnectionKind ==
                BridgeControlConnectionKind.SerialUsb
                ? await _serial.ConnectAsync(request.Endpoint.Trim(), cancellationToken)
                    .ConfigureAwait(false)
                : await _tcp.ConnectAsync(request.Endpoint.Trim(), request.Port,
                    request.Timeout ?? DefaultTcpConnectTimeout, cancellationToken)
                    .ConfigureAwait(false);
            ApplyNewSession(session);
            _sessions?.FreshConnected(SessionFacts(session));
            if (session.ConnectionKind == BridgeControlConnectionKind.SerialUsb)
                ResetAutoReconnect(session.DisplayEndpoint);
            else
                ResetAutoReconnect();
            return Success(session);
        }
        catch (OperationCanceledException)
        {
            return RejectConnection(request.ConnectionKind,
                BridgeConnectionFailure.Cancelled, "Connection was cancelled.");
        }
        catch (ArgumentException ex)
        {
            return RejectConnection(request.ConnectionKind,
                BridgeConnectionFailure.InvalidEndpoint, ex.Message);
        }
        catch (TimeoutException ex)
        {
            return RejectConnection(request.ConnectionKind,
                BridgeConnectionFailure.Timeout, ex.Message);
        }
        catch (IOException ex)
        {
            return RejectConnection(request.ConnectionKind,
                request.ConnectionKind == BridgeControlConnectionKind.WifiTcp
                    ? BridgeConnectionFailure.HandshakeFailed
                    : BridgeConnectionFailure.OpenFailed,
                ex.Message);
        }
        catch (Exception ex)
        {
            return RejectConnection(request.ConnectionKind,
                BridgeConnectionFailure.Unexpected, ex.Message);
        }
        finally { _gate.Release(); }
    }

    public async Task<BridgeDisconnectResult> DisconnectAsync(
        BridgeDisconnectReason reason,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return RejectDisconnect(reason, BridgeDisconnectFailure.Cancelled,
                AccessoryPersistenceResult.Clean, "Disconnect was cancelled.");
        try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            return RejectDisconnect(reason, BridgeDisconnectFailure.Cancelled,
                AccessoryPersistenceResult.Clean,
                "Disconnect was cancelled before ownership was acquired.");
        }

        try
        {
            bool shutdown = reason == BridgeDisconnectReason.ApplicationShutdown;
            if (!shutdown && (_firmwareReplacementActive || FirmwareTransitionIsActive()))
                return RejectDisconnect(reason, BridgeDisconnectFailure.Busy,
                    AccessoryPersistenceResult.Clean,
                    "A firmware control-session replacement is active.");
            if (!shutdown &&
                _state.Current.ControlConnectionState == BridgeConnectionState.Disconnected)
                return RejectDisconnect(reason, BridgeDisconnectFailure.AlreadyDisconnected,
                    AccessoryPersistenceResult.Clean, "The NICE64 Bridge is already disconnected.");

            BridgeDisconnectAuthorization authorization =
                await BridgeDisconnectPersistencePolicy.AuthorizeAsync(
                    _persistence, shutdown, cancellationToken).ConfigureAwait(false);
            if (!authorization.DisconnectAllowed)
                return RejectDisconnect(reason,
                    BridgeDisconnectFailure.BlockedByPersistence,
                    authorization.Persistence,
                    $"Persistence flush failed: {authorization.Persistence.Error}");

            BridgeDisconnectCleanupResult cleanup;
            try
            {
                cleanup = await _cleanup.CleanupAsync(
                    sendNeutralControllerState: true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _sessions?.Ended();
                _updater.Disconnect();
                ResetAutoReconnect();
                _persistence.TryDeactivateClean();
                return new(false, reason, authorization.Persistence, false, false, ex.Message)
                { Failure = BridgeDisconnectFailure.CleanupFailure };
            }

            _sessions?.Ended();
            _updater.Disconnect();
            ResetAutoReconnect();
            _persistence.TryDeactivateClean();
            if (!authorization.Persistence.Success)
                return new(true, reason, authorization.Persistence,
                    cleanup.NetworkPakCleanupInvoked,
                    cleanup.ControllerCleanupInvoked,
                    authorization.Persistence.Error)
                { Failure = BridgeDisconnectFailure.ShutdownContinuedWithPersistenceFailure };
            return new(true, reason, authorization.Persistence,
                cleanup.NetworkPakCleanupInvoked, cleanup.ControllerCleanupInvoked,
                string.Empty);
        }
        catch (OperationCanceledException)
        {
            return RejectDisconnect(reason, BridgeDisconnectFailure.Cancelled,
                AccessoryPersistenceResult.Clean, "Disconnect was cancelled.");
        }
        catch (Exception ex)
        {
            return RejectDisconnect(reason, BridgeDisconnectFailure.Unexpected,
                AccessoryPersistenceResult.Clean, ex.Message);
        }
        finally { _gate.Release(); }
    }

    internal BridgeTransportFaultDisposition ReportSerialControlLoss(
        string? preferredPort)
    {
        string port = preferredPort?.Trim() ?? string.Empty;
        if (FirmwareTransitionIsActive())
        {
            ResetAutoReconnect(port);
            _sessions?.TemporarilyUnavailable();
            _updater.SetControlConnectionState(BridgeControlConnectionKind.SerialUsb,
                BridgeConnectionState.Disconnected);
            return BridgeTransportFaultDisposition.ExpectedFirmwareReplacement;
        }

        if (port.Length == 0)
        {
            _sessions?.Ended();
            _updater.Disconnect();
            ResetAutoReconnect();
            return BridgeTransportFaultDisposition.GenuineDisconnect;
        }
        _sessions?.TemporarilyUnavailable();
        _updater.Disconnect();
        lock (_autoSync)
            _autoReconnect = new(true, 0, DateTimeOffset.UtcNow.AddSeconds(1), port);
        return BridgeTransportFaultDisposition.AutoReconnectScheduled;
    }

    internal BridgeTransportFaultDisposition ReportTcpControlLoss()
    {
        if (FirmwareTransitionIsActive())
        {
            _sessions?.TemporarilyUnavailable();
            _updater.SetControlConnectionState(BridgeControlConnectionKind.WifiTcp,
                BridgeConnectionState.Disconnected);
            return BridgeTransportFaultDisposition.ExpectedFirmwareReplacement;
        }
        _sessions?.Ended();
        _updater.Disconnect();
        return BridgeTransportFaultDisposition.GenuineDisconnect;
    }

    internal async Task<BridgeAutoReconnectAttemptResult> TryAutoReconnectAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        BridgeSerialAutoReconnectSnapshot snapshot = AutoReconnect;
        if (!snapshot.Pending || now < snapshot.NextAttemptUtc)
            return new(false, null, string.Empty);

        try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            return new(false, null, "Automatic reconnect was cancelled.");
        }
        try
        {
            snapshot = AutoReconnect;
            if (!snapshot.Pending || now < snapshot.NextAttemptUtc ||
                _firmwareReplacementActive || FirmwareTransitionIsActive())
                return new(false, null, string.Empty);
            try
            {
                BridgeControlSession session = await _serial.ConnectAsync(
                    snapshot.PreferredPort, cancellationToken).ConfigureAwait(false);
                ApplyNewSession(session);
                _sessions?.Restored(SessionFacts(session));
                ResetAutoReconnect(session.DisplayEndpoint);
                return new(true, Success(session), string.Empty);
            }
            catch (OperationCanceledException)
            {
                return new(true, RejectConnection(BridgeControlConnectionKind.SerialUsb,
                    BridgeConnectionFailure.Cancelled,
                    "Automatic reconnect was cancelled."),
                    "Automatic reconnect was cancelled.");
            }
            catch (BridgeSerialPortUnavailableException ex)
            {
                lock (_autoSync)
                    _autoReconnect = snapshot with
                    {
                        NextAttemptUtc = now.AddSeconds(1)
                    };
                return new(false, null, ex.Message);
            }
            catch (Exception ex)
            {
                int attempt = snapshot.AttemptCount + 1;
                int delaySeconds = Math.Min(5, Math.Max(1, attempt));
                lock (_autoSync)
                    _autoReconnect = snapshot with
                    {
                        AttemptCount = attempt,
                        NextAttemptUtc = now.AddSeconds(delaySeconds)
                    };
                return new(true, RejectConnection(BridgeControlConnectionKind.SerialUsb,
                    BridgeConnectionFailure.OpenFailed, ex.Message), ex.Message);
            }
        }
        finally { _gate.Release(); }
    }

    async Task IBridgeFirmwareConnectionReplacement.PrepareTcpFirmwareReplacementAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _firmwareReplacementActive = true;
            await _tcp.PrepareFirmwareReplacementAsync(cancellationToken)
                .ConfigureAwait(false);
            _sessions?.TemporarilyUnavailable();
            _updater.SetControlConnectionState(BridgeControlConnectionKind.WifiTcp,
                BridgeConnectionState.Disconnected);
        }
        catch
        {
            _firmwareReplacementActive = false;
            throw;
        }
        finally { _gate.Release(); }
    }

    async Task<FirmwareModeInfo>
        IBridgeFirmwareConnectionReplacement.ReplaceTcpControlSessionAsync(
            int attempt,
            CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BridgeControlSession session = await _tcp.ReplaceAfterFirmwareRebootAsync(
                attempt, cancellationToken).ConfigureAwait(false);
            ApplyReplacementSession(session);
            _sessions?.Restored(SessionFacts(session));
            _firmwareReplacementActive = false;
            return session.Mode ?? throw new IOException(
                "The replacement TCP session did not report firmware mode.");
        }
        finally { _gate.Release(); }
    }

    void IBridgeFirmwareConnectionReplacement.EndTcpFirmwareReplacementWithoutSession()
    {
        _tcp.EndFirmwareReplacementWithoutSession();
        _firmwareReplacementActive = false;
        _sessions?.Ended();
    }

    async Task<FirmwareModeInfo>
        IBridgeFirmwareConnectionReplacement.ReplaceSerialControlSessionAsync(
            FirmwarePersonality target,
            CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _firmwareReplacementActive = true;
            _sessions?.TemporarilyUnavailable();
            _updater.SetControlConnectionState(BridgeControlConnectionKind.SerialUsb,
                BridgeConnectionState.Disconnected);
            BridgeControlSession session =
                await _serial.ReplaceAfterFirmwareRebootAsync(
                    AutoReconnect.PreferredPort, target, cancellationToken)
                    .ConfigureAwait(false);
            ApplyReplacementSession(session);
            _sessions?.Restored(SessionFacts(session));
            return session.Mode ?? throw new IOException(
                "The replacement serial session did not report firmware mode.");
        }
        catch
        {
            _sessions?.Ended();
            throw;
        }
        finally
        {
            _firmwareReplacementActive = false;
            _gate.Release();
        }
    }

    private void ApplyNewSession(BridgeControlSession session)
    {
        _updater.Connect(session.ConnectionKind, session.DeviceIdentity,
            session.DeviceVersion, session.Mode, session.DeviceReportedCapabilities);
        _updater.SelectNetworkPakTransport(session.SelectedNetworkPakTransport);
    }

    private void ApplyReplacementSession(BridgeControlSession session)
    {
        _updater.ReplaceControlSession(session.ConnectionKind, session.DeviceIdentity,
            session.DeviceVersion, session.Mode, session.DeviceReportedCapabilities);
    }

    private static BridgeSessionConnectionFacts SessionFacts(
        BridgeControlSession session) =>
        new(session.ConnectionKind, session.DisplayEndpoint, session.DeviceIdentity);

    private static BridgeConnectionResult Success(BridgeControlSession session) =>
        new(true, session.ConnectionKind, session.DisplayEndpoint,
            session.DeviceIdentity, session.Mode?.Personality ?? FirmwarePersonality.Unknown,
            session.DeviceReportedCapabilities, string.Empty);

    private BridgeConnectionResult RejectConnection(
        BridgeControlConnectionKind kind,
        BridgeConnectionFailure failure,
        string error) =>
        new(false, kind, string.Empty, string.Empty,
            _state.Current.FirmwarePersonality,
            _state.Current.DeviceReportedCapabilities, error) { Failure = failure };

    private static BridgeDisconnectResult RejectDisconnect(
        BridgeDisconnectReason reason,
        BridgeDisconnectFailure failure,
        AccessoryPersistenceResult persistence,
        string error) =>
        new(false, reason, persistence, false, false, error) { Failure = failure };

    private bool FirmwareTransitionIsActive() =>
        _state.Current.FirmwareTransition.Phase is
            BridgeFirmwareTransitionPhase.TransitionPending or
            BridgeFirmwareTransitionPhase.RebootExpected or
            BridgeFirmwareTransitionPhase.Reconnecting;

    private void ResetAutoReconnect(string preferredPort = "")
    {
        lock (_autoSync)
            _autoReconnect = new(false, 0, DateTimeOffset.MinValue, preferredPort);
    }
}
