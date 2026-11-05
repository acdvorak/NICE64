using System.Buffers.Binary;

namespace PicoSwitchController;

internal enum LocalIpv4Disposition
{
    EchoReply, UdpEchoReply, TcpSynAck, TcpEstablished, TcpDataEcho, TcpFinAck, TcpClosed,
    TooShort, NotIpv4, InvalidHeaderLength, InvalidTotalLength, InvalidHeaderChecksum,
    Fragmented, WrongSource, WrongDestination, UnsupportedProtocol,
    InvalidIcmpLength, InvalidIcmpChecksum, UnsupportedIcmp,
    InvalidUdpLength, InvalidUdpChecksum, WrongUdpPort,
    InvalidTcpLength, InvalidTcpChecksum, WrongTcpPort, TcpBusy,
    InvalidTcpState, InvalidTcpSequence, InvalidTcpAcknowledgment,
}

internal sealed record LocalIpv4Result(
    LocalIpv4Disposition Disposition, Ipv4Address Source, Ipv4Address Destination,
    byte Protocol, int TotalLength, ushort IcmpIdentifier = 0, ushort IcmpSequence = 0,
    ushort SourcePort = 0, ushort DestinationPort = 0, uint TcpSequence = 0,
    uint TcpAcknowledgment = 0, byte TcpFlags = 0, int PayloadLength = 0,
    ReadOnlyMemory<byte> Reply = default)
{
    public bool Replied => !Reply.IsEmpty;
    public bool Handled => Disposition is LocalIpv4Disposition.EchoReply or
        LocalIpv4Disposition.UdpEchoReply or LocalIpv4Disposition.TcpSynAck or
        LocalIpv4Disposition.TcpEstablished or LocalIpv4Disposition.TcpDataEcho or
        LocalIpv4Disposition.TcpFinAck or LocalIpv4Disposition.TcpClosed;
}

/// <summary>IPCP-gated local ICMP and bounded Stage 19I UDP/TCP echo endpoint.</summary>
internal sealed class LocalIpv4Endpoint
{
    internal const ushort UdpEchoPort = 19090;
    internal const ushort TcpEchoPort = 19091;
    internal const uint TcpInitialSequence = 0x4E364900;
    private const byte IcmpProtocol = 1, TcpProtocol = 6, UdpProtocol = 17;
    private const byte TcpFin = 0x01, TcpSyn = 0x02, TcpRst = 0x04, TcpPsh = 0x08, TcpAck = 0x10;
    private enum TcpState { Closed, SynReceived, Established, LastAck }

    private readonly PppAddressAssignment _addresses;
    private TcpState _tcpState;
    private ushort _tcpPeerPort;
    private uint _tcpPeerInitial, _tcpPeerNext, _tcpLocalNext, _lastPeerDataSequence;
    private byte[] _lastPeerData = Array.Empty<byte>();
    private byte[] _lastDataReply = Array.Empty<byte>();
    private byte[] _lastFinReply = Array.Empty<byte>();

