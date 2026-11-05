namespace PicoSwitchController;

public sealed class BridgeNetworkPakCarrierSelectionStoreAdapter(string path) :
    IBridgeNetworkPakCarrierSelectionStore
{
    private readonly string _path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A settings path is required.", nameof(path))
        : path;

    public ValueTask<BridgeNetworkPakCarrierSelectionLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(new BridgeNetworkPakCarrierSelectionLoadResult(
                false, BridgeNetworkPakTransport.None, false,
                BridgeNetworkPakCarrierFailure.Cancelled,
                "Carrier selection load was cancelled."));
        }

        try
        {
            bool usedDefault = !File.Exists(_path);
            NetworkPakWifiTransport legacy = NetworkPakTransportSettings.Load(_path);
            BridgeNetworkPakTransport selection =
                NetworkPakCarrierSelection.ToBridgeTransport(legacy);
            return ValueTask.FromResult(new BridgeNetworkPakCarrierSelectionLoadResult(
                true, selection, usedDefault,
                BridgeNetworkPakCarrierFailure.None, null));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(new BridgeNetworkPakCarrierSelectionLoadResult(
                false, BridgeNetworkPakTransport.None, false,
                BridgeNetworkPakCarrierFailure.PersistenceFailed, ex.Message));
        }
    }

    public ValueTask<BridgeNetworkPakCarrierSelectionSaveResult> SaveAsync(
        BridgeNetworkPakTransport selection,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(new BridgeNetworkPakCarrierSelectionSaveResult(
                false, selection, BridgeNetworkPakCarrierFailure.Cancelled,
                "Carrier selection save was cancelled."));
        }

        if (!TryToLegacy(selection, out NetworkPakWifiTransport legacy))
        {
            return ValueTask.FromResult(new BridgeNetworkPakCarrierSelectionSaveResult(
                false, selection, BridgeNetworkPakCarrierFailure.InvalidCarrier,
                "The requested carrier cannot be stored in the legacy settings format."));
        }

        try
        {
            NetworkPakTransportSettings.Save(_path, legacy);
            return ValueTask.FromResult(new BridgeNetworkPakCarrierSelectionSaveResult(
                true, selection, BridgeNetworkPakCarrierFailure.None, null));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(new BridgeNetworkPakCarrierSelectionSaveResult(
                false, selection, BridgeNetworkPakCarrierFailure.PersistenceFailed,
                ex.Message));
        }
    }

    public static bool TryToLegacy(
        BridgeNetworkPakTransport carrier,
        out NetworkPakWifiTransport legacy)
    {
        switch (carrier)
        {
            case BridgeNetworkPakTransport.UsbSerial:
                legacy = NetworkPakWifiTransport.UsbSerial;
                return true;
            case BridgeNetworkPakTransport.Tcp:
                legacy = NetworkPakWifiTransport.Tcp;
                return true;
            case BridgeNetworkPakTransport.ReliableUdpQuick:
                legacy = NetworkPakWifiTransport.ReliableUdpQuick;
                return true;
            default:
                legacy = default;
                return false;
        }
    }
}

public sealed class DelegateBridgeNetworkPakCarrierActivationContext(
    Func<bool> activationRequired) : IBridgeNetworkPakCarrierActivationContext
{
    public bool ActivationRequired => activationRequired();
}

public sealed class DelegateBridgeNetworkPakLineCarrierPort(
    Func<BridgeNetworkPakTransport, bool> isAvailable,
    Func<BridgeNetworkPakTransport, long, CancellationToken,
        ValueTask<BridgeNetworkPakCarrierPortResult>> beginActivation,
    Func<BridgeNetworkPakTransport, CancellationToken,
        ValueTask<BridgeNetworkPakCarrierPortResult>> release) :
    IBridgeNetworkPakLineCarrierPort
{
    public bool IsAvailable(BridgeNetworkPakTransport carrier) => isAvailable(carrier);

    public ValueTask<BridgeNetworkPakCarrierPortResult> BeginActivationAsync(
        BridgeNetworkPakTransport carrier,
        long generation,
        CancellationToken cancellationToken = default) =>
        beginActivation(carrier, generation, cancellationToken);

    public ValueTask<BridgeNetworkPakCarrierPortResult> ReleaseAsync(
        BridgeNetworkPakTransport carrier,
        CancellationToken cancellationToken = default) =>
        release(carrier, cancellationToken);
}

public sealed class DelegateBridgeNetworkPakQuickCarrierPort(
    Func<bool> isAvailable,
    Func<long, CancellationToken, ValueTask<BridgeNetworkPakCarrierPortResult>> start,
    Func<long, CancellationToken, ValueTask<BridgeNetworkPakCarrierPortResult>> stop) :
    IBridgeNetworkPakQuickCarrierPort
{
    public bool IsAvailable => isAvailable();

    public ValueTask<BridgeNetworkPakCarrierPortResult> StartAsync(
        long generation,
        CancellationToken cancellationToken = default) =>
        start(generation, cancellationToken);

    public ValueTask<BridgeNetworkPakCarrierPortResult> StopAsync(
        long generation,
        CancellationToken cancellationToken = default) =>
        stop(generation, cancellationToken);
}

public sealed class DelegateBridgeNetworkPakApplicationOwnerPort(
    Action<bool> setConnected,
    Action<bool> setHandshakeComplete) : IBridgeNetworkPakApplicationOwnerPort
{
    public void SetConnected(bool connected) => setConnected(connected);
    public void SetHandshakeComplete(bool complete) => setHandshakeComplete(complete);
}

public sealed class DelegateBridgeNetworkPakCarrierDelay(
    Func<TimeSpan, CancellationToken, ValueTask> delay) :
    IBridgeNetworkPakCarrierDelay
{
    public ValueTask DelayAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default) =>
        delay(duration, cancellationToken);
}
