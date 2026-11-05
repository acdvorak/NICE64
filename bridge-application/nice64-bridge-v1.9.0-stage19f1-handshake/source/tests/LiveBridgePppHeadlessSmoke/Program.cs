using PicoSwitchController;
using static TestIds;

if (typeof(Nice64PppGatewayModule).Assembly.GetName().Name != "Nice64.PppGateway")
{
    Console.WriteLine("FAIL PPP implementation is not owned by Nice64.PppGateway.dll");
    return 1;
}
using (var discovery = new NetworkPakBridgeService())
{
    INetworkPakModule? loaded = discovery.ListModules().FirstOrDefault(
        module => module.Id == Nice64PppGatewayModule.ModuleId);
    if (loaded?.GetType().Assembly.GetName().Name != "Nice64.PppGateway")
    {
        Console.WriteLine("FAIL ppp://gateway was not discovered through the external module loader");
        return 1;
    }
}

var tests = new (string Name, Action Run)[]
{
    ("01 OPEN live resource succeeds", LiveOpenSucceeds),
    ("02 live stream ID remains stable", StableStreamId),
    ("03 empty live read is WOULD_BLOCK", EmptyReadWouldBlock),
    ("04 data added after OPEN is visible", PostOpenDataVisible),
    ("05 live reads are limited to 16 bytes", PartialReads),
    ("06 live offset advances by delivered bytes", OffsetAdvances),
    ("07 temporary empty read preserves offset", EmptyReadPreservesOffset),
    ("08 CLOSE returns existing A3 response", CloseReturnsA3),
    ("09 read after CLOSE fails cleanly", ReadAfterCloseFails),
    ("10 disconnect disposes live resource", DisconnectDisposes),
    ("11 outbound queue bound is enforced", QueueBoundEnforced),
    ("12 live sessions cannot read each other's data", SessionReadIsolation),
    ("13 connection A WRITE reaches A only", ConnectionAWriteBinding),
    ("14 connection B WRITE reaches B only", ConnectionBWriteBinding),
    ("15 same resource opens distinct stable streams", DistinctConnectionStreams),
    ("16 closing A does not affect B", CloseOneKeepsOther),
    ("17 cross-connection live stream ID is rejected", CrossConnectionIdRejected),
    ("18 new PPP session starts isolated", PppSessionStartsIsolated),
    ("19 LCP Configure-Request is queued", LcpRequestQueued),
    ("20 peer LCP request produces Ack", PeerLcpRequestAcked),
    ("21 peer Ack completes LCP", LcpAckCompletes),
    ("22 IPCP starts after LCP OPENED", IpcpStartsAutomatically),
    ("23 zero-address IPCP request gets Nak", IpcpZeroAddressNak),
    ("24 assigned-address IPCP request gets Ack", IpcpAssignedAddressAck),
    ("25 local IPCP Ack reaches OPENED", IpcpAckOpensSession),
    ("26 empty PPP live read remains WOULD_BLOCK", PppWouldBlock),
    ("27 PPP frame split across writes decodes", SplitPppFrame),
    ("28 multiple PPP frames in one write decode", MultiplePppFrames),
    ("29 malformed PPP frame does not poison recovery", MalformedThenValidFrame),
    ("30 LCP Terminate-Request drains Ack then EOF", TerminateClosesSession),
    ("31 second PPP session negotiates independently", IndependentPppSessions),
    ("32 runtime state becomes OPENED", RuntimeStateOpened),
    ("33 inbound IPv4 payload is surfaced exactly", InboundIpv4Placeholder),
    ("34 outbound IPv4 payload is PPP encoded", OutboundIpv4Placeholder),
    ("35 finite speed resource still opens", FiniteOpenRegression),
    ("36 finite reads retain normal EOS", FiniteReadEosRegression),
    ("37 finite CLOSE retains A3 semantics", FiniteCloseRegression),
    ("38 finite stream data remains a fixed snapshot", FiniteSnapshotRegression),
    ("39 GUID session provider binds and cleans live stream", SessionProviderBinding),
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

static void LiveOpenSucceeds()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle handle = StreamHandle.Open(service, ConnectionA, Nice64PppGatewayModule.ResourceName);
    Equal((byte)0xA0, handle.OpenResponse.Command);
    Equal(10, handle.OpenResponse.Payload.Length);
    Equal(0u, ReadU32(handle.OpenResponse.Payload, 1));
}

