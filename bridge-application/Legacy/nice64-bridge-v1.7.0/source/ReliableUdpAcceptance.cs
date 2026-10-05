namespace PicoSwitchController;

public enum Nice64RudpcAcceptanceTest
{
    Baseline,
    ForcedHole,
    AckLoss,
    DuplicateData,
    DuplicateAck,
    AdjacentReorder,
    SequenceWrap,
    PausedConsumer
}

public enum Nice64RudpcAcceptanceRunDisposition
{
    RunPass,
    RunFail,
    RunControlInconclusive
}

public sealed record Nice64RudpcAcceptanceCase(
    int Run,
    Nice64RudpcAcceptanceTest Test,
    int RtoMs,
    int FlightCap,
    uint InitialSequence,
    int ByteCount)
{
    public bool IsDownload => Test == Nice64RudpcAcceptanceTest.DuplicateAck;

    public Nice64RudpcImpairment CreateImpairment() => Test switch
    {
        Nice64RudpcAcceptanceTest.ForcedHole => new(DropDataOnce: 3),
        Nice64RudpcAcceptanceTest.AckLoss => new(DropAckOnce: 1),
        Nice64RudpcAcceptanceTest.DuplicateData => new(DuplicateData: 2),
        Nice64RudpcAcceptanceTest.DuplicateAck => new(DuplicateAck: 3),
        Nice64RudpcAcceptanceTest.AdjacentReorder => new(ReorderWithNext: 2),
        Nice64RudpcAcceptanceTest.SequenceWrap => new(DropDataOnce: 3),
        Nice64RudpcAcceptanceTest.PausedConsumer =>
            new(PauseConsumerMilliseconds: 250, PausedConsumerFlightCap: 8),
        _ => new()
    };
}

public sealed record Nice64RudpcAcceptanceCaseResult(
    Nice64RudpcAcceptanceCase Case,
    uint SessionId,
    Nice64RudpcDataResult DataResult,
    Nice64RudpcControlResult ControlResult,
    Nice64RudpcSampleDisposition Disposition,
    Nice64RudpcTransferResult? Transfer,
    string DuplicateCoverage,
    string? Error);

public sealed record Nice64RudpcAcceptanceAggregate(
    int RequestedRuns,
    int CompletePassRuns,
    int ControlInconclusiveRuns,
    int FailedRuns,
    int InitiatedDataCases,
    int DataPass,
    int DataFail,
    int RetryExhaustionCases,
    int CrcFailures,
    int DeliveryCountFailures,
    int HostLeakCases,
    int RxLeakCases);

public static class Nice64RudpcAcceptance
{
    public static IReadOnlyList<Nice64RudpcAcceptanceCase> CreatePlan(
        int rtoMs, int flightCap, int runs)
    {
        if (rtoMs is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(rtoMs));
        if (flightCap != 2) throw new ArgumentOutOfRangeException(nameof(flightCap));
        if (runs is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(runs));
        List<Nice64RudpcAcceptanceCase> plan = new(runs * 8);
        foreach (int run in Enumerable.Range(1, runs))
        {
            uint runBase = unchecked(0x20000000u + (uint)run * 0x01000000u);
            foreach (Nice64RudpcAcceptanceTest test in
                     Enum.GetValues<Nice64RudpcAcceptanceTest>())
            {
                uint sequence = test == Nice64RudpcAcceptanceTest.SequenceWrap
                    ? 0xFFFFFFFCu
                    : runBase + (uint)test * 0x00010000u;
                int bytes = test == Nice64RudpcAcceptanceTest.SequenceWrap ? 4096 : 65_536;
                plan.Add(new(run, test, rtoMs, flightCap, sequence, bytes));
            }
        }
        return plan;
    }

