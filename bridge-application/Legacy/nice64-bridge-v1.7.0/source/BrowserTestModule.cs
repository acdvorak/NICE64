namespace PicoSwitchController;

public sealed class BrowserTestModule : INetworkPakModule
{
    private const string TestResource = "browser://test";
    private const int StreamChunkSize = 16;
    private readonly ModulePackage _package;

    public BrowserTestModule(ModulePackage package)
    {
        _package = package;
    }

    public string Id => _package.Manifest.Id;
    public string DisplayName => _package.Manifest.DisplayName;

    public NetworkPakResource? OpenResource(string resource, ModuleContext context)
    {
        context.Log("Browser Test request received");
        context.Log($"Resource: {resource}");
        if (!string.Equals(resource, TestResource, StringComparison.OrdinalIgnoreCase))
        {
            context.Log("Browser resource not found");
            return null;
        }

        byte[] data;
        try
        {
            data = BuildTestDocument();
        }
        catch (Exception ex)
        {
            context.Log($"Browser Test payload generation failed: {ex.Message}");
            return null;
        }

        int chunks = (data.Length + StreamChunkSize - 1) / StreamChunkSize;
        context.Log($"Payload bytes: {data.Length}");
        context.Log($"Browser test resource opened: {resource} bytes={data.Length} chunks={chunks} wireVersion={NiceWireDocument.Version}");
        context.Log("Request completed");
        return new NetworkPakResource(resource, data);
    }

    public NetworkPakWriteResult HandleWrite(NetworkPakWriteChunk chunk, ModuleContext context)
    {
        context.Log($"Browser test write ignored flags={chunk.Flags:X2} bytes={chunk.Data.Length}");
        return NetworkPakWriteResult.Ack(new byte[] { 1 });
    }

    public static byte[] BuildTestDocument()
    {
        NiceWireBlock[] blocks =
        [
            new(0, "NICE64 Browser", null, 0, 0),
            new(1, "Bridge Delivered NICE Document", null, 0, 0),
            new(3, "This page was delivered through the existing Network Pak stream protocol and decoded into the v0.9D document loader.", null, 0, 0),
            new(8, null, null, 0, 0),
            new(2, "Renderer Coverage", null, 0, 0),
            new(4, "Title, H1, H2, paragraphs, lists, quotes, links, image slots, and rules.", null, 0, 0),
            new(5, "The ROM renders a NICE document. The Bridge prepares the document. The transport just moves bytes.", null, 0, 0),
            new(6, "Open NICE home placeholder", "nice://home", 0, 0),
            new(6, "Open cached article placeholder", "nice://cache/article-001", 0, 0),
            new(7, "Bridge supplied image placeholder", "image://bridge-test", 240, 64),
            new(3, "No HTML, CSS, JavaScript, HTTP, or image decoding is present in the ROM for this milestone.", null, 0, 0),
            new(6, "End of test document", "nice://end", 0, 0),
            new(9, null, null, 0, 0)
        ];

        return NiceWireDocument.Build(0x0000090Du, blocks);
    }

    public static bool IsTestResource(string resource)
    {
        return string.Equals(resource, TestResource, StringComparison.OrdinalIgnoreCase);
    }
}
