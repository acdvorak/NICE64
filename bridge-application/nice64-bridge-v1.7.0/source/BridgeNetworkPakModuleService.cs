namespace PicoSwitchController;

public sealed record BridgeNetworkPakModuleInfo(string Id, string DisplayName);

public sealed record BridgeNetworkPakModuleRuntimeState(
    string ActiveModuleId,
    string ActiveModuleName);

public interface IBridgeNetworkPakModuleRuntime
{
    string DefaultModuleId { get; }
    IReadOnlyList<BridgeNetworkPakModuleInfo> ListSelectableModules();
    BridgeNetworkPakModuleRuntimeState CurrentModule { get; }
    bool SetActiveModule(string? moduleId);
}

public interface IBridgeNetworkPakModuleSelectionStore
{
    ValueTask SaveAsync(string moduleId, CancellationToken cancellationToken = default);
}

public enum BridgeNetworkPakModulePersistenceState
{
    Unverified,
    Confirmed,
    FailedUnknown
}

public enum BridgeNetworkPakModuleSelectionOutcome
{
    None,
    RequestedModuleSelected,
    FallbackSelected,
    Failed
}

public enum BridgeNetworkPakModuleSelectionFailure
{
    None,
    Busy,
    Cancelled,
    PersistenceFailure,
    RuntimeSelectionFailure,
    RollbackFailure,
    UnexpectedFailure
}

public sealed record BridgeNetworkPakModuleSnapshot(
    string ActiveModuleId,
    string ActiveModuleName,
    IReadOnlyList<BridgeNetworkPakModuleInfo> AvailableModules,
    BridgeNetworkPakModulePersistenceState PersistenceState,
    BridgeNetworkPakModuleSelectionOutcome LastSelectionOutcome,
    string? LastError);

public sealed record BridgeNetworkPakModuleSelectionResult(
    bool Success,
    string? RequestedModuleId,
    string SelectedModuleId,
    string SelectedModuleName,
    bool FallbackUsed,
    bool PersistenceSucceeded,
    BridgeNetworkPakModuleSelectionFailure Failure,
    string? Error,
    BridgeNetworkPakModuleSnapshot Snapshot);

public interface IBridgeNetworkPakModuleService
{
    BridgeNetworkPakModuleSnapshot Current { get; }
    IReadOnlyList<BridgeNetworkPakModuleInfo> ListModules();
    Task<BridgeNetworkPakModuleSelectionResult> SelectModuleAsync(
        string? moduleId,
        CancellationToken cancellationToken = default);
}