    public static Nice64RudpcAcceptanceCaseResult FromTransfer(
        Nice64RudpcAcceptanceCase item, uint sessionId,
        Nice64RudpcTransferResult result)
    {
        Nice64RudpcCharacterizationSample core =
            Nice64RudpcRtoCharacterization.FromTransfer(
                new(item.Run, Nice64RudpcCharacterizationTest.Baseline,
                    item.RtoMs, item.FlightCap, item.InitialSequence), result);
        bool firmware = result.FirmwareDiagnosticsAfterAvailable;
        uint Delta(string name) => firmware
            ? result.FirmwareDiagnostics.Delta(name, result.FirmwareDiagnosticsBefore) : 0;
        uint expectedDeliveries = (uint)((item.ByteCount + 511) / 512);
        bool exact = result.Passed && result.ExpectedBytes == item.ByteCount &&
            result.ActualOrderedBytes == item.ByteCount &&
            result.ExpectedCrc32 == result.ActualCrc32 &&
            (item.IsDownload || result.UpperDeliveries == expectedDeliveries) &&
            result.HostDiagnostics.RetryExhaustion == 0 &&
            result.FinalHostOutstanding == 0 &&
            (item.Test == Nice64RudpcAcceptanceTest.PausedConsumer ||
             result.MaximumOutstanding <= item.FlightCap) &&
            (!firmware || (Delta("retry_exhaustion") == 0 &&
                           result.FirmwareDiagnostics["rx_occupied_slots"] == 0));
        string duplicateCoverage = "NOT_APPLICABLE";
        bool caseEvidence = exact;
        if (item.Test == Nice64RudpcAcceptanceTest.ForcedHole)
            caseEvidence &= result.RetransmittedOffsets.Contains(3);
        else if (item.Test == Nice64RudpcAcceptanceTest.AckLoss && firmware)
            caseEvidence &= Delta("duplicate_upper_deliveries") == 0;
        else if (item.Test == Nice64RudpcAcceptanceTest.DuplicateData)
        {
            Nice64RudpcDuplicateAcceptanceResult duplicate =
                Nice64RudpcDuplicateAcceptance.Evaluate(result.DuplicateInjected,
                    Delta("rx_datagrams"), result.HostDiagnostics.TxDatagrams,
                    (uint)result.ExpectedBytes, result.ActualOrderedBytes,
                    result.ExpectedCrc32, result.ActualCrc32,
                    expectedDeliveries, result.UpperDeliveries,
                    Delta("duplicate_upper_deliveries"), Delta("duplicate_rx"),
                    Delta("old_or_stale_rx"), Delta("retry_exhaustion"),
                    firmware ? result.FirmwareDiagnostics["rx_occupied_slots"] : 0);
            duplicateCoverage = duplicate.Disposition;
            caseEvidence &= duplicate.TransportPassed && duplicate.InjectionAttempted;
        }
        else if (item.Test == Nice64RudpcAcceptanceTest.AdjacentReorder && firmware)
            caseEvidence &= Delta("out_of_order_rx") != 0;
        else if (item.Test == Nice64RudpcAcceptanceTest.PausedConsumer)
            caseEvidence &= result.ResumeEvidence is { StateAcknowledged: true,
                HostOutstandingBefore: 8, AckBitmapAfterDrain: 0,
                HostOutstandingAfter: 0 } &&
                result.ResumeEvidence.AckBaseAfterDrain == item.InitialSequence + 7u &&
                result.PacingEvidence is { NormalCap: 2, PauseOverrideCap: 8,
                    MaximumPausedOutstanding: 8, NormalCapRestored: true,
                    MaximumPostResumeOutstanding: <= 2, PausedAckBitmap: 0xFF } &&
                result.PacingEvidence.PausedAckBase == item.InitialSequence - 1u;

        Nice64RudpcDataResult data = caseEvidence
            ? core.DataResult
            : core.DataResult == Nice64RudpcDataResult.FailRetryExhaustion
                ? core.DataResult : Nice64RudpcDataResult.FailTransport;
        Nice64RudpcSampleDisposition disposition = data != Nice64RudpcDataResult.Pass
            ? Nice64RudpcSampleDisposition.Fail
            : core.ControlResult == Nice64RudpcControlResult.Pass
                ? Nice64RudpcSampleDisposition.Pass
                : Nice64RudpcSampleDisposition.ControlInconclusive;
        return new(item, sessionId, data, core.ControlResult, disposition,
            result, duplicateCoverage, null);
    }

    public static Nice64RudpcAcceptanceCaseResult FromException(
        Nice64RudpcAcceptanceCase item, Exception error, uint sessionId = 0)
    {
        Nice64RudpcCharacterizationSample core =
            Nice64RudpcRtoCharacterization.FromException(
                new(item.Run, Nice64RudpcCharacterizationTest.Baseline,
                    item.RtoMs, item.FlightCap, item.InitialSequence), error);
        return new(item, sessionId, core.DataResult, core.ControlResult,
            core.Disposition, null, "NOT_APPLICABLE", error.Message);
    }

