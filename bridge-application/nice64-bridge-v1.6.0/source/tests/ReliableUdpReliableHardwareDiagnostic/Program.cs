using PicoSwitchController;
using System.Security.Cryptography;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Print(string label, Nice64RudpcTransferResult result)
{
    uint Delta(string name) =>
        result.FirmwareDiagnostics.Delta(name, result.FirmwareDiagnosticsBefore);
    double kib = result.ActualOrderedBytes / 1024.0;
    double rate = kib / result.Elapsed.TotalSeconds;
    int expectedDeliveries = (result.ExpectedBytes + 511) / 512;
    string status = result.Status switch
    {
        0 => "success",
        1 => "inactive-or-incomplete-byte-count",
        2 => "crc-mismatch",
        3 => "firmware-tx-retry-exhaustion",
        _ => "unknown"
    };
    Console.WriteLine($"{label}: pass={result.Passed} status={result.Status} " +
        $"({status}) expected_bytes={result.ExpectedBytes} " +
        $"actual_ordered_bytes={result.ActualOrderedBytes} " +
        $"elapsed={result.Elapsed.TotalMilliseconds:F1}ms rate={rate:F2}KiB/s " +
        $"expected_crc=0x{result.ExpectedCrc32:X8} actual_crc=0x{result.ActualCrc32:X8} " +
        $"expected_deliveries={expectedDeliveries} actual_deliveries={result.UpperDeliveries} " +
        $"host_tx={result.HostDiagnostics.TxDatagrams} " +
        $"host_retx={result.HostDiagnostics.Retransmissions} " +
        $"retx_offsets=[{string.Join(',', result.RetransmittedOffsets)}] " +
        $"host_acks={result.HostDiagnostics.AcksReceived} " +
        $"final_ack_base={result.FinalAckBase} final_ack_bitmap=0x{result.FinalAckBitmap:X8} " +
        $"max_outstanding={result.MaximumOutstanding} " +
        $"host_outstanding={result.FinalHostOutstanding} " +
        $"host_retry_exhaustion={result.HostDiagnostics.RetryExhaustion} " +
        $"firmware_rx_total={result.FirmwareDiagnostics["rx_datagrams"]} " +
        $"firmware_rx_delta={Delta("rx_datagrams")} " +
        $"firmware_retx_total={result.FirmwareDiagnostics["retransmissions"]} " +
        $"firmware_retx_delta={Delta("retransmissions")} " +
        $"duplicate_rx_total={result.FirmwareDiagnostics["duplicate_rx"]} " +
        $"duplicate_rx_delta={Delta("duplicate_rx")} " +
        $"old_or_stale_total={result.FirmwareDiagnostics["old_or_stale_rx"]} " +
        $"old_or_stale_delta={Delta("old_or_stale_rx")} " +
        $"out_of_order_total={result.FirmwareDiagnostics["out_of_order_rx"]} " +
        $"out_of_order_delta={Delta("out_of_order_rx")} " +
        $"staging_full_total={result.FirmwareDiagnostics["staging_full"]} " +
        $"staging_full_delta={Delta("staging_full")} " +
        $"staging_drop_first32=0x{result.FirmwareDiagnostics["staging_drop_first32_bitmap"]:X8} " +
        $"last_staging_drop_seq={result.FirmwareDiagnostics["last_staging_drop_sequence"]} " +
        $"firmware_ack_base={result.FirmwareDiagnostics["last_ack_base"]} " +
        $"firmware_ack_bitmap=0x{result.FirmwareDiagnostics["last_ack_bitmap"]:X8} " +
        $"firmware_rx_occupied={result.FirmwareDiagnostics["rx_occupied_slots"]} " +
        $"firmware_retry_exhaustion_total={result.FirmwareDiagnostics["retry_exhaustion"]} " +
        $"firmware_retry_exhaustion_delta={Delta("retry_exhaustion")} " +
        $"stream_active={result.FirmwareDiagnostics["stream_active"]}");
    if (result.ResumeEvidence is Nice64RudpcResumeEvidence resume)
    {
        Console.WriteLine($"PAUSE/RESUME state_ack={resume.StateAcknowledged} " +
            $"host_base_before={resume.HostBaseBefore} " +
            $"host_outstanding_before={resume.HostOutstandingBefore} " +
            $"ack_base_after_drain={resume.AckBaseAfterDrain} " +
            $"ack_bitmap_after_drain=0x{resume.AckBitmapAfterDrain:X8} " +
            $"host_outstanding_after={resume.HostOutstandingAfter}");
    }
    if (result.PacingEvidence is Nice64RudpcPacingEvidence pacing)
    {
        Console.WriteLine($"FLOW CONTROL configured_cap_before_pause={pacing.NormalCap} " +
            $"temporary_pause_cap={pacing.PauseOverrideCap} " +
            $"paused_outstanding={pacing.MaximumPausedOutstanding} " +
            $"restored_cap={(pacing.NormalCapRestored ? pacing.NormalCap : 0)} " +
            $"post_resume_max_outstanding={pacing.MaximumPostResumeOutstanding}");
    }
}

