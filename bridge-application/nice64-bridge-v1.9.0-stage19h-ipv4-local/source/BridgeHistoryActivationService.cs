namespace PicoSwitchController;

public enum BridgeHistoryActivationFailure
{
    None,
    Busy,
    Cancelled,
    InvalidPath,
    NormalModeRequired,
    MissingFile,
    ConfirmationRequired,
    InspectionFailure,
    AccessoryTransitionFailure,
    ContentFailure,
    UnexpectedFailure
}

public enum BridgeHistoryActivationPhase
{
    Inspecting,
    SelectingAccessory,
    LoadingContent,
    RecordingHistory,
    Completed
}

public sealed record BridgeHistoryActivationProgress(
    BridgeHistoryCategory Category,
    BridgeHistoryActivationPhase Phase,
    string Path,
    object? DomainProgress = null);

public sealed record BridgeHistoryActivationResult(
    bool ActivationSucceeded,
    bool HistoryRecorded,
    bool StateChanged,
    BridgeHistoryCategory Category,
    string Path,
    BridgeHistoryActivationFailure Failure,
    string Error)
{
    public BridgeAccessoryTransitionResult? AccessoryResult { get; init; }
    public BridgeControllerPakContentResult? ControllerPakResult { get; init; }
    public BridgeTransferPakContentResult? TransferPakResult { get; init; }
    public BridgeTransferPakRomInspection? Inspection { get; init; }
    public BridgeHistoryResult? HistoryResult { get; init; }
}

public interface IBridgeHistoryActivationFileSystem
{
    string NormalizePath(string path);
    bool FileExists(string path);
}

public sealed class BridgeHistoryActivationFileSystem : IBridgeHistoryActivationFileSystem
{
    public string NormalizePath(string path) => System.IO.Path.GetFullPath(path);
    public bool FileExists(string path) => File.Exists(path);
}

public interface IBridgeHistoryActivationService
{
    Task<BridgeTransferPakContentResult> InspectTransferPakAsync(string romPath,
        CancellationToken cancellationToken = default);

