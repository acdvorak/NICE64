namespace PicoSwitchController;

public enum BridgeWifiProvisioningOperation
{
    None,
    GetStatus,
    ConfigureAndConnect,
    ConnectRetained,
    Disconnect,
    Forget
}

public enum BridgeWifiProvisioningOutcome
{
    None,
    Succeeded,
    Failed,
    Cancelled
}

public enum BridgeWifiProvisioningFailure
{
    None,
    Busy,
    Cancelled,
    TransportUnavailable,
    DeviceRejected,
    MalformedReply,
    Timeout,
    TransportFailure,
    ValidationFailure,
    StateUnknownAfterMutation,
    UnexpectedFailure
}

public sealed record BridgeWifiProvisioningSnapshot(
    bool DeviceStatusKnown,
    bool Configured,
    bool Connected,
    string? State,
    string? Mode,
    string? Ssid,
    string? Link,
    string? IpAddress,
    string? Netmask,
    string? Gateway,
    string? Dns,
    string? DeviceFailure,
    BridgeWifiProvisioningOperation LastOperation,
    BridgeWifiProvisioningOutcome LastOperationOutcome,
    string? LastError)
{
    public static BridgeWifiProvisioningSnapshot Unknown { get; } = new(
        false, false, false, null, null, null, null, null, null, null, null, null,
        BridgeWifiProvisioningOperation.None,
        BridgeWifiProvisioningOutcome.None,
        null);
}

public sealed record BridgeWifiProvisioningResult(
    bool Success,
    BridgeWifiProvisioningOperation Operation,
    BridgeWifiProvisioningFailure Failure,
    string? Error,
    BridgeWifiProvisioningSnapshot Snapshot);

public interface IBridgeWifiProvisioningService
{
    BridgeWifiProvisioningSnapshot Current { get; }
    Task<BridgeWifiProvisioningResult> GetStatusAsync(
        CancellationToken cancellationToken = default);
    Task<BridgeWifiProvisioningResult> ConfigureAndConnectAsync(
        string ssid,
        string password,
        CancellationToken cancellationToken = default);
    Task<BridgeWifiProvisioningResult> ConnectRetainedAsync(
        CancellationToken cancellationToken = default);
    Task<BridgeWifiProvisioningResult> DisconnectAsync(
        CancellationToken cancellationToken = default);
    Task<BridgeWifiProvisioningResult> ForgetAsync(
        CancellationToken cancellationToken = default);
}