static void StableStreamId()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle handle = StreamHandle.Open(service, ConnectionA, Nice64PppGatewayModule.ResourceName);
    StreamRead read = handle.Read();
    Equal(handle.StreamId, read.StreamId);
}

static void EmptyReadWouldBlock()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle handle = StreamHandle.Open(service, ConnectionA, Nice64PppGatewayModule.ResourceName);
    handle.Drain();
    StreamRead empty = handle.Read();
    Equal(0, empty.Data.Length);
    True(!empty.Eos, "temporary empty read reported EOS");
}

static void PostOpenDataVisible()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle handle = StreamHandle.Open(service, ConnectionA, Nice64PppGatewayModule.ResourceName);
    handle.Drain();
    handle.WriteWire(Encode(PeerLcpRequest(0x31)));
    StreamRead read = handle.Read();
    True(read.Data.Length > 0, "post-OPEN response was not visible");
}

static void PartialReads()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle handle = StreamHandle.Open(service, ConnectionA, Nice64PppGatewayModule.ResourceName);
    StreamRead first = handle.Read();
    Equal(16, first.Data.Length);
    StreamRead second = handle.Read();
    True(second.Data.Length is > 0 and <= 16, "second partial read invalid");
}

static void OffsetAdvances()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle handle = StreamHandle.Open(service, ConnectionA, Nice64PppGatewayModule.ResourceName);
    uint initialOffset = handle.Offset;
    StreamRead first = handle.Read();
    StreamRead second = handle.Read();
    Equal(initialOffset, first.Offset);
    Equal(initialOffset + (uint)first.Data.Length, second.Offset);
    Equal(second.Offset + (uint)second.Data.Length, handle.Offset);
}

static void EmptyReadPreservesOffset()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle handle = StreamHandle.Open(service, ConnectionA, Nice64PppGatewayModule.ResourceName);
    handle.Drain();
    uint before = handle.Offset;
    StreamRead empty = handle.Read();
    Equal(before, empty.Offset);
    Equal(before, handle.Offset);
}

static void CloseReturnsA3()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle handle = StreamHandle.Open(service, ConnectionA, Nice64PppGatewayModule.ResourceName);
    BridgeResponse close = handle.Close();
    True(close.Ok, close.Error);
    Equal((byte)0xA3, close.Command);
    Bytes(new[] { handle.StreamId }, close.Payload);
}

static void ReadAfterCloseFails()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle handle = StreamHandle.Open(service, ConnectionA, Nice64PppGatewayModule.ResourceName);
    handle.Close();
    BridgeResponse read = handle.ReadRaw();
    True(!read.Ok, "read after close unexpectedly succeeded");
    Equal("BAD_STREAM_ID", read.Error);
}

static void DisconnectDisposes()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle handle = StreamHandle.Open(service, ConnectionA, Nice64PppGatewayModule.ResourceName);
    service.DisconnectConnectionForTesting(ConnectionA);
    Equal("BAD_STREAM_ID", handle.ReadRaw().Error);
}

static void QueueBoundEnforced()
{
    using PppSession session = OpenPppSession(out _ , outboundCapacity: 128);
    byte[] payload = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
    True(!session.EnqueueIpv4Packet(payload), "oversized outbound frame was accepted");
    Equal(PppSessionState.Failed, session.State);
    Equal("PPP_OUTBOUND_QUEUE_OVERFLOW", session.FailureReason);
}