    Task<BridgeHistoryActivationResult> ActivateControllerPakAsync(string path,
        IProgress<BridgeHistoryActivationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<BridgeHistoryActivationResult> ActivateTransferPakAsync(string romPath,
        bool destructiveInstallConfirmed,
        IProgress<BridgeHistoryActivationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

internal enum BridgeHistoryActivationStartDisposition
{
    Accepted,
    Immediate,
    Busy,
    AdmissionGuardRejected
}

internal sealed record BridgeHistoryActivationStart(
    BridgeHistoryActivationStartDisposition Disposition,
    long? OperationId,
    Task<BridgeHistoryActivationResult>? Completion,
    BridgeHistoryActivationResult? ImmediateResult,
    BridgeOperationSnapshot Operation)
{
    public bool Accepted =>
        Disposition == BridgeHistoryActivationStartDisposition.Accepted;
}

internal interface IBridgeHistoryActivationStarter
{
    BridgeHistoryActivationStart BeginControllerPak(
        string path,
        IProgress<BridgeHistoryActivationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<bool>? admissionGuard = null);

    Task<BridgeHistoryActivationStart> BeginTransferPakAsync(
        string romPath,
        bool destructiveInstallConfirmed,
        IProgress<BridgeHistoryActivationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<bool>? admissionGuard = null);
}

public sealed class BridgeHistoryActivationService :
    IBridgeHistoryActivationService, IBridgeHistoryActivationStarter
{
    private readonly IBridgeHistoryService _history;
    private readonly IBridgeDeviceStateProvider _device;
    private readonly IBridgeAccessoryService _accessory;
    private readonly IBridgeControllerPakContentService _controllerPak;
    private readonly IBridgeTransferPakContentService _transferPak;
    private readonly BridgeOperationCoordinator _operations;
    private readonly IBridgeHistoryActivationFileSystem _files;

    public BridgeHistoryActivationService(
        IBridgeHistoryService history,
        IBridgeDeviceStateProvider device,
        IBridgeAccessoryService accessory,
        IBridgeControllerPakContentService controllerPak,
        IBridgeTransferPakContentService transferPak,
        BridgeOperationCoordinator operations,
        IBridgeHistoryActivationFileSystem? files = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _accessory = accessory ?? throw new ArgumentNullException(nameof(accessory));
        _controllerPak = controllerPak ?? throw new ArgumentNullException(nameof(controllerPak));
        _transferPak = transferPak ?? throw new ArgumentNullException(nameof(transferPak));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _files = files ?? new BridgeHistoryActivationFileSystem();
    }

    public Task<BridgeTransferPakContentResult> InspectTransferPakAsync(string romPath,
        CancellationToken cancellationToken = default) =>
        _transferPak.InspectRomAsync(romPath, cancellationToken);

    public async Task<BridgeHistoryActivationResult> ActivateControllerPakAsync(
        string path,
        IProgress<BridgeHistoryActivationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BridgeHistoryActivationStart start = BeginControllerPak(
            path, progress, cancellationToken);
        return start.Accepted
            ? await start.Completion!.ConfigureAwait(false)
            : start.ImmediateResult!;
    }

    BridgeHistoryActivationStart IBridgeHistoryActivationStarter.BeginControllerPak(
        string path,
        IProgress<BridgeHistoryActivationProgress>? progress,
        CancellationToken cancellationToken,
        Func<bool>? admissionGuard) =>
        BeginControllerPak(path, progress, cancellationToken, admissionGuard);

    internal BridgeHistoryActivationStart BeginControllerPak(
        string path,
        IProgress<BridgeHistoryActivationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<bool>? admissionGuard = null)
    {
        BridgeHistoryActivationResult? preflight = Preflight(
            BridgeHistoryCategory.ControllerPak, path, ".mpk", out string normalized);
        if (preflight is not null) return Immediate(preflight);
        if (cancellationToken.IsCancellationRequested)
            return Immediate(Failure(BridgeHistoryCategory.ControllerPak, normalized,
                BridgeHistoryActivationFailure.Cancelled,
                "History activation was cancelled before it began."));
        if (admissionGuard is not null && !admissionGuard())
            return GuardRejected();

        BridgeOperationAdmission admission = _operations.TryBegin(
            BridgeOperationKind.HistoryActivation, "HistoryActivation",
            BridgeOperationCancellationCapability.BeforeCommitOnly);
        if (!admission.Accepted)
            return Busy(Failure(BridgeHistoryCategory.ControllerPak, normalized,
                BridgeHistoryActivationFailure.Busy,
                $"Bridge operation {admission.Current.Kind} is already in progress."),
                admission.Current);

        BridgeOperationLease lease = admission.Lease!;
        Task<BridgeHistoryActivationResult> completion =
            ExecuteControllerPakAsync(normalized, progress, cancellationToken, lease);
        return Accepted(lease.OperationId, completion, admission.Current);
    }

    private async Task<BridgeHistoryActivationResult> ExecuteControllerPakAsync(
        string normalized,
        IProgress<BridgeHistoryActivationProgress>? progress,
        CancellationToken cancellationToken,
        BridgeOperationLease lease)
    {
        await Task.Yield();
        using (lease)
        {
        BridgeAccessoryMode initialAccessory = _device.Current.ActiveAccessory;
        BridgeControllerPakContentSnapshot initialContent = _controllerPak.Current;
        try
        {
            if (_device.Current.FirmwarePersonality != FirmwarePersonality.Controller)
            {
                BridgeHistoryActivationResult changedMode = Failure(
                    BridgeHistoryCategory.ControllerPak, normalized,
                    BridgeHistoryActivationFailure.NormalModeRequired,
                    "History activation requires the Controller/Normal firmware personality.");
                lease.Fail(changedMode.Failure.ToString(), changedMode.Error, changedMode);
                return changedMode;
            }
            Report(progress, BridgeHistoryCategory.ControllerPak,
                BridgeHistoryActivationPhase.SelectingAccessory, normalized);
            lease.ReportProgress("SelectingAccessory", domainProgress: normalized);
            BridgeAccessoryTransitionResult transition =
                await _accessory.SelectAccessoryAsync(BridgeAccessoryMode.ControllerPak,
                    cancellationToken).ConfigureAwait(false);
            if (!transition.Success)
            {
                bool changed = initialAccessory != _device.Current.ActiveAccessory;
                BridgeHistoryActivationResult failed = Failure(
                    BridgeHistoryCategory.ControllerPak, normalized,
                    transition.Failure == BridgeAccessoryTransitionFailure.Cancelled
                        ? BridgeHistoryActivationFailure.Cancelled
                        : BridgeHistoryActivationFailure.AccessoryTransitionFailure,
                    transition.Error, changed) with { AccessoryResult = transition };
                CompleteFailure(lease, failed, canCancel: !changed);
                return failed;
            }

            lease.SetCancellationCapability(BridgeOperationCancellationCapability.None);
            Report(progress, BridgeHistoryCategory.ControllerPak,
                BridgeHistoryActivationPhase.LoadingContent, normalized);
            InlineProgress<BridgeControllerPakContentProgress> contentProgress = new(item =>
            {
                lease.ReportProgress(item.Phase.ToString(), item.CompletedBytes,
                    item.TotalBytes, item.ItemIndex, item.ItemCount, "bytes",
                    domainProgress: item);
                Report(progress, BridgeHistoryCategory.ControllerPak,
                    BridgeHistoryActivationPhase.LoadingContent, normalized, item);
            });
            BridgeControllerPakContentResult content =
                await _controllerPak.LoadAsync(normalized, contentProgress, cancellationToken)
                    .ConfigureAwait(false);
            if (!content.Success)
            {
                bool changed = HasControllerStateChanged(initialAccessory, initialContent);
                BridgeHistoryActivationResult failed = Failure(
                    BridgeHistoryCategory.ControllerPak, normalized,
                    BridgeHistoryActivationFailure.ContentFailure, content.Error, changed) with
                {
                    AccessoryResult = transition,
                    ControllerPakResult = content
                };
                CompleteFailure(lease, failed, canCancel: false);
                return failed;
            }

            Report(progress, BridgeHistoryCategory.ControllerPak,
                BridgeHistoryActivationPhase.RecordingHistory, normalized);
            BridgeHistoryResult history = await _history.RecordControllerPakAsync(
                normalized, CancellationToken.None).ConfigureAwait(false);
            BridgeHistoryActivationResult result = new(true, history.Success, true,
                BridgeHistoryCategory.ControllerPak, normalized,
                BridgeHistoryActivationFailure.None, string.Empty)
            {
                AccessoryResult = transition,
                ControllerPakResult = content,
                HistoryResult = history
            };
            lease.Succeed(result);
            Report(progress, BridgeHistoryCategory.ControllerPak,
                BridgeHistoryActivationPhase.Completed, normalized, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            bool changed = HasControllerStateChanged(initialAccessory, initialContent);
            BridgeHistoryActivationResult failed = Failure(
                BridgeHistoryCategory.ControllerPak, normalized,
                changed ? BridgeHistoryActivationFailure.ContentFailure
                    : BridgeHistoryActivationFailure.Cancelled,
                changed
                    ? "History activation was interrupted after semantic state changed."
                    : "History activation was cancelled before semantic state changed.", changed);
            CompleteFailure(lease, failed, canCancel: !changed);
            return failed;
        }
        catch (Exception ex)
        {
            bool changed = HasControllerStateChanged(initialAccessory, initialContent);
            BridgeHistoryActivationResult failed = Failure(
                BridgeHistoryCategory.ControllerPak, normalized,
                BridgeHistoryActivationFailure.UnexpectedFailure, ex.Message, changed);
            CompleteFailure(lease, failed, canCancel: false);
            return failed;
        }
        }
    }

    public async Task<BridgeHistoryActivationResult> ActivateTransferPakAsync(
        string romPath,
        bool destructiveInstallConfirmed,
        IProgress<BridgeHistoryActivationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BridgeHistoryActivationStart start = await BeginTransferPakAsync(
            romPath, destructiveInstallConfirmed, progress, cancellationToken)
            .ConfigureAwait(false);
        return start.Accepted
            ? await start.Completion!.ConfigureAwait(false)
            : start.ImmediateResult!;
    }

    Task<BridgeHistoryActivationStart>
        IBridgeHistoryActivationStarter.BeginTransferPakAsync(
            string romPath,
            bool destructiveInstallConfirmed,
            IProgress<BridgeHistoryActivationProgress>? progress,
            CancellationToken cancellationToken,
            Func<bool>? admissionGuard) =>
        BeginTransferPakAsync(romPath, destructiveInstallConfirmed, progress,
            cancellationToken, admissionGuard);

    internal async Task<BridgeHistoryActivationStart> BeginTransferPakAsync(
        string romPath,
        bool destructiveInstallConfirmed,
        IProgress<BridgeHistoryActivationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<bool>? admissionGuard = null)
    {
        BridgeHistoryActivationResult? preflight = Preflight(
            BridgeHistoryCategory.TransferPak, romPath, ".gb|.gbc", out string normalized);
        if (preflight is not null) return Immediate(preflight);
        if (cancellationToken.IsCancellationRequested)
            return Immediate(Failure(BridgeHistoryCategory.TransferPak, normalized,
                BridgeHistoryActivationFailure.Cancelled,
                "History activation was cancelled before it began."));

        Report(progress, BridgeHistoryCategory.TransferPak,
            BridgeHistoryActivationPhase.Inspecting, normalized);
        BridgeTransferPakContentResult inspection =
            await _transferPak.InspectRomAsync(normalized, cancellationToken)
                .ConfigureAwait(false);
        if (!inspection.Success)
            return Immediate(Failure(BridgeHistoryCategory.TransferPak, normalized,
                BridgeHistoryActivationFailure.InspectionFailure, inspection.Error) with
            {
                TransferPakResult = inspection,
                Inspection = inspection.Inspection
            });
        if (!destructiveInstallConfirmed)
            return Immediate(Failure(BridgeHistoryCategory.TransferPak, normalized,
                BridgeHistoryActivationFailure.ConfirmationRequired,
                "Transfer Pak full-ROM installation requires explicit caller confirmation.") with
            {
                TransferPakResult = inspection,
                Inspection = inspection.Inspection
            });
        if (admissionGuard is not null && !admissionGuard())
            return GuardRejected();

        BridgeOperationAdmission admission = _operations.TryBegin(
            BridgeOperationKind.HistoryActivation, "HistoryActivation",
            BridgeOperationCancellationCapability.BeforeCommitOnly);
        if (!admission.Accepted)
            return Busy(Failure(BridgeHistoryCategory.TransferPak, normalized,
                BridgeHistoryActivationFailure.Busy,
                $"Bridge operation {admission.Current.Kind} is already in progress.") with
            {
                Inspection = inspection.Inspection
            }, admission.Current);

        BridgeOperationLease lease = admission.Lease!;
        Task<BridgeHistoryActivationResult> completion = ExecuteTransferPakAsync(
            normalized, inspection, progress, cancellationToken, lease);
        return Accepted(lease.OperationId, completion, admission.Current);
    }

    private async Task<BridgeHistoryActivationResult> ExecuteTransferPakAsync(
        string normalized,
        BridgeTransferPakContentResult inspection,
        IProgress<BridgeHistoryActivationProgress>? progress,
        CancellationToken cancellationToken,
        BridgeOperationLease lease)
    {
        await Task.Yield();
        using (lease)
        {
        BridgeAccessoryMode initialAccessory = _device.Current.ActiveAccessory;
        BridgeTransferPakContentSnapshot initialContent = _transferPak.Current;
        try
        {
            if (_device.Current.FirmwarePersonality != FirmwarePersonality.Controller)
            {
                BridgeHistoryActivationResult changedMode = Failure(
                    BridgeHistoryCategory.TransferPak, normalized,
                    BridgeHistoryActivationFailure.NormalModeRequired,
                    "History activation requires the Controller/Normal firmware personality.") with
                {
                    Inspection = inspection.Inspection
                };
                lease.Fail(changedMode.Failure.ToString(), changedMode.Error, changedMode);
                return changedMode;
            }
            Report(progress, BridgeHistoryCategory.TransferPak,
                BridgeHistoryActivationPhase.SelectingAccessory, normalized);
            lease.ReportProgress("SelectingAccessory", domainProgress: normalized);
            BridgeAccessoryTransitionResult transition =
                await _accessory.SelectAccessoryAsync(BridgeAccessoryMode.TransferPak,
                    cancellationToken).ConfigureAwait(false);
            if (!transition.Success)
            {
                bool changed = initialAccessory != _device.Current.ActiveAccessory;
                BridgeHistoryActivationResult failed = Failure(
                    BridgeHistoryCategory.TransferPak, normalized,
                    transition.Failure == BridgeAccessoryTransitionFailure.Cancelled
                        ? BridgeHistoryActivationFailure.Cancelled
                        : BridgeHistoryActivationFailure.AccessoryTransitionFailure,
                    transition.Error, changed) with
                {
                    AccessoryResult = transition,
                    TransferPakResult = inspection,
                    Inspection = inspection.Inspection
                };
                CompleteFailure(lease, failed, canCancel: !changed);
                return failed;
            }

            lease.SetCancellationCapability(BridgeOperationCancellationCapability.None);
            Report(progress, BridgeHistoryCategory.TransferPak,
                BridgeHistoryActivationPhase.LoadingContent, normalized);
            InlineProgress<BridgeTransferPakContentProgress> contentProgress = new(item =>
            {
                lease.ReportProgress(item.Phase.ToString(), item.CompletedBytes,
                    item.TotalBytes, item.ItemIndex, item.ItemCount, "bytes",
                    domainProgress: item);
                Report(progress, BridgeHistoryCategory.TransferPak,
                    BridgeHistoryActivationPhase.LoadingContent, normalized, item);
            });
            BridgeTransferPakContentResult content =
                await _transferPak.InstallRomAsync(normalized, contentProgress,
                    cancellationToken).ConfigureAwait(false);
            if (!content.Success)
            {
                bool changed = HasTransferStateChanged(initialAccessory, initialContent);
                BridgeHistoryActivationResult failed = Failure(
                    BridgeHistoryCategory.TransferPak, normalized,
                    BridgeHistoryActivationFailure.ContentFailure, content.Error, changed) with
                {
                    AccessoryResult = transition,
                    TransferPakResult = content,
                    Inspection = inspection.Inspection
                };
                CompleteFailure(lease, failed, canCancel: false);
                return failed;
            }

            Report(progress, BridgeHistoryCategory.TransferPak,
                BridgeHistoryActivationPhase.RecordingHistory, normalized);
            BridgeHistoryResult history = await _history.RecordTransferPakAsync(
                normalized, CancellationToken.None).ConfigureAwait(false);
            BridgeHistoryActivationResult result = new(true, history.Success, true,
                BridgeHistoryCategory.TransferPak, normalized,
                BridgeHistoryActivationFailure.None, string.Empty)
            {
                AccessoryResult = transition,
                TransferPakResult = content,
                Inspection = inspection.Inspection,
                HistoryResult = history
            };
            lease.Succeed(result);
            Report(progress, BridgeHistoryCategory.TransferPak,
                BridgeHistoryActivationPhase.Completed, normalized, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            bool changed = HasTransferStateChanged(initialAccessory, initialContent);
            BridgeHistoryActivationResult failed = Failure(
                BridgeHistoryCategory.TransferPak, normalized,
                changed ? BridgeHistoryActivationFailure.ContentFailure
                    : BridgeHistoryActivationFailure.Cancelled,
                changed
                    ? "History activation was interrupted after semantic state changed."
                    : "History activation was cancelled before semantic state changed.", changed);
            CompleteFailure(lease, failed, canCancel: !changed);
            return failed;
        }
        catch (Exception ex)
        {
            bool changed = HasTransferStateChanged(initialAccessory, initialContent);
            BridgeHistoryActivationResult failed = Failure(
                BridgeHistoryCategory.TransferPak, normalized,
                BridgeHistoryActivationFailure.UnexpectedFailure, ex.Message, changed);
            CompleteFailure(lease, failed, canCancel: false);
            return failed;
        }
        }
    }

    private BridgeHistoryActivationStart Immediate(
        BridgeHistoryActivationResult result) => new(
            BridgeHistoryActivationStartDisposition.Immediate, null, null,
            result, _operations.Current);

    private static BridgeHistoryActivationStart Busy(
        BridgeHistoryActivationResult result,
        BridgeOperationSnapshot current) => new(
            BridgeHistoryActivationStartDisposition.Busy, null, null,
            result, current);

    private BridgeHistoryActivationStart GuardRejected() => new(
        BridgeHistoryActivationStartDisposition.AdmissionGuardRejected,
        null, null, null, _operations.Current);

    private static BridgeHistoryActivationStart Accepted(
        long operationId,
        Task<BridgeHistoryActivationResult> completion,
        BridgeOperationSnapshot current) => new(
            BridgeHistoryActivationStartDisposition.Accepted,
            operationId, completion, null, current);

    private BridgeHistoryActivationResult? Preflight(BridgeHistoryCategory category,
        string path, string extensions, out string normalized)
    {
        normalized = string.Empty;
        if (_device.Current.FirmwarePersonality != FirmwarePersonality.Controller)
            return Failure(category, path ?? string.Empty,
                BridgeHistoryActivationFailure.NormalModeRequired,
                "History activation requires the Controller/Normal firmware personality.");
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A History path is required.", nameof(path));
            normalized = _files.NormalizePath(path);
            string extension = System.IO.Path.GetExtension(normalized);
            bool valid = category == BridgeHistoryCategory.ControllerPak
                ? extension.Equals(".mpk", StringComparison.OrdinalIgnoreCase)
                : extension.Equals(".gb", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".gbc", StringComparison.OrdinalIgnoreCase);
            if (!valid)
                return Failure(category, normalized,
                    BridgeHistoryActivationFailure.InvalidPath,
                    $"History activation requires a {extensions} path.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
            PathTooLongException or System.Security.SecurityException)
        {
            return Failure(category, path ?? string.Empty,
                BridgeHistoryActivationFailure.InvalidPath, ex.Message);
        }
        if (!_files.FileExists(normalized))
            return Failure(category, normalized,
                BridgeHistoryActivationFailure.MissingFile,
                $"The History file does not exist: {normalized}");
        return null;
    }

    private bool HasControllerStateChanged(BridgeAccessoryMode initialAccessory,
        BridgeControllerPakContentSnapshot initialContent) =>
        initialAccessory != _device.Current.ActiveAccessory ||
        initialContent != _controllerPak.Current;

    private bool HasTransferStateChanged(BridgeAccessoryMode initialAccessory,
        BridgeTransferPakContentSnapshot initialContent) =>
        initialAccessory != _device.Current.ActiveAccessory ||
        initialContent != _transferPak.Current;

    private static BridgeHistoryActivationResult Failure(BridgeHistoryCategory category,
        string path, BridgeHistoryActivationFailure failure, string error,
        bool stateChanged = false) =>
        new(false, false, stateChanged, category, path, failure, error);

    private static void CompleteFailure(BridgeOperationLease lease,
        BridgeHistoryActivationResult result, bool canCancel)
    {
        if (result.Failure == BridgeHistoryActivationFailure.Cancelled && canCancel)
            lease.Cancel(result.Failure.ToString(), result.Error, result);
        else
            lease.Fail(result.Failure.ToString(), result.Error, result);
    }

    private static void Report(IProgress<BridgeHistoryActivationProgress>? progress,
        BridgeHistoryCategory category, BridgeHistoryActivationPhase phase,
        string path, object? domainProgress = null) =>
        progress?.Report(new(category, phase, path, domainProgress));

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public sealed record BridgeHistoryAssociationRecordingSnapshot(
    BridgeHistoryCategory Category,
    bool ContentSucceeded,
    bool RecordingRequired,
    bool HistoryRecorded,
    string? Path,
    BridgeHistoryResult? HistoryResult,
    string Error)
{
    public static BridgeHistoryAssociationRecordingSnapshot None(
        BridgeHistoryCategory category) =>
        new(category, false, false, false, null, null, string.Empty);
}

public interface IBridgeHistoryAssociationRecordingStatus
{
    BridgeHistoryAssociationRecordingSnapshot LastRecording { get; }
}

public sealed class BridgeHistoryRecordingControllerPakContentService :
    IBridgeControllerPakContentService, IBridgeHistoryAssociationRecordingStatus
{
    private readonly object _sync = new();
    private readonly IBridgeControllerPakContentService _inner;
    private readonly IBridgeHistoryService _history;
    private readonly Action<BridgeHistoryAssociationRecordingSnapshot>? _observer;
    private BridgeHistoryAssociationRecordingSnapshot _last =
        BridgeHistoryAssociationRecordingSnapshot.None(BridgeHistoryCategory.ControllerPak);

    public BridgeHistoryRecordingControllerPakContentService(
        IBridgeControllerPakContentService inner,
        IBridgeHistoryService history,
        Action<BridgeHistoryAssociationRecordingSnapshot>? observer = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _observer = observer;
    }

    public BridgeControllerPakContentSnapshot Current => _inner.Current;
    public BridgeHistoryAssociationRecordingSnapshot LastRecording
    {
        get { lock (_sync) return _last; }
    }

    public Task<BridgeControllerPakContentResult> CreateAsync(string? path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _inner.CreateAsync(path, progress, cancellationToken));

    public Task<BridgeControllerPakContentResult> LoadAsync(string path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _inner.LoadAsync(path, progress, cancellationToken));

    public Task<BridgeControllerPakContentResult> SaveAsync(string path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _inner.SaveAsync(path, progress, cancellationToken));

    private async Task<BridgeControllerPakContentResult> ExecuteAsync(
        Func<Task<BridgeControllerPakContentResult>> operation)
    {
        BridgeControllerPakContentResult content = await operation().ConfigureAwait(false);
        string? path = content.Success && content.Snapshot.HasAssociatedFile
            ? content.Snapshot.Path : null;
        await PublishRecordingAsync(BridgeHistoryCategory.ControllerPak, content.Success,
            path, path is null ? null : () => _history.RecordControllerPakAsync(
                path, CancellationToken.None)).ConfigureAwait(false);
        return content;
    }

    private async Task PublishRecordingAsync(BridgeHistoryCategory category,
        bool contentSucceeded, string? path,
        Func<Task<BridgeHistoryResult>>? record)
    {
        BridgeHistoryAssociationRecordingSnapshot snapshot;
        if (!contentSucceeded || record is null)
            snapshot = new(category, contentSucceeded, false, false, path, null, string.Empty);
        else
        {
            try
            {
                BridgeHistoryResult history = await record().ConfigureAwait(false);
                snapshot = new(category, true, true, history.Success, path, history,
                    history.Error);
            }
            catch (Exception ex)
            {
                snapshot = new(category, true, true, false, path, null, ex.Message);
            }
        }
        lock (_sync) _last = snapshot;
        try { _observer?.Invoke(snapshot); } catch { }
    }
}

public sealed class BridgeHistoryRecordingTransferPakContentService :
    IBridgeTransferPakContentService, IBridgeHistoryAssociationRecordingStatus
{
    private readonly object _sync = new();
    private readonly IBridgeTransferPakContentService _inner;
    private readonly IBridgeHistoryService _history;
    private readonly Action<BridgeHistoryAssociationRecordingSnapshot>? _observer;
    private BridgeHistoryAssociationRecordingSnapshot _last =
        BridgeHistoryAssociationRecordingSnapshot.None(BridgeHistoryCategory.TransferPak);

    public BridgeHistoryRecordingTransferPakContentService(
        IBridgeTransferPakContentService inner,
        IBridgeHistoryService history,
        Action<BridgeHistoryAssociationRecordingSnapshot>? observer = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _observer = observer;
    }

    public BridgeTransferPakContentSnapshot Current => _inner.Current;
    public BridgeHistoryAssociationRecordingSnapshot LastRecording
    {
        get { lock (_sync) return _last; }
    }

    public Task<BridgeTransferPakContentResult> InspectRomAsync(string path,
        CancellationToken cancellationToken = default) =>
        _inner.InspectRomAsync(path, cancellationToken);

    public async Task<BridgeTransferPakContentResult> InstallRomAsync(string path,
        IProgress<BridgeTransferPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BridgeTransferPakContentResult content =
            await _inner.InstallRomAsync(path, progress, cancellationToken)
                .ConfigureAwait(false);
        string? romPath = content.Success && content.Snapshot.HasAssociatedRom &&
            content.Snapshot.InstallKind == BridgeTransferPakInstallKind.FullFlash
            ? content.Snapshot.RomPath : null;
        await PublishRecordingAsync(content.Success, romPath).ConfigureAwait(false);
        return content;
    }

    public async Task<BridgeTransferPakContentResult> SaveCurrentAsync(string? path = null,
        IProgress<BridgeTransferPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BridgeTransferPakContentResult content =
            await _inner.SaveCurrentAsync(path, progress, cancellationToken)
                .ConfigureAwait(false);
        Publish(new(BridgeHistoryCategory.TransferPak, content.Success, false, false,
            null, null, string.Empty));
        return content;
    }

    private async Task PublishRecordingAsync(bool contentSucceeded, string? romPath)
    {
        BridgeHistoryAssociationRecordingSnapshot snapshot;
        if (!contentSucceeded || romPath is null)
            snapshot = new(BridgeHistoryCategory.TransferPak, contentSucceeded,
                false, false, romPath, null, string.Empty);
        else
        {
            try
            {
                BridgeHistoryResult history = await _history.RecordTransferPakAsync(
                    romPath, CancellationToken.None).ConfigureAwait(false);
                snapshot = new(BridgeHistoryCategory.TransferPak, true, true,
                    history.Success, romPath, history, history.Error);
            }
            catch (Exception ex)
            {
                snapshot = new(BridgeHistoryCategory.TransferPak, true, true,
                    false, romPath, null, ex.Message);
            }
        }
        Publish(snapshot);
    }

    private void Publish(BridgeHistoryAssociationRecordingSnapshot snapshot)
    {
        lock (_sync) _last = snapshot;
        try { _observer?.Invoke(snapshot); } catch { }
    }
}
