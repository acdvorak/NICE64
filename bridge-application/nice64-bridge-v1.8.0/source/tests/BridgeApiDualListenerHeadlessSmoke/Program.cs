using System.Net;
using PicoSwitchController;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

BridgeApiResolvedHostOptions local =
    BridgeApiResolvedHostOptions.LocalOnly(6470);
Check(local.AccessMode == BridgeApiAccessMode.LocalOnly,
    "LocalOnly resolved mode is immutable");
Check(local.ListenerAddresses.SequenceEqual([IPAddress.Loopback]),
    "LocalOnly resolves 127.0.0.1 only");
Check(local.ListenerAddresses.Count == 1 && local.LanAddress is null,
    "LocalOnly contains no LAN target");
Check(local.Port == 6470, "LocalOnly uses configured port");

BridgeApiLanSelection selection = new(
    "ethernet-id", "192.168.44.20", "Ethernet");
BridgeApiResolvedHostOptions lan =
    BridgeApiResolvedHostOptions.LocalNetwork(6480, selection);
Check(lan.AccessMode == BridgeApiAccessMode.LocalNetwork,
    "LocalNetwork resolved mode is immutable");
Check(lan.ListenerAddresses.SequenceEqual(
    [IPAddress.Loopback, IPAddress.Parse("192.168.44.20")]),
    "LocalNetwork resolves exact loopback plus selected LAN IPv4");
Check(lan.ListenerAddresses.Count == 2,
    "LocalNetwork contains exactly two listeners");
Check(lan.LanAddress!.Equals(IPAddress.Parse("192.168.44.20")),
    "selected LAN IPv4 remains exact");
Check(lan.Port == 6480, "both resolved listeners share configured API port");
Check(lan.ListenerAddresses.All(address =>
        !address.GetAddressBytes().All(value => value == 0)),
    "resolved listeners contain no wildcard address");

bool offRejected = false;
try { BridgeApiResolvedHostOptions.LocalOnly(6465); }
catch (ArgumentOutOfRangeException) { offRejected = true; }
Check(offRejected, "reserved device port cannot become API listener port");

string sourceRoot = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string hostSource = File.ReadAllText(Path.Combine(sourceRoot, "BridgeApiHost.cs"));
string formSource = File.ReadAllText(Path.Combine(sourceRoot, "Form1.cs"));
string lifecycleSource = File.ReadAllText(Path.Combine(
    sourceRoot, "BridgeApiAccessLifecycle.cs"));

Check(hostSource.Contains("foreach (BridgeApiResolvedListener listener in options.GetListeners())",
        StringComparison.Ordinal) &&
    hostSource.Contains("kestrel.Listen(listener.Address, listener.Port);",
        StringComparison.Ordinal),
    "Kestrel composes only immutable explicit listener targets");
Check(!hostSource.Contains("IPAddress.Any", StringComparison.Ordinal),
    "BridgeApiHost never uses IPAddress.Any");
Check(!hostSource.Contains("ListenAnyIP", StringComparison.Ordinal),
    "BridgeApiHost never uses ListenAnyIP");
Check(!hostSource.Contains("0.0.0.0", StringComparison.Ordinal),
    "BridgeApiHost contains no wildcard literal");
Check(!hostSource.Contains("UseUrls", StringComparison.Ordinal),
    "BridgeApiHost does not use hostname or URL expansion");
Check(hostSource.Contains("endpoint.UseHttps(listener.Certificate!)",
        StringComparison.Ordinal),
    "D5C HTTPS is limited to an explicitly resolved secure listener");
Check(!hostSource.Contains("AddCors", StringComparison.Ordinal) &&
    !hostSource.Contains("UseCors", StringComparison.Ordinal),
    "D5B adds no CORS policy");
Check(hostSource.Contains("MaxRequestBodySize = 16 * 1024", StringComparison.Ordinal),
    "request-body limit is preserved");
Check(hostSource.Contains("MaxRequestHeadersTotalSize = 16 * 1024",
        StringComparison.Ordinal) &&
    hostSource.Contains("MaxRequestHeaderCount = 64", StringComparison.Ordinal),
    "header limits are preserved");
Check(hostSource.Contains("RequestHeadersTimeout = TimeSpan.FromSeconds(10)",
        StringComparison.Ordinal), "header timeout is preserved");
Check(hostSource.Contains("KeepAliveTimeout = TimeSpan.FromSeconds(30)",
        StringComparison.Ordinal), "keepalive timeout is preserved");
Check(hostSource.Contains("AddServerHeader = false", StringComparison.Ordinal),
    "server header remains disabled");
Check(hostSource.Contains("AuthenticationHeaderValue.TryParse", StringComparison.Ordinal) &&
    hostSource.Contains("FixedTimeEquals", StringComparison.Ordinal),
    "bearer authentication pipeline remains present");
Check(hostSource.Contains("/api/v1/state", StringComparison.Ordinal) &&
    hostSource.Contains("/api/v1/operation", StringComparison.Ordinal) &&
    hostSource.Contains("/api/v1/history", StringComparison.Ordinal) &&
    hostSource.Contains("/api/v1/sessions", StringComparison.Ordinal),
    "existing state, operation, history, and session routes remain present");
Check(!hostSource.Contains("/api/v1/api-settings", StringComparison.Ordinal) &&
    !hostSource.Contains("/api/v1/listeners", StringComparison.Ordinal) &&
    !hostSource.Contains("/api/v1/certificate", StringComparison.Ordinal) &&
    !hostSource.Contains("/api/v1/firewall", StringComparison.Ordinal),
    "no listener configuration route was added");
Check(formSource.Contains("allowDevelopmentPlaintextLan: false",
        StringComparison.Ordinal),
    "normal desktop composition blocks plaintext LAN exposure");
Check(!formSource.Contains("NetworkChange.NetworkAddressChanged",
        StringComparison.Ordinal) &&
    !lifecycleSource.Contains("NetworkChange.NetworkAddressChanged",
        StringComparison.Ordinal),
    "no address-loss observer was added");
Check(!formSource.Contains("netsh", StringComparison.OrdinalIgnoreCase) &&
    !lifecycleSource.Contains("firewall", StringComparison.OrdinalIgnoreCase),
    "no Windows Firewall behavior was added");

Console.WriteLine($"BridgeApiDualListenerHeadlessSmoke: {passed} checks passed.");