static void SessionReadIsolation() => VerifyOneConnectionWrite(ConnectionA, ConnectionB, 0x41);
static void ConnectionAWriteBinding() => VerifyOneConnectionWrite(ConnectionA, ConnectionB, 0x42);
static void ConnectionBWriteBinding() => VerifyOneConnectionWrite(ConnectionB, ConnectionA, 0x43);

static void DistinctConnectionStreams()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle a = StreamHandle.Open(service, ConnectionA, Nice64PppGatewayModule.ResourceName);
    StreamHandle b = StreamHandle.Open(service, ConnectionB, Nice64PppGatewayModule.ResourceName);
    True(a.StreamId != b.StreamId, "connections shared a stream ID");
    Equal(a.StreamId, a.Read().StreamId);
    Equal(b.StreamId, b.Read().StreamId);
}

static void CloseOneKeepsOther()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle a = StreamHandle.Open(service, ConnectionA, Nice64PppGatewayModule.ResourceName);
    StreamHandle b = StreamHandle.Open(service, ConnectionB, Nice64PppGatewayModule.ResourceName);
    a.Drain();
    b.Drain();
    a.Close();
    b.WriteWire(Encode(PeerLcpRequest(0x51)));
    True(b.Read().Data.Length > 0, "closing A affected B");
}

static void CrossConnectionIdRejected()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle a = StreamHandle.Open(service, ConnectionA, Nice64PppGatewayModule.ResourceName);
    byte[] request = new byte[5];
    request[0] = a.StreamId;
    BridgeResponse response = Exchange(service, ConnectionB, 90, "READ_STREAM_CHUNK", request);
    True(!response.Ok, "cross-connection read succeeded");
    Equal("BAD_STREAM_ID", response.Error);
}

static void PppSessionStartsIsolated()
{
    using var a = NewSession(ConnectionA);
    using var b = NewSession(ConnectionB);
    Equal(ConnectionA, a.ConnectionId);
    Equal(ConnectionB, b.ConnectionId);
    Equal(PppSessionState.LcpNegotiating, a.State);
    Equal(PppSessionState.LcpNegotiating, b.State);
}

static void LcpRequestQueued()
{
    using var session = NewSession(ConnectionA);
    PppFrame frame = SingleFrame(DrainFrames(session));
    Equal(LcpConstants.Protocol, frame.Protocol);
    True(LcpPacket.TryParse(frame, out LcpPacket? packet, out _), "queued LCP was malformed");
    Equal(LcpConstants.ConfigureRequest, packet!.Code);
}

static void PeerLcpRequestAcked()
{
    using var session = NewSession(ConnectionA);
    DrainFrames(session);
    session.Feed(Encode(PeerLcpRequest(0x61)));
    LcpPacket response = ParseLcp(SingleFrame(DrainFrames(session)));
    Equal(LcpConstants.ConfigureAck, response.Code);
    Equal((byte)0x61, response.Identifier);
}

static void LcpAckCompletes()
{
    using var session = NewSession(ConnectionA);
    LcpPacket ours = ParseLcp(SingleFrame(DrainFrames(session)));
    session.Feed(Encode(PeerLcpRequest(0x62)));
    DrainFrames(session);
    session.Feed(Encode(new LcpPacket(LcpConstants.ConfigureAck, ours.Identifier, ours.Data).ToPppFrame()));
    Equal(LcpState.Opened, session.LcpState);
}

static void IpcpStartsAutomatically()
{
    using var session = NewSession(ConnectionA);
    IpcpPacket request = OpenLcpAndGetIpcpRequest(session);
    Equal(IpcpConstants.ConfigureRequest, request.Code);
    Bytes(IpcpOptionCodec.IpAddress(Local()), request.Data.Span);
    Equal(PppSessionState.IpcpNegotiating, session.State);
}

