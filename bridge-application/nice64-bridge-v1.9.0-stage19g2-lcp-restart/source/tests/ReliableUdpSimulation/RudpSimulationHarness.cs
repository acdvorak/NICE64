using PicoSwitchController;

internal sealed record ImpairmentProfile(
    int Seed,
    double DataLoss = 0,
    double AckLoss = 0,
    double DuplicateData = 0,
    double DuplicateAck = 0,
    int MaximumOneWayDelayMs = 1,
    uint? DropFirstSequence = null,
    uint? DropEverySequence = null,
    bool DropFirstAck = false);

internal sealed record SimulationResult(
    bool Completed,
    bool Failed,
    byte[] Output,
    int ElapsedMs,
    int UpperDeliveries,
    int MaximumQueuedDatagrams,
    IReadOnlyDictionary<uint, int> DataSendCounts,
    Nice64RudpDiagnostics Tx,
    Nice64RudpDiagnostics Rx);

internal sealed class RudpSimulationHarness
{
    private sealed record Scheduled(long DueMs, long Order, byte[] Wire);

    private readonly Nice64RudpConfiguration _configuration;
    private readonly ImpairmentProfile _impairment;
    private readonly Random _random;
    private readonly List<Scheduled> _dataQueue = [];
    private readonly List<Scheduled> _ackQueue = [];
    private readonly Dictionary<uint, int> _dataSendCounts = [];
    private long _order;
    private bool _firstAckSeen;

    public RudpSimulationHarness(Nice64RudpConfiguration configuration,
                                 ImpairmentProfile impairment)
    {
        _configuration = configuration.Validate();
        _impairment = impairment;
        _random = new Random(impairment.Seed);
    }

    public SimulationResult Run(byte[] input, uint sessionId = 0x10203040,
                                uint initialSequence = 0, int maximumMilliseconds = 300_000)
    {
        using MemoryStream delivered = new();
        int deliveryCount = 0;
        Nice64RudpTransmitWindow sender = new(_configuration, sessionId, initialSequence);
        Nice64RudpReceiveWindow receiver = new(_configuration, sessionId, (_, payload) =>
        {
            delivered.Write(payload.Span);
            deliveryCount++;
        }, initialSequence);

        int inputOffset = 0;
        int maximumQueued = 0;
        for (int now = 0; now <= maximumMilliseconds; now++)
        {
            while (inputOffset < input.Length && !sender.IsFull && !sender.Failed)
            {
                int count = Math.Min(_configuration.MaximumPayloadBytes, input.Length - inputOffset);
                if (!sender.TryEnqueue(input.AsSpan(inputOffset, count), out _)) break;
                inputOffset += count;
            }

            foreach (Nice64RudpTransmission transmission in sender.CollectDue(now))
                ScheduleData(transmission, sender.SessionId, now);

            DeliverData(now, receiver);
            DeliverAcks(now, sender);
            maximumQueued = Math.Max(maximumQueued, _dataQueue.Count + _ackQueue.Count);

            bool allInputAdmitted = inputOffset == input.Length;
            if (allInputAdmitted && sender.IsEmpty && _dataQueue.Count == 0 && _ackQueue.Count == 0)
            {
                return new(true, false, delivered.ToArray(), now, deliveryCount,
                    maximumQueued, new Dictionary<uint, int>(_dataSendCounts),
                    sender.Diagnostics, receiver.Diagnostics);
            }
            if (sender.Failed)
            {
                return new(false, true, delivered.ToArray(), now, deliveryCount,
                    maximumQueued, new Dictionary<uint, int>(_dataSendCounts),
                    sender.Diagnostics, receiver.Diagnostics);
            }
        }

        return new(false, sender.Failed, delivered.ToArray(), maximumMilliseconds,
            deliveryCount, maximumQueued, new Dictionary<uint, int>(_dataSendCounts),
            sender.Diagnostics, receiver.Diagnostics);
    }

