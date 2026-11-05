namespace PicoSwitchController;

internal interface ILcpRestartScheduler : IDisposable
{
    bool IsArmed { get; }
    void Arm(TimeSpan delay);
    void Cancel();
}

/// <summary>
/// Per-session, one-shot scheduling for the existing synchronous LCP FSM.
/// The owner decides whether another deadline is required after each callback.
/// </summary>
internal sealed class LcpRestartScheduler : ILcpRestartScheduler
{
    private readonly object _sync = new();
    private readonly Action _callback;
    private readonly Timer _timer;
    private bool _armed;
    private bool _disposed;

    public LcpRestartScheduler(Action callback)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool IsArmed
    {
        get { lock (_sync) return _armed && !_disposed; }
    }

    public void Arm(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));

        lock (_sync)
        {
            if (_disposed) return;
            _armed = true;
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _armed = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _armed = false;
            _timer.Dispose();
        }
    }

    private void OnTimer(object? state)
    {
        lock (_sync)
        {
            if (_disposed || !_armed) return;
            _armed = false;
        }

        _callback();
    }
}
