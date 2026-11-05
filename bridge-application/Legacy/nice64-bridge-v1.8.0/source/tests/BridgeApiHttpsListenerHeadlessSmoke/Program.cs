using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PicoSwitchController;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

BridgeApiAccessConfiguration Lan() => new(
    BridgeApiAccessMode.LocalNetwork, 6470, "ethernet-id",
    "192.168.77.20", "Ethernet");
BridgeApiAccessResolution Resolve(BridgeApiAccessConfiguration configuration) =>
    new(configuration, configuration, BridgeApiAccessConfigurationSource.Persisted);

using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
CertificateRequest request = new("CN=D5C Test", key, HashAlgorithmName.SHA256);
using X509Certificate2 certificate = request.CreateSelfSigned(
    DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
BridgeApiTlsIdentitySnapshot identity = new(certificate, "test-pin", "test-fingerprint");
BridgeApiLanAddressValidator validator = new(new StaticCatalog([
    new("ethernet-id", "Ethernet", OperationalStatus.Up,
        NetworkInterfaceType.Ethernet,
        [IPAddress.Parse("192.168.77.20"), IPAddress.Parse("192.168.77.21")]),
    new("wifi-id", "Wi-Fi", OperationalStatus.Up,
        NetworkInterfaceType.Wireless80211,
        [IPAddress.Parse("192.168.88.30")])
]));

BridgeApiResolvedHostOptions localOptions =
    BridgeApiResolvedHostOptions.LocalOnly(6470);
Check(localOptions.Listeners.Count == 1 &&
    localOptions.Listeners[0].Address.Equals(IPAddress.Loopback) &&
    localOptions.Listeners[0].Security == BridgeApiListenerSecurity.Http,
    "LocalOnly resolves HTTP loopback only");

BridgeApiResolvedHostOptions secureOptions =
    BridgeApiResolvedHostOptions.LocalNetworkHttps(6470,
        new("ethernet-id", "192.168.77.20", "Ethernet"), identity);
Check(secureOptions.Listeners.Count == 2,
    "LocalNetwork resolves exactly two listeners");
Check(secureOptions.Listeners[0].Address.Equals(IPAddress.Loopback) &&
    secureOptions.Listeners[0].Security == BridgeApiListenerSecurity.Http,
    "LocalNetwork preserves HTTP loopback");
Check(secureOptions.Listeners[1].Address.Equals(
        IPAddress.Parse("192.168.77.20")) &&
    secureOptions.Listeners[1].Security == BridgeApiListenerSecurity.Https,
    "LocalNetwork uses HTTPS on exact selected LAN IPv4");
Check(ReferenceEquals(secureOptions.Listeners[1].Certificate, certificate) &&
    secureOptions.Listeners[1].Certificate!.HasPrivateKey,
    "LAN HTTPS listener carries TLS identity certificate");
Check(secureOptions.Listeners.All(listener => listener.Port == 6470),
    "configured API port is preserved on both listeners");
Check(!secureOptions.ListenerAddresses.Contains(IPAddress.Parse("192.168.77.21")) &&
    !secureOptions.ListenerAddresses.Contains(IPAddress.Parse("192.168.88.30")),
    "no extra interface address is resolved");
Check(secureOptions.ListenerAddresses.All(address =>
        !address.GetAddressBytes().All(value => value == 0)),
    "secure listener set contains no wildcard");

FakeHostFactory healthyFactory = new();
await using (BridgeApiAccessLifecycle lifecycle = new(validator,
    healthyFactory, new FakeTlsProvider(identity)))
{
    string sessionId = "session-stable";
    string transport = "SerialUsb";
    string firmware = "Controller";
    string accessory = "ControllerPak";
    string owner = "Bridge";
    string carrier = "Tcp";
    BridgeApiAccessConfiguration desired = Lan();
    BridgeApiAccessRuntimeState state =
        await lifecycle.ApplyAsync(Resolve(desired));
    Check(healthyFactory.Options.Single().Listeners[1].Security ==
        BridgeApiListenerSecurity.Https,
        "production lifecycle constructs no HTTP LAN listener");
    Check(state.Desired == desired &&
        state.LoopbackStatus == BridgeApiLoopbackRuntimeStatus.Listening &&
        state.LanStatus == BridgeApiLanRuntimeStatus.Listening,
        "healthy HTTPS lifecycle publishes both listeners");
    Check(state.LoopbackEndpoint == "http://127.0.0.1:6470" &&
        state.LanEndpoint == "https://192.168.77.20:6470",
        "healthy runtime endpoints report mixed HTTP/HTTPS truthfully");
    Check(sessionId == "session-stable" && transport == "SerialUsb",
        "TLS startup leaves SessionId and USB control unchanged");
    Check(firmware == "Controller" && accessory == "ControllerPak" &&
        owner == "Bridge" && carrier == "Tcp",
        "TLS startup leaves semantic services and carrier unchanged");
}

FakeHostFactory tlsFailureFactory = new();
await using (BridgeApiAccessLifecycle lifecycle = new(validator,
    tlsFailureFactory, new FakeTlsProvider(null)))
{
    BridgeApiAccessConfiguration desired = Lan();
    BridgeApiAccessRuntimeState state =
        await lifecycle.ApplyAsync(Resolve(desired));
    Check(tlsFailureFactory.Options.Count == 1 &&
        tlsFailureFactory.Options[0].AccessMode == BridgeApiAccessMode.LocalOnly,
        "TLS identity failure starts loopback-only host");
    Check(state.Desired == desired && state.IsDegraded &&
        state.LoopbackStatus == BridgeApiLoopbackRuntimeStatus.Listening &&
        state.LanStatus == BridgeApiLanRuntimeStatus.UnavailableCertificate &&
        state.LanEndpoint is null,
        "TLS failure preserves desired LocalNetwork and degrades truthfully");
}

FakeHostFactory httpsFailureFactory = new() { FailNextStart = true };
await using (BridgeApiAccessLifecycle lifecycle = new(validator,
    httpsFailureFactory, new FakeTlsProvider(identity)))
{
    BridgeApiAccessConfiguration desired = Lan();
    BridgeApiAccessRuntimeState state =
        await lifecycle.ApplyAsync(Resolve(desired));
    Check(httpsFailureFactory.Options.Count == 2 &&
        httpsFailureFactory.Options[0].Listeners[1].Security ==
            BridgeApiListenerSecurity.Https &&
        httpsFailureFactory.Options[1].AccessMode == BridgeApiAccessMode.LocalOnly,
        "HTTPS failure recovers with a fresh loopback-only host");
    Check(httpsFailureFactory.Hosts[0].DisposeCount == 1,
        "failed HTTPS host is disposed");
    Check(state.Desired == desired && state.IsDegraded &&
        state.LanStatus == BridgeApiLanRuntimeStatus.BindFailed &&
        state.LoopbackEndpoint == "http://127.0.0.1:6470" &&
        state.LanEndpoint is null,
        "HTTPS start failure preserves intent without LAN HTTP fallback");
}

string sourceRoot = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string hostSource = File.ReadAllText(Path.Combine(sourceRoot, "BridgeApiHost.cs"));
string formSource = File.ReadAllText(Path.Combine(sourceRoot, "Form1.cs"));
string lifecycleSource = File.ReadAllText(Path.Combine(
    sourceRoot, "BridgeApiAccessLifecycle.cs"));
string tlsSource = File.ReadAllText(Path.Combine(
    sourceRoot, "BridgeApiTlsIdentity.cs"));
Check(hostSource.Contains("endpoint.UseHttps(listener.Certificate!)",
        StringComparison.Ordinal),
    "Kestrel applies TLS only to resolved HTTPS listener");
Check(!hostSource.Contains("IPAddress.Any", StringComparison.Ordinal) &&
    !hostSource.Contains("ListenAnyIP", StringComparison.Ordinal) &&
    !hostSource.Contains("UseUrls", StringComparison.Ordinal) &&
    !hostSource.Contains("0.0.0.0", StringComparison.Ordinal),
    "HTTPS host contains no wildcard binding path");
Check(hostSource.Contains("MaxRequestBodySize = 16 * 1024", StringComparison.Ordinal) &&
    hostSource.Contains("MaxRequestHeadersTotalSize = 16 * 1024",
        StringComparison.Ordinal) &&
    hostSource.Contains("RequestHeadersTimeout = TimeSpan.FromSeconds(10)",
        StringComparison.Ordinal) &&
    hostSource.Contains("KeepAliveTimeout = TimeSpan.FromSeconds(30)",
        StringComparison.Ordinal) &&
    hostSource.Contains("AddServerHeader = false", StringComparison.Ordinal),
    "Kestrel security limits remain unchanged");
Check(hostSource.Contains("AuthenticationHeaderValue.TryParse", StringComparison.Ordinal) &&
    hostSource.Contains("FixedTimeEquals", StringComparison.Ordinal),
    "bearer authentication remains present with HTTPS");
Check(!hostSource.Contains("AddCors", StringComparison.Ordinal) &&
    !hostSource.Contains("UseCors", StringComparison.Ordinal),
    "CORS remains disabled");
Check(!hostSource.Contains("/api/v1/tls", StringComparison.Ordinal) &&
    !hostSource.Contains("/api/v1/certificate", StringComparison.Ordinal) &&
    !hostSource.Contains("/api/v1/onboarding", StringComparison.Ordinal) &&
    !hostSource.Contains("/api/v1/token", StringComparison.Ordinal) &&
    !hostSource.Contains("/api/v1/pin", StringComparison.Ordinal),
    "no TLS, onboarding, token, or pin API route was added");
Check(formSource.Contains("BridgeApiTlsIdentityProvider tlsIdentity = new(tlsIdentityPath",
        StringComparison.Ordinal) &&
    formSource.Contains("allowDevelopmentPlaintextLan: false",
        StringComparison.Ordinal),
    "desktop production path supplies TLS and disables plaintext override");
Check(!tlsSource.Contains("X509Store", StringComparison.Ordinal) &&
    !tlsSource.Contains("TrustedRoot", StringComparison.Ordinal) &&
    !tlsSource.Contains("StoreName.Root", StringComparison.Ordinal),
    "TLS identity performs no root-store or trust automation");
Check(!formSource.Contains("NetworkChange.NetworkAddressChanged",
        StringComparison.Ordinal) &&
    !lifecycleSource.Contains("NetworkChange.NetworkAddressChanged",
        StringComparison.Ordinal),
    "no address observer was added");
Check(!formSource.Contains("netsh", StringComparison.OrdinalIgnoreCase) &&
    !lifecycleSource.Contains("firewall", StringComparison.OrdinalIgnoreCase),
    "no firewall behavior was added");

Console.WriteLine($"BridgeApiHttpsListenerHeadlessSmoke: {passed} checks passed.");

sealed class StaticCatalog(IReadOnlyList<BridgeApiNetworkInterfaceSnapshot> items) :
    IBridgeApiNetworkInterfaceCatalog
{
    public IReadOnlyList<BridgeApiNetworkInterfaceSnapshot> GetAll() => items;
}

sealed class FakeTlsProvider(BridgeApiTlsIdentitySnapshot? identity) :
    IBridgeApiTlsIdentityProvider
{
    public BridgeApiTlsIdentityResult GetOrCreate() => identity is null
        ? BridgeApiTlsIdentityResult.Unavailable
        : BridgeApiTlsIdentityResult.Available(identity);
    public void Dispose() { }
}

sealed class FakeHostFactory : IBridgeApiHostFactory
{
    public bool FailNextStart { get; set; }
    public List<BridgeApiResolvedHostOptions> Options { get; } = [];
    public List<FakeHost> Hosts { get; } = [];

    public IBridgeApiHost Create(BridgeApiResolvedHostOptions options)
    {
        Options.Add(options);
        FakeHost host = new(FailNextStart);
        FailNextStart = false;
        Hosts.Add(host);
        return host;
    }
}

sealed class FakeHost(bool failStart) : IBridgeApiHost
{
    public bool IsRunning { get; private set; }
    public int DisposeCount { get; private set; }
    public Task<BridgeApiHostStartResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        IsRunning = !failStart;
        return Task.FromResult(failStart
            ? new BridgeApiHostStartResult(BridgeApiHostStartDisposition.Failed,
                false, "host-start-failed", "bounded")
            : new BridgeApiHostStartResult(BridgeApiHostStartDisposition.Started,
                true, null, null));
    }
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = false;
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        IsRunning = false;
        return ValueTask.CompletedTask;
    }
}