static async Task<Nice64RudpcTransferResult> Upload(string target, int port,
    byte[] payload, int rto, uint sequence, string label,
    int flightCap, Action<string>? trace,
    Nice64RudpcImpairment? impairment = null, TimeSpan? timeout = null)
{
    await using Nice64RudpcSyntheticClient client = new(target, port);
    await client.EstablishAsync(TimeSpan.FromSeconds(2));
    impairment = (impairment ?? new()) with
    {
        NewDataFlightCap = flightCap,
        Trace = trace
    };
    Nice64RudpcTransferResult result = await client.UploadAsync(payload, rto, sequence,
        impairment, timeout ?? TimeSpan.FromSeconds(30));
    Print(label, result);
    return result;
}

static async Task<Nice64RudpcTransferResult> Download(string target, int port,
    int bytes, int rto, uint sequence, string label,
    Nice64RudpcImpairment? impairment = null, TimeSpan? timeout = null)
{
    await using Nice64RudpcSyntheticClient client = new(target, port);
    await client.EstablishAsync(TimeSpan.FromSeconds(2));
    Nice64RudpcTransferResult result = await client.DownloadAsync(bytes, rto, sequence,
        impairment, timeout ?? TimeSpan.FromSeconds(30));
    Print(label, result);
    return result;
}

static async Task<Nice64RudpcCharacterizationSample> CharacterizeOne(
    string target, int port, byte[] payload, Nice64RudpcCharacterizationCase item,
    Action<string>? trace)
{
    await using Nice64RudpcSyntheticClient client = new(target, port);
    try
    {
        await client.EstablishAsync(TimeSpan.FromSeconds(2));
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException("HELLO_INFRASTRUCTURE: " + ex.Message, ex);
    }
    Nice64RudpcImpairment impairment = item.CreateImpairment() with
    {
        NewDataFlightCap = item.FlightCap,
        CaptureControlTimeouts = true,
        Trace = trace
    };
    Nice64RudpcTransferResult result = await client.UploadAsync(payload, item.RtoMs,
        item.InitialSequence, impairment, TimeSpan.FromSeconds(30));
    return Nice64RudpcRtoCharacterization.FromTransfer(item, result);
}

static void PrintCharacterizationSample(Nice64RudpcCharacterizationSample row)
{
    string firmware(string value) => row.FirmwareDeltasAvailable ? value : "NA";
    Console.WriteLine($"CHARACTERIZATION test_type={row.Case.Test} " +
        $"sample_number={row.Case.Sample} rto_ms={row.Case.RtoMs} " +
        $"flight_cap={row.Case.FlightCap} data_transport_result={row.DataResult} " +
        $"control_result={row.ControlResult} sample_disposition={row.Disposition} " +
        $"expected_bytes={row.ExpectedBytes} actual_bytes={row.ActualBytes} " +
        $"expected_crc=0x{row.ExpectedCrc:X8} actual_crc=0x{row.ActualCrc:X8} " +
        $"expected_deliveries={row.ExpectedDeliveries} actual_deliveries={row.ActualDeliveries} " +
        $"elapsed_ms={row.ElapsedMilliseconds:F1} effective_KiB_s={row.RateKibPerSecond:F2} " +
        $"host_tx={row.HostTx} host_retransmissions={row.HostRetransmissions} " +
        $"unique_retransmitted_offsets={row.UniqueRetransmittedOffsets} " +
        $"host_retry_exhaustion={row.HostRetryExhaustion} " +
        $"firmware_rx_delta={firmware(row.FirmwareRxDelta.ToString())} " +
        $"firmware_retx_delta={firmware(row.FirmwareRetransmissionDelta.ToString())} " +
        $"duplicate_rx_delta={firmware(row.DuplicateRxDelta.ToString())} " +
        $"old_or_stale_delta={firmware(row.OldOrStaleDelta.ToString())} " +
        $"out_of_order_delta={firmware(row.OutOfOrderDelta.ToString())} " +
        $"staging_full_delta={firmware(row.StagingFullDelta.ToString())} " +
        $"firmware_retry_exhaustion_delta={firmware(row.FirmwareRetryExhaustionDelta.ToString())} " +
        $"final_rx_occupied={firmware(row.FinalRxOccupied.ToString())} " +
        $"max_outstanding={row.MaximumOutstanding} " +
        $"final_host_outstanding={row.FinalHostOutstanding} " +
        $"error={row.Error ?? "NONE"}");
}

