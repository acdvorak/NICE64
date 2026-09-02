namespace PicoSwitchController;

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

public sealed record NetworkPakRudpTransportSnapshot(
    uint SessionId,
    bool Connected,
    long RequestsReceived,
    long ResponsesQueued,
    long InvalidMessages,
    long ApplicationDeliveries,
    long DuplicateApplicationDeliveries,
    Nice64RudpDiagnostics Tx,
    Nice64RudpDiagnostics Rx,
    string LastError);

public readonly record struct NetworkPakRudpSessionParameters(
    uint SessionId,
    uint HostInitialSequence,
    uint FirmwareInitialSequence)
{
    public static NetworkPakRudpSessionParameters CreateRandom() => new(
        NetworkPakRudpTransport.RandomNonzeroUInt32(),
        NetworkPakRudpTransport.RandomNonzeroUInt32(),
        NetworkPakRudpTransport.RandomNonzeroUInt32());
}

public sealed class NetworkPakRudpTransport : IAsyncDisposable
{
    public const int DefaultPort = 6465;
    public const int Window = 8;
    public const int PhysicalFlightCap = 2;
    public const int PayloadBytes = 512;
    public const int RtoMs = 40;
    public const int RetryBudget = 4;

    private const byte Hello = 0x01;
    private const byte Goodbye = 0x05;
    private const byte Ping = 0x03;
    private const byte Pong = 0x83;
    private const byte HelloAck = 0x81;
    private const byte NetworkBegin = 0x20;
    private const byte NetworkBeginAck = 0xA0;
    private const byte ApplicationVersion = 1;

    private readonly UdpClient _udp;
    private readonly Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>?> _handler;
    private readonly Action<string>? _log;
    private readonly ConcurrentQueue<byte[]> _outgoing = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Nice64RudpTransmitWindow _tx;
    private readonly Nice64RudpReceiveWindow _rx;
    private readonly NetworkPakApplicationMessageFramer _framer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Task? _serviceTask;
    private byte[]? _pendingFrame;
    private int _pendingOffset;
    private uint _controlSequence;
    private bool _disposed;
    private long _requestsReceived;
    private long _responsesQueued;
    private long _invalidMessages;
    private long _applicationDeliveries;
    private long _duplicateApplicationDeliveries = 0;
    private string _lastError = "None";
    private long _lastKeepAliveMs;

