namespace PicoSwitchController;

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

public sealed record Nice64RudpcImpairment(
    int? DropDataOnce = null,
    int? DropDataAlways = null,
    int? DropAckOnce = null,
    int? DropAckAlways = null,
    int? DuplicateData = null,
    int? DuplicateAck = null,
    int? ReorderWithNext = null,
    int PauseConsumerMilliseconds = 0,
    int? PausedConsumerFlightCap = null,
    int NewDataFlightCap = 2,
    bool CaptureControlTimeouts = false,
    Action<string>? Trace = null);

public sealed record Nice64RudpcTransferResult(
    bool Passed,
    byte Status,
    int ExpectedBytes,
    uint ActualOrderedBytes,
    uint ExpectedCrc32,
    uint ActualCrc32,
    uint UpperDeliveries,
    TimeSpan Elapsed,
    IReadOnlyList<int> RetransmittedOffsets,
    Nice64RudpDiagnostics HostDiagnostics,
    Nice64RudpcFirmwareDiagnostics FirmwareDiagnosticsBefore,
    Nice64RudpcFirmwareDiagnostics FirmwareDiagnostics,
    int MaximumOutstanding,
    uint FinalAckBase,
    uint FinalAckBitmap,
    uint FinalHostOutstanding,
    Nice64RudpcResumeEvidence? ResumeEvidence,
    Nice64RudpcPacingEvidence? PacingEvidence,
    bool DuplicateInjected,
    uint? DuplicateSequence,
    string ControlResult,
    bool FirmwareDiagnosticsAfterAvailable);

public sealed record Nice64RudpcResumeEvidence(
    bool StateAcknowledged,
    uint HostBaseBefore,
    uint HostOutstandingBefore,
    uint AckBaseAfterDrain,
    uint AckBitmapAfterDrain,
    uint HostOutstandingAfter);

public sealed record Nice64RudpcPacingEvidence(
    int NormalCap,
    int? PauseOverrideCap,
    int MaximumPausedOutstanding,
    int MaximumPostResumeOutstanding,
    bool NormalCapRestored,
    uint PausedAckBase,
    uint PausedAckBitmap);

public sealed class Nice64RudpcFlightCapController
{
    public Nice64RudpcFlightCapController(int normalCap, int? pauseOverrideCap)
    {
        Validate(normalCap, nameof(normalCap));
        if (pauseOverrideCap is int overrideCap)
            Validate(overrideCap, nameof(pauseOverrideCap));
        NormalCap = normalCap;
        PauseOverrideCap = pauseOverrideCap;
    }

    public int NormalCap { get; }
    public int? PauseOverrideCap { get; }
    public bool PauseOverrideActive { get; private set; }
    public int CurrentCap => PauseOverrideActive ? PauseOverrideCap!.Value : NormalCap;

    public void BeginPausedFill()
    {
        if (!PauseOverrideCap.HasValue)
            throw new InvalidOperationException("No paused-consumer flight cap was configured.");
        PauseOverrideActive = true;
    }

    public void CompleteResume() => PauseOverrideActive = false;

    private static void Validate(int cap, string parameterName)
    {
        if (cap is not (1 or 2 or 4 or 8))
            throw new ArgumentOutOfRangeException(parameterName,
                "Flight cap must be 1, 2, 4, or 8.");
    }
}

public sealed record Nice64RudpcFirmwareDiagnostics(
    IReadOnlyDictionary<string, uint> Values)
{
    public uint this[string name] => Values[name];

    public uint Delta(string name, Nice64RudpcFirmwareDiagnostics before)
    {
        uint current = this[name];
        uint previous = before[name];
        return current >= previous ? current - previous : current;
    }
}

public sealed record Nice64RudpcDuplicateAcceptanceResult(
    string Disposition,
    bool TransportPassed,
    bool InjectionAttempted,
    bool InjectionObserved,
    string FirmwareClassification);

