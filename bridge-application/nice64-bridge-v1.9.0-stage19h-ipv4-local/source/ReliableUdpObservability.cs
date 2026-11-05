namespace PicoSwitchController;

using System.Diagnostics;

public sealed record Nice64BoundedHistogramSnapshot(
    long Count,
    long CountAbove40Milliseconds,
    double MinimumMicroseconds,
    double MeanMicroseconds,
    double LastMicroseconds,
    double P50Microseconds,
    double P95Microseconds,
    double P99Microseconds,
    double MaximumMicroseconds,
    IReadOnlyList<long> BucketUpperBoundsMicroseconds,
    IReadOnlyList<long> BucketCounts);

public sealed record Nice64RudpSequenceTimingSnapshot(
    uint Sequence,
    double AdmissionMicroseconds,
    double OriginalSendMicroseconds,
    double LastRetransmitMicroseconds,
    int RetransmissionCount,
    double AckMicroseconds,
    bool Acknowledged);

public sealed record Nice64RudpHostObservabilitySnapshot(
    Nice64BoundedHistogramSnapshot ServiceLoopGap,
    int OutgoingQueueDepth,
    int OutgoingQueueHighWater,
    Nice64BoundedHistogramSnapshot AdmissionToAck,
    Nice64BoundedHistogramSnapshot SendToAck,
    Nice64BoundedHistogramSnapshot ReceiveToDecode,
    Nice64BoundedHistogramSnapshot HandlerDuration,
    long StandaloneAckPackets,
    long DataAndAckPackets,
    long Retransmissions,
    byte LastControlOperation,
    uint LastControlSequence,
    double LastControlSendMicroseconds,
    double LastControlReceiveMicroseconds,
    string LastControlPhase,
    IReadOnlyList<Nice64RudpSequenceTimingSnapshot> RecentSequences,
    IReadOnlyList<Nice64RudpSequenceTimingSnapshot> ActiveSequences);

internal sealed class Nice64BoundedLatencyHistogram
{
    private static readonly long[] Limits =
    [
        100, 250, 500, 1_000, 2_000, 4_000, 8_000, 16_000,
        32_000, 64_000, 128_000, 256_000, 512_000, 1_000_000,
        long.MaxValue
    ];

    private readonly object _sync = new();
    private readonly long[] _counts = new long[Limits.Length];
    private long _count;
    private long _totalTicks;
    private long _minimumTicks = long.MaxValue;
    private long _maximumTicks;
    private long _lastTicks;
    private long _countAbove40Milliseconds;

    public void ObserveTicks(long ticks)
    {
        if (ticks < 0) return;
        long microseconds = TicksToMicrosecondsRoundedUp(ticks);
        lock (_sync)
        {
            _count++;
            _totalTicks += ticks;
            _lastTicks = ticks;
            _minimumTicks = Math.Min(_minimumTicks, ticks);
            _maximumTicks = Math.Max(_maximumTicks, ticks);
            if (microseconds > 40_000) _countAbove40Milliseconds++;
            int bucket = Array.FindIndex(Limits, limit => microseconds <= limit);
            _counts[bucket < 0 ? _counts.Length - 1 : bucket]++;
        }
    }

    public Nice64BoundedHistogramSnapshot Snapshot()
    {
        lock (_sync)
        {
            long[] counts = (long[])_counts.Clone();
            long[] limits = (long[])Limits.Clone();
            if (_count == 0)
                return new(0, 0, 0, 0, 0, 0, 0, 0, 0, limits, counts);
            return new(
                _count,
                _countAbove40Milliseconds,
                TicksToMicroseconds(_minimumTicks),
                TicksToMicroseconds(_totalTicks) / _count,
                TicksToMicroseconds(_lastTicks),
                Percentile(counts, limits, 0.50),
                Percentile(counts, limits, 0.95),
                Percentile(counts, limits, 0.99),
                TicksToMicroseconds(_maximumTicks),
                limits,
                counts);
        }
    }

    private static double Percentile(long[] counts, long[] limits, double percentile)
    {
        long total = counts.Sum();
        long target = Math.Max(1, (long)Math.Ceiling(total * percentile));
        long cumulative = 0;
        for (int index = 0; index < counts.Length; index++)
        {
            cumulative += counts[index];
            if (cumulative >= target)
                return limits[index] == long.MaxValue
                    ? limits[^2]
                    : limits[index];
        }
        return limits[^2];
    }

    internal static double TicksToMicroseconds(long ticks) =>
        ticks * 1_000_000.0 / Stopwatch.Frequency;

    private static long TicksToMicrosecondsRoundedUp(long ticks) =>
        (long)Math.Ceiling(TicksToMicroseconds(ticks));
}