    private void ScheduleData(Nice64RudpTransmission transmission, uint sessionId, long now)
    {
        _dataSendCounts[transmission.Sequence] =
            _dataSendCounts.GetValueOrDefault(transmission.Sequence) + 1;
        int sendCount = _dataSendCounts[transmission.Sequence];
        bool forcedFirstDrop = _impairment.DropFirstSequence == transmission.Sequence && sendCount == 1;
        bool forcedEveryDrop = _impairment.DropEverySequence == transmission.Sequence;
        if (forcedFirstDrop || forcedEveryDrop || Chance(_impairment.DataLoss)) return;

        Nice64RudpHeader header = Header(Nice64RudpPacketFlags.Data, sessionId,
            transmission.Sequence, 0, 0, (ushort)transmission.Payload.Length);
        byte[] wire = Nice64RudpPacketCodec.Encode(header, transmission.Payload,
            _configuration.MaximumPayloadBytes);
        Schedule(_dataQueue, wire, now);
        if (Chance(_impairment.DuplicateData)) Schedule(_dataQueue, wire.ToArray(), now + 1);
    }

    private void DeliverData(long now, Nice64RudpReceiveWindow receiver)
    {
        foreach (Scheduled scheduled in TakeDue(_dataQueue, now))
        {
            Nice64RudpReceiveResult result = receiver.ReceiveWire(
                scheduled.Wire, out Nice64RudpValidationError error);
            if (result is Nice64RudpReceiveResult.InvalidPacket or
                Nice64RudpReceiveResult.InvalidDeliveryClass or
                Nice64RudpReceiveResult.UnsupportedDeliveryClass or
                Nice64RudpReceiveResult.SessionMismatch)
                throw new InvalidOperationException($"simulated data decode failed: {error}");
            Nice64RudpAck ack = receiver.CreateAck();
            ScheduleAck(ack, receiver.SessionId, now);
        }
    }

    private void ScheduleAck(Nice64RudpAck ack, uint sessionId, long now)
    {
        bool forcedDrop = _impairment.DropFirstAck && !_firstAckSeen;
        _firstAckSeen = true;
        if (forcedDrop || Chance(_impairment.AckLoss)) return;

        Nice64RudpHeader header = Header(Nice64RudpPacketFlags.Ack, sessionId,
            0, ack.AckBase, ack.AckBitmap, 0);
        byte[] wire = Nice64RudpPacketCodec.Encode(header, [],
            _configuration.MaximumPayloadBytes);
        Schedule(_ackQueue, wire, now);
        if (Chance(_impairment.DuplicateAck)) Schedule(_ackQueue, wire.ToArray(), now + 1);
    }

    private void DeliverAcks(long now, Nice64RudpTransmitWindow sender)
    {
        foreach (Scheduled scheduled in TakeDue(_ackQueue, now))
        {
            if (!Nice64RudpPacketCodec.TryDecode(scheduled.Wire,
                    _configuration.MaximumPayloadBytes, sender.SessionId,
                    out Nice64RudpPacket? packet, out Nice64RudpValidationError error))
                throw new InvalidOperationException($"simulated ACK decode failed: {error}");
            sender.ApplyAck(new(packet!.Header.AckBase, packet.Header.AckBitmap));
        }
    }

    private void Schedule(List<Scheduled> queue, byte[] wire, long now)
    {
        int maximumDelay = Math.Max(1, _impairment.MaximumOneWayDelayMs);
        int delay = _random.Next(1, maximumDelay + 1);
        queue.Add(new(now + delay, _order++, wire));
    }

    private static IReadOnlyList<Scheduled> TakeDue(List<Scheduled> queue, long now)
    {
        List<Scheduled> due = queue.Where(item => item.DueMs <= now)
            .OrderBy(item => item.DueMs).ThenBy(item => item.Order).ToList();
        if (due.Count != 0) queue.RemoveAll(item => item.DueMs <= now);
        return due;
    }

    private bool Chance(double probability) =>
        probability > 0 && _random.NextDouble() < probability;

    private static Nice64RudpHeader Header(
        Nice64RudpPacketFlags flags, uint sessionId, uint sequence,
        uint ackBase, uint ackBitmap, ushort payloadLength) => new(
            Nice64WirelessTransportContract.Magic,
            Nice64WirelessTransportContract.TransportVersion,
            flags,
            (byte)Nice64WirelessDeliveryClass.ReliableOrdered,
            Nice64WirelessTransportContract.HeaderLength,
            sessionId,
            sequence,
            ackBase,
            ackBitmap,
            payloadLength,
            0);
}
