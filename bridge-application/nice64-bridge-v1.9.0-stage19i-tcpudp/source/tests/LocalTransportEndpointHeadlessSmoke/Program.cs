using System.Buffers.Binary;
using PicoSwitchController;

const ushort UdpClientPort = 41000;
const ushort TcpClientPort = 41001;
const uint ClientIsn = 0x10203040;
byte[] UdpPayload = "NICE64-UDP-E9"u8.ToArray();
byte[] TcpPayload = "NICE64-TCP-E9"u8.ToArray();

var tests = new (string Name, Action Run)[]
{
    ("UDP valid checksum-zero request echoes", UdpZeroChecksum),
    ("UDP valid nonzero checksum echoes", UdpNonzeroChecksum),
    ("UDP bad checksum drops", UdpBadChecksum),
    ("UDP wrong destination IP drops", UdpWrongDestination),
    ("UDP wrong source IP drops", UdpWrongSource),
    ("UDP wrong destination port drops", UdpWrongPort),
    ("UDP malformed length drops", UdpBadLength),
    ("UDP fragmented IPv4 drops", UdpFragmented),
    ("UDP response addresses and ports swap", UdpEndpointsSwap),
    ("UDP exact payload preserved", UdpPayloadPreserved),
    ("UDP response IPv4 checksum valid", UdpIpv4Checksum),
    ("UDP response checksum valid and nonzero", UdpReplyChecksum),
    ("UDP is gated before IPCP Opened", UdpIpcpGate),
    ("TCP SYN produces SYN-ACK", TcpSynAck),
    ("TCP sequence and acknowledgment are correct", TcpSequenceAck),
    ("TCP ACK establishes", TcpEstablish),
    ("TCP data returns exact echo", TcpDataEcho),
    ("TCP response checksum valid", TcpReplyChecksum),
    ("TCP bad checksum drops", TcpBadChecksum),
    ("TCP wrong destination IP drops", TcpWrongDestination),
    ("TCP wrong source IP drops", TcpWrongSource),
    ("TCP wrong destination port drops", TcpWrongPort),
    ("TCP invalid ACK drops", TcpInvalidAck),
    ("TCP duplicate SYN repeats SYN-ACK", TcpDuplicateSyn),
    ("TCP retransmitted data repeats exact echo", TcpRetransmit),
    ("TCP FIN performs bounded close", TcpFinClose),
    ("TCP is gated before IPCP Opened", TcpIpcpGate),
    ("new endpoint has no stale TCP state", TcpFreshEndpoint),
};

