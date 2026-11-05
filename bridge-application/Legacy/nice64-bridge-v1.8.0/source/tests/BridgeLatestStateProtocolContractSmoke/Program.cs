using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static uint HexUInt32(string text) =>
    uint.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

static byte[] HexBytes(string text) => Convert.FromHexString(text);

string vectorPath = Path.Combine(AppContext.BaseDirectory,
    "LATEST_STATE_V1_TEST_VECTORS.json");
using JsonDocument document = JsonDocument.Parse(File.ReadAllText(vectorPath));
JsonElement root = document.RootElement;
Require(root.GetProperty("protocol").GetString() == "latest-state-v1",
    "Protocol identifier changed");
Require(root.GetProperty("packetLength").GetInt32() == 32,
    "Documented packet length changed");

int vectorCount = 0;
foreach (JsonElement vector in root.GetProperty("vectors").EnumerateArray())
{
    string name = vector.GetProperty("name").GetString()!;
    uint session = HexUInt32(vector.GetProperty("sessionId").GetString()!);
    uint sequence = HexUInt32(vector.GetProperty("sequence").GetString()!);
    uint buttons = HexUInt32(vector.GetProperty("buttons").GetString()!);
    sbyte analogX = checked((sbyte)vector.GetProperty("analogX").GetInt32());
    sbyte analogY = checked((sbyte)vector.GetProperty("analogY").GetInt32());
    bool analogActive = vector.GetProperty("analogActive").GetBoolean();
    byte[] expected = HexBytes(vector.GetProperty("continuousHex").GetString()!);
    string spaced = string.Join(' ', expected.Select(value => value.ToString("X2")));
    Require(spaced == vector.GetProperty("spacedHex").GetString(),
        $"{name}: spaced and continuous hex disagree");
    Require(expected.Length == BridgeLatestStateUdpPacket.PacketBytes,
        $"{name}: expected packet is not 32 bytes");

    byte[] actual = new byte[BridgeLatestStateUdpPacket.PacketBytes];
    BridgeLatestStateUdpPacket.Write(actual, session, sequence,
        new BridgeControllerState(ConsoleControllerType.N64, buttons,
            analogX, analogY, analogActive));
    Require(actual.SequenceEqual(expected),
        $"{name}: production encoder mismatch\nexpected={Convert.ToHexString(expected)}\nactual={Convert.ToHexString(actual)}");
    Require(BinaryPrimitives.ReadUInt32BigEndian(actual.AsSpan(0, 4)) ==
            Nice64WirelessTransportContract.Magic &&
            actual[4] == Nice64WirelessTransportContract.TransportVersion &&
            actual[5] == (byte)Nice64RudpPacketFlags.Data &&
            actual[6] == (byte)Nice64WirelessDeliveryClass.LatestState &&
            actual[7] == Nice64WirelessTransportContract.HeaderLength &&
            BinaryPrimitives.ReadUInt32BigEndian(actual.AsSpan(8, 4)) == session &&
            BinaryPrimitives.ReadUInt32BigEndian(actual.AsSpan(12, 4)) == sequence &&
            BinaryPrimitives.ReadUInt32BigEndian(actual.AsSpan(16, 4)) == 0 &&
            BinaryPrimitives.ReadUInt32BigEndian(actual.AsSpan(20, 4)) == 0 &&
            BinaryPrimitives.ReadUInt16BigEndian(actual.AsSpan(24, 2)) == 4 &&
            BinaryPrimitives.ReadUInt16BigEndian(actual.AsSpan(26, 2)) == 0,
        $"{name}: envelope field mismatch");
    Require(Convert.ToHexString(actual.AsSpan(28, 4)) ==
            vector.GetProperty("payloadHex").GetString(),
        $"{name}: payload field mismatch");
    vectorCount++;
}

Require(vectorCount == 14, $"Expected 14 vectors, found {vectorCount}");
Require(BridgeLatestStateUdpControllerSender.UpdatesPerSecond == 120,
    "Sender cadence contract changed");
Require(BridgeLatestStateUdpControllerSender.DefaultPort == 6465,
    "UDP port contract changed");
Require(BridgeLatestStateAcceptancePolicy.RetiredSessionCapacity == 4,
    "Retired-session capacity changed");

BridgeLatestStateAcceptancePolicy sequencePolicy = new();
Require(sequencePolicy.TryAccept(0x10203040, 0xFFFFFFFE), "initial sequence rejected");
Require(sequencePolicy.TryAccept(0x10203040, 0xFFFFFFFF), "pre-wrap sequence rejected");
Require(sequencePolicy.TryAccept(0x10203040, 0x00000000), "wrapped sequence rejected");
Require(!sequencePolicy.TryAccept(0x10203040, 0xFFFFFFFF), "stale sequence accepted");
Require(!sequencePolicy.TryAccept(0x10203040, 0x00000000), "duplicate accepted");

BridgeLatestStateAcceptancePolicy ownershipPolicy = new();
Require(ownershipPolicy.TryAccept(0xAAAAAAAA, 100), "sender A rejected");
Require(ownershipPolicy.TryAccept(0xBBBBBBBB, 1), "sender B takeover rejected");
Require(!ownershipPolicy.TryAccept(0xAAAAAAAA, 101), "retired sender A resurrected");
Require(ownershipPolicy.TryAccept(0xBBBBBBBB, 2), "sender B continuation rejected");

Console.WriteLine($"PASS — {vectorCount}/{vectorCount} canonical vectors match the production codec");
Console.WriteLine("PASS — wrap-safe sequence and retired-session policies match the frozen contract");