public sealed class BridgeNetworkPakModuleService : IBridgeNetworkPakModuleService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private readonly IBridgeNetworkPakModuleRuntime _runtime;
    private readonly IBridgeNetworkPakModuleSelectionStore _store;
    private BridgeNetworkPakModulePersistenceState _persistenceState =
        BridgeNetworkPakModulePersistenceState.Unverified;
    private BridgeNetworkPakModuleSelectionOutcome _lastSelectionOutcome;
    private string? _lastError;

    public BridgeNetworkPakModuleService(
        IBridgeNetworkPakModuleRuntime runtime,
        IBridgeNetworkPakModuleSelectionStore store)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public BridgeNetworkPakModuleSnapshot Current => CreateSnapshot();

    public IReadOnlyList<BridgeNetworkPakModuleInfo> ListModules() =>
        _runtime.ListSelectableModules();

    public async Task<BridgeNetworkPakModuleSelectionResult> SelectModuleAsync(
        string? moduleId,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return FailureResult(moduleId, false,
                BridgeNetworkPakModuleSelectionFailure.Cancelled,
                "Module selection was cancelled.", updateCurrent: false);
        }

        bool entered;
        try
        {
            entered = await _selectionGate.WaitAsync(0, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FailureResult(moduleId, false,
                BridgeNetworkPakModuleSelectionFailure.Cancelled,
                "Module selection was cancelled.", updateCurrent: false);
        }

        if (!entered)
        {
            return FailureResult(moduleId, false,
                BridgeNetworkPakModuleSelectionFailure.Busy,
                "Another module selection is already in progress.", updateCurrent: false);
        }

        try
        {
            IReadOnlyList<BridgeNetworkPakModuleInfo> modules = ListModules();
            BridgeNetworkPakModuleInfo? requested = modules.FirstOrDefault(module =>
                string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase));
            BridgeNetworkPakModuleInfo? fallback = modules.FirstOrDefault(module =>
                string.Equals(module.Id, _runtime.DefaultModuleId,
                    StringComparison.Ordinal));
            if (fallback is null)
            {
                return FailureResult(moduleId, false,
                    BridgeNetworkPakModuleSelectionFailure.UnexpectedFailure,
                    "The default Network Pak module is unavailable.");
            }

            bool fallbackUsed = requested is null;
            BridgeNetworkPakModuleInfo target = requested ?? fallback;

            try
            {
                await _store.SaveAsync(target.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return FailureResult(moduleId, fallbackUsed,
                    BridgeNetworkPakModuleSelectionFailure.Cancelled,
                    "Module selection was cancelled while persistence was in progress.",
                    BridgeNetworkPakModulePersistenceState.FailedUnknown);
            }
            catch (Exception ex)
            {
                return FailureResult(moduleId, fallbackUsed,
                    BridgeNetworkPakModuleSelectionFailure.PersistenceFailure,
                    ex.Message, BridgeNetworkPakModulePersistenceState.FailedUnknown);
            }

            // Persistence has committed. Finish the runtime transition even if the
            // caller cancels now so the two authoritative states cannot diverge.
            try
            {
                bool selected = _runtime.SetActiveModule(target.Id);
                BridgeNetworkPakModuleRuntimeState runtime = _runtime.CurrentModule;
                if (!selected || !string.Equals(runtime.ActiveModuleId, target.Id,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return await ReconcileRuntimeFailureAsync(
                        moduleId, fallbackUsed,
                        "The runtime did not select the persisted module.")
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                return await ReconcileRuntimeFailureAsync(moduleId, fallbackUsed, ex.Message)
                    .ConfigureAwait(false);
            }

            lock (_sync)
            {
                _persistenceState = BridgeNetworkPakModulePersistenceState.Confirmed;
                _lastSelectionOutcome = fallbackUsed
                    ? BridgeNetworkPakModuleSelectionOutcome.FallbackSelected
                    : BridgeNetworkPakModuleSelectionOutcome.RequestedModuleSelected;
                _lastError = null;
            }

            BridgeNetworkPakModuleSnapshot snapshot = Current;
            return new(true, moduleId, snapshot.ActiveModuleId, snapshot.ActiveModuleName,
                fallbackUsed, true, BridgeNetworkPakModuleSelectionFailure.None, null,
                snapshot);
        }
        catch (Exception ex)
        {
            return FailureResult(moduleId, false,
                BridgeNetworkPakModuleSelectionFailure.UnexpectedFailure, ex.Message);
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    private async Task<BridgeNetworkPakModuleSelectionResult> ReconcileRuntimeFailureAsync(
        string? requestedModuleId,
        bool fallbackUsed,
        string runtimeError)
    {
        BridgeNetworkPakModuleRuntimeState runtime = _runtime.CurrentModule;
        try
        {
            await _store.SaveAsync(runtime.ActiveModuleId, CancellationToken.None)
                .ConfigureAwait(false);
            return FailureResult(requestedModuleId, fallbackUsed,
                BridgeNetworkPakModuleSelectionFailure.RuntimeSelectionFailure,
                runtimeError, BridgeNetworkPakModulePersistenceState.Confirmed,
                persistenceSucceeded: true);
        }
        catch (Exception recoveryError)
        {
            return FailureResult(requestedModuleId, fallbackUsed,
                BridgeNetworkPakModuleSelectionFailure.RollbackFailure,
                $"{runtimeError} Persistence recovery failed: {recoveryError.Message}",
                BridgeNetworkPakModulePersistenceState.FailedUnknown);
        }
    }

    private BridgeNetworkPakModuleSelectionResult FailureResult(
        string? requestedModuleId,
        bool fallbackUsed,
        BridgeNetworkPakModuleSelectionFailure failure,
        string error,
        BridgeNetworkPakModulePersistenceState? persistenceState = null,
        bool persistenceSucceeded = false,
        bool updateCurrent = true)
    {
        if (updateCurrent)
        {
            lock (_sync)
            {
                if (persistenceState.HasValue)
                {
                    _persistenceState = persistenceState.Value;
                }
                _lastSelectionOutcome = BridgeNetworkPakModuleSelectionOutcome.Failed;
                _lastError = error;
            }
        }

        BridgeNetworkPakModuleSnapshot snapshot = Current;
        return new(false, requestedModuleId, snapshot.ActiveModuleId,
            snapshot.ActiveModuleName, fallbackUsed,
            persistenceSucceeded,
            failure, error, snapshot);
    }

    private BridgeNetworkPakModuleSnapshot CreateSnapshot()
    {
        BridgeNetworkPakModuleRuntimeState runtime = _runtime.CurrentModule;
        IReadOnlyList<BridgeNetworkPakModuleInfo> modules =
            _runtime.ListSelectableModules().ToArray();
        lock (_sync)
        {
            return new(runtime.ActiveModuleId, runtime.ActiveModuleName, modules,
                _persistenceState, _lastSelectionOutcome, _lastError);
        }
    }
}
