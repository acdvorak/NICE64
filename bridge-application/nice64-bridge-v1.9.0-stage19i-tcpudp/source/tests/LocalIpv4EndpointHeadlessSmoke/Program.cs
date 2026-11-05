using System.Buffers.Binary;
using PicoSwitchController;

var tests = new (string Name, Action Run)[]
{
    ("01 IPv4 before IPCP Opened is not serviced", BeforeIpcpDropped),
    ("02 valid Echo Request produces one reply", ValidEchoReplies),
    ("03 reply addresses are swapped", ReplyAddresses),
    ("04 ICMP identifier is preserved", IdentifierPreserved),
    ("05 ICMP sequence is preserved", SequencePreserved),
    ("06 ICMP payload is preserved", PayloadPreserved),
    ("07 IPv4 total length is correct", TotalLengthCorrect),
    ("08 IPv4 checksum is valid", Ipv4ChecksumValid),
    ("09 ICMP checksum is valid", IcmpChecksumValid),
    ("10 malformed IPv4 length is dropped", MalformedLengthDropped),
    ("11 wrong destination is dropped", WrongDestinationDropped),
    ("12 unsupported protocol is dropped", UnsupportedProtocolDropped),
    ("13 fragmented IPv4 is dropped", FragmentDropped),
    ("14 invalid IPv4 checksum is dropped", BadIpv4ChecksumDropped),
    ("15 invalid ICMP checksum is dropped", BadIcmpChecksumDropped),
    ("16 close clears queued reply", CloseClearsReply),
    ("17 second session has no stale IPv4", SecondSessionFresh),
    ("18 odd payload checksum is valid", OddPayloadChecksum),
    ("19 zero payload checksum is valid", ZeroPayloadChecksum),
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

static void BeforeIpcpDropped()
{
    using PppSession session = NewSession();
    DrainFrames(session);
    session.Feed(Encode(new PppFrame(0x0021, EchoRequest("early"u8))));
    Equal(1L, session.Statistics.Ipv4FramesIgnored);
    Equal(NetworkPakLiveReadStatus.WouldBlock, session.Read(4096).Status);
}

static void ValidEchoReplies()
{
    using PppSession session = OpenSession();
    PppFrame reply = SendEcho(session, "hello"u8);
    Equal((ushort)0x0021, reply.Protocol);
    Equal((byte)0, reply.Information.Span[20]);
    Equal(1L, session.Statistics.IcmpEchoRequests);
    Equal(1L, session.Statistics.IcmpEchoReplies);
}

static void ReplyAddresses()
{
    using PppSession session = OpenSession();
    ReadOnlySpan<byte> reply = SendEcho(session, "address"u8).Information.Span;
    Equal(Local().Value, BinaryPrimitives.ReadUInt32BigEndian(reply.Slice(12, 4)));
    Equal(Remote().Value, BinaryPrimitives.ReadUInt32BigEndian(reply.Slice(16, 4)));
}

static void IdentifierPreserved()
{
    using PppSession session = OpenSession();
    ReadOnlySpan<byte> reply = SendEcho(session, "id"u8, 0xCAFE, 7).Information.Span;
    Equal((ushort)0xCAFE, BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(24, 2)));
}

static void SequencePreserved()
{
    using PppSession session = OpenSession();
    ReadOnlySpan<byte> reply = SendEcho(session, "seq"u8, 5, 0xBEEF).Information.Span;
    Equal((ushort)0xBEEF, BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(26, 2)));
}

static void PayloadPreserved()
{
    using PppSession session = OpenSession();
    byte[] payload = { 0x00, 0x7D, 0x7E, 0xFF, 0x21 };
    ReadOnlySpan<byte> reply = SendEcho(session, payload).Information.Span;
    Bytes(payload, reply[28..]);
}

static void TotalLengthCorrect()
{
    using PppSession session = OpenSession();
    ReadOnlySpan<byte> reply = SendEcho(session, "length"u8).Information.Span;
    Equal(reply.Length, (int)BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(2, 2)));
}

