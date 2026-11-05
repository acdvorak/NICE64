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
    public long Ipv4FramesDropped { get; internal set; }
    public long IcmpEchoRequests { get; internal set; }
    public long IcmpEchoReplies { get; internal set; }
    public long UdpEchoRequests { get; internal set; }
    public long UdpEchoReplies { get; internal set; }
    public long TcpSegmentsHandled { get; internal set; }
    public long TcpReplies { get; internal set; }
    public int QueueHighWaterMark { get; internal set; }
    public string CloseReason { get; internal set; } = "None";
}

/// <summary>Runtime orchestration for framing, LCP, IPCP, and local IPv4 services.</summary>
internal sealed class PppSession : IDisposable
{
    public const int DefaultOutboundQueueCapacity = 32 * 1024;
    public const int MaximumCapturedIpv4Frames = 64;
    public const int MaximumCapturedIpv4Bytes = 64 * 1024;
    // RFC 1661 describes a 3-second default Restart timer. LcpPeer already
    // owns the attempt limit and retransmission contents; this is only its clock.
    public static readonly TimeSpan DefaultLcpRestartInterval = TimeSpan.FromSeconds(3);

    private readonly object _sync = new();
    private readonly PppFrameDecoder _decoder = new();
    private readonly PppFrameEncoder _encoder = new();
    private readonly LcpPeer _lcp;
    private readonly PppAddressAssignment _addresses;
    private readonly byte _ipcpIdentifierSeed;
    private readonly Action<string> _log;
    private readonly BoundedByteQueue _outbound;
    private readonly Queue<byte[]> _ipv4Received = new();
    private readonly LocalIpv4Endpoint _localIpv4;
    private readonly ILcpRestartScheduler _lcpRestartScheduler;
    private readonly TimeSpan _lcpRestartInterval;
    private IpcpPeer _ipcp;
    private int _ipv4ReceivedBytes;
    private bool _ipcpStarted;
    private bool _disposed;
    private int _ipv4DropLogsRemaining = 8;

    public PppSession(
        Guid connectionId,
        PppAddressAssignment addresses,
        uint localMagic,
        byte lcpIdentifierSeed = 1,
        byte ipcpIdentifierSeed = 1,
        int outboundQueueCapacity = DefaultOutboundQueueCapacity,
        Action<string>? log = null,
        Func<Action, ILcpRestartScheduler>? lcpRestartSchedulerFactory = null,
        TimeSpan? lcpRestartInterval = null)
    {
        if (connectionId == Guid.Empty)
            throw new ArgumentException("PPP session requires a connection UUID.", nameof(connectionId));
        if (outboundQueueCapacity < 128)
            throw new ArgumentOutOfRangeException(nameof(outboundQueueCapacity));

        TimeSpan restartInterval = lcpRestartInterval ?? DefaultLcpRestartInterval;
        if (restartInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lcpRestartInterval));

        ConnectionId = connectionId;
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
        _ipcpIdentifierSeed = ipcpIdentifierSeed;
        _log = log ?? (_ => { });
        _outbound = new BoundedByteQueue(outboundQueueCapacity);
        _localIpv4 = new LocalIpv4Endpoint(_addresses);
        _lcp = new LcpPeer(localMagic, lcpIdentifierSeed);
        _ipcp = CreateIpcpPeer();
        _lcpRestartInterval = restartInterval;
        _lcpRestartScheduler = (lcpRestartSchedulerFactory ??
            (callback => new LcpRestartScheduler(callback)))(OnLcpRestartTimeout);
        State = PppSessionState.Created;

        _log($"PPP session created connection={ConnectionId:D}");
        QueueLcpResult(_lcp.Start());
        if (State != PppSessionState.Failed)
        {
            State = PppSessionState.LcpNegotiating;
            ArmLcpRestart("initial Configure-Request");
        }
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
    internal bool LcpRestartScheduled => _lcpRestartScheduler.IsArmed;
    internal int LcpConfigureAttempts => _lcp.ConfigureAttempts;
    internal byte? LcpOutstandingIdentifier => _lcp.OutstandingIdentifier;

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
            _lcpRestartScheduler.Cancel();
            _lcpRestartScheduler.Dispose();
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
                byte? previousIdentifier = _lcp.OutstandingIdentifier;
                int previousAttempts = _lcp.ConfigureAttempts;
                LcpPeerResult lcpResult = _lcp.Handle(frame);
                QueueLcpResult(lcpResult);
                if (State == PppSessionState.Failed) return;

