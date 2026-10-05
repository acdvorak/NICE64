namespace PicoSwitchController;

public sealed record BridgeStateSnapshot(
    long CaptureSequence,
    DateTimeOffset CapturedUtc,
    BridgeDeviceStateSnapshot Device,
    BridgeControllerSnapshot Controller,
    BridgeControllerPakContentSnapshot ControllerPakContent,
    BridgeTransferPakContentSnapshot TransferPakContent,
    AccessoryPersistenceSnapshot Persistence,
    BridgeNetworkPakModuleSnapshot NetworkPakModule,
    BridgeWifiProvisioningSnapshot WiFi,
    BridgeOperationSnapshot Operation)
{
    public BridgeInstanceSnapshot Bridge { get; init; } =
        new(string.Empty, BridgeInstanceNamingPolicy.DefaultUnconnectedName);
}

public interface IBridgeStateProvider
{
    BridgeStateSnapshot GetSnapshot();
}

public sealed class BridgeStateAggregator : IBridgeStateProvider
{
    private readonly IBridgeHostStateProvider _device;
    private readonly IBridgeControllerService _controller;
    private readonly IBridgeControllerPakContentService _controllerPakContent;
    private readonly IBridgeTransferPakContentService _transferPakContent;
    private readonly IAccessoryPersistenceService _persistence;
    private readonly IBridgeNetworkPakModuleService _networkPakModule;
    private readonly IBridgeWifiProvisioningService _wifi;
    private readonly IBridgeOperationStateProvider _operation;
    private readonly IBridgeInstanceStateProvider _bridge;
    private long _captureSequence;

    public BridgeStateAggregator(
        IBridgeHostStateProvider device,
        IBridgeControllerService controller,
        IBridgeControllerPakContentService controllerPakContent,
        IBridgeTransferPakContentService transferPakContent,
        IAccessoryPersistenceService persistence,
        IBridgeNetworkPakModuleService networkPakModule,
        IBridgeWifiProvisioningService wifi,
        IBridgeOperationStateProvider operation,
        IBridgeInstanceStateProvider? bridge = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _controllerPakContent = controllerPakContent ??
            throw new ArgumentNullException(nameof(controllerPakContent));
        _transferPakContent = transferPakContent ??
            throw new ArgumentNullException(nameof(transferPakContent));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _networkPakModule = networkPakModule ??
            throw new ArgumentNullException(nameof(networkPakModule));
        _wifi = wifi ?? throw new ArgumentNullException(nameof(wifi));
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
        _bridge = bridge ?? new BridgeInstanceIdentity();
    }

    public BridgeStateSnapshot GetSnapshot()
    {
        BridgeDeviceStateSnapshot device = _device.Current;
        BridgeControllerSnapshot controller = _controller.Current;
        BridgeControllerPakContentSnapshot controllerPakContent =
            _controllerPakContent.Current;
        BridgeTransferPakContentSnapshot transferPakContent =
            _transferPakContent.Current;
        AccessoryPersistenceSnapshot persistence = _persistence.Current;
        BridgeNetworkPakModuleSnapshot networkPakModule = _networkPakModule.Current;
        BridgeWifiProvisioningSnapshot wifi = _wifi.Current;
        BridgeOperationSnapshot operation = _operation.Current;

        long captureSequence = Interlocked.Increment(ref _captureSequence);
        if (captureSequence <= 0)
            throw new InvalidOperationException("Bridge state capture sequence was exhausted.");

        return new(captureSequence, DateTimeOffset.UtcNow, device, controller,
            controllerPakContent, transferPakContent, persistence, networkPakModule,
            wifi, operation)
        {
            Bridge = _bridge.Current
        };
    }
}