int passed = 0;
foreach ((string name, Action run) in tests)
{
    try { run(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { Console.WriteLine($"FAIL {name}: {error.Message}"); }
}
Console.WriteLine($"RESULT {passed}/{tests.Length} PASS");
return passed == tests.Length ? 0 : 1;

void UdpZeroChecksum() => Equal(LocalIpv4Disposition.UdpEchoReply,
    Endpoint().Handle(UdpPacket(UdpPayload, checksum: false)).Disposition);
void UdpNonzeroChecksum() => Equal(LocalIpv4Disposition.UdpEchoReply,
    Endpoint().Handle(UdpPacket(UdpPayload, checksum: true)).Disposition);
void UdpBadChecksum()
{
    byte[] packet = UdpPacket(UdpPayload, checksum: true); packet[^1] ^= 1;
    Equal(LocalIpv4Disposition.InvalidUdpChecksum, Endpoint().Handle(packet).Disposition);
}
void UdpWrongDestination()
{
    byte[] packet = UdpPacket(UdpPayload, false); Address(10,64,0,99).ToBytes().CopyTo(packet,16); FixIp(packet);
    Equal(LocalIpv4Disposition.WrongDestination, Endpoint().Handle(packet).Disposition);
}
void UdpWrongSource()
{
    byte[] packet = UdpPacket(UdpPayload, false); Address(10,64,0,99).ToBytes().CopyTo(packet,12); FixIp(packet);
    Equal(LocalIpv4Disposition.WrongSource, Endpoint().Handle(packet).Disposition);
}
void UdpWrongPort() => Equal(LocalIpv4Disposition.WrongUdpPort,
    Endpoint().Handle(UdpPacket(UdpPayload, false, 19092)).Disposition);
void UdpBadLength()
{
    byte[] packet = UdpPacket(UdpPayload, false); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), 9);
    Equal(LocalIpv4Disposition.InvalidUdpLength, Endpoint().Handle(packet).Disposition);
}
void UdpFragmented()
{
    byte[] packet = UdpPacket(UdpPayload, false); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), 0x2000); FixIp(packet);
    Equal(LocalIpv4Disposition.Fragmented, Endpoint().Handle(packet).Disposition);
}
void UdpEndpointsSwap()
{
    ReadOnlySpan<byte> reply = Endpoint().Handle(UdpPacket(UdpPayload, true)).Reply.Span;
    Equal(Local().Value, BinaryPrimitives.ReadUInt32BigEndian(reply.Slice(12,4)));
    Equal(Remote().Value, BinaryPrimitives.ReadUInt32BigEndian(reply.Slice(16,4)));
    Equal((ushort)19090, BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(20,2)));
    Equal(UdpClientPort, BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(22,2)));
}
void UdpPayloadPreserved() => Bytes(UdpPayload, Endpoint().Handle(UdpPacket(UdpPayload,true)).Reply.Span[28..]);
void UdpIpv4Checksum() => Equal((ushort)0, Checksum(Endpoint().Handle(UdpPacket(UdpPayload,true)).Reply.Span[..20]));
void UdpReplyChecksum()
{
    ReadOnlySpan<byte> reply = Endpoint().Handle(UdpPacket(UdpPayload,true)).Reply.Span;
    True(BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(26,2)) != 0, "zero UDP reply checksum");
    Equal((ushort)0, TransportChecksum(Local(), Remote(), 17, reply[20..]));
}
void UdpIpcpGate()
{
    using PppSession session = NewSession(); Drain(session);
    session.Feed(Encode(new PppFrame(0x0021, UdpPacket(UdpPayload,false))));
    Equal(1L, session.Statistics.Ipv4FramesIgnored); Equal(0, Drain(session).Length);
}

void TcpSynAck() => Equal(LocalIpv4Disposition.TcpSynAck, Endpoint().Handle(TcpPacket(ClientIsn,0,0x02)).Disposition);
void TcpSequenceAck()
{
    LocalIpv4Result result = Endpoint().Handle(TcpPacket(ClientIsn,0,0x02));
    ReadOnlySpan<byte> tcp = result.Reply.Span[20..];
    Equal(LocalIpv4Endpoint.TcpInitialSequence, BinaryPrimitives.ReadUInt32BigEndian(tcp.Slice(4,4)));
    Equal(ClientIsn + 1, BinaryPrimitives.ReadUInt32BigEndian(tcp.Slice(8,4)));
    Equal((byte)0x12, tcp[13]);
}
void TcpEstablish()
{
    LocalIpv4Endpoint endpoint = ConnectedTcp();
    Equal(LocalIpv4Disposition.TcpEstablished,
        endpoint.Handle(TcpPacket(ClientIsn+1, LocalIpv4Endpoint.TcpInitialSequence+1, 0x10)).Disposition);
}
void TcpDataEcho()
{
    LocalIpv4Endpoint endpoint = EstablishedTcp();
    LocalIpv4Result result = endpoint.Handle(TcpPacket(ClientIsn+1,
        LocalIpv4Endpoint.TcpInitialSequence+1, 0x18, TcpPayload));
    Equal(LocalIpv4Disposition.TcpDataEcho, result.Disposition);
    Bytes(TcpPayload, result.Reply.Span[40..]);
}
void TcpReplyChecksum()
{
    LocalIpv4Endpoint endpoint = EstablishedTcp();
    ReadOnlySpan<byte> reply = endpoint.Handle(TcpPacket(ClientIsn+1,
        LocalIpv4Endpoint.TcpInitialSequence+1, 0x18, TcpPayload)).Reply.Span;
    Equal((ushort)0, Checksum(reply[..20]));
    Equal((ushort)0, TransportChecksum(Local(), Remote(), 6, reply[20..]));
}
void TcpBadChecksum()
{
    byte[] packet = TcpPacket(ClientIsn,0,0x02); packet[36] ^= 1;
    Equal(LocalIpv4Disposition.InvalidTcpChecksum, Endpoint().Handle(packet).Disposition);
}
void TcpWrongDestination()
{
    byte[] packet = TcpPacket(ClientIsn,0,0x02); Address(10,64,0,99).ToBytes().CopyTo(packet,16); FixIp(packet);
    Equal(LocalIpv4Disposition.WrongDestination, Endpoint().Handle(packet).Disposition);
}
void TcpWrongSource()
{
    byte[] packet = TcpPacket(ClientIsn,0,0x02); Address(10,64,0,99).ToBytes().CopyTo(packet,12); FixIp(packet);
    Equal(LocalIpv4Disposition.WrongSource, Endpoint().Handle(packet).Disposition);
}
void TcpWrongPort() => Equal(LocalIpv4Disposition.WrongTcpPort,
    Endpoint().Handle(TcpPacket(ClientIsn,0,0x02, default,19092)).Disposition);