                UpdateLcpRestartAfterInput(previousIdentifier, previousAttempts);

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

                LocalIpv4Result local = _localIpv4.Handle(payload);
                if (!local.Handled)
                {
                    Statistics.Ipv4FramesDropped++;
                    if (_ipv4DropLogsRemaining > 0)
                    {
                        _ipv4DropLogsRemaining--;
                        _log($"PPP IPv4 DROP connection={ConnectionId:D} " +
                             $"reason={local.Disposition} len={payload.Length}");
                    }
                    return;
                }

                _log($"PPP IPv4 RX src={local.Source} dst={local.Destination} " +
                     $"proto={local.Protocol} len={local.TotalLength} connection={ConnectionId:D}");
                switch (local.Disposition)
                {
                    case LocalIpv4Disposition.EchoReply:
                        Statistics.IcmpEchoRequests++;
                        _log($"PPP ICMP EchoRequest id={local.IcmpIdentifier} " +
                             $"seq={local.IcmpSequence} bytes={local.TotalLength} connection={ConnectionId:D}");
                        break;
                    case LocalIpv4Disposition.UdpEchoReply:
                        Statistics.UdpEchoRequests++;
                        _log($"PPP UDP_RX src={local.Source}:{local.SourcePort} " +
                             $"dst={local.Destination}:{local.DestinationPort} " +
                             $"length={local.PayloadLength} checksum=valid connection={ConnectionId:D}");
                        break;
                    case LocalIpv4Disposition.TcpSynAck:
                        Statistics.TcpSegmentsHandled++;
                        _log($"PPP TCP_SYN_RX src={local.Source}:{local.SourcePort} " +
                             $"dst={local.Destination}:{local.DestinationPort} seq={local.TcpSequence} connection={ConnectionId:D}");
                        break;
                    case LocalIpv4Disposition.TcpEstablished:
                        Statistics.TcpSegmentsHandled++;
                        _log($"PPP TCP_ACK_RX seq={local.TcpSequence} ack={local.TcpAcknowledgment} connection={ConnectionId:D}");
                        _log($"PPP TCP_ESTABLISHED port={local.SourcePort} connection={ConnectionId:D}");
                        break;
                    case LocalIpv4Disposition.TcpDataEcho:
                        Statistics.TcpSegmentsHandled++;
                        _log($"PPP TCP_DATA_RX seq={local.TcpSequence} ack={local.TcpAcknowledgment} " +
                             $"length={local.PayloadLength} connection={ConnectionId:D}");
                        break;
                    case LocalIpv4Disposition.TcpFinAck:
                        Statistics.TcpSegmentsHandled++;
                        _log($"PPP TCP_FIN_RX seq={local.TcpSequence} ack={local.TcpAcknowledgment} connection={ConnectionId:D}");
                        break;
                    case LocalIpv4Disposition.TcpClosed:
                        Statistics.TcpSegmentsHandled++;
                        _log($"PPP TCP_CLOSED port={local.SourcePort} connection={ConnectionId:D}");
                        break;
                }
                if (!local.Replied) return;
                if (!QueueFrame(new PppFrame(0x0021, local.Reply))) return;
                switch (local.Disposition)
                {
                    case LocalIpv4Disposition.EchoReply:
                        Statistics.IcmpEchoReplies++;
                        _log($"PPP ICMP EchoReply id={local.IcmpIdentifier} " +
                             $"seq={local.IcmpSequence} bytes={local.TotalLength} connection={ConnectionId:D}");
                        break;
                    case LocalIpv4Disposition.UdpEchoReply:
                        Statistics.UdpEchoReplies++;
                        _log($"PPP UDP_TX_ECHO src={_addresses.Local}:{LocalIpv4Endpoint.UdpEchoPort} " +
                             $"dst={_addresses.Remote}:{local.SourcePort} length={local.PayloadLength} connection={ConnectionId:D}");
                        break;
                    case LocalIpv4Disposition.TcpSynAck:
                        Statistics.TcpReplies++;
                        _log($"PPP TCP_SYNACK_TX seq={LocalIpv4Endpoint.TcpInitialSequence} " +
                             $"ack={local.TcpSequence + 1} connection={ConnectionId:D}");
                        break;
                    case LocalIpv4Disposition.TcpDataEcho:
                        Statistics.TcpReplies++;
                        _log($"PPP TCP_DATA_TX length={local.PayloadLength} connection={ConnectionId:D}");
                        break;
                    case LocalIpv4Disposition.TcpFinAck:
                        Statistics.TcpReplies++;
                        _log($"PPP TCP_FIN_TX connection={ConnectionId:D}");
                        break;
                }
                _log($"PPP IPv4 TX src={_addresses.Local} dst={_addresses.Remote} " +
                     $"proto={local.Protocol} len={local.Reply.Length} connection={ConnectionId:D}");
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
        _lcpRestartScheduler.Cancel();
    }

    private void Fail(string reason)
    {
        if (State == PppSessionState.Failed) return;
        FailureReason = reason;
        Statistics.CloseReason = reason;
        State = PppSessionState.Failed;
        _lcpRestartScheduler.Cancel();
        _log($"PPP session failed connection={ConnectionId:D} reason={reason}");
    }

    private void OnLcpRestartTimeout()
    {
        lock (_sync)
        {
            if (_disposed || !LcpRestartRequired()) return;

            byte identifier = _lcp.OutstandingIdentifier!.Value;
            int nextAttempt = _lcp.ConfigureAttempts + 1;
            _log($"PPP LCP session={ConnectionId:D} restart timeout " +
                 $"id={identifier} attempt={nextAttempt}");

            LcpPeerResult result = _lcp.OnRestartTimeout();
            QueueLcpResult(result);
            if (State == PppSessionState.Failed)
            {
                _log($"PPP LCP session={ConnectionId:D} retry exhausted id={identifier}");
                return;
            }

            if (result.OutboundPackets.Any(packet =>
                    packet.Code == LcpConstants.ConfigureRequest))
            {
                _log($"PPP LCP session={ConnectionId:D} Configure-Request " +
                     $"retransmitted id={identifier} attempt={_lcp.ConfigureAttempts}");
            }

            if (LcpRestartRequired())
                ArmLcpRestart("retransmission");
        }
    }

    private void UpdateLcpRestartAfterInput(byte? previousIdentifier, int previousAttempts)
    {
        if (!LcpRestartRequired())
        {
            _lcpRestartScheduler.Cancel();
            return;
        }

        // Peer Configure-Requests do not reset our independent deadline. A Nak,
        // Reject, or renegotiation creates a new request and therefore a new one.
        if (_lcp.OutstandingIdentifier != previousIdentifier ||
            _lcp.ConfigureAttempts != previousAttempts)
            ArmLcpRestart("new Configure-Request");
    }

    private bool LcpRestartRequired() =>
        !_disposed &&
        State is not (PppSessionState.Closing or PppSessionState.Closed or PppSessionState.Failed) &&
        _lcp.OutstandingIdentifier.HasValue &&
        _lcp.State is LcpState.ReqSent or LcpState.AckSent;

    private void ArmLcpRestart(string reason)
    {
        if (!LcpRestartRequired()) return;
        _lcpRestartScheduler.Arm(_lcpRestartInterval);
        _log($"PPP LCP session={ConnectionId:D} restart armed " +
             $"id={_lcp.OutstandingIdentifier} attempt={_lcp.ConfigureAttempts} " +
             $"delayMs={_lcpRestartInterval.TotalMilliseconds:F0} reason={reason}");
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
    private const int DeferredInputCapacity = 2048;

    private readonly object _sync = new();
    private readonly Guid _connectionId;
    private readonly PppAddressAssignment _addresses;
    private readonly uint _localMagic;
    private readonly ModuleContext _openContext;
    private readonly Func<Action, ILcpRestartScheduler>? _lcpRestartSchedulerFactory;
    private readonly TimeSpan? _lcpRestartInterval;
    private readonly DirectConnectionHandshake _handshake = new();
    private readonly BoundedByteQueue _deferredInput = new(DeferredInputCapacity);
    private PppSession? _session;
    private string? _failureReason;
    private bool _closed;

    public PppLiveResource(
        Guid connectionId,
        PppAddressAssignment addresses,
        uint localMagic,
        ModuleContext openContext,
        Func<Action, ILcpRestartScheduler>? lcpRestartSchedulerFactory = null,
        TimeSpan? lcpRestartInterval = null)
    {
        if (connectionId == Guid.Empty)
            throw new ArgumentException("PPP live resource requires a connection UUID.", nameof(connectionId));
        _connectionId = connectionId;
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
        _localMagic = localMagic;
        _openContext = openContext ?? throw new ArgumentNullException(nameof(openContext));
        _lcpRestartSchedulerFactory = lcpRestartSchedulerFactory;
        _lcpRestartInterval = lcpRestartInterval;
    }

    internal PppSession? Session => _session;
    internal DirectConnectionHandshakeState HandshakeState => _handshake.State;
    public bool IsClosed => _closed || _session?.IsClosed == true;

    public NetworkPakLiveReadResult Read(int maximumBytes)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        lock (_sync)
        {
            if (_failureReason != null)
                return NetworkPakLiveReadResult.Failure(_failureReason);
            if (_closed)
                return NetworkPakLiveReadResult.Eof();

            if (_handshake.State == DirectConnectionHandshakeState.ResponsePending)
            {
                byte[] response = _handshake.ReadResponse(maximumBytes);
                if (_handshake.State == DirectConnectionHandshakeState.Complete)
                {
                    _openContext.Log(
                        $"PPP DIRECT HANDSHAKE session={_connectionId:D} complete");
                    StartPpp();
                }
                // Never coalesce initial LCP with the final CLIENTSERVER bytes.
                // The next read is the first read allowed to expose PPP output.
                return NetworkPakLiveReadResult.Available(response);
            }

            if (_handshake.State == DirectConnectionHandshakeState.WaitingForClient)
                return NetworkPakLiveReadResult.WouldBlock();

            return _session?.Read(maximumBytes) ?? NetworkPakLiveReadResult.WouldBlock();
        }
    }

    public NetworkPakWriteResult Write(NetworkPakWriteChunk chunk, ModuleContext context)
    {
        lock (_sync)
        {
            if (context.ConnectionId != _connectionId)
                return NetworkPakWriteResult.Fail("LIVE_CONNECTION_MISMATCH");
            if (_closed)
                return NetworkPakWriteResult.Fail("LIVE_STREAM_CLOSED");
            if (_failureReason != null)
                return NetworkPakWriteResult.Fail(_failureReason);

            if (_handshake.State == DirectConnectionHandshakeState.WaitingForClient)
            {
                int consumed = _handshake.Feed(chunk.Data);
                if (consumed >= 0)
                {
                    _openContext.Log(
                        $"PPP DIRECT HANDSHAKE session={_connectionId:D} CLIENT matched");
                    _openContext.Log(
                        $"PPP DIRECT HANDSHAKE session={_connectionId:D} CLIENTSERVER queued");
                    if (consumed < chunk.Data.Length &&
                        !_deferredInput.TryEnqueue(chunk.Data.AsSpan(consumed)))
                        return Fail("PPP_DIRECT_DEFERRED_INPUT_OVERFLOW");
                }
                return NetworkPakWriteResult.Ack([1]);
            }

            if (_handshake.State == DirectConnectionHandshakeState.ResponsePending)
            {
                // A whole-token retry is expected if CE's watchdog fires before it
                // has drained CLIENTSERVER. Suppress it without queuing a duplicate.
                if (!_handshake.IsCompleteClientRetry(chunk.Data) &&
                    !_deferredInput.TryEnqueue(chunk.Data))
                    return Fail("PPP_DIRECT_DEFERRED_INPUT_OVERFLOW");
                return NetworkPakWriteResult.Ack([1]);
            }

            if (_session == null || !_session.Feed(chunk.Data))
                return NetworkPakWriteResult.Fail(
                    _session?.FailureReason ?? "LIVE_STREAM_CLOSED");
            return NetworkPakWriteResult.Ack([1]);
        }
    }

    public void Close(string reason)
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            _handshake.Close();
            _deferredInput.Clear();
            _openContext.Log($"PPP live resource close connection={_connectionId:D} reason={reason}");
            _session?.Close(reason);
        }
    }

    public void Dispose() => Close("DISPOSED");

    private void StartPpp()
    {
        if (_session != null || _failureReason != null || _closed) return;
        _session = new PppSession(
            _connectionId,
            _addresses,
            _localMagic,
            log: _openContext.Log,
            lcpRestartSchedulerFactory: _lcpRestartSchedulerFactory,
            lcpRestartInterval: _lcpRestartInterval);
        _openContext.Log($"PPP session={_connectionId:D} PPP start released");

        if (_deferredInput.Count > 0)
        {
            byte[] deferred = _deferredInput.Dequeue(_deferredInput.Count);
            if (!_session.Feed(deferred))
                _failureReason = _session.FailureReason ?? "PPP_DEFERRED_INPUT_FAILED";
        }
    }

    private NetworkPakWriteResult Fail(string reason)
    {
        _failureReason = reason;
        return NetworkPakWriteResult.Fail(reason);
    }
}
