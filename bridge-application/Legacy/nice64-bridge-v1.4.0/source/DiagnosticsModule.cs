namespace PicoSwitchController;

public sealed class DiagnosticsModule : INetworkPakModule
{
    private readonly ModulePackage _package;

    public DiagnosticsModule(ModulePackage package)
    {
        _package = package;
    }

    public string Id => _package.Manifest.Id;
    public string DisplayName => _package.Manifest.DisplayName;

    public NetworkPakResource? OpenResource(string resource, ModuleContext context)
    {
        byte[]? data = resource switch
        {
            "diag://menu" => _package.TryReadContentBytes("menu.txt"),
            "diag://status" => _package.TryReadContentBytes("status.txt"),
            "diag://test1k" => MakePatternResource(1024),
            _ => null
        };

        return data is null ? null : new NetworkPakResource(resource, data);
    }

    public NetworkPakWriteResult HandleWrite(NetworkPakWriteChunk chunk, ModuleContext context)
    {
        context.Log($"Diagnostics write ignored flags={chunk.Flags:X2} bytes={chunk.Data.Length}");
        return NetworkPakWriteResult.Ack(new byte[] { 1 });
    }

    private static byte[] MakePatternResource(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 17 + 5) & 0xFF);
        return data;
    }
}
