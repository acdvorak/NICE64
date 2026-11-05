using PicoSwitchController;

int passed = 0;

async Task CheckAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
        Environment.ExitCode = 1;
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static BridgeSessionSnapshot Session(
    string id = "session-a",
    BridgeSessionState state = BridgeSessionState.Connected,
    BridgeControlConnectionKind kind = BridgeControlConnectionKind.WifiTcp,
    FirmwarePersonality firmware = FirmwarePersonality.Controller,
    BridgeAccessoryMode accessory = BridgeAccessoryMode.None,
    string endpoint = "NICE64 192.168.2.81:6464") =>
    new(id, state, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        kind, endpoint, "NICE64", firmware, accessory);

static BridgeDeviceStateSnapshot Device(
    BridgeControlConnectionKind kind = BridgeControlConnectionKind.WifiTcp,
    BridgeConnectionState connection = BridgeConnectionState.Connected,
    FirmwarePersonality firmware = FirmwarePersonality.Controller,
    BridgeAccessoryMode accessory = BridgeAccessoryMode.None) =>
    new("NICE64", "1.2.0", BridgeDeviceRole.Controller, BridgeDeviceService.None,
        firmware, kind, connection, BridgeNetworkPakTransport.None,
        BridgeNetworkPakTransport.None,
        BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.DeviceReported),
        BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.BridgeImplemented),
        BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.ApplicationNegotiated),
        BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.Effective),
        0, DateTimeOffset.UnixEpoch)
    { ActiveAccessory = accessory };

static TestRig Rig(
    BridgeSessionSnapshot? session = null,
    BridgeDeviceStateSnapshot? device = null)
{
    FakeSessionProvider sessions = new() { CurrentValue = session ?? Session() };
    FakeDeviceProvider devices = new() { CurrentValue = device ?? Device() };
    FakeTcpAuthority tcp = new();
    FakeUsbAuthority usb = new();
    FakeProtectionProvider protection = new();
    BridgeControllerEndpointProvider provider = new(
        sessions, devices, tcp, usb, protection);
    return new(provider, sessions, devices, tcp, usb, protection);
}

await CheckAsync("no session", async () =>
{
    TestRig rig = Rig();
    rig.Sessions.CurrentValue = null;
    var result = await rig.Provider.ResolveCurrentAsync();
    Require(!result.Available && result.UnavailableReason == "no-session",
        "No-session reason was not returned.");
});

await CheckAsync("temporarily unavailable session", async () =>
{
    TestRig rig = Rig(Session(state: BridgeSessionState.TemporarilyUnavailable));
    var result = await rig.Provider.ResolveCurrentAsync();
    Require(!result.Available && result.UnavailableReason == "session-not-connected",
        "Temporary gap must invalidate the endpoint.");
});

foreach (FirmwarePersonality personality in new[]
         { FirmwarePersonality.Diagnostics, FirmwarePersonality.Network })
{
    await CheckAsync($"{personality} firmware rejected", async () =>
    {
        TestRig rig = Rig(Session(firmware: personality), Device(firmware: personality));
        var result = await rig.Provider.ResolveCurrentAsync();
        Require(!result.Available &&
            result.UnavailableReason == "controller-firmware-unavailable",
            $"{personality} must not expose controller UDP.");
    });
}

await CheckAsync("verified Wi-Fi TCP IPv4", async () =>
{
    TestRig rig = Rig();
    var result = await rig.Provider.ResolveCurrentAsync();
    Require(result.Available && result.Address == "192.168.2.81",
        "Verified live TCP IPv4 was not returned.");
    Require(rig.Tcp.LastSession?.DisplayEndpoint == "NICE64 192.168.2.81:6464",
        "TCP authority did not receive the current management session.");
});

await CheckAsync("stale TCP configuration rejected", async () =>
{
    TestRig rig = Rig();
    rig.Tcp.Address = null;
    var result = await rig.Provider.ResolveCurrentAsync();
    Require(!result.Available && result.Address is null &&
        result.UnavailableReason == "pico-wifi-endpoint-unverified",
        "A non-live TCP address was exposed.");
});

await CheckAsync("USB fresh connected status", async () =>
{
    TestRig rig = Rig(Session(kind: BridgeControlConnectionKind.SerialUsb,
        endpoint: "NICE64 COM7"), Device(kind: BridgeControlConnectionKind.SerialUsb));
    rig.Usb.Result = BridgeControllerUsbWifiStatusResult.ConfirmedStatus(
        true, "192.168.2.82");
    var result = await rig.Provider.ResolveCurrentAsync();
    Require(result.Available && result.Address == "192.168.2.82" &&
        rig.Usb.Calls == 1, "Fresh USB WIFI_STATUS authority was not used.");
});

