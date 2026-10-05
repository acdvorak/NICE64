namespace PicoSwitchController;

public enum Nice64RudpcCharacterizationTest
{
    Baseline,
    ForcedHole,
    AckLoss
}

public enum Nice64RudpcDataResult
{
    Pass,
    FailRetryExhaustion,
    FailTransport,
    NotRun
}

public enum Nice64RudpcControlResult
{
    Pass,
    Timeout0x90,
    Timeout0x91,
    Timeout0x96,
    InfrastructureFailure
}

public enum Nice64RudpcSampleDisposition
{
    Pass,
    Fail,
    ControlInconclusive
}

public sealed record Nice64RudpcCharacterizationCase(
    int Sample,
    Nice64RudpcCharacterizationTest Test,
    int RtoMs,
    int FlightCap,
    uint InitialSequence)
{
    public Nice64RudpcImpairment CreateImpairment() => Test switch
    {
        Nice64RudpcCharacterizationTest.ForcedHole => new(DropDataOnce: 3),
        Nice64RudpcCharacterizationTest.AckLoss => new(DropAckOnce: 1),
        _ => new()
    };
}

public sealed record Nice64RudpcCharacterizationSample(
    Nice64RudpcCharacterizationCase Case,
    Nice64RudpcDataResult DataResult,
    Nice64RudpcControlResult ControlResult,
    Nice64RudpcSampleDisposition Disposition,
    int ExpectedBytes,
    uint ActualBytes,
    uint ExpectedCrc,
    uint ActualCrc,
    uint ExpectedDeliveries,
    uint ActualDeliveries,
    double ElapsedMilliseconds,
    double RateKibPerSecond,
    long HostTx,
    long HostRetransmissions,
    int UniqueRetransmittedOffsets,
    long HostRetryExhaustion,
    uint FirmwareRxDelta,
    uint FirmwareRetransmissionDelta,
    uint DuplicateRxDelta,
    uint OldOrStaleDelta,
    uint OutOfOrderDelta,
    uint StagingFullDelta,
    uint FirmwareRetryExhaustionDelta,
    uint FinalRxOccupied,
    int MaximumOutstanding,
    uint FinalHostOutstanding,
    bool FirmwareDeltasAvailable,
    string? Error);

public sealed record Nice64RudpcCharacterizationSummary(
    Nice64RudpcCharacterizationTest Test,
    int Attempted,
    int Passed,
    int Failed,
    int ControlInconclusive,
    double PassRate,
    double MinimumElapsed,
    double MaximumElapsed,
    double MeanElapsed,
    double MedianElapsed,
    double MinimumRate,
    double MaximumRate,
    double MeanRate,
    double MedianRate,
    long TotalRetransmissions,
    long MinimumRetransmissions,
    long MaximumRetransmissions,
    double MedianRetransmissions,
    int SamplesWithStagingFull,
    ulong TotalStagingFull,
    int SamplesWithRetryExhaustion,
    ulong TotalOldOrStale,
    ulong TotalOutOfOrder);

public static class Nice64RudpcRtoCharacterization
{
    public static IReadOnlyList<Nice64RudpcCharacterizationCase> CreatePlan(
        int rtoMs, int flightCap, int samples)
    {
        if (rtoMs is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(rtoMs));
        if (flightCap != 2) throw new ArgumentOutOfRangeException(nameof(flightCap));
        if (samples is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(samples));
        List<Nice64RudpcCharacterizationCase> plan = new(samples * 3);
        for (int sample = 1; sample <= samples; sample++)
        {
            uint sampleBase = unchecked(0x10000000u + (uint)sample * 0x00100000u);
            plan.Add(new(sample, Nice64RudpcCharacterizationTest.Baseline,
                rtoMs, flightCap, sampleBase));
            plan.Add(new(sample, Nice64RudpcCharacterizationTest.ForcedHole,
                rtoMs, flightCap, sampleBase + 0x00010000u));
            plan.Add(new(sample, Nice64RudpcCharacterizationTest.AckLoss,
                rtoMs, flightCap, sampleBase + 0x00020000u));
        }
        return plan;
    }

