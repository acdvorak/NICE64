namespace PicoSwitchController;

public sealed record Nice64RudpConfiguration(
    int WindowSize = 8,
    int MaximumPayloadBytes = 512,
    int RetransmissionTimeoutMs = 10,
    int MaximumRetransmissions = 4)
{
    public Nice64RudpConfiguration Validate()
    {
        if (WindowSize is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(WindowSize));
        if (MaximumPayloadBytes is < 1 or > 1400) throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes));
        if (RetransmissionTimeoutMs < 1) throw new ArgumentOutOfRangeException(nameof(RetransmissionTimeoutMs));
        if (MaximumRetransmissions is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(MaximumRetransmissions));
        return this;
    }
}

public sealed record Nice64RudpMemoryProjection(
    int PayloadStorageBytes,
    int TxMetadataBytes,
    int RxMetadataBytes,
    int SessionAckConfigurationBytes,
    int DiagnosticCounterBytes,
    int TotalProjectedBytes)
{
    public static Nice64RudpMemoryProjection For(Nice64RudpConfiguration configuration)
    {
        configuration.Validate();
        int payload = 2 * configuration.WindowSize * configuration.MaximumPayloadBytes;
        int txMetadata = configuration.WindowSize * 16;
        int rxMetadata = configuration.WindowSize * 8;
        const int sessionAckConfiguration = 64;
        const int diagnosticCounters = 18 * sizeof(uint);
        return new(payload, txMetadata, rxMetadata, sessionAckConfiguration,
            diagnosticCounters,
            payload + txMetadata + rxMetadata + sessionAckConfiguration + diagnosticCounters);
    }
}

public sealed record Nice64RudpTransmission(
    uint Sequence,
    byte[] Payload,
    bool IsRetransmission,
    int RetransmissionCount);

public readonly record struct Nice64RudpAck(uint AckBase, uint AckBitmap);

public sealed class Nice64RudpTransmitWindow
{
    private sealed class Slot
    {
        public Slot(int capacity) => Payload = new byte[capacity];
        public bool Active;
        public bool Sent;
        public bool SelectivelyAcknowledged;
        public uint Sequence;
        public byte[] Payload { get; }
        public int PayloadLength;
        public long LastSentMs;
        public int Retransmissions;
    }

    private readonly Nice64RudpConfiguration _configuration;
    private readonly Slot[] _slots;
    private uint _baseSequence;
    private uint _nextSequence;
    private bool _failureReported;

    public Nice64RudpTransmitWindow(
        Nice64RudpConfiguration configuration,
        uint sessionId,
        uint initialSequence = 0)
    {
        _configuration = configuration.Validate();
        _slots = Enumerable.Range(0, configuration.WindowSize)
            .Select(_ => new Slot(configuration.MaximumPayloadBytes)).ToArray();
        Diagnostics = new Nice64RudpDiagnostics();
        Reset(sessionId, initialSequence, countReset: false);
    }

    public uint SessionId { get; private set; }
    public uint BaseSequence => _baseSequence;
    public uint NextSequence => _nextSequence;
    public bool Failed { get; private set; }
    public bool IsFull => Nice64RudpSequence.ForwardDistance(_baseSequence, _nextSequence) >=
                          (uint)_configuration.WindowSize;
    public bool IsEmpty => _baseSequence == _nextSequence;
    public Nice64RudpDiagnostics Diagnostics { get; }

    public bool TryEnqueue(ReadOnlySpan<byte> payload, out uint sequence)
    {
        sequence = _nextSequence;
        if (Failed || IsFull)
        {
            if (IsFull) Diagnostics.WindowFullEvents++;
            return false;
        }
        if (payload.Length > _configuration.MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload));

