namespace PicoSwitchController;

using System.Text;

public sealed record GameBoyRomMetadata(
    string Title,
    byte CartType,
    string CartTypeName,
    string Mapper,
    int DecodedRomSize,
    int DecodedRamSize,
    bool RtcRequired)
{
    public byte FirmwareFlags => (byte)(RtcRequired ? 0x01 : 0x00);

    public GameBoyTransferProfile ToFirmwareProfile() =>
        new(CartType, DecodedRomSize, DecodedRamSize, FirmwareFlags);

    public string Summary =>
        $"{Title}: cart=0x{CartType:X2} {CartTypeName}, mapper={Mapper}, " +
        $"ROM={DecodedRomSize / 1024}KB, SRAM={DecodedRamSize / 1024}KB, " +
        $"RTC={(RtcRequired ? "yes" : "no")}";
}

public static class GameBoyRomMetadataParser
{
    public static GameBoyRomMetadata Parse(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        string title = GetTitle(rom);
        byte cartType = rom.Length > 0x0147 ? rom[0x0147] : (byte)0x00;
        byte romSizeCode = rom.Length > 0x0148 ? rom[0x0148] : (byte)0x00;
        byte ramSizeCode = rom.Length > 0x0149 ? rom[0x0149] : (byte)0x00;
        return new(title, cartType, GetCartTypeName(cartType), GetMapperName(cartType),
            DecodeRomSize(romSizeCode, rom.Length), DecodeRamSize(ramSizeCode),
            cartType is 0x0F or 0x10);
    }

    internal static int DecodeRomSize(byte code, int actualLength) => code switch
    {
        0x00 => 32 * 1024,
        0x01 => 64 * 1024,
        0x02 => 128 * 1024,
        0x03 => 256 * 1024,
        0x04 => 512 * 1024,
        0x05 => 1024 * 1024,
        0x06 => 2 * 1024 * 1024,
        0x07 => 4 * 1024 * 1024,
        0x08 => 8 * 1024 * 1024,
        0x52 => 1152 * 1024,
        0x53 => 1280 * 1024,
        0x54 => 1536 * 1024,
        _ => actualLength
    };

    internal static int DecodeRamSize(byte code) => code switch
    {
        0x01 => 2 * 1024,
        0x02 => 8 * 1024,
        0x03 => 32 * 1024,
        0x04 => 128 * 1024,
        0x05 => 64 * 1024,
        _ => 0
    };

    internal static string GetMapperName(byte cartType) => cartType switch
    {
        >= 0x01 and <= 0x03 => "MBC1",
        >= 0x0F and <= 0x13 => "MBC3",
        >= 0x19 and <= 0x1E => "MBC5",
        _ => "ROM"
    };

    internal static string GetCartTypeName(byte cartType) => cartType switch
    {
        0x00 => "ROM ONLY",
        0x01 => "MBC1",
        0x02 => "MBC1+RAM",
        0x03 => "MBC1+RAM+BATTERY",
        0x0F => "MBC3+TIMER+BATTERY",
        0x10 => "MBC3+TIMER+RAM+BATTERY",
        0x11 => "MBC3",
        0x12 => "MBC3+RAM",
        0x13 => "MBC3+RAM+BATTERY",
        0x19 => "MBC5",
        0x1A => "MBC5+RAM",
        0x1B => "MBC5+RAM+BATTERY",
        0x1C => "MBC5+RUMBLE",
        0x1D => "MBC5+RUMBLE+RAM",
        0x1E => "MBC5+RUMBLE+RAM+BATTERY",
        _ => "Unknown"
    };

    private static string GetTitle(byte[] rom)
    {
        int length = Math.Min(16, rom.Length - 0x0134);
        if (length <= 0) return "GB ROM";
        int end = 0;
        while (end < length && rom[0x0134 + end] != 0) end++;
        string title = Encoding.ASCII.GetString(rom, 0x0134, end).Trim();
        return string.IsNullOrWhiteSpace(title) ? "GB ROM" : title;
    }
}