static void IpcpZeroAddressNak()
{
    using var session = NewSession(ConnectionA);
    OpenLcpAndGetIpcpRequest(session);
    session.Feed(Encode(new IpcpPacket(
        IpcpConstants.ConfigureRequest, 0x70,
        IpcpOptionCodec.IpAddress(Ipv4Address.Zero)).ToPppFrame()));
    IpcpPacket response = ParseIpcp(SingleFrame(DrainFrames(session)));
    Equal(IpcpConstants.ConfigureNak, response.Code);
    Bytes(IpcpOptionCodec.IpAddress(Remote()), response.Data.Span);
}

static void IpcpAssignedAddressAck()
{
    using var session = NewSession(ConnectionA);
    OpenLcpAndGetIpcpRequest(session);
    session.Feed(Encode(PeerIpcpRequest(0x71)));
    IpcpPacket response = ParseIpcp(SingleFrame(DrainFrames(session)));
    Equal(IpcpConstants.ConfigureAck, response.Code);
}

static void IpcpAckOpensSession()
{
    using PppSession session = OpenPppSession(out _);
    Equal(IpcpState.Opened, session.IpcpState);
    Equal(PppSessionState.Opened, session.State);
}

static void PppWouldBlock()
{
    using var session = NewSession(ConnectionA);
    DrainFrames(session);
    Equal(NetworkPakLiveReadStatus.WouldBlock, session.Read(16).Status);
}

static void SplitPppFrame()
{
    using var session = NewSession(ConnectionA);
    DrainFrames(session);
    byte[] wire = Encode(PeerLcpRequest(0x72));
    int split = wire.Length / 2;
    session.Feed(wire.AsSpan(0, split));
    Equal(NetworkPakLiveReadStatus.WouldBlock, session.Read(16).Status);
    session.Feed(wire.AsSpan(split));
    Equal(LcpConstants.ConfigureAck, ParseLcp(SingleFrame(DrainFrames(session))).Code);
}

static void MultiplePppFrames()
{
    using var session = NewSession(ConnectionA);
    LcpPacket ours = ParseLcp(SingleFrame(DrainFrames(session)));
    byte[] request = Encode(PeerLcpRequest(0x73));
    byte[] ack = Encode(new LcpPacket(LcpConstants.ConfigureAck, ours.Identifier, ours.Data).ToPppFrame());
    session.Feed(request.Concat(ack).ToArray());
    IReadOnlyList<PppFrame> output = DrainFrames(session);
    Equal(2, output.Count);
    Equal(LcpConstants.ConfigureAck, ParseLcp(output[0]).Code);
    Equal(IpcpConstants.ConfigureRequest, ParseIpcp(output[1]).Code);
}

static void MalformedThenValidFrame()
{
    using var session = NewSession(ConnectionA);
    DrainFrames(session);
    byte[] bad = Encode(PeerLcpRequest(0x74));
    bad[^3] ^= 0x01;
    byte[] good = Encode(PeerLcpRequest(0x75));
    session.Feed(bad.Concat(good).ToArray());
    LcpPacket response = ParseLcp(SingleFrame(DrainFrames(session)));
    Equal((byte)0x75, response.Identifier);
    True(session.MalformedFrames >= 1, "malformed frame was not counted");
}

static void TerminateClosesSession()
{
    using PppSession session = OpenPppSession(out _);
    session.Feed(Encode(new LcpPacket(
        LcpConstants.TerminateRequest, 0x76, Array.Empty<byte>()).ToPppFrame()));
    LcpPacket response = ParseLcp(SingleFrame(DrainFrames(session)));
    Equal(LcpConstants.TerminateAck, response.Code);
    Equal(PppSessionState.Closed, session.State);
    Equal(NetworkPakLiveReadStatus.Eof, session.Read(16).Status);
}

static void IndependentPppSessions()
{
    using var a = NewSession(ConnectionA);
    using var b = NewSession(ConnectionB);
    LcpPacket aRequest = ParseLcp(SingleFrame(DrainFrames(a)));
    LcpPacket bRequest = ParseLcp(SingleFrame(DrainFrames(b)));
    a.Feed(Encode(PeerLcpRequest(0x77)));
    Equal(LcpState.AckSent, a.LcpState);
    Equal(LcpState.ReqSent, b.LcpState);
    Equal(aRequest.Identifier, bRequest.Identifier);
}