        Slot slot = _slots[(int)(_nextSequence % (uint)_slots.Length)];
        if (slot.Active) throw new InvalidOperationException("TX slot collision.");
        slot.Active = true;
        slot.Sent = false;
        slot.SelectivelyAcknowledged = false;
        slot.Sequence = _nextSequence;
        payload.CopyTo(slot.Payload);
        slot.PayloadLength = payload.Length;
        slot.LastSentMs = 0;
        slot.Retransmissions = 0;
        _nextSequence = unchecked(_nextSequence + 1);
        return true;
    }

    public IReadOnlyList<Nice64RudpTransmission> CollectDue(long nowMs)
    {
        if (Failed) return [];
        List<Nice64RudpTransmission> due = [];
        uint outstanding = Nice64RudpSequence.ForwardDistance(_baseSequence, _nextSequence);
        for (uint distance = 0; distance < outstanding; distance++)
        {
            uint sequence = unchecked(_baseSequence + distance);
            Slot slot = SlotFor(sequence);
            if (!slot.Active || slot.Sequence != sequence || slot.SelectivelyAcknowledged) continue;

            bool retransmission = slot.Sent;
            if (retransmission && nowMs - slot.LastSentMs < _configuration.RetransmissionTimeoutMs)
                continue;
            if (retransmission && slot.Retransmissions >= _configuration.MaximumRetransmissions)
            {
                Failed = true;
                if (!_failureReported)
                {
                    Diagnostics.RetryExhaustion++;
                    _failureReported = true;
                }
                return [];
            }

            if (retransmission)
            {
                slot.Retransmissions++;
                Diagnostics.Retransmissions++;
                Diagnostics.MaxRetryCount = Math.Max(Diagnostics.MaxRetryCount, slot.Retransmissions);
            }
            slot.Sent = true;
            slot.LastSentMs = nowMs;
            Diagnostics.TxDatagrams++;
            Diagnostics.TxPayloadBytes += slot.PayloadLength;
            due.Add(new(sequence, slot.Payload.AsSpan(0, slot.PayloadLength).ToArray(),
                retransmission, slot.Retransmissions));
        }
        return due;
    }

    public void ApplyAck(Nice64RudpAck ack)
    {
        Diagnostics.AcksReceived++;
        uint outstanding = Nice64RudpSequence.ForwardDistance(_baseSequence, _nextSequence);
        if (outstanding == 0) return;

        uint sequenceBeforeBase = unchecked(_baseSequence - 1);
        uint cumulativeCount = Nice64RudpSequence.ForwardDistance(sequenceBeforeBase, ack.AckBase);
        if (cumulativeCount <= outstanding)
        {
            for (uint distance = 0; distance < cumulativeCount; distance++)
            {
                uint sequence = unchecked(_baseSequence + distance);
                ClearSlot(sequence);
            }
            _baseSequence = unchecked(_baseSequence + cumulativeCount);
        }
        else if (ack.AckBase != sequenceBeforeBase)
        {
            return; // stale or impossible cumulative ACK; ignore its bitmap too
        }

        for (int bit = 0; bit < 32; bit++)
        {
            if ((ack.AckBitmap & (1u << bit)) == 0) continue;
            uint sequence = unchecked(ack.AckBase + 1u + (uint)bit);
            uint distance = Nice64RudpSequence.ForwardDistance(_baseSequence, sequence);
            uint remaining = Nice64RudpSequence.ForwardDistance(_baseSequence, _nextSequence);
            if (distance >= remaining) continue;
            Slot slot = SlotFor(sequence);
            if (slot.Active && slot.Sequence == sequence)
                slot.SelectivelyAcknowledged = true;
        }
    }

    public void Reset(uint sessionId, uint initialSequence = 0) =>
        Reset(sessionId, initialSequence, countReset: true);

    private void Reset(uint sessionId, uint initialSequence, bool countReset)
    {
        foreach (Slot slot in _slots)
        {
            slot.Active = false;
            slot.PayloadLength = 0;
        }
        SessionId = sessionId;
        _baseSequence = initialSequence;
        _nextSequence = initialSequence;
        Failed = false;
        _failureReported = false;
        if (countReset) Diagnostics.SessionResets++;
    }

    private Slot SlotFor(uint sequence) => _slots[(int)(sequence % (uint)_slots.Length)];

    private void ClearSlot(uint sequence)
    {
        Slot slot = SlotFor(sequence);
        if (!slot.Active || slot.Sequence != sequence) return;
        slot.Active = false;
        slot.Sent = false;
        slot.SelectivelyAcknowledged = false;
        slot.PayloadLength = 0;
    }
}

public enum Nice64RudpReceiveResult
{
    Accepted,
    Duplicate,
    AlreadyDelivered,
    OutsideWindow,
    SessionMismatch,
    UnsupportedDeliveryClass,
    InvalidDeliveryClass,
    InvalidPacket
}

public sealed class Nice64RudpReceiveWindow
{
    private sealed class Slot
    {
        public Slot(int capacity) => Payload = new byte[capacity];
        public bool Active;
        public uint Sequence;
        public byte[] Payload { get; }
        public int PayloadLength;
    }

    private readonly Nice64RudpConfiguration _configuration;
    private readonly Slot[] _slots;
    private readonly Action<uint, ReadOnlyMemory<byte>> _deliver;

    public Nice64RudpReceiveWindow(
        Nice64RudpConfiguration configuration,
        uint sessionId,
        Action<uint, ReadOnlyMemory<byte>> deliver,
        uint initialSequence = 0)
    {
        _configuration = configuration.Validate();
        _slots = Enumerable.Range(0, configuration.WindowSize)
            .Select(_ => new Slot(configuration.MaximumPayloadBytes)).ToArray();
        _deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
        Diagnostics = new Nice64RudpDiagnostics();
        Reset(sessionId, initialSequence, countReset: false);
    }

    public uint SessionId { get; private set; }
    public uint NextExpected { get; private set; }
    public bool ConsumerPaused { get; set; }
    public Nice64RudpDiagnostics Diagnostics { get; }