static void PrintCharacterizationSummary(Nice64RudpcCharacterizationSummary summary)
{
    Console.WriteLine($"SUMMARY test={summary.Test} attempted={summary.Attempted} " +
        $"pass={summary.Passed} fail={summary.Failed} " +
        $"control_inconclusive={summary.ControlInconclusive} pass_rate={summary.PassRate:F1}% " +
        $"elapsed_min={summary.MinimumElapsed:F1} elapsed_max={summary.MaximumElapsed:F1} " +
        $"elapsed_mean={summary.MeanElapsed:F1} elapsed_median={summary.MedianElapsed:F1} " +
        $"rate_min={summary.MinimumRate:F2} rate_max={summary.MaximumRate:F2} " +
        $"rate_mean={summary.MeanRate:F2} rate_median={summary.MedianRate:F2} " +
        $"retx_total={summary.TotalRetransmissions} retx_min={summary.MinimumRetransmissions} " +
        $"retx_max={summary.MaximumRetransmissions} retx_median={summary.MedianRetransmissions:F1} " +
        $"staging_samples={summary.SamplesWithStagingFull} " +
        $"staging_total={summary.TotalStagingFull} " +
        $"retry_exhaustion_samples={summary.SamplesWithRetryExhaustion} " +
        $"old_or_stale_total={summary.TotalOldOrStale} " +
        $"out_of_order_total={summary.TotalOutOfOrder}");
}

static async Task<Nice64RudpcAcceptanceCaseResult> AcceptOne(
    string target, int port, byte[] payload, Nice64RudpcAcceptanceCase item,
    Action<string>? trace)
{
    await using Nice64RudpcSyntheticClient client = new(target, port);
    try
    {
        await client.EstablishAsync(TimeSpan.FromSeconds(2));
    }
    catch (Exception ex)
    {
        return Nice64RudpcAcceptance.FromException(item,
            new InvalidOperationException("HELLO_INFRASTRUCTURE: " + ex.Message, ex),
            client.SessionId);
    }
    Nice64RudpcImpairment impairment = item.CreateImpairment() with
    {
        NewDataFlightCap = item.FlightCap,
        CaptureControlTimeouts = true,
        Trace = trace
    };
    try
    {
        Nice64RudpcTransferResult transfer = item.IsDownload
            ? await client.DownloadAsync(item.ByteCount, item.RtoMs, item.InitialSequence,
                impairment, TimeSpan.FromSeconds(30))
            : await client.UploadAsync(payload.AsMemory(0, item.ByteCount), item.RtoMs,
                item.InitialSequence, impairment, TimeSpan.FromSeconds(30));
        return Nice64RudpcAcceptance.FromTransfer(item, client.SessionId, transfer);
    }
    catch (Exception ex)
    {
        return Nice64RudpcAcceptance.FromException(item, ex, client.SessionId);
    }
}