public sealed class Nice64RudpHostObservability
{
    private const int SequenceSlots = 16;
    private static readonly Nice64BoundedHistogramSnapshot EmptyHistogram = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<long>(), Array.Empty<long>());
    private static readonly Nice64RudpHostObservabilitySnapshot EmptySnapshot = new(
        EmptyHistogram, 0, 0, EmptyHistogram, EmptyHistogram, EmptyHistogram,
        EmptyHistogram, 0, 0, 0, 0, 0, 0, 0, "Disabled",
        Array.Empty<Nice64RudpSequenceTimingSnapshot>(),
        Array.Empty<Nice64RudpSequenceTimingSnapshot>());
    private readonly object _sync = new();
    private readonly bool _enabled;
    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly SequenceTiming[]? _active;
    private readonly SequenceTiming[]? _recent;
    private readonly Nice64BoundedLatencyHistogram? _serviceLoopGap;
    private readonly Nice64BoundedLatencyHistogram? _admissionToAck;
    private readonly Nice64BoundedLatencyHistogram? _sendToAck;
    private readonly Nice64BoundedLatencyHistogram? _receiveToDecode;
    private readonly Nice64BoundedLatencyHistogram? _handlerDuration;
    private long _lastLoopTicks;
    private int _queueDepth;
    private int _queueHighWater;
    private int _recentNext;
    private long _standaloneAcks;
    private long _dataAndAcks;
    private long _retransmissions;
    private byte _lastControlOperation;
    private uint _lastControlSequence;
    private long _lastControlSendTicks;
    private long _lastControlReceiveTicks;
    private string _lastControlPhase = "None";

    public Nice64RudpHostObservability(bool enabled = true)
    {
        _enabled = enabled;
        if (!enabled) return;
        _active = Enumerable.Range(0, SequenceSlots)
            .Select(_ => new SequenceTiming()).ToArray();
        _recent = Enumerable.Range(0, SequenceSlots)
            .Select(_ => new SequenceTiming()).ToArray();
        _serviceLoopGap = new();
        _admissionToAck = new();
        _sendToAck = new();
        _receiveToDecode = new();
        _handlerDuration = new();
    }

    public void RecordServiceLoop()
    {
        if (!_enabled) return;
        long now = Stopwatch.GetTimestamp();
        long previous = Interlocked.Exchange(ref _lastLoopTicks, now);
        if (previous != 0) _serviceLoopGap!.ObserveTicks(now - previous);
    }

    public void RecordQueueEnqueue()
    {
        if (!_enabled) return;
        int depth = Interlocked.Increment(ref _queueDepth);
        int observed;
        while (depth > (observed = Volatile.Read(ref _queueHighWater)) &&
               Interlocked.CompareExchange(ref _queueHighWater, depth, observed) != observed)
        {
        }
    }

    public void RecordQueueDequeue()
    {
        if (_enabled) Interlocked.Decrement(ref _queueDepth);
    }

    public void RecordAdmission(uint sequence)
    {
        if (!_enabled) return;
        long now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            SequenceTiming timing = SlotFor(sequence);
            timing.Reset(sequence, now);
        }
    }

    public void RecordTransmission(uint sequence, bool retransmission, int retransmissionCount)
    {
        if (!_enabled) return;
        long now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            SequenceTiming timing = SlotFor(sequence);
            if (!timing.Active || timing.Sequence != sequence) timing.Reset(sequence, now);
            if (!retransmission && timing.OriginalSendTicks == 0)
                timing.OriginalSendTicks = now;
            if (retransmission)
            {
                timing.LastRetransmitTicks = now;
                timing.RetransmissionCount = Math.Max(
                    timing.RetransmissionCount, retransmissionCount);
                Interlocked.Increment(ref _retransmissions);
            }
        }
    }

    public void RecordAck(Nice64RudpAck ack)
    {
        if (!_enabled) return;
        long now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            foreach (SequenceTiming timing in _active!)
            {
                if (!timing.Active || !Acknowledges(timing.Sequence, ack)) continue;
                timing.AckTicks = now;
                if (timing.AdmissionTicks != 0)
                    _admissionToAck!.ObserveTicks(now - timing.AdmissionTicks);
                if (timing.OriginalSendTicks != 0)
                    _sendToAck!.ObserveTicks(now - timing.OriginalSendTicks);
                CopyToRecent(timing);
                timing.Active = false;
            }
        }
    }

    public void RecordReceiveToDecode(long startedTicks)
    {
        if (_enabled)
            _receiveToDecode!.ObserveTicks(Stopwatch.GetTimestamp() - startedTicks);
    }

    public void RecordHandler(long startedTicks)
    {
        if (_enabled)
            _handlerDuration!.ObserveTicks(Stopwatch.GetTimestamp() - startedTicks);
    }

    public void RecordStandaloneAck()
    {
        if (_enabled) Interlocked.Increment(ref _standaloneAcks);
    }

    public void RecordDataAndAck()
    {
        if (_enabled) Interlocked.Increment(ref _dataAndAcks);
    }

    public void RecordControlSend(byte operation, uint sequence)
    {
        if (!_enabled) return;
        lock (_sync)
        {
            _lastControlOperation = operation;
            _lastControlSequence = sequence;
            _lastControlSendTicks = Stopwatch.GetTimestamp();
            _lastControlReceiveTicks = 0;
            _lastControlPhase = "WaitingForReply";
        }
    }

    public void RecordControlReceive()
    {
        if (!_enabled) return;
        lock (_sync)
        {
            _lastControlReceiveTicks = Stopwatch.GetTimestamp();
            _lastControlPhase = "ReplyReceived";
        }
    }

    public void RecordControlTimeout(byte expectedOperation)
    {
        if (!_enabled) return;
        lock (_sync)
            _lastControlPhase = $"TimeoutWaitingFor0x{expectedOperation:X2}";
    }

    public Nice64RudpHostObservabilitySnapshot Snapshot()
    {
        if (!_enabled) return EmptySnapshot;
        lock (_sync)
        {
            Nice64RudpSequenceTimingSnapshot[] recent = _recent!
                .Where(item => item.Valid)
                .Select(SnapshotSequence)
                .ToArray();
            Nice64RudpSequenceTimingSnapshot[] active = _active!
                .Where(item => item.Active)
                .Select(SnapshotSequence)
                .ToArray();
            return new(
                _serviceLoopGap!.Snapshot(),
                Volatile.Read(ref _queueDepth),
                Volatile.Read(ref _queueHighWater),
                _admissionToAck!.Snapshot(),
                _sendToAck!.Snapshot(),
                _receiveToDecode!.Snapshot(),
                _handlerDuration!.Snapshot(),
                Interlocked.Read(ref _standaloneAcks),
                Interlocked.Read(ref _dataAndAcks),
                Interlocked.Read(ref _retransmissions),
                _lastControlOperation,
                _lastControlSequence,
                SinceOrigin(_lastControlSendTicks),
                SinceOrigin(_lastControlReceiveTicks),
                _lastControlPhase,
                recent,
                active);
        }
    }

    private SequenceTiming SlotFor(uint sequence) =>
        _active![(int)(sequence % SequenceSlots)];

    private void CopyToRecent(SequenceTiming timing)
    {
        SequenceTiming destination = _recent![_recentNext++ % SequenceSlots];
        destination.CopyFrom(timing);
        destination.Active = false;
        destination.Valid = true;
    }

    private Nice64RudpSequenceTimingSnapshot SnapshotSequence(SequenceTiming timing) => new(
        timing.Sequence,
        SinceOrigin(timing.AdmissionTicks),
        SinceOrigin(timing.OriginalSendTicks),
        SinceOrigin(timing.LastRetransmitTicks),
        timing.RetransmissionCount,
        SinceOrigin(timing.AckTicks),
        timing.AckTicks != 0);

    private double SinceOrigin(long ticks) => ticks == 0
        ? 0
        : Nice64BoundedLatencyHistogram.TicksToMicroseconds(ticks - _origin);

    private static bool Acknowledges(uint sequence, Nice64RudpAck ack)
    {
        if (sequence == ack.AckBase || Nice64RudpSequence.IsBefore(sequence, ack.AckBase))
            return true;
        uint distance = Nice64RudpSequence.ForwardDistance(
            unchecked(ack.AckBase + 1u), sequence);
        return distance < 32u && (ack.AckBitmap & (1u << (int)distance)) != 0;
    }

    private sealed class SequenceTiming
    {
        public bool Active;
        public bool Valid;
        public uint Sequence;
        public long AdmissionTicks;
        public long OriginalSendTicks;
        public long LastRetransmitTicks;
        public int RetransmissionCount;
        public long AckTicks;

        public void Reset(uint sequence, long now)
        {
            Active = true;
            Valid = true;
            Sequence = sequence;
            AdmissionTicks = now;
            OriginalSendTicks = 0;
            LastRetransmitTicks = 0;
            RetransmissionCount = 0;
            AckTicks = 0;
        }

        public void CopyFrom(SequenceTiming source)
        {
            Sequence = source.Sequence;
            AdmissionTicks = source.AdmissionTicks;
            OriginalSendTicks = source.OriginalSendTicks;
            LastRetransmitTicks = source.LastRetransmitTicks;
            RetransmissionCount = source.RetransmissionCount;
            AckTicks = source.AckTicks;
        }
    }
}
