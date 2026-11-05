using System.Buffers.Binary;

namespace PicoSwitchController;

internal enum LcpState
{
    Initial,
    ReqSent,
    AckRcvd,
    AckSent,
    Opened,
    Closing,
    Stopped,
    Failed,
}

internal sealed class LcpStatistics
{
    public int PacketsReceived { get; internal set; }
    public int PacketsSent { get; internal set; }
    public int ConfigureRequestsSent { get; internal set; }
    public int ConfigureRequestsReceived { get; internal set; }
    public int ConfigureAcksSent { get; internal set; }
    public int ConfigureAcksReceived { get; internal set; }
    public int ConfigureNaksReceived { get; internal set; }
    public int ConfigureRejectsReceived { get; internal set; }
    public int RestartTimeouts { get; internal set; }
    public int MalformedPackets { get; internal set; }
    public int InvalidResponses { get; internal set; }
    public int EchoRequestsReceived { get; internal set; }
    public int TerminateRequestsReceived { get; internal set; }
    public int ProtocolRejectsReceived { get; internal set; }
    public int CodeRejectsSent { get; internal set; }
}

internal sealed record LcpPeerResult(
    IReadOnlyList<LcpPacket> OutboundPackets,
    LcpState PreviousState,
    LcpState CurrentState,
    bool Opened = false,
    bool TerminationRequested = false,
    bool Failed = false,
    bool Ignored = false,
    string? Error = null,
    ushort? RejectedProtocol = null,
    ReadOnlyMemory<byte> RejectedProtocolData = default)
{
    public bool StateChanged => PreviousState != CurrentState;
}

/// <summary>
/// Minimal, synchronous LCP peer. It returns protocol actions and owns no
/// transport, timer, thread, or global negotiation state.
/// </summary>
internal sealed class LcpPeer
{
    public const ushort DefaultMru = 1500;
    public const ushort MinimumMru = 128;
    public const int DefaultMaxConfigureAttempts = 10;

    private byte _nextIdentifier;
    private bool _requestMru = true;
    private bool _requestMagic = true;
    private bool _ourRequestAcked;
    private bool _peerRequestAcked;
    private byte? _outstandingIdentifier;
    private byte[] _lastConfigureData = Array.Empty<byte>();
    private int _configureAttempts;

    public LcpPeer(
        uint localMagic,
        byte initialIdentifier = 0,
        int maxConfigureAttempts = DefaultMaxConfigureAttempts)
    {
        if (localMagic == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(localMagic), "Magic number must be nonzero.");
        }

