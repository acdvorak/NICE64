using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static (BridgeNetworkPakModuleService Service, FakeRuntime Runtime, FakeStore Store) Create()
{
    FakeRuntime runtime = new();
    FakeStore store = new();
    return (new BridgeNetworkPakModuleService(runtime, store), runtime, store);
}

static async Task TestEnumerationAndValidSelectionAsync()
{
    var (service, runtime, store) = Create();
    IReadOnlyList<BridgeNetworkPakModuleInfo> modules = service.ListModules();
    Require(modules.Select(module => module.Id).SequenceEqual(
            ["network-portal", "diagnostics", "browser-live", "external-demo"]),
        "Module ordering or external-module enumeration was not preserved");

    BridgeNetworkPakModuleSelectionResult result =
        await service.SelectModuleAsync("DIAGNOSTICS");
    Require(result.Success && !result.FallbackUsed &&
        result.SelectedModuleId == "diagnostics" &&
        result.SelectedModuleName == "Diagnostics" &&
        result.PersistenceSucceeded &&
        result.Failure == BridgeNetworkPakModuleSelectionFailure.None,
        "Case-insensitive valid selection did not return truthful success");
    Require(store.SavedIds.SequenceEqual(["diagnostics"]) &&
        runtime.CurrentModule.ActiveModuleId == "diagnostics" &&
        runtime.SelectionCount == 1 && runtime.StreamClearCount == 1,
        "Valid selection did not persist first and invoke the existing runtime once");
    Require(service.Current.ActiveModuleId == runtime.CurrentModule.ActiveModuleId &&
        service.Current.PersistenceState ==
            BridgeNetworkPakModulePersistenceState.Confirmed &&
        service.Current.LastSelectionOutcome ==
            BridgeNetworkPakModuleSelectionOutcome.RequestedModuleSelected,
        "Semantic snapshot did not match runtime selection truth");
    Require(runtime.PicoMutationCount == 0,
        "Host-only module selection caused a Pico/transport mutation");
}

static async Task TestFallbackParityAsync(string? requested)
{
    var (service, runtime, store) = Create();
    await service.SelectModuleAsync("diagnostics");
    store.SavedIds.Clear();

    BridgeNetworkPakModuleSelectionResult result =
        await service.SelectModuleAsync(requested);
    Require(result.Success && result.FallbackUsed &&
        result.SelectedModuleId == "network-portal" &&
        result.Snapshot.ActiveModuleId == "network-portal" &&
        result.Snapshot.LastSelectionOutcome ==
            BridgeNetworkPakModuleSelectionOutcome.FallbackSelected,
        "Invalid/null/empty selection did not retain network-portal fallback semantics");
    Require(store.SavedIds.SequenceEqual(["network-portal"]) &&
        runtime.CurrentModule.ActiveModuleId == "network-portal",
        "Fallback did not persist the actual final module ID");
}

static async Task TestPersistenceFailureDoesNotMutateRuntimeAsync()
{
    var (service, runtime, store) = Create();
    await service.SelectModuleAsync("diagnostics");
    int selectionsBefore = runtime.SelectionCount;
    int clearsBefore = runtime.StreamClearCount;
    store.ThrowOnNextSave = true;

    BridgeNetworkPakModuleSelectionResult result =
        await service.SelectModuleAsync("browser-live");
    Require(!result.Success &&
        result.Failure == BridgeNetworkPakModuleSelectionFailure.PersistenceFailure &&
        !result.PersistenceSucceeded &&
        result.SelectedModuleId == "diagnostics" &&
        result.Snapshot.PersistenceState ==
            BridgeNetworkPakModulePersistenceState.FailedUnknown,
        "Persistence failure did not expose final authoritative runtime truth");
    Require(runtime.SelectionCount == selectionsBefore &&
        runtime.StreamClearCount == clearsBefore &&
        runtime.CurrentModule.ActiveModuleId == "diagnostics",
        "Persistence failure mutated runtime or cleared active streams");
}

static async Task TestRuntimeFailureRecoveryAsync()
{
    var (service, runtime, store) = Create();
    runtime.ThrowBeforeNextSelection = true;

    BridgeNetworkPakModuleSelectionResult result =
        await service.SelectModuleAsync("diagnostics");
    Require(!result.Success &&
        result.Failure == BridgeNetworkPakModuleSelectionFailure.RuntimeSelectionFailure &&
        result.PersistenceSucceeded &&
        result.SelectedModuleId == "network-portal" &&
        store.SavedIds.SequenceEqual(["diagnostics", "network-portal"]),
        "Runtime failure did not restore persistence to final runtime truth");

    var (failedService, failedRuntime, failedStore) = Create();
    failedRuntime.ThrowBeforeNextSelection = true;
    failedStore.ThrowOnSaveNumber = 2;
    BridgeNetworkPakModuleSelectionResult failedRecovery =
        await failedService.SelectModuleAsync("diagnostics");
    Require(!failedRecovery.Success &&
        failedRecovery.Failure == BridgeNetworkPakModuleSelectionFailure.RollbackFailure &&
        !failedRecovery.PersistenceSucceeded &&
        failedRecovery.SelectedModuleId == "network-portal" &&
        failedRecovery.Snapshot.PersistenceState ==
            BridgeNetworkPakModulePersistenceState.FailedUnknown,
        "Persistence-recovery failure was not reported truthfully");
}

