namespace PicoSwitchController;

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

public enum Nice64RudpbHelloResult
{
    Accepted,
    Busy
}

public sealed record Nice64RudpbFirmwareDiagnostics(
    IReadOnlyDictionary<string, uint> Values)
{
    public uint this[string name] => Values[name];
}

public sealed class Nice64RudpbDiagnosticClient : IAsyncDisposable
{
    public const int DefaultPort = 6465;
    public const int MaximumTestPayloadBytes = 512;
    public const int MaximumControlPayloadBytes = MaximumTestPayloadBytes + 1;

    public const byte Hello = 0x01;
    public const byte Ping = 0x03;
    public const byte Echo = 0x04;
    public const byte Goodbye = 0x05;
    public const byte Stats = 0x06;
    public const byte HelloAck = 0x81;
    public const byte Busy = 0x82;
    public const byte Pong = 0x83;
    public const byte EchoReply = 0x84;
    public const byte Released = 0x85;
    public const byte StatsReply = 0x86;
    public const byte UnsupportedClass = 0xE1;
    public const byte InvalidClass = 0xE2;

    private static readonly string[] StatsNames =
    [
        "udp_rx", "udp_tx", "bytes_rx", "bytes_tx", "valid_headers",
        "invalid_headers", "unsupported_class", "invalid_class",
        "session_mismatch", "hello_received", "hello_accepted",
        "busy_responses", "ping_received", "pong_sent",
        "ownership_releases", "ownership_timeouts", "staging_full",
        "tx_failures", "pbuf_chains", "maximum_pbuf_length",
        "pbuf_type_internal_last", "pbuf_type_internal_or", "copy_count",
        "copy_total_us", "copy_min_us", "copy_max_us", "callback_count",
        "callback_total_us", "callback_min_us", "callback_max_us",
        "validation_count", "validation_total_us", "validation_min_us",
        "validation_max_us", "service_count", "service_total_us",
        "service_min_us", "service_max_us", "maximum_pending_us",
        "send_count", "send_total_us", "send_min_us", "send_max_us",
        "codec_self_test_pass", "owner_active"
    ];

    private readonly UdpClient _udp;
    private uint _nextSequence;
    private long _ignoredResponses;
    private bool _disposed;