static void PrintAcceptanceCase(Nice64RudpcAcceptanceCaseResult row)
{
    Nice64RudpcTransferResult? result = row.Transfer;
    uint Delta(string name) => result is { FirmwareDiagnosticsAfterAvailable: true }
        ? result.FirmwareDiagnostics.Delta(name, result.FirmwareDiagnosticsBefore) : 0;
    string Firmware(uint value) => result is { FirmwareDiagnosticsAfterAvailable: true }
        ? value.ToString() : "NA";
    double rate = result is { Elapsed.TotalSeconds: > 0 }
        ? result.ActualOrderedBytes / 1024.0 / result.Elapsed.TotalSeconds : 0;
    uint expectedDeliveries = (uint)((row.Case.ByteCount + 511) / 512);
    Console.WriteLine($"ACCEPTANCE run_number={row.Case.Run} test_type={row.Case.Test} " +
        $"rto_ms={row.Case.RtoMs} flight_cap={row.Case.FlightCap} " +
        $"session_id={row.SessionId} data_result={row.DataResult} " +
        $"control_result={row.ControlResult} disposition={row.Disposition} " +
        $"expected_bytes={result?.ExpectedBytes ?? row.Case.ByteCount} " +
        $"actual_bytes={result?.ActualOrderedBytes ?? 0} " +
        $"expected_crc=0x{result?.ExpectedCrc32 ?? 0:X8} " +
        $"actual_crc=0x{result?.ActualCrc32 ?? 0:X8} " +
        $"expected_deliveries={expectedDeliveries} actual_deliveries={result?.UpperDeliveries ?? 0} " +
        $"elapsed_ms={result?.Elapsed.TotalMilliseconds ?? 0:F1} effective_KiB_s={rate:F2} " +
        $"host_tx={result?.HostDiagnostics.TxDatagrams ?? 0} " +
        $"host_retransmissions={result?.HostDiagnostics.Retransmissions ?? 0} " +
        $"unique_retransmitted_offsets={result?.RetransmittedOffsets.Count ?? 0} " +
        $"host_retry_exhaustion={result?.HostDiagnostics.RetryExhaustion ?? 0} " +
        $"firmware_rx_delta={Firmware(Delta("rx_datagrams"))} " +
        $"duplicate_rx_delta={Firmware(Delta("duplicate_rx"))} " +
        $"old_or_stale_delta={Firmware(Delta("old_or_stale_rx"))} " +
        $"out_of_order_delta={Firmware(Delta("out_of_order_rx"))} " +
        $"staging_full_delta={Firmware(Delta("staging_full"))} " +
        $"max_outstanding={result?.MaximumOutstanding ?? 0} " +
        $"final_host_outstanding={result?.FinalHostOutstanding ?? 0} " +
        $"final_rx_occupied={Firmware(result is { FirmwareDiagnosticsAfterAvailable: true }
            ? result.FirmwareDiagnostics["rx_occupied_slots"] : 0)} " +
        $"duplicate_coverage={row.DuplicateCoverage} error={row.Error ?? "NONE"}");
    if (row.Case.Test == Nice64RudpcAcceptanceTest.PausedConsumer &&
        result?.PacingEvidence is Nice64RudpcPacingEvidence pacing &&
        result.ResumeEvidence is Nice64RudpcResumeEvidence resume)
    {
        Console.WriteLine($"ACCEPTANCE PAUSE run_number={row.Case.Run} " +
            $"configured_cap_before_pause={pacing.NormalCap} " +
            $"temporary_pause_cap={pacing.PauseOverrideCap} " +
            $"paused_outstanding={pacing.MaximumPausedOutstanding} " +
            $"paused_ack_base={pacing.PausedAckBase} " +
            $"paused_ack_bitmap=0x{pacing.PausedAckBitmap:X8} " +
            $"resume_ack_base={resume.AckBaseAfterDrain} " +
            $"resume_ack_bitmap=0x{resume.AckBitmapAfterDrain:X8} " +
            $"state_ack={resume.StateAcknowledged} " +
            $"restored_cap={(pacing.NormalCapRestored ? pacing.NormalCap : 0)} " +
            $"post_resume_max_outstanding={pacing.MaximumPostResumeOutstanding}");
    }
}

static async Task<Nice64RudpcCloseoutResult> CloseoutOne(
    string target, int port, ReadOnlyMemory<byte> payload,
    Nice64RudpcCloseoutCase item, Action<string>? trace)
{
    await using Nice64RudpcSyntheticClient client = new(target, port);
    try
    {
        await client.EstablishAsync(TimeSpan.FromSeconds(2));
    }
    catch (Exception ex)
    {
        return Nice64RudpcCloseout.FromException(item, client.SessionId,
            new InvalidOperationException("HELLO_INFRASTRUCTURE: " + ex.Message, ex));
    }

    Nice64RudpcImpairment impairment = item.CreateImpairment() with
    {
        NewDataFlightCap = item.FlightCap,
        CaptureControlTimeouts = true,
        Trace = trace
    };
    try
    {
        Nice64RudpcTransferResult transfer = item.IsDownload
            ? await client.DownloadAsync(item.ByteCount, item.RtoMs,
                item.InitialSequence, impairment, TimeSpan.FromSeconds(3))
            : await client.UploadAsync(payload[..item.ByteCount], item.RtoMs,
                item.InitialSequence, impairment,
                item.Test == Nice64RudpcCloseoutTest.TwoMiBSoak
                    ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(30));
        return Nice64RudpcCloseout.FromTransfer(item, client.SessionId, transfer);
    }
    catch (Exception ex)
    {
        return Nice64RudpcCloseout.FromException(item, client.SessionId, ex);
    }
}

