using PicoSwitchController;
using System.Text;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static WifiStatus Parse(string line)
{
    Check(WifiProvisioningProtocol.TryParseStatus(line, out WifiStatus status), "status parse failed");
    return status;
}

static string Status(string state, string link, string failure = "NONE") =>
    $"WIFI|STATE={state}|MODE=DHCP|SSID_B64=TGFiIE5ldHdvcms=|LINK={link}|IP=0.0.0.0|NETMASK=0.0.0.0|GW=0.0.0.0|DNS=0.0.0.0|FAIL={failure}";

void TestEncodingAndCommands()
{
    (string Text, string Encoded)[] values =
    [
        ("Network", "TmV0d29yaw=="),
        ("Lab Network", "TGFiIE5ldHdvcms="),
        ("Lab|Wi-Fi!", "TGFifFdpLUZpIQ=="),
        ("Café 無線", Convert.ToBase64String(Encoding.UTF8.GetBytes("Café 無線"))),
        ("normalpass", "bm9ybWFscGFzcw=="),
        ("p@ss|word!#", "cEBzc3x3b3JkISM=")
    ];
    foreach ((string text, string expected) in values)
    {
        Check(WifiProvisioningProtocol.EncodeUtf8(text) == expected, "UTF-8 Base64 mismatch");
    }

    Check(WifiProvisioningProtocol.BuildSsidCommand("Lab Network") ==
          "WIFI_CONFIG|SSID_B64|TGFiIE5ldHdvcms=", "SSID command mismatch");
    Check(WifiProvisioningProtocol.BuildPasswordCommand("p@ss|word!#") ==
          "WIFI_CONFIG|PASSWORD_B64|cEBzc3x3b3JkISM=", "password command mismatch");
    Check(WifiProvisioningProtocol.DhcpCommand == "WIFI_CONFIG|MODE|DHCP", "DHCP command mismatch");
    Check(WifiProvisioningProtocol.ConnectCommand == "WIFI_CONNECT", "connect command mismatch");
    Check(WifiProvisioningProtocol.DisconnectCommand == "WIFI_DISCONNECT", "disconnect command mismatch");
    Check(WifiProvisioningProtocol.ClearCommand == "WIFI_CLEAR", "clear command mismatch");
    Check(WifiProvisioningProtocol.StatusCommand == "WIFI_STATUS?", "status command mismatch");
    Console.WriteLine("PASS Wi-Fi UTF-8/Base64 and exact command generation");
}

void TestStatusParser()
{
    foreach ((string state, string link) in new[]
    {
        ("READY", "DOWN"), ("ASSOCIATING", "ASSOCIATING"),
        ("WAITING_FOR_ADDRESS", "UP_NO_ADDRESS"), ("CONNECTED", "UP"),
        ("FAILED", "DOWN"), ("DISCONNECTED", "DOWN")
    })
    {
        WifiStatus status = Parse(Status(state, link, state == "FAILED" ? "AUTH_FAILED" : "NONE"));
        Check(status.State == state && status.Link == link, $"{state} parse mismatch");
    }

    WifiStatus connected = Parse("WIFI|STATE=CONNECTED|MODE=DHCP|SSID_B64=V2ktRmkgfCBMYWI=|LINK=UP|IP=192.0.2.45|NETMASK=255.255.255.0|GW=192.0.2.1|DNS=1.1.1.1|FAIL=NONE|FUTURE=value");
    Check(connected.Ssid == "Wi-Fi | Lab", "SSID decode mismatch");
    Check(connected.IpAddress == "192.0.2.45", "IP parse mismatch");
    Check(connected.IsTerminal, "CONNECTED was not terminal");

    WifiStatus missing = Parse("WIFI|STATE=READY|LINK=DOWN|EXTRA=future");
    Check(missing.Mode == "UNKNOWN" && missing.IpAddress == "Unknown", "missing fields not tolerated");

    WifiStatus unknown = Parse("WIFI|STATE=FUTURE_STATE|MODE=DHCP|SSID_B64=***|LINK=FUTURE_LINK|IP=999.1.1.1|FAIL=FUTURE_FAILURE");
    Check(unknown.State == "FUTURE_STATE", "unknown state not preserved");
    Check(unknown.Failure == "FUTURE_FAILURE", "unknown failure not preserved");
    Check(unknown.Ssid == WifiProvisioningProtocol.InvalidEncodedSsid, "invalid SSID placeholder missing");
    Check(unknown.IpAddress == "Invalid (999.1.1.1)", "malformed IP was not safe");
    Console.WriteLine("PASS defensive Wi-Fi status parsing");
}

