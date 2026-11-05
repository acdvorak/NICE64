using PicoSwitchController;
using System.Text;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string Status(
    string state,
    string ssid = "Lab Network",
    string link = "DOWN",
    string failure = "NONE",
    string ip = "0.0.0.0") =>
    $"WIFI|STATE={state}|MODE=DHCP|SSID_B64={WifiProvisioningProtocol.EncodeUtf8(ssid)}" +
    $"|LINK={link}|IP={ip}|NETMASK=255.255.255.0|GW=192.0.2.1|DNS=1.1.1.1|FAIL={failure}";

static (BridgeWifiProvisioningService Service, FakeTransport Transport) Create()
{
    FakeTransport transport = new();
    return (new BridgeWifiProvisioningService(new WifiProvisioningSession(transport)),
        transport);
}

static async Task TestStatusTruthAsync()
{
    var (service, transport) = Create();
    transport.Reply(Status("CONNECTED", link: "UP", ip: "192.0.2.45"));
    BridgeWifiProvisioningResult connected = await service.GetStatusAsync();
    Require(connected.Success && connected.Snapshot.DeviceStatusKnown &&
        connected.Snapshot.Configured && connected.Snapshot.Connected &&
        connected.Snapshot.State == "CONNECTED" &&
        connected.Snapshot.Ssid == "Lab Network" &&
        connected.Snapshot.IpAddress == "192.0.2.45",
        "Connected device status was not published as authoritative truth");

    transport.Reply(Status("DISCONNECTED"));
    BridgeWifiProvisioningResult disconnected = await service.GetStatusAsync();
    Require(disconnected.Success && disconnected.Snapshot.DeviceStatusKnown &&
        disconnected.Snapshot.Configured && !disconnected.Snapshot.Connected &&
        disconnected.Snapshot.State == "DISCONNECTED",
        "Disconnected device status was not preserved distinctly");

    transport.Reply(Status("FAILED", link: "DOWN", failure: "AUTH_FAILED"));
    BridgeWifiProvisioningResult failed = await service.GetStatusAsync();
    Require(failed.Success && failed.Snapshot.DeviceStatusKnown &&
        failed.Snapshot.State == "FAILED" &&
        failed.Snapshot.DeviceFailure == "AUTH_FAILED",
        "Device-reported failed status was confused with a query failure");
}

static async Task TestCommandSequencesAsync()
{
    var (service, transport) = Create();
    transport.Reply("WIFI_CONFIG|OK|SSID");
    transport.Reply("WIFI_CONFIG|OK|PASSWORD");
    transport.Reply("WIFI_CONFIG|OK|MODE=DHCP");
    transport.Reply("WIFI|CONNECT|ACCEPTED");
    transport.Reply(Status("ASSOCIATING", link: "ASSOCIATING"));
    BridgeWifiProvisioningResult configured =
        await service.ConfigureAndConnectAsync("Café 無線", "special|password!");
    Require(configured.Success && configured.Snapshot.DeviceStatusKnown &&
        transport.Commands.SequenceEqual(new[]
        {
            WifiProvisioningProtocol.BuildSsidCommand("Café 無線"),
            WifiProvisioningProtocol.BuildPasswordCommand("special|password!"),
            WifiProvisioningProtocol.DhcpCommand,
            WifiProvisioningProtocol.ConnectCommand,
            WifiProvisioningProtocol.StatusCommand
        }), "Configure/connect grammar or authoritative refresh sequence changed");
    Require(transport.Commands[0].EndsWith(
        Convert.ToBase64String(Encoding.UTF8.GetBytes("Café 無線")),
        StringComparison.Ordinal), "UTF-8 SSID encoding parity changed");

    transport.Commands.Clear();
    transport.Reply("WIFI|CONNECT|ACCEPTED");
    transport.Reply(Status("ASSOCIATING", link: "ASSOCIATING"));
    Require((await service.ConnectRetainedAsync()).Success &&
        transport.Commands.SequenceEqual(
            [WifiProvisioningProtocol.ConnectCommand,
             WifiProvisioningProtocol.StatusCommand]),
        "Retained-connect sequence changed or stored credentials locally");

    transport.Commands.Clear();
    transport.Reply("WIFI|DISCONNECT|ACCEPTED");
    transport.Reply(Status("DISCONNECTED"));
    BridgeWifiProvisioningResult disconnected = await service.DisconnectAsync();
    Require(disconnected.Success && disconnected.Snapshot.State == "DISCONNECTED" &&
        transport.Commands.SequenceEqual(
            [WifiProvisioningProtocol.DisconnectCommand,
             WifiProvisioningProtocol.StatusCommand]),
        "Disconnect sequence did not refresh authoritative state");

    transport.Commands.Clear();
    transport.Reply("WIFI|CLEAR|ACCEPTED");
    transport.Reply(Status("READY", ssid: string.Empty));
    BridgeWifiProvisioningResult forgotten = await service.ForgetAsync();
    Require(forgotten.Success && forgotten.Snapshot.State == "READY" &&
        !forgotten.Snapshot.Configured && string.IsNullOrEmpty(forgotten.Snapshot.Ssid) &&
        transport.Commands.SequenceEqual(
            [WifiProvisioningProtocol.ClearCommand,
             WifiProvisioningProtocol.StatusCommand]),
        "Forget sequence retained a false configuration association");
}

