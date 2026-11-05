namespace PicoSwitchController;

/// <summary>
/// PPP FCS-16 (CRC-16/X-25) using the reflected 0x1021 polynomial.
/// </summary>
public static class PppFcs16
{
    public const ushort Initial = 0xFFFF;
    public const ushort GoodResidue = 0xF0B8;
    private const ushort ReflectedPolynomial = 0x8408;

    public static ushort Update(ushort fcs, byte value)
    {
        fcs ^= value;
        for (int bit = 0; bit < 8; ++bit)
        {
            fcs = (ushort)((fcs >> 1) ^
                ((fcs & 1) != 0 ? ReflectedPolynomial : 0));
        }
        return fcs;
    }

    public static ushort ComputeFinal(ReadOnlySpan<byte> bytes)
    {
        ushort fcs = Initial;
        foreach (byte value in bytes) fcs = Update(fcs, value);
        return (ushort)~fcs;
    }

    public static bool HasGoodResidue(ReadOnlySpan<byte> bytes)
    {
        ushort fcs = Initial;
        foreach (byte value in bytes) fcs = Update(fcs, value);
        return fcs == GoodResidue;
    }
}
