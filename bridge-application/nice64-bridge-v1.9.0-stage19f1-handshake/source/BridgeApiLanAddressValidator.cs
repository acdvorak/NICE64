namespace PicoSwitchController;

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

public sealed record BridgeApiLanSelection(
    string InterfaceId,
    string Ipv4Address,
    string? InterfaceName);

public enum BridgeApiLanValidationFailure
{
    None,
    ModeNotLocalNetwork,
    MissingInterfaceId,
    MissingAddress,
    InterfaceNotFound,
    InterfaceIdentityAmbiguous,
    InterfaceNotOperational,
    LoopbackInterface,
    InvalidIpv4,
    UnspecifiedAddress,
    LoopbackAddress,
    LinkLocalAddress,
    MulticastAddress,
    BroadcastAddress,
    ReservedAddress,
    AddressNotAssignedToInterface,
    InvalidPort,
    ReservedPort,
    InterfaceCatalogUnavailable
}

public sealed record BridgeApiLanValidationResult(
    bool Success,
    BridgeApiLanValidationFailure Failure,
    BridgeApiLanSelection? Selection)
{
    public static BridgeApiLanValidationResult Valid(
        BridgeApiLanSelection selection) => new(true,
            BridgeApiLanValidationFailure.None, selection);

    public static BridgeApiLanValidationResult Invalid(
        BridgeApiLanValidationFailure failure) => new(false, failure, null);
}

public sealed record BridgeApiNetworkInterfaceSnapshot(
    string Id,
    string Name,
    OperationalStatus OperationalStatus,
    NetworkInterfaceType InterfaceType,
    IReadOnlyList<IPAddress> UnicastAddresses);

public interface IBridgeApiNetworkInterfaceCatalog
{
    IReadOnlyList<BridgeApiNetworkInterfaceSnapshot> GetAll();
}

public sealed class BridgeApiNetworkInterfaceCatalog :
    IBridgeApiNetworkInterfaceCatalog
{
    public IReadOnlyList<BridgeApiNetworkInterfaceSnapshot> GetAll() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Select(networkInterface => new BridgeApiNetworkInterfaceSnapshot(
                networkInterface.Id,
                networkInterface.Name,
                networkInterface.OperationalStatus,
                networkInterface.NetworkInterfaceType,
                networkInterface.GetIPProperties().UnicastAddresses
                    .Select(address => address.Address)
                    .Where(address =>
                        address.AddressFamily == AddressFamily.InterNetwork)
                    .ToArray()))
            .ToArray();
}

public interface IBridgeApiLanAddressValidator
{
    BridgeApiLanValidationResult Validate(
        BridgeApiAccessConfiguration configuration);
}

public sealed class BridgeApiLanAddressValidator(
    IBridgeApiNetworkInterfaceCatalog catalog) : IBridgeApiLanAddressValidator
{
    public const int Nice64ManagementPort = 6464;
    public const int Nice64ControllerAndRudpPort = 6465;

    private readonly IBridgeApiNetworkInterfaceCatalog _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));

    public BridgeApiLanValidationResult Validate(
        BridgeApiAccessConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.AccessMode != BridgeApiAccessMode.LocalNetwork)
            return Invalid(BridgeApiLanValidationFailure.ModeNotLocalNetwork);
        if (string.IsNullOrWhiteSpace(configuration.SelectedInterfaceId))
            return Invalid(BridgeApiLanValidationFailure.MissingInterfaceId);
        if (string.IsNullOrWhiteSpace(configuration.SelectedIpv4Address))
            return Invalid(BridgeApiLanValidationFailure.MissingAddress);

        IReadOnlyList<BridgeApiNetworkInterfaceSnapshot> interfaces;
        try
        {
            interfaces = _catalog.GetAll();
        }
        catch
        {
            return Invalid(BridgeApiLanValidationFailure.InterfaceCatalogUnavailable);
        }

        BridgeApiNetworkInterfaceSnapshot[] matches = interfaces
            .Where(candidate => string.Equals(candidate.Id,
                configuration.SelectedInterfaceId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
            return Invalid(BridgeApiLanValidationFailure.InterfaceNotFound);
        if (matches.Length != 1)
            return Invalid(BridgeApiLanValidationFailure.InterfaceIdentityAmbiguous);

        BridgeApiNetworkInterfaceSnapshot selected = matches[0];
        if (selected.OperationalStatus != OperationalStatus.Up)
            return Invalid(BridgeApiLanValidationFailure.InterfaceNotOperational);
        if (selected.InterfaceType == NetworkInterfaceType.Loopback)
            return Invalid(BridgeApiLanValidationFailure.LoopbackInterface);

        string addressText = configuration.SelectedIpv4Address.Trim();
        if (!IPAddress.TryParse(addressText, out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
            return Invalid(BridgeApiLanValidationFailure.InvalidIpv4);

        byte[] bytes = address.GetAddressBytes();
        if (bytes[0] == 0)
            return Invalid(BridgeApiLanValidationFailure.UnspecifiedAddress);
        if (bytes[0] == 127)
            return Invalid(BridgeApiLanValidationFailure.LoopbackAddress);
        if (bytes[0] == 169 && bytes[1] == 254)
            return Invalid(BridgeApiLanValidationFailure.LinkLocalAddress);
        if (bytes[0] is >= 224 and <= 239)
            return Invalid(BridgeApiLanValidationFailure.MulticastAddress);
        if (bytes.All(value => value == byte.MaxValue))
            return Invalid(BridgeApiLanValidationFailure.BroadcastAddress);
        if (bytes[0] >= 240)
            return Invalid(BridgeApiLanValidationFailure.ReservedAddress);
        if (!selected.UnicastAddresses.Any(candidate => candidate.Equals(address)))
            return Invalid(
                BridgeApiLanValidationFailure.AddressNotAssignedToInterface);

        BridgeApiLanValidationFailure portFailure = ValidatePort(configuration.Port);
        if (portFailure != BridgeApiLanValidationFailure.None)
            return Invalid(portFailure);

        return BridgeApiLanValidationResult.Valid(new(
            selected.Id, address.ToString(), selected.Name));
    }

    public static BridgeApiLanValidationFailure ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
            return BridgeApiLanValidationFailure.InvalidPort;
        if (port is Nice64ManagementPort or Nice64ControllerAndRudpPort)
            return BridgeApiLanValidationFailure.ReservedPort;
        return BridgeApiLanValidationFailure.None;
    }

    private static BridgeApiLanValidationResult Invalid(
        BridgeApiLanValidationFailure failure) =>
        BridgeApiLanValidationResult.Invalid(failure);
}