await CheckAsync("USB cached address ignored", async () =>
{
    TestRig rig = Rig(Session(kind: BridgeControlConnectionKind.SerialUsb,
        endpoint: "NICE64 COM7"), Device(kind: BridgeControlConnectionKind.SerialUsb));
    string cachedAddress = "192.168.2.99";
    rig.Usb.Result = BridgeControllerUsbWifiStatusResult.Unavailable;
    var result = await rig.Provider.ResolveCurrentAsync();
    Require(!result.Available && result.Address != cachedAddress,
        "Cached USB address became authority.");
});

await CheckAsync("USB disconnected status", async () =>
{
    TestRig rig = Rig(Session(kind: BridgeControlConnectionKind.SerialUsb,
        endpoint: "NICE64 COM7"), Device(kind: BridgeControlConnectionKind.SerialUsb));
    rig.Usb.Result = BridgeControllerUsbWifiStatusResult.ConfirmedStatus(
        false, "192.168.2.82");
    Require(!(await rig.Provider.ResolveCurrentAsync()).Available,
        "Disconnected Wi-Fi status was accepted.");
});

foreach (string malformed in new[] { "Unknown", "999.1.1.1", "::1" })
{
    await CheckAsync($"USB invalid IPv4 {malformed}", async () =>
    {
        TestRig rig = Rig(Session(kind: BridgeControlConnectionKind.SerialUsb,
            endpoint: "NICE64 COM7"), Device(kind: BridgeControlConnectionKind.SerialUsb));
        rig.Usb.Result = BridgeControllerUsbWifiStatusResult.ConfirmedStatus(true, malformed);
        Require(!(await rig.Provider.ResolveCurrentAsync()).Available,
            "Invalid/non-IPv4 address was accepted.");
    });
}

await CheckAsync("USB query failure", async () =>
{
    TestRig rig = Rig(Session(kind: BridgeControlConnectionKind.SerialUsb,
        endpoint: "NICE64 COM7"), Device(kind: BridgeControlConnectionKind.SerialUsb));
    rig.Usb.Exception = new IOException("simulated");
    Require(!(await rig.Provider.ResolveCurrentAsync()).Available,
        "USB query exception escaped as availability.");
});

await CheckAsync("USB query timeout", async () =>
{
    TestRig rig = Rig(Session(kind: BridgeControlConnectionKind.SerialUsb,
        endpoint: "NICE64 COM7"), Device(kind: BridgeControlConnectionKind.SerialUsb));
    rig.Usb.Exception = new TimeoutException("simulated");
    Require(!(await rig.Provider.ResolveCurrentAsync()).Available,
        "USB timeout escaped as availability.");
});

await CheckAsync("USB protected control line reports protected operation", async () =>
{
    TestRig rig = Rig(Session(kind: BridgeControlConnectionKind.SerialUsb,
        endpoint: "NICE64 COM7"), Device(kind: BridgeControlConnectionKind.SerialUsb));
    rig.Usb.Result = BridgeControllerUsbWifiStatusResult.Protected;
    var result = await rig.Provider.ResolveCurrentAsync();
    Require(!result.Available && result.UnavailableReason == "protected-operation",
        "Protected USB admission was not preserved semantically.");
});

foreach (BridgeControlConnectionKind kind in new[]
         { BridgeControlConnectionKind.SerialUsb, BridgeControlConnectionKind.WifiTcp })
{
    await CheckAsync($"{kind} resolution does not change control transport", async () =>
    {
        TestRig rig = Rig(Session(kind: kind,
            endpoint: kind == BridgeControlConnectionKind.SerialUsb
                ? "NICE64 COM7" : "NICE64 192.168.2.81:6464"), Device(kind: kind));
        rig.Usb.Result = BridgeControllerUsbWifiStatusResult.ConfirmedStatus(
            true, "192.168.2.82");
        BridgeControlConnectionKind beforeSession = rig.Sessions.Current!.ConnectionKind;
        BridgeControlConnectionKind beforeDevice = rig.Devices.Current.ControlConnectionKind;
        _ = await rig.Provider.ResolveCurrentAsync();
        Require(rig.Sessions.Current!.ConnectionKind == beforeSession &&
            rig.Devices.Current.ControlConnectionKind == beforeDevice,
            "Endpoint resolution mutated the control transport.");
    });
}

await CheckAsync("All guard blocks", async () =>
{
    TestRig rig = Rig();
    rig.Protection.Value = BridgeControllerEndpointProtection.All;
    var result = await rig.Provider.ResolveCurrentAsync();
    Require(!result.Available && result.UnavailableReason == "protected-operation" &&
        rig.Tcp.Calls == 0 && rig.Usb.Calls == 0,
        "All guard did not block before endpoint I/O.");
});

