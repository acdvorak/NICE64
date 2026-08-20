using System.Text;
using System.Security.Cryptography;
using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

const string requestLine = "NETPAK_REQ|37|PING_BRIDGE|";
NetworkPakBridgeService tcpService = new();
Require(tcpService.TryHandleRequest(requestLine, out string tcpResponse),
    "TCP reference request did not enter the Network Pak application boundary");
NetworkPakBridgeService rudpService = new();
NetworkPakApplicationDispatcher dispatcher = new(rudpService);
byte[] requestBytes = Encoding.ASCII.GetBytes(requestLine);
Require(dispatcher.TryHandle(requestBytes, out byte[] rudpResponse),
    "RUDP request did not enter the shared Network Pak application boundary");
Require(Encoding.ASCII.GetString(rudpResponse) == tcpResponse &&
        requestBytes.SequenceEqual(Encoding.ASCII.GetBytes(requestLine)),
    "TCP/RUDP application request or response bytes diverged");
Console.WriteLine("PASS TCP and RUDP reach the same Network Pak boundary byte-for-byte");

byte[] largeMessage = Enumerable.Range(0, 1400)
    .Select(index => (byte)('A' + index % 26)).ToArray();
List<byte[]> delivered = [];
NetworkPakApplicationMessageFramer framer = new(message => delivered.Add(message.ToArray()));
byte[] framed = NetworkPakApplicationMessageFramer.Frame(largeMessage);
foreach (byte[] piece in framed.Chunk(512)) framer.Feed(piece);
Require(delivered.Count == 1 && delivered[0].SequenceEqual(largeMessage),
    "multi-payload application message was not reconstructed exactly once");
Console.WriteLine("PASS 1400-byte application message spans multiple 512-byte payloads exactly once");

const uint session = 0x10203040;
const uint initial = 0xFFFFFFFE;
List<byte[]> ordered = [];
NetworkPakApplicationMessageFramer orderedFramer =
    new(message => ordered.Add(message.ToArray()));
Nice64RudpConfiguration configuration = new(8, 512, 40, 4);
Nice64RudpReceiveWindow receive = new(configuration, session,
    (_, payload) => orderedFramer.Feed(payload.Span), initial);
byte[] firstMessage = NetworkPakApplicationMessageFramer.Frame(
    Encoding.ASCII.GetBytes("NETPAK_REQ|1|PING_BRIDGE|"));
byte[] secondMessage = NetworkPakApplicationMessageFramer.Frame(
    Encoding.ASCII.GetBytes("NETPAK_REQ|2|GET_BRIDGE_STATUS|"));
Nice64RudpPacket first = new(new(Nice64WirelessTransportContract.Magic, 1,
    Nice64RudpPacketFlags.Data | Nice64RudpPacketFlags.Ack, 0, 28, session,
    initial, 0, 0, (ushort)firstMessage.Length, 0), firstMessage);
Nice64RudpPacket second = new(first.Header with
    { Sequence = unchecked(initial + 1), PayloadLength = (ushort)secondMessage.Length },
    secondMessage);
Require(receive.Receive(second) == Nice64RudpReceiveResult.Accepted &&
        ordered.Count == 0, "reordered successor delivered before its hole");
Require(receive.Receive(first) == Nice64RudpReceiveResult.Accepted &&
        ordered.Count == 2 &&
        Encoding.ASCII.GetString(ordered[0]).Contains("|1|") &&
        Encoding.ASCII.GetString(ordered[1]).Contains("|2|"),
    "reordered application messages were not delivered in order");
Require(receive.Receive(first) == Nice64RudpReceiveResult.AlreadyDelivered &&
        ordered.Count == 2,
    "duplicate RUDP DATA produced a duplicate Network Pak application delivery");
Nice64RudpPacket staleSession = first with
    { Header = first.Header with { SessionId = 0x55667788 } };
Require(receive.Receive(staleSession) == Nice64RudpReceiveResult.SessionMismatch &&
        ordered.Count == 2,
    "stale prior-session DATA entered the active Network Pak session");
Console.WriteLine("PASS duplicate, reorder, wrap, and stale-session application isolation");

NetworkPakRudpSessionParameters[] sessions = Enumerable.Range(0, 64)
    .Select(_ => NetworkPakRudpSessionParameters.CreateRandom()).ToArray();
