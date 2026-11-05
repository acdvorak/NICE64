using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
int shutdownRuns = 0;
int finalCloseRequests = 0;
List<Exception> failures = [];
BridgeDesktopShutdownCoordinator coordinator = new(
    async () =>
    {
        Interlocked.Increment(ref shutdownRuns);
        started.TrySetResult();
        await release.Task;
    },
    () => Interlocked.Increment(ref finalCloseRequests),
    failures.Add);

Require(coordinator.State == BridgeDesktopShutdownState.Running,
    "Coordinator did not begin in Running state.");
Require(coordinator.RequestClose() ==
        BridgeDesktopCloseDisposition.CancelAndStartShutdown,
    "First close did not cancel and admit exactly one shutdown.");
Require(coordinator.State == BridgeDesktopShutdownState.ShuttingDown,
    "First close did not enter ShuttingDown state.");
Require(coordinator.RequestClose() ==
        BridgeDesktopCloseDisposition.CancelShutdownAlreadyRunning,
    "Repeated close did not remain cancelled while shutdown was running.");
Require(coordinator.RequestClose() ==
        BridgeDesktopCloseDisposition.CancelShutdownAlreadyRunning,
    "A third close request started or allowed duplicate shutdown.");

await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
Require(shutdownRuns == 1 && coordinator.ShutdownRunCount == 1,
    "Repeated close requests created duplicate shutdown work.");
Require(finalCloseRequests == 0,
    "Final close was requested before async cleanup completed.");

release.SetResult();
await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(2));
Require(coordinator.State == BridgeDesktopShutdownState.Complete,
    "Coordinator did not enter Complete state.");
Require(shutdownRuns == 1 && finalCloseRequests == 1 && failures.Count == 0,
    "Successful shutdown did not request exactly one final close.");
Require(coordinator.RequestClose() == BridgeDesktopCloseDisposition.AllowFinalClose,
    "Completion close was not allowed.");
Require(shutdownRuns == 1 && finalCloseRequests == 1,
    "Completion close restarted shutdown.");

InvalidOperationException expected = new("safe test failure");
Exception? reported = null;
int failedFinalCloseRequests = 0;
BridgeDesktopShutdownCoordinator failed = new(
    () => Task.FromException(expected),
    () => Interlocked.Increment(ref failedFinalCloseRequests),
    ex => reported = ex);
Require(failed.RequestClose() ==
        BridgeDesktopCloseDisposition.CancelAndStartShutdown,
    "Failing shutdown was not admitted.");
await failed.Completion.WaitAsync(TimeSpan.FromSeconds(2));
Require(ReferenceEquals(reported, expected),
    "Unexpected shutdown failure was not reported truthfully.");
Require(failed.State == BridgeDesktopShutdownState.Complete &&
        failedFinalCloseRequests == 1,
    "Failure path did not reach one bounded final close request.");
Require(failed.RequestClose() == BridgeDesktopCloseDisposition.AllowFinalClose,
    "Failure completion close was not allowed.");

string formPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", "..", "Form1.cs"));
string form = File.ReadAllText(formPath);
int closingStart = form.IndexOf("protected override void OnFormClosing",
    StringComparison.Ordinal);
int closedStart = form.IndexOf("protected override void OnFormClosed",
    closingStart, StringComparison.Ordinal);
Require(closingStart >= 0 && closedStart > closingStart,
    "Form shutdown integration boundaries were not found.");
string shutdownSlice = form[closingStart..closedStart];
Require(shutdownSlice.Contains("_shutdownCoordinator.RequestClose()",
        StringComparison.Ordinal) &&
    shutdownSlice.Contains("e.Cancel = true", StringComparison.Ordinal) &&
    shutdownSlice.Contains("Enabled = false", StringComparison.Ordinal),
    "FormClosing does not enforce one-shot admission and new-work suppression.");
Require(shutdownSlice.Contains("await _apiAccessLifecycle.DisposeAsync()",
        StringComparison.Ordinal) &&
    shutdownSlice.Contains("await _connectionService.DisconnectAsync(",
        StringComparison.Ordinal) &&
    shutdownSlice.Contains("await _accessoryPersistence.DisposeAsync()",
        StringComparison.Ordinal) &&
    shutdownSlice.Contains("await _controllerPrimaryService.DisposeAsync()",
        StringComparison.Ordinal),
    "Async shutdown omits an accepted cleanup owner.");
Require(shutdownSlice.Contains("await Task.WhenAll(input, serial)",
        StringComparison.Ordinal),
    "Async shutdown does not await input/writer worker retirement.");
Require(!shutdownSlice.Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal) &&
    !shutdownSlice.Contains(".Wait(", StringComparison.Ordinal) &&
    !shutdownSlice.Contains(".Result", StringComparison.Ordinal),
    "Form shutdown path still synchronously blocks on async cleanup.");
Require(form.Contains("await StopLatestStateUdpSenderAsync().ConfigureAwait(false)",
        StringComparison.Ordinal),
    "Disconnect cleanup does not await latest-state sender retirement.");

Console.WriteLine("PASS Bridge desktop one-shot async shutdown coordination");