static async Task TestValidationCancellationBusyAndAvailabilityAsync()
{
    var (service, transport) = Create();
    BridgeWifiProvisioningResult invalid =
        await service.ConfigureAndConnectAsync(string.Empty, "password");
    Require(invalid.Failure == BridgeWifiProvisioningFailure.ValidationFailure &&
        transport.Commands.Count == 0,
        "Credential validation parity changed or sent invalid credentials");

    using CancellationTokenSource cancelled = new();
    cancelled.Cancel();
    BridgeWifiProvisioningResult cancelledResult =
        await service.GetStatusAsync(cancelled.Token);
    Require(cancelledResult.Failure == BridgeWifiProvisioningFailure.Cancelled &&
        transport.Commands.Count == 0,
        "Pre-cancelled operation sent a command");

    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    transport.BlockReply(Status("READY", ssid: string.Empty), release.Task);
    Task<BridgeWifiProvisioningResult> first = service.GetStatusAsync();
    await transport.SendEntered.Task;
    BridgeWifiProvisioningResult busy = await service.GetStatusAsync();
    Require(busy.Failure == BridgeWifiProvisioningFailure.Busy &&
        transport.Commands.Count == 1,
        "Concurrent Wi-Fi operation interleaved instead of returning Busy");
    release.SetResult();
    Require((await first).Success, "Serialized status operation did not complete");

    var (unavailableService, unavailableTransport) = Create();
    unavailableTransport.IsConnected = false;
    BridgeWifiProvisioningResult unavailable =
        await unavailableService.GetStatusAsync();
    Require(unavailable.Failure == BridgeWifiProvisioningFailure.TransportUnavailable &&
        !unavailable.Snapshot.DeviceStatusKnown &&
        unavailableTransport.Commands.Count == 0,
        "Unavailable transport was not distinguished before command submission");
}

static async Task TestTypedFailuresAsync()
{
    foreach ((Exception exception, BridgeWifiProvisioningFailure expected) in
        new (Exception, BridgeWifiProvisioningFailure)[]
    {
        (new TimeoutException(), BridgeWifiProvisioningFailure.Timeout),
        (new IOException("wire failed"), BridgeWifiProvisioningFailure.TransportFailure)
    })
    {
        var (service, transport) = Create();
        transport.Throw(exception);
        BridgeWifiProvisioningResult result = await service.GetStatusAsync();
        Require(result.Failure == expected && !result.Snapshot.DeviceStatusKnown,
            $"{expected} was not typed or cleared stale status");
    }

    var (rejectedService, rejectedTransport) = Create();
    rejectedTransport.Reply("WIFI_CONFIG|ERR|BUSY");
    BridgeWifiProvisioningResult rejected =
        await rejectedService.ConfigureAndConnectAsync("Lab Network", "password1");
    // Configure failures are followed by a status refresh; make that refresh fail
    // to exercise explicit unknown-after-mutation semantics separately below.
    Require(rejected.Failure == BridgeWifiProvisioningFailure.StateUnknownAfterMutation,
        "Unrefreshable configure rejection did not expose unknown state");

    var (malformedService, malformedTransport) = Create();
    malformedTransport.Reply("WIFI|STATE=");
    BridgeWifiProvisioningResult malformed = await malformedService.GetStatusAsync();
    Require(malformed.Failure == BridgeWifiProvisioningFailure.MalformedReply &&
        !malformed.Snapshot.DeviceStatusKnown,
        "Malformed status was not distinguished");
}