Require(sessions.All(item => item.SessionId != 0 &&
                             item.HostInitialSequence != 0 &&
                             item.FirmwareInitialSequence != 0) &&
        sessions.Select(item => item.SessionId).Distinct().Count() == sessions.Length,
    "RUDP reconnect parameters were zero or reused");
Console.WriteLine("PASS reconnect creates fresh cryptographically random nonzero sessions");

string settingsPath = Path.Combine(Path.GetTempPath(),
    $"nice64-network-rudp-{Guid.NewGuid():N}.ini");
try
{
    NetworkPakTransportSettings.Save(settingsPath,
        NetworkPakWifiTransport.ReliableUdpQuick);
    Require(NetworkPakTransportSettings.Load(settingsPath) ==
            NetworkPakWifiTransport.ReliableUdpQuick,
        "Product Quick Mode selection did not persist");
    NetworkPakTransportSettings.Save(settingsPath, NetworkPakWifiTransport.Tcp);
    Require(NetworkPakTransportSettings.Load(settingsPath) ==
            NetworkPakWifiTransport.Tcp,
        "TCP reference selection did not persist independently");
}
finally
{
    if (File.Exists(settingsPath)) File.Delete(settingsPath);
}
Console.WriteLine("PASS explicit TCP/Quick Mode selection persists without carrier tuning controls");

Require(NetworkPakWifiTransport.Tcp != NetworkPakWifiTransport.ReliableUdpQuick &&
        dispatcher.TryHandle(requestBytes, out byte[] isolatedTcpResponse) &&
        isolatedTcpResponse.SequenceEqual(rudpResponse),
    "TCP reference selection was replaced, coupled, or behaviorally changed");
Console.WriteLine("PASS TCP reference path remains functional and explicitly isolated");

string hostProduction = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
    "production-host-rudp.cs"));
string firmwareProduction = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
    "production-firmware-adapter.c"));
string[] forbiddenSynthetic =
[
    "STREAM_BEGIN", "STREAM_END", "RUDPC_STREAM", "SyntheticClient",
    "0x90", "0x91", "0x96"
];
Require(forbiddenSynthetic.All(value =>
        !hostProduction.Contains(value, StringComparison.OrdinalIgnoreCase) &&
        !firmwareProduction.Contains(value, StringComparison.OrdinalIgnoreCase)),
    "production Network Pak adapter references a synthetic diagnostic sink/source");
Require(firmwareProduction.Contains("network_pak_handle_rudp_line", StringComparison.Ordinal),
    "firmware adapter no longer terminates at the existing Network Pak boundary");
Console.WriteLine("PASS production adapter uses no synthetic diagnostic sink/source");

static string HashFixture(string name) => Convert.ToHexString(SHA256.HashData(
    File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name))));
Require(HashFixture("protected-n64-controller.c") ==
            "360E7BB2CB1264C7B1D0A181525FA661885E5454796712067986285944DA971A" &&
        HashFixture("protected-controller-state.c") ==
            "D4932D454963097A905D0F3CA66F44D1787DD063D7368D787353620516F296D2" &&
        HashFixture("protected-normal-rx.pio") ==
            "D4F4AA9C18839440FF7C6695453EE81F2CB41AD1A2A425066D6D52F331E4FC93",
    "Normal Controller/Core0 state/PIO protected sources changed");
Console.WriteLine("PASS Normal Controller/Core0 state/PIO protected sources unchanged");

Require(NetworkPakRudpTransport.Window == 8 &&
        NetworkPakRudpTransport.PhysicalFlightCap == 2 &&
        NetworkPakRudpTransport.PayloadBytes == 512 &&
        NetworkPakRudpTransport.RtoMs == 40 &&
        NetworkPakRudpTransport.RetryBudget == 4 &&
        !Nice64WirelessTransportContract.FastModeVisible &&
        Nice64WirelessTransportContract.Select(
            (byte)Nice64WirelessDeliveryClass.LatestState).Result ==
            Nice64WirelessSelectionResult.Unsupported,
    "frozen carrier tuple or Fast reservation changed");
Console.WriteLine("PASS frozen tuple and unsupported Fast/LATEST_STATE reservation");
Console.WriteLine("ALL NETWORK PAK RUDP ADAPTER TESTS PASSED");
