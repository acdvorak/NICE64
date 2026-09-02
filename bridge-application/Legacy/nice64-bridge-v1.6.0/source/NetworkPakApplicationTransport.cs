namespace PicoSwitchController;

using System.Buffers.Binary;
using System.Text;

public enum NetworkPakWifiTransport
{
    Tcp,
    ReliableUdpQuick
}

public sealed class NetworkPakApplicationMessageFramer
{
    public const int MaximumMessageBytes = 4096;
    private readonly Action<ReadOnlyMemory<byte>> _deliver;
    private readonly byte[] _message = new byte[MaximumMessageBytes];
    private readonly byte[] _length = new byte[2];
    private int _lengthBytes;
    private int _expected = -1;
    private int _received;

    public NetworkPakApplicationMessageFramer(Action<ReadOnlyMemory<byte>> deliver) =>
        _deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));

    public static byte[] Frame(ReadOnlySpan<byte> message)
    {
        if (message.Length is < 1 or > MaximumMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(message));
        byte[] framed = new byte[2 + message.Length];
        BinaryPrimitives.WriteUInt16BigEndian(framed, checked((ushort)message.Length));
        message.CopyTo(framed.AsSpan(2));
        return framed;
    }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            if (_expected < 0)
            {
                while (_lengthBytes < 2 && !bytes.IsEmpty)
                {
                    _length[_lengthBytes++] = bytes[0];
                    bytes = bytes[1..];
                }
                if (_lengthBytes < 2) return;
                _expected = BinaryPrimitives.ReadUInt16BigEndian(_length);
                _lengthBytes = 0;
                _received = 0;
                if (_expected is < 1 or > MaximumMessageBytes)
                {
                    Reset();
                    throw new InvalidDataException("Invalid Network Pak application message length.");
                }
            }

            int take = Math.Min(_expected - _received, bytes.Length);
            bytes[..take].CopyTo(_message.AsSpan(_received));
            _received += take;
            bytes = bytes[take..];
            if (_received == _expected)
            {
                byte[] complete = _message.AsSpan(0, _expected).ToArray();
                Reset();
                _deliver(complete);
            }
        }
    }

    public void Reset()
    {
        _lengthBytes = 0;
        _expected = -1;
        _received = 0;
    }
}

public sealed class NetworkPakApplicationDispatcher(NetworkPakBridgeService service)
{
    public bool TryHandle(ReadOnlySpan<byte> request, out byte[] response)
    {
        string line = Encoding.ASCII.GetString(request);
        if (!service.TryHandleRequest(line, out string responseLine))
        {
            response = [];
            return false;
        }
        response = responseLine.Length == 0 ? [] : Encoding.ASCII.GetBytes(responseLine);
        return true;
    }

    public void MarkResponseWritten(ReadOnlySpan<byte> response) =>
        service.MarkNetworkPakResponseWritten(Encoding.ASCII.GetString(response));
}