    public Nice64RudpReceiveResult Receive(Nice64RudpPacket packet)
    {
        Diagnostics.RxDatagrams++;
        Nice64WirelessSelection selection =
            Nice64WirelessTransportContract.Select(packet.Header.DeliveryClass);
        if (selection.Result == Nice64WirelessSelectionResult.Unsupported)
        {
            Diagnostics.UnsupportedDeliveryClass++;
            return Nice64RudpReceiveResult.UnsupportedDeliveryClass;
        }
        if (selection.Result == Nice64WirelessSelectionResult.Invalid)
        {
            Diagnostics.InvalidDeliveryClass++;
            return Nice64RudpReceiveResult.InvalidDeliveryClass;
        }
        if (packet.Header.SessionId != SessionId)
        {
            Diagnostics.SessionMismatch++;
            return Nice64RudpReceiveResult.SessionMismatch;
        }
        if ((packet.Header.Flags & Nice64RudpPacketFlags.Data) == 0 ||
            packet.Payload.Length != packet.Header.PayloadLength ||
            packet.Payload.Length > _configuration.MaximumPayloadBytes)
        {
            Diagnostics.InvalidHeader++;
            return Nice64RudpReceiveResult.InvalidPacket;
        }

        uint distance = Nice64RudpSequence.ForwardDistance(NextExpected, packet.Header.Sequence);
        if (distance >= Nice64RudpSequence.HalfRange)
        {
            Diagnostics.OldOrStaleRx++;
            return Nice64RudpReceiveResult.AlreadyDelivered;
        }
        if (distance >= (uint)_configuration.WindowSize)
        {
            Diagnostics.WindowFullEvents++;
            return Nice64RudpReceiveResult.OutsideWindow;
        }

        Slot slot = SlotFor(packet.Header.Sequence);
        if (slot.Active)
        {
            if (slot.Sequence == packet.Header.Sequence)
            {
                Diagnostics.DuplicateRx++;
                return Nice64RudpReceiveResult.Duplicate;
            }
            Diagnostics.WindowFullEvents++;
            return Nice64RudpReceiveResult.OutsideWindow;
        }

        slot.Active = true;
        slot.Sequence = packet.Header.Sequence;
        packet.Payload.CopyTo(slot.Payload, 0);
        slot.PayloadLength = packet.Payload.Length;
        Diagnostics.RxPayloadBytes += slot.PayloadLength;
        if (distance > 0) Diagnostics.OutOfOrderRx++;
        if (!ConsumerPaused) Drain();
        return Nice64RudpReceiveResult.Accepted;
    }

    public Nice64RudpReceiveResult ReceiveWire(
        ReadOnlySpan<byte> wire,
        out Nice64RudpValidationError validationError)
    {
        if (Nice64RudpPacketCodec.TryDecode(wire,
                _configuration.MaximumPayloadBytes, SessionId,
                out Nice64RudpPacket? packet, out validationError))
            return Receive(packet!);

        Diagnostics.RxDatagrams++;
        switch (validationError)
        {
            case Nice64RudpValidationError.UnsupportedDeliveryClass:
                Diagnostics.UnsupportedDeliveryClass++;
                return Nice64RudpReceiveResult.UnsupportedDeliveryClass;
            case Nice64RudpValidationError.InvalidDeliveryClass:
                Diagnostics.InvalidDeliveryClass++;
                return Nice64RudpReceiveResult.InvalidDeliveryClass;
            case Nice64RudpValidationError.SessionMismatch:
                Diagnostics.SessionMismatch++;
                return Nice64RudpReceiveResult.SessionMismatch;
            default:
                Diagnostics.InvalidHeader++;
                return Nice64RudpReceiveResult.InvalidPacket;
        }
    }

    public int Drain()
    {
        if (ConsumerPaused) return 0;
        int delivered = 0;
        while (true)
        {
            Slot slot = SlotFor(NextExpected);
            if (!slot.Active || slot.Sequence != NextExpected) break;
            uint sequence = NextExpected;
            int payloadLength = slot.PayloadLength;
            slot.Active = false;
            NextExpected = unchecked(NextExpected + 1);
            _deliver(sequence, slot.Payload.AsMemory(0, payloadLength));
            slot.PayloadLength = 0;
            delivered++;
        }
        return delivered;
    }

    public Nice64RudpAck CreateAck()
    {
        uint ackBase = unchecked(NextExpected - 1);
        uint bitmap = 0;
        for (int bit = 0; bit < 32; bit++)
        {
            uint sequence = unchecked(NextExpected + (uint)bit);
            Slot slot = SlotFor(sequence);
            if (slot.Active && slot.Sequence == sequence) bitmap |= 1u << bit;
        }
        Diagnostics.AcksSent++;
        return new(ackBase, bitmap);
    }

    public void Reset(uint sessionId, uint initialSequence = 0) =>
        Reset(sessionId, initialSequence, countReset: true);

    private void Reset(uint sessionId, uint initialSequence, bool countReset)
    {
        foreach (Slot slot in _slots)
        {
            slot.Active = false;
            slot.PayloadLength = 0;
        }
        SessionId = sessionId;
        NextExpected = initialSequence;
        ConsumerPaused = false;
        if (countReset) Diagnostics.SessionResets++;
    }

    private Slot SlotFor(uint sequence) => _slots[(int)(sequence % (uint)_slots.Length)];
}
