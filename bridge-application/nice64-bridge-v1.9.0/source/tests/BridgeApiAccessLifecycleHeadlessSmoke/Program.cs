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

BridgeApiAccessConfiguration Off() => new(
    BridgeApiAccessMode.Off, 6470, null, null);
BridgeApiAccessConfiguration Local(int port = 6470) => new(
    BridgeApiAccessMode.LocalOnly, port, null, null);
BridgeApiAccessConfiguration Lan(string address = "192.168.50.10",
    int port = 6470, string interfaceId = "ethernet-id") => new(
        BridgeApiAccessMode.LocalNetwork, port, interfaceId, address, "Ethernet");
BridgeApiAccessResolution Resolve(BridgeApiAccessConfiguration configuration) =>
    new(configuration, configuration, BridgeApiAccessConfigurationSource.Persisted);

StaticCatalog catalog = new([
    new("ethernet-id", "Ethernet", OperationalStatus.Up,
        NetworkInterfaceType.Ethernet,
        [IPAddress.Parse("192.168.50.10"), IPAddress.Parse("192.168.50.11")]),
    new("wifi-id", "Wi-Fi", OperationalStatus.Up,
        NetworkInterfaceType.Wireless80211,
        [IPAddress.Parse("192.168.60.20")])
]);
BridgeApiLanAddressValidator validator = new(catalog);

FakeHostFactory offFactory = new();
await using (BridgeApiAccessLifecycle lifecycle = new(validator, offFactory,
    allowDevelopmentPlaintextLan: true))
{
    BridgeApiAccessRuntimeState state = await lifecycle.ApplyAsync(Resolve(Off()));
    Check(offFactory.Hosts.Count == 0, "Off creates no host");
    Check(state.LoopbackStatus == BridgeApiLoopbackRuntimeStatus.NotRequested &&
        state.LanStatus == BridgeApiLanRuntimeStatus.NotRequested,
        "Off publishes no listeners");
}

FakeHostFactory replacementFactory = new();
await using (BridgeApiAccessLifecycle lifecycle = new(validator,
    replacementFactory, allowDevelopmentPlaintextLan: true))
{
    BridgeApiAccessRuntimeState localState =
        await lifecycle.ApplyAsync(Resolve(Local()));
    FakeHost localHost = replacementFactory.Hosts[0];
    Check(localState.LoopbackStatus == BridgeApiLoopbackRuntimeStatus.Listening &&
        localState.LanStatus == BridgeApiLanRuntimeStatus.NotRequested,
        "LocalOnly starts loopback only");
    Check(replacementFactory.Options[0].ListenerAddresses.SequenceEqual(
        [IPAddress.Loopback]), "LocalOnly resolves one exact loopback target");

    BridgeApiAccessRuntimeState lanState =
        await lifecycle.ApplyAsync(Resolve(Lan()));
    FakeHost lanHost = replacementFactory.Hosts[1];
    Check(replacementFactory.Hosts.Count == 2 && !ReferenceEquals(localHost, lanHost),
        "LocalOnly to LocalNetwork creates a fresh host");
    Check(localHost.StopCount == 1 && localHost.DisposeCount == 1,
        "replaced LocalOnly host is stopped and disposed");
    Check(lanHost.StartCount == 1 && localHost.StartCount == 1,
        "same host instance is never restarted");
    Check(lanState.LoopbackStatus == BridgeApiLoopbackRuntimeStatus.Listening &&
        lanState.LanStatus == BridgeApiLanRuntimeStatus.Listening,
        "healthy LocalNetwork preserves loopback and starts LAN");
    Check(replacementFactory.Options[1].ListenerAddresses.SequenceEqual(
        [IPAddress.Loopback, IPAddress.Parse("192.168.50.10")]),
        "LocalNetwork uses exactly selected IPv4");
    Check(!replacementFactory.Options[1].ListenerAddresses.Contains(
        IPAddress.Parse("192.168.50.11")),
        "second address on selected adapter is excluded");
    Check(!replacementFactory.Options[1].ListenerAddresses.Contains(
        IPAddress.Parse("192.168.60.20")),
        "another adapter address is excluded");

    BridgeApiAccessRuntimeState backToLocal =
        await lifecycle.ApplyAsync(Resolve(Local()));
    Check(replacementFactory.Hosts.Count == 3 && lanHost.DisposeCount == 1 &&
        backToLocal.LanStatus == BridgeApiLanRuntimeStatus.NotRequested,
        "LocalNetwork to LocalOnly replaces only the host");

    FakeHost port6470 = replacementFactory.Hosts[2];
    await lifecycle.ApplyAsync(Resolve(Local(6480)));
    Check(replacementFactory.Options[^1].Port == 6480 &&
        port6470.DisposeCount == 1, "port change replaces the host");

    FakeHost port6480 = replacementFactory.Hosts[^1];
    await lifecycle.ApplyAsync(Resolve(Lan("192.168.50.11", 6480)));
    Check(replacementFactory.Options[^1].LanAddress!.Equals(
        IPAddress.Parse("192.168.50.11")) && port6480.DisposeCount == 1,
        "LocalNetwork address change creates an exact fresh host");

    FakeHost finalLan = replacementFactory.Hosts[^1];
    BridgeApiAccessRuntimeState stopped =
        await lifecycle.ApplyAsync(Resolve(Off()));
    Check(finalLan.DisposeCount == 1 && stopped.LoopbackEndpoint is null &&
        stopped.LanEndpoint is null, "Off after LocalNetwork disposes ownership");
}