        if (maxConfigureAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConfigureAttempts));
        }

        LocalMagic = localMagic;
        _nextIdentifier = initialIdentifier;
        MaxConfigureAttempts = maxConfigureAttempts;
    }

    public LcpState State { get; private set; } = LcpState.Initial;
    public LcpStatistics Statistics { get; } = new();
    public uint LocalMagic { get; private set; }
    public uint? PeerMagic { get; private set; }
    public ushort? PeerMru { get; private set; }
    public uint? PeerAccm { get; private set; }
    public ushort RequestedMru { get; private set; } = DefaultMru;
    public int MaxConfigureAttempts { get; }
    public int ConfigureAttempts => _configureAttempts;
    public byte? OutstandingIdentifier => _outstandingIdentifier;
    public ReadOnlyMemory<byte> OutstandingOptions => _lastConfigureData;
    public string? LastFailureReason { get; private set; }

    public LcpPeerResult Start()
    {
        LcpState previous = State;
        if (State is LcpState.Failed or LcpState.Stopped)
        {
            return Result(previous, error: "LCP_NOT_STARTABLE");
        }

        LcpPacket request = CreateNewConfigureRequest();
        UpdateNegotiationState();
        return Result(previous, new[] { request });
    }

    public LcpPeerResult OnRestartTimeout()
    {
        LcpState previous = State;
        Statistics.RestartTimeouts++;

        if (_outstandingIdentifier is null || State is LcpState.Opened or LcpState.Stopped or LcpState.Failed)
        {
            return Result(previous, error: "NO_CONFIGURE_REQUEST_PENDING");
        }

        if (_configureAttempts >= MaxConfigureAttempts)
        {
            State = LcpState.Failed;
            LastFailureReason = "CONFIGURE_RETRY_EXHAUSTED";
            return Result(previous, failed: true, error: LastFailureReason);
        }

        _configureAttempts++;
        Statistics.ConfigureRequestsSent++;
        var retransmit = new LcpPacket(
            LcpConstants.ConfigureRequest,
            _outstandingIdentifier.Value,
            _lastConfigureData.ToArray());
        return Result(previous, new[] { retransmit });
    }

    public LcpPeerResult Handle(PppFrame frame)
    {
        LcpState previous = State;
        if (frame.Protocol != LcpConstants.Protocol)
        {
            return Result(previous, ignored: true, error: "NOT_LCP");
        }

        Statistics.PacketsReceived++;
        if (!LcpPacket.TryParse(frame, out LcpPacket? packet, out string? error))
        {
            Statistics.MalformedPackets++;
            return Result(previous, error: error);
        }

        return packet!.Code switch
        {
            LcpConstants.ConfigureRequest => HandleConfigureRequest(packet, previous),
            LcpConstants.ConfigureAck => HandleConfigureAck(packet, previous),
            LcpConstants.ConfigureNak => HandleConfigureNak(packet, previous),
            LcpConstants.ConfigureReject => HandleConfigureReject(packet, previous),
            LcpConstants.TerminateRequest => HandleTerminateRequest(packet, previous),
            LcpConstants.TerminateAck => HandleTerminateAck(previous),
            LcpConstants.ProtocolReject => HandleProtocolReject(packet, previous),
            LcpConstants.EchoRequest => HandleEchoRequest(packet, previous),
            LcpConstants.EchoReply => HandleEchoReply(packet, previous),
            LcpConstants.CodeReject => Result(previous),
            _ => HandleUnknownCode(packet, previous),
        };
    }

    private LcpPeerResult HandleConfigureRequest(LcpPacket packet, LcpState previous)
    {
        Statistics.ConfigureRequestsReceived++;
        if (!LcpOptionCodec.TryParse(packet.Data.Span, out IReadOnlyList<LcpOption> options, out string? error))
        {
            Statistics.MalformedPackets++;
            return Result(previous, error: error);
        }

        var rejected = new List<LcpOption>();
        var naks = new List<LcpOption>();
        ushort? acceptedMru = null;
        uint? acceptedAccm = null;
        uint? acceptedMagic = null;

        foreach (LcpOption option in options)
        {
            switch (option.Type)
            {
                case LcpConstants.OptionMru:
                    if (option.Raw.Length != 4)
                    {
                        return MalformedOption(previous, "LCP_MRU_BAD_LENGTH");
                    }

                    ushort mru = BinaryPrimitives.ReadUInt16BigEndian(option.Value);
                    if (mru < MinimumMru || mru > DefaultMru)
                    {
                        naks.Add(new LcpOption(LcpConstants.OptionMru, LcpOptionCodec.Mru(DefaultMru)));
                    }
                    else
                    {
                        acceptedMru = mru;
                    }
                    break;

                case LcpConstants.OptionAccm:
                    if (option.Raw.Length != 6)
                    {
                        return MalformedOption(previous, "LCP_ACCM_BAD_LENGTH");
                    }

                    acceptedAccm = BinaryPrimitives.ReadUInt32BigEndian(option.Value);
                    break;

                case LcpConstants.OptionMagicNumber:
                    if (option.Raw.Length != 6)
                    {
                        return MalformedOption(previous, "LCP_MAGIC_BAD_LENGTH");
                    }

                    uint magic = BinaryPrimitives.ReadUInt32BigEndian(option.Value);
                    if (magic == 0 || magic == LocalMagic)
                    {
                        uint suggestion = AlternativeMagic(LocalMagic);
                        naks.Add(new LcpOption(LcpConstants.OptionMagicNumber, LcpOptionCodec.Magic(suggestion)));
                    }
                    else
                    {
                        acceptedMagic = magic;
                    }
                    break;

                case LcpConstants.OptionAuthentication:
                    if (option.Raw.Length < 4)
                    {
                        return MalformedOption(previous, "LCP_AUTH_BAD_LENGTH");
                    }
                    rejected.Add(option);
                    break;

                case LcpConstants.OptionProtocolFieldCompression:
                case LcpConstants.OptionAddressControlFieldCompression:
                    if (option.Raw.Length != 2)
                    {
                        return MalformedOption(previous, "LCP_COMPRESSION_BAD_LENGTH");
                    }
                    rejected.Add(option);
                    break;

                default:
                    rejected.Add(option);
                    break;
            }
        }

        bool wasOpened = State == LcpState.Opened;
        var outbound = new List<LcpPacket>();
        if (rejected.Count > 0)
        {
            _peerRequestAcked = false;
            outbound.Add(new LcpPacket(
                LcpConstants.ConfigureReject,
                packet.Identifier,
                LcpOptionCodec.Concatenate(rejected)));
        }
        else if (naks.Count > 0)
        {
            _peerRequestAcked = false;
            outbound.Add(new LcpPacket(
                LcpConstants.ConfigureNak,
                packet.Identifier,
                LcpOptionCodec.Concatenate(naks)));
        }
        else
        {
            _peerRequestAcked = true;
            PeerMru = acceptedMru;
            PeerAccm = acceptedAccm;
            PeerMagic = acceptedMagic;
            Statistics.ConfigureAcksSent++;
            outbound.Add(new LcpPacket(
                LcpConstants.ConfigureAck,
                packet.Identifier,
                packet.Data.ToArray()));
        }

        if (wasOpened)
        {
            _ourRequestAcked = false;
            outbound.Add(CreateNewConfigureRequest());
        }

        UpdateNegotiationState();
        return Result(previous, outbound);
    }

    private LcpPeerResult HandleConfigureAck(LcpPacket packet, LcpState previous)
    {
        if (_outstandingIdentifier is null ||
            packet.Identifier != _outstandingIdentifier.Value ||
            !packet.Data.Span.SequenceEqual(_lastConfigureData))
        {
            Statistics.InvalidResponses++;
            return Result(previous, error: "INVALID_CONFIGURE_ACK");
        }

        Statistics.ConfigureAcksReceived++;
        _ourRequestAcked = true;
        UpdateNegotiationState();
        return Result(previous);
    }

    private LcpPeerResult HandleConfigureNak(LcpPacket packet, LcpState previous)
    {
        if (!ValidateOutstandingIdentifier(packet))
        {
            Statistics.InvalidResponses++;
            return Result(previous, error: "INVALID_CONFIGURE_NAK_IDENTIFIER");
        }

        if (!LcpOptionCodec.TryParse(packet.Data.Span, out IReadOnlyList<LcpOption> options, out string? error) || options.Count == 0)
        {
            Statistics.InvalidResponses++;
            return Result(previous, error: error ?? "EMPTY_CONFIGURE_NAK");
        }

        bool sawMru = false;
        bool sawMagic = false;
        ushort newMru = RequestedMru;
        uint newMagic = LocalMagic;
        foreach (LcpOption option in options)
        {
            if (option.Type == LcpConstants.OptionMru && _requestMru && option.Raw.Length == 4 && !sawMru)
            {
                ushort suggestion = BinaryPrimitives.ReadUInt16BigEndian(option.Value);
                if (suggestion < MinimumMru || suggestion > DefaultMru)
                {
                    return InvalidNegotiationResponse(previous, "UNSUPPORTED_MRU_NAK");
                }
                newMru = suggestion;
                sawMru = true;
            }
            else if (option.Type == LcpConstants.OptionMagicNumber && _requestMagic && option.Raw.Length == 6 && !sawMagic)
            {
                uint suggestion = BinaryPrimitives.ReadUInt32BigEndian(option.Value);
                if (suggestion == 0 || suggestion == PeerMagic)
                {
                    return InvalidNegotiationResponse(previous, "UNSUPPORTED_MAGIC_NAK");
                }
                newMagic = suggestion;
                sawMagic = true;
            }
            else
            {
                return InvalidNegotiationResponse(previous, "UNSUPPORTED_CONFIGURE_NAK");
            }
        }

        RequestedMru = newMru;
        LocalMagic = newMagic;
        Statistics.ConfigureNaksReceived++;
        _ourRequestAcked = false;
        LcpPacket request = CreateNewConfigureRequest();
        UpdateNegotiationState();
        return Result(previous, new[] { request });
    }

    private LcpPeerResult HandleConfigureReject(LcpPacket packet, LcpState previous)
    {
        string? error = null;
        if (!ValidateOutstandingIdentifier(packet) ||
            !LcpOptionCodec.TryParse(packet.Data.Span, out IReadOnlyList<LcpOption> rejected, out error) ||
            rejected.Count == 0 ||
            !LcpOptionCodec.TryParse(_lastConfigureData, out IReadOnlyList<LcpOption> requested, out _))
        {
            Statistics.InvalidResponses++;
            return Result(previous, error: error ?? "INVALID_CONFIGURE_REJECT");
        }

        int searchFrom = 0;
        bool removeMru = false;
        bool removeMagic = false;
        foreach (LcpOption reject in rejected)
        {
            int match = -1;
            for (int i = searchFrom; i < requested.Count; i++)
            {
                if (reject.Raw.Span.SequenceEqual(requested[i].Raw.Span))
                {
                    match = i;
                    break;
                }
            }

            if (match < 0)
            {
                return InvalidNegotiationResponse(previous, "CONFIGURE_REJECT_NOT_EXACT");
            }

            searchFrom = match + 1;
            if (reject.Type == LcpConstants.OptionMru)
            {
                removeMru = true;
            }
            else if (reject.Type == LcpConstants.OptionMagicNumber)
            {
                removeMagic = true;
            }
        }

        _requestMru &= !removeMru;
        _requestMagic &= !removeMagic;
        Statistics.ConfigureRejectsReceived++;
        _ourRequestAcked = false;
        LcpPacket request = CreateNewConfigureRequest();
        UpdateNegotiationState();
        return Result(previous, new[] { request });
    }

    private LcpPeerResult HandleTerminateRequest(LcpPacket packet, LcpState previous)
    {
        Statistics.TerminateRequestsReceived++;
        _ourRequestAcked = false;
        _peerRequestAcked = false;
        State = LcpState.Stopped;
        var acknowledgement = new LcpPacket(LcpConstants.TerminateAck, packet.Identifier, Array.Empty<byte>());
        return Result(previous, new[] { acknowledgement }, terminationRequested: true);
    }

    private LcpPeerResult HandleTerminateAck(LcpState previous)
    {
        if (State == LcpState.Closing)
        {
            State = LcpState.Stopped;
        }
        return Result(previous);
    }

    private LcpPeerResult HandleEchoRequest(LcpPacket packet, LcpState previous)
    {
        if (State != LcpState.Opened)
        {
            return Result(previous, ignored: true, error: "ECHO_BEFORE_OPENED");
        }

        if (packet.Data.Length < 4)
        {
            Statistics.MalformedPackets++;
            return Result(previous, error: "ECHO_TOO_SHORT");
        }

        Statistics.EchoRequestsReceived++;
        byte[] data = packet.Data.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), LocalMagic);
        var reply = new LcpPacket(LcpConstants.EchoReply, packet.Identifier, data);
        return Result(previous, new[] { reply });
    }

    private LcpPeerResult HandleEchoReply(LcpPacket packet, LcpState previous)
    {
        if (packet.Data.Length < 4)
        {
            Statistics.MalformedPackets++;
            return Result(previous, error: "ECHO_REPLY_TOO_SHORT");
        }
        return Result(previous);
    }

    private LcpPeerResult HandleProtocolReject(LcpPacket packet, LcpState previous)
    {
        if (packet.Data.Length < 2)
        {
            Statistics.MalformedPackets++;
            return Result(previous, error: "PROTOCOL_REJECT_TOO_SHORT");
        }

        Statistics.ProtocolRejectsReceived++;
        ushort protocol = BinaryPrimitives.ReadUInt16BigEndian(packet.Data.Span.Slice(0, 2));
        ReadOnlyMemory<byte> rejectedData = packet.Data.Slice(2).ToArray();
        if (protocol == LcpConstants.Protocol)
        {
            State = LcpState.Failed;
            LastFailureReason = "LCP_PROTOCOL_REJECTED";
            return Result(previous, failed: true, error: LastFailureReason,
                rejectedProtocol: protocol, rejectedProtocolData: rejectedData);
        }

        return Result(previous, rejectedProtocol: protocol, rejectedProtocolData: rejectedData);
    }

    private LcpPeerResult HandleUnknownCode(LcpPacket packet, LcpState previous)
    {
        Statistics.CodeRejectsSent++;
        var reject = new LcpPacket(LcpConstants.CodeReject, AllocateIdentifier(), packet.ToInformation());
        return Result(previous, new[] { reject });
    }

    private LcpPacket CreateNewConfigureRequest()
    {
        var options = new List<LcpOption>();
        if (_requestMru)
        {
            options.Add(new LcpOption(LcpConstants.OptionMru, LcpOptionCodec.Mru(RequestedMru)));
        }
        if (_requestMagic)
        {
            options.Add(new LcpOption(LcpConstants.OptionMagicNumber, LcpOptionCodec.Magic(LocalMagic)));
        }

        _lastConfigureData = LcpOptionCodec.Concatenate(options);
        _outstandingIdentifier = AllocateIdentifier();
        _configureAttempts = 1;
        Statistics.ConfigureRequestsSent++;
        return new LcpPacket(LcpConstants.ConfigureRequest, _outstandingIdentifier.Value, _lastConfigureData.ToArray());
    }

    private void UpdateNegotiationState()
    {
        if (State is LcpState.Failed or LcpState.Stopped or LcpState.Closing)
        {
            return;
        }

        State = (_ourRequestAcked, _peerRequestAcked) switch
        {
            (true, true) => LcpState.Opened,
            (true, false) => LcpState.AckRcvd,
            (false, true) => LcpState.AckSent,
            _ => LcpState.ReqSent,
        };
    }

    private bool ValidateOutstandingIdentifier(LcpPacket packet) =>
        _outstandingIdentifier.HasValue && packet.Identifier == _outstandingIdentifier.Value;

    private LcpPeerResult MalformedOption(LcpState previous, string error)
    {
        Statistics.MalformedPackets++;
        return Result(previous, error: error);
    }

    private LcpPeerResult InvalidNegotiationResponse(LcpState previous, string error)
    {
        Statistics.InvalidResponses++;
        return Result(previous, error: error);
    }

    private static uint AlternativeMagic(uint conflict)
    {
        uint value = conflict ^ 0xA5A5A5A5u;
        return value == 0 || value == conflict ? 1u : value;
    }

    private byte AllocateIdentifier()
    {
        byte result = _nextIdentifier;
        unchecked
        {
            _nextIdentifier++;
        }
        return result;
    }

    private LcpPeerResult Result(
        LcpState previous,
        IReadOnlyList<LcpPacket>? outbound = null,
        bool terminationRequested = false,
        bool failed = false,
        bool ignored = false,
        string? error = null,
        ushort? rejectedProtocol = null,
        ReadOnlyMemory<byte> rejectedProtocolData = default)
    {
        outbound ??= Array.Empty<LcpPacket>();
        Statistics.PacketsSent += outbound.Count;
        return new LcpPeerResult(
            outbound,
            previous,
            State,
            previous != LcpState.Opened && State == LcpState.Opened,
            terminationRequested,
            failed,
            ignored,
            error,
            rejectedProtocol,
            rejectedProtocolData);
    }
}