    public static Nice64RudpcCharacterizationSample FromTransfer(
        Nice64RudpcCharacterizationCase item, Nice64RudpcTransferResult result)
    {
        bool retryExhausted = result.HostDiagnostics.RetryExhaustion != 0 ||
            (result.FirmwareDiagnosticsAfterAvailable &&
             result.FirmwareDiagnostics.Delta("retry_exhaustion",
                 result.FirmwareDiagnosticsBefore) != 0);
        Nice64RudpcDataResult data = result.Passed
            ? Nice64RudpcDataResult.Pass
            : retryExhausted
                ? Nice64RudpcDataResult.FailRetryExhaustion
                : Nice64RudpcDataResult.FailTransport;
        Nice64RudpcControlResult control = result.ControlResult switch
        {
            "TIMEOUT_0x91" => Nice64RudpcControlResult.Timeout0x91,
            "TIMEOUT_0x96" => Nice64RudpcControlResult.Timeout0x96,
            _ => Nice64RudpcControlResult.Pass
        };
        Nice64RudpcSampleDisposition disposition = data != Nice64RudpcDataResult.Pass
            ? Nice64RudpcSampleDisposition.Fail
            : control == Nice64RudpcControlResult.Pass
                ? Nice64RudpcSampleDisposition.Pass
                : Nice64RudpcSampleDisposition.ControlInconclusive;
        uint Delta(string name) => result.FirmwareDiagnosticsAfterAvailable
            ? result.FirmwareDiagnostics.Delta(name, result.FirmwareDiagnosticsBefore)
            : 0;
        double rate = result.Elapsed.TotalSeconds > 0
            ? result.ActualOrderedBytes / 1024.0 / result.Elapsed.TotalSeconds
            : 0;
        return new(item, data, control, disposition, result.ExpectedBytes,
            result.ActualOrderedBytes, result.ExpectedCrc32, result.ActualCrc32,
            (uint)((result.ExpectedBytes + 511) / 512), result.UpperDeliveries,
            result.Elapsed.TotalMilliseconds, rate, result.HostDiagnostics.TxDatagrams,
            result.HostDiagnostics.Retransmissions, result.RetransmittedOffsets.Count,
            result.HostDiagnostics.RetryExhaustion, Delta("rx_datagrams"),
            Delta("retransmissions"), Delta("duplicate_rx"), Delta("old_or_stale_rx"),
            Delta("out_of_order_rx"), Delta("staging_full"), Delta("retry_exhaustion"),
            result.FirmwareDiagnosticsAfterAvailable
                ? result.FirmwareDiagnostics["rx_occupied_slots"] : 0,
            result.MaximumOutstanding, result.FinalHostOutstanding,
            result.FirmwareDiagnosticsAfterAvailable, null);
    }

    public static Nice64RudpcCharacterizationSample FromException(
        Nice64RudpcCharacterizationCase item, Exception error)
    {
        Nice64RudpcControlResult control = error.Message.Contains("0x90", StringComparison.Ordinal)
            ? Nice64RudpcControlResult.Timeout0x90
            : error.Message.Contains("0x91", StringComparison.Ordinal)
                ? Nice64RudpcControlResult.Timeout0x91
                : error.Message.Contains("0x96", StringComparison.Ordinal)
                    ? Nice64RudpcControlResult.Timeout0x96
                    : error.Message.StartsWith("HELLO_INFRASTRUCTURE:", StringComparison.Ordinal)
                        ? Nice64RudpcControlResult.InfrastructureFailure
                        : Nice64RudpcControlResult.Pass;
        bool controlTimeout = control is
            Nice64RudpcControlResult.Timeout0x90 or
            Nice64RudpcControlResult.Timeout0x91 or
            Nice64RudpcControlResult.Timeout0x96
                ;
        Nice64RudpcDataResult data = controlTimeout ||
            control == Nice64RudpcControlResult.InfrastructureFailure
                ? Nice64RudpcDataResult.NotRun
                : Nice64RudpcDataResult.FailTransport;
        Nice64RudpcSampleDisposition disposition = controlTimeout
            ? Nice64RudpcSampleDisposition.ControlInconclusive
            : Nice64RudpcSampleDisposition.Fail;
        return new(item, data, control, disposition,
            65_536, 0, 0, 0, 128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, false, error.Message);
    }