await CheckAsync("LineOnly guard allows UDP endpoint", async () =>
{
    TestRig rig = Rig();
    rig.Protection.Value = BridgeControllerEndpointProtection.LineOnly;
    Require((await rig.Provider.ResolveCurrentAsync()).Available,
        "LineOnly incorrectly blocked direct UDP.");
});

foreach (BridgeAccessoryMode accessory in new[]
         {
             BridgeAccessoryMode.None, BridgeAccessoryMode.ControllerPak,
             BridgeAccessoryMode.TransferPak, BridgeAccessoryMode.RumblePak
         })
{
    await CheckAsync($"{accessory} accessory eligible", async () =>
    {
        TestRig rig = Rig(Session(accessory: accessory), Device(accessory: accessory));
        Require((await rig.Provider.ResolveCurrentAsync()).Available,
            $"{accessory} incorrectly blocked the endpoint.");
    });
}

await CheckAsync("snapshot session ID is authoritative", async () =>
{
    TestRig rig = Rig(Session(id: "authoritative-session"));
    Require((await rig.Provider.ResolveCurrentAsync()).SessionId ==
        "authoritative-session", "Snapshot session ID mismatch.");
});

await CheckAsync("new session replaces old authority", async () =>
{
    TestRig rig = Rig(Session(id: "old-session"));
    var oldResult = await rig.Provider.ResolveCurrentAsync();
    rig.Sessions.CurrentValue = Session(id: "new-session");
    var newResult = await rig.Provider.ResolveCurrentAsync();
    Require(oldResult.SessionId == "old-session" &&
        newResult.SessionId == "new-session", "Old session authority was retained.");
});

await CheckAsync("ended session invalidates endpoint", async () =>
{
    TestRig rig = Rig();
    Require((await rig.Provider.ResolveCurrentAsync()).Available, "Setup failed.");
    rig.Sessions.CurrentValue = null;
    Require(!(await rig.Provider.ResolveCurrentAsync()).Available,
        "Ended session retained endpoint authority.");
});

await CheckAsync("firmware reboot gap invalidates endpoint", async () =>
{
    TestRig rig = Rig();
    rig.Sessions.CurrentValue = Session(state: BridgeSessionState.TemporarilyUnavailable);
    Require(!(await rig.Provider.ResolveCurrentAsync()).Available,
        "Temporary firmware gap retained endpoint authority.");
});

await CheckAsync("restored TCP revalidates", async () =>
{
    TestRig rig = Rig(Session(state: BridgeSessionState.TemporarilyUnavailable));
    Require(!(await rig.Provider.ResolveCurrentAsync()).Available, "Gap setup failed.");
    rig.Sessions.CurrentValue = Session();
    Require((await rig.Provider.ResolveCurrentAsync()).Available && rig.Tcp.Calls == 1,
        "Restored TCP was not revalidated.");
});

await CheckAsync("restored USB requires fresh status", async () =>
{
    TestRig rig = Rig(Session(kind: BridgeControlConnectionKind.SerialUsb,
        endpoint: "NICE64 COM7"), Device(kind: BridgeControlConnectionKind.SerialUsb));
    rig.Usb.Result = BridgeControllerUsbWifiStatusResult.ConfirmedStatus(
        true, "192.168.2.82");
    Require((await rig.Provider.ResolveCurrentAsync()).Available, "Initial USB setup failed.");
    rig.Sessions.CurrentValue = Session(state: BridgeSessionState.TemporarilyUnavailable,
        kind: BridgeControlConnectionKind.SerialUsb, endpoint: "NICE64 COM7");
    Require(!(await rig.Provider.ResolveCurrentAsync()).Available, "Gap was available.");
    rig.Sessions.CurrentValue = Session(kind: BridgeControlConnectionKind.SerialUsb,
        endpoint: "NICE64 COM7");
    Require((await rig.Provider.ResolveCurrentAsync()).Available && rig.Usb.Calls == 2,
        "Restored USB reused old authority.");
});

await CheckAsync("session changes during USB query", async () =>
{
    TestRig rig = Rig(Session(id: "old", kind: BridgeControlConnectionKind.SerialUsb,
        endpoint: "NICE64 COM7"), Device(kind: BridgeControlConnectionKind.SerialUsb));
    rig.Usb.OnRead = () => rig.Sessions.CurrentValue =
        Session(id: "new", kind: BridgeControlConnectionKind.SerialUsb,
            endpoint: "NICE64 COM7");
    rig.Usb.Result = BridgeControllerUsbWifiStatusResult.ConfirmedStatus(
        true, "192.168.2.82");
    var result = await rig.Provider.ResolveCurrentAsync();
    Require(!result.Available && result.SessionId == "new" &&
        result.UnavailableReason == "pico-wifi-endpoint-unverified",
        "Old USB observation crossed a session boundary.");
});

