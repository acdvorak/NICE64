namespace PicoSwitchController;

internal enum PppSessionState
{
    Created,
    LcpNegotiating,
    IpcpNegotiating,
    Opened,
    Closing,
    Closed,
    Failed,
}

internal sealed class PppSessionStatistics
{
    public long LiveReads { get; internal set; }
    public long WouldBlockReads { get; internal set; }
    public long BytesRead { get; internal set; }
    public long BytesWritten { get; internal set; }
    public long FramesReceived { get; internal set; }
    public long FramesTransmitted { get; internal set; }
    public long Ipv4FramesObserved { get; internal set; }
    public long Ipv4FramesIgnored { get; internal set; }
    public int QueueHighWaterMark { get; internal set; }
    public string CloseReason { get; internal set; } = "None";
}

/// <summary>Runtime orchestration only: framing, LCP, IPCP, and IPv4 placeholders.</summary>
internal sealed class PppSession : IDisposable
{
    public const int DefaultOutboundQueueCapacity = 32 * 1024;
    public const int MaximumCapturedIpv4Frames = 64;
    public const int MaximumCapturedIpv4Bytes = 64 * 1024;

    private readonly object _sync = new();
    private readonly PppFrameDecoder _decoder = new();
    private readonly PppFrameEncoder _encoder = new();
    private readonly LcpPeer _lcp;
    private readonly PppAddressAssignment _addresses;
    private readonly byte _ipcpIdentifierSeed;
    private readonly Action<string> _log;
    private readonly BoundedByteQueue _outbound;
    private readonly Queue<byte[]> _ipv4Received = new();
    private IpcpPeer _ipcp;
    private int _ipv4ReceivedBytes;
    private bool _ipcpStarted;
    private bool _disposed;

    public PppSession(
        Guid connectionId,
        PppAddressAssignment addresses,
        uint localMagic,
        byte lcpIdentifierSeed = 1,
        byte ipcpIdentifierSeed = 1,
        int outboundQueueCapacity = DefaultOutboundQueueCapacity,
        Action<string>? log = null)
    {
        if (connectionId == Guid.Empty)
            throw new ArgumentException("PPP session requires a connection UUID.", nameof(connectionId));
        if (outboundQueueCapacity < 128)
            throw new ArgumentOutOfRangeException(nameof(outboundQueueCapacity));

        ConnectionId = connectionId;
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
        _ipcpIdentifierSeed = ipcpIdentifierSeed;
        _log = log ?? (_ => { });
        _outbound = new BoundedByteQueue(outboundQueueCapacity);
        _lcp = new LcpPeer(localMagic, lcpIdentifierSeed);
        _ipcp = CreateIpcpPeer();
        State = PppSessionState.Created;

        _log($"PPP session created connection={ConnectionId:D}");
        QueueLcpResult(_lcp.Start());
        if (State != PppSessionState.Failed)
            State = PppSessionState.LcpNegotiating;
    }

    public Guid ConnectionId { get; }
    public PppSessionState State { get; private set; }
    public LcpState LcpState => _lcp.State;
    public IpcpState IpcpState => _ipcp.State;
    public PppSessionStatistics Statistics { get; } = new();
    public int OutboundQueueCapacity => _outbound.Capacity;
    public int OutboundBytesAvailable { get { lock (_sync) return _outbound.Count; } }
    public long MalformedFrames => _decoder.FramesRejectedFcs +
        _decoder.FramesRejectedMalformed + _decoder.FramesRejectedOversize;
    public string? FailureReason { get; private set; }
    public bool IsClosed => State is PppSessionState.Closed or PppSessionState.Failed;