static void RuntimeStateOpened()
{
    using PppSession session = OpenPppSession(out _);
    Equal(PppSessionState.Opened, session.State);
    Equal(Remote(), session is not null ? Remote() : Ipv4Address.Zero);
}

static void InboundIpv4Placeholder()
{
    using PppSession session = OpenPppSession(out _);
    byte[] payload = { 0x45, 0, 0, 4, 1, 2, 3, 4 };
    session.Feed(Encode(new PppFrame(0x0021, payload)));
    True(session.TryDequeueIpv4(out byte[] actual), "IPv4 payload not surfaced");
    Bytes(payload, actual);
    Equal(1L, session.Statistics.Ipv4FramesObserved);
}

static void OutboundIpv4Placeholder()
{
    using PppSession session = OpenPppSession(out _);
    byte[] payload = { 0x45, 1, 2, 3, 4 };
    True(session.EnqueueIpv4Packet(payload), "outbound IPv4 enqueue failed");
    PppFrame frame = SingleFrame(DrainFrames(session));
    Equal((ushort)0x0021, frame.Protocol);
    Bytes(payload, frame.Information.Span);
}

static void FiniteOpenRegression()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle handle = StreamHandle.Open(service, ConnectionA, "speed://1k");
    Equal((byte)0xA0, handle.OpenResponse.Command);
    Equal(1024u, ReadU32(handle.OpenResponse.Payload, 1));
}

static void FiniteReadEosRegression()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle handle = StreamHandle.Open(service, ConnectionA, "speed://1k");
    int total = 0;
    StreamRead read;
    do
    {
        read = handle.Read();
        total += read.Data.Length;
    } while (!read.Eos);
    Equal(1024, total);
    Equal(16, read.Data.Length);
}

static void FiniteCloseRegression()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle handle = StreamHandle.Open(service, ConnectionA, "speed://1k");
    Equal((byte)0xA3, handle.Close().Command);
}

static void FiniteSnapshotRegression()
{
    using var service = new NetworkPakBridgeService();
    StreamHandle handle = StreamHandle.Open(service, ConnectionA, "speed://1k");
    byte[] data = handle.Drain(expectEof: true);
    Equal(1024, data.Length);
    Equal(SpeedTestModule.PatternByte(0), data[0]);
    Equal(SpeedTestModule.PatternByte(255), data[255]);
    Equal(SpeedTestModule.PatternByte(256), data[256]);
}

static void SessionProviderBinding()
{
    var provider = new FakeSessionProvider(ConnectionA);
    using var service = new NetworkPakBridgeService(sessionProvider: provider);
    BridgeResponse open = ExchangeDefault(
        service,
        1,
        "OPEN_STREAM",
        System.Text.Encoding.ASCII.GetBytes(Nice64PppGatewayModule.ResourceName));
    True(open.Ok, open.Error);
    byte streamId = open.Payload[0];
    provider.End();

    byte[] request = new byte[5];
    request[0] = streamId;
    BridgeResponse afterDisconnect = Exchange(
        service, ConnectionA, 2, "READ_STREAM_CHUNK", request);
    Equal("BAD_STREAM_ID", afterDisconnect.Error);
}

static void VerifyOneConnectionWrite(Guid writer, Guid other, byte identifier)
{
    using var service = new NetworkPakBridgeService();
    StreamHandle target = StreamHandle.Open(service, writer, Nice64PppGatewayModule.ResourceName);
    StreamHandle untouched = StreamHandle.Open(service, other, Nice64PppGatewayModule.ResourceName);
    target.Drain();
    untouched.Drain();
    target.WriteWire(Encode(PeerLcpRequest(identifier)));
    StreamRead otherRead = untouched.Read();
    Equal(0, otherRead.Data.Length);
    True(!otherRead.Eos, "unrelated session reached EOF");
    LcpPacket response = ParseLcp(SingleFrame(DecodeWire(target.Drain())));
    Equal(identifier, response.Identifier);
}