async Task TestWorkflowAndPollingAsync()
{
    FakeTransport transport = new();
    transport.Enqueue("WIFI_CONFIG|OK|SSID");
    transport.Enqueue("WIFI_CONFIG|OK|PASSWORD");
    transport.Enqueue("WIFI_CONFIG|OK|MODE=DHCP");
    transport.Enqueue("WIFI|CONNECT|ACCEPTED");
    WifiProvisioningSession session = new(transport);
    await session.ConfigureAndConnectAsync("Lab Network", "special|password!", CancellationToken.None);
    Check(transport.Commands.SequenceEqual(new[]
    {
        "WIFI_CONFIG|SSID_B64|TGFiIE5ldHdvcms=",
        "WIFI_CONFIG|PASSWORD_B64|c3BlY2lhbHxwYXNzd29yZCE=",
        "WIFI_CONFIG|MODE|DHCP",
        "WIFI_CONNECT"
    }), "connect sequence mismatch");

    foreach (string stateLine in new[]
    {
        Status("CONFIGURED", "DOWN"),
        Status("ASSOCIATING", "ASSOCIATING"),
        Status("WAITING_FOR_ADDRESS", "UP_NO_ADDRESS"),
        "WIFI|STATE=CONNECTED|MODE=DHCP|SSID_B64=TGFiIE5ldHdvcms=|LINK=UP|IP=198.51.100.50|NETMASK=255.255.255.0|GW=198.51.100.1|DNS=198.51.100.1|FAIL=NONE"
    }) transport.Enqueue(stateLine);

    WifiStatus status;
    do
    {
        status = await session.RefreshAsync(CancellationToken.None);
    } while (WifiProvisioningSession.ShouldContinuePolling(status));
    Check(status.State == "CONNECTED", "polling did not stop at CONNECTED");

    transport.Enqueue(Status("ASSOCIATING", "ASSOCIATING"));
    transport.Enqueue(Status("FAILED", "DOWN", "AUTH_FAILED"));
    status = await session.RefreshAsync(CancellationToken.None);
    Check(WifiProvisioningSession.ShouldContinuePolling(status), "polling stopped during ASSOCIATING");
    status = await session.RefreshAsync(CancellationToken.None);
    Check(!WifiProvisioningSession.ShouldContinuePolling(status), "polling continued after FAILED");
    Console.WriteLine("PASS connect workflow and terminal polling behavior");
}

async Task TestDisconnectClearAndUsbLossAsync()
{
    FakeTransport transport = new();
    WifiProvisioningSession session = new(transport);
    transport.Enqueue("WIFI|DISCONNECT|ACCEPTED");
    transport.Enqueue(Status("DISCONNECTED", "DOWN"));
    WifiStatus status = await session.DisconnectAsync(CancellationToken.None);
    Check(status.State == "DISCONNECTED", "disconnect status mismatch");

    transport.Enqueue("WIFI|CLEAR|ACCEPTED");
    transport.Enqueue("WIFI|STATE=READY|MODE=DHCP|SSID_B64=|LINK=DOWN|IP=0.0.0.0|NETMASK=0.0.0.0|GW=0.0.0.0|DNS=0.0.0.0|FAIL=NONE");
    status = await session.ClearAsync(CancellationToken.None);
    Check(status.State == "READY" && status.Ssid.Length == 0, "clear status mismatch");

    transport.IsConnected = false;
    try
    {
        await session.RefreshAsync(CancellationToken.None);
        throw new InvalidOperationException("USB disconnect did not fail cleanly");
    }
    catch (IOException ex)
    {
        Check(ex.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase), "USB loss message unclear");
    }
    Check(transport.SendCountAfterDisconnect == 0, "USB loss caused a command loop");
    Console.WriteLine("PASS disconnect/clear and bounded USB-loss behavior");
}

void TestSecretSafety()
{
    const string password = "Never|Log!This#Password";
    string command = WifiProvisioningProtocol.BuildPasswordCommand(password);
    string encoded = command[(command.LastIndexOf('|') + 1)..];
    string safeName = WifiProvisioningProtocol.SafeCommandName(command);
    Check(!safeName.Contains(password, StringComparison.Ordinal), "safe command name contains plaintext password");
    Check(!safeName.Contains(encoded, StringComparison.Ordinal), "safe command name contains Base64 password");
    Check(typeof(BridgeSettings).GetProperties().All(property =>
        !property.Name.Contains("Wifi", StringComparison.OrdinalIgnoreCase) &&
        !property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) &&
        !property.Name.Contains("Ssid", StringComparison.OrdinalIgnoreCase)),
        "BridgeSettings exposes persisted Wi-Fi credential state");
    WifiStatus status = Parse(Status("CONNECTED", "UP"));
    string modelText = string.Join('|', status.State, status.Mode, status.Ssid, status.Link,
        status.IpAddress, status.Netmask, status.Gateway, status.Dns, status.Failure);
    Check(!modelText.Contains(password, StringComparison.Ordinal), "status model contains plaintext password");
    Check(!modelText.Contains(encoded, StringComparison.Ordinal), "status model contains Base64 password");
    Console.WriteLine("PASS Wi-Fi secret-safe logging/model/settings surface");
}

TestEncodingAndCommands();
TestStatusParser();
await TestWorkflowAndPollingAsync();
await TestDisconnectClearAndUsbLossAsync();
TestSecretSafety();
Console.WriteLine("PASS WifiProvisioningSmoke");

sealed class FakeTransport : IWifiCommandTransport
{
    private readonly Queue<string> _replies = new();
    public bool IsConnected { get; set; } = true;
    public List<string> Commands { get; } = [];
    public int SendCountAfterDisconnect { get; private set; }

    public void Enqueue(string reply) => _replies.Enqueue(reply);

    public Task<string> SendAsync(string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected)
        {
            SendCountAfterDisconnect++;
            throw new IOException("USB disconnected");
        }
        Commands.Add(command);
        if (_replies.Count == 0) throw new InvalidOperationException("No fake reply queued");
        return Task.FromResult(_replies.Dequeue());
    }
}
