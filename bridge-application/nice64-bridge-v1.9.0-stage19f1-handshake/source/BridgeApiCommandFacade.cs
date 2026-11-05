namespace PicoSwitchController;

public enum BridgeApiCommandDisposition
{
    Accepted,
    Busy,
    Rejected
}

public sealed record BridgeApiCommandResult(
    BridgeApiCommandDisposition Disposition,
    long? OperationId,
    BridgeOperationKind OperationKind,
    string? FailureCode,
    string? SafeDetail)
{
    public bool Accepted => Disposition == BridgeApiCommandDisposition.Accepted;
}

public interface IBridgeApiCommandFacade
{
    BridgeApiCommandResult SelectFirmware(FirmwarePersonality target);
    BridgeApiCommandResult SelectAccessory(BridgeAccessoryMode target);
    BridgeApiCommandResult SelectNetworkPakModule(string? moduleId);
    BridgeApiCommandResult SelectNetworkPakCarrier(BridgeNetworkPakTransport target);
    BridgeApiCommandResult Connect(BridgeConnectionRequest? request);
    BridgeApiCommandResult Disconnect();
}

public sealed class BridgeApiCommandFacade : IBridgeApiCommandFacade
{
    private static readonly TimeSpan MaximumConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly BridgeOperationCoordinator _operations;
    private readonly IBridgeFirmwareService _firmware;
    private readonly IBridgeAccessoryService _accessory;
    private readonly IBridgeNetworkPakModuleService _modules;
    private readonly IBridgeNetworkPakCarrierService _carrier;
    private readonly IBridgeConnectionService _connection;

    public BridgeApiCommandFacade(
        BridgeOperationCoordinator operations,
        IBridgeFirmwareService firmware,
        IBridgeAccessoryService accessory,
        IBridgeNetworkPakModuleService modules,
        IBridgeNetworkPakCarrierService carrier,
        IBridgeConnectionService connection)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _firmware = firmware ?? throw new ArgumentNullException(nameof(firmware));
        _accessory = accessory ?? throw new ArgumentNullException(nameof(accessory));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _carrier = carrier ?? throw new ArgumentNullException(nameof(carrier));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public BridgeApiCommandResult SelectFirmware(FirmwarePersonality target)
    {
        if (target is not (FirmwarePersonality.Controller or
            FirmwarePersonality.Diagnostics or FirmwarePersonality.Network or
            FirmwarePersonality.Keyboard or FirmwarePersonality.Mouse))
            return Rejected(BridgeOperationKind.FirmwareModeSwitch,
                "invalid-firmware", "A supported firmware personality is required.");

        return Start(
            BridgeOperationKind.FirmwareModeSwitch,
            "Firmware",
            BridgeOperationCancellationCapability.BeforeCommitOnly,
            token => _firmware.SelectModeAsync(target, token),
            result => result.Success,
            result => result.Failure == FirmwareModeTransitionFailure.Cancelled,
            result => result.Failure.ToString(),
            result => result.Error);
    }

    public BridgeApiCommandResult SelectAccessory(BridgeAccessoryMode target)
    {
        if (target is not (BridgeAccessoryMode.None or BridgeAccessoryMode.ControllerPak or
            BridgeAccessoryMode.RumblePak or BridgeAccessoryMode.TransferPak or
            BridgeAccessoryMode.NetworkPak))
            return Rejected(BridgeOperationKind.AccessorySelection,
                "invalid-accessory", "A supported accessory is required.");

        return Start(
            BridgeOperationKind.AccessorySelection,
            "Accessory",
            BridgeOperationCancellationCapability.BeforeCommitOnly,
            token => _accessory.SelectAccessoryAsync(target, token),
            result => result.Success,
            result => result.Failure == BridgeAccessoryTransitionFailure.Cancelled,
            result => result.Failure.ToString(),
            result => result.Error);
    }

    public BridgeApiCommandResult SelectNetworkPakModule(string? moduleId)
    {
        string normalized = moduleId?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 256)
            return Rejected(BridgeOperationKind.NetworkPakModuleSelection,
                "invalid-module", "A valid Network Pak module ID is required.");