static PppSession NewSession(Guid connection, int outboundCapacity = PppSession.DefaultOutboundQueueCapacity) =>
    new(connection, Addresses(), 0x11223344, 1, 1, outboundCapacity);

static PppSession OpenPppSession(out IpcpPacket localIpcp, int outboundCapacity = PppSession.DefaultOutboundQueueCapacity)
{
    PppSession session = NewSession(ConnectionA, outboundCapacity);
    localIpcp = OpenLcpAndGetIpcpRequest(session);
    session.Feed(Encode(new IpcpPacket(
        IpcpConstants.ConfigureRequest, 0x80,
        IpcpOptionCodec.IpAddress(Ipv4Address.Zero)).ToPppFrame()));
    DrainFrames(session);
    session.Feed(Encode(PeerIpcpRequest(0x81)));
    DrainFrames(session);
    session.Feed(Encode(new IpcpPacket(
        IpcpConstants.ConfigureAck, localIpcp.Identifier, localIpcp.Data).ToPppFrame()));
    Equal(PppSessionState.Opened, session.State);
    return session;
}

static IpcpPacket OpenLcpAndGetIpcpRequest(PppSession session)
{
    LcpPacket local = ParseLcp(SingleFrame(DrainFrames(session)));
    session.Feed(Encode(PeerLcpRequest(0x30)));
    Equal(LcpConstants.ConfigureAck, ParseLcp(SingleFrame(DrainFrames(session))).Code);
    session.Feed(Encode(new LcpPacket(
        LcpConstants.ConfigureAck, local.Identifier, local.Data).ToPppFrame()));
    IpcpPacket ipcp = ParseIpcp(SingleFrame(DrainFrames(session)));
    Equal(LcpState.Opened, session.LcpState);
    return ipcp;
}

static PppFrame PeerLcpRequest(byte identifier) =>
    new LcpPacket(
        LcpConstants.ConfigureRequest,
        identifier,
        Options(LcpOptionCodec.Mru(1500), LcpOptionCodec.Magic(0xAABBCCDD)))
    .ToPppFrame();

static PppFrame PeerIpcpRequest(byte identifier) =>
    new IpcpPacket(
        IpcpConstants.ConfigureRequest,
        identifier,
        IpcpOptionCodec.IpAddress(Remote()))
    .ToPppFrame();

static LcpPacket ParseLcp(PppFrame frame)
{
    True(LcpPacket.TryParse(frame, out LcpPacket? packet, out string? error), error ?? "bad LCP");
    return packet!;
}

static IpcpPacket ParseIpcp(PppFrame frame)
{
    True(IpcpPacket.TryParse(frame, out IpcpPacket? packet, out string? error), error ?? "bad IPCP");
    return packet!;
}

static IReadOnlyList<PppFrame> DrainFrames(PppSession session)
{
    var bytes = new List<byte>();
    while (true)
    {
        NetworkPakLiveReadResult read = session.Read(16);
        if (read.Status == NetworkPakLiveReadStatus.Data)
        {
            bytes.AddRange(read.Data);
            continue;
        }
        if (read.Status is NetworkPakLiveReadStatus.WouldBlock or NetworkPakLiveReadStatus.Eof)
            break;
        throw new InvalidOperationException(read.Error ?? "PPP read failed");
    }
    return DecodeWire(bytes.ToArray());
}

static IReadOnlyList<PppFrame> DecodeWire(byte[] wire)
{
    var output = new List<PppFrame>();
    var decoder = new PppFrameDecoder();
    decoder.Feed(wire, output.Add);
    return output;
}

static PppFrame SingleFrame(IReadOnlyList<PppFrame> frames)
{
    Equal(1, frames.Count);
    return frames[0];
}