await CheckAsync("available contract constants", async () =>
{
    TestRig rig = Rig();
    var result = await rig.Provider.ResolveCurrentAsync();
    Require(result.Port == 6465 && result.Protocol == "latest-state-v1" &&
        result.ControllerType == "n64", "Endpoint contract constants changed.");
});

await CheckAsync("unavailable snapshot has no endpoint", async () =>
{
    TestRig rig = Rig();
    rig.Tcp.Address = null;
    var result = await rig.Provider.ResolveCurrentAsync();
    Require(result.Address is null && result.Port is null && result.Protocol is null &&
        result.ControllerType is null, "Unavailable snapshot exposed stale endpoint data.");
});

await CheckAsync("snapshot excludes management and ownership data", () =>
{
    string[] names = typeof(BridgeControllerEndpointSnapshot).GetProperties()
        .Select(property => property.Name).ToArray();
    foreach (string forbidden in new[]
             { "ComPort", "TcpManagementPort", "WifiCredentials", "SenderSessionId",
               "LeaseId", "Owner" })
        Require(!names.Contains(forbidden, StringComparer.Ordinal),
            $"Snapshot exposed forbidden field {forbidden}.");
    return Task.CompletedTask;
});

await CheckAsync("provider is observational", async () =>
{
    TestRig rig = Rig();
    BridgeSessionSnapshot beforeSession = rig.Sessions.Current!;
    BridgeDeviceStateSnapshot beforeDevice = rig.Devices.Current;
    _ = await rig.Provider.ResolveCurrentAsync();
    Require(Equals(beforeSession, rig.Sessions.Current) &&
        Equals(beforeDevice, rig.Devices.Current) &&
        rig.Sessions.Mutations == 0 && rig.Devices.Mutations == 0,
        "Provider mutated session/device/firmware/accessory state.");
});

await CheckAsync("provider exposes no ownership or sender operations", () =>
{
    string[] methods = typeof(IBridgeControllerEndpointProvider).GetMethods()
        .Select(method => method.Name).ToArray();
    Require(methods.SequenceEqual(new[] { "ResolveCurrentAsync" }),
        "Provider contract contains non-observational operations.");
    return Task.CompletedTask;
});

Console.WriteLine($"BridgeControllerEndpointProviderHeadlessSmoke: {passed} tests passed.");
if (Environment.ExitCode != 0) return Environment.ExitCode;
return 0;

sealed record TestRig(
    BridgeControllerEndpointProvider Provider,
    FakeSessionProvider Sessions,
    FakeDeviceProvider Devices,
    FakeTcpAuthority Tcp,
    FakeUsbAuthority Usb,
    FakeProtectionProvider Protection);

sealed class FakeSessionProvider : IBridgeSessionProvider
{
    private BridgeSessionSnapshot? _current;
    public BridgeSessionSnapshot? CurrentValue
    {
        get => _current;
        set => _current = value;
    }
    public BridgeSessionSnapshot? Current => _current;
    public int Mutations { get; private set; }
    public event EventHandler<BridgeSessionChangedEventArgs>? CurrentChanged
    {
        add { }
        remove { }
    }
}

sealed class FakeDeviceProvider : IBridgeDeviceStateProvider
{
    private BridgeDeviceStateSnapshot _current = null!;
    public BridgeDeviceStateSnapshot CurrentValue
    {
        get => _current;
        set => _current = value;
    }
    public BridgeDeviceStateSnapshot Current => _current;
    public int Mutations { get; private set; }
    public event EventHandler<BridgeDeviceStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }
}

sealed class FakeTcpAuthority : IBridgeControllerTcpEndpointAuthority
{
    public string? Address { get; set; } = "192.168.2.81";
    public int Calls { get; private set; }
    public BridgeSessionSnapshot? LastSession { get; private set; }
    public string? GetVerifiedIpv4(BridgeSessionSnapshot session)
    {
        Calls++;
        LastSession = session;
        return Address;
    }
}

sealed class FakeUsbAuthority : IBridgeControllerUsbWifiStatusAuthority
{
    public BridgeControllerUsbWifiStatusResult Result { get; set; } =
        BridgeControllerUsbWifiStatusResult.Unavailable;
    public Exception? Exception { get; set; }
    public Action? OnRead { get; set; }
    public int Calls { get; private set; }
    public Task<BridgeControllerUsbWifiStatusResult> ReadFreshAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        OnRead?.Invoke();
        if (Exception is not null) throw Exception;
        return Task.FromResult(Result);
    }
}

sealed class FakeProtectionProvider : IBridgeControllerEndpointProtectionProvider
{
    public BridgeControllerEndpointProtection Value { get; set; }
    public BridgeControllerEndpointProtection Current => Value;
}