public static class Nice64RudpcDuplicateAcceptance
{
    public static Nice64RudpcDuplicateAcceptanceResult Evaluate(
        bool duplicateInjected, uint firmwareRxDelta, long hostTxDatagrams,
        uint expectedBytes, uint actualBytes,
        uint expectedCrc, uint actualCrc,
        uint expectedDeliveries, uint actualDeliveries,
        uint duplicateUpperDeliveries, uint duplicateRxDelta,
        uint oldOrStaleDelta, uint retryExhaustionDelta,
        uint occupiedRxSlots)
    {
        bool transportPassed = actualBytes == expectedBytes &&
            actualCrc == expectedCrc && actualDeliveries == expectedDeliveries &&
            duplicateUpperDeliveries == 0 && retryExhaustionDelta == 0 &&
            occupiedRxSlots == 0;
        bool injectionObserved = duplicateInjected &&
            firmwareRxDelta > (ulong)Math.Max(0, hostTxDatagrams);
        string classification = duplicateRxDelta != 0 ? "buffered-duplicate" :
            oldOrStaleDelta != 0 ? "already-delivered-old/stale" :
            "not-distinguished-by-firmware-counters";
        string disposition = !transportPassed ? "FAIL" :
            !duplicateInjected ? "FAIL" :
            injectionObserved ? "PASS" : "INCONCLUSIVE";
        return new(disposition, transportPassed, duplicateInjected,
            injectionObserved, injectionObserved ? classification : "NOT_OBSERVED");
    }
}

public sealed class Nice64RudpcResumeAckTracker
{
    private readonly uint _controlSequence;
    private readonly byte _stateAckOperation;
    private readonly uint _baseBeforeResume;

    public Nice64RudpcResumeAckTracker(uint controlSequence,
        byte stateAckOperation, Nice64RudpTransmitWindow window)
    {
        _controlSequence = controlSequence;
        _stateAckOperation = stateAckOperation;
        _baseBeforeResume = window.BaseSequence;
        OutstandingBefore = Nice64RudpSequence.ForwardDistance(
            window.BaseSequence, window.NextSequence);
        CumulativeProgressObserved = window.IsEmpty;
    }

    public bool StateAcknowledged { get; private set; }
    public bool CumulativeProgressObserved { get; private set; }
    public uint LastAckBase { get; private set; }
    public uint LastAckBitmap { get; private set; }
    public uint PreDrainAckBase { get; private set; }
    public uint PreDrainAckBitmap { get; private set; }
    public uint BaseBeforeResume => _baseBeforeResume;
    public uint OutstandingBefore { get; }
    public uint OutstandingAfter { get; private set; }
    public bool Complete => StateAcknowledged && CumulativeProgressObserved;

    public void Observe(Nice64RudpPacket packet, Nice64RudpTransmitWindow window,
        Action<string>? trace = null)
    {
        if (packet.Header.Flags == Nice64RudpPacketFlags.Ack)
        {
            uint before = window.BaseSequence;
            Nice64RudpAck ack = new(packet.Header.AckBase, packet.Header.AckBitmap);
            if (ack.AckBase == _baseBeforeResume - 1u)
            {
                PreDrainAckBase = ack.AckBase;
                PreDrainAckBitmap = ack.AckBitmap;
            }
            window.ApplyAck(ack);
            LastAckBase = ack.AckBase;
            LastAckBitmap = ack.AckBitmap;
            CumulativeProgressObserved |= window.BaseSequence != _baseBeforeResume;
            OutstandingAfter = Nice64RudpSequence.ForwardDistance(
                window.BaseSequence, window.NextSequence);
            trace?.Invoke($"RESUME ACK base={ack.AckBase} bitmap=0x{ack.AckBitmap:X8} " +
                $"released={Nice64RudpSequence.ForwardDistance(before, window.BaseSequence)} " +
                $"host_base={window.BaseSequence} outstanding=" +
                $"{Nice64RudpSequence.ForwardDistance(window.BaseSequence, window.NextSequence)}");
        }
        else if (packet.Header.Sequence == _controlSequence &&
                 packet.Payload.Length != 0 &&
                 packet.Payload[0] == _stateAckOperation)
        {
            StateAcknowledged = true;
            trace?.Invoke("RESUME STATE_ACK received");
        }
    }
}

public sealed class Nice64RudpcSyntheticClient : IAsyncDisposable
{
    public const int DefaultPort = 6465;
    private const int MaximumPayload = 512;
    private const byte Hello = 0x01;
    private const byte Goodbye = 0x05;
    private const byte HelloAck = 0x81;
    private const byte Released = 0x85;
    private const byte StreamBegin = 0x10;
    private const byte StreamEnd = 0x11;
    private const byte StreamPause = 0x12;
    private const byte StreamResume = 0x13;
    private const byte RudpcStats = 0x16;
    private const byte StreamBeginAck = 0x90;
    private const byte StreamResult = 0x91;
    private const byte StreamStateAck = 0x92;
    private const byte RudpcStatsReply = 0x96;