static byte[] Encode(PppFrame frame) => new PppFrameEncoder().Encode(frame);
static PppAddressAssignment Addresses() => new(Local(), Remote());
static Ipv4Address Local() => Ipv4Address.FromOctets(10, 64, 0, 1);
static Ipv4Address Remote() => Ipv4Address.FromOctets(10, 64, 0, 2);

static byte[] Options(params byte[][] options)
{
    var output = new byte[options.Sum(option => option.Length)];
    int offset = 0;
    foreach (byte[] option in options)
    {
        option.CopyTo(output, offset);
        offset += option.Length;
    }
    return output;
}

static uint ReadU32(byte[] bytes, int offset) =>
    System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));

static BridgeResponse Exchange(
    NetworkPakBridgeService service,
    Guid connection,
    byte sequence,
    string command,
    byte[] payload)
{
    string line = $"NETPAK_REQ_HEX|{sequence}|{command}|{Convert.ToHexString(payload)}";
    True(service.TryHandleRequest(connection, line, out string response), "request not handled");
    string[] parts = response.Split('|', 5);
    True(parts.Length == 5, $"malformed response: {response}");
    bool ok = parts[2] == "OK";
    byte responseCommand = Convert.ToByte(parts[3], 16);
    byte[] responsePayload = Convert.FromHexString(parts[4]);
    return new(ok, responseCommand, responsePayload,
        ok ? string.Empty : System.Text.Encoding.ASCII.GetString(responsePayload));
}

static BridgeResponse ExchangeDefault(
    NetworkPakBridgeService service,
    byte sequence,
    string command,
    byte[] payload)
{
    string line = $"NETPAK_REQ_HEX|{sequence}|{command}|{Convert.ToHexString(payload)}";
    True(service.TryHandleRequest(line, out string response), "request not handled");
    string[] parts = response.Split('|', 5);
    True(parts.Length == 5, $"malformed response: {response}");
    bool ok = parts[2] == "OK";
    byte responseCommand = Convert.ToByte(parts[3], 16);
    byte[] responsePayload = Convert.FromHexString(parts[4]);
    return new(ok, responseCommand, responsePayload,
        ok ? string.Empty : System.Text.Encoding.ASCII.GetString(responsePayload));
}

static void Bytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException($"expected {Convert.ToHexString(expected)}, actual {Convert.ToHexString(actual)}");
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

internal sealed record BridgeResponse(bool Ok, byte Command, byte[] Payload, string Error);
internal sealed record StreamRead(byte StreamId, uint Offset, byte[] Data, bool Eos);

internal static class TestIds
{
    public static readonly Guid ConnectionA =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid ConnectionB =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
}

internal sealed class FakeSessionProvider : IBridgeSessionProvider
{
    public FakeSessionProvider(Guid id)
    {
        Current = new BridgeSessionSnapshot(
            id.ToString("D"),
            BridgeSessionState.Connected,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            BridgeControlConnectionKind.SerialUsb,
            "test",
            "test-device",
            FirmwarePersonality.Network,
            BridgeAccessoryMode.NetworkPak);
    }

    public BridgeSessionSnapshot? Current { get; private set; }
    public event EventHandler<BridgeSessionChangedEventArgs>? CurrentChanged;

    public void End()
    {
        BridgeSessionSnapshot? previous = Current;
        Current = null;
        CurrentChanged?.Invoke(this, new(previous, null));
    }
}

internal sealed class StreamHandle
{
    private byte _sequence = 1;

    private StreamHandle(
        NetworkPakBridgeService service,
        Guid connection,
        BridgeResponse openResponse)
    {
        Service = service;
        Connection = connection;
        OpenResponse = openResponse;
        StreamId = openResponse.Payload[0];
    }

    public NetworkPakBridgeService Service { get; }
    public Guid Connection { get; }
    public BridgeResponse OpenResponse { get; }
    public byte StreamId { get; }
    public uint Offset { get; private set; }