static void Ipv4ChecksumValid()
{
    using PppSession session = OpenSession();
    ReadOnlySpan<byte> reply = SendEcho(session, "ip-check"u8).Information.Span;
    Equal((ushort)0, LocalIpv4Endpoint.ComputeChecksum(reply[..20]));
}

static void IcmpChecksumValid()
{
    using PppSession session = OpenSession();
    ReadOnlySpan<byte> reply = SendEcho(session, "icmp-check"u8).Information.Span;
    Equal((ushort)0, LocalIpv4Endpoint.ComputeChecksum(reply[20..]));
}

static void MalformedLengthDropped()
{
    byte[] packet = EchoRequest("bad-length"u8);
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)(packet.Length + 1));
    Equal(LocalIpv4Disposition.InvalidTotalLength, Endpoint().Handle(packet).Disposition);
}

static void WrongDestinationDropped()
{
    byte[] packet = EchoRequest("wrong-dst"u8);
    Ipv4Address.FromOctets(10, 64, 0, 99).ToBytes().CopyTo(packet, 16);
    FixIpv4Checksum(packet);
    Equal(LocalIpv4Disposition.WrongDestination, Endpoint().Handle(packet).Disposition);
}

static void UnsupportedProtocolDropped()
{
    byte[] packet = EchoRequest("udp"u8);
    packet[9] = 99;
    FixIpv4Checksum(packet);
    Equal(LocalIpv4Disposition.UnsupportedProtocol, Endpoint().Handle(packet).Disposition);
}

static void FragmentDropped()
{
    byte[] packet = EchoRequest("fragment"u8);
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), 0x2000);
    FixIpv4Checksum(packet);
    Equal(LocalIpv4Disposition.Fragmented, Endpoint().Handle(packet).Disposition);
}

static void BadIpv4ChecksumDropped()
{
    byte[] packet = EchoRequest("bad-ip"u8);
    packet[8] ^= 1;
    Equal(LocalIpv4Disposition.InvalidHeaderChecksum, Endpoint().Handle(packet).Disposition);
}

static void BadIcmpChecksumDropped()
{
    byte[] packet = EchoRequest("bad-icmp"u8);
    packet[^1] ^= 1;
    Equal(LocalIpv4Disposition.InvalidIcmpChecksum, Endpoint().Handle(packet).Disposition);
}

static void CloseClearsReply()
{
    PppSession session = OpenSession();
    session.Feed(Encode(new PppFrame(0x0021, EchoRequest("close"u8))));
    session.Close("TEST_CLOSE");
    Equal(NetworkPakLiveReadStatus.Eof, session.Read(4096).Status);
}

static void SecondSessionFresh()
{
    PppSession first = OpenSession(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    first.Feed(Encode(new PppFrame(0x0021, EchoRequest("first"u8))));
    first.Close("TEST_CLOSE");
    using PppSession second = OpenSession(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    Equal(NetworkPakLiveReadStatus.WouldBlock, second.Read(4096).Status);
    Equal(0L, second.Statistics.IcmpEchoReplies);
}

static void OddPayloadChecksum()
{
    LocalIpv4Result result = Endpoint().Handle(EchoRequest(new byte[] { 1, 2, 3, 4, 5 }));
    True(result.Replied, result.Disposition.ToString());
    Equal((ushort)0, LocalIpv4Endpoint.ComputeChecksum(result.Reply.Span[20..]));
}

static void ZeroPayloadChecksum()
{
    LocalIpv4Result result = Endpoint().Handle(EchoRequest(Array.Empty<byte>()));
    True(result.Replied, result.Disposition.ToString());
    Equal(28, result.Reply.Length);
    Equal((ushort)0, LocalIpv4Endpoint.ComputeChecksum(result.Reply.Span[20..]));
}

static PppFrame SendEcho(
    PppSession session,
    ReadOnlySpan<byte> payload,
    ushort identifier = 0x1234,
    ushort sequence = 1)
{
    session.Feed(Encode(new PppFrame(0x0021, EchoRequest(payload, identifier, sequence))));
    PppFrame[] frames = DrainFrames(session);
    Equal(1, frames.Length);
    return frames[0];
}

static byte[] EchoRequest(
    ReadOnlySpan<byte> payload,
    ushort identifier = 0x1234,
    ushort sequence = 1)
{
    byte[] packet = new byte[20 + 8 + payload.Length];
    packet[0] = 0x45;
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)packet.Length);
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 0x3456);
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), 0x4000);
    packet[8] = 32;
    packet[9] = 1;
    Remote().ToBytes().CopyTo(packet, 12);
    Local().ToBytes().CopyTo(packet, 16);
    packet[20] = 8;
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24, 2), identifier);
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26, 2), sequence);
    payload.CopyTo(packet.AsSpan(28));
    ushort icmp = LocalIpv4Endpoint.ComputeChecksum(packet.AsSpan(20));
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), icmp);
    FixIpv4Checksum(packet);
    return packet;
}