    public LocalIpv4Endpoint(PppAddressAssignment addresses) =>
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));

    public LocalIpv4Result Handle(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 20) return Result(LocalIpv4Disposition.TooShort);
        if ((packet[0] >> 4) != 4) return Result(LocalIpv4Disposition.NotIpv4);
        int headerLength = (packet[0] & 0x0F) * 4;
        if (headerLength < 20 || headerLength > packet.Length)
            return Result(LocalIpv4Disposition.InvalidHeaderLength);
        int totalLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        if (totalLength < headerLength || totalLength != packet.Length)
            return Result(LocalIpv4Disposition.InvalidTotalLength, packet, totalLength);
        if (ComputeChecksum(packet[..headerLength]) != 0)
            return Result(LocalIpv4Disposition.InvalidHeaderChecksum, packet, totalLength);
        if ((BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2)) & 0x3FFF) != 0)
            return Result(LocalIpv4Disposition.Fragmented, packet, totalLength);

        Ipv4Address source = ReadAddress(packet.Slice(12, 4));
        Ipv4Address destination = ReadAddress(packet.Slice(16, 4));
        byte protocol = packet[9];
        if (source != _addresses.Remote)
            return new(LocalIpv4Disposition.WrongSource, source, destination, protocol, totalLength);
        if (destination != _addresses.Local)
            return new(LocalIpv4Disposition.WrongDestination, source, destination, protocol, totalLength);
        return protocol switch
        {
            IcmpProtocol => HandleIcmp(packet, headerLength, totalLength, source, destination),
            UdpProtocol => HandleUdp(packet, headerLength, totalLength, source, destination),
            TcpProtocol => HandleTcp(packet, headerLength, totalLength, source, destination),
            _ => new(LocalIpv4Disposition.UnsupportedProtocol, source, destination, protocol, totalLength),
        };
    }

    private LocalIpv4Result HandleIcmp(ReadOnlySpan<byte> packet, int headerLength,
        int totalLength, Ipv4Address source, Ipv4Address destination)
    {
        ReadOnlySpan<byte> icmp = packet[headerLength..];
        if (icmp.Length < 8)
            return new(LocalIpv4Disposition.InvalidIcmpLength, source, destination, IcmpProtocol, totalLength);
        if (ComputeChecksum(icmp) != 0)
            return new(LocalIpv4Disposition.InvalidIcmpChecksum, source, destination, IcmpProtocol, totalLength);
        if (icmp[0] != 8 || icmp[1] != 0)
            return new(LocalIpv4Disposition.UnsupportedIcmp, source, destination, IcmpProtocol, totalLength);
        ushort identifier = BinaryPrimitives.ReadUInt16BigEndian(icmp.Slice(4, 2));
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(icmp.Slice(6, 2));
        byte[] reply = packet.ToArray();
        reply[8] = 64;
        _addresses.Local.ToBytes().CopyTo(reply, 12);
        _addresses.Remote.ToBytes().CopyTo(reply, 16);
        SetIpv4Checksum(reply, headerLength);
        Span<byte> replyIcmp = reply.AsSpan(headerLength);
        replyIcmp[0] = 0;
        replyIcmp[2] = replyIcmp[3] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(replyIcmp.Slice(2, 2), ComputeChecksum(replyIcmp));
        return new(LocalIpv4Disposition.EchoReply, source, destination, IcmpProtocol,
            totalLength, identifier, sequence, PayloadLength: icmp.Length - 8, Reply: reply);
    }

    private LocalIpv4Result HandleUdp(ReadOnlySpan<byte> packet, int headerLength,
        int totalLength, Ipv4Address source, Ipv4Address destination)
    {
        ReadOnlySpan<byte> udp = packet[headerLength..];
        if (udp.Length < 8) return TransportResult(LocalIpv4Disposition.InvalidUdpLength,
            source, destination, UdpProtocol, totalLength, udp);
        ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(udp);
        ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(udp.Slice(2, 2));
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(udp.Slice(4, 2));
        if (length < 8 || length != udp.Length)
            return TransportResult(LocalIpv4Disposition.InvalidUdpLength, source, destination,
                UdpProtocol, totalLength, udp, sourcePort, destinationPort);
        ushort incomingChecksum = BinaryPrimitives.ReadUInt16BigEndian(udp.Slice(6, 2));
        if (incomingChecksum != 0 && ComputeTransportChecksum(source, destination, UdpProtocol, udp) != 0)
            return TransportResult(LocalIpv4Disposition.InvalidUdpChecksum, source, destination,
                UdpProtocol, totalLength, udp, sourcePort, destinationPort);
        if (destinationPort != UdpEchoPort)
            return TransportResult(LocalIpv4Disposition.WrongUdpPort, source, destination,
                UdpProtocol, totalLength, udp, sourcePort, destinationPort);
        ReadOnlySpan<byte> payload = udp[8..];
        byte[] replyUdp = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(replyUdp, UdpEchoPort);
        BinaryPrimitives.WriteUInt16BigEndian(replyUdp.AsSpan(2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(replyUdp.AsSpan(4), (ushort)replyUdp.Length);
        payload.CopyTo(replyUdp.AsSpan(8));
        ushort checksum = ComputeTransportChecksum(_addresses.Local, _addresses.Remote, UdpProtocol, replyUdp);
        BinaryPrimitives.WriteUInt16BigEndian(replyUdp.AsSpan(6), checksum == 0 ? (ushort)0xFFFF : checksum);
        return new(LocalIpv4Disposition.UdpEchoReply, source, destination, UdpProtocol,
            totalLength, SourcePort: sourcePort, DestinationPort: destinationPort,
            PayloadLength: payload.Length,
            Reply: BuildIpv4Reply(packet, UdpProtocol, replyUdp));
    }

    private LocalIpv4Result HandleTcp(ReadOnlySpan<byte> packet, int headerLength,
        int totalLength, Ipv4Address source, Ipv4Address destination)
    {
        ReadOnlySpan<byte> tcp = packet[headerLength..];
        if (tcp.Length < 20) return TcpResult(LocalIpv4Disposition.InvalidTcpLength,
            source, destination, totalLength, tcp);
        int tcpHeaderLength = (tcp[12] >> 4) * 4;
        if (tcpHeaderLength < 20 || tcpHeaderLength > tcp.Length)
            return TcpResult(LocalIpv4Disposition.InvalidTcpLength, source, destination, totalLength, tcp);
        ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(tcp);
        ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(tcp.Slice(2, 2));
        uint sequence = BinaryPrimitives.ReadUInt32BigEndian(tcp.Slice(4, 4));
        uint acknowledgment = BinaryPrimitives.ReadUInt32BigEndian(tcp.Slice(8, 4));
        byte flags = tcp[13];
        ReadOnlySpan<byte> payload = tcp[tcpHeaderLength..];
        if (ComputeTransportChecksum(source, destination, TcpProtocol, tcp) != 0)
            return TcpResult(LocalIpv4Disposition.InvalidTcpChecksum, source, destination,
                totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);
        if (destinationPort != TcpEchoPort)
            return TcpResult(LocalIpv4Disposition.WrongTcpPort, source, destination,
                totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);
        if ((flags & TcpRst) != 0)
        {
            ResetTcp();
            return TcpResult(LocalIpv4Disposition.TcpClosed, source, destination,
                totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);
        }

        bool syn = (flags & TcpSyn) != 0, ack = (flags & TcpAck) != 0, fin = (flags & TcpFin) != 0;
        if (syn && !ack && payload.IsEmpty)
        {
            if (_tcpState != TcpState.Closed &&
                (sourcePort != _tcpPeerPort || sequence != _tcpPeerInitial))
                return TcpResult(LocalIpv4Disposition.TcpBusy, source, destination,
                    totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);
            if (_tcpState == TcpState.Closed)
            {
                _tcpPeerPort = sourcePort;
                _tcpPeerInitial = sequence;
                _tcpPeerNext = sequence + 1;
                _tcpLocalNext = TcpInitialSequence + 1;
                _tcpState = TcpState.SynReceived;
            }
            byte[] reply = BuildTcpReply(packet, sourcePort, TcpInitialSequence,
                _tcpPeerNext, TcpSyn | TcpAck, ReadOnlySpan<byte>.Empty);
            return TcpResult(LocalIpv4Disposition.TcpSynAck, source, destination,
                totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags, reply);
        }
        if (_tcpState == TcpState.Closed || sourcePort != _tcpPeerPort)
            return TcpResult(LocalIpv4Disposition.InvalidTcpState, source, destination,
                totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);

        if (_tcpState == TcpState.SynReceived)
        {
            if (!ack || sequence != _tcpPeerNext || acknowledgment != _tcpLocalNext)
                return TcpResult(LocalIpv4Disposition.InvalidTcpAcknowledgment, source, destination,
                    totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);
            _tcpState = TcpState.Established;
            if (payload.IsEmpty && !fin)
                return TcpResult(LocalIpv4Disposition.TcpEstablished, source, destination,
                    totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);
        }

        if (_tcpState == TcpState.LastAck)
        {
            if (fin && sequence + 1 == _tcpPeerNext && _lastFinReply.Length != 0)
                return TcpResult(LocalIpv4Disposition.TcpFinAck, source, destination,
                    totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags, _lastFinReply);
            if (ack && sequence == _tcpPeerNext && acknowledgment == _tcpLocalNext)
            {
                ResetTcp();
                return TcpResult(LocalIpv4Disposition.TcpClosed, source, destination,
                    totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);
            }
            return TcpResult(LocalIpv4Disposition.InvalidTcpAcknowledgment, source, destination,
                totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);
        }

        if (!payload.IsEmpty && sequence == _lastPeerDataSequence &&
            payload.SequenceEqual(_lastPeerData) && _lastDataReply.Length != 0)
            return TcpResult(LocalIpv4Disposition.TcpDataEcho, source, destination,
                totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags, _lastDataReply);
        if (ack && acknowledgment != _tcpLocalNext)
            return TcpResult(LocalIpv4Disposition.InvalidTcpAcknowledgment, source, destination,
                totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);
        if (!payload.IsEmpty)
        {
            if (fin) return TcpResult(LocalIpv4Disposition.InvalidTcpState, source, destination,
                totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);
            if (sequence != _tcpPeerNext)
            {
                return TcpResult(LocalIpv4Disposition.InvalidTcpSequence, source, destination,
                    totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);
            }
            _lastPeerDataSequence = sequence;
            _lastPeerData = payload.ToArray();
            _tcpPeerNext += (uint)payload.Length;
            byte[] reply = BuildTcpReply(packet, sourcePort, _tcpLocalNext,
                _tcpPeerNext, TcpPsh | TcpAck, payload);
            _tcpLocalNext += (uint)payload.Length;
            _lastDataReply = reply;
            return TcpResult(LocalIpv4Disposition.TcpDataEcho, source, destination,
                totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags, reply);
        }
        if (fin)
        {
            if (sequence != _tcpPeerNext)
                return TcpResult(LocalIpv4Disposition.InvalidTcpSequence, source, destination,
                    totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);
            _tcpPeerNext++;
            _lastFinReply = BuildTcpReply(packet, sourcePort, _tcpLocalNext,
                _tcpPeerNext, TcpFin | TcpAck, ReadOnlySpan<byte>.Empty);
            _tcpLocalNext++;
            _tcpState = TcpState.LastAck;
            return TcpResult(LocalIpv4Disposition.TcpFinAck, source, destination,
                totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags, _lastFinReply);
        }
        if (ack && sequence == _tcpPeerNext)
            return TcpResult(LocalIpv4Disposition.TcpEstablished, source, destination,
                totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);
        return TcpResult(LocalIpv4Disposition.InvalidTcpSequence, source, destination,
            totalLength, tcp, sourcePort, destinationPort, sequence, acknowledgment, flags);
    }

    private byte[] BuildTcpReply(ReadOnlySpan<byte> request, ushort destinationPort,
        uint sequence, uint acknowledgment, byte flags, ReadOnlySpan<byte> payload)
    {
        byte[] tcp = new byte[20 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(tcp, TcpEchoPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(2), destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.AsSpan(8), acknowledgment);
        tcp[12] = 0x50;
        tcp[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(14), 4096);
        payload.CopyTo(tcp.AsSpan(20));
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(16),
            ComputeTransportChecksum(_addresses.Local, _addresses.Remote, TcpProtocol, tcp));
        return BuildIpv4Reply(request, TcpProtocol, tcp);
    }

    private byte[] BuildIpv4Reply(ReadOnlySpan<byte> request, byte protocol, ReadOnlySpan<byte> transport)
    {
        byte[] reply = new byte[20 + transport.Length];
        reply[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), (ushort)reply.Length);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4), BinaryPrimitives.ReadUInt16BigEndian(request.Slice(4, 2)));
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(6), 0x4000);
        reply[8] = 64;
        reply[9] = protocol;
        _addresses.Local.ToBytes().CopyTo(reply, 12);
        _addresses.Remote.ToBytes().CopyTo(reply, 16);
        transport.CopyTo(reply.AsSpan(20));
        SetIpv4Checksum(reply, 20);
        return reply;
    }

    internal static ushort ComputeTransportChecksum(Ipv4Address source, Ipv4Address destination,
        byte protocol, ReadOnlySpan<byte> segment)
    {
        byte[] pseudo = new byte[12 + segment.Length];
        source.ToBytes().CopyTo(pseudo, 0);
        destination.ToBytes().CopyTo(pseudo, 4);
        pseudo[9] = protocol;
        BinaryPrimitives.WriteUInt16BigEndian(pseudo.AsSpan(10), (ushort)segment.Length);
        segment.CopyTo(pseudo.AsSpan(12));
        return ComputeChecksum(pseudo);
    }

    internal static ushort ComputeChecksum(ReadOnlySpan<byte> bytes)
    {
        uint sum = 0;
        int offset = 0;
        while (offset + 1 < bytes.Length)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            offset += 2;
        }
        if (offset < bytes.Length) sum += (uint)bytes[offset] << 8;
        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    private static void SetIpv4Checksum(Span<byte> packet, int headerLength)
    {
        packet[10] = packet[11] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(10, 2), ComputeChecksum(packet[..headerLength]));
    }

    private void ResetTcp()
    {
        _tcpState = TcpState.Closed;
        _tcpPeerPort = 0;
        _tcpPeerInitial = _tcpPeerNext = _tcpLocalNext = _lastPeerDataSequence = 0;
        _lastPeerData = _lastDataReply = _lastFinReply = Array.Empty<byte>();
    }

    private static Ipv4Address ReadAddress(ReadOnlySpan<byte> bytes) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(bytes));

    private static LocalIpv4Result Result(LocalIpv4Disposition disposition,
        ReadOnlySpan<byte> packet = default, int totalLength = 0)
    {
        Ipv4Address source = packet.Length >= 16 ? ReadAddress(packet.Slice(12, 4)) : Ipv4Address.Zero;
        Ipv4Address destination = packet.Length >= 20 ? ReadAddress(packet.Slice(16, 4)) : Ipv4Address.Zero;
        byte protocol = packet.Length >= 10 ? packet[9] : (byte)0;
        return new(disposition, source, destination, protocol, totalLength);
    }

    private static LocalIpv4Result TransportResult(LocalIpv4Disposition disposition,
        Ipv4Address source, Ipv4Address destination, byte protocol, int totalLength,
        ReadOnlySpan<byte> segment, ushort sourcePort = 0, ushort destinationPort = 0) =>
        new(disposition, source, destination, protocol, totalLength,
            SourcePort: sourcePort, DestinationPort: destinationPort,
            PayloadLength: Math.Max(0, segment.Length - 8));

    private static LocalIpv4Result TcpResult(LocalIpv4Disposition disposition,
        Ipv4Address source, Ipv4Address destination, int totalLength, ReadOnlySpan<byte> segment,
        ushort sourcePort = 0, ushort destinationPort = 0, uint sequence = 0,
        uint acknowledgment = 0, byte flags = 0, ReadOnlyMemory<byte> reply = default)
    {
        int headerLength = segment.Length >= 13 ? (segment[12] >> 4) * 4 : segment.Length;
        int payloadLength = Math.Max(0, segment.Length - headerLength);
        return new(disposition, source, destination, TcpProtocol, totalLength,
            SourcePort: sourcePort, DestinationPort: destinationPort, TcpSequence: sequence,
            TcpAcknowledgment: acknowledgment, TcpFlags: flags, PayloadLength: payloadLength, Reply: reply);
    }
}