    private static readonly string[] StatsNames =
    [
        "tx_datagrams", "rx_datagrams", "tx_payload_bytes", "rx_payload_bytes",
        "acks_sent", "acks_received", "retransmissions", "duplicate_rx",
        "out_of_order_rx", "old_or_stale_rx", "invalid_headers",
        "unsupported_class", "invalid_class", "session_mismatch",
        "tx_window_full", "rx_window_full", "staging_full", "retry_exhaustion",
        "session_resets", "maximum_retry_count", "rtt_sample_count",
        "rtt_total_ms", "rtt_max_ms", "fixed_engine_bytes", "stream_active",
        "stream_direction", "expected_bytes", "transferred_bytes",
        "upper_deliveries", "duplicate_upper_deliveries", "completed_transfers",
        "rx_occupied_slots", "last_staging_drop_sequence",
        "staging_drop_first32_bitmap", "last_ack_base", "last_ack_bitmap"
    ];

    private readonly UdpClient _udp;
    private uint _controlSequence;
    private bool _established;

    public Nice64RudpcSyntheticClient(string targetIp, int port = DefaultPort)
    {
        if (!IPAddress.TryParse(targetIp, out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("A literal manual IPv4 address is required.", nameof(targetIp));
        SessionId = RandomNonzeroUInt32();
        _controlSequence = RandomNonzeroUInt32();
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Connect(new IPEndPoint(address, port));
    }

    public uint SessionId { get; }

    public async Task EstablishAsync(TimeSpan timeout, CancellationToken token = default)
    {
        Nice64RudpPacket reply = await ExchangeControlAsync(new byte[] { Hello }, HelloAck, timeout, token);
        _established = reply.Payload[0] == HelloAck;
    }

    public async Task<Nice64RudpcTransferResult> UploadAsync(
        ReadOnlyMemory<byte> source, int rtoMs, uint initialSequence,
        Nice64RudpcImpairment? impairment = null,
        TimeSpan? timeout = null, CancellationToken token = default)
    {
        EnsureEstablished();
        impairment ??= new();
        Nice64RudpcFlightCapController flightCaps = new(
            impairment.NewDataFlightCap,
            impairment.PauseConsumerMilliseconds > 0
                ? impairment.PausedConsumerFlightCap
                : null);
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(30);
        Nice64RudpcFirmwareDiagnostics firmwareBefore =
            await GetDiagnosticsAsync(limit, token);
        uint crc = ComputeCrc32(source.Span);
        await BeginAsync(0, rtoMs, initialSequence, source.Length, crc, limit, token);
        if (impairment.PauseConsumerMilliseconds > 0)
        {
            await ExchangeControlAsync(new byte[] { StreamPause }, StreamStateAck, limit, token);
            flightCaps.BeginPausedFill();
            impairment.Trace?.Invoke($"FLIGHT CAP OVERRIDE normal_cap={flightCaps.NormalCap} " +
                $"pause_override_cap={flightCaps.CurrentCap}");
        }

        Nice64RudpConfiguration configuration = new(8, MaximumPayload, rtoMs, 4);
        Nice64RudpTransmitWindow window = new(configuration, SessionId, initialSequence);
        Stopwatch elapsed = Stopwatch.StartNew();
        int admitted = 0;
        bool droppedDataOnce = false;
        bool droppedAckOnce = false;
        bool resumed = impairment.PauseConsumerMilliseconds <= 0;
        HashSet<int> retransmittedOffsets = [];
        int maximumOutstanding = 0;
        int maximumPausedOutstanding = 0;
        int maximumPostResumeOutstanding = 0;
        uint finalAckBase = initialSequence - 1u;
        uint finalAckBitmap = 0u;
        uint pausedAckBase = 0u;
        uint pausedAckBitmap = 0u;
        Nice64RudpcResumeEvidence? resumeEvidence = null;
        bool duplicateInjected = false;
        uint? duplicateSequence = null;

        while ((admitted < source.Length || !window.IsEmpty) && !window.Failed)
        {
            token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed > limit) throw new TimeoutException("RUDP-C upload timed out.");
            while (admitted < source.Length && !window.IsFull &&
                   Nice64RudpSequence.ForwardDistance(window.BaseSequence,
                       window.NextSequence) < (uint)flightCaps.CurrentCap)
            {
                int length = Math.Min(MaximumPayload, source.Length - admitted);
                if (!window.TryEnqueue(source.Span.Slice(admitted, length), out uint sequence)) break;
                admitted += length;
                impairment.Trace?.Invoke($"ENQUEUE seq={sequence} base={window.BaseSequence} " +
                    $"next={window.NextSequence} outstanding=" +
                    $"{Nice64RudpSequence.ForwardDistance(window.BaseSequence, window.NextSequence)}");
            }
            maximumOutstanding = Math.Max(maximumOutstanding,
                checked((int)Nice64RudpSequence.ForwardDistance(window.BaseSequence,
                    window.NextSequence)));
            int currentOutstanding = checked((int)Nice64RudpSequence.ForwardDistance(
                window.BaseSequence, window.NextSequence));
            if (flightCaps.PauseOverrideActive)
                maximumPausedOutstanding = Math.Max(maximumPausedOutstanding, currentOutstanding);
            else
                maximumPostResumeOutstanding = Math.Max(maximumPostResumeOutstanding, currentOutstanding);

            List<Nice64RudpTransmission> due = window.CollectDue(elapsed.ElapsedMilliseconds).ToList();
            if (impairment.ReorderWithNext is int reorder)
            {
                int at = due.FindIndex(x => Relative(initialSequence, x.Sequence) == reorder);
                if (at >= 0 && at + 1 < due.Count) (due[at], due[at + 1]) = (due[at + 1], due[at]);
            }
            foreach (Nice64RudpTransmission transmission in due)
            {
                int relative = Relative(initialSequence, transmission.Sequence);
                if (transmission.IsRetransmission) retransmittedOffsets.Add(relative);
                impairment.Trace?.Invoke($"SEND {(transmission.IsRetransmission ? "RETRANSMIT" : "ORIGINAL")} " +
                    $"seq={transmission.Sequence} offset={relative} base={window.BaseSequence} " +
                    $"next={window.NextSequence} outstanding=" +
                    $"{Nice64RudpSequence.ForwardDistance(window.BaseSequence, window.NextSequence)}");
                bool drop = impairment.DropDataAlways == relative ||
                            (impairment.DropDataOnce == relative && !droppedDataOnce);
                if (impairment.DropDataOnce == relative) droppedDataOnce = true;
                if (!drop)
                {
                    byte[] wire = CreateWire(transmission.Payload, transmission.Sequence,
                        Nice64RudpPacketFlags.Data | Nice64RudpPacketFlags.Ack, 0, 0);
                    await _udp.SendAsync(wire, token);
                    if (impairment.DuplicateData == relative)
                    {
                        duplicateInjected = true;
                        duplicateSequence = transmission.Sequence;
                        impairment.Trace?.Invoke($"DUPLICATE INJECTION seq={transmission.Sequence} " +
                            $"offset={relative} timing=immediate-before-host-ack-observation");
                        await _udp.SendAsync(wire, token);
                    }
                }
            }

            if (!resumed && elapsed.ElapsedMilliseconds >= impairment.PauseConsumerMilliseconds)
            {
                pausedAckBase = finalAckBase;
                pausedAckBitmap = finalAckBitmap;
                Nice64RudpcResumeAckTracker resume = await ResumeAndApplyAckAsync(
                    window, limit, impairment.Trace, token);
                if (resume.PreDrainAckBitmap != 0)
                {
                    pausedAckBase = resume.PreDrainAckBase;
                    pausedAckBitmap = resume.PreDrainAckBitmap;
                }
                finalAckBase = resume.LastAckBase;
                finalAckBitmap = resume.LastAckBitmap;
                resumeEvidence = new(resume.StateAcknowledged,
                    resume.BaseBeforeResume, resume.OutstandingBefore,
                    resume.LastAckBase, resume.LastAckBitmap,
                    resume.OutstandingAfter);
                resumed = true;
                flightCaps.CompleteResume();
                impairment.Trace?.Invoke($"FLIGHT CAP RESTORED normal_cap={flightCaps.NormalCap} " +
                    $"post_resume_outstanding={resume.OutstandingAfter}");
            }

            Nice64RudpPacket? packet = await ReceiveOneAsync(TimeSpan.FromMilliseconds(2), token);
            if (packet is not null && packet.Header.Flags == Nice64RudpPacketFlags.Ack)
            {
                int ackIndex = Relative(initialSequence, unchecked(packet.Header.AckBase + 1));
                bool drop = impairment.DropAckAlways == ackIndex ||
                            (impairment.DropAckOnce == ackIndex && !droppedAckOnce);
                if (impairment.DropAckOnce == ackIndex) droppedAckOnce = true;
                if (!drop)
                {
                    uint before = window.BaseSequence;
                    Nice64RudpAck ack = new(packet.Header.AckBase, packet.Header.AckBitmap);
                    window.ApplyAck(ack);
                    finalAckBase = ack.AckBase;
                    finalAckBitmap = ack.AckBitmap;
                    impairment.Trace?.Invoke($"ACK base={ack.AckBase} bitmap=0x{ack.AckBitmap:X8} " +
                        $"released={Nice64RudpSequence.ForwardDistance(before, window.BaseSequence)} " +
                        $"host_base={window.BaseSequence} outstanding=" +
                        $"{Nice64RudpSequence.ForwardDistance(window.BaseSequence, window.NextSequence)}");
                    if (impairment.DuplicateAck == ackIndex) window.ApplyAck(ack);
                }
            }
        }

        Nice64RudpPacket? resultPacket = null;
        try
        {
            if (window.Failed)
            {
                impairment.Trace?.Invoke($"UPLOAD TERMINAL FAILURE sequence={window.BaseSequence} " +
                    $"retry_exhaustion={window.Diagnostics.RetryExhaustion != 0} " +
                    $"outstanding={Nice64RudpSequence.ForwardDistance(window.BaseSequence, window.NextSequence)}");
            }
            resultPacket = await EndAsync(source.Length, crc, limit, token);
        }
        catch (TimeoutException) when (impairment.CaptureControlTimeouts)
        {
            bool dataAcknowledged = !window.Failed && admitted == source.Length && window.IsEmpty;
            uint acknowledgedBytes = dataAcknowledged ? (uint)source.Length : 0u;
            uint acknowledgedCrc = dataAcknowledged ? crc : 0u;
            uint acknowledgedDeliveries = dataAcknowledged
                ? (uint)((source.Length + MaximumPayload - 1) / MaximumPayload) : 0u;
            return new(dataAcknowledged, dataAcknowledged ? (byte)0 : (byte)1,
                source.Length, acknowledgedBytes, crc, acknowledgedCrc,
                acknowledgedDeliveries, elapsed.Elapsed,
                retransmittedOffsets.Order().ToArray(), window.Diagnostics,
                firmwareBefore, firmwareBefore, maximumOutstanding, finalAckBase,
                finalAckBitmap, Nice64RudpSequence.ForwardDistance(window.BaseSequence,
                    window.NextSequence), resumeEvidence,
                new(flightCaps.NormalCap, flightCaps.PauseOverrideCap,
                    maximumPausedOutstanding, maximumPostResumeOutstanding,
                    !flightCaps.PauseOverrideActive, pausedAckBase, pausedAckBitmap), duplicateInjected,
                duplicateSequence, "TIMEOUT_0x91", false);
        }
        catch (TimeoutException ex) when (window.Failed)
        {
            throw new InvalidOperationException(
                $"UPLOAD TERMINAL FAILURE: sequence={window.BaseSequence} " +
                $"retry_exhaustion=True outstanding=" +
                $"{Nice64RudpSequence.ForwardDistance(window.BaseSequence, window.NextSequence)}; " +
                "cleanup STREAM_END/control attempt timed out waiting for 0x91.", ex);
        }
        Nice64RudpcFirmwareDiagnostics firmware;
        string controlResult = "PASS";
        bool firmwareAfterAvailable = true;
        try
        {
            firmware = await GetDiagnosticsAsync(limit, token);
        }
        catch (TimeoutException) when (impairment.CaptureControlTimeouts)
        {
            firmware = firmwareBefore;
            controlResult = "TIMEOUT_0x96";
            firmwareAfterAvailable = false;
        }
        return ParseResult(resultPacket!, source.Length, crc, elapsed.Elapsed,
                           retransmittedOffsets.Order().ToArray(), window.Diagnostics,
                           firmware, maximumOutstanding, finalAckBase, finalAckBitmap,
                           Nice64RudpSequence.ForwardDistance(window.BaseSequence,
                               window.NextSequence), resumeEvidence,
                           new(flightCaps.NormalCap, flightCaps.PauseOverrideCap,
                               maximumPausedOutstanding, maximumPostResumeOutstanding,
                               !flightCaps.PauseOverrideActive,
                               pausedAckBase, pausedAckBitmap),
                           firmwareBefore, duplicateInjected, duplicateSequence,
                           controlResult, firmwareAfterAvailable);
    }

    public async Task<Nice64RudpcTransferResult> DownloadAsync(
        int byteCount, int rtoMs, uint initialSequence,
        Nice64RudpcImpairment? impairment = null,
        TimeSpan? timeout = null, CancellationToken token = default)
    {
        EnsureEstablished();
        impairment ??= new();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(30);
        Nice64RudpcFirmwareDiagnostics firmwareBefore =
            await GetDiagnosticsAsync(limit, token);
        byte[] expected = CreatePattern(byteCount);
        uint expectedCrc = ComputeCrc32(expected);
        await BeginAsync(1, rtoMs, initialSequence, byteCount, expectedCrc, limit, token);

        using MemoryStream received = new(byteCount);
        Nice64RudpConfiguration configuration = new(8, MaximumPayload, rtoMs, 4);
        Nice64RudpReceiveWindow window = new(configuration, SessionId,
            (_, payload) => received.Write(payload.Span), initialSequence);
        Stopwatch elapsed = Stopwatch.StartNew();
        bool droppedDataOnce = false;
        bool droppedAckOnce = false;
        Nice64RudpPacket? held = null;

        while (received.Length < byteCount)
        {
            token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed > limit) break;
            Nice64RudpPacket? packet = await ReceiveOneAsync(TimeSpan.FromMilliseconds(20), token);
            if (packet is null ||
                (packet.Header.Flags & Nice64RudpPacketFlags.Data) == 0 ||
                packet.Payload.Length == 0) continue;
            int relative = Relative(initialSequence, packet.Header.Sequence);
            bool drop = impairment.DropDataAlways == relative ||
                        (impairment.DropDataOnce == relative && !droppedDataOnce);
            if (impairment.DropDataOnce == relative) droppedDataOnce = true;
            if (drop) continue;

            if (impairment.ReorderWithNext == relative && held is null)
            {
                held = packet;
                continue;
            }
            droppedAckOnce = await AcceptAndAckAsync(packet, window, initialSequence,
                                                     impairment, droppedAckOnce, token);
            if (held is not null)
            {
                droppedAckOnce = await AcceptAndAckAsync(held, window, initialSequence,
                                                         impairment, droppedAckOnce, token);
                held = null;
            }
            if (impairment.DuplicateData == relative)
                window.Receive(packet);
        }

        uint actualCrc = ComputeCrc32(received.GetBuffer().AsSpan(0, (int)received.Length));
        Nice64RudpPacket? resultPacket;
        try
        {
            resultPacket = await EndAsync((int)received.Length, actualCrc, limit, token);
        }
        catch (TimeoutException) when (impairment.CaptureControlTimeouts)
        {
            bool dataComplete = received.Length == byteCount && actualCrc == expectedCrc;
            return new(dataComplete, dataComplete ? (byte)0 : (byte)1,
                byteCount, (uint)received.Length, expectedCrc, actualCrc,
                dataComplete ? (uint)((byteCount + MaximumPayload - 1) / MaximumPayload) : 0u,
                elapsed.Elapsed, Array.Empty<int>(), window.Diagnostics,
                firmwareBefore, firmwareBefore, 0, 0, 0, 0, null, null,
                false, null, "TIMEOUT_0x91", false);
        }
        Nice64RudpcFirmwareDiagnostics firmware;
        string controlResult = "PASS";
        bool firmwareAfterAvailable = true;
        try
        {
            firmware = await GetDiagnosticsAsync(limit, token);
        }
        catch (TimeoutException) when (impairment.CaptureControlTimeouts)
        {
            firmware = firmwareBefore;
            controlResult = "TIMEOUT_0x96";
            firmwareAfterAvailable = false;
        }
        return ParseResult(resultPacket, byteCount, expectedCrc, elapsed.Elapsed,
                           Array.Empty<int>(), window.Diagnostics, firmware,
                           firmwareBefore: firmwareBefore,
                           controlResult: controlResult,
                           firmwareAfterAvailable: firmwareAfterAvailable);
    }