static void FixIpv4Checksum(Span<byte> packet)
{
    packet[10] = 0;
    packet[11] = 0;
    ushort checksum = LocalIpv4Endpoint.ComputeChecksum(packet[..20]);
    BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(10, 2), checksum);
}

static LocalIpv4Endpoint Endpoint() => new(Addresses());

static PppSession NewSession(Guid? connection = null) => new(
    connection ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
    Addresses(), 0x11223344);

static PppSession OpenSession(Guid? connection = null)
{
    PppSession session = NewSession(connection);
    LcpPacket localLcp = ParseLcp(SingleFrame(DrainFrames(session)));
    session.Feed(Encode(new LcpPacket(
        LcpConstants.ConfigureRequest, 0x30,
        LcpOptionCodec.Accm(0)).ToPppFrame()));
    DrainFrames(session);
    session.Feed(Encode(new LcpPacket(
        LcpConstants.ConfigureAck, localLcp.Identifier, localLcp.Data).ToPppFrame()));
    IpcpPacket localIpcp = ParseIpcp(SingleFrame(DrainFrames(session)));
    session.Feed(Encode(new IpcpPacket(
        IpcpConstants.ConfigureRequest, 0x40,
        IpcpOptionCodec.IpAddress(Remote())).ToPppFrame()));
    DrainFrames(session);
    session.Feed(Encode(new IpcpPacket(
        IpcpConstants.ConfigureAck, localIpcp.Identifier, localIpcp.Data).ToPppFrame()));
    Equal(PppSessionState.Opened, session.State);
    return session;
}

static PppAddressAssignment Addresses() => new(Local(), Remote());
static Ipv4Address Local() => Ipv4Address.FromOctets(10, 64, 0, 1);
static Ipv4Address Remote() => Ipv4Address.FromOctets(10, 64, 0, 2);

static byte[] Encode(PppFrame frame) => new PppFrameEncoder().Encode(frame);

static PppFrame[] DrainFrames(PppSession session)
{
    var wire = new List<byte>();
    while (true)
    {
        NetworkPakLiveReadResult read = session.Read(4096);
        if (read.Status != NetworkPakLiveReadStatus.Data) break;
        wire.AddRange(read.Data);
    }
    if (wire.Count == 0) return Array.Empty<PppFrame>();
    var frames = new List<PppFrame>();
    new PppFrameDecoder().Feed(wire.ToArray(), frames.Add);
    return frames.ToArray();
}

static PppFrame SingleFrame(PppFrame[] frames)
{
    Equal(1, frames.Length);
    return frames[0];
}

static LcpPacket ParseLcp(PppFrame frame)
{
    True(LcpPacket.TryParse(frame, out LcpPacket? packet, out string? error),
        error ?? "LCP parse failed");
    return packet!;
}

static IpcpPacket ParseIpcp(PppFrame frame)
{
    True(IpcpPacket.TryParse(frame, out IpcpPacket? packet, out string? error),
        error ?? "IPCP parse failed");
    return packet!;
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
