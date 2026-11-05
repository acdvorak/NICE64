using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static BridgeOperationLease Begin(BridgeOperationCoordinator coordinator,
    BridgeOperationKind kind, BridgeOperationCancellationCapability capability =
        BridgeOperationCancellationCapability.Cooperative)
{
    BridgeOperationAdmission admission = coordinator.TryBegin(kind, kind.ToString(), capability);
    Require(admission.Accepted && admission.Lease is not null &&
        admission.OperationId == admission.Current.OperationId,
        $"{kind} was not admitted");
    return admission.Lease!;
}

static void TestLifecycleProgressAndCancellation()
{
    BridgeOperationCoordinator coordinator = new();
    Require(coordinator.Current == BridgeOperationSnapshot.Idle,
        "Initial operation state was not Idle");

    List<BridgeOperationSnapshot> changes = [];
    coordinator.CurrentChanged += (_, args) => changes.Add(args.Current);

    long firstId;
    using (BridgeOperationLease first = Begin(coordinator,
               BridgeOperationKind.ControllerPakLoad,
               BridgeOperationCancellationCapability.BeforeCommitOnly))
    {
        firstId = first.OperationId;
        BridgeOperationSnapshot running = coordinator.Current;
        Require(firstId > 0 && running.Status == BridgeOperationStatus.Running &&
            running.StartedUtc.HasValue && !running.CompletedUtc.HasValue,
            "Accepted operation did not publish identity/running/start state");

        object domainProgress = new { Block = 2 };
        Require(first.ReportProgress("Uploading", 64, 32768, 2, 1024,
                "bytes", "block", domainProgress),
            "Current progress was rejected");
        Require(coordinator.Current.LatestProgress is
            { CompletedUnits: 64, TotalUnits: 32768, DomainProgress: not null },
            "Latest progress was not retained");
        Require(first.SetCancellationCapability(
                BridgeOperationCancellationCapability.StopWaitingOnly) &&
            first.MarkCancellationRequested(),
            "Cancellation state update was rejected");
        Require(coordinator.Current.CancellationRequested &&
            coordinator.Current.CancellationCapability ==
                BridgeOperationCancellationCapability.StopWaitingOnly &&
            coordinator.Current.Status == BridgeOperationStatus.Running,
            "Cancellation request was conflated with capability or terminal state");

        object domainResult = new { Success = true };
        Require(first.Succeed(domainResult), "Success completion was rejected");
        BridgeOperationSnapshot succeeded = coordinator.Current;
        Require(succeeded.Status == BridgeOperationStatus.Succeeded &&
            succeeded.CompletedUtc.HasValue && ReferenceEquals(succeeded.DomainResult, domainResult),
            "Succeeded terminal state was not retained");
        int changesAtTerminal = changes.Count;
        Require(!first.ReportProgress("Late", 1, 1),
            "Post-terminal progress was accepted");
        Require(changes.Count == changesAtTerminal,
            "Post-terminal progress emitted CurrentChanged");
    }

    BridgeOperationSnapshot retained = coordinator.Current;
    Require(retained.OperationId == firstId &&
        retained.Status == BridgeOperationStatus.Succeeded,
        "Terminal snapshot was not retained after lease disposal");

    using BridgeOperationLease failed = Begin(coordinator,
        BridgeOperationKind.TransferPakInstall);
    Require(failed.OperationId > firstId, "Operation ID was not monotonically increasing");
    Require(coordinator.Current.LatestProgress is null,
        "New operation retained prior progress");
    Require(failed.Fail("TransferFailure", "wire failed", new { Success = false }),
        "Failure completion was rejected");
    Require(coordinator.Current is
        { Status: BridgeOperationStatus.Failed, FailureCode: "TransferFailure" },
        "Failed terminal state was not retained");

    using BridgeOperationLease cancelled = Begin(coordinator,
        BridgeOperationKind.WifiForget,
        BridgeOperationCancellationCapability.BeforeCommitOnly);
    cancelled.MarkCancellationRequested();
    Require(cancelled.Cancel("Cancelled", "caller cancelled"),
        "Cancellation completion was rejected");
    Require(coordinator.Current is
        { Status: BridgeOperationStatus.Cancelled, CancellationRequested: true,
          CompletedUtc: not null },
        "Cancelled terminal state was not retained");
    Require(changes.Count >= 8, "CurrentChanged omitted meaningful state changes");
}

static void TestConflict(BridgeOperationKind firstKind, BridgeOperationKind conflictingKind)
{
    BridgeOperationCoordinator coordinator = new();
    using BridgeOperationLease owner = Begin(coordinator, firstKind);
    long ownerId = owner.OperationId;
    BridgeOperationAdmission conflict = coordinator.TryBegin(conflictingKind,
        conflictingKind.ToString());
    Require(!conflict.Accepted && conflict.Lease is null && conflict.OperationId is null,
        $"{firstKind} did not reject {conflictingKind}");
    Require(coordinator.Current.OperationId == ownerId &&
        coordinator.Current.Kind == firstKind &&
        coordinator.Current.Status == BridgeOperationStatus.Running,
        "Conflict changed the current operation");
    owner.Succeed();

    using BridgeOperationLease next = Begin(coordinator, conflictingKind);
    Require(next.OperationId == ownerId + 1,
        "Rejected conflict consumed an operation ID");
    next.Succeed();
}

