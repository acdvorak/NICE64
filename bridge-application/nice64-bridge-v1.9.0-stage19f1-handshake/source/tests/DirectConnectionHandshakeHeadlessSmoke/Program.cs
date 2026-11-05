using PicoSwitchController;
using static TestIds;

var tests = new (string Name, Action Run)[]
{
    ("01 one-write CLIENT returns one CLIENTSERVER", OneWriteClient),
    ("02 split CLIENT matches across writes", SplitClient),
    ("03 byte-wise CLIENT matches", ByteWiseClient),
    ("04 duplicate CLIENT before drain queues no duplicate", DuplicateBeforeDrain),
    ("05 CLIENT after completion is not a handshake", ClientAfterComplete),
    ("06 arbitrary preamble then CLIENT is deterministic", PreambleThenClient),
    ("07 partial-prefix mismatch recovers", PartialPrefixRecovery),
    ("08 response is exact 12-byte CLIENTSERVER", ExactResponse),
    ("09 CLIENTSERVER precedes initial LCP", ResponsePrecedesLcp),
    ("10 CLIENT is not fed to PPP decoder", ClientNotFedToPpp),
    ("11 post-handshake PPP input reaches decoder", PostHandshakePpp),
    ("12 close clears pending handshake state", CloseClearsState),
    ("13 second session starts fresh", SecondSessionFresh),
};

