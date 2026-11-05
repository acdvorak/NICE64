namespace PicoSwitchController;

internal enum DirectConnectionHandshakeState
{
    WaitingForClient,
    ResponsePending,
    Complete,
    Closed,
}

/// <summary>
/// Bounded per-session recognizer for the stock CE 2.11 direct-serial
/// CLIENT/CLIENTSERVER preamble. It owns no transport behavior.
/// </summary>
internal sealed class DirectConnectionHandshake
{
    private static readonly byte[] Client = "CLIENT"u8.ToArray();
    private static readonly byte[] ClientServer = "CLIENTSERVER"u8.ToArray();

    private int _matchLength;
    private int _responseOffset;

    public DirectConnectionHandshakeState State { get; private set; } =
        DirectConnectionHandshakeState.WaitingForClient;

    public static ReadOnlySpan<byte> RequestBytes => Client;
    public static ReadOnlySpan<byte> ResponseBytes => ClientServer;

    /// <summary>
    /// Consumes pre-handshake bytes. Returns the index immediately after the
    /// first complete CLIENT token, or -1 when no complete token was found.
    /// </summary>
    public int Feed(ReadOnlySpan<byte> bytes)
    {
        if (State != DirectConnectionHandshakeState.WaitingForClient)
            return -1;

        for (int index = 0; index < bytes.Length; index++)
        {
            byte value = bytes[index];
            if (value == Client[_matchLength])
            {
                _matchLength++;
                if (_matchLength != Client.Length) continue;

                _matchLength = 0;
                _responseOffset = 0;
                State = DirectConnectionHandshakeState.ResponsePending;
                return index + 1;
            }

            // CLIENT contains no internal 'C', so this is the complete
            // overlap rule required to recover from CLICLIENT and repeated C.
            _matchLength = value == Client[0] ? 1 : 0;
        }

        return -1;
    }

    public byte[] ReadResponse(int maximumBytes)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (State != DirectConnectionHandshakeState.ResponsePending)
            return Array.Empty<byte>();

        int count = Math.Min(maximumBytes, ClientServer.Length - _responseOffset);
        byte[] output = ClientServer.AsSpan(_responseOffset, count).ToArray();
        _responseOffset += count;
        if (_responseOffset == ClientServer.Length)
            State = DirectConnectionHandshakeState.Complete;
        return output;
    }

    public bool IsCompleteClientRetry(ReadOnlySpan<byte> bytes) =>
        bytes.SequenceEqual(Client);

    public void Close()
    {
        _matchLength = 0;
        _responseOffset = 0;
        State = DirectConnectionHandshakeState.Closed;
    }
}