static void PrintCloseoutCase(Nice64RudpcCloseoutResult row)
{
    Nice64RudpcTransferResult? result = row.Transfer;
    uint Delta(string name) => result is { FirmwareDiagnosticsAfterAvailable: true }
        ? result.FirmwareDiagnostics.Delta(name, result.FirmwareDiagnosticsBefore) : 0;
    string Firmware(uint value) => result is { FirmwareDiagnosticsAfterAvailable: true }
        ? value.ToString() : "NA";
    double rate = result is { Elapsed.TotalSeconds: > 0 }
        ? result.ActualOrderedBytes / 1024.0 / result.Elapsed.TotalSeconds : 0;
    uint expectedDeliveries = (uint)((row.Case.ByteCount + 511) / 512);
    Console.WriteLine($"CLOSEOUT test_type={row.Case.Test} " +
        $"rto_ms={row.Case.RtoMs} flight_cap={row.Case.FlightCap} " +
        $"session_id={row.SessionId} data_result={row.DataResult} " +
        $"control_result={row.ControlResult} disposition={row.Disposition} " +
        $"expected_bytes={result?.ExpectedBytes ?? row.Case.ByteCount} " +
        $"actual_bytes={result?.ActualOrderedBytes ?? 0} " +
        $"expected_crc=0x{result?.ExpectedCrc32 ?? 0:X8} " +
        $"actual_crc=0x{result?.ActualCrc32 ?? 0:X8} " +
        $"expected_deliveries={expectedDeliveries} " +
        $"actual_deliveries={result?.UpperDeliveries ?? 0} " +
        $"elapsed_ms={result?.Elapsed.TotalMilliseconds ?? 0:F1} " +
        $"effective_KiB_s={rate:F2} " +
        $"host_tx={result?.HostDiagnostics.TxDatagrams ?? 0} " +
        $"host_retransmissions={result?.HostDiagnostics.Retransmissions ?? 0} " +
        $"host_retry_exhaustion={result?.HostDiagnostics.RetryExhaustion ?? 0} " +
        $"firmware_rx_delta={Firmware(Delta("rx_datagrams"))} " +
        $"duplicate_rx_delta={Firmware(Delta("duplicate_rx"))} " +
        $"old_or_stale_delta={Firmware(Delta("old_or_stale_rx"))} " +
        $"out_of_order_delta={Firmware(Delta("out_of_order_rx"))} " +
        $"staging_full_delta={Firmware(Delta("staging_full"))} " +
        $"firmware_retry_exhaustion_delta={Firmware(Delta("retry_exhaustion"))} " +
        $"maximum_retry_count={Firmware(result is { FirmwareDiagnosticsAfterAvailable: true }
            ? result.FirmwareDiagnostics["maximum_retry_count"] : 0)} " +
        $"max_outstanding={result?.MaximumOutstanding ?? 0} " +
        $"final_host_outstanding={result?.FinalHostOutstanding ?? 0} " +
        $"final_rx_occupied={Firmware(result is { FirmwareDiagnosticsAfterAvailable: true }
            ? result.FirmwareDiagnostics["rx_occupied_slots"] : 0)} " +
        $"error={row.Error ?? "NONE"}");
}

Nice64RudpcHardwareDiagnosticOptions options =
    Nice64RudpcHardwareDiagnosticOptions.Parse(args);
string? target = options.Target;
int port = options.Port;
bool quick = options.Quick;
int flightCap = options.FlightCap;
Action<string>? trace = options.TraceEnabled ? Console.WriteLine : null;

if (target is null)
{
    Console.WriteLine("RUDP-C reliable hardware diagnostic built successfully.");
    Console.WriteLine("Run: --target <manual-pico-ip> [--port 6465] [--quick] [--flight-cap 1|2|4|8] [--rto 1..1000] [--trace]");
    Console.WriteLine("RTO characterization: --target <ip> --characterize-rto --flight-cap 2 --rto 20|40 [--samples 1..100]");
    Console.WriteLine("RUDP-C acceptance: --target <ip> --accept-rudpc --flight-cap 2 --rto 40 [--runs 1..100] [--trace]");
    Console.WriteLine("RUDP-C closeout: --target <ip> --closeout-rudpc --flight-cap 2 --rto 40 [--trace]");
    Console.WriteLine("Default performs the required RTO matrix, impairments, and 2 MiB soak.");
    Console.WriteLine("With --rto, only the selected baseline RTO runs before the unchanged impairment suite.");
    return;
}

