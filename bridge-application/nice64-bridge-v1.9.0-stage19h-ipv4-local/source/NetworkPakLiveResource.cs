namespace PicoSwitchController;

public enum NetworkPakLiveReadStatus
{
    Data,
    WouldBlock,
    Eof,
    Error,
}

public sealed record NetworkPakLiveReadResult(
    NetworkPakLiveReadStatus Status,
    byte[] Data,
    string? Error = null)
{
    public static NetworkPakLiveReadResult Available(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
            throw new ArgumentException("A DATA read must contain bytes.", nameof(data));
        return new(NetworkPakLiveReadStatus.Data, data);
    }

    public static NetworkPakLiveReadResult WouldBlock() =>
        new(NetworkPakLiveReadStatus.WouldBlock, Array.Empty<byte>());

    public static NetworkPakLiveReadResult Eof() =>
        new(NetworkPakLiveReadStatus.Eof, Array.Empty<byte>());

    public static NetworkPakLiveReadResult Failure(string error) =>
        new(NetworkPakLiveReadStatus.Error, Array.Empty<byte>(), error);
}

/// <summary>
/// Generic persistent Network Pak resource. Bridge Core knows only these
/// stream semantics; all protocol behavior remains in the owning module.
/// </summary>
public interface INetworkPakLiveResource : IDisposable
{
    bool IsClosed { get; }
    NetworkPakLiveReadResult Read(int maximumBytes);
    NetworkPakWriteResult Write(NetworkPakWriteChunk chunk, ModuleContext context);
    void Close(string reason);
}