    public Nice64RudpbDiagnosticClient(string targetIp, int port = DefaultPort)
    {
        if (!IPAddress.TryParse(targetIp, out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("A literal manual IPv4 address is required.", nameof(targetIp));
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(port));

        SessionId = CreateNonzeroRandomUInt32();
        _nextSequence = CreateNonzeroRandomUInt32();
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Connect(new IPEndPoint(address, port));
    }

    public uint SessionId { get; }
    public long IgnoredResponses => _ignoredResponses;

    public async Task<Nice64RudpbHelloResult> EstablishAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Nice64RudpPacket response = await ExchangeAsync(
            new byte[] { Hello }, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Payload[0] switch
        {
            HelloAck => Nice64RudpbHelloResult.Accepted,
            Busy => Nice64RudpbHelloResult.Busy,
            _ => throw UnexpectedOperation(response.Payload[0], HelloAck)
        };
    }

    public async Task<TimeSpan> PingAsync(
        uint requestId, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        byte[] payload = new byte[13];
        payload[0] = Ping;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), requestId);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(5, 8), Stopwatch.GetTimestamp());
        long started = Stopwatch.GetTimestamp();
        Nice64RudpPacket response = await ExchangeAsync(
            payload, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.Payload[0] != Pong ||
            !response.Payload.AsSpan(1).SequenceEqual(payload.AsSpan(1)))
            throw new InvalidDataException("PONG did not preserve the diagnostic token.");
        return Stopwatch.GetElapsedTime(started);
    }

    public async Task EchoAsync(
        ReadOnlyMemory<byte> testPayload, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (testPayload.Length > MaximumTestPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(testPayload));
        byte[] payload = new byte[testPayload.Length + 1];
        payload[0] = Echo;
        testPayload.CopyTo(payload.AsMemory(1));
        Nice64RudpPacket response = await ExchangeAsync(
            payload, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.Payload[0] != EchoReply ||
            !response.Payload.AsSpan(1).SequenceEqual(testPayload.Span))
            throw new InvalidDataException("ECHO_REPLY payload was not byte-identical.");
    }

    public async Task<Nice64RudpbFirmwareDiagnostics> GetDiagnosticsAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Nice64RudpPacket response = await ExchangeAsync(
            new byte[] { Stats }, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        int required = 1 + (StatsNames.Length * sizeof(uint));
        if (response.Payload.Length != required || response.Payload[0] != StatsReply)
            throw new InvalidDataException("Invalid RUDP-B statistics response.");
        Dictionary<string, uint> values = new(StatsNames.Length, StringComparer.Ordinal);
        for (int index = 0; index < StatsNames.Length; index++)
        {
            values.Add(StatsNames[index], BinaryPrimitives.ReadUInt32BigEndian(
                response.Payload.AsSpan(1 + (index * sizeof(uint)), sizeof(uint))));
        }
        return new Nice64RudpbFirmwareDiagnostics(values);
    }

    public async Task ReleaseAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Nice64RudpPacket response = await ExchangeAsync(
            new byte[] { Goodbye }, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.Payload[0] != Released)
            throw UnexpectedOperation(response.Payload[0], Released);
    }

    public Task<byte> ProbeDeliveryClassAsync(
        byte deliveryClass, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        byte expected = deliveryClass == 1 ? UnsupportedClass : InvalidClass;
        return ExchangeMutatedAsync(new byte[] { Ping }, wire => wire[6] = deliveryClass,
                                    expected, timeout, cancellationToken);
    }

    public async Task SendMalformedAsync(
        Func<byte[], byte[]> mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        byte[] wire = CreateWire([Ping], NextSequence());
        wire = mutation(wire) ?? throw new InvalidOperationException(
            "Malformed-packet mutation returned null.");
        await _udp.SendAsync(wire, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int Sent, int Received)> BurstPingAsync(
        int count, TimeSpan collectFor,
        CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(count));
        HashSet<uint> pending = new(count);
        for (int index = 0; index < count; index++)
        {
            uint sequence = NextSequence();
            pending.Add(sequence);
            byte[] payload = new byte[5];
            payload[0] = Ping;
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1), (uint)index);
            await _udp.SendAsync(CreateWire(payload, sequence), cancellationToken)
                .ConfigureAwait(false);
        }

        long deadline = Stopwatch.GetTimestamp() +
            (long)(collectFor.TotalSeconds * Stopwatch.Frequency);
        while (pending.Count != 0 && Stopwatch.GetTimestamp() < deadline)
        {
            TimeSpan remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            using CancellationTokenSource timeoutSource = new(remaining);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutSource.Token);
            try
            {
                UdpReceiveResult received = await _udp.ReceiveAsync(linked.Token).ConfigureAwait(false);
                if (Nice64RudpPacketCodec.TryDecode(received.Buffer,
                        MaximumControlPayloadBytes, SessionId,
                        out Nice64RudpPacket? response, out _) &&
                    response!.Payload.Length != 0 && response.Payload[0] == Pong)
                    pending.Remove(response.Header.Sequence);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested &&
                                                     !cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
        return (count, count - pending.Count);
    }

    private async Task<byte> ExchangeMutatedAsync(
        ReadOnlyMemory<byte> payload, Action<byte[]> mutation, byte expected,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        uint sequence = NextSequence();
        byte[] wire = CreateWire(payload.Span, sequence);
        mutation(wire);
        await _udp.SendAsync(wire, cancellationToken).ConfigureAwait(false);
        Nice64RudpPacket response = await ReceiveAsync(
            sequence, timeout, cancellationToken).ConfigureAwait(false);
        if (response.Payload.Length != 1 || response.Payload[0] != expected)
            throw UnexpectedOperation(response.Payload[0], expected);
        return response.Payload[0];
    }

    private async Task<Nice64RudpPacket> ExchangeAsync(
        ReadOnlyMemory<byte> payload, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        uint sequence = NextSequence();
        byte[] wire = CreateWire(payload.Span, sequence);
        await _udp.SendAsync(wire, cancellationToken).ConfigureAwait(false);
        return await ReceiveAsync(sequence, timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Nice64RudpPacket> ReceiveAsync(
        uint sequence, TimeSpan timeout, CancellationToken cancellationToken)
    {
        long deadline = Stopwatch.GetTimestamp() +
            (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            TimeSpan remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            using CancellationTokenSource timeoutSource = new(remaining);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutSource.Token);
            try
            {
                UdpReceiveResult received = await _udp.ReceiveAsync(linked.Token).ConfigureAwait(false);
                if (!Nice64RudpPacketCodec.TryDecode(received.Buffer,
                        MaximumControlPayloadBytes, SessionId,
                        out Nice64RudpPacket? response, out Nice64RudpValidationError error))
                    throw new InvalidDataException($"Invalid RUDP-B response: {error}.");
                if (response!.Payload.Length == 0)
                    throw new InvalidDataException("RUDP-B response payload is empty.");
                if (response.Header.Sequence == sequence) return response;
                _ignoredResponses++;
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested &&
                                                     !cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
        throw new TimeoutException("Timed out waiting for the RUDP-B diagnostic response.");
    }

    private byte[] CreateWire(ReadOnlySpan<byte> payload, uint sequence)
    {
        Nice64RudpHeader header = new(
            Nice64WirelessTransportContract.Magic,
            Nice64WirelessTransportContract.TransportVersion,
            Nice64RudpPacketFlags.Data,
            (byte)Nice64WirelessDeliveryClass.ReliableOrdered,
            Nice64WirelessTransportContract.HeaderLength,
            SessionId,
            sequence,
            0,
            0,
            checked((ushort)payload.Length),
            0);
        return Nice64RudpPacketCodec.Encode(header, payload, MaximumControlPayloadBytes);
    }

    private uint NextSequence() => unchecked(_nextSequence++);

    private static uint CreateNonzeroRandomUInt32()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        uint value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        } while (value == 0);
        return value;
    }

    private static InvalidDataException UnexpectedOperation(byte actual, byte expected) =>
        new($"Unexpected RUDP-B operation 0x{actual:X2}; expected 0x{expected:X2}.");

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _udp.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
