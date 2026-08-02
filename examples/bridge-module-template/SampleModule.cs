using System.Text;
using PicoSwitchController;

namespace SampleModule;

public sealed class SampleNetworkPakModule : INetworkPakModule
{
    public string Id => "sample-module";
    public string DisplayName => "Sample Module";

    public NetworkPakResource? OpenResource(string resource, ModuleContext context)
    {
        if (!string.Equals(resource, "sample://hello", StringComparison.OrdinalIgnoreCase)) return null;
        context.Log("Sample module served sample://hello");
        return new NetworkPakResource(resource, Encoding.UTF8.GetBytes("Hello from the NICE64 sample module."));
    }

    public NetworkPakWriteResult HandleWrite(NetworkPakWriteChunk chunk, ModuleContext context) =>
        NetworkPakWriteResult.Ack([]);
}