static async Task TestPartialMutationCorrectionAsync()
{
    var (service, transport) = Create();
    transport.Reply("WIFI_CONFIG|OK|SSID");
    transport.Reply("WIFI_CONFIG|ERR|BUSY");
    transport.Reply(Status("CONFIGURED", ssid: "New Network"));
    BridgeWifiProvisioningResult partial =
        await service.ConfigureAndConnectAsync("New Network", "password1");
    Require(partial.Failure == BridgeWifiProvisioningFailure.DeviceRejected &&
        partial.Snapshot.DeviceStatusKnown && partial.Snapshot.Configured &&
        partial.Snapshot.Ssid == "New Network" &&
        transport.Commands[^1] == WifiProvisioningProtocol.StatusCommand,
        "Partial configure failure did not refresh and publish confirmed Pico truth");

    var (unknownService, unknownTransport) = Create();
    unknownTransport.Reply("WIFI_CONFIG|OK|SSID");
    unknownTransport.Reply("WIFI_CONFIG|OK|PASSWORD");
    unknownTransport.Throw(new TimeoutException());
    unknownTransport.Throw(new IOException("refresh failed"));
    BridgeWifiProvisioningResult unknown =
        await unknownService.ConfigureAndConnectAsync("New Network", "password1");
    Require(unknown.Failure ==
            BridgeWifiProvisioningFailure.StateUnknownAfterMutation &&
        !unknown.Snapshot.DeviceStatusKnown && unknown.Snapshot.Ssid is null,
        "Failed partial-mutation refresh retained stale device state");

    foreach ((BridgeWifiProvisioningOperation operation, string accepted) in new[]
    {
        (BridgeWifiProvisioningOperation.Disconnect, "WIFI|DISCONNECT|ACCEPTED"),
        (BridgeWifiProvisioningOperation.Forget, "WIFI|CLEAR|ACCEPTED")
    })
    {
        var (edgeService, edgeTransport) = Create();
        edgeTransport.Reply(Status("CONNECTED", link: "UP", ip: "192.0.2.45"));
        Require((await edgeService.GetStatusAsync()).Success,
            "Could not establish prior authoritative status");
        edgeTransport.Reply(accepted);
        edgeTransport.Throw(new TimeoutException());
        BridgeWifiProvisioningResult edge = operation ==
            BridgeWifiProvisioningOperation.Disconnect
            ? await edgeService.DisconnectAsync()
            : await edgeService.ForgetAsync();
        Require(edge.Failure == BridgeWifiProvisioningFailure.StateUnknownAfterMutation &&
            !edge.Snapshot.DeviceStatusKnown && edge.Snapshot.Ssid is null &&
            edge.Snapshot.IpAddress is null,
            $"{operation} refresh failure retained stale connected/configured truth");
    }
}

static async Task TestCredentialSafetyAsync()
{
    const string password = "Never|Log!This#Password";
    string encoded = WifiProvisioningProtocol.EncodeUtf8(password);
    var (service, transport) = Create();
    transport.Reply("WIFI_CONFIG|OK|SSID");
    transport.Reply("WIFI_CONFIG|ERR|BUSY");
    transport.Reply(Status("CONFIGURED"));
    BridgeWifiProvisioningResult result =
        await service.ConfigureAndConnectAsync("Lab Network", password);
    string semanticText = result + "|" + result.Snapshot;
    Require(!semanticText.Contains(password, StringComparison.Ordinal) &&
        !semanticText.Contains(encoded, StringComparison.Ordinal) &&
        typeof(BridgeWifiProvisioningSnapshot).GetProperties().All(property =>
            !property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)),
        "Password entered the semantic result/snapshot surface");
    Require(WifiProvisioningProtocol.SafeCommandName(
            WifiProvisioningProtocol.BuildPasswordCommand(password)) ==
            "WIFI_CONFIG PASSWORD" &&
        WifiProvisioningProtocol.SafeReplyName(WifiProvisioningProtocol.StatusCommand) ==
            "WIFI_STATUS" &&
        !WifiProvisioningProtocol.SafeReplyName(WifiProvisioningProtocol.StatusCommand)
            .Contains("Lab Network", StringComparison.Ordinal),
        "Wi-Fi command/reply logging labels expose credentials or SSID details");
}

await TestStatusTruthAsync();
await TestCommandSequencesAsync();
await TestValidationCancellationBusyAndAvailabilityAsync();
await TestTypedFailuresAsync();
await TestPartialMutationCorrectionAsync();
await TestCredentialSafetyAsync();
Console.WriteLine("PASS Bridge Wi-Fi provisioning service headless semantics");

sealed class FakeTransport : IWifiCommandTransport
{
    private readonly Queue<Func<CancellationToken, Task<string>>> _steps = new();
    public bool IsConnected { get; set; } = true;
    public List<string> Commands { get; } = [];
    public TaskCompletionSource SendEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Reply(string reply) =>
        _steps.Enqueue(_ => Task.FromResult(reply));

    public void Throw(Exception exception) =>
        _steps.Enqueue(_ => Task.FromException<string>(exception));

    public void BlockReply(string reply, Task blocker) =>
        _steps.Enqueue(async token =>
        {
            SendEntered.TrySetResult();
            await blocker.WaitAsync(token);
            return reply;
        });

    public Task<string> SendAsync(string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Commands.Add(command);
        if (_steps.Count == 0)
            throw new InvalidOperationException("No fake Wi-Fi reply queued");
        return _steps.Dequeue()(cancellationToken);
    }
}