    public async Task<Nice64RudpcFirmwareDiagnostics> GetDiagnosticsAsync(
        TimeSpan timeout, CancellationToken token = default)
    {
        Nice64RudpPacket reply = await ExchangeControlAsync(new byte[] { RudpcStats }, RudpcStatsReply,
                                                             timeout, token);
        if (reply.Payload.Length != 1 + StatsNames.Length * sizeof(uint))
            throw new InvalidDataException("Invalid RUDP-C statistics response length.");
        Dictionary<string, uint> values = new(StatsNames.Length, StringComparer.Ordinal);
        for (int index = 0; index < StatsNames.Length; index++)
            values.Add(StatsNames[index], BinaryPrimitives.ReadUInt32BigEndian(
                reply.Payload.AsSpan(1 + index * sizeof(uint), sizeof(uint))));
        return new(values);
    }

    public async ValueTask DisposeAsync()
    {
        if (_established)
        {
            try { await ExchangeControlAsync(new byte[] { Goodbye }, Released, TimeSpan.FromMilliseconds(500), default); }
            catch { }
        }
        _udp.Dispose();
    }

    private async Task<bool> AcceptAndAckAsync(Nice64RudpPacket packet,
        Nice64RudpReceiveWindow window, uint initialSequence,
        Nice64RudpcImpairment impairment, bool droppedAckOnce,
        CancellationToken token)
    {
        window.Receive(packet);
        Nice64RudpAck ack = window.CreateAck();
        int ackIndex = Relative(initialSequence, unchecked(ack.AckBase + 1));
        bool drop = impairment.DropAckAlways == ackIndex ||
                    (impairment.DropAckOnce == ackIndex && !droppedAckOnce);
        if (impairment.DropAckOnce == ackIndex) droppedAckOnce = true;
        if (drop) return droppedAckOnce;
        byte[] wire = CreateWire([], 0, Nice64RudpPacketFlags.Ack,
                                 ack.AckBase, ack.AckBitmap);
        await _udp.SendAsync(wire, token);
        if (impairment.DuplicateAck == ackIndex) await _udp.SendAsync(wire, token);
        return droppedAckOnce;
    }

