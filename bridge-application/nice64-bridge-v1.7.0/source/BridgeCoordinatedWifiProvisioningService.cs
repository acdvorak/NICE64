namespace PicoSwitchController;

public sealed class BridgeCoordinatedWifiProvisioningService(
    IBridgeWifiProvisioningService inner,
    BridgeOperationCoordinator operations) : IBridgeWifiProvisioningService
{
    private readonly IBridgeWifiProvisioningService _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly BridgeOperationCoordinator _operations =
        operations ?? throw new ArgumentNullException(nameof(operations));

    public BridgeWifiProvisioningSnapshot Current => _inner.Current;

    public Task<BridgeWifiProvisioningResult> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        _inner.GetStatusAsync(cancellationToken);

    public Task<BridgeWifiProvisioningResult> ConfigureAndConnectAsync(
        string ssid, string password, CancellationToken cancellationToken = default) =>
        ExecuteAsync(BridgeOperationKind.WifiConfigureAndConnect,
            BridgeWifiProvisioningOperation.ConfigureAndConnect,
            token => _inner.ConfigureAndConnectAsync(ssid, password, token),
            cancellationToken);

    public Task<BridgeWifiProvisioningResult> ConnectRetainedAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(BridgeOperationKind.WifiConnectRetained,
            BridgeWifiProvisioningOperation.ConnectRetained,
            _inner.ConnectRetainedAsync, cancellationToken);

    public Task<BridgeWifiProvisioningResult> DisconnectAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(BridgeOperationKind.WifiDisconnect,
            BridgeWifiProvisioningOperation.Disconnect,
            _inner.DisconnectAsync, cancellationToken);

    public Task<BridgeWifiProvisioningResult> ForgetAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(BridgeOperationKind.WifiForget,
            BridgeWifiProvisioningOperation.Forget,
            _inner.ForgetAsync, cancellationToken);

    private async Task<BridgeWifiProvisioningResult> ExecuteAsync(
        BridgeOperationKind kind,
        BridgeWifiProvisioningOperation operation,
        Func<CancellationToken, Task<BridgeWifiProvisioningResult>> action,
        CancellationToken cancellationToken)
    {
        BridgeOperationAdmission admission = _operations.TryBegin(kind, "WiFi",
            BridgeOperationCancellationCapability.BeforeCommitOnly);
        if (!admission.Accepted)
        {
            return new(false, operation, BridgeWifiProvisioningFailure.Busy,
                $"Bridge operation {admission.Current.Kind} is already in progress.",
                _inner.Current);
        }

        using BridgeOperationLease lease = admission.Lease!;
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((BridgeOperationLease)state!).MarkCancellationRequested(), lease);
        try
        {
            BridgeWifiProvisioningResult result = await action(cancellationToken)
                .ConfigureAwait(false);
            if (result.Success)
                lease.Succeed(result);
            else if (result.Failure == BridgeWifiProvisioningFailure.Cancelled)
                lease.Cancel(result.Failure.ToString(), result.Error, result);
            else
                lease.Fail(result.Failure.ToString(), result.Error, result);
            return result;
        }
        catch (OperationCanceledException ex)
        {
            lease.MarkCancellationRequested();
            lease.Cancel(BridgeWifiProvisioningFailure.Cancelled.ToString(), ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            lease.Fail(BridgeWifiProvisioningFailure.UnexpectedFailure.ToString(), ex.Message);
            throw;
        }
    }
}
