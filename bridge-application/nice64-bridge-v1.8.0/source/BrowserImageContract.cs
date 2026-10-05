namespace PicoSwitchController;

public enum BrowserImageFitMode
{
    Fit,
    Fill
}

public readonly record struct BrowserImageGeometry(ushort Width, ushort Height, int ScaledWidth, int ScaledHeight, int CropX, int CropY);

public static class BrowserImageContract
{
    public const ushort MaxWidth = 160;
    public const ushort MaxHeight = 96;
    public const ushort NimgVersion = 1;
    public const ushort Rgb5A1Format = 1;
    public const int NimgHeaderSize = 16;
    public const int BytesPerPixel = 2;

    public static (ushort Width, ushort Height) CalculateDimensions(int sourceWidth, int sourceHeight)
    {
        BrowserImageGeometry geometry = CalculateGeometry(sourceWidth, sourceHeight, BrowserImageFitMode.Fit);
        return (geometry.Width, geometry.Height);
    }

    public static BrowserImageGeometry CalculateGeometry(int sourceWidth, int sourceHeight, BrowserImageFitMode mode)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentOutOfRangeException();
        if (mode == BrowserImageFitMode.Fit)
        {
            double scale = Math.Min(1.0, Math.Min(MaxWidth / (double)sourceWidth, MaxHeight / (double)sourceHeight));
            ushort width = (ushort)Math.Clamp((int)Math.Round(sourceWidth * scale, MidpointRounding.AwayFromZero), 1, MaxWidth);
            ushort height = (ushort)Math.Clamp((int)Math.Round(sourceHeight * scale, MidpointRounding.AwayFromZero), 1, MaxHeight);
            return new BrowserImageGeometry(width, height, width, height, 0, 0);
        }

        double fillScale = Math.Max(MaxWidth / (double)sourceWidth, MaxHeight / (double)sourceHeight);
        int scaledWidth = Math.Max(MaxWidth, (int)Math.Round(sourceWidth * fillScale, MidpointRounding.AwayFromZero));
        int scaledHeight = Math.Max(MaxHeight, (int)Math.Round(sourceHeight * fillScale, MidpointRounding.AwayFromZero));
        return new BrowserImageGeometry(MaxWidth, MaxHeight, scaledWidth, scaledHeight,
            Math.Max(0, (scaledWidth - MaxWidth) / 2), Math.Max(0, (scaledHeight - MaxHeight) / 2));
    }

    public static bool TryReadNimg(string filePath, out ushort width, out ushort height, out string reason)
    {
        width = 0;
        height = 0;
        reason = "INVALID_NIMG";
        try
        {
            FileInfo file = new(filePath);
            if (!file.Exists || file.Length < NimgHeaderSize)
            {
                reason = "NIMG_TOO_SHORT";
                return false;
            }

            byte[] header = new byte[NimgHeaderSize];
            using FileStream stream = File.OpenRead(filePath);
            if (stream.Read(header, 0, header.Length) != header.Length ||
                header[0] != (byte)'N' || header[1] != (byte)'I' || header[2] != (byte)'M' || header[3] != (byte)'G')
            {
                reason = "BAD_NIMG_MAGIC";
                return false;
            }

            ushort version = ReadBigEndian16(header, 4);
            width = ReadBigEndian16(header, 6);
            height = ReadBigEndian16(header, 8);
            ushort format = ReadBigEndian16(header, 10);
            uint payloadLength = ReadBigEndian32(header, 12);
            uint expectedPayload = (uint)width * height * BytesPerPixel;
            if (version != NimgVersion || format != Rgb5A1Format || width == 0 || height == 0 ||
                width > MaxWidth || height > MaxHeight || payloadLength != expectedPayload ||
                file.Length != NimgHeaderSize + payloadLength)
            {
                reason = "NIMG_CONTRACT_MISMATCH";
                return false;
            }

            reason = "OK";
            return true;
        }
        catch
        {
            reason = "NIMG_READ_FAILED";
            return false;
        }
    }

    private static ushort ReadBigEndian16(byte[] data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);
    private static uint ReadBigEndian32(byte[] data, int offset) => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