FakeHostFactory invalidFactory = new();
await using (BridgeApiAccessLifecycle lifecycle = new(validator, invalidFactory,
    allowDevelopmentPlaintextLan: true))
{
    BridgeApiAccessConfiguration requested = Lan(interfaceId: "missing-id");
    BridgeApiAccessRuntimeState state =
        await lifecycle.ApplyAsync(Resolve(requested));
    Check(invalidFactory.Hosts.Count == 1 &&
        invalidFactory.Options[0].AccessMode == BridgeApiAccessMode.LocalOnly,
        "invalid LocalNetwork never constructs a LAN host");
    Check(state.Desired == requested && state.IsDegraded,
        "invalid LocalNetwork preserves desired intent");
    Check(state.LoopbackStatus == BridgeApiLoopbackRuntimeStatus.Listening &&
        state.LanStatus == BridgeApiLanRuntimeStatus.UnavailableSelectedInterface &&
        state.LanEndpoint is null,
        "invalid LocalNetwork degrades truthfully to loopback");
    Check(state.LanFailure == BridgeApiLanValidationFailure.InterfaceNotFound,
        "validation failure remains bounded");
}

FakeHostFactory guardFactory = new();
await using (BridgeApiAccessLifecycle lifecycle = new(validator, guardFactory))
{
    BridgeApiAccessConfiguration requested = Lan();
    BridgeApiAccessRuntimeState state =
        await lifecycle.ApplyAsync(Resolve(requested));
    Check(guardFactory.Hosts.Count == 1 &&
        guardFactory.Options[0].AccessMode == BridgeApiAccessMode.LocalOnly,
        "normal production guard suppresses plaintext LAN");
    Check(state.Desired == requested && state.IsDegraded &&
        state.LanStatus == BridgeApiLanRuntimeStatus.UnavailableCertificate,
        "guard retains LocalNetwork intent pending TLS");
}

FakeHostFactory failureFactory = new();
failureFactory.FailNextStart = true;
await using (BridgeApiAccessLifecycle lifecycle = new(validator, failureFactory,
    allowDevelopmentPlaintextLan: true))
{
    BridgeApiAccessConfiguration requested = Lan();
    BridgeApiAccessRuntimeState state =
        await lifecycle.ApplyAsync(Resolve(requested));
    Check(failureFactory.Hosts.Count == 2,
        "dual-listener failure creates one fresh fallback host");
    Check(failureFactory.Hosts[0].DisposeCount == 1 &&
        failureFactory.Hosts[0].StopCount == 0,
        "failed dual-listener host is disposed completely");
    Check(failureFactory.Options[1].AccessMode == BridgeApiAccessMode.LocalOnly &&
        failureFactory.Hosts[1].StartCount == 1,
        "dual-listener failure recovers with fresh loopback host");
    Check(state.Desired == requested && state.IsDegraded &&
        state.LoopbackStatus == BridgeApiLoopbackRuntimeStatus.Listening &&
        state.LanStatus == BridgeApiLanRuntimeStatus.BindFailed &&
        state.LanEndpoint is null,
        "dual-listener failure publishes degraded authoritative state");
}

FakeHostFactory envFactory = new();
await using (BridgeApiAccessLifecycle lifecycle = new(validator, envFactory,
    allowDevelopmentPlaintextLan: true))
{
    BridgeApiAccessConfiguration persisted = Lan();
    BridgeApiAccessResolution environment = BridgeApiAccessSettingsResolver.Resolve(
        persisted, "true", "6480");
    BridgeApiAccessRuntimeState state = await lifecycle.ApplyAsync(environment);
    Check(envFactory.Options.Single().AccessMode == BridgeApiAccessMode.LocalOnly &&
        envFactory.Options.Single().Port == 6480,
        "legacy environment enable composes LocalOnly only");
    Check(state.Desired == persisted,
        "environment override does not erase persisted intent");
}

string sessionId = "session-stable";
string controlTransport = "WifiTcp";
string firmware = "Controller";
string accessory = "TransferPak";
string owner = "Bridge";
string carrier = "ReliableUdpQuick";
FakeHostFactory safetyFactory = new();
await using (BridgeApiAccessLifecycle lifecycle = new(validator, safetyFactory,
    allowDevelopmentPlaintextLan: true))
{
    await lifecycle.ApplyAsync(Resolve(Local()));
    await lifecycle.ApplyAsync(Resolve(Lan()));
    Check(sessionId == "session-stable", "Bridge SessionId is untouched");
    Check(controlTransport == "WifiTcp", "Wi-Fi TCP control remains Wi-Fi TCP");
    Check(firmware == "Controller" && accessory == "TransferPak",
        "firmware and accessory semantic state are untouched");
    Check(owner == "Bridge" && carrier == "ReliableUdpQuick",
        "controller ownership and Network Pak carrier are untouched");
}

Check(replacementFactory.Options.All(options =>
        options.ListenerAddresses.All(address =>
            !address.GetAddressBytes().All(value => value == 0))),
    "no lifecycle host option contains a wildcard");
Check(replacementFactory.Hosts.All(host => host.StartCount <= 1),
    "all replacement hosts are one-start-only");

Console.WriteLine($"BridgeApiAccessLifecycleHeadlessSmoke: {passed} checks passed.");

sealed class StaticCatalog(IReadOnlyList<BridgeApiNetworkInterfaceSnapshot> items) :
    IBridgeApiNetworkInterfaceCatalog
{
    public IReadOnlyList<BridgeApiNetworkInterfaceSnapshot> GetAll() => items;
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
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int DisposeCount { get; private set; }

    public Task<BridgeApiHostStartResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        IsRunning = !failStart;
        return Task.FromResult(failStart
            ? new BridgeApiHostStartResult(BridgeApiHostStartDisposition.Failed,
                false, "host-start-failed", "bounded")
            : new BridgeApiHostStartResult(BridgeApiHostStartDisposition.Started,
                true, null, null));
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
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
