namespace PicoSwitchController;

using System.Collections.Concurrent;

public sealed record BridgeApiV1HistoryActivationRequest(
    bool DestructiveInstallConfirmed);

public enum BridgeApiV1HistoryActivationDisposition
{
    Accepted,
    ConfirmationRequired,
    Rejected,
    Busy,
    SessionNotFound,
    HistoryItemNotFound,
    HistoryItemUnavailable
}

public sealed record BridgeApiV1HistoryActivationConfirmation(
    string Category,
    string FileName,
    string ContentKind,
    string? Title);

public sealed record BridgeApiV1HistoryActivationResult(
    BridgeApiV1HistoryActivationDisposition Disposition,
    bool Accepted,
    long? OperationId,
    string? OperationKind,
    long? CurrentOperationId,
    string? CurrentOperationKind,
    string? Code,
    string? SafeDetail,
    BridgeApiV1HistoryActivationConfirmation? Confirmation);

internal interface IBridgeApiV1HistoryActivationFacade
{
    Task<BridgeApiV1HistoryActivationResult> StartAsync(
        string? sessionId,
        string? itemId,
        BridgeApiV1HistoryActivationRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class BridgeApiV1HistoryActivationFacade(
    IBridgeSessionProvider sessions,
    IBridgeApiV1HistoryItemResolver resolver,
    IBridgeHistoryActivationStarter activation) :
    IBridgeApiV1HistoryActivationFacade
{
    public const string HistoryOperationKind = "history-activation";

    private readonly IBridgeSessionProvider _sessions = sessions ??
        throw new ArgumentNullException(nameof(sessions));
    private readonly IBridgeApiV1HistoryItemResolver _resolver = resolver ??
        throw new ArgumentNullException(nameof(resolver));
    private readonly IBridgeHistoryActivationStarter _activation = activation ??
        throw new ArgumentNullException(nameof(activation));
    private readonly ConcurrentDictionary<long, Task> _executions = new();

    internal int ActiveExecutionCount => _executions.Count;

    public async Task<BridgeApiV1HistoryActivationResult> StartAsync(
        string? sessionId,
        string? itemId,
        BridgeApiV1HistoryActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        BridgeApiV1HistoryActivationResult? sessionFailure =
            ValidateSession(sessionId);
        if (sessionFailure is not null) return sessionFailure;

        BridgeApiV1HistoryResolution resolution = _resolver.Resolve(itemId);
        if (resolution.Status != BridgeApiV1HistoryResolutionStatus.Resolved ||
            resolution.Item is null)
            return ItemNotFound();
        BridgeApiV1ResolvedHistoryItem item = resolution.Item;
        if (!item.Available) return ItemUnavailable();

        if (item.Category == BridgeHistoryCategory.ControllerPak &&
            request.DestructiveInstallConfirmed)
        {
            return Rejected("invalid-confirmation",
                "Destructive installation confirmation is valid only for Transfer Pak History.");
        }

        Func<bool> admissionGuard = () => AuthorityStillCurrent(
            sessionId!, itemId!, item);
        BridgeHistoryActivationStart start;
        try
        {
            start = item.Category switch
            {
                BridgeHistoryCategory.ControllerPak =>
                    _activation.BeginControllerPak(item.Path,
                        cancellationToken: cancellationToken,
                        admissionGuard: admissionGuard),
                BridgeHistoryCategory.TransferPak =>
                    await _activation.BeginTransferPakAsync(item.Path,
                        request.DestructiveInstallConfirmed,
                        cancellationToken: cancellationToken,
                        admissionGuard: admissionGuard).ConfigureAwait(false),
                _ => throw new InvalidOperationException()
            };
        }
        catch (OperationCanceledException)
        {
            return Rejected("request-cancelled",
                "The History activation request was cancelled.");
        }
        catch
        {
            return Rejected("activation-unavailable",
                "History activation could not be prepared safely.");
        }

        if (start.Disposition ==
            BridgeHistoryActivationStartDisposition.AdmissionGuardRejected)
            return CurrentAuthorityFailure(sessionId, itemId);
        if (start.Disposition == BridgeHistoryActivationStartDisposition.Busy)
            return Busy(start.Operation);
        if (start.Accepted)
        {
            Observe(start.OperationId!.Value, start.Completion!);
            return new(BridgeApiV1HistoryActivationDisposition.Accepted, true,
                start.OperationId, HistoryOperationKind, null, null, null, null, null);
        }

        BridgeHistoryActivationResult immediate = start.ImmediateResult!;
        if (item.Category == BridgeHistoryCategory.TransferPak &&
            immediate.Failure == BridgeHistoryActivationFailure.ConfirmationRequired)
        {
            BridgeApiV1HistoryActivationResult? changed =
                CurrentAuthorityFailureOrNull(sessionId, itemId, item);
            if (changed is not null) return changed;
            return ConfirmationRequired(item, immediate.Inspection);
        }
        return MapImmediate(immediate);
    }

    private bool AuthorityStillCurrent(
        string sessionId,
        string itemId,
        BridgeApiV1ResolvedHistoryItem expected)
    {
        if (ValidateSession(sessionId) is not null) return false;
        BridgeApiV1HistoryResolution current = _resolver.Resolve(itemId);
        return current.Status == BridgeApiV1HistoryResolutionStatus.Resolved &&
            current.Item is { Available: true } item &&
            item.Category == expected.Category &&
            string.Equals(item.Path, expected.Path,
                StringComparison.OrdinalIgnoreCase);
    }

    private BridgeApiV1HistoryActivationResult? ValidateSession(string? sessionId)
    {
        BridgeSessionSnapshot? current = _sessions.Current;
        if (current is null || string.IsNullOrWhiteSpace(sessionId) ||
            !string.Equals(current.SessionId, sessionId, StringComparison.Ordinal))
            return SessionNotFound();
        return current.State == BridgeSessionState.Connected
            ? null
            : Rejected("session-unavailable",
                "The requested Bridge session is temporarily unavailable.");
    }

    private BridgeApiV1HistoryActivationResult CurrentAuthorityFailure(
        string? sessionId,
        string? itemId) =>
        CurrentAuthorityFailureOrNull(sessionId, itemId, null) ??
        Rejected("activation-prerequisite-changed",
            "History activation prerequisites changed before admission.");

    private BridgeApiV1HistoryActivationResult? CurrentAuthorityFailureOrNull(
        string? sessionId,
        string? itemId,
        BridgeApiV1ResolvedHistoryItem? expected)
    {
        BridgeApiV1HistoryActivationResult? session = ValidateSession(sessionId);
        if (session is not null) return session;
        BridgeApiV1HistoryResolution current = _resolver.Resolve(itemId);
        if (current.Status != BridgeApiV1HistoryResolutionStatus.Resolved ||
            current.Item is null) return ItemNotFound();
        if (!current.Item.Available) return ItemUnavailable();
        if (expected is not null &&
            (current.Item.Category != expected.Category ||
             !string.Equals(current.Item.Path, expected.Path,
                 StringComparison.OrdinalIgnoreCase)))
            return ItemNotFound();
        return null;
    }

    private void Observe(long operationId, Task completion)
    {
        _executions[operationId] = completion;
        _ = ObserveAsync(operationId, completion);
    }

    private async Task ObserveAsync(long operationId, Task completion)
    {
        try { await completion.ConfigureAwait(false); }
        catch { }
        finally { _executions.TryRemove(operationId, out _); }
    }

    private static BridgeApiV1HistoryActivationResult MapImmediate(
        BridgeHistoryActivationResult result) => result.Failure switch
        {
            BridgeHistoryActivationFailure.Busy => Rejected(
                "operation-busy", "Another Bridge operation is already in progress."),
            BridgeHistoryActivationFailure.NormalModeRequired => Rejected(
                "normal-mode-required",
                "History activation requires Controller/Normal firmware."),
            BridgeHistoryActivationFailure.MissingFile => ItemUnavailable(),
            BridgeHistoryActivationFailure.InspectionFailure => Rejected(
                "inspection-failed",
                "The Transfer Pak History item could not be inspected safely."),
            BridgeHistoryActivationFailure.Cancelled => Rejected(
                "request-cancelled", "The History activation request was cancelled."),
            BridgeHistoryActivationFailure.InvalidPath => ItemNotFound(),
            _ => Rejected("activation-rejected",
                "The History activation request could not be accepted safely.")
        };

    private static BridgeApiV1HistoryActivationResult ConfirmationRequired(
        BridgeApiV1ResolvedHistoryItem item,
        BridgeTransferPakRomInspection? inspection) => new(
            BridgeApiV1HistoryActivationDisposition.ConfirmationRequired,
            false, null, null, null, null, "confirmation-required",
            "Transfer Pak full-ROM installation requires explicit confirmation.",
            new("transfer-pak", item.FileName, "game-boy-rom",
                SafeText(inspection?.Metadata.Title)));

    private static BridgeApiV1HistoryActivationResult Busy(
        BridgeOperationSnapshot current) => new(
            BridgeApiV1HistoryActivationDisposition.Busy, false, null, null,
            current.OperationId, SafeOperationKind(current.Kind),
            "operation-busy", "Another Bridge operation is already in progress.", null);

    private static BridgeApiV1HistoryActivationResult SessionNotFound() => new(
        BridgeApiV1HistoryActivationDisposition.SessionNotFound, false,
        null, null, null, null, "session-not-found",
        "The requested Bridge session is not active.", null);

    private static BridgeApiV1HistoryActivationResult ItemNotFound() => new(
        BridgeApiV1HistoryActivationDisposition.HistoryItemNotFound, false,
        null, null, null, null, "history-item-not-found",
        "The requested History item does not exist.", null);

    private static BridgeApiV1HistoryActivationResult ItemUnavailable() => new(
        BridgeApiV1HistoryActivationDisposition.HistoryItemUnavailable, false,
        null, null, null, null, "history-item-unavailable",
        "The requested History item is currently unavailable.", null);

    private static BridgeApiV1HistoryActivationResult Rejected(
        string code,
        string detail) => new(
            BridgeApiV1HistoryActivationDisposition.Rejected, false,
            null, null, null, null, code, detail, null);

    private static string? SafeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string safe = new(value.Where(character => !char.IsControl(character))
            .ToArray());
        safe = safe.Trim();
        return safe.Length <= 128 ? safe : safe[..128];
    }

    private static string SafeOperationKind(BridgeOperationKind kind) => kind switch
    {
        BridgeOperationKind.Connect => "connect",
        BridgeOperationKind.Disconnect => "disconnect",
        BridgeOperationKind.FirmwareQuery => "firmware-query",
        BridgeOperationKind.FirmwareModeSwitch => "firmware-mode-switch",
        BridgeOperationKind.AccessorySelection => "accessory-selection",
        BridgeOperationKind.ControllerPakCreate => "controller-pak-create",
        BridgeOperationKind.ControllerPakLoad => "controller-pak-load",
        BridgeOperationKind.ControllerPakSave => "controller-pak-save",
        BridgeOperationKind.TransferPakInstall => "transfer-pak-install",
        BridgeOperationKind.TransferPakSave => "transfer-pak-save",
        BridgeOperationKind.TransferPakLegacyLoad => "transfer-pak-legacy-load",
        BridgeOperationKind.WifiConfigureAndConnect => "wifi-configure-and-connect",
        BridgeOperationKind.WifiConnectRetained => "wifi-connect-retained",
        BridgeOperationKind.WifiDisconnect => "wifi-disconnect",
        BridgeOperationKind.WifiForget => "wifi-forget",
        BridgeOperationKind.NetworkPakModuleSelection =>
            "network-pak-module-selection",
        BridgeOperationKind.NetworkPakCarrierSelection =>
            "network-pak-carrier-selection",
        BridgeOperationKind.HistoryActivation => HistoryOperationKind,
        _ => "unknown"
    };
}
