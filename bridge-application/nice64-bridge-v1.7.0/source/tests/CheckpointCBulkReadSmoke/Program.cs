using System.Buffers.Binary;
using System.Text;
using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string Exchange(NetworkPakBridgeService service, byte sequence, string command, byte[] payload)
{
    string request = $"NETPAK_REQ_HEX|{sequence}|{command}|{Convert.ToHexString(payload)}";
    Require(service.TryHandleRequest(request, out string response), $"{command} not handled");
    return response;
}

static byte[] HexPayload(string response)
{
    string[] fields = response.Split('|');
    Require(fields.Length == 5, $"bad response: {response}");
    return Convert.FromHexString(fields[4]);
}

static uint Crc32(ReadOnlySpan<byte> data)
{
    uint crc = 0xFFFFFFFFu;
    foreach (byte value in data)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
    }
    return ~crc;
}

BridgeDeviceStateService state = new();
IBridgeDeviceStateUpdater stateUpdater = state;
stateUpdater.Connect(BridgeControlConnectionKind.WifiTcp, "NICE64", "1.1.0",
    new(FirmwarePersonality.Network, "STANDALONE", "NETWORK"));
stateUpdater.SelectNetworkPakTransport(BridgeNetworkPakTransport.Tcp);
stateUpdater.ActivateNetworkPakTransport(BridgeNetworkPakTransport.Tcp);
Require(!state.Supports(BridgeCapabilityId.BulkRead),
    "bulk became effective before application negotiation");
NetworkPakBridgeService service = new("speed-test", deviceState: state);
string legacyCapsAttempt = Exchange(service, 0, "GET_STREAM_INFO", "BCAP"u8.ToArray());
Require(legacyCapsAttempt.Contains("|ERR|FF|"),
    "old firmware/client shape incorrectly advertised bulk");
string caps = Exchange(service, 1, "GET_APPLICATION_CAPABILITIES", "BCAP"u8.ToArray());
byte[] capPayload = HexPayload(caps);
Require(capPayload.SequenceEqual(new byte[] { (byte)'B', (byte)'C', 1, 1, 2, 0, 16, 224 }),
    "BULK_READ_V1 capability payload mismatch");
Require(state.Supports(BridgeCapabilityId.BulkRead),
    "negotiated BULK_READ_V1 did not become effective in authoritative state");

string open = Exchange(service, 2, "OPEN_STREAM", Encoding.ASCII.GetBytes("speed://4k"));
byte[] metadata = HexPayload(open);
byte streamId = metadata[0];
uint expectedWholeCrc = BinaryPrimitives.ReadUInt32BigEndian(metadata.AsSpan(6, 4));

byte[] legacyRequest = new byte[5];
legacyRequest[0] = streamId;
string legacy = Exchange(service, 3, "READ_STREAM_CHUNK", legacyRequest);
byte[] legacyPayload = HexPayload(legacy);
Require(legacyPayload.Length == 23 && legacyPayload[5] == 16, "legacy READ16 shape changed");

ushort generation = 0x1234;
byte[] bulk = new byte[10];
bulk[0] = 0xB0;
bulk[1] = streamId;
BinaryPrimitives.WriteUInt16BigEndian(bulk.AsSpan(2, 2), generation);
BinaryPrimitives.WriteUInt32BigEndian(bulk.AsSpan(4, 4), 0);
BinaryPrimitives.WriteUInt16BigEndian(bulk.AsSpan(8, 2), 512);

List<byte> reconstructed = [];
ushort segmentOffset = 0;
uint expectedBlockCrc = 0;
for (int segmentIndex = 0; segmentIndex < 3; segmentIndex++)
{
    byte[] request = segmentIndex == 0 ? bulk : new byte[12];
    string command = segmentIndex == 0 ? "BULK_READ" : "BULK_READ_SEGMENT";
    if (segmentIndex != 0)
    {
        bulk.CopyTo(request, 0);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10, 2), segmentOffset);
    }
    string frame = Exchange(service, (byte)(4 + segmentIndex), command, request);
    string[] fields = frame.Split('|');
    Require(fields.Length == 11 && fields[0] == "NETPAK_BULK_SEG", "bad bulk frame");
    Require(Convert.ToUInt16(fields[2], 16) == generation, "generation mismatch");
    Require(Convert.ToByte(fields[3], 16) == streamId, "stream mismatch");
    Require(Convert.ToUInt16(fields[5], 16) == 512, "logical length mismatch");
    Require(Convert.ToUInt16(fields[6], 16) == segmentOffset, "segment offset mismatch");
    uint frameCrc = Convert.ToUInt32(fields[8], 16);
    if (segmentIndex == 0) expectedBlockCrc = frameCrc;
    Require(frameCrc == expectedBlockCrc, "inconsistent block CRC");
    byte[] bytes = Convert.FromHexString(fields[10]);
    Require(bytes.Length == Convert.ToByte(fields[7], 16), "segment length mismatch");
    reconstructed.AddRange(bytes);
    segmentOffset += (ushort)bytes.Length;
}
Require(reconstructed.Count == 512, "512-byte bulk reconstruction failed");
Require(Crc32(reconstructed.ToArray()) == expectedBlockCrc, "bulk CRC mismatch");
Require(Crc32(reconstructed.ToArray()) != 0 && expectedWholeCrc != 0,
    "deterministic CRC missing");

NetworkPakBridgeSnapshot snapshot = service.GetSnapshot();
Require(snapshot.BulkFetchCount == 1, "bulk fetch metric mismatch");
Require(snapshot.BulkSegmentCount == 3, "bulk segment metric mismatch");
Require(snapshot.BulkBytesDelivered == 512, "bulk byte metric mismatch");

byte[] tooLarge = (byte[])bulk.Clone();
BinaryPrimitives.WriteUInt16BigEndian(tooLarge.AsSpan(8, 2), 513);
Require(Exchange(service, 20, "BULK_READ", tooLarge).Contains("|ERR|FF|"),
    "513-byte request was not rejected");
byte[] wrongStream = (byte[])bulk.Clone();
wrongStream[1] = 0xFE;
Require(Exchange(service, 21, "BULK_READ", wrongStream).Contains("|ERR|FF|"),
    "wrong stream was not rejected");

static bool SelectBulkOrFallback(string? response)
{
    if (string.IsNullOrEmpty(response)) return false;
    try
    {
        byte[] payload = HexPayload(response);
        return payload.Length == 8 && payload[0] == 'B' && payload[1] == 'C' &&
               payload[2] == 1 && (payload[3] & 1) != 0;
    }
    catch { return false; }
}
Require(SelectBulkOrFallback(caps), "new client did not select advertised bulk");
Require(!SelectBulkOrFallback(null), "missing capability did not fall back");
Require(!SelectBulkOrFallback("NETPAK_RSP_HEX|1|OK|A1|00"), "malformed capability did not fall back");
Require(!SelectBulkOrFallback("NETPAK_RSP_HEX|1|ERR|FF|00"), "unsupported capability did not fall back");

Console.WriteLine("CheckpointCBulkReadSmoke PASS");