    public static StreamHandle Open(
        NetworkPakBridgeService service,
        Guid connection,
        string resource,
        bool completeDirectHandshake = true)
    {
        BridgeResponse response = ExchangeCore(service, connection, 1, "OPEN_STREAM",
            System.Text.Encoding.ASCII.GetBytes(resource));
        if (!response.Ok) throw new InvalidOperationException(response.Error);
        var handle = new StreamHandle(service, connection, response) { _sequence = 2 };
        if (completeDirectHandshake &&
            string.Equals(resource, Nice64PppGatewayModule.ResourceName,
                StringComparison.OrdinalIgnoreCase))
            handle.CompleteDirectHandshake();
        return handle;
    }

    private void CompleteDirectHandshake()
    {
        WriteWire("CLIENT"u8);
        StreamRead response = Read();
        BytesCore("CLIENTSERVER"u8, response.Data);
    }

    public StreamRead Read()
    {
        BridgeResponse response = ReadRaw();
        if (!response.Ok) throw new InvalidOperationException(response.Error);
        byte[] payload = response.Payload;
        int count = payload[5];
        var data = payload.AsSpan(7, count).ToArray();
        var read = new StreamRead(payload[0], ReadU32Core(payload, 1), data, payload[6] != 0);
        Offset += (uint)count;
        return read;
    }

    public BridgeResponse ReadRaw()
    {
        byte[] request = new byte[5];
        request[0] = StreamId;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(1), Offset);
        return ExchangeCore(Service, Connection, _sequence++, "READ_STREAM_CHUNK", request);
    }

    public byte[] Drain(bool expectEof = false)
    {
        var output = new List<byte>();
        while (true)
        {
            StreamRead read = Read();
            output.AddRange(read.Data);
            if (read.Eos || read.Data.Length == 0)
            {
                if (expectEof && !read.Eos)
                    throw new InvalidOperationException("finite drain ended without EOS");
                break;
            }
        }
        return output.ToArray();
    }

    public void WriteWire(ReadOnlySpan<byte> bytes)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = Math.Min(21, bytes.Length - offset);
            byte flags = 0;
            if (offset == 0) flags |= 1;
            if (offset + count == bytes.Length) flags |= 2;
            byte[] payload = new byte[2 + count];
            payload[0] = flags;
            payload[1] = (byte)count;
            bytes.Slice(offset, count).CopyTo(payload.AsSpan(2));
            BridgeResponse response = ExchangeCore(
                Service, Connection, _sequence++, "WRITE_STREAM_CHUNK", payload);
            if (!response.Ok) throw new InvalidOperationException(response.Error);
            offset += count;
        }
    }

    public BridgeResponse Close() => ExchangeCore(
        Service, Connection, _sequence++, "CLOSE_STREAM", new[] { StreamId });

    private static BridgeResponse ExchangeCore(
        NetworkPakBridgeService service,
        Guid connection,
        byte sequence,
        string command,
        byte[] payload)
    {
        string line = $"NETPAK_REQ_HEX|{sequence}|{command}|{Convert.ToHexString(payload)}";
        if (!service.TryHandleRequest(connection, line, out string response))
            throw new InvalidOperationException("request not handled");
        string[] parts = response.Split('|', 5);
        if (parts.Length != 5) throw new InvalidOperationException($"bad response: {response}");
        bool ok = parts[2] == "OK";
        byte responseCommand = Convert.ToByte(parts[3], 16);
        byte[] responsePayload = Convert.FromHexString(parts[4]);
        return new(ok, responseCommand, responsePayload,
            ok ? string.Empty : System.Text.Encoding.ASCII.GetString(responsePayload));
    }

    private static uint ReadU32Core(byte[] bytes, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));

    private static void BytesCore(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(
                $"expected {Convert.ToHexString(expected)}, actual {Convert.ToHexString(actual)}");
    }
}
