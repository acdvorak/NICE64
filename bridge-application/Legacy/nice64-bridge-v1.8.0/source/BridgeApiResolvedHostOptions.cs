namespace PicoSwitchController;

using System.Net;

public sealed class BridgeApiResolvedHostOptions
{
    private BridgeApiResolvedHostOptions(
        BridgeApiAccessMode accessMode,
        int port,
        IReadOnlyList<BridgeApiResolvedListener> listeners,
        TimeSpan shutdownTimeout)
    {
        if (accessMode is not BridgeApiAccessMode.LocalOnly and
            not BridgeApiAccessMode.LocalNetwork)
            throw new ArgumentOutOfRangeException(nameof(accessMode));
        BridgeApiLanValidationFailure portFailure =
            BridgeApiLanAddressValidator.ValidatePort(port);
        if (portFailure != BridgeApiLanValidationFailure.None)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (shutdownTimeout <= TimeSpan.Zero ||
            shutdownTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
        ArgumentNullException.ThrowIfNull(listeners);
        if (listeners.Count != (accessMode == BridgeApiAccessMode.LocalOnly ? 1 : 2) ||
            !listeners[0].Address.Equals(IPAddress.Loopback) ||
            listeners[0].Security != BridgeApiListenerSecurity.Http ||
            listeners.Any(listener => listener.Port != port) ||
            listeners.Select(listener => listener.Address).Distinct().Count() !=
                listeners.Count)
            throw new ArgumentException(
                "Resolved listeners do not match the access mode.",
                nameof(listeners));

        AccessMode = accessMode;
        Port = port;
        ShutdownTimeout = shutdownTimeout;
        Listeners = Array.AsReadOnly(listeners.ToArray());
        ListenerAddresses = Array.AsReadOnly(
            listeners.Select(listener => listener.Address).ToArray());
    }

    public BridgeApiAccessMode AccessMode { get; }
    public int Port { get; }
    public IPAddress? LanAddress => Listeners.Count == 2
        ? Listeners[1].Address : null;
    public TimeSpan ShutdownTimeout { get; }
    public IReadOnlyList<BridgeApiResolvedListener> Listeners { get; }
    public IReadOnlyList<IPAddress> ListenerAddresses { get; }

    internal static BridgeApiResolvedHostOptions LocalOnly(
        int port,
        TimeSpan? shutdownTimeout = null) => new(
            BridgeApiAccessMode.LocalOnly, port,
            [BridgeApiResolvedListener.Http(IPAddress.Loopback, port)],
            shutdownTimeout ?? TimeSpan.FromSeconds(5));

    // Strictly internal D5B compatibility path for deterministic plaintext
    // listener testing. Production LocalNetwork uses LocalNetworkHttps.
    internal static BridgeApiResolvedHostOptions LocalNetwork(
        int port,
        BridgeApiLanSelection selection,
        TimeSpan? shutdownTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!IPAddress.TryParse(selection.Ipv4Address, out IPAddress? address))
            throw new ArgumentException(
                "The resolved LAN selection must contain an IPv4 address.",
                nameof(selection));
        return new(BridgeApiAccessMode.LocalNetwork, port,
            [BridgeApiResolvedListener.Http(IPAddress.Loopback, port),
                BridgeApiResolvedListener.Http(address, port)],
            shutdownTimeout ?? TimeSpan.FromSeconds(5));
    }

    internal static BridgeApiResolvedHostOptions LocalNetworkHttps(
        int port,
        BridgeApiLanSelection selection,
        BridgeApiTlsIdentitySnapshot identity,
        TimeSpan? shutdownTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(identity);
        if (!IPAddress.TryParse(selection.Ipv4Address, out IPAddress? address))
            throw new ArgumentException(
                "The resolved LAN selection must contain an IPv4 address.",
                nameof(selection));
        return new(BridgeApiAccessMode.LocalNetwork, port,
            [BridgeApiResolvedListener.Http(IPAddress.Loopback, port),
                BridgeApiResolvedListener.Https(address, port,
                    identity.Certificate)],
            shutdownTimeout ?? TimeSpan.FromSeconds(5));
    }

}
