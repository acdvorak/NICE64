namespace PicoSwitchController;

// Reference-only SDK example. This file is package content and is excluded
// from the production Bridge build by PicoSwitchController.csproj.
public sealed class ExampleModule : INetworkPakModule
{
    private readonly ModulePackage _package;

    public ExampleModule(ModulePackage package)
    {
        _package = package;
    }

    public string Id => _package.Manifest.Id;
    public string DisplayName => _package.Manifest.DisplayName;

    public NetworkPakResource? OpenResource(string resource, ModuleContext context)
    {
        if (resource != "example://hello")
        {
            return null;
        }

        byte[]? data = _package.TryReadContentBytes("hello.txt");
        return data is null ? null : new NetworkPakResource(resource, data);
    }

    public NetworkPakWriteResult HandleWrite(NetworkPakWriteChunk chunk, ModuleContext context)
    {
        return NetworkPakWriteResult.Ack(new byte[] { 1 });
    }
}
