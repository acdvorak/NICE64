namespace PicoSwitchController;

internal enum IpcpState
{
    Initial,
    ReqSent,
    AckRcvd,
    AckSent,
    Opened,
    Stopped,
    Failed,
}

internal sealed class IpcpStatistics
{
    public int PacketsReceived { get; internal set; }
    public int PacketsSent { get; internal set; }
    public int ConfigureRequestsSent { get; internal set; }
    public int ConfigureRequestsReceived { get; internal set; }
    public int ConfigureAcksSent { get; internal set; }
    public int ConfigureAcksReceived { get; internal set; }
    public int ConfigureNaksSent { get; internal set; }
    public int ConfigureNaksReceived { get; internal set; }
    public int ConfigureRejectsSent { get; internal set; }
    public int ConfigureRejectsReceived { get; internal set; }
    public int MalformedPackets { get; internal set; }
    public int InvalidResponses { get; internal set; }
    public int RestartTimeouts { get; internal set; }
    public int TerminateRequestsReceived { get; internal set; }
    public int Renegotiations { get; internal set; }
    public int CodeRejectsSent { get; internal set; }
}

internal sealed record IpcpPeerResult(
    IReadOnlyList<IpcpPacket> OutboundPackets,
    IpcpState PreviousState,
    IpcpState CurrentState,
    bool Opened = false,
    bool TerminationRequested = false,
    bool Failed = false,
    bool Ignored = false,
    string? Error = null)
{
    public bool StateChanged => PreviousState != CurrentState;
}

/// <summary>
/// Minimal synchronous IPCP address negotiator. PppSession must start it only
/// after LCP is OPENED; Gate C intentionally provides no cross-protocol runtime.
/// </summary>
internal sealed class IpcpPeer
{
    public const int DefaultMaxConfigureAttempts = 10;

    private byte _nextIdentifier;
    private bool _ourRequestAcked;
    private bool _peerRequestAcked;
    private byte? _outstandingIdentifier;
    private byte[] _lastConfigureData = Array.Empty<byte>();
    private int _configureAttempts;

