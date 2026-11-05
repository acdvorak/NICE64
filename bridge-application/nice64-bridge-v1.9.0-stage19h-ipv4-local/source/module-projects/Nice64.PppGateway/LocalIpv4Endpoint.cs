using System.Buffers.Binary;

namespace PicoSwitchController;

internal enum LocalIpv4Disposition
{
    EchoReply,
    TooShort,
    NotIpv4,
    InvalidHeaderLength,
    InvalidTotalLength,
    InvalidHeaderChecksum,
    Fragmented,
    WrongSource,
    WrongDestination,
    UnsupportedProtocol,
    InvalidIcmpLength,
    InvalidIcmpChecksum,
    UnsupportedIcmp,
}

internal sealed record LocalIpv4Result(
    LocalIpv4Disposition Disposition,
    Ipv4Address Source,
    Ipv4Address Destination,
    byte Protocol,
    int TotalLength,
    ushort IcmpIdentifier = 0,
    ushort IcmpSequence = 0,
    ReadOnlyMemory<byte> Reply = default)
{
    public bool Replied => Disposition == LocalIpv4Disposition.EchoReply;
}

/// <summary>
/// Minimal IPCP-owned local endpoint. It answers only unfragmented ICMP Echo
/// Requests from the negotiated peer to the configured Gateway address.
/// </summary>
internal sealed class LocalIpv4Endpoint
{
    private const int MinimumIpv4HeaderLength = 20;
    private const int MinimumIcmpEchoLength = 8;
    private const byte IcmpProtocol = 1;
    private const byte IcmpEchoRequest = 8;
    private const byte IcmpEchoReply = 0;

    private readonly PppAddressAssignment _addresses;

    public LocalIpv4Endpoint(PppAddressAssignment addresses)
    {
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
    }

    public LocalIpv4Result Handle(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < MinimumIpv4HeaderLength)
            return Result(LocalIpv4Disposition.TooShort);

        int version = packet[0] >> 4;
        if (version != 4)
            return Result(LocalIpv4Disposition.NotIpv4);

        int headerLength = (packet[0] & 0x0F) * 4;
        if (headerLength < MinimumIpv4HeaderLength || headerLength > packet.Length)
            return Result(LocalIpv4Disposition.InvalidHeaderLength);

        int totalLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        if (totalLength < headerLength + MinimumIcmpEchoLength || totalLength != packet.Length)
            return Result(LocalIpv4Disposition.InvalidTotalLength, packet, totalLength);

        if (ComputeChecksum(packet[..headerLength]) != 0)
            return Result(LocalIpv4Disposition.InvalidHeaderChecksum, packet, totalLength);

        ushort fragment = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
        if ((fragment & 0x3FFF) != 0)
            return Result(LocalIpv4Disposition.Fragmented, packet, totalLength);

        Ipv4Address source = ReadAddress(packet.Slice(12, 4));
        Ipv4Address destination = ReadAddress(packet.Slice(16, 4));
        byte protocol = packet[9];

        if (source != _addresses.Remote)
            return new(LocalIpv4Disposition.WrongSource, source, destination, protocol, totalLength);
        if (destination != _addresses.Local)
            return new(LocalIpv4Disposition.WrongDestination, source, destination, protocol, totalLength);
        if (protocol != IcmpProtocol)
            return new(LocalIpv4Disposition.UnsupportedProtocol, source, destination, protocol, totalLength);

        ReadOnlySpan<byte> icmp = packet.Slice(headerLength, totalLength - headerLength);
        if (icmp.Length < MinimumIcmpEchoLength)
            return new(LocalIpv4Disposition.InvalidIcmpLength, source, destination, protocol, totalLength);
        if (ComputeChecksum(icmp) != 0)
            return new(LocalIpv4Disposition.InvalidIcmpChecksum, source, destination, protocol, totalLength);
        if (icmp[0] != IcmpEchoRequest || icmp[1] != 0)
            return new(LocalIpv4Disposition.UnsupportedIcmp, source, destination, protocol, totalLength);

        ushort identifier = BinaryPrimitives.ReadUInt16BigEndian(icmp.Slice(4, 2));
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(icmp.Slice(6, 2));
        byte[] reply = packet[..totalLength].ToArray();

        reply[8] = 64;
        _addresses.Local.ToBytes().CopyTo(reply, 12);
        _addresses.Remote.ToBytes().CopyTo(reply, 16);
        reply[10] = 0;
        reply[11] = 0;
        ushort headerChecksum = ComputeChecksum(reply.AsSpan(0, headerLength));
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(10, 2), headerChecksum);

        Span<byte> replyIcmp = reply.AsSpan(headerLength, totalLength - headerLength);
        replyIcmp[0] = IcmpEchoReply;
        replyIcmp[2] = 0;
        replyIcmp[3] = 0;
        ushort icmpChecksum = ComputeChecksum(replyIcmp);
        BinaryPrimitives.WriteUInt16BigEndian(replyIcmp.Slice(2, 2), icmpChecksum);

        return new(
            LocalIpv4Disposition.EchoReply,
            source,
            destination,
            protocol,
            totalLength,
            identifier,
            sequence,
            reply);
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
        if (offset < bytes.Length)
            sum += (uint)bytes[offset] << 8;

        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    private static Ipv4Address ReadAddress(ReadOnlySpan<byte> bytes) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(bytes));

    private static LocalIpv4Result Result(
        LocalIpv4Disposition disposition,
        ReadOnlySpan<byte> packet = default,
        int totalLength = 0)
    {
        Ipv4Address source = packet.Length >= 16 ? ReadAddress(packet.Slice(12, 4)) : Ipv4Address.Zero;
        Ipv4Address destination = packet.Length >= 20 ? ReadAddress(packet.Slice(16, 4)) : Ipv4Address.Zero;
        byte protocol = packet.Length >= 10 ? packet[9] : (byte)0;
        return new(disposition, source, destination, protocol, totalLength);
    }
}