int passed = 0;
foreach ((string name, Action run) in tests)
{
    try
    {
        run();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        Console.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

Console.WriteLine($"RESULT {passed}/{tests.Length} PASS");
return passed == tests.Length ? 0 : 1;

static void OneWriteClient()
{
    using PppLiveResource resource = NewResource();
    Write(resource, "CLIENT"u8);
    Bytes("CLIENTSERVER"u8, Data(resource.Read(32)));
    Equal(NetworkPakLiveReadStatus.Data, resource.Read(32).Status);
}

static void SplitClient()
{
    using PppLiveResource resource = NewResource();
    Write(resource, "CLI"u8);
    Equal(NetworkPakLiveReadStatus.WouldBlock, resource.Read(16).Status);
    Write(resource, "ENT"u8);
    Bytes("CLIENTSERVER"u8, Data(resource.Read(16)));
}

static void ByteWiseClient()
{
    using PppLiveResource resource = NewResource();
    foreach (byte value in "CLIENT"u8)
        Write(resource, new[] { value });
    Bytes("CLIENTSERVER"u8, Data(resource.Read(16)));
}

static void DuplicateBeforeDrain()
{
    using PppLiveResource resource = NewResource();
    Write(resource, "CLIENT"u8);
    Write(resource, "CLIENT"u8);
    Bytes("CLIENTSERVER"u8, Data(resource.Read(16)));
    byte[] lcp = Data(resource.Read(64));
    True(lcp.Length > 0 && lcp[0] == 0x7E, "initial LCP did not follow response");
    Equal(NetworkPakLiveReadStatus.WouldBlock, resource.Read(64).Status);
}

static void ClientAfterComplete()
{
    using PppLiveResource resource = NewResource();
    CompleteHandshakeAndDrainInitialLcp(resource);
    Write(resource, "CLIENT"u8);
    Equal(NetworkPakLiveReadStatus.WouldBlock, resource.Read(64).Status);
}

static void PreambleThenClient()
{
    using PppLiveResource resource = NewResource();
    Write(resource, "noise-CLIENT"u8);
    Bytes("CLIENTSERVER"u8, Data(resource.Read(16)));
}

static void PartialPrefixRecovery()
{
    using PppLiveResource resource = NewResource();
    Write(resource, "CLICLIENT"u8);
    Bytes("CLIENTSERVER"u8, Data(resource.Read(16)));
}

static void ExactResponse()
{
    using PppLiveResource resource = NewResource();
    Write(resource, "CLIENT"u8);
    var output = new List<byte>();
    output.AddRange(Data(resource.Read(5)));
    output.AddRange(Data(resource.Read(5)));
    output.AddRange(Data(resource.Read(5)));
    Equal(12, output.Count);
    Bytes(Convert.FromHexString("434C49454E54534552564552"), output.ToArray());
}

static void ResponsePrecedesLcp()
{
    using PppLiveResource resource = NewResource();
    Write(resource, "CLIENT"u8);
    byte[] first = Data(resource.Read(16));
    Bytes("CLIENTSERVER"u8, first);
    byte[] second = Data(resource.Read(16));
    Equal((byte)0x7E, second[0]);
}

static void ClientNotFedToPpp()
{
    using PppLiveResource resource = NewResource();
    Write(resource, "CLIENT"u8);
    Data(resource.Read(16));
    True(resource.Session != null, "PPP session was not released");
    Equal(0L, resource.Session!.Statistics.BytesWritten);
    Equal(0L, resource.Session.Statistics.FramesReceived);
}

static void PostHandshakePpp()
{
    using PppLiveResource resource = NewResource();
    CompleteHandshakeAndDrainInitialLcp(resource);
    var packet = new LcpPacket(
        LcpConstants.ConfigureRequest,
        0x31,
        LcpOptionCodec.Mru(1500));
    Write(resource, new PppFrameEncoder().Encode(packet.ToPppFrame()));
    True(resource.Session != null, "PPP session missing");
    Equal(1L, resource.Session!.Statistics.FramesReceived);
    Equal(NetworkPakLiveReadStatus.Data, resource.Read(64).Status);
}

static void CloseClearsState()
{
    using PppLiveResource resource = NewResource();
    Write(resource, "CLI"u8);
    resource.Close("TEST_CLOSE");
    Equal(DirectConnectionHandshakeState.Closed, resource.HandshakeState);
    Equal(NetworkPakLiveReadStatus.Eof, resource.Read(16).Status);

    using PppLiveResource replacement = NewResource();
    Equal(NetworkPakLiveReadStatus.WouldBlock, replacement.Read(16).Status);
}

static void SecondSessionFresh()
{
    using PppLiveResource first = NewResource(ConnectionA);
    using PppLiveResource second = NewResource(ConnectionB);
    Write(first, "CLIENT"u8, ConnectionA);
    Bytes("CLIENTSERVER"u8, Data(first.Read(16)));
    Equal(NetworkPakLiveReadStatus.WouldBlock, second.Read(16).Status);
    Write(second, "CLIENT"u8, ConnectionB);
    Bytes("CLIENTSERVER"u8, Data(second.Read(16)));
}

static PppLiveResource NewResource(Guid? connection = null)
{
    Guid id = connection ?? ConnectionA;
    return new PppLiveResource(
        id,
        new PppAddressAssignment(
            Ipv4Address.FromOctets(10, 64, 0, 1),
            Ipv4Address.FromOctets(10, 64, 0, 2)),
        0x11223344,
        Context(id));
}

static ModuleContext Context(Guid connection) => new(
    AppContext.BaseDirectory,
    _ => { },
    "test",
    23,
    64 * 1024,
    "test")
{
    ConnectionId = connection,
};

static void Write(PppLiveResource resource, ReadOnlySpan<byte> bytes, Guid? connection = null)
{
    Guid id = connection ?? ConnectionA;
    NetworkPakWriteResult result = resource.Write(
        new NetworkPakWriteChunk(3, bytes.ToArray()),
        Context(id));
    True(result.Success, result.Error ?? "write failed");
}

static void CompleteHandshakeAndDrainInitialLcp(PppLiveResource resource)
{
    Write(resource, "CLIENT"u8);
    Bytes("CLIENTSERVER"u8, Data(resource.Read(16)));
    while (resource.Read(64).Status == NetworkPakLiveReadStatus.Data) { }
}

static byte[] Data(NetworkPakLiveReadResult result)
{
    Equal(NetworkPakLiveReadStatus.Data, result.Status);
    return result.Data;
}

static void Bytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException(
            $"expected {Convert.ToHexString(expected)}, actual {Convert.ToHexString(actual)}");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected {expected}, actual {actual}");
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal static class TestIds
{
    public static readonly Guid ConnectionA =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid ConnectionB =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
}