    public static async Task<IReadOnlyList<Nice64RudpcAcceptanceCaseResult>> ExecutePlanAsync(
        IEnumerable<Nice64RudpcAcceptanceCase> plan,
        Func<Nice64RudpcAcceptanceCase, Task<Nice64RudpcAcceptanceCaseResult>> execute,
        Action<Nice64RudpcAcceptanceCaseResult>? observed = null)
    {
        List<Nice64RudpcAcceptanceCaseResult> results = [];
        int consecutiveHelloFailures = 0;
        foreach (Nice64RudpcAcceptanceCase item in plan)
        {
            Nice64RudpcAcceptanceCaseResult row;
            try
            {
                row = await execute(item);
            }
            catch (Exception ex)
            {
                row = FromException(item, ex);
            }
            consecutiveHelloFailures = row.Error?.StartsWith("HELLO_INFRASTRUCTURE:",
                StringComparison.Ordinal) == true ? consecutiveHelloFailures + 1 : 0;
            results.Add(row);
            observed?.Invoke(row);
            if (consecutiveHelloFailures >= 3) break;
        }
        return results;
    }

    public static Nice64RudpcAcceptanceRunDisposition ClassifyRun(
        int run, IEnumerable<Nice64RudpcAcceptanceCaseResult> source)
    {
        Nice64RudpcAcceptanceCaseResult[] rows = source
            .Where(row => row.Case.Run == run).ToArray();
        if (rows.Any(row => row.Disposition == Nice64RudpcSampleDisposition.Fail))
            return Nice64RudpcAcceptanceRunDisposition.RunFail;
        if (rows.Length != 8 || rows.Any(row =>
                row.Disposition == Nice64RudpcSampleDisposition.ControlInconclusive))
            return Nice64RudpcAcceptanceRunDisposition.RunControlInconclusive;
        return Nice64RudpcAcceptanceRunDisposition.RunPass;
    }

    public static Nice64RudpcAcceptanceAggregate Summarize(
        int requestedRuns, IEnumerable<Nice64RudpcAcceptanceCaseResult> source)
    {
        Nice64RudpcAcceptanceCaseResult[] rows = source.ToArray();
        Nice64RudpcAcceptanceRunDisposition[] runs = Enumerable.Range(1, requestedRuns)
            .Select(run => ClassifyRun(run, rows)).ToArray();
        Nice64RudpcTransferResult[] initiated = rows.Where(row => row.Transfer is not null)
            .Select(row => row.Transfer!).ToArray();
        return new(requestedRuns,
            runs.Count(x => x == Nice64RudpcAcceptanceRunDisposition.RunPass),
            runs.Count(x => x == Nice64RudpcAcceptanceRunDisposition.RunControlInconclusive),
            runs.Count(x => x == Nice64RudpcAcceptanceRunDisposition.RunFail),
            initiated.Length,
            rows.Count(row => row.Transfer is not null &&
                              row.DataResult == Nice64RudpcDataResult.Pass),
            rows.Count(row => row.Transfer is not null &&
                              row.DataResult is Nice64RudpcDataResult.FailRetryExhaustion or
                                  Nice64RudpcDataResult.FailTransport),
            initiated.Count(result => result.HostDiagnostics.RetryExhaustion != 0 ||
                (result.FirmwareDiagnosticsAfterAvailable &&
                 result.FirmwareDiagnostics.Delta("retry_exhaustion",
                     result.FirmwareDiagnosticsBefore) != 0)),
            initiated.Count(result => result.ExpectedCrc32 != result.ActualCrc32),
            rows.Count(row => row.Transfer is { } result && !row.Case.IsDownload &&
                result.UpperDeliveries != (uint)((row.Case.ByteCount + 511) / 512)),
            initiated.Count(result => result.FinalHostOutstanding != 0),
            initiated.Count(result => result.FirmwareDiagnosticsAfterAvailable &&
                result.FirmwareDiagnostics["rx_occupied_slots"] != 0));
    }

    public static bool HasUniqueNonzeroSessions(
        IEnumerable<Nice64RudpcAcceptanceCaseResult> results)
    {
        uint[] sessions = results.Select(row => row.SessionId).ToArray();
        return sessions.All(session => session != 0) && sessions.Distinct().Count() == sessions.Length;
    }
}