    public NetworkPakLiveReadResult Read(int maximumBytes)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        lock (_sync)
        {
            Statistics.LiveReads++;
            if (State == PppSessionState.Failed)
                return NetworkPakLiveReadResult.Failure(FailureReason ?? "PPP_SESSION_FAILED");

            byte[] data = _outbound.Dequeue(maximumBytes);
            if (data.Length > 0)
            {
                Statistics.BytesRead += data.Length;
                return NetworkPakLiveReadResult.Available(data);
            }

            if (State == PppSessionState.Closing)
            {
                State = PppSessionState.Closed;
                return NetworkPakLiveReadResult.Eof();
            }
            if (State == PppSessionState.Closed)
                return NetworkPakLiveReadResult.Eof();

            Statistics.WouldBlockReads++;
            return NetworkPakLiveReadResult.WouldBlock();
        }
    }

    public bool Feed(ReadOnlySpan<byte> bytes)
    {
        lock (_sync)
        {
            if (State is PppSessionState.Closing or PppSessionState.Closed or PppSessionState.Failed)
                return false;
            Statistics.BytesWritten += bytes.Length;
            _decoder.Feed(bytes, HandleFrame);
            return State != PppSessionState.Failed;
        }
    }

    public bool TryDequeueIpv4(out byte[] payload)
    {
        lock (_sync)
        {
            if (_ipv4Received.Count == 0)
            {
                payload = Array.Empty<byte>();
                return false;
            }
            payload = _ipv4Received.Dequeue();
            _ipv4ReceivedBytes -= payload.Length;
            return true;
        }
    }

    public bool EnqueueIpv4Packet(ReadOnlySpan<byte> payload)
    {
        lock (_sync)
        {
            if (State != PppSessionState.Opened) return false;
            return QueueFrame(new PppFrame(0x0021, payload.ToArray()));
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _decoder.Reset();
            _outbound.Clear();
            _ipv4Received.Clear();
            _ipv4ReceivedBytes = 0;
            if (State != PppSessionState.Failed)
                State = PppSessionState.Closed;
            if (Statistics.CloseReason == "None")
                Statistics.CloseReason = "DISPOSED";
            _log($"PPP session closed connection={ConnectionId:D} reason={Statistics.CloseReason}");
        }
    }

    public void Close(string reason)
    {
        lock (_sync)
        {
            Statistics.CloseReason = string.IsNullOrWhiteSpace(reason) ? "CLOSED" : reason;
            Dispose();
        }
    }

    private void HandleFrame(PppFrame frame)
    {
        Statistics.FramesReceived++;
        switch (frame.Protocol)
        {
            case LcpConstants.Protocol:
                LogControlPacket("LCP", "RX", frame.Information.Span);
                bool wasOpened = _lcp.State == LcpState.Opened;
                LcpPeerResult lcpResult = _lcp.Handle(frame);
                QueueLcpResult(lcpResult);
                if (State == PppSessionState.Failed) return;

                if (wasOpened && _lcp.State != LcpState.Opened &&
                    !lcpResult.TerminationRequested)
                {
                    _ipcp = CreateIpcpPeer();
                    _ipcpStarted = false;
                    State = PppSessionState.LcpNegotiating;
                }

                if (_lcp.State == LcpState.Opened && !_ipcpStarted)
                {
                    _log($"PPP LCP OPENED connection={ConnectionId:D}");
                    _ipcpStarted = true;
                    State = PppSessionState.IpcpNegotiating;
                    QueueIpcpResult(_ipcp.Start());
                }
                break;

            case IpcpConstants.Protocol:
                LogControlPacket("IPCP", "RX", frame.Information.Span);
                if (_lcp.State != LcpState.Opened || !_ipcpStarted) return;
                QueueIpcpResult(_ipcp.Handle(frame));
                if (State == PppSessionState.Failed) return;
                if (_ipcp.State == IpcpState.Opened)
                {
                    if (State != PppSessionState.Opened)
                        _log($"PPP IPCP OPENED connection={ConnectionId:D} " +
                            $"local={_addresses.Local} remote={_addresses.Remote}");
                    State = PppSessionState.Opened;
                }
                else if (State == PppSessionState.Opened)
                {
                    State = PppSessionState.IpcpNegotiating;
                }
                break;

            case 0x0021:
                if (State != PppSessionState.Opened)
                {
                    Statistics.Ipv4FramesIgnored++;
                    return;
                }
                if (_ipv4Received.Count >= MaximumCapturedIpv4Frames ||
                    frame.Information.Length > MaximumCapturedIpv4Bytes - _ipv4ReceivedBytes)
                {
                    Fail("IPV4_PLACEHOLDER_OVERFLOW");
                    return;
                }
                byte[] payload = frame.Information.ToArray();
                _ipv4Received.Enqueue(payload);
                _ipv4ReceivedBytes += payload.Length;
                Statistics.Ipv4FramesObserved++;
                break;
        }
    }

    private void QueueLcpResult(LcpPeerResult result)
    {
        foreach (LcpPacket packet in result.OutboundPackets)
        {
            PppFrame frame = packet.ToPppFrame();
            LogControlPacket("LCP", "TX", frame.Information.Span);
            if (!QueueFrame(frame)) return;
        }
        if (result.Failed)
        {
            Fail(result.Error ?? "LCP_FAILED");
            return;
        }
        if (result.TerminationRequested)
            BeginGracefulClose("LCP_TERMINATE_REQUEST");
    }

    private void QueueIpcpResult(IpcpPeerResult result)
    {
        foreach (IpcpPacket packet in result.OutboundPackets)
        {
            PppFrame frame = packet.ToPppFrame();
            LogControlPacket("IPCP", "TX", frame.Information.Span);
            if (!QueueFrame(frame)) return;
        }
        if (result.Failed)
        {
            Fail(result.Error ?? "IPCP_FAILED");
            return;
        }
        if (result.TerminationRequested)
            BeginGracefulClose("IPCP_TERMINATE_REQUEST");
    }

    private bool QueueFrame(PppFrame frame)
    {
        byte[] wire = _encoder.Encode(frame);
        if (!_outbound.TryEnqueue(wire))
        {
            Fail("PPP_OUTBOUND_QUEUE_OVERFLOW");
            return false;
        }
        Statistics.FramesTransmitted++;
        Statistics.QueueHighWaterMark = _outbound.HighWaterMark;
        return true;
    }

    private void BeginGracefulClose(string reason)
    {
        Statistics.CloseReason = reason;
        State = PppSessionState.Closing;
    }

    private void Fail(string reason)
    {
        if (State == PppSessionState.Failed) return;
        FailureReason = reason;
        Statistics.CloseReason = reason;
        State = PppSessionState.Failed;
        _log($"PPP session failed connection={ConnectionId:D} reason={reason}");
    }

    private IpcpPeer CreateIpcpPeer() => new(_addresses, _ipcpIdentifierSeed);

    private void LogControlPacket(string protocol, string direction,
                                  ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 4)
        {
            _log($"PPP {protocol} {direction} malformed length={packet.Length} " +
                 $"connection={ConnectionId:D}");
            return;
        }
        int declared = (packet[2] << 8) | packet[3];
        int shown = Math.Min(packet.Length, Math.Max(4, declared));
        _log($"PPP {protocol} {direction} code={packet[0]} id={packet[1]} " +
             $"length={declared} bytes={Convert.ToHexString(packet[..shown])} " +
             $"connection={ConnectionId:D}");
    }
}

internal sealed class PppLiveResource : INetworkPakLiveResource
{
    private readonly PppSession _session;
    private readonly ModuleContext _openContext;

    public PppLiveResource(PppSession session, ModuleContext openContext)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _openContext = openContext ?? throw new ArgumentNullException(nameof(openContext));
    }

    internal PppSession Session => _session;
    public bool IsClosed => _session.IsClosed;
    public NetworkPakLiveReadResult Read(int maximumBytes) => _session.Read(maximumBytes);

    public NetworkPakWriteResult Write(NetworkPakWriteChunk chunk, ModuleContext context)
    {
        if (context.ConnectionId != _session.ConnectionId)
            return NetworkPakWriteResult.Fail("LIVE_CONNECTION_MISMATCH");
        if (!_session.Feed(chunk.Data))
            return NetworkPakWriteResult.Fail(_session.FailureReason ?? "LIVE_STREAM_CLOSED");
        return NetworkPakWriteResult.Ack([1]);
    }

    public void Close(string reason)
    {
        _openContext.Log($"PPP live resource close connection={_session.ConnectionId:D} reason={reason}");
        _session.Close(reason);
    }

    public void Dispose() => Close("DISPOSED");
}
