namespace PicoSwitchController;

public enum BridgeOperationKind
{
    None,
    Connect,
    Disconnect,
    FirmwareQuery,
    FirmwareModeSwitch,
    AccessorySelection,
    ControllerPakCreate,
    ControllerPakLoad,
    ControllerPakSave,
    TransferPakInstall,
    TransferPakSave,
    TransferPakLegacyLoad,
    WifiConfigureAndConnect,
    WifiConnectRetained,
    WifiDisconnect,
    WifiForget,
    NetworkPakModuleSelection,
    NetworkPakCarrierSelection,
    HistoryActivation
}

public enum BridgeOperationStatus
{
    Idle,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum BridgeOperationCancellationCapability
{
    None,
    Cooperative,
    BeforeCommitOnly,
    StopWaitingOnly
}

public sealed record BridgeOperationProgressEnvelope(
    long OperationId,
    BridgeOperationKind OperationKind,
    string Phase,
    long? CompletedUnits,
    long? TotalUnits,
    int? ItemIndex,
    int? ItemCount,
    string? Unit,
    string? Message,
    object? DomainProgress,
    DateTimeOffset TimestampUtc);

public sealed record BridgeOperationSnapshot(
    long OperationId,
    BridgeOperationKind Kind,
    BridgeOperationStatus Status,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    BridgeOperationProgressEnvelope? LatestProgress,
    BridgeOperationCancellationCapability CancellationCapability,
    bool CancellationRequested,
    string Domain,
    string? FailureCode,
    string? Error,
    object? DomainResult)
{
    public static BridgeOperationSnapshot Idle { get; } = new(
        0, BridgeOperationKind.None, BridgeOperationStatus.Idle, null, null, null,
        BridgeOperationCancellationCapability.None, false, string.Empty, null, null, null);
}

public sealed class BridgeOperationStateChangedEventArgs(
    BridgeOperationSnapshot previous,
    BridgeOperationSnapshot current) : EventArgs
{
    public BridgeOperationSnapshot Previous { get; } = previous;
    public BridgeOperationSnapshot Current { get; } = current;
}

public interface IBridgeOperationStateProvider
{
    BridgeOperationSnapshot Current { get; }
    event EventHandler<BridgeOperationStateChangedEventArgs>? CurrentChanged;
}

public sealed record BridgeOperationAdmission(
    bool Accepted,
    BridgeOperationLease? Lease,
    BridgeOperationSnapshot Current)
{
    public long? OperationId => Lease?.OperationId;
}

public sealed class BridgeOperationCoordinator : IBridgeOperationStateProvider
{
    private readonly object _sync = new();
    private long _nextOperationId;
    private BridgeOperationSnapshot _current = BridgeOperationSnapshot.Idle;

    public BridgeOperationSnapshot Current
    {
        get { lock (_sync) return _current; }
    }

    public event EventHandler<BridgeOperationStateChangedEventArgs>? CurrentChanged;

    public BridgeOperationAdmission TryBegin(
        BridgeOperationKind kind,
        string domain,
        BridgeOperationCancellationCapability cancellationCapability =
            BridgeOperationCancellationCapability.None)
    {
        if (kind == BridgeOperationKind.None)
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        BridgeOperationSnapshot previous;
        BridgeOperationSnapshot current;
        lock (_sync)
        {
            if (_current.Status == BridgeOperationStatus.Running)
                return new(false, null, _current);

            long operationId = checked(++_nextOperationId);
            previous = _current;
            current = new(operationId, kind, BridgeOperationStatus.Running,
                DateTimeOffset.UtcNow, null, null, cancellationCapability, false,
                domain, null, null, null);
            _current = current;
        }

        RaiseChanged(previous, current);
        return new(true, new BridgeOperationLease(this, current.OperationId), current);
    }