    public IpcpPeer(
        PppAddressAssignment addresses,
        byte initialIdentifier = 0,
        int maxConfigureAttempts = DefaultMaxConfigureAttempts)
    {
        if (addresses.Local == Ipv4Address.Zero || addresses.Remote == Ipv4Address.Zero)
        {
            throw new ArgumentException("Configured IPCP addresses must be nonzero.", nameof(addresses));
        }
        if (addresses.Local == addresses.Remote)
        {
            throw new ArgumentException("Local and remote IPCP addresses must differ.", nameof(addresses));
        }
        if (maxConfigureAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConfigureAttempts));
        }

        Addresses = addresses;
        _nextIdentifier = initialIdentifier;
        MaxConfigureAttempts = maxConfigureAttempts;
    }

    public PppAddressAssignment Addresses { get; }
    public IpcpState State { get; private set; } = IpcpState.Initial;
    public IpcpStatistics Statistics { get; } = new();
    public int MaxConfigureAttempts { get; }
    public int ConfigureAttempts => _configureAttempts;
    public byte? OutstandingIdentifier => _outstandingIdentifier;
    public ReadOnlyMemory<byte> OutstandingOptions => _lastConfigureData;
    public Ipv4Address? NegotiatedPeerAddress { get; private set; }
    public string? LastFailureReason { get; private set; }

    public IpcpPeerResult Start()
    {
        IpcpState previous = State;
        if (State is IpcpState.Failed or IpcpState.Stopped)
        {
            return Result(previous, error: "IPCP_NOT_STARTABLE");
        }

        _ourRequestAcked = false;
        IpcpPacket request = CreateNewConfigureRequest(resetAttempts: true);
        UpdateNegotiationState();
        return Result(previous, new[] { request });
    }

    public IpcpPeerResult OnRestartTimeout()
    {
        IpcpState previous = State;
        Statistics.RestartTimeouts++;

        if (_outstandingIdentifier is null || State is IpcpState.Opened or IpcpState.Stopped or IpcpState.Failed)
        {
            return Result(previous, error: "NO_IPCP_CONFIGURE_REQUEST_PENDING");
        }

        if (_configureAttempts >= MaxConfigureAttempts)
        {
            return Fail(previous, "IPCP_CONFIGURE_RETRY_EXHAUSTED");
        }

        _configureAttempts++;
        var retransmit = new IpcpPacket(
            IpcpConstants.ConfigureRequest,
            _outstandingIdentifier.Value,
            _lastConfigureData.ToArray());
        return Result(previous, new[] { retransmit });
    }

    public IpcpPeerResult Handle(PppFrame frame)
    {
        IpcpState previous = State;
        if (frame.Protocol != IpcpConstants.Protocol)
        {
            return Result(previous, ignored: true, error: "NOT_IPCP");
        }

        Statistics.PacketsReceived++;
        if (!IpcpPacket.TryParse(frame, out IpcpPacket? packet, out string? error))
        {
            Statistics.MalformedPackets++;
            return Result(previous, error: error);
        }

        return packet!.Code switch
        {
            IpcpConstants.ConfigureRequest => HandleConfigureRequest(packet, previous),
            IpcpConstants.ConfigureAck => HandleConfigureAck(packet, previous),
            IpcpConstants.ConfigureNak => HandleConfigureNak(packet, previous),
            IpcpConstants.ConfigureReject => HandleConfigureReject(packet, previous),
            IpcpConstants.TerminateRequest => HandleTerminateRequest(packet, previous),
            IpcpConstants.TerminateAck => Result(previous),
            IpcpConstants.CodeReject => Result(previous),
            _ => HandleUnknownCode(packet, previous),
        };
    }

    private IpcpPeerResult HandleConfigureRequest(IpcpPacket packet, IpcpState previous)
    {
        Statistics.ConfigureRequestsReceived++;
        if (!IpcpOptionCodec.TryParse(packet.Data.Span, out IReadOnlyList<IpcpOption> options, out string? error))
        {
            Statistics.MalformedPackets++;
            return Result(previous, error: error);
        }

        var rejected = new List<IpcpOption>();
        bool sawAddress = false;
        Ipv4Address requestedAddress = Ipv4Address.Zero;

        foreach (IpcpOption option in options)
        {
            if (option.Type == IpcpConstants.OptionIpAddress)
            {
                if (option.Raw.Length != 6)
                {
                    return MalformedOption(previous, "IPCP_ADDRESS_BAD_LENGTH");
                }
                if (sawAddress)
                {
                    return MalformedOption(previous, "IPCP_DUPLICATE_ADDRESS_OPTION");
                }

                sawAddress = true;
                requestedAddress = IpcpOptionCodec.ReadAddress(option);
            }
            else
            {
                rejected.Add(option);
            }
        }

        bool wasOpened = State == IpcpState.Opened;
        var outbound = new List<IpcpPacket>();
        if (rejected.Count > 0)
        {
            _peerRequestAcked = false;
            NegotiatedPeerAddress = null;
            outbound.Add(new IpcpPacket(
                IpcpConstants.ConfigureReject,
                packet.Identifier,
                IpcpOptionCodec.Concatenate(rejected)));
        }
        else if (!sawAddress || requestedAddress != Addresses.Remote)
        {
            _peerRequestAcked = false;
            NegotiatedPeerAddress = null;
            outbound.Add(new IpcpPacket(
                IpcpConstants.ConfigureNak,
                packet.Identifier,
                IpcpOptionCodec.IpAddress(Addresses.Remote)));
        }
        else
        {
            _peerRequestAcked = true;
            NegotiatedPeerAddress = Addresses.Remote;
            outbound.Add(new IpcpPacket(
                IpcpConstants.ConfigureAck,
                packet.Identifier,
                packet.Data.ToArray()));
        }

        if (wasOpened)
        {
            Statistics.Renegotiations++;
            _ourRequestAcked = false;
            outbound.Add(CreateNewConfigureRequest(resetAttempts: true));
        }

        UpdateNegotiationState();
        return Result(previous, outbound);
    }

    private IpcpPeerResult HandleConfigureAck(IpcpPacket packet, IpcpState previous)
    {
        if (_outstandingIdentifier is null ||
            packet.Identifier != _outstandingIdentifier.Value ||
            !packet.Data.Span.SequenceEqual(_lastConfigureData))
        {
            Statistics.InvalidResponses++;
            return Result(previous, error: "INVALID_IPCP_CONFIGURE_ACK");
        }

        Statistics.ConfigureAcksReceived++;
        _ourRequestAcked = true;
        UpdateNegotiationState();
        return Result(previous);
    }

    private IpcpPeerResult HandleConfigureNak(IpcpPacket packet, IpcpState previous)
    {
        string? error = null;
        if (!ValidateOutstandingIdentifier(packet) ||
            !IpcpOptionCodec.TryParse(packet.Data.Span, out IReadOnlyList<IpcpOption> options, out error) ||
            options.Count != 1 ||
            options[0].Type != IpcpConstants.OptionIpAddress ||
            options[0].Raw.Length != 6)
        {
            Statistics.InvalidResponses++;
            return Result(previous, error: error ?? "INVALID_IPCP_CONFIGURE_NAK");
        }

        if (_configureAttempts >= MaxConfigureAttempts)
        {
            return Fail(previous, "IPCP_CONFIGURE_RETRY_EXHAUSTED");
        }

        // The local endpoint address is policy-owned. Any well-formed proposed
        // address is observed but never allowed to rewrite the assignment.
        _ = IpcpOptionCodec.ReadAddress(options[0]);
        Statistics.ConfigureNaksReceived++;
        _ourRequestAcked = false;
        IpcpPacket request = CreateNewConfigureRequest(resetAttempts: false);
        UpdateNegotiationState();
        return Result(previous, new[] { request });
    }

    private IpcpPeerResult HandleConfigureReject(IpcpPacket packet, IpcpState previous)
    {
        if (!ValidateOutstandingIdentifier(packet) ||
            !packet.Data.Span.SequenceEqual(_lastConfigureData))
        {
            Statistics.InvalidResponses++;
            return Result(previous, error: "INVALID_IPCP_CONFIGURE_REJECT");
        }

        Statistics.ConfigureRejectsReceived++;
        return Fail(previous, "IPCP_ADDRESS_OPTION_REJECTED");
    }

    private IpcpPeerResult HandleTerminateRequest(IpcpPacket packet, IpcpState previous)
    {
        Statistics.TerminateRequestsReceived++;
        _ourRequestAcked = false;
        _peerRequestAcked = false;
        NegotiatedPeerAddress = null;
        State = IpcpState.Stopped;
        var acknowledgement = new IpcpPacket(IpcpConstants.TerminateAck, packet.Identifier, Array.Empty<byte>());
        return Result(previous, new[] { acknowledgement }, terminationRequested: true);
    }

    private IpcpPeerResult HandleUnknownCode(IpcpPacket packet, IpcpState previous)
    {
        Statistics.CodeRejectsSent++;
        var reject = new IpcpPacket(IpcpConstants.CodeReject, AllocateIdentifier(), packet.ToInformation());
        return Result(previous, new[] { reject });
    }

    private IpcpPacket CreateNewConfigureRequest(bool resetAttempts)
    {
        _lastConfigureData = IpcpOptionCodec.IpAddress(Addresses.Local);
        _outstandingIdentifier = AllocateIdentifier();
        _configureAttempts = resetAttempts ? 1 : _configureAttempts + 1;
        return new IpcpPacket(
            IpcpConstants.ConfigureRequest,
            _outstandingIdentifier.Value,
            _lastConfigureData.ToArray());
    }

    private void UpdateNegotiationState()
    {
        if (State is IpcpState.Failed or IpcpState.Stopped)
        {
            return;
        }

        State = (_ourRequestAcked, _peerRequestAcked) switch
        {
            (true, true) => IpcpState.Opened,
            (true, false) => IpcpState.AckRcvd,
            (false, true) => IpcpState.AckSent,
            _ => IpcpState.ReqSent,
        };
    }

    private bool ValidateOutstandingIdentifier(IpcpPacket packet) =>
        _outstandingIdentifier.HasValue && packet.Identifier == _outstandingIdentifier.Value;

    private IpcpPeerResult MalformedOption(IpcpState previous, string error)
    {
        Statistics.MalformedPackets++;
        return Result(previous, error: error);
    }

    private IpcpPeerResult Fail(IpcpState previous, string reason)
    {
        _ourRequestAcked = false;
        _peerRequestAcked = false;
        NegotiatedPeerAddress = null;
        State = IpcpState.Failed;
        LastFailureReason = reason;
        return Result(previous, failed: true, error: reason);
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

    private IpcpPeerResult Result(
        IpcpState previous,
        IReadOnlyList<IpcpPacket>? outbound = null,
        bool terminationRequested = false,
        bool failed = false,
        bool ignored = false,
        string? error = null)
    {
        outbound ??= Array.Empty<IpcpPacket>();
        foreach (IpcpPacket packet in outbound)
        {
            Statistics.PacketsSent++;
            switch (packet.Code)
            {
                case IpcpConstants.ConfigureRequest:
                    Statistics.ConfigureRequestsSent++;
                    break;
                case IpcpConstants.ConfigureAck:
                    Statistics.ConfigureAcksSent++;
                    break;
                case IpcpConstants.ConfigureNak:
                    Statistics.ConfigureNaksSent++;
                    break;
                case IpcpConstants.ConfigureReject:
                    Statistics.ConfigureRejectsSent++;
                    break;
            }
        }

        return new IpcpPeerResult(
            outbound,
            previous,
            State,
            previous != IpcpState.Opened && State == IpcpState.Opened,
            terminationRequested,
            failed,
            ignored,
            error);
    }
}