        return Start(
            BridgeOperationKind.NetworkPakModuleSelection,
            "NetworkPakModule",
            BridgeOperationCancellationCapability.BeforeCommitOnly,
            token => _modules.SelectModuleAsync(normalized, token),
            result => result.Success,
            result => result.Failure == BridgeNetworkPakModuleSelectionFailure.Cancelled,
            result => result.Failure.ToString(),
            result => result.Error);
    }

    public BridgeApiCommandResult SelectNetworkPakCarrier(
        BridgeNetworkPakTransport target)
    {
        if (target is not (BridgeNetworkPakTransport.Tcp or
            BridgeNetworkPakTransport.ReliableUdpQuick))
            return Rejected(BridgeOperationKind.NetworkPakCarrierSelection,
                "invalid-carrier", "A supported remote Network Pak carrier is required.");

        return Start(
            BridgeOperationKind.NetworkPakCarrierSelection,
            "NetworkPakCarrier",
            BridgeOperationCancellationCapability.BeforeCommitOnly,
            token => _carrier.SelectCarrierAsync(target, token),
            result => result.Success,
            result => result.Failure == BridgeNetworkPakCarrierFailure.Cancelled,
            result => result.Failure.ToString(),
            result => result.Diagnostic);
    }

    public BridgeApiCommandResult Connect(BridgeConnectionRequest? request)
    {
        if (!TryNormalizeConnectionRequest(request, out BridgeConnectionRequest? normalized))
            return Rejected(BridgeOperationKind.Connect,
                "invalid-connection", "A valid Bridge connection request is required.");

        return Start(
            BridgeOperationKind.Connect,
            "Connection",
            BridgeOperationCancellationCapability.Cooperative,
            token => _connection.ConnectAsync(normalized!, token),
            result => result.Success,
            result => result.Failure == BridgeConnectionFailure.Cancelled,
            result => result.Failure.ToString(),
            result => result.Error);
    }

    public BridgeApiCommandResult Disconnect() => Start(
        BridgeOperationKind.Disconnect,
        "Connection",
        BridgeOperationCancellationCapability.BeforeCommitOnly,
        token => _connection.DisconnectAsync(BridgeDisconnectReason.UserRequest, token),
        result => result.Success,
        result => result.Failure == BridgeDisconnectFailure.Cancelled,
        result => result.Failure.ToString(),
        result => result.Error);

    private BridgeApiCommandResult Start<TResult>(
        BridgeOperationKind kind,
        string domain,
        BridgeOperationCancellationCapability cancellationCapability,
        Func<CancellationToken, Task<TResult>> action,
        Func<TResult, bool> succeeded,
        Func<TResult, bool> cancelled,
        Func<TResult, string> failureCode,
        Func<TResult, string?> error)
    {
        BridgeOperationAdmission admission = _operations.TryBegin(
            kind, domain, cancellationCapability);
        if (!admission.Accepted)
        {
            return new(BridgeApiCommandDisposition.Busy,
                admission.Current.OperationId, admission.Current.Kind,
                "operation-busy", "Another Bridge operation is already in progress.");
        }

        BridgeOperationLease lease = admission.Lease!;
        _ = Task.Run(() => ExecuteObservedAsync(
            lease, action, succeeded, cancelled, failureCode, error));
        return new(BridgeApiCommandDisposition.Accepted, lease.OperationId, kind, null, null);
    }

    private static async Task ExecuteObservedAsync<TResult>(
        BridgeOperationLease lease,
        Func<CancellationToken, Task<TResult>> action,
        Func<TResult, bool> succeeded,
        Func<TResult, bool> cancelled,
        Func<TResult, string> failureCode,
        Func<TResult, string?> error)
    {
        using (lease)
        {
            try
            {
                TResult result = await action(CancellationToken.None).ConfigureAwait(false);
                if (succeeded(result))
                    lease.Succeed(result);
                else if (cancelled(result))
                    lease.Cancel(failureCode(result), error(result), result);
                else
                    lease.Fail(failureCode(result), error(result), result);
            }
            catch (OperationCanceledException ex)
            {
                lease.MarkCancellationRequested();
                lease.Cancel("Cancelled", ex.Message);
            }
            catch (Exception ex)
            {
                lease.Fail("UnexpectedFailure", ex.Message);
            }
        }
    }

    private static BridgeApiCommandResult Rejected(
        BridgeOperationKind kind, string failureCode, string safeDetail) =>
        new(BridgeApiCommandDisposition.Rejected, null, kind, failureCode, safeDetail);

    private static bool TryNormalizeConnectionRequest(
        BridgeConnectionRequest? request, out BridgeConnectionRequest? normalized)
    {
        normalized = null;
        if (request is null || request.ConnectionKind is not
                (BridgeControlConnectionKind.SerialUsb or BridgeControlConnectionKind.WifiTcp))
            return false;

        string endpoint = request.Endpoint?.Trim() ?? string.Empty;
        if (endpoint.Length is 0 or > 512) return false;

        if (request.Timeout is TimeSpan timeout &&
            (timeout <= TimeSpan.Zero || timeout > MaximumConnectTimeout))
            return false;

        if (request.ConnectionKind == BridgeControlConnectionKind.WifiTcp &&
            request.Port is < 1 or > 65535)
            return false;

        normalized = request with { Endpoint = endpoint };
        return true;
    }
}