    internal bool UpdateProgress(long operationId, string phase,
        long? completedUnits, long? totalUnits, int? itemIndex, int? itemCount,
        string? unit, string? message, object? domainProgress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        return MutateRunning(operationId, current => current with
        {
            LatestProgress = new(operationId, current.Kind, phase,
                completedUnits, totalUnits, itemIndex, itemCount, unit, message,
                domainProgress, DateTimeOffset.UtcNow)
        });
    }

    internal bool UpdateCancellationCapability(long operationId,
        BridgeOperationCancellationCapability capability) =>
        MutateRunning(operationId, current => current with
        {
            CancellationCapability = capability
        });

    internal bool MarkCancellationRequested(long operationId) =>
        MutateRunning(operationId, current => current with
        {
            CancellationRequested = true
        });

    internal bool Complete(long operationId, BridgeOperationStatus status,
        string? failureCode, string? error, object? domainResult)
    {
        if (status is not (BridgeOperationStatus.Succeeded or
            BridgeOperationStatus.Failed or BridgeOperationStatus.Cancelled))
            throw new ArgumentOutOfRangeException(nameof(status));

        return MutateRunning(operationId, current => current with
        {
            Status = status,
            CompletedUtc = DateTimeOffset.UtcNow,
            CancellationCapability = BridgeOperationCancellationCapability.None,
            FailureCode = failureCode,
            Error = error,
            DomainResult = domainResult
        });
    }

    private bool MutateRunning(long operationId,
        Func<BridgeOperationSnapshot, BridgeOperationSnapshot> mutation)
    {
        BridgeOperationSnapshot previous;
        BridgeOperationSnapshot current;
        lock (_sync)
        {
            if (_current.Status != BridgeOperationStatus.Running ||
                _current.OperationId != operationId)
                return false;

            previous = _current;
            current = mutation(previous);
            if (current == previous) return true;
            _current = current;
        }

        RaiseChanged(previous, current);
        return true;
    }

    private void RaiseChanged(BridgeOperationSnapshot previous,
        BridgeOperationSnapshot current) =>
        CurrentChanged?.Invoke(this, new(previous, current));
}

public sealed class BridgeOperationLease : IDisposable
{
    private BridgeOperationCoordinator? _coordinator;

    internal BridgeOperationLease(BridgeOperationCoordinator coordinator, long operationId)
    {
        _coordinator = coordinator;
        OperationId = operationId;
    }

    public long OperationId { get; }

    public bool ReportProgress(string phase, long? completedUnits = null,
        long? totalUnits = null, int? itemIndex = null, int? itemCount = null,
        string? unit = null, string? message = null, object? domainProgress = null) =>
        _coordinator?.UpdateProgress(OperationId, phase, completedUnits, totalUnits,
            itemIndex, itemCount, unit, message, domainProgress) == true;

    public bool SetCancellationCapability(
        BridgeOperationCancellationCapability capability) =>
        _coordinator?.UpdateCancellationCapability(OperationId, capability) == true;

    public bool MarkCancellationRequested() =>
        _coordinator?.MarkCancellationRequested(OperationId) == true;

    public bool Succeed(object? domainResult = null) =>
        Finish(BridgeOperationStatus.Succeeded, null, null, domainResult);

    public bool Fail(string failureCode, string? error, object? domainResult = null) =>
        Finish(BridgeOperationStatus.Failed, failureCode, error, domainResult);

    public bool Cancel(string? failureCode, string? error, object? domainResult = null) =>
        Finish(BridgeOperationStatus.Cancelled, failureCode, error, domainResult);

    private bool Finish(BridgeOperationStatus status, string? failureCode,
        string? error, object? domainResult)
    {
        BridgeOperationCoordinator? coordinator = Interlocked.Exchange(ref _coordinator, null);
        return coordinator?.Complete(OperationId, status, failureCode, error, domainResult) == true;
    }

    public void Dispose()
    {
        BridgeOperationCoordinator? coordinator = Interlocked.Exchange(ref _coordinator, null);
        coordinator?.Complete(OperationId, BridgeOperationStatus.Failed,
            "OperationAbandoned", "The operation ended without a terminal result.", null);
    }
}
