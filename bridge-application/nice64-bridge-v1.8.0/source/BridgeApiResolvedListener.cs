namespace PicoSwitchController;

using System.Net;
using System.Security.Cryptography.X509Certificates;

public enum BridgeApiListenerSecurity
{
    Http,
    Https
}

public sealed class BridgeApiResolvedListener
{
    private BridgeApiResolvedListener(
        IPAddress address,
        int port,
        BridgeApiListenerSecurity security,
        X509Certificate2? certificate)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily !=
            System.Net.Sockets.AddressFamily.InterNetwork ||
            address.GetAddressBytes().All(value => value == 0))
            throw new ArgumentException(
                "A resolved listener requires an explicit IPv4 address.",
                nameof(address));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (security == BridgeApiListenerSecurity.Https &&
            (certificate is null || !certificate.HasPrivateKey))
            throw new ArgumentException(
                "An HTTPS listener requires a certificate with a private key.",
                nameof(certificate));
        if (security == BridgeApiListenerSecurity.Http && certificate is not null)
            throw new ArgumentException(
                "An HTTP listener cannot carry a TLS certificate.",
                nameof(certificate));

        Address = address;
        Port = port;
        Security = security;
        Certificate = certificate;
    }

    public IPAddress Address { get; }
    public int Port { get; }
    public BridgeApiListenerSecurity Security { get; }
    internal X509Certificate2? Certificate { get; }

    internal static BridgeApiResolvedListener Http(
        IPAddress address, int port) => new(
            address, port, BridgeApiListenerSecurity.Http, null);

    internal static BridgeApiResolvedListener Https(
        IPAddress address, int port, X509Certificate2 certificate) => new(
            address, port, BridgeApiListenerSecurity.Https, certificate);
}
