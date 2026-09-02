namespace PicoSwitchController;

public enum Nice64RudpcCloseoutTest
{
    BoundedTerminalExhaustion,
    FreshSessionRecovery,
    TwoMiBSoak
}

public enum Nice64RudpcCloseoutDisposition
{
    Pass,
    Fail,
    ControlInconclusive
}

public sealed record Nice64RudpcCloseoutCase(
    Nice64RudpcCloseoutTest Test,
    int RtoMs,
    int FlightCap,
    uint InitialSequence,
    int ByteCount)
{
    public bool IsDownload => Test == Nice64RudpcCloseoutTest.BoundedTerminalExhaustion;

    public Nice64RudpcImpairment CreateImpairment() => Test switch
    {
        Nice64RudpcCloseoutTest.BoundedTerminalExhaustion => new(DropDataAlways: 3),
        Nice64RudpcCloseoutTest.TwoMiBSoak =>
            new(DropDataOnce: 3, DuplicateData: 10, ReorderWithNext: 20),
        _ => new()
    };
}

public sealed record Nice64RudpcCloseoutResult(
    Nice64RudpcCloseoutCase Case,
    uint SessionId,
    Nice64RudpcCloseoutDisposition Disposition,
    string DataResult,
    Nice64RudpcControlResult ControlResult,
    Nice64RudpcTransferResult? Transfer,
    string? Error);

public static class Nice64RudpcCloseout
{
    public static IReadOnlyList<Nice64RudpcCloseoutCase> CreatePlan(
        int rtoMs, int flightCap)
    {
        if (rtoMs != 40) throw new ArgumentOutOfRangeException(nameof(rtoMs));
        if (flightCap != 2) throw new ArgumentOutOfRangeException(nameof(flightCap));
        return
        [
            new(Nice64RudpcCloseoutTest.BoundedTerminalExhaustion,
                rtoMs, flightCap, 0x80000000u, 65_536),
            new(Nice64RudpcCloseoutTest.FreshSessionRecovery,
                rtoMs, flightCap, 0x90000000u, 65_536),
            new(Nice64RudpcCloseoutTest.TwoMiBSoak,
                rtoMs, flightCap, 0xA0000000u, 2 * 1024 * 1024)
        ];
    }

    public static Nice64RudpcCloseoutResult FromTransfer(
        Nice64RudpcCloseoutCase item, uint sessionId,
        Nice64RudpcTransferResult result)
    {
        Nice64RudpcControlResult control = result.ControlResult switch
        {
            "TIMEOUT_0x91" => Nice64RudpcControlResult.Timeout0x91,
            "TIMEOUT_0x96" => Nice64RudpcControlResult.Timeout0x96,
            _ => Nice64RudpcControlResult.Pass
        };
        bool firmware = result.FirmwareDiagnosticsAfterAvailable;
        uint Delta(string name) => firmware
            ? result.FirmwareDiagnostics.Delta(name, result.FirmwareDiagnosticsBefore) : 0;

        if (item.Test == Nice64RudpcCloseoutTest.BoundedTerminalExhaustion)
        {
            bool bounded = !result.Passed && result.Status == 3 && firmware &&
                Delta("retry_exhaustion") > 0 &&
                result.FirmwareDiagnostics["maximum_retry_count"] == 4 &&
                result.Elapsed < TimeSpan.FromSeconds(10);
            Nice64RudpcCloseoutDisposition disposition = bounded
                ? Nice64RudpcCloseoutDisposition.Pass
                : control != Nice64RudpcControlResult.Pass
                    ? Nice64RudpcCloseoutDisposition.ControlInconclusive
                    : Nice64RudpcCloseoutDisposition.Fail;
            return new(item, sessionId, disposition,
                bounded ? "EXPECTED_EXHAUSTION_PASS" : "EXPECTED_EXHAUSTION_NOT_PROVEN",
                control, result, null);
        }

        uint expectedDeliveries = (uint)((item.ByteCount + 511) / 512);
        bool exact = result.Passed && result.ExpectedBytes == item.ByteCount &&
            result.ActualOrderedBytes == item.ByteCount &&
            result.ExpectedCrc32 == result.ActualCrc32 &&
            result.UpperDeliveries == expectedDeliveries &&
            result.HostDiagnostics.RetryExhaustion == 0 &&
            result.FinalHostOutstanding == 0 &&
            result.MaximumOutstanding <= item.FlightCap &&
            (!firmware || (Delta("retry_exhaustion") == 0 &&
                           Delta("duplicate_upper_deliveries") == 0 &&
                           result.FirmwareDiagnostics["rx_occupied_slots"] == 0));
        Nice64RudpcCloseoutDisposition successfulDisposition = !exact
            ? Nice64RudpcCloseoutDisposition.Fail
            : control == Nice64RudpcControlResult.Pass
                ? Nice64RudpcCloseoutDisposition.Pass
                : Nice64RudpcCloseoutDisposition.ControlInconclusive;
        return new(item, sessionId, successfulDisposition,
            exact ? "DATA_PASS" : "DATA_FAIL", control, result, null);
    }

    public static Nice64RudpcCloseoutResult FromException(
        Nice64RudpcCloseoutCase item, uint sessionId, Exception error)
    {
        Nice64RudpcCharacterizationSample classified =
            Nice64RudpcRtoCharacterization.FromException(
                new(1, Nice64RudpcCharacterizationTest.Baseline,
                    item.RtoMs, item.FlightCap, item.InitialSequence), error);
        Nice64RudpcCloseoutDisposition disposition = classified.Disposition ==
            Nice64RudpcSampleDisposition.ControlInconclusive
                ? Nice64RudpcCloseoutDisposition.ControlInconclusive
                : Nice64RudpcCloseoutDisposition.Fail;
        return new(item, sessionId, disposition, classified.DataResult.ToString(),
            classified.ControlResult, null, error.Message);
    }
}