void TcpInvalidAck()
{
    LocalIpv4Endpoint endpoint = ConnectedTcp();
    Equal(LocalIpv4Disposition.InvalidTcpAcknowledgment,
        endpoint.Handle(TcpPacket(ClientIsn+1, 7, 0x10)).Disposition);
}
void TcpDuplicateSyn()
{
    LocalIpv4Endpoint endpoint = Endpoint();
    byte[] first = endpoint.Handle(TcpPacket(ClientIsn,0,0x02)).Reply.ToArray();
    byte[] second = endpoint.Handle(TcpPacket(ClientIsn,0,0x02)).Reply.ToArray();
    Bytes(first, second);
}
void TcpRetransmit()
{
    LocalIpv4Endpoint endpoint = EstablishedTcp();
    byte[] packet = TcpPacket(ClientIsn+1, LocalIpv4Endpoint.TcpInitialSequence+1, 0x18, TcpPayload);
    byte[] first = endpoint.Handle(packet).Reply.ToArray();
    byte[] second = endpoint.Handle(packet).Reply.ToArray();
    Bytes(first, second);
}
void TcpFinClose()
{
    LocalIpv4Endpoint endpoint = EstablishedTcp();
    LocalIpv4Result fin = endpoint.Handle(TcpPacket(ClientIsn+1,
        LocalIpv4Endpoint.TcpInitialSequence+1, 0x11));
    Equal(LocalIpv4Disposition.TcpFinAck, fin.Disposition);
    ReadOnlySpan<byte> tcp = fin.Reply.Span[20..];
    Equal((byte)0x11, tcp[13]);
    LocalIpv4Result closed = endpoint.Handle(TcpPacket(ClientIsn+2,
        LocalIpv4Endpoint.TcpInitialSequence+2, 0x10));
    Equal(LocalIpv4Disposition.TcpClosed, closed.Disposition);
}
void TcpIpcpGate()
{
    using PppSession session = NewSession(); Drain(session);
    session.Feed(Encode(new PppFrame(0x0021, TcpPacket(ClientIsn,0,0x02))));
    Equal(1L, session.Statistics.Ipv4FramesIgnored); Equal(0, Drain(session).Length);
}
void TcpFreshEndpoint()
{
    LocalIpv4Endpoint first = ConnectedTcp();
    LocalIpv4Endpoint second = Endpoint();
    Equal(LocalIpv4Disposition.InvalidTcpState,
        second.Handle(TcpPacket(ClientIsn+1, LocalIpv4Endpoint.TcpInitialSequence+1, 0x10)).Disposition);
}