    private async Task BeginAsync(byte direction, int rtoMs, uint initialSequence,
        int byteCount, uint crc, TimeSpan timeout, CancellationToken token)
    {
        if (byteCount is < 1 or > 2 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(byteCount));
        byte[] payload = new byte[16];
        payload[0] = StreamBegin;
        payload[1] = direction;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), checked((ushort)rtoMs));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), initialSequence);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), (uint)byteCount);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12), crc);
        Nice64RudpPacket reply = await ExchangeControlAsync(payload, StreamBeginAck, timeout, token);
        if (reply.Payload.Length != 12 || reply.Payload[1] != 0)
            throw new InvalidOperationException("Firmware rejected RUDP-C stream parameters.");
    }

    private async Task<Nice64RudpcResumeAckTracker> ResumeAndApplyAckAsync(
        Nice64RudpTransmitWindow window, TimeSpan timeout,
        Action<string>? trace, CancellationToken token)
    {
        uint sequence = unchecked(_controlSequence++);
        Nice64RudpcResumeAckTracker tracker = new(sequence, StreamStateAck, window);
        trace?.Invoke($"RESUME send control_seq={sequence} host_base={window.BaseSequence} " +
            $"outstanding={Nice64RudpSequence.ForwardDistance(window.BaseSequence, window.NextSequence)}");
        await _udp.SendAsync(CreateWire([StreamResume], sequence,
            Nice64RudpPacketFlags.Data, 0, 0), token);
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            Nice64RudpPacket? packet = await ReceiveOneAsync(timeout - elapsed.Elapsed, token);
            if (packet is null) continue;
            tracker.Observe(packet, window, trace);
            if (tracker.Complete)
            {
                trace?.Invoke($"RESUME final state_ack={tracker.StateAcknowledged} " +
                    $"ack_base_after_drain={tracker.LastAckBase} " +
                    $"ack_bitmap_after_drain=0x{tracker.LastAckBitmap:X8} " +
                    $"host_outstanding_after={tracker.OutstandingAfter}");
                return tracker;
            }
        }
        throw new TimeoutException(
            $"RUDP-C resume incomplete: state_ack={tracker.StateAcknowledged} " +
            $"cumulative_progress={tracker.CumulativeProgressObserved} " +
            $"ack_base={tracker.LastAckBase} ack_bitmap=0x{tracker.LastAckBitmap:X8}.");
    }

    private async Task<Nice64RudpPacket> EndAsync(int actualBytes, uint actualCrc,
        TimeSpan timeout, CancellationToken token)
    {
        byte[] payload = new byte[9];
        payload[0] = StreamEnd;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1), (uint)actualBytes);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(5), actualCrc);
        return await ExchangeControlAsync(payload, StreamResult, timeout, token);
    }

    private Nice64RudpcTransferResult ParseResult(Nice64RudpPacket reply,
        int payloadBytes, uint expectedCrc, TimeSpan elapsed,
        IReadOnlyList<int> retransmittedOffsets, Nice64RudpDiagnostics host,
        Nice64RudpcFirmwareDiagnostics firmware, int maximumOutstanding = 0,
        uint finalAckBase = 0, uint finalAckBitmap = 0,
        uint finalHostOutstanding = 0,
        Nice64RudpcResumeEvidence? resumeEvidence = null,
        Nice64RudpcPacingEvidence? pacingEvidence = null,
        Nice64RudpcFirmwareDiagnostics? firmwareBefore = null,
        bool duplicateInjected = false, uint? duplicateSequence = null,
        string controlResult = "PASS", bool firmwareAfterAvailable = true)
    {
        if (reply.Payload.Length != 32) throw new InvalidDataException("Invalid RUDP-C result length.");
        byte status = reply.Payload[1];
        uint actualBytes = BinaryPrimitives.ReadUInt32BigEndian(reply.Payload.AsSpan(8));
        uint actualCrc = BinaryPrimitives.ReadUInt32BigEndian(reply.Payload.AsSpan(16));
        uint deliveries = BinaryPrimitives.ReadUInt32BigEndian(reply.Payload.AsSpan(20));
        bool pass = status == 0 && actualBytes == payloadBytes && actualCrc == expectedCrc;
        firmwareBefore ??= firmware;
        return new(pass, status, payloadBytes, actualBytes, expectedCrc, actualCrc,
                   deliveries, elapsed, retransmittedOffsets, host, firmwareBefore, firmware,
                   maximumOutstanding, finalAckBase, finalAckBitmap,
                   finalHostOutstanding, resumeEvidence, pacingEvidence, duplicateInjected,
                   duplicateSequence, controlResult, firmwareAfterAvailable);
    }

    private async Task<Nice64RudpPacket> ExchangeControlAsync(ReadOnlyMemory<byte> payload,
        byte expectedOperation, TimeSpan timeout, CancellationToken token)
    {
        uint sequence = unchecked(_controlSequence++);
        await _udp.SendAsync(CreateWire(payload.Span, sequence,
            Nice64RudpPacketFlags.Data, 0, 0), token);
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            Nice64RudpPacket? packet = await ReceiveOneAsync(timeout - elapsed.Elapsed, token);
            if (packet is not null && packet.Header.Sequence == sequence &&
                packet.Payload.Length != 0 && packet.Payload[0] == expectedOperation)
                return packet;
        }
        throw new TimeoutException($"No RUDP-C control reply 0x{expectedOperation:X2}.");
    }

    private async Task<Nice64RudpPacket?> ReceiveOneAsync(TimeSpan timeout,
        CancellationToken token)
    {
        if (timeout <= TimeSpan.Zero) return null;
        using CancellationTokenSource timer = new(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token);
        try
        {
            UdpReceiveResult result = await _udp.ReceiveAsync(linked.Token);
            return Nice64RudpPacketCodec.TryDecode(result.Buffer, MaximumPayload + 1,
                SessionId, out Nice64RudpPacket? packet, out _) ? packet : null;
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    private byte[] CreateWire(ReadOnlySpan<byte> payload, uint sequence,
        Nice64RudpPacketFlags flags, uint ackBase, uint ackBitmap)
    {
        Nice64RudpHeader header = new(Nice64WirelessTransportContract.Magic,
            Nice64WirelessTransportContract.TransportVersion, flags, 0,
            Nice64WirelessTransportContract.HeaderLength, SessionId, sequence,
            ackBase, ackBitmap, checked((ushort)payload.Length), 0);
        return Nice64RudpPacketCodec.Encode(header, payload, MaximumPayload + 1);
    }

    private static byte[] CreatePattern(int count)
    {
        byte[] result = new byte[count];
        for (int index = 0; index < result.Length; index++) result[index] = (byte)(index * 31 + 7);
        return result;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static int Relative(uint initial, uint sequence) => unchecked((int)(sequence - initial));
    private static uint RandomNonzeroUInt32()
    {
        Span<byte> bytes = stackalloc byte[4];
        uint value;
        do { RandomNumberGenerator.Fill(bytes); value = BinaryPrimitives.ReadUInt32LittleEndian(bytes); } while (value == 0);
        return value;
    }
    private void EnsureEstablished()
    {
        if (!_established) throw new InvalidOperationException("EstablishAsync must succeed first.");
    }
}