    private NetworkPakRudpTransport(
        IPAddress address,
        int port,
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>?> handler,
        Action<string>? log)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _log = log;
        NetworkPakRudpSessionParameters session =
            NetworkPakRudpSessionParameters.CreateRandom();
        SessionId = session.SessionId;
        HostInitialSequence = session.HostInitialSequence;
        FirmwareInitialSequence = session.FirmwareInitialSequence;
        _controlSequence = RandomNonzeroUInt32();
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Connect(new IPEndPoint(address, port));
        Nice64RudpConfiguration configuration = new(Window, PayloadBytes, RtoMs, RetryBudget);
        _framer = new NetworkPakApplicationMessageFramer(DeliverApplicationMessage);
        _tx = new Nice64RudpTransmitWindow(configuration, SessionId, HostInitialSequence);
        _rx = new Nice64RudpReceiveWindow(configuration, SessionId,
            (_, payload) => _framer.Feed(payload.Span), FirmwareInitialSequence);
    }

    public uint SessionId { get; }
    public uint HostInitialSequence { get; }
    public uint FirmwareInitialSequence { get; }
    public bool IsConnected { get; private set; }
    public event Action<Exception>? Faulted;

    public static async Task<NetworkPakRudpTransport> ConnectAsync(
        string target,
        int port,
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>?> handler,
        TimeSpan timeout,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(target, out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("A literal manual IPv4 address is required.", nameof(target));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        NetworkPakRudpTransport transport = new(address, port, handler, log);
        try
        {
            await transport.EstablishAsync(timeout, cancellationToken).ConfigureAwait(false);
            transport.IsConnected = true;
            transport._serviceTask = Task.Run(
                () => transport.ServiceLoopAsync(transport._lifetime.Token));
            return transport;
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void QueueMessage(ReadOnlySpan<byte> message)
    {
        if (!IsConnected) throw new IOException("Network Pak RUDP session is not connected.");
        _outgoing.Enqueue(NetworkPakApplicationMessageFramer.Frame(message));
        Interlocked.Increment(ref _responsesQueued);
    }

    public NetworkPakRudpTransportSnapshot GetSnapshot() => new(
        SessionId, IsConnected, Interlocked.Read(ref _requestsReceived),
        Interlocked.Read(ref _responsesQueued), Interlocked.Read(ref _invalidMessages),
        Interlocked.Read(ref _applicationDeliveries),
        Interlocked.Read(ref _duplicateApplicationDeliveries),
        _tx.Diagnostics, _rx.Diagnostics, _lastError);

    private async Task EstablishAsync(TimeSpan timeout, CancellationToken token)
    {
        Nice64RudpPacket hello = await ExchangeControlAsync(
            new byte[] { Hello }, HelloAck, timeout, token)
            .ConfigureAwait(false);
        if (hello.Payload.Length != 1) throw new IOException("Invalid RUDP HELLO_ACK.");

        byte[] begin = new byte[12];
        begin[0] = NetworkBegin;
        begin[1] = ApplicationVersion;
        BinaryPrimitives.WriteUInt16BigEndian(begin.AsSpan(2), RtoMs);
        BinaryPrimitives.WriteUInt32BigEndian(begin.AsSpan(4), HostInitialSequence);
        BinaryPrimitives.WriteUInt32BigEndian(begin.AsSpan(8), FirmwareInitialSequence);
        Nice64RudpPacket reply = await ExchangeControlAsync(begin, NetworkBeginAck, timeout, token)
            .ConfigureAwait(false);
        if (reply.Payload.Length != 12 || reply.Payload[1] != 0 ||
            reply.Payload[2] != ApplicationVersion ||
            BinaryPrimitives.ReadUInt32BigEndian(reply.Payload.AsSpan(4)) != HostInitialSequence ||
            BinaryPrimitives.ReadUInt32BigEndian(reply.Payload.AsSpan(8)) != FirmwareInitialSequence)
            throw new IOException("NICE64 rejected Product Quick Mode session parameters.");
        _log?.Invoke($"NETWORK RUDP CONNECT session=0x{SessionId:X8} " +
            $"host_tx={HostInitialSequence} firmware_tx={FirmwareInitialSequence}");
    }

    private async Task ServiceLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_clock.ElapsedMilliseconds - _lastKeepAliveMs >= 10_000)
                {
                    byte[] ping = CreateWire([Ping], unchecked(_controlSequence++),
                        Nice64RudpPacketFlags.Data, 0, 0);
                    await _udp.SendAsync(ping, token).ConfigureAwait(false);
                    _lastKeepAliveMs = _clock.ElapsedMilliseconds;
                }
                AdmitMessages();
                foreach (Nice64RudpTransmission transmission in
                         _tx.CollectDue(_clock.ElapsedMilliseconds))
                {
                    Nice64RudpAck ack = _rx.CreateAck();
                    byte[] wire = CreateWire(transmission.Payload, transmission.Sequence,
                        Nice64RudpPacketFlags.Data | Nice64RudpPacketFlags.Ack,
                        ack.AckBase, ack.AckBitmap);
                    await _udp.SendAsync(wire, token).ConfigureAwait(false);
                }
                if (_tx.Failed)
                    throw new IOException("Network Pak RUDP retry budget exhausted.");

                while (_udp.Available > 0)
                {
                    UdpReceiveResult datagram = await _udp.ReceiveAsync(token).ConfigureAwait(false);
                    if (!Nice64RudpPacketCodec.TryDecode(datagram.Buffer, PayloadBytes,
                            SessionId, out Nice64RudpPacket? packet, out _))
                    {
                        Interlocked.Increment(ref _invalidMessages);
                        continue;
                    }
                    if ((packet!.Header.Flags & Nice64RudpPacketFlags.Ack) != 0)
                        _tx.ApplyAck(new(packet.Header.AckBase, packet.Header.AckBitmap));
                    if (packet.Header.Flags == Nice64RudpPacketFlags.Data &&
                        packet.Payload.Length == 1 && packet.Payload[0] == Pong)
                        continue;
                    if ((packet.Header.Flags & Nice64RudpPacketFlags.Data) != 0 &&
                        packet.Payload.Length != 0)
                    {
                        Nice64RudpReceiveResult receive = _rx.Receive(packet);
                        await SendAckAsync(token).ConfigureAwait(false);
                    }
                }
                await Task.Delay(1, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            IsConnected = false;
            Faulted?.Invoke(ex);
        }
    }

    private void AdmitMessages()
    {
        while (Nice64RudpSequence.ForwardDistance(_tx.BaseSequence, _tx.NextSequence) <
               PhysicalFlightCap)
        {
            if (_pendingFrame is null && !_outgoing.TryDequeue(out _pendingFrame)) return;
            int count = Math.Min(PayloadBytes, _pendingFrame.Length - _pendingOffset);
            if (!_tx.TryEnqueue(_pendingFrame.AsSpan(_pendingOffset, count), out _)) return;
            _pendingOffset += count;
            if (_pendingOffset == _pendingFrame.Length)
            {
                _pendingFrame = null;
                _pendingOffset = 0;
            }
        }
    }

    private void DeliverApplicationMessage(ReadOnlyMemory<byte> message)
    {
        Interlocked.Increment(ref _requestsReceived);
        Interlocked.Increment(ref _applicationDeliveries);
        ReadOnlyMemory<byte>? response;
        try
        {
            response = _handler(message);
        }
        catch
        {
            Interlocked.Increment(ref _invalidMessages);
            throw;
        }
        if (response is { Length: > 0 }) QueueMessage(response.Value.Span);
    }

    private async Task SendAckAsync(CancellationToken token)
    {
        Nice64RudpAck ack = _rx.CreateAck();
        await _udp.SendAsync(CreateWire([], 0, Nice64RudpPacketFlags.Ack,
            ack.AckBase, ack.AckBitmap), token).ConfigureAwait(false);
    }

    private async Task<Nice64RudpPacket> ExchangeControlAsync(
        ReadOnlyMemory<byte> payload, byte expectedOperation,
        TimeSpan timeout, CancellationToken token)
    {
        uint sequence = unchecked(_controlSequence++);
        await _udp.SendAsync(CreateWire(payload.Span, sequence,
            Nice64RudpPacketFlags.Data, 0, 0), token).ConfigureAwait(false);
        using CancellationTokenSource limit =
            CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        try
        {
            while (true)
            {
                UdpReceiveResult datagram = await _udp.ReceiveAsync(limit.Token)
                    .ConfigureAwait(false);
                if (Nice64RudpPacketCodec.TryDecode(datagram.Buffer, PayloadBytes,
                        SessionId, out Nice64RudpPacket? packet, out _) &&
                    packet!.Header.Sequence == sequence && packet.Payload.Length != 0 &&
                    packet.Payload[0] == expectedOperation)
                    return packet;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out establishing Network Pak Product Quick Mode (0x{expectedOperation:X2}).");
        }
    }

    private byte[] CreateWire(ReadOnlySpan<byte> payload, uint sequence,
        Nice64RudpPacketFlags flags, uint ackBase, uint ackBitmap)
    {
        Nice64RudpHeader header = new(Nice64WirelessTransportContract.Magic,
            Nice64WirelessTransportContract.TransportVersion, flags,
            (byte)Nice64WirelessDeliveryClass.ReliableOrdered,
            Nice64WirelessTransportContract.HeaderLength, SessionId, sequence,
            ackBase, ackBitmap, checked((ushort)payload.Length), 0);
        return Nice64RudpPacketCodec.Encode(header, payload, PayloadBytes);
    }

    internal static uint RandomNonzeroUInt32()
    {
        Span<byte> bytes = stackalloc byte[4];
        uint value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        } while (value == 0);
        return value;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        IsConnected = false;
        _lifetime.Cancel();
        if (_serviceTask is not null)
        {
            try { await _serviceTask.ConfigureAwait(false); }
            catch { }
        }
        try
        {
            byte[] goodbye = CreateWire([Goodbye], unchecked(_controlSequence++),
                Nice64RudpPacketFlags.Data, 0, 0);
            await _udp.SendAsync(goodbye).ConfigureAwait(false);
        }
        catch { }
        _udp.Dispose();
        _lifetime.Dispose();
    }
}