byte[] primary = new byte[65_536];
RandomNumberGenerator.Fill(primary);
if (options.CloseoutRudpc)
{
    byte[] closeoutPayload = new byte[2 * 1024 * 1024];
    RandomNumberGenerator.Fill(closeoutPayload);
    IReadOnlyList<Nice64RudpcCloseoutCase> plan =
        Nice64RudpcCloseout.CreatePlan(options.SelectedRto!.Value, options.FlightCap);
    List<Nice64RudpcCloseoutResult> results = new(plan.Count);
    foreach (Nice64RudpcCloseoutCase item in plan)
    {
        Nice64RudpcCloseoutResult result = await CloseoutOne(
            target, port, closeoutPayload, item, trace);
        results.Add(result);
        PrintCloseoutCase(result);
    }
    bool uniqueSessions = results.All(row => row.SessionId != 0) &&
        results.Select(row => row.SessionId).Distinct().Count() == results.Count;
    Console.WriteLine("CLOSEOUT SUMMARY " +
        $"bounded_exhaustion={results[0].Disposition} " +
        $"fresh_session_recovery={results[1].Disposition} " +
        $"two_MiB_soak={results[2].Disposition} " +
        $"unique_nonzero_sessions={uniqueSessions} physical_status=HARDWARE_PENDING");
    Console.WriteLine("Network Pak remains on TCP; protected Network Pak/RUDP integration was not started.");
    Console.WriteLine("LATEST_STATE/Fast remains reserved, unsupported, unadvertised, and unselectable.");
    return;
}
if (options.AcceptRudpc)
{
    IReadOnlyList<Nice64RudpcAcceptanceCase> plan =
        Nice64RudpcAcceptance.CreatePlan(
            options.SelectedRto!.Value, options.FlightCap, options.Runs);
    IReadOnlyList<Nice64RudpcAcceptanceCaseResult> results =
        await Nice64RudpcAcceptance.ExecutePlanAsync(plan,
            item => AcceptOne(target, port, primary, item, trace), PrintAcceptanceCase);
    foreach (int run in Enumerable.Range(1, options.Runs))
        Console.WriteLine($"ACCEPTANCE RUN run_number={run} disposition=" +
            Nice64RudpcAcceptance.ClassifyRun(run, results));
    Nice64RudpcAcceptanceAggregate summary =
        Nice64RudpcAcceptance.Summarize(options.Runs, results);
    Console.WriteLine($"ACCEPTANCE SUMMARY requested_runs={summary.RequestedRuns} " +
        $"complete_pass_runs={summary.CompletePassRuns} " +
        $"control_inconclusive_runs={summary.ControlInconclusiveRuns} " +
        $"failed_runs={summary.FailedRuns} " +
        $"initiated_data_cases={summary.InitiatedDataCases} data_pass={summary.DataPass} " +
        $"data_fail={summary.DataFail} retry_exhaustion_cases={summary.RetryExhaustionCases} " +
        $"crc_failures={summary.CrcFailures} " +
        $"delivery_count_failures={summary.DeliveryCountFailures} " +
        $"host_leak_cases={summary.HostLeakCases} rx_leak_cases={summary.RxLeakCases} " +
        $"unique_nonzero_sessions={Nice64RudpcAcceptance.HasUniqueNonzeroSessions(results)}");
    if (results.Count < plan.Count)
        Console.WriteLine("ACCEPTANCE ABORT infrastructure_blocker=" +
            "three_consecutive_HELLO_failures");
    Console.WriteLine("--quick remains a development smoke mode and was not used by acceptance.");
    Console.WriteLine("Network Pak remains on TCP; RUDP-D was not started.");
    return;
}
if (options.CharacterizeRto)
{
    IReadOnlyList<Nice64RudpcCharacterizationCase> plan =
        Nice64RudpcRtoCharacterization.CreatePlan(
            options.SelectedRto!.Value, options.FlightCap, options.Samples);
    IReadOnlyList<Nice64RudpcCharacterizationSample> results =
        await Nice64RudpcRtoCharacterization.ExecutePlanAsync(plan,
            item => CharacterizeOne(target, port, primary, item, trace),
            PrintCharacterizationSample);
    if (results.Count < plan.Count)
        Console.WriteLine("CHARACTERIZATION ABORT infrastructure_blocker=" +
            "three_consecutive_HELLO_failures");
    Nice64RudpcCharacterizationSummary[] summaries = Enum
        .GetValues<Nice64RudpcCharacterizationTest>()
        .Where(test => results.Any(row => row.Case.Test == test))
        .Select(test => Nice64RudpcRtoCharacterization.Summarize(test, results))
        .ToArray();
    foreach (Nice64RudpcCharacterizationSummary summary in summaries)
        PrintCharacterizationSummary(summary);
    int passes = results.Count(row => row.Disposition == Nice64RudpcSampleDisposition.Pass);
    int failures = results.Count(row => row.Disposition == Nice64RudpcSampleDisposition.Fail);
    int inconclusive = results.Count - passes - failures;
    int retryExhaustions = results.Count(row => row.HostRetryExhaustion != 0 ||
        row.FirmwareRetryExhaustionDelta != 0);
    double[] measuredRates = results.Where(row => row.RateKibPerSecond > 0)
        .Select(row => row.RateKibPerSecond).Order().ToArray();
    double medianRate = measuredRates.Length == 0 ? 0 :
        measuredRates.Length % 2 == 0
            ? (measuredRates[measuredRates.Length / 2 - 1] +
               measuredRates[measuredRates.Length / 2]) / 2.0
            : measuredRates[measuredRates.Length / 2];
    Console.WriteLine($"RTO {options.SelectedRto}ms / cap{options.FlightCap}: " +
        $"attempted={results.Count}/{plan.Count} pass={passes} fail={failures} " +
        $"control-inconclusive={inconclusive} retry-exhaustions={retryExhaustions} " +
        $"median_rate={medianRate:F2}KiB/s");
    Console.WriteLine("RTO affects retransmission timing only; ACK-clocked fresh admission remains immediate.");
    Console.WriteLine("No RTO winner was selected. Network Pak remains on TCP; RUDP-D was not started.");
    return;
}
int samples = quick ? 1 : 5;
foreach (int rto in options.BaselineRtos)
{
    List<double> elapsed = [];
    for (int run = 1; run <= samples; run++)
    {
        Nice64RudpcTransferResult result = await Upload(target, port, primary, rto,
            (uint)(0x10000000 + rto * 0x1000 + run * 0x100), $"RTO {rto} ms run {run}",
            flightCap, trace);
        Require(result.Passed, $"RTO {rto} ms run {run} failed.");
        elapsed.Add(result.Elapsed.TotalMilliseconds);
    }
    double[] ordered = elapsed.Order().ToArray();
    Console.WriteLine($"RTO {rto} ms summary: min={ordered[0]:F1}ms " +
        $"max={ordered[^1]:F1}ms mean={ordered.Average():F1}ms " +
        $"median={ordered[ordered.Length / 2]:F1}ms");
}