static async Task TestCancellationAndSerializationAsync()
{
    var (service, runtime, store) = Create();
    BridgeNetworkPakModuleSnapshot before = service.Current;
    using CancellationTokenSource cancelled = new();
    cancelled.Cancel();
    BridgeNetworkPakModuleSelectionResult cancelledResult =
        await service.SelectModuleAsync("diagnostics", cancelled.Token);
    Require(cancelledResult.Failure ==
            BridgeNetworkPakModuleSelectionFailure.Cancelled &&
        runtime.SelectionCount == 0 && store.SaveCount == 0 &&
        service.Current.ActiveModuleId == before.ActiveModuleId &&
        service.Current.LastSelectionOutcome == before.LastSelectionOutcome,
        "Pre-cancelled selection performed work or changed semantic state");

    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    store.BlockNextSave = release.Task;
    Task<BridgeNetworkPakModuleSelectionResult> first =
        service.SelectModuleAsync("diagnostics");
    await store.SaveEntered.Task;
    BridgeNetworkPakModuleSelectionResult busy =
        await service.SelectModuleAsync("browser-live");
    Require(busy.Failure == BridgeNetworkPakModuleSelectionFailure.Busy &&
        !busy.PersistenceSucceeded && runtime.SelectionCount == 0,
        "Concurrent selection interleaved instead of returning Busy");
    release.SetResult();
    BridgeNetworkPakModuleSelectionResult completed = await first;
    Require(completed.Success && runtime.CurrentModule.ActiveModuleId == "diagnostics",
        "Serialized selection did not complete after the gate was released");
}

await TestEnumerationAndValidSelectionAsync();
await TestFallbackParityAsync("missing-module");
await TestFallbackParityAsync(null);
await TestFallbackParityAsync(string.Empty);
await TestPersistenceFailureDoesNotMutateRuntimeAsync();
await TestRuntimeFailureRecoveryAsync();
await TestCancellationAndSerializationAsync();
Console.WriteLine("PASS Bridge Network Pak module service headless semantics");

sealed class FakeRuntime : IBridgeNetworkPakModuleRuntime
{
    private readonly IReadOnlyList<BridgeNetworkPakModuleInfo> _modules =
    [
        new("network-portal", "Network Portal"),
        new("diagnostics", "Diagnostics"),
        new("browser-live", "Browser Live"),
        new("external-demo", "External Demo")
    ];
    private BridgeNetworkPakModuleInfo _active;

    public FakeRuntime() => _active = _modules[0];

    public string DefaultModuleId => "network-portal";
    public int SelectionCount { get; private set; }
    public int StreamClearCount { get; private set; }
    public int PicoMutationCount { get; private set; }
    public bool ThrowBeforeNextSelection { get; set; }
    public BridgeNetworkPakModuleRuntimeState CurrentModule =>
        new(_active.Id, _active.DisplayName);

    public IReadOnlyList<BridgeNetworkPakModuleInfo> ListSelectableModules() => _modules;

    public bool SetActiveModule(string? moduleId)
    {
        SelectionCount++;
        if (ThrowBeforeNextSelection)
        {
            ThrowBeforeNextSelection = false;
            throw new InvalidOperationException("runtime selection failed");
        }

        BridgeNetworkPakModuleInfo? selected = _modules.FirstOrDefault(module =>
            string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase));
        bool valid = selected is not null;
        _active = selected ?? _modules[0];
        StreamClearCount++;
        return valid;
    }
}

sealed class FakeStore : IBridgeNetworkPakModuleSelectionStore
{
    public List<string> SavedIds { get; } = [];
    public int SaveCount { get; private set; }
    public bool ThrowOnNextSave { get; set; }
    public int? ThrowOnSaveNumber { get; set; }
    public Task? BlockNextSave { get; set; }
    public TaskCompletionSource SaveEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask SaveAsync(
        string moduleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveCount++;
        SavedIds.Add(moduleId);
        if (ThrowOnNextSave || ThrowOnSaveNumber == SaveCount)
        {
            ThrowOnNextSave = false;
            throw new IOException("settings write failed");
        }

        if (BlockNextSave is Task blocker)
        {
            BlockNextSave = null;
            SaveEntered.TrySetResult();
            await blocker.WaitAsync(cancellationToken);
        }
    }
}
