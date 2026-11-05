namespace PicoSwitchController;

/// <summary>
/// Encodes canonical, uncompressed PPP frames into asynchronous HDLC wire
/// framing. LCP may provide a negotiated transmit ACCM in a later gate.
/// </summary>
public sealed class PppFrameEncoder
{
    public const byte Flag = 0x7E;
    public const byte ControlEscape = 0x7D;
    public const byte EscapeXor = 0x20;
    public const byte Address = 0xFF;
    public const byte Control = 0x03;

    public PppFrameEncoder(uint txAsyncControlCharacterMap = uint.MaxValue)
    {
        TxAsyncControlCharacterMap = txAsyncControlCharacterMap;
    }

    /// <summary>
    /// Bits 0 through 31 select control bytes that must be escaped on output.
    /// Flag and Control Escape are always escaped independently of this map.
    /// </summary>
    public uint TxAsyncControlCharacterMap { get; }

    public byte[] Encode(PppFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateCanonicalProtocol(frame.Protocol);

        ReadOnlySpan<byte> information = frame.Information.Span;
        byte[] content = new byte[checked(4 + information.Length + 2)];
        content[0] = Address;
        content[1] = Control;
        content[2] = (byte)(frame.Protocol >> 8);
        content[3] = (byte)frame.Protocol;
        information.CopyTo(content.AsSpan(4));

        ushort fcs = PppFcs16.ComputeFinal(content.AsSpan(0, content.Length - 2));
        content[^2] = (byte)fcs;
        content[^1] = (byte)(fcs >> 8);

        int wireLength = 2;
        foreach (byte value in content)
            wireLength += MustEscape(value) ? 2 : 1;

        byte[] wire = new byte[wireLength];
        int output = 0;
        wire[output++] = Flag;
        foreach (byte value in content)
        {
            if (MustEscape(value))
            {
                wire[output++] = ControlEscape;
                wire[output++] = (byte)(value ^ EscapeXor);
            }
            else
            {
                wire[output++] = value;
            }
        }
        wire[output] = Flag;
        return wire;
    }

    private bool MustEscape(byte value)
    {
        if (value is Flag or ControlEscape) return true;
        return value < 0x20 &&
            (TxAsyncControlCharacterMap & (1U << value)) != 0;
    }

    private static void ValidateCanonicalProtocol(ushort protocol)
    {
        byte high = (byte)(protocol >> 8);
        byte low = (byte)protocol;
        if ((high & 1) != 0 || (low & 1) == 0)
            throw new ArgumentOutOfRangeException(nameof(protocol), protocol,
                "PPP protocol must use the canonical two-byte encoding.");
    }
}