static void TestStaleProgressAndReplacementContinuity()
{
    BridgeOperationCoordinator coordinator = new();
    BridgeOperationLease firmware = Begin(coordinator,
        BridgeOperationKind.FirmwareModeSwitch,
        BridgeOperationCancellationCapability.BeforeCommitOnly);
    long firmwareId = firmware.OperationId;
    firmware.ReportProgress("RebootExpected", domainProgress: new { Session = "old" });
    firmware.SetCancellationCapability(
        BridgeOperationCancellationCapability.StopWaitingOnly);
    firmware.ReportProgress("Reconnecting", domainProgress: new { Session = "replacement" });
    Require(coordinator.Current.OperationId == firmwareId &&
        coordinator.Current.LatestProgress?.Phase == "Reconnecting",
        "Firmware parent identity did not survive connection replacement progress");
    firmware.Succeed(new { Reconnected = true });

    using BridgeOperationLease content = Begin(coordinator,
        BridgeOperationKind.ControllerPakSave);
    BridgeOperationProgressEnvelope? before = coordinator.Current.LatestProgress;
    Require(!firmware.ReportProgress("Stale", 1, 1) &&
        coordinator.Current.LatestProgress == before,
        "Progress from a prior operation ID was accepted");
    content.Succeed();
}

static async Task TestCoordinatedWifiBoundaryAsync()
{
    BridgeOperationCoordinator coordinator = new();
    FakeWifiProvisioningService inner = new();
    BridgeCoordinatedWifiProvisioningService service = new(inner, coordinator);

    using (BridgeOperationLease content = Begin(coordinator,
               BridgeOperationKind.ControllerPakSave))
    {
        BridgeWifiProvisioningResult busy =
            await service.ConfigureAndConnectAsync("ssid", "password");
        Require(!busy.Success && busy.Failure == BridgeWifiProvisioningFailure.Busy &&
            inner.MutationCount == 0 &&
            coordinator.Current.OperationId == content.OperationId,
            "Reusable Wi-Fi mutation did not return deterministic global conflict");

        BridgeWifiProvisioningResult status = await service.GetStatusAsync();
        Require(status.Success && inner.StatusCount == 1,
            "Wi-Fi status read incorrectly acquired mutable-operation ownership");
        content.Succeed();
    }

    BridgeWifiProvisioningResult accepted =
        await service.ConfigureAndConnectAsync("ssid", "password");
    Require(accepted.Success && inner.MutationCount == 1 &&
        coordinator.Current.Kind == BridgeOperationKind.WifiConfigureAndConnect &&
        coordinator.Current.Status == BridgeOperationStatus.Succeeded &&
        ReferenceEquals(coordinator.Current.DomainResult, accepted),
        "Accepted Wi-Fi mutation did not retain its typed domain result");
}

TestLifecycleProgressAndCancellation();
TestConflict(BridgeOperationKind.ControllerPakLoad,
    BridgeOperationKind.TransferPakInstall);
TestConflict(BridgeOperationKind.ControllerPakLoad,
    BridgeOperationKind.FirmwareModeSwitch);
TestConflict(BridgeOperationKind.FirmwareModeSwitch,
    BridgeOperationKind.ControllerPakLoad);
TestConflict(BridgeOperationKind.ControllerPakSave,
    BridgeOperationKind.AccessorySelection);
TestConflict(BridgeOperationKind.AccessorySelection,
    BridgeOperationKind.ControllerPakSave);
TestConflict(BridgeOperationKind.Disconnect,
    BridgeOperationKind.TransferPakSave);
TestConflict(BridgeOperationKind.TransferPakSave,
    BridgeOperationKind.Disconnect);
TestConflict(BridgeOperationKind.WifiConfigureAndConnect,
    BridgeOperationKind.FirmwareModeSwitch);
TestStaleProgressAndReplacementContinuity();
await TestCoordinatedWifiBoundaryAsync();

Console.WriteLine("PASS Bridge operation state and global ownership semantics");

sealed class FakeWifiProvisioningService : IBridgeWifiProvisioningService
{
    public int MutationCount { get; private set; }
    public int StatusCount { get; private set; }
    public BridgeWifiProvisioningSnapshot Current { get; private set; } =
        BridgeWifiProvisioningSnapshot.Unknown;

    public Task<BridgeWifiProvisioningResult> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        StatusCount++;
        return Task.FromResult(new BridgeWifiProvisioningResult(true,
            BridgeWifiProvisioningOperation.GetStatus,
            BridgeWifiProvisioningFailure.None, null, Current));
    }

    public Task<BridgeWifiProvisioningResult> ConfigureAndConnectAsync(
        string ssid, string password, CancellationToken cancellationToken = default) =>
        Mutate(BridgeWifiProvisioningOperation.ConfigureAndConnect);

    public Task<BridgeWifiProvisioningResult> ConnectRetainedAsync(
        CancellationToken cancellationToken = default) =>
        Mutate(BridgeWifiProvisioningOperation.ConnectRetained);

    public Task<BridgeWifiProvisioningResult> DisconnectAsync(
        CancellationToken cancellationToken = default) =>
        Mutate(BridgeWifiProvisioningOperation.Disconnect);

    public Task<BridgeWifiProvisioningResult> ForgetAsync(
        CancellationToken cancellationToken = default) =>
        Mutate(BridgeWifiProvisioningOperation.Forget);

    private Task<BridgeWifiProvisioningResult> Mutate(
        BridgeWifiProvisioningOperation operation)
    {
        MutationCount++;
        return Task.FromResult(new BridgeWifiProvisioningResult(true, operation,
            BridgeWifiProvisioningFailure.None, null, Current));
    }
}
