namespace PicoSwitchController;

internal enum BridgeDesktopShutdownState
{
    Running,
    ShuttingDown,
    Complete
}

internal enum BridgeDesktopCloseDisposition
{
    CancelAndStartShutdown,
    CancelShutdownAlreadyRunning,
    AllowFinalClose
}

internal sealed class BridgeDesktopShutdownCoordinator(
    Func<Task> shutdown,
    Action requestFinalClose,
    Action<Exception>? reportUnexpectedFailure = null)
{
    private readonly object _sync = new();
    private BridgeDesktopShutdownState _state = BridgeDesktopShutdownState.Running;
    private Task _completion = Task.CompletedTask;
    private int _shutdownRunCount;

    internal BridgeDesktopShutdownState State
    {
        get { lock (_sync) return _state; }
    }

    internal Task Completion
    {
        get { lock (_sync) return _completion; }
    }

    internal int ShutdownRunCount
    {
        get { lock (_sync) return _shutdownRunCount; }
    }

    internal BridgeDesktopCloseDisposition RequestClose()
    {
        lock (_sync)
        {
            if (_state == BridgeDesktopShutdownState.Complete)
                return BridgeDesktopCloseDisposition.AllowFinalClose;
            if (_state == BridgeDesktopShutdownState.ShuttingDown)
                return BridgeDesktopCloseDisposition.CancelShutdownAlreadyRunning;

            _state = BridgeDesktopShutdownState.ShuttingDown;
            _shutdownRunCount++;
            _completion = RunAsync();
            return BridgeDesktopCloseDisposition.CancelAndStartShutdown;
        }
    }

    private async Task RunAsync()
    {
        // The first close event must return before functional shutdown begins so
        // WinForms can continue pumping continuations required by async cleanup.
        await Task.Yield();
        try
        {
            await shutdown();
        }
        catch (Exception ex)
        {
            try { reportUnexpectedFailure?.Invoke(ex); }
            catch
            {
                // Failure reporting cannot prevent the final close request.
            }
        }
        finally
        {
            lock (_sync) _state = BridgeDesktopShutdownState.Complete;
            requestFinalClose();
        }
    }
}