int impairmentRto = options.ImpairmentRto;
Nice64RudpcTransferResult hole = await Upload(target, port, primary, impairmentRto, 0x20000000,
    "forced hole", flightCap, trace, new(DropDataOnce: 3));
Require(hole.Passed && hole.RetransmittedOffsets.Contains(3),
        "Forced hole sequence was not selectively retransmitted.");

Nice64RudpcTransferResult ackLoss = await Upload(target, port, primary, impairmentRto, 0x30000000,
    "ACK loss", flightCap, trace, new(DropAckOnce: 1));
Require(ackLoss.Passed, "ACK-loss transfer failed.");

Nice64RudpcTransferResult duplicateData = await Upload(target, port, primary, impairmentRto, 0x40000000,
    "duplicate data", flightCap, trace, new(DuplicateData: 2));
uint expectedDuplicateDeliveries = (uint)((duplicateData.ExpectedBytes + 511) / 512);
Nice64RudpcDuplicateAcceptanceResult duplicateAcceptance =
    Nice64RudpcDuplicateAcceptance.Evaluate(
        duplicateData.DuplicateInjected,
        duplicateData.FirmwareDiagnostics.Delta("rx_datagrams",
            duplicateData.FirmwareDiagnosticsBefore),
        duplicateData.HostDiagnostics.TxDatagrams,
        (uint)duplicateData.ExpectedBytes, duplicateData.ActualOrderedBytes,
        duplicateData.ExpectedCrc32, duplicateData.ActualCrc32,
        expectedDuplicateDeliveries, duplicateData.UpperDeliveries,
        duplicateData.FirmwareDiagnostics.Delta("duplicate_upper_deliveries",
            duplicateData.FirmwareDiagnosticsBefore),
        duplicateData.FirmwareDiagnostics.Delta("duplicate_rx",
            duplicateData.FirmwareDiagnosticsBefore),
        duplicateData.FirmwareDiagnostics.Delta("old_or_stale_rx",
            duplicateData.FirmwareDiagnosticsBefore),
        duplicateData.FirmwareDiagnostics.Delta("retry_exhaustion",
            duplicateData.FirmwareDiagnosticsBefore),
        duplicateData.FirmwareDiagnostics["rx_occupied_slots"]);
Console.WriteLine($"DUPLICATE TEST injection_seq={duplicateData.DuplicateSequence} " +
    $"injection_attempted={duplicateAcceptance.InjectionAttempted} " +
    $"transport_behavior={(duplicateAcceptance.TransportPassed ? "PASS" : "FAIL")} " +
    $"injection_observed={duplicateAcceptance.InjectionObserved} " +
    $"coverage={duplicateAcceptance.Disposition} " +
    $"firmware_classification={duplicateAcceptance.FirmwareClassification}");