LocalIpv4Endpoint ConnectedTcp()
{
    LocalIpv4Endpoint endpoint = Endpoint();
    endpoint.Handle(TcpPacket(ClientIsn,0,0x02));
    return endpoint;
}
LocalIpv4Endpoint EstablishedTcp()
{
    LocalIpv4Endpoint endpoint = ConnectedTcp();
    endpoint.Handle(TcpPacket(ClientIsn+1, LocalIpv4Endpoint.TcpInitialSequence+1, 0x10));
    return endpoint;
}
byte[] UdpPacket(ReadOnlySpan<byte> payload, bool checksum, ushort destinationPort=19090)
{
    byte[] segment = new byte[8+payload.Length];
    BinaryPrimitives.WriteUInt16BigEndian(segment,UdpClientPort);
    BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2),destinationPort);
    BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(4),(ushort)segment.Length);
    payload.CopyTo(segment.AsSpan(8));
    if(checksum) BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(6),TransportChecksum(Remote(),Local(),17,segment));
    return IpPacket(17,segment);
}
byte[] TcpPacket(uint sequence,uint acknowledgment,byte flags,ReadOnlySpan<byte> payload=default,ushort destinationPort=19091)
{
    byte[] segment=new byte[20+payload.Length];
    BinaryPrimitives.WriteUInt16BigEndian(segment,TcpClientPort);
    BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2),destinationPort);
    BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(4),sequence);
    BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(8),acknowledgment);
    segment[12]=0x50; segment[13]=flags;
    BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(14),4096);
    payload.CopyTo(segment.AsSpan(20));
    BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(16),TransportChecksum(Remote(),Local(),6,segment));
    return IpPacket(6,segment);
}
byte[] IpPacket(byte protocol,ReadOnlySpan<byte> segment)
{
    byte[] packet=new byte[20+segment.Length]; packet[0]=0x45;
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2),(ushort)packet.Length);
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4),0x3456);
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6),0x4000);
    packet[8]=32; packet[9]=protocol; Remote().ToBytes().CopyTo(packet,12); Local().ToBytes().CopyTo(packet,16);
    segment.CopyTo(packet.AsSpan(20)); FixIp(packet); return packet;
}
ushort TransportChecksum(Ipv4Address source,Ipv4Address destination,byte protocol,ReadOnlySpan<byte> segment)
{
    byte[] pseudo=new byte[12+segment.Length]; source.ToBytes().CopyTo(pseudo,0); destination.ToBytes().CopyTo(pseudo,4);
    pseudo[9]=protocol; BinaryPrimitives.WriteUInt16BigEndian(pseudo.AsSpan(10),(ushort)segment.Length);
    segment.CopyTo(pseudo.AsSpan(12)); return Checksum(pseudo);
}
ushort Checksum(ReadOnlySpan<byte> bytes)
{
    uint sum=0; int i=0; for(;i+1<bytes.Length;i+=2) sum+=(uint)((bytes[i]<<8)|bytes[i+1]);
    if(i<bytes.Length) sum+=(uint)bytes[i]<<8; while((sum>>16)!=0) sum=(sum&0xFFFF)+(sum>>16); return (ushort)~sum;
}
void FixIp(Span<byte> packet){packet[10]=packet[11]=0;BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(10,2),Checksum(packet[..20]));}
LocalIpv4Endpoint Endpoint()=>new(Addresses());
PppAddressAssignment Addresses()=>new(Local(),Remote());
Ipv4Address Local()=>Address(10,64,0,1); Ipv4Address Remote()=>Address(10,64,0,2);
Ipv4Address Address(byte a,byte b,byte c,byte d)=>Ipv4Address.FromOctets(a,b,c,d);
PppSession NewSession()=>new(Guid.Parse("11111111-1111-1111-1111-111111111111"),Addresses(),0x11223344);
byte[] Encode(PppFrame frame)=>new PppFrameEncoder().Encode(frame);
PppFrame[] Drain(PppSession session)
{
    var wire=new List<byte>(); while(true){var read=session.Read(4096);if(read.Status!=NetworkPakLiveReadStatus.Data)break;wire.AddRange(read.Data);}
    var frames=new List<PppFrame>(); if(wire.Count>0)new PppFrameDecoder().Feed(wire.ToArray(),frames.Add);return frames.ToArray();
}
void Bytes(ReadOnlySpan<byte> expected,ReadOnlySpan<byte> actual){if(!expected.SequenceEqual(actual))throw new InvalidOperationException($"expected {Convert.ToHexString(expected)}, actual {Convert.ToHexString(actual)}");}
void Equal<T>(T expected,T actual){if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new InvalidOperationException($"expected {expected}, actual {actual}");}
void True(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
