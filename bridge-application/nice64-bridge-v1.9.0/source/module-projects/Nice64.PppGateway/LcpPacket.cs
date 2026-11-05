using System.Buffers.Binary;

namespace PicoSwitchController;

internal static class LcpConstants
{
    public const ushort Protocol = 0xC021;

    public const byte ConfigureRequest = 1;
    public const byte ConfigureAck = 2;
    public const byte ConfigureNak = 3;
    public const byte ConfigureReject = 4;
    public const byte TerminateRequest = 5;
    public const byte TerminateAck = 6;
    public const byte CodeReject = 7;
    public const byte ProtocolReject = 8;
    public const byte EchoRequest = 9;
    public const byte EchoReply = 10;

    public const byte OptionMru = 1;
    public const byte OptionAccm = 2;
    public const byte OptionAuthentication = 3;
    public const byte OptionMagicNumber = 5;
    public const byte OptionProtocolFieldCompression = 7;
    public const byte OptionAddressControlFieldCompression = 8;
}

/// <summary>A validated LCP packet, excluding the PPP protocol field.</summary>
internal sealed record LcpPacket(byte Code, byte Identifier, ReadOnlyMemory<byte> Data)
{
    public byte[] ToInformation()
    {
        if (Data.Length > ushort.MaxValue - 4)
        {
            throw new InvalidOperationException("LCP packet is too large.");
        }

        var output = new byte[4 + Data.Length];
        output[0] = Code;
        output[1] = Identifier;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2, 2), (ushort)output.Length);
        Data.Span.CopyTo(output.AsSpan(4));
        return output;
    }

    public PppFrame ToPppFrame() => new(LcpConstants.Protocol, ToInformation());

    public static bool TryParse(PppFrame frame, out LcpPacket? packet, out string? error)
    {
        packet = null;
        error = null;

        if (frame.Protocol != LcpConstants.Protocol)
        {
            error = "NOT_LCP";
            return false;
        }

        ReadOnlySpan<byte> input = frame.Information.Span;
        if (input.Length < 4)
        {
            error = "LCP_TOO_SHORT";
            return false;
        }

        int declaredLength = BinaryPrimitives.ReadUInt16BigEndian(input.Slice(2, 2));
        if (declaredLength < 4)
        {
            error = "LCP_LENGTH_BELOW_HEADER";
            return false;
        }

        if (declaredLength > input.Length)
        {
            error = "LCP_TRUNCATED";
            return false;
        }

        // RFC 1661 permits padding after the declared LCP packet length.
        packet = new LcpPacket(input[0], input[1], input.Slice(4, declaredLength - 4).ToArray());
        return true;
    }
}

/// <summary>An LCP option retaining its exact wire bytes for Ack/Reject fidelity.</summary>
internal sealed record LcpOption(byte Type, ReadOnlyMemory<byte> Raw)
{
    public ReadOnlySpan<byte> Value => Raw.Span.Slice(2);
}

internal static class LcpOptionCodec
{
    public static bool TryParse(
        ReadOnlySpan<byte> data,
        out IReadOnlyList<LcpOption> options,
        out string? error)
    {
        var parsed = new List<LcpOption>();
        int offset = 0;

        while (offset < data.Length)
        {
            if (data.Length - offset < 2)
            {
                options = Array.Empty<LcpOption>();
                error = "LCP_OPTION_TRUNCATED_HEADER";
                return false;
            }

            int length = data[offset + 1];
            if (length < 2)
            {
                options = Array.Empty<LcpOption>();
                error = "LCP_OPTION_LENGTH_BELOW_HEADER";
                return false;
            }

            if (length > data.Length - offset)
            {
                options = Array.Empty<LcpOption>();
                error = "LCP_OPTION_TRUNCATED";
                return false;
            }

            byte[] raw = data.Slice(offset, length).ToArray();
            parsed.Add(new LcpOption(raw[0], raw));
            offset += length;
        }

        options = parsed;
        error = null;
        return true;
    }

    public static byte[] Concatenate(IEnumerable<LcpOption> options)
    {
        LcpOption[] materialized = options.ToArray();
        int length = materialized.Sum(option => option.Raw.Length);
        var output = new byte[length];
        int offset = 0;
        foreach (LcpOption option in materialized)
        {
            option.Raw.Span.CopyTo(output.AsSpan(offset));
            offset += option.Raw.Length;
        }

        return output;
    }

    public static byte[] Mru(ushort value)
    {
        var option = new byte[4] { LcpConstants.OptionMru, 4, 0, 0 };
        BinaryPrimitives.WriteUInt16BigEndian(option.AsSpan(2), value);
        return option;
    }

    public static byte[] Accm(uint value)
    {
        var option = new byte[6] { LcpConstants.OptionAccm, 6, 0, 0, 0, 0 };
        BinaryPrimitives.WriteUInt32BigEndian(option.AsSpan(2), value);
        return option;
    }

    public static byte[] Magic(uint value)
    {
        var option = new byte[6] { LcpConstants.OptionMagicNumber, 6, 0, 0, 0, 0 };
        BinaryPrimitives.WriteUInt32BigEndian(option.AsSpan(2), value);
        return option;
    }
}