Require(duplicateAcceptance.TransportPassed,
    "Duplicate transport behavior violated exact-once ordered delivery.");
if (duplicateAcceptance.Disposition == "INCONCLUSIVE")
    Console.WriteLine("Duplicate classification coverage is INCONCLUSIVE: the host sent " +
        "the diagnostic copy, but firmware engine RX accounting did not observe it.");
else
    Require(duplicateAcceptance.Disposition == "PASS",
        "Duplicate diagnostic injection was not attempted.");

Nice64RudpcTransferResult duplicateAck = await Download(target, port, 65_536, impairmentRto, 0x50000000,
    "duplicate ACK", new(DuplicateAck: 3));
Require(duplicateAck.Passed, "Duplicate-ACK transfer failed.");

Nice64RudpcTransferResult reordered = await Upload(target, port, primary, impairmentRto, 0x60000000,
    "adjacent reorder", flightCap, trace, new(ReorderWithNext: 2));
Require(reordered.Passed && reordered.FirmwareDiagnostics["out_of_order_rx"] != 0,
        "Reorder did not exercise out-of-order buffering.");

Nice64RudpcTransferResult wrap = await Upload(target, port, primary[..4096], impairmentRto, 0xFFFFFFFC,
    "sequence wrap", flightCap, trace, new(DropDataOnce: 3));
Require(wrap.Passed, "Sequence-wrap transfer failed.");

Nice64RudpcTransferResult backpressure = await Upload(target, port, primary, impairmentRto, 0x70000000,
    "backpressure (temporary full logical window)", flightCap, trace,
    new(PauseConsumerMilliseconds: 250, PausedConsumerFlightCap: 8));
Require(backpressure.Passed && backpressure.FirmwareDiagnostics["rx_window_full"] != 0,
        "Backpressure did not fill the bounded RX window.");
Require(backpressure.ResumeEvidence is { StateAcknowledged: true,
        HostOutstandingBefore: 8, AckBitmapAfterDrain: 0,
        HostOutstandingAfter: 0 } &&
        backpressure.ResumeEvidence.AckBaseAfterDrain == 0x70000007,
    "Paused consumer did not drain eight packets and release the host window.");
Require(backpressure.PacingEvidence is {
        PauseOverrideCap: 8,
        MaximumPausedOutstanding: 8,
        NormalCapRestored: true } &&
        backpressure.PacingEvidence.NormalCap == flightCap &&
        backpressure.PacingEvidence.MaximumPostResumeOutstanding <= flightCap,
    "Paused-consumer override did not restore the selected normal flight cap.");
Console.WriteLine("PAUSE accepted / 8-of-8 window buffered / RESUME STATE_ACK received / " +
    $"8 ordered packets cumulatively released / normal cap {flightCap} restored / " +
    $"post-resume max <= {flightCap}: PASS");

if (options.StopAfterPausedConsumer)
{
    Console.WriteLine($"RUDP-C selected-RTO narrow quick suite through paused-consumer recovery: PASS " +
        $"(rto={impairmentRto}ms)");
    Console.WriteLine("Retry exhaustion, recovery, soak, and later acceptance work were not started.");
    Console.WriteLine("Network Pak was not routed through UDP by this diagnostic.");
    return;
}

Nice64RudpcTransferResult exhausted = await Download(target, port, 65_536, 20, 0x80000000,
    "retry exhaustion", new(DropDataAlways: 3), TimeSpan.FromSeconds(3));
Require(!exhausted.Passed && exhausted.Status == 3 &&
        exhausted.FirmwareDiagnostics["retry_exhaustion"] != 0,
        "Permanent loss did not produce bounded retry exhaustion.");

Nice64RudpcTransferResult recovery = await Upload(target, port, primary, 20, 0x90000000,
    "post-exhaustion new session", flightCap, trace);
Require(recovery.Passed, "A new session did not recover after retry exhaustion.");

if (!quick)
{
    byte[] soak = new byte[2 * 1024 * 1024];
    RandomNumberGenerator.Fill(soak);
    Nice64RudpcTransferResult soakResult = await Upload(target, port, soak, 20, 0xA0000000,
        "2 MiB soak", flightCap, trace,
        new(DropDataOnce: 3, DuplicateData: 10, ReorderWithNext: 20),
        TimeSpan.FromMinutes(3));
    Require(soakResult.Passed, "2 MiB soak failed.");
}

Console.WriteLine("RUDP-C synthetic hardware reliability suite: PASS");
Console.WriteLine("Network Pak was not routed through UDP by this diagnostic.");
