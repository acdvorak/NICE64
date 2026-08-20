namespace PicoSwitchController;

public sealed class SpeedTestModule : INetworkPakModule
{
    private const int StreamChunkSize = 16;
    private readonly ModulePackage _package;

    private static readonly IReadOnlyDictionary<string, int> ResourceSizes =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["speed://1k"] = 1024,
            ["speed://4k"] = 4096,
            ["speed://16k"] = 16 * 1024,
            ["speed://64k"] = 64 * 1024,
            ["speed://256k"] = 256 * 1024,
            ["speed://1m"] = 1024 * 1024
        };

    public SpeedTestModule(ModulePackage package)
    {
        _package = package;
    }

    public string Id => _package.Manifest.Id;
    public string DisplayName => _package.Manifest.DisplayName;

    public NetworkPakResource? OpenResource(string resource, ModuleContext context)
    {
        if (!ResourceSizes.TryGetValue(resource, out int length))
        {
            return null;
        }

        byte[] data = MakePatternResource(length);
        uint crc32 = ComputeCrc32(data);
        int chunks = (length + StreamChunkSize - 1) / StreamChunkSize;
        context.Log($"Speed test resource opened: {resource} bytes={length} chunks={chunks} expectedCrc32={crc32:X8}");
        return new NetworkPakResource(resource, data);
    }

    public NetworkPakWriteResult HandleWrite(NetworkPakWriteChunk chunk, ModuleContext context)
    {
        context.Log($"Speed test write ignored flags={chunk.Flags:X2} bytes={chunk.Data.Length}");
        return NetworkPakWriteResult.Ack(new byte[] { 1 });
    }

    public static byte PatternByte(uint index)
    {
        return (byte)((index * 251u + 7u) & 0xFFu);
    }

    public static byte[] MakePatternResource(int length)
    {
        byte[] data = new byte[length];
        for (uint i = 0; i < data.Length; i++)
        {
            data[i] = PatternByte(i);
        }

        return data;
    }

    public static uint ComputeCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1u));
            }
        }

        return ~crc;
    }
}
