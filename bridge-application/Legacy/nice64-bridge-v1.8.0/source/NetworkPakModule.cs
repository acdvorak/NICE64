using System.Text;

namespace PicoSwitchController;

/// <summary>
/// Defines the internal Bridge SDK contract for a Network Pak module.
/// Built-in modules implement this interface today; future DLL-loaded modules should use the same shape.
/// </summary>
public interface INetworkPakModule
{
    /// <summary>
    /// Stable module identifier used by settings, manifests, and module selection.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Human-readable module name shown in Bridge UI and logs.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Opens a named module resource and returns the complete byte payload for Bridge-owned stream serving.
    /// Return null when the module does not own or cannot resolve the resource.
    /// </summary>
    NetworkPakResource? OpenResource(string resource, ModuleContext context);

    /// <summary>
    /// Handles raw WRITE_STREAM_CHUNK payload bytes routed by the Bridge Core.
    /// Modules decide whether those bytes are text, binary input, save data, asset requests, or another format.
    /// </summary>
    NetworkPakWriteResult HandleWrite(NetworkPakWriteChunk chunk, ModuleContext context);
}

/// <summary>
/// Complete resource payload returned by a module. The Bridge Core owns stream IDs, CRC, offsets, and chunking after this point.
/// </summary>
public sealed record NetworkPakResource(
    string Name,
    byte[] Data,
    Action<int>? CommitRead = null);

/// <summary>
/// Raw data from one WRITE_STREAM_CHUNK request. The Bridge preserves protocol flags and bytes; modules interpret meaning.
/// </summary>
public sealed record NetworkPakWriteChunk(byte Flags, byte[] Data)
{
    /// <summary>
    /// True when bit 0 of the write flags marks the first chunk in a module-defined write sequence.
    /// </summary>
    public bool IsFirst => (Flags & 0x01) != 0;

    /// <summary>
    /// True when bit 1 of the write flags marks the final chunk in a module-defined write sequence.
    /// </summary>
    public bool IsLast => (Flags & 0x02) != 0;

    /// <summary>
    /// Convenience helper for text-oriented modules. Binary modules should read Data directly.
    /// </summary>
    public string GetUtf8Text() => Encoding.UTF8.GetString(Data);
}

/// <summary>
/// Result returned after a module handles a write. Successful writes should preserve the current A4|01 ACK behavior.
/// </summary>
public sealed record NetworkPakWriteResult(bool Success, byte[] Payload, string? Error)
{
    /// <summary>
    /// Creates a successful write result with the payload the Bridge returns in STREAM_WRITE_ACK.
    /// </summary>
    public static NetworkPakWriteResult Ack(byte[] payload) => new(true, payload, null);

    /// <summary>
    /// Creates a failed write result. The Bridge converts Error into the existing protocol error response.
    /// </summary>
    public static NetworkPakWriteResult Fail(string error) => new(false, Encoding.ASCII.GetBytes(error), error);
}

/// <summary>
/// Runtime services and constants passed from the Bridge Core to module code.
/// This avoids modules reaching into Bridge internals or assuming repository layout.
/// </summary>
public sealed record ModuleContext(
    string BaseDirectory,
    Action<string> Log,
    string BridgeVersion,
    int MaxPayloadLength,
    int MaxGeneratedPageBytes,
    string DefaultMessage)
{
    public IBridgeDeviceStateProvider DeviceState { get; init; } =
        BridgeDeviceStateProviders.Disconnected;
    public IBridgeCapabilityProvider Capabilities { get; init; } =
        BridgeDeviceStateProviders.Disconnected;
}