public sealed class BridgeWifiProvisioningService : IBridgeWifiProvisioningService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly WifiProvisioningSession _session;
    private BridgeWifiProvisioningSnapshot _current =
        BridgeWifiProvisioningSnapshot.Unknown;

    public BridgeWifiProvisioningService(WifiProvisioningSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public BridgeWifiProvisioningSnapshot Current
    {
        get { lock (_sync) return _current; }
    }

    public Task<BridgeWifiProvisioningResult> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteStatusQueryAsync(cancellationToken);

    public Task<BridgeWifiProvisioningResult> ConfigureAndConnectAsync(
        string ssid,
        string password,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            BridgeWifiProvisioningOperation.ConfigureAndConnect,
            token => _session.ConfigureAndConnectAsync(ssid, password, token),
            cancellationToken);

    public Task<BridgeWifiProvisioningResult> ConnectRetainedAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            BridgeWifiProvisioningOperation.ConnectRetained,
            _session.ConnectRetainedAsync,
            cancellationToken);

    public Task<BridgeWifiProvisioningResult> DisconnectAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            BridgeWifiProvisioningOperation.Disconnect,
            _session.DisconnectCommandAsync,
            cancellationToken);

    public Task<BridgeWifiProvisioningResult> ForgetAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            BridgeWifiProvisioningOperation.Forget,
            _session.ClearCommandAsync,
            cancellationToken);

    private async Task<BridgeWifiProvisioningResult> ExecuteStatusQueryAsync(
        CancellationToken cancellationToken)
    {
        BridgeWifiProvisioningResult? admission =
            await TryEnterAsync(BridgeWifiProvisioningOperation.GetStatus, cancellationToken)
                .ConfigureAwait(false);
        if (admission is not null) return admission;

        try
        {
            if (!_session.IsConnected)
            {
                return SetUnknownFailure(BridgeWifiProvisioningOperation.GetStatus,
                    BridgeWifiProvisioningFailure.TransportUnavailable,
                    "NICE64 USB transport is unavailable.");
            }

            try
            {
                WifiStatus status = await _session.RefreshAsync(cancellationToken)
                    .ConfigureAwait(false);
                return SetConfirmedSuccess(BridgeWifiProvisioningOperation.GetStatus, status);
            }
            catch (Exception ex)
            {
                return SetUnknownFailure(BridgeWifiProvisioningOperation.GetStatus,
                    Classify(ex), SafeError(ex));
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<BridgeWifiProvisioningResult> ExecuteMutationAsync(
        BridgeWifiProvisioningOperation operation,
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken)
    {
        BridgeWifiProvisioningResult? admission =
            await TryEnterAsync(operation, cancellationToken).ConfigureAwait(false);
        if (admission is not null) return admission;

        try
        {
            if (!_session.IsConnected)
            {
                return SetUnknownFailure(operation,
                    BridgeWifiProvisioningFailure.TransportUnavailable,
                    "NICE64 USB transport is unavailable.");
            }

            BridgeWifiProvisioningFailure originalFailure =
                BridgeWifiProvisioningFailure.None;
            string? originalError = null;
            try
            {
                await mutation(cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                return SetFailureKeepingStatus(operation,
                    BridgeWifiProvisioningFailure.ValidationFailure,
                    "The Wi-Fi credentials are invalid.");
            }
            catch (Exception ex)
            {
                originalFailure = Classify(ex);
                originalError = SafeError(ex);
            }

            return await RefreshAfterPossibleMutationAsync(
                operation, originalFailure, originalError).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<BridgeWifiProvisioningResult> RefreshAfterPossibleMutationAsync(
        BridgeWifiProvisioningOperation operation,
        BridgeWifiProvisioningFailure originalFailure,
        string? originalError)
    {
        try
        {
            WifiStatus status = await _session.RefreshAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (originalFailure == BridgeWifiProvisioningFailure.None)
            {
                return SetConfirmedSuccess(operation, status);
            }

            return SetConfirmedFailure(operation, originalFailure,
                originalError ?? "The Wi-Fi operation failed.", status);
        }
        catch
        {
            return SetUnknownFailure(operation,
                BridgeWifiProvisioningFailure.StateUnknownAfterMutation,
                "The Wi-Fi operation may have changed device state, but authoritative status could not be refreshed.");
        }
    }

    private async Task<BridgeWifiProvisioningResult?> TryEnterAsync(
        BridgeWifiProvisioningOperation operation,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return SetFailureKeepingStatus(operation,
                BridgeWifiProvisioningFailure.Cancelled,
                "The Wi-Fi operation was cancelled.");
        }

        bool entered;
        try
        {
            entered = await _operationGate.WaitAsync(0, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return SetFailureKeepingStatus(operation,
                BridgeWifiProvisioningFailure.Cancelled,
                "The Wi-Fi operation was cancelled.");
        }

        return entered
            ? null
            : Result(false, operation, BridgeWifiProvisioningFailure.Busy,
                "Another Wi-Fi operation is already in progress.", Current);
    }

    private BridgeWifiProvisioningResult SetConfirmedSuccess(
        BridgeWifiProvisioningOperation operation,
        WifiStatus status)
    {
        BridgeWifiProvisioningSnapshot snapshot = FromStatus(status, operation,
            BridgeWifiProvisioningOutcome.Succeeded, null);
        SetCurrent(snapshot);
        return Result(true, operation, BridgeWifiProvisioningFailure.None, null, snapshot);
    }

    private BridgeWifiProvisioningResult SetConfirmedFailure(
        BridgeWifiProvisioningOperation operation,
        BridgeWifiProvisioningFailure failure,
        string error,
        WifiStatus status)
    {
        BridgeWifiProvisioningOutcome outcome = failure ==
            BridgeWifiProvisioningFailure.Cancelled
            ? BridgeWifiProvisioningOutcome.Cancelled
            : BridgeWifiProvisioningOutcome.Failed;
        BridgeWifiProvisioningSnapshot snapshot =
            FromStatus(status, operation, outcome, error);
        SetCurrent(snapshot);
        return Result(false, operation, failure, error, snapshot);
    }

    private BridgeWifiProvisioningResult SetUnknownFailure(
        BridgeWifiProvisioningOperation operation,
        BridgeWifiProvisioningFailure failure,
        string error)
    {
        BridgeWifiProvisioningOutcome outcome = failure ==
            BridgeWifiProvisioningFailure.Cancelled
            ? BridgeWifiProvisioningOutcome.Cancelled
            : BridgeWifiProvisioningOutcome.Failed;
        BridgeWifiProvisioningSnapshot snapshot = new(
            false, false, false, null, null, null, null, null, null, null, null, null,
            operation, outcome, error);
        SetCurrent(snapshot);
        return Result(false, operation, failure, error, snapshot);
    }

    private BridgeWifiProvisioningResult SetFailureKeepingStatus(
        BridgeWifiProvisioningOperation operation,
        BridgeWifiProvisioningFailure failure,
        string error)
    {
        BridgeWifiProvisioningSnapshot current = Current;
        BridgeWifiProvisioningSnapshot snapshot = current with
        {
            LastOperation = operation,
            LastOperationOutcome = failure == BridgeWifiProvisioningFailure.Cancelled
                ? BridgeWifiProvisioningOutcome.Cancelled
                : BridgeWifiProvisioningOutcome.Failed,
            LastError = error
        };
        SetCurrent(snapshot);
        return Result(false, operation, failure, error, snapshot);
    }

    private static BridgeWifiProvisioningSnapshot FromStatus(
        WifiStatus status,
        BridgeWifiProvisioningOperation operation,
        BridgeWifiProvisioningOutcome outcome,
        string? error) => new(
            true,
            IsConfigured(status),
            status.State.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase),
            status.State,
            status.Mode,
            status.Ssid,
            status.Link,
            status.IpAddress,
            status.Netmask,
            status.Gateway,
            status.Dns,
            status.Failure,
            operation,
            outcome,
            error);

    private static bool IsConfigured(WifiStatus status) => status.State.ToUpperInvariant() is
        "CONFIGURED" or "ASSOCIATING" or "WAITING_FOR_ADDRESS" or
        "CONNECTED" or "FAILED" or "DISCONNECTED";

    private static BridgeWifiProvisioningFailure Classify(Exception exception) =>
        exception switch
        {
            OperationCanceledException => BridgeWifiProvisioningFailure.Cancelled,
            TimeoutException => BridgeWifiProvisioningFailure.Timeout,
            WifiProvisioningDeviceRejectedException =>
                BridgeWifiProvisioningFailure.DeviceRejected,
            WifiProvisioningMalformedReplyException =>
                BridgeWifiProvisioningFailure.MalformedReply,
            IOException => BridgeWifiProvisioningFailure.TransportFailure,
            _ => BridgeWifiProvisioningFailure.UnexpectedFailure
        };

    private static string SafeError(Exception exception) => exception switch
    {
        OperationCanceledException => "The Wi-Fi operation was cancelled.",
        TimeoutException => "Timed out waiting for a NICE64 Wi-Fi response.",
        WifiProvisioningDeviceRejectedException rejected => rejected.Message,
        WifiProvisioningMalformedReplyException =>
            "NICE64 returned a malformed Wi-Fi reply.",
        IOException => "The NICE64 USB transport failed.",
        _ => "An unexpected Wi-Fi provisioning failure occurred."
    };

    private void SetCurrent(BridgeWifiProvisioningSnapshot snapshot)
    {
        lock (_sync) _current = snapshot;
    }

    private static BridgeWifiProvisioningResult Result(
        bool success,
        BridgeWifiProvisioningOperation operation,
        BridgeWifiProvisioningFailure failure,
        string? error,
        BridgeWifiProvisioningSnapshot snapshot) =>
        new(success, operation, failure, error, snapshot);
}
