using System.Net;
using System.Net.NetworkInformation;
using PicoSwitchController;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

static BridgeApiNetworkInterfaceSnapshot Interface(
    string id,
    string name,
    OperationalStatus status,
    NetworkInterfaceType type,
    params string[] addresses) => new(id, name, status, type,
        addresses.Select(IPAddress.Parse).ToArray());

List<BridgeApiNetworkInterfaceSnapshot> inventory =
[
    Interface("ethernet-id", "Ethernet", OperationalStatus.Up,
        NetworkInterfaceType.Ethernet, "192.168.1.20", "192.168.1.21",
        "0.0.0.0", "127.0.0.2", "169.254.1.2", "224.0.0.1",
        "255.255.255.255", "240.0.0.1"),
    Interface("wifi-id", "Wi-Fi", OperationalStatus.Up,
        NetworkInterfaceType.Wireless80211, "10.0.0.5"),
    Interface("vpn-id", "Explicit VPN", OperationalStatus.Up,
        NetworkInterfaceType.Ppp, "10.8.0.2"),
    Interface("virtual-id", "Hyper-V Virtual Ethernet", OperationalStatus.Up,
        NetworkInterfaceType.Ethernet, "172.20.0.1"),
    Interface("down-id", "Disconnected", OperationalStatus.Down,
        NetworkInterfaceType.Ethernet, "192.168.2.20"),
    Interface("loopback-id", "Software Loopback", OperationalStatus.Up,
        NetworkInterfaceType.Loopback, "127.0.0.1"),
    Interface("other-id", "Other", OperationalStatus.Up,
        NetworkInterfaceType.Ethernet, "192.168.50.9")
];
MutableCatalog catalog = new(inventory);
BridgeApiLanAddressValidator validator = new(catalog);

BridgeApiAccessConfiguration Config(
    string? id = "ethernet-id",
    string? address = "192.168.1.20",
    int port = 6470,
    BridgeApiAccessMode mode = BridgeApiAccessMode.LocalNetwork,
    string? name = "presentation only") =>
    new(mode, port, id, address, name);

void Expect(
    BridgeApiAccessConfiguration configuration,
    BridgeApiLanValidationFailure expected,
    string name)
{
    BridgeApiLanValidationResult result = validator.Validate(configuration);
    Check(result.Failure == expected &&
        result.Success == (expected == BridgeApiLanValidationFailure.None) &&
        (result.Success == (result.Selection is not null)), name);
}

Expect(Config(), BridgeApiLanValidationFailure.None,
    "valid active Ethernet exact tuple passes");
Expect(Config("wifi-id", "10.0.0.5"), BridgeApiLanValidationFailure.None,
    "valid active Wi-Fi exact tuple passes");
Expect(Config("vpn-id", "10.8.0.2"), BridgeApiLanValidationFailure.None,
    "explicit VPN selection may pass");
Expect(Config("virtual-id", "172.20.0.1"), BridgeApiLanValidationFailure.None,
    "explicit virtual adapter selection may pass");
Expect(Config(mode: BridgeApiAccessMode.Off),
    BridgeApiLanValidationFailure.ModeNotLocalNetwork,
    "Off is not a LAN-validation request");
Expect(Config(mode: BridgeApiAccessMode.LocalOnly),
    BridgeApiLanValidationFailure.ModeNotLocalNetwork,
    "LocalOnly is not a LAN-validation request");
Expect(Config(id: null), BridgeApiLanValidationFailure.MissingInterfaceId,
    "missing interface ID fails");
Expect(Config(id: "missing-id"),
    BridgeApiLanValidationFailure.InterfaceNotFound,
    "unknown interface ID fails");
Expect(Config("down-id", "192.168.2.20"),
    BridgeApiLanValidationFailure.InterfaceNotOperational,
    "down interface fails");
Expect(Config("loopback-id", "127.0.0.1"),
    BridgeApiLanValidationFailure.LoopbackInterface,
    "software loopback interface fails");
Expect(Config(address: null), BridgeApiLanValidationFailure.MissingAddress,
    "missing address fails");
Expect(Config(address: "not-an-ip"), BridgeApiLanValidationFailure.InvalidIpv4,
    "malformed address fails");
Expect(Config(address: "2001:db8::1"), BridgeApiLanValidationFailure.InvalidIpv4,
    "IPv6 address fails");
Expect(Config(address: "0.0.0.0"),
    BridgeApiLanValidationFailure.UnspecifiedAddress,
    "wildcard unspecified address fails");
Expect(Config(address: "127.0.0.1"),
    BridgeApiLanValidationFailure.LoopbackAddress,
    "127.0.0.1 fails as LAN address");
