using System.Buffers.Binary;

namespace PicoSwitchController;

internal static class IpcpConstants
{
    public const ushort Protocol = 0x8021;

    public const byte ConfigureRequest = 1;
    public const byte ConfigureAck = 2;
    public const byte ConfigureNak = 3;
    public const byte ConfigureReject = 4;
    public const byte TerminateRequest = 5;
    public const byte TerminateAck = 6;
    public const byte CodeReject = 7;

    public const byte OptionIpAddresses = 1;
    public const byte OptionIpCompression = 2;
    public const byte OptionIpAddress = 3;
    public const byte OptionPrimaryDns = 129;
    public const byte OptionPrimaryNbns = 130;
    public const byte OptionSecondaryDns = 131;
    public const byte OptionSecondaryNbns = 132;
}

/// <summary>An IPv4 address stored as its canonical 32-bit network value.</summary>
internal readonly record struct Ipv4Address(uint Value)
{
    public static Ipv4Address Zero => new(0);

    public static Ipv4Address FromOctets(byte a, byte b, byte c, byte d) =>
        new(((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d);

    public byte[] ToBytes()
    {
        var output = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(output, Value);
        return output;
    }

    public override string ToString() =>
        $"{(Value >> 24) & 0xFF}.{(Value >> 16) & 0xFF}.{(Value >> 8) & 0xFF}.{Value & 0xFF}";
}

internal sealed record PppAddressAssignment(Ipv4Address Local, Ipv4Address Remote);

/// <summary>A validated IPCP packet, excluding the PPP protocol field.</summary>
internal sealed record IpcpPacket(byte Code, byte Identifier, ReadOnlyMemory<byte> Data)
{
    public byte[] ToInformation()
    {
        if (Data.Length > ushort.MaxValue - 4)
        {
            throw new InvalidOperationException("IPCP packet is too large.");
        }

        var output = new byte[4 + Data.Length];
        output[0] = Code;
        output[1] = Identifier;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2, 2), (ushort)output.Length);
        Data.Span.CopyTo(output.AsSpan(4));
        return output;
    }

    public PppFrame ToPppFrame() => new(IpcpConstants.Protocol, ToInformation());

    public static bool TryParse(PppFrame frame, out IpcpPacket? packet, out string? error)
    {
        packet = null;
        error = null;

        if (frame.Protocol != IpcpConstants.Protocol)
        {
            error = "NOT_IPCP";
            return false;
        }

        ReadOnlySpan<byte> input = frame.Information.Span;
        if (input.Length < 4)
        {
            error = "IPCP_TOO_SHORT";
            return false;
        }

        int declaredLength = BinaryPrimitives.ReadUInt16BigEndian(input.Slice(2, 2));
        if (declaredLength < 4)
        {
            error = "IPCP_LENGTH_BELOW_HEADER";
            return false;
        }

        if (declaredLength > input.Length)
        {
            error = "IPCP_TRUNCATED";
            return false;
        }

        // Match Gate B: bytes after the declared control-packet length are padding.
        packet = new IpcpPacket(input[0], input[1], input.Slice(4, declaredLength - 4).ToArray());
        return true;
    }
}

internal sealed record IpcpOption(byte Type, ReadOnlyMemory<byte> Raw)
{
    public ReadOnlySpan<byte> Value => Raw.Span.Slice(2);
}

internal static class IpcpOptionCodec
{
    public static bool TryParse(
        ReadOnlySpan<byte> data,
        out IReadOnlyList<IpcpOption> options,
        out string? error)
    {
        var parsed = new List<IpcpOption>();
        int offset = 0;

        while (offset < data.Length)
        {
            if (data.Length - offset < 2)
            {
                options = Array.Empty<IpcpOption>();
                error = "IPCP_OPTION_TRUNCATED_HEADER";
                return false;
            }

            int length = data[offset + 1];
            if (length < 2)
            {
                options = Array.Empty<IpcpOption>();
                error = "IPCP_OPTION_LENGTH_BELOW_HEADER";
                return false;
            }

            if (length > data.Length - offset)
            {
                options = Array.Empty<IpcpOption>();
                error = "IPCP_OPTION_TRUNCATED";
                return false;
            }

            byte[] raw = data.Slice(offset, length).ToArray();
            parsed.Add(new IpcpOption(raw[0], raw));
            offset += length;
        }

        options = parsed;
        error = null;
        return true;
    }

    public static byte[] Concatenate(IEnumerable<IpcpOption> options)
    {
        IpcpOption[] materialized = options.ToArray();
        var output = new byte[materialized.Sum(option => option.Raw.Length)];
        int offset = 0;
        foreach (IpcpOption option in materialized)
        {
            option.Raw.Span.CopyTo(output.AsSpan(offset));
            offset += option.Raw.Length;
        }
        return output;
    }

    public static byte[] IpAddress(Ipv4Address address)
    {
        var output = new byte[6] { IpcpConstants.OptionIpAddress, 6, 0, 0, 0, 0 };
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(2), address.Value);
        return output;
    }

    public static Ipv4Address ReadAddress(IpcpOption option) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(option.Value));
}
