namespace PicoSwitchController;

public sealed class BridgeSessionService :
    IBridgeSessionService, IBridgeSessionLifecycle
{
    private readonly object _sync = new();
    private readonly IBridgeDeviceStateProvider _device;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _newSessionId;
    private BridgeSessionSnapshot? _current;
    private bool _disposed;

    public BridgeSessionService(IBridgeDeviceStateProvider device)
        : this(device, TimeProvider.System, Guid.NewGuid)
    {
    }

    internal BridgeSessionService(
        IBridgeDeviceStateProvider device,
        TimeProvider timeProvider,
        Func<Guid> newSessionId)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _newSessionId = newSessionId ?? throw new ArgumentNullException(nameof(newSessionId));
        _device.StateChanged += Device_StateChanged;
    }

    public BridgeSessionSnapshot? Current
    {
        get { lock (_sync) return _current; }
    }

    public event EventHandler<BridgeSessionChangedEventArgs>? CurrentChanged;

    void IBridgeSessionLifecycle.FreshConnected(BridgeSessionConnectionFacts connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        BridgeSessionSnapshot? previous;
        BridgeSessionSnapshot? current;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_current is not null) return;
            Guid id = _newSessionId();
            if (id == Guid.Empty)
                throw new InvalidOperationException("The Bridge session ID source returned an empty UUID.");
            DateTimeOffset now = _timeProvider.GetUtcNow().ToUniversalTime();
            BridgeDeviceStateSnapshot device = _device.Current;
            previous = null;
            current = _current = new(id.ToString("D").ToLowerInvariant(),
                BridgeSessionState.Connected, now, now,
                connection.ConnectionKind, connection.DisplayEndpoint,
                connection.DeviceIdentity, device.FirmwarePersonality,
                device.ActiveAccessory);
        }
        RaiseChanged(previous, current);
    }

    void IBridgeSessionLifecycle.TemporarilyUnavailable()
    {
        Mutate(current => current.State == BridgeSessionState.TemporarilyUnavailable
            ? current
            : current with { State = BridgeSessionState.TemporarilyUnavailable });
    }

    void IBridgeSessionLifecycle.Restored(BridgeSessionConnectionFacts connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Mutate(current =>
        {
            BridgeDeviceStateSnapshot device = _device.Current;
            if (current.State == BridgeSessionState.Connected &&
                current.ConnectionKind == connection.ConnectionKind &&
                current.DisplayEndpoint == connection.DisplayEndpoint &&
                current.DeviceIdentity == connection.DeviceIdentity &&
                current.FirmwarePersonality == device.FirmwarePersonality &&
                current.ActiveAccessory == device.ActiveAccessory)
                return current;
            return current with
            {
                State = BridgeSessionState.Connected,
                LastConnectedUtc = current.State == BridgeSessionState.Connected
                    ? current.LastConnectedUtc
                    : _timeProvider.GetUtcNow().ToUniversalTime(),
                ConnectionKind = connection.ConnectionKind,
                DisplayEndpoint = connection.DisplayEndpoint,
                DeviceIdentity = connection.DeviceIdentity,
                FirmwarePersonality = device.FirmwarePersonality,
                ActiveAccessory = device.ActiveAccessory
            };
        });
    }

    void IBridgeSessionLifecycle.Ended()
    {
        BridgeSessionSnapshot? previous;
        lock (_sync)
        {
            if (_disposed || _current is null) return;
            previous = _current;
            _current = null;
        }
        RaiseChanged(previous, null);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _current = null;
        }
        _device.StateChanged -= Device_StateChanged;
    }

    private void Device_StateChanged(object? sender, BridgeDeviceStateChangedEventArgs e)
    {
        Mutate(current => current.FirmwarePersonality == e.Current.FirmwarePersonality &&
            current.ActiveAccessory == e.Current.ActiveAccessory
                ? current
                : current with
                {
                    FirmwarePersonality = e.Current.FirmwarePersonality,
                    ActiveAccessory = e.Current.ActiveAccessory
                });
    }

    private void Mutate(Func<BridgeSessionSnapshot, BridgeSessionSnapshot> update)
    {
        BridgeSessionSnapshot? previous;
        BridgeSessionSnapshot? current;
        lock (_sync)
        {
            if (_disposed || _current is null) return;
            previous = _current;
            current = update(previous);
            if (Equals(previous, current)) return;
            _current = current;
        }
        RaiseChanged(previous, current);
    }

    private void RaiseChanged(
        BridgeSessionSnapshot? previous,
        BridgeSessionSnapshot? current)
    {
        EventHandler<BridgeSessionChangedEventArgs>? handler = CurrentChanged;
        if (handler is null) return;
        try { handler(this, new(previous, current)); } catch { }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