Expect(Config(address: "127.0.0.2"),
    BridgeApiLanValidationFailure.LoopbackAddress,
    "entire 127/8 fails as LAN address");
Expect(Config(address: "169.254.1.2"),
    BridgeApiLanValidationFailure.LinkLocalAddress,
    "IPv4 link-local fails without override");
Expect(Config(address: "224.0.0.1"),
    BridgeApiLanValidationFailure.MulticastAddress,
    "multicast IPv4 fails");
Expect(Config(address: "255.255.255.255"),
    BridgeApiLanValidationFailure.BroadcastAddress,
    "limited broadcast fails");
Expect(Config(address: "240.0.0.1"),
    BridgeApiLanValidationFailure.ReservedAddress,
    "reserved non-unicast range fails");
Expect(Config(address: "192.168.1.99"),
    BridgeApiLanValidationFailure.AddressNotAssignedToInterface,
    "address absent from selected interface fails");
Expect(Config(address: "192.168.50.9"),
    BridgeApiLanValidationFailure.AddressNotAssignedToInterface,
    "address owned by different interface fails");

BridgeApiLanValidationResult multiple = validator.Validate(
    Config(address: "192.168.1.21"));
Check(multiple.Success && multiple.Selection?.InterfaceId == "ethernet-id" &&
    multiple.Selection.Ipv4Address == "192.168.1.21",
    "multiple addresses validate only the exact selected address");

catalog.Interfaces =
[
    Interface("ethernet-id", "Ethernet Renamed", OperationalStatus.Up,
        NetworkInterfaceType.Ethernet, "192.168.1.30"),
    Interface("other-id", "presentation only", OperationalStatus.Up,
        NetworkInterfaceType.Ethernet, "192.168.1.20")
];
Expect(Config(), BridgeApiLanValidationFailure.AddressNotAssignedToInterface,
    "DHCP-new address does not replace stored old address");
Expect(Config(id: "wrong-id", name: "Ethernet Renamed"),
    BridgeApiLanValidationFailure.InterfaceNotFound,
    "display-name match cannot override interface ID");
Check(catalog.ReadCount > 0,
    "validator uses only injected read-only catalog");

catalog.Interfaces =
[
    Interface("duplicate-id", "One", OperationalStatus.Up,
        NetworkInterfaceType.Ethernet, "192.168.4.2"),
    Interface("duplicate-id", "Two", OperationalStatus.Up,
        NetworkInterfaceType.Ethernet, "192.168.4.2")
];
Expect(Config("duplicate-id", "192.168.4.2"),
    BridgeApiLanValidationFailure.InterfaceIdentityAmbiguous,
    "interface ID must resolve to exactly one interface");

catalog.Throw = true;
Expect(Config(), BridgeApiLanValidationFailure.InterfaceCatalogUnavailable,
    "catalog exception becomes bounded failure");
catalog.Throw = false;

Check(BridgeApiLanAddressValidator.ValidatePort(6470) ==
        BridgeApiLanValidationFailure.None,
    "default API port 6470 passes");
Check(BridgeApiLanAddressValidator.ValidatePort(0) ==
        BridgeApiLanValidationFailure.InvalidPort,
    "port zero fails");
Check(BridgeApiLanAddressValidator.ValidatePort(65536) ==
        BridgeApiLanValidationFailure.InvalidPort,
    "port 65536 fails");
Check(BridgeApiLanAddressValidator.ValidatePort(-1) ==
        BridgeApiLanValidationFailure.InvalidPort,
    "negative port fails");
Check(BridgeApiLanAddressValidator.ValidatePort(6480) ==
        BridgeApiLanValidationFailure.None,
    "valid alternate unprivileged port passes");
Check(BridgeApiLanAddressValidator.ValidatePort(6464) ==
        BridgeApiLanValidationFailure.ReservedPort &&
    BridgeApiLanAddressValidator.ValidatePort(6465) ==
        BridgeApiLanValidationFailure.ReservedPort,
    "NICE64 management and controller/RUDP ports are reserved");

Console.WriteLine($"BridgeApiLanAddressValidatorHeadlessSmoke: {passed} checks passed.");

sealed class MutableCatalog(
    IReadOnlyList<BridgeApiNetworkInterfaceSnapshot> interfaces) :
    IBridgeApiNetworkInterfaceCatalog
{
    public IReadOnlyList<BridgeApiNetworkInterfaceSnapshot> Interfaces { get; set; } =
        interfaces;
    public bool Throw { get; set; }
    public int ReadCount { get; private set; }

    public IReadOnlyList<BridgeApiNetworkInterfaceSnapshot> GetAll()
    {
        ReadCount++;
        if (Throw) throw new InvalidOperationException("private adapter failure");
        return Interfaces;
    }
}