    public static async Task<IReadOnlyList<Nice64RudpcCharacterizationSample>> ExecutePlanAsync(
        IEnumerable<Nice64RudpcCharacterizationCase> plan,
        Func<Nice64RudpcCharacterizationCase,
            Task<Nice64RudpcCharacterizationSample>> execute,
        Action<Nice64RudpcCharacterizationSample>? observed = null)
    {
        List<Nice64RudpcCharacterizationSample> results = [];
        int consecutiveHelloFailures = 0;
        foreach (Nice64RudpcCharacterizationCase item in plan)
        {
            Nice64RudpcCharacterizationSample row;
            try
            {
                row = await execute(item);
                consecutiveHelloFailures = 0;
            }
            catch (Exception ex)
            {
                row = FromException(item, ex);
                consecutiveHelloFailures = ex.Message.StartsWith("HELLO_INFRASTRUCTURE:",
                    StringComparison.Ordinal) ? consecutiveHelloFailures + 1 : 0;
            }
            results.Add(row);
            observed?.Invoke(row);
            if (consecutiveHelloFailures >= 3) break;
        }
        return results;
    }

    public static Nice64RudpcCharacterizationSummary Summarize(
        Nice64RudpcCharacterizationTest test,
        IEnumerable<Nice64RudpcCharacterizationSample> source)
    {
        Nice64RudpcCharacterizationSample[] rows = source
            .Where(row => row.Case.Test == test).ToArray();
        if (rows.Length == 0) throw new ArgumentException("At least one sample is required.");
        double[] elapsed = rows.Where(row => row.ElapsedMilliseconds > 0)
            .Select(row => row.ElapsedMilliseconds).Order().ToArray();
        double[] rates = rows.Where(row => row.RateKibPerSecond > 0)
            .Select(row => row.RateKibPerSecond).Order().ToArray();
        if (elapsed.Length == 0) elapsed = [0];
        if (rates.Length == 0) rates = [0];
        long[] retransmissions = rows.Select(row => row.HostRetransmissions).Order().ToArray();
        int passed = rows.Count(row => row.Disposition == Nice64RudpcSampleDisposition.Pass);
        int failed = rows.Count(row => row.Disposition == Nice64RudpcSampleDisposition.Fail);
        int inconclusive = rows.Length - passed - failed;
        return new(test, rows.Length, passed, failed, inconclusive,
            100.0 * passed / rows.Length,
            elapsed[0], elapsed[^1], elapsed.Average(), Median(elapsed),
            rates[0], rates[^1], rates.Average(), Median(rates),
            retransmissions.Sum(), retransmissions[0], retransmissions[^1],
            Median(retransmissions), rows.Count(row => row.StagingFullDelta > 0),
            rows.Aggregate(0UL, (total, row) => total + row.StagingFullDelta),
            rows.Count(row => row.HostRetryExhaustion != 0 ||
                              row.FirmwareRetryExhaustionDelta != 0),
            rows.Aggregate(0UL, (total, row) => total + row.OldOrStaleDelta),
            rows.Aggregate(0UL, (total, row) => total + row.OutOfOrderDelta));
    }

    private static double Median(double[] ordered) => ordered.Length % 2 == 0
        ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2.0
        : ordered[ordered.Length / 2];

    private static double Median(long[] ordered) => ordered.Length % 2 == 0
        ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2.0
        : ordered[ordered.Length / 2];
}
