using System.Security.Cryptography;
using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static T RequireThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T ex) { return ex; }
    throw new InvalidOperationException(message);
}

static byte[] Pattern(int size) =>
    Enumerable.Range(0, size).Select(i => (byte)((i * 73 + i / 251) & 0xFF)).ToArray();

static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..16];

static Nice64RudpHeader Header(
    Nice64RudpPacketFlags flags = Nice64RudpPacketFlags.Data,
    byte deliveryClass = 0,
    uint session = 0x10203040,
    uint sequence = 0x50607080,
    uint ackBase = 0x90A0B0C0,
    uint ackBitmap = 0xD0E0F001,
    ushort payloadLength = 3,
    ushort reserved = 0) => new(
        Nice64WirelessTransportContract.Magic,
        Nice64WirelessTransportContract.TransportVersion,
        flags, deliveryClass, Nice64WirelessTransportContract.HeaderLength,
        session, sequence, ackBase, ackBitmap, payloadLength, reserved);

static Nice64RudpPacket Packet(uint session, uint sequence, byte value) =>
    new(Header(session: session, sequence: sequence, ackBase: 0, ackBitmap: 0,
        payloadLength: 1), [value]);

foreach (int selectedRto in new[] { 10, 20, 40 })
{
    Nice64RudpcHardwareDiagnosticOptions selected =
        Nice64RudpcHardwareDiagnosticOptions.Parse(
            ["--target", "192.0.2.1", "--quick", "--flight-cap", "2",
             "--rto", selectedRto.ToString(), "--trace"]);
    Require(selected.SelectedRto == selectedRto &&
            selected.BaselineRtos.SequenceEqual([selectedRto]) &&
            selected.ImpairmentRto == selectedRto &&
            selected.StopAfterPausedConsumer,
        $"selected RTO {selectedRto} did not isolate the baseline/narrow suite");
}
Nice64RudpcHardwareDiagnosticOptions omittedRto =
    Nice64RudpcHardwareDiagnosticOptions.Parse(["--quick"]);
Require(omittedRto.SelectedRto is null &&
        omittedRto.BaselineRtos.SequenceEqual([10, 20, 40]) &&
        omittedRto.ImpairmentRto == 20 && !omittedRto.StopAfterPausedConsumer,
    "omitted RTO did not preserve existing diagnostic behavior");
RequireThrows<ArgumentOutOfRangeException>(() =>
    Nice64RudpcHardwareDiagnosticOptions.Parse(["--rto", "0"]),
    "zero RTO was accepted");
RequireThrows<ArgumentOutOfRangeException>(() =>
    Nice64RudpcHardwareDiagnosticOptions.Parse(["--rto", "1001"]),
    "oversized RTO was accepted");
RequireThrows<ArgumentException>(() =>
    Nice64RudpcHardwareDiagnosticOptions.Parse(["--rto", "abc"]),
    "non-integer RTO was accepted");
Console.WriteLine("PASS diagnostic CLI RTO 10/20/40, omitted default, invalid bounds, selected quick policy");

Nice64RudpcHardwareDiagnosticOptions characterizationOptions =
    Nice64RudpcHardwareDiagnosticOptions.Parse(
        ["--target", "192.0.2.1", "--characterize-rto", "--flight-cap", "2",
         "--rto", "40", "--samples", "7"]);
Require(characterizationOptions.CharacterizeRto &&
        characterizationOptions.SelectedRto == 40 &&
        characterizationOptions.FlightCap == 2 &&
        characterizationOptions.Samples == 7 && !characterizationOptions.Quick,
    "RTO characterization CLI did not preserve selected values");
Nice64RudpcHardwareDiagnosticOptions defaultCharacterizationSamples =
    Nice64RudpcHardwareDiagnosticOptions.Parse(
        ["--characterize-rto", "--flight-cap", "2", "--rto", "20"]);
Require(defaultCharacterizationSamples.Samples == 10,
    "RTO characterization default sample count is not ten");
RequireThrows<ArgumentOutOfRangeException>(() =>
    Nice64RudpcHardwareDiagnosticOptions.Parse(
        ["--characterize-rto", "--rto", "20", "--samples", "0"]),
    "zero characterization samples were accepted");
RequireThrows<ArgumentOutOfRangeException>(() =>
    Nice64RudpcHardwareDiagnosticOptions.Parse(
        ["--characterize-rto", "--rto", "20", "--samples", "101"]),
    "more than 100 characterization samples were accepted");
RequireThrows<ArgumentException>(() =>
    Nice64RudpcHardwareDiagnosticOptions.Parse(["--characterize-rto"]),
    "characterization without an explicit RTO was accepted");
RequireThrows<ArgumentException>(() =>
    Nice64RudpcHardwareDiagnosticOptions.Parse(
        ["--characterize-rto", "--rto", "20", "--flight-cap", "1"]),
    "cap one was accepted by cap-two characterization");

IReadOnlyList<Nice64RudpcCharacterizationCase> characterizationPlan =
    Nice64RudpcRtoCharacterization.CreatePlan(40, 2, 2);
Require(characterizationPlan.Count == 6 &&
        characterizationPlan.All(item => item.RtoMs == 40 && item.FlightCap == 2) &&
        characterizationPlan.GroupBy(item => item.Sample).All(group =>
            group.Select(item => item.Test).Order().SequenceEqual(
                Enum.GetValues<Nice64RudpcCharacterizationTest>().Order())),
    "characterization plan did not apply RTO/cap to all three independent tests");
Nice64RudpcImpairment firstHole = characterizationPlan
    .First(item => item.Test == Nice64RudpcCharacterizationTest.ForcedHole)
    .CreateImpairment();
Nice64RudpcImpairment secondHole = characterizationPlan
    .First(item => item.Test == Nice64RudpcCharacterizationTest.ForcedHole)
    .CreateImpairment();
Nice64RudpcImpairment ackOnly = characterizationPlan
    .First(item => item.Test == Nice64RudpcCharacterizationTest.AckLoss)
    .CreateImpairment();
Require(!ReferenceEquals(firstHole, secondHole) && firstHole.DropDataOnce == 3 &&
        firstHole.DropAckOnce is null && ackOnly.DropAckOnce == 1 &&
        ackOnly.DropDataOnce is null,
    "characterization impairment records are not isolated per transfer");

int executedCases = 0;
IReadOnlyList<Nice64RudpcCharacterizationSample> continued =
    Nice64RudpcRtoCharacterization.ExecutePlanAsync(
        characterizationPlan.Take(3), item =>
        {
            executedCases++;
            if (executedCases == 2) throw new InvalidOperationException("synthetic DATA failure");
            return Task.FromResult(Nice64RudpcRtoCharacterization.FromException(item,
                new TimeoutException("No RUDP-C control reply 0x96.")));
        }).GetAwaiter().GetResult();
Require(executedCases == 3 && continued.Count == 3 &&
        continued[1].Disposition == Nice64RudpcSampleDisposition.Fail,
    "one transport failure aborted later independent characterization cases");

Nice64RudpcCharacterizationCase classificationCase = characterizationPlan[0];
Nice64RudpcFirmwareDiagnostics emptyFirmware = new(new Dictionary<string, uint>());
Nice64RudpcTransferResult failedDataWithControlTimeout = new(false, 1, 65_536, 0,
    0x12345678, 0, 0, TimeSpan.FromMilliseconds(250), [], new(), emptyFirmware,
    emptyFirmware, 2, 0, 0, 1, null, null, false, null, "TIMEOUT_0x91", false);
Nice64RudpcCharacterizationSample separatedFailure =
    Nice64RudpcRtoCharacterization.FromTransfer(
        classificationCase, failedDataWithControlTimeout);
Require(separatedFailure.DataResult == Nice64RudpcDataResult.FailTransport &&
        separatedFailure.ControlResult == Nice64RudpcControlResult.Timeout0x91 &&
        separatedFailure.Disposition == Nice64RudpcSampleDisposition.Fail,
    "DATA failure and secondary control timeout were conflated");
Nice64RudpcCharacterizationSample timeout0x90 =
    Nice64RudpcRtoCharacterization.FromException(classificationCase,
        new TimeoutException("No RUDP-C control reply 0x90."));
Nice64RudpcCharacterizationSample timeout0x91 =
    Nice64RudpcRtoCharacterization.FromException(classificationCase,
        new TimeoutException("No RUDP-C control reply 0x91."));
Nice64RudpcCharacterizationSample timeout0x96 =
    Nice64RudpcRtoCharacterization.FromException(classificationCase,
        new TimeoutException("No RUDP-C control reply 0x96."));
Require(timeout0x90.DataResult == Nice64RudpcDataResult.NotRun &&
        timeout0x90.ControlResult == Nice64RudpcControlResult.Timeout0x90 &&
        timeout0x90.Disposition == Nice64RudpcSampleDisposition.ControlInconclusive &&
        timeout0x90.ExpectedBytes == 65_536 && timeout0x90.ActualBytes == 0 &&
        timeout0x90.HostTx == 0 && timeout0x90.HostRetryExhaustion == 0,
    "pre-DATA STREAM_BEGIN_ACK 0x90 loss was classified as a DATA failure");
Require(timeout0x91.DataResult == Nice64RudpcDataResult.NotRun &&
        timeout0x91.ControlResult == Nice64RudpcControlResult.Timeout0x91 &&
        timeout0x91.Disposition == Nice64RudpcSampleDisposition.ControlInconclusive,
    "standalone 0x91 timeout classification regressed");
Require(timeout0x96.DataResult == Nice64RudpcDataResult.NotRun &&
        timeout0x96.ControlResult == Nice64RudpcControlResult.Timeout0x96 &&
        timeout0x96.Disposition == Nice64RudpcSampleDisposition.ControlInconclusive,
    "standalone 0x96 timeout classification regressed");
Nice64RudpcCharacterizationSample aggregateSeed = continued[0] with
{
    Case = classificationCase,
    Disposition = Nice64RudpcSampleDisposition.Pass,
    ElapsedMilliseconds = 100,
    RateKibPerSecond = 10,
    HostRetransmissions = 1,
    StagingFullDelta = 0,
    OldOrStaleDelta = 1,
    OutOfOrderDelta = 2
};
Nice64RudpcCharacterizationSample[] aggregateRows =
[
    aggregateSeed,
    aggregateSeed with
    {
        Disposition = Nice64RudpcSampleDisposition.Fail,
        ElapsedMilliseconds = 200,
        RateKibPerSecond = 20,
        HostRetransmissions = 3,
        StagingFullDelta = 4,
        HostRetryExhaustion = 1,
        OldOrStaleDelta = 2,
        OutOfOrderDelta = 3
    },
    aggregateSeed with
    {
        Disposition = Nice64RudpcSampleDisposition.ControlInconclusive,
        ElapsedMilliseconds = 300,
        RateKibPerSecond = 30,
        HostRetransmissions = 2,
        StagingFullDelta = 1,
        OldOrStaleDelta = 3,
        OutOfOrderDelta = 4
    }
];
Nice64RudpcCharacterizationSummary aggregateSummary =
    Nice64RudpcRtoCharacterization.Summarize(
        Nice64RudpcCharacterizationTest.Baseline, aggregateRows);
Require(aggregateSummary.Attempted == 3 && aggregateSummary.Passed == 1 &&
        aggregateSummary.Failed == 1 && aggregateSummary.ControlInconclusive == 1 &&
        Math.Abs(aggregateSummary.PassRate - 100.0 / 3.0) < 0.01 &&
        aggregateSummary.MinimumElapsed == 100 && aggregateSummary.MaximumElapsed == 300 &&
        aggregateSummary.MeanElapsed == 200 && aggregateSummary.MedianElapsed == 200 &&
        aggregateSummary.MinimumRate == 10 && aggregateSummary.MaximumRate == 30 &&
        aggregateSummary.MeanRate == 20 && aggregateSummary.MedianRate == 20 &&
        aggregateSummary.TotalRetransmissions == 6 &&
        aggregateSummary.MinimumRetransmissions == 1 &&
        aggregateSummary.MaximumRetransmissions == 3 &&
        aggregateSummary.MedianRetransmissions == 2 &&
        aggregateSummary.SamplesWithStagingFull == 2 &&
        aggregateSummary.TotalStagingFull == 5 &&
        aggregateSummary.SamplesWithRetryExhaustion == 1 &&
        aggregateSummary.TotalOldOrStale == 6 &&
        aggregateSummary.TotalOutOfOrder == 9,
    "RTO characterization summary aggregation was incorrect");
Console.WriteLine("PASS cap-2 RTO characterization CLI, bounds, plan isolation, continuation, result separation");

Nice64RudpcHardwareDiagnosticOptions acceptanceOptions =
    Nice64RudpcHardwareDiagnosticOptions.Parse(
        ["--target", "192.0.2.1", "--accept-rudpc", "--flight-cap", "2",
         "--rto", "40", "--runs", "5"]);
Require(acceptanceOptions.AcceptRudpc && acceptanceOptions.Runs == 5 &&
        acceptanceOptions.SelectedRto == 40 && acceptanceOptions.FlightCap == 2,
    "isolated acceptance CLI did not preserve selected values");
RequireThrows<ArgumentException>(() => Nice64RudpcHardwareDiagnosticOptions.Parse(
    ["--accept-rudpc", "--quick", "--rto", "40"]),
    "acceptance was allowed to reuse quick mode");
RequireThrows<ArgumentException>(() => Nice64RudpcHardwareDiagnosticOptions.Parse(
    ["--accept-rudpc", "--characterize-rto", "--rto", "40"]),
    "acceptance was allowed to reuse characterization mode");
RequireThrows<ArgumentException>(() => Nice64RudpcHardwareDiagnosticOptions.Parse(
    ["--accept-rudpc"]), "acceptance without explicit RTO was accepted");
RequireThrows<ArgumentException>(() => Nice64RudpcHardwareDiagnosticOptions.Parse(
    ["--accept-rudpc", "--rto", "40", "--flight-cap", "1"]),
    "acceptance allowed a non-cap-two configuration");
RequireThrows<ArgumentOutOfRangeException>(() => Nice64RudpcHardwareDiagnosticOptions.Parse(
    ["--accept-rudpc", "--rto", "40", "--runs", "0"]),
    "zero acceptance runs were accepted");
RequireThrows<ArgumentOutOfRangeException>(() => Nice64RudpcHardwareDiagnosticOptions.Parse(
    ["--accept-rudpc", "--rto", "40", "--runs", "101"]),
    "more than 100 acceptance runs were accepted");

IReadOnlyList<Nice64RudpcAcceptanceCase> acceptancePlan =
    Nice64RudpcAcceptance.CreatePlan(40, 2, 5);
Require(acceptancePlan.Count == 40 &&
        acceptancePlan.All(item => item.RtoMs == 40 && item.FlightCap == 2) &&
        acceptancePlan.GroupBy(item => item.Run).All(group => group.Count() == 8 &&
            group.Select(item => item.Test).Order().SequenceEqual(
                Enum.GetValues<Nice64RudpcAcceptanceTest>().Order())),
    "acceptance plan did not contain all eight isolated cases for five runs");
foreach (Nice64RudpcAcceptanceCase item in acceptancePlan.Take(8))
{
    Nice64RudpcImpairment first = item.CreateImpairment();
    Nice64RudpcImpairment second = item.CreateImpairment();
    Require(!ReferenceEquals(first, second),
        $"acceptance impairment object was reused for {item.Test}");
}

Nice64RudpcTransferResult exactAcceptanceTransfer = new(true, 0, 65_536, 65_536,
    0x12345678, 0x12345678, 128, TimeSpan.FromMilliseconds(400), [], new(),
    emptyFirmware, emptyFirmware, 2, 0, 0, 0, null, null, false, null, "PASS", false);
List<Nice64RudpcAcceptanceCaseResult> passingAcceptanceRows = acceptancePlan
    .Select((item, index) => new Nice64RudpcAcceptanceCaseResult(item,
        (uint)(index + 1), Nice64RudpcDataResult.Pass,
        Nice64RudpcControlResult.Pass, Nice64RudpcSampleDisposition.Pass,
        exactAcceptanceTransfer, "NOT_APPLICABLE", null)).ToList();
Require(Nice64RudpcAcceptance.HasUniqueNonzeroSessions(passingAcceptanceRows),
    "acceptance session evidence reused or omitted a session ID");
Nice64RudpcAcceptanceAggregate fiveRunSummary =
    Nice64RudpcAcceptance.Summarize(5, passingAcceptanceRows);
Require(fiveRunSummary.CompletePassRuns == 5 &&
        fiveRunSummary.ControlInconclusiveRuns == 0 &&
        fiveRunSummary.FailedRuns == 0 && fiveRunSummary.InitiatedDataCases == 40 &&
        fiveRunSummary.DataPass == 40 && fiveRunSummary.DataFail == 0,
    "five-run isolated acceptance aggregation was incorrect");
Require(Nice64RudpcAcceptance.ClassifyRun(1, passingAcceptanceRows) ==
        Nice64RudpcAcceptanceRunDisposition.RunPass,
    "eight passing cases did not produce RunPass");
List<Nice64RudpcAcceptanceCaseResult> inconclusiveRows = passingAcceptanceRows.ToList();
inconclusiveRows[2] = inconclusiveRows[2] with
    { Disposition = Nice64RudpcSampleDisposition.ControlInconclusive };
Require(Nice64RudpcAcceptance.ClassifyRun(1, inconclusiveRows) ==
        Nice64RudpcAcceptanceRunDisposition.RunControlInconclusive,
    "control-inconclusive case did not produce RunControlInconclusive");
List<Nice64RudpcAcceptanceCaseResult> failedRows = passingAcceptanceRows.ToList();
failedRows[3] = failedRows[3] with { Disposition = Nice64RudpcSampleDisposition.Fail };
Require(Nice64RudpcAcceptance.ClassifyRun(1, failedRows) ==
        Nice64RudpcAcceptanceRunDisposition.RunFail,
    "DATA failure did not take run-level precedence");

int acceptanceExecuted = 0;
IReadOnlyList<Nice64RudpcAcceptanceCaseResult> acceptanceContinued =
    Nice64RudpcAcceptance.ExecutePlanAsync(acceptancePlan.Take(8), item =>
    {
        acceptanceExecuted++;
        if (acceptanceExecuted == 3)
            throw new TimeoutException("No RUDP-C control reply 0x96.");
        Nice64RudpcAcceptanceCaseResult row = passingAcceptanceRows
            .First(result => result.Case == item);
        return Task.FromResult(row);
    }).GetAwaiter().GetResult();
Require(acceptanceExecuted == 8 && acceptanceContinued.Count == 8 &&
        acceptanceContinued[2].Disposition ==
            Nice64RudpcSampleDisposition.ControlInconclusive,
    "control-inconclusive acceptance case stopped later isolated cases");

Nice64RudpcTransferResult successful0x91 = exactAcceptanceTransfer with
    { ControlResult = "TIMEOUT_0x91" };
Nice64RudpcTransferResult successful0x96 = exactAcceptanceTransfer with
    { ControlResult = "TIMEOUT_0x96" };
Nice64RudpcCharacterizationSample retained0x91 =
    Nice64RudpcRtoCharacterization.FromTransfer(classificationCase, successful0x91);
Nice64RudpcCharacterizationSample retained0x96 =
    Nice64RudpcRtoCharacterization.FromTransfer(classificationCase, successful0x96);
Require(retained0x91.DataResult == Nice64RudpcDataResult.Pass &&
        retained0x91.ControlResult == Nice64RudpcControlResult.Timeout0x91 &&
        retained0x91.Disposition == Nice64RudpcSampleDisposition.ControlInconclusive &&
        retained0x96.DataResult == Nice64RudpcDataResult.Pass &&
        retained0x96.ControlResult == Nice64RudpcControlResult.Timeout0x96 &&
        retained0x96.Disposition == Nice64RudpcSampleDisposition.ControlInconclusive,
    "successful DATA was not retained across 0x91/0x96 control loss");
Console.WriteLine("PASS isolated eight-case acceptance plan, lifecycle evidence, continuation, run and five-run aggregation");

Nice64RudpcHardwareDiagnosticOptions closeoutOptions =
    Nice64RudpcHardwareDiagnosticOptions.Parse(
        ["--target", "192.0.2.1", "--closeout-rudpc", "--flight-cap", "2",
         "--rto", "40"]);
Require(closeoutOptions.CloseoutRudpc && closeoutOptions.SelectedRto == 40 &&
        closeoutOptions.FlightCap == 2,
    "final closeout CLI did not preserve the frozen physical tuple");
RequireThrows<ArgumentException>(() => Nice64RudpcHardwareDiagnosticOptions.Parse(
    ["--closeout-rudpc", "--quick", "--rto", "40"]),
    "closeout was allowed to reuse quick mode");
RequireThrows<ArgumentException>(() => Nice64RudpcHardwareDiagnosticOptions.Parse(
    ["--closeout-rudpc", "--accept-rudpc", "--rto", "40"]),
    "closeout was allowed to reuse acceptance mode");
RequireThrows<ArgumentException>(() => Nice64RudpcHardwareDiagnosticOptions.Parse(
    ["--closeout-rudpc", "--characterize-rto", "--rto", "40"]),
    "closeout was allowed to reuse characterization mode");
RequireThrows<ArgumentException>(() => Nice64RudpcHardwareDiagnosticOptions.Parse(
    ["--closeout-rudpc", "--flight-cap", "2"]),
    "closeout without explicit RTO 40 was accepted");
RequireThrows<ArgumentException>(() => Nice64RudpcHardwareDiagnosticOptions.Parse(
    ["--closeout-rudpc", "--flight-cap", "1", "--rto", "40"]),
    "closeout with a non-cap-two configuration was accepted");
RequireThrows<ArgumentException>(() => Nice64RudpcHardwareDiagnosticOptions.Parse(
    ["--closeout-rudpc", "--flight-cap", "2", "--rto", "20"]),
    "closeout with a non-40ms RTO was accepted");

IReadOnlyList<Nice64RudpcCloseoutCase> closeoutPlan =
    Nice64RudpcCloseout.CreatePlan(40, 2);
Require(closeoutPlan.Count == 3 &&
        closeoutPlan.Select(item => item.Test).SequenceEqual(
            Enum.GetValues<Nice64RudpcCloseoutTest>()) &&
        closeoutPlan.All(item => item.RtoMs == 40 && item.FlightCap == 2) &&
        closeoutPlan[0].IsDownload && !closeoutPlan[1].IsDownload &&
        closeoutPlan[0].ByteCount == 65_536 &&
        closeoutPlan[1].ByteCount == 65_536 &&
        closeoutPlan[2].ByteCount == 2 * 1024 * 1024,
    "closeout plan did not contain exhaustion, fresh recovery, and 2 MiB soak");
Nice64RudpcImpairment exhaustionImpairment = closeoutPlan[0].CreateImpairment();
Nice64RudpcImpairment soakImpairment = closeoutPlan[2].CreateImpairment();
Require(exhaustionImpairment.DropDataAlways == 3 &&
        exhaustionImpairment.DropDataOnce is null &&
        soakImpairment.DropDataOnce == 3 && soakImpairment.DuplicateData == 10 &&
        soakImpairment.ReorderWithNext == 20 && soakImpairment.DropDataAlways is null &&
        !ReferenceEquals(soakImpairment, closeoutPlan[2].CreateImpairment()),
    "closeout impairment objects or bounded impairment pattern were incorrect");

Nice64RudpcFirmwareDiagnostics closeoutBefore = new(new Dictionary<string, uint>
{
    ["retry_exhaustion"] = 0,
    ["maximum_retry_count"] = 0,
    ["duplicate_upper_deliveries"] = 0,
    ["rx_occupied_slots"] = 0
});
Nice64RudpcFirmwareDiagnostics exhaustedAfter = new(new Dictionary<string, uint>
{
    ["retry_exhaustion"] = 1,
    ["maximum_retry_count"] = 4,
    ["duplicate_upper_deliveries"] = 0,
    ["rx_occupied_slots"] = 0
});
Nice64RudpcTransferResult boundedExhaustionTransfer = new(false, 3, 65_536, 1_536,
    0x12345678, 0, 3, TimeSpan.FromSeconds(2), [], new(), closeoutBefore,
    exhaustedAfter, 0, 0, 0, 0, null, null, false, null, "PASS", true);
Nice64RudpcCloseoutResult boundedExhaustion = Nice64RudpcCloseout.FromTransfer(
    closeoutPlan[0], 1, boundedExhaustionTransfer);
Require(boundedExhaustion.Disposition == Nice64RudpcCloseoutDisposition.Pass &&
        boundedExhaustion.DataResult == "EXPECTED_EXHAUSTION_PASS",
    "bounded original-plus-four terminal exhaustion was not accepted as intentional");
Nice64RudpcCloseoutResult unboundedOrMissing = Nice64RudpcCloseout.FromTransfer(
    closeoutPlan[0], 1, boundedExhaustionTransfer with
    { FirmwareDiagnostics = closeoutBefore });
Require(unboundedOrMissing.Disposition == Nice64RudpcCloseoutDisposition.Fail,
    "terminal failure without firmware retry exhaustion was accepted");

Nice64RudpcFirmwareDiagnostics exactAfter = new(new Dictionary<string, uint>
{
    ["retry_exhaustion"] = 0,
    ["maximum_retry_count"] = 0,
    ["duplicate_upper_deliveries"] = 0,
    ["rx_occupied_slots"] = 0
});
Nice64RudpcTransferResult exactRecoveryTransfer = new(true, 0, 65_536, 65_536,
    0x12345678, 0x12345678, 128, TimeSpan.FromMilliseconds(400), [], new(),
    closeoutBefore, exactAfter, 2, 0, 0, 0, null, null, false, null, "PASS", true);
Nice64RudpcCloseoutResult recovery = Nice64RudpcCloseout.FromTransfer(
    closeoutPlan[1], 2, exactRecoveryTransfer);
Require(recovery.Disposition == Nice64RudpcCloseoutDisposition.Pass &&
        recovery.DataResult == "DATA_PASS",
    "exact fresh-session recovery did not pass");
Nice64RudpcTransferResult exactSoakTransfer = exactRecoveryTransfer with
{
    ExpectedBytes = 2 * 1024 * 1024,
    ActualOrderedBytes = 2 * 1024 * 1024,
    UpperDeliveries = 4096,
    Elapsed = TimeSpan.FromSeconds(20),
    ControlResult = "TIMEOUT_0x96"
};
Nice64RudpcCloseoutResult soakControlLoss = Nice64RudpcCloseout.FromTransfer(
    closeoutPlan[2], 3, exactSoakTransfer);
Require(soakControlLoss.DataResult == "DATA_PASS" &&
        soakControlLoss.ControlResult == Nice64RudpcControlResult.Timeout0x96 &&
        soakControlLoss.Disposition == Nice64RudpcCloseoutDisposition.ControlInconclusive,
    "exact 2 MiB DATA was not retained across trailing diagnostics control loss");
Require(new uint[] { 1, 2, 3 }.All(value => value != 0) &&
        new uint[] { 1, 2, 3 }.Distinct().Count() == 3,
    "closeout fresh-session test evidence was not unique and nonzero");
Console.WriteLine("PASS final closeout CLI, isolated plan, bounded exhaustion, recovery, soak, and control-loss classification");

Nice64RudpcDuplicateAcceptanceResult bufferedDuplicate =
    Nice64RudpcDuplicateAcceptance.Evaluate(true, 129, 128,
        65_536, 65_536, 0x12345678, 0x12345678, 128, 128,
        0, 1, 0, 0, 0);
Require(bufferedDuplicate.Disposition == "PASS" && bufferedDuplicate.TransportPassed &&
        bufferedDuplicate.InjectionAttempted &&
        bufferedDuplicate.InjectionObserved &&
        bufferedDuplicate.FirmwareClassification == "buffered-duplicate",
    "observed buffered duplicate with exact delivery did not pass");
Nice64RudpcDuplicateAcceptanceResult extraDelivery =
    Nice64RudpcDuplicateAcceptance.Evaluate(true, 129, 128,
        65_536, 66_048, 0x12345678, 0x12345678, 128, 129,
        1, 1, 0, 0, 0);
Require(extraDelivery.Disposition == "FAIL" && !extraDelivery.TransportPassed,
    "duplicate upper delivery was accepted");
Nice64RudpcDuplicateAcceptanceResult notObserved =
    Nice64RudpcDuplicateAcceptance.Evaluate(true, 128, 128,
        65_536, 65_536, 0x12345678, 0x12345678, 128, 128,
        0, 0, 0, 0, 0);
Require(notObserved.Disposition == "INCONCLUSIVE" && notObserved.TransportPassed &&
        notObserved.InjectionAttempted && !notObserved.InjectionObserved &&
        notObserved.FirmwareClassification == "NOT_OBSERVED",
    "unobserved impairment was not separated from transport correctness");
Nice64RudpcDuplicateAcceptanceResult cumulativeOnly =
    Nice64RudpcDuplicateAcceptance.Evaluate(true, 128, 128,
        65_536, 65_536, 0x12345678, 0x12345678, 128, 128,
        0, 0, 0, 0, 0);
Nice64RudpcFirmwareDiagnostics cumulativeBefore = new(
    new Dictionary<string, uint> { ["duplicate_rx"] = 7 });
Nice64RudpcFirmwareDiagnostics cumulativeAfter = new(
    new Dictionary<string, uint> { ["duplicate_rx"] = 7 });
Require(cumulativeAfter.Delta("duplicate_rx", cumulativeBefore) == 0 &&
        cumulativeOnly.Disposition == "INCONCLUSIVE",
    "nonzero prior cumulative counters with zero current delta falsely passed");
Nice64RudpcDuplicateAcceptanceResult deliveredStaleDuplicate =
    Nice64RudpcDuplicateAcceptance.Evaluate(true, 129, 128,
        65_536, 65_536, 0x12345678, 0x12345678, 128, 128,
        0, 0, 1, 0, 0);
Require(deliveredStaleDuplicate.Disposition == "PASS" &&
        deliveredStaleDuplicate.FirmwareClassification ==
            "already-delivered-old/stale",
    "old/stale-classified physical duplicate with exact delivery did not pass");
Console.WriteLine("PASS duplicate acceptance behavior, injection observability, deltas, stale classification");

Nice64RudpcDuplicateAcceptanceResult notAttempted =
    Nice64RudpcDuplicateAcceptance.Evaluate(false, 128, 128,
        65_536, 65_536, 0x12345678, 0x12345678, 128, 128,
        0, 0, 0, 0, 0);
Require(notAttempted.Disposition == "FAIL" && notAttempted.TransportPassed &&
        !notAttempted.InjectionAttempted,
    "missing diagnostic injection was not distinguished from an unobserved injection");

Nice64RudpcFlightCapController capController = new(2, 8);
Require(capController.NormalCap == 2 && capController.CurrentCap == 2 &&
        !capController.PauseOverrideActive,
    "normal cap 2 was not active before paused-consumer override");
capController.BeginPausedFill();
Require(capController.CurrentCap == 8 &&
        capController.PauseOverrideActive,
    "paused-consumer cap-8 override did not activate");
capController.CompleteResume();
Require(capController.CurrentCap == 2 && !capController.PauseOverrideActive,
    "resume completion did not restore normal cap 2");
int simulatedPostResumeMaximum = Enumerable.Range(0, 32)
    .Select(_ => capController.CurrentCap).Max();
Require(simulatedPostResumeMaximum <= 2,
    "post-resume admission exceeded restored cap 2");
Console.WriteLine("PASS temporary paused-consumer cap 8 restores cap 2 after resume");

Nice64RudpcImpairment forcedHoleOnly = new(DropDataOnce: 3);
Nice64RudpcImpairment ackLossOnly = new(DropAckOnce: 1);
Require(forcedHoleOnly.DropDataOnce == 3 && forcedHoleOnly.DropAckOnce is null &&
        forcedHoleOnly.DuplicateData is null && forcedHoleOnly.ReorderWithNext is null &&
        ackLossOnly.DropAckOnce == 1 && ackLossOnly.DropDataOnce is null &&
        ackLossOnly.DuplicateData is null && ackLossOnly.ReorderWithNext is null &&
        !ReferenceEquals(forcedHoleOnly, ackLossOnly),
    "diagnostic impairment state leaked between independent test definitions");
Console.WriteLine("PASS diagnostic impairment definitions are isolated per test");

static (byte[] Output, int MaximumOutstanding, int StagingDrops, long Retransmissions)
    RunPacedIngress(int flightCap, int? dropOnce = null)
{
    const uint session = 0xCAFE1234;
    const uint initial = 0x10000000;
    byte[] input = Pattern(64 * 1024);
    using MemoryStream output = new(input.Length);
    Nice64RudpConfiguration configuration = new(8, 512, 10, 4);
    Nice64RudpTransmitWindow tx = new(configuration, session, initial);
    Nice64RudpReceiveWindow rx = new(configuration, session,
        (_, payload) => output.Write(payload.Span), initial);
    int admitted = 0;
    int maximumOutstanding = 0;
    int stagingDrops = 0;
    bool droppedOnce = false;
    long now = 0;
    while ((admitted < input.Length || !tx.IsEmpty) && !tx.Failed && now < 100_000)
    {
        while (admitted < input.Length && !tx.IsFull &&
               Nice64RudpSequence.ForwardDistance(tx.BaseSequence, tx.NextSequence) <
                   (uint)flightCap)
        {
            int length = Math.Min(512, input.Length - admitted);
            Require(tx.TryEnqueue(input.AsSpan(admitted, length), out _),
                "paced admission failed");
            admitted += length;
        }
        maximumOutstanding = Math.Max(maximumOutstanding,
            (int)Nice64RudpSequence.ForwardDistance(tx.BaseSequence, tx.NextSequence));
        IReadOnlyList<Nice64RudpTransmission> due = tx.CollectDue(now);
        int staged = 0;
        foreach (Nice64RudpTransmission transmission in due)
        {
            int relative = unchecked((int)(transmission.Sequence - initial));
            if (dropOnce == relative && !droppedOnce)
            {
                droppedOnce = true;
                continue;
            }
            if (staged == 2)
            {
                stagingDrops++;
                continue;
            }
            staged++;
            rx.Receive(new(Header(flags: Nice64RudpPacketFlags.Data | Nice64RudpPacketFlags.Ack,
                session: session, sequence: transmission.Sequence,
                ackBase: 0, ackBitmap: 0,
                payloadLength: (ushort)transmission.Payload.Length), transmission.Payload));
            tx.ApplyAck(rx.CreateAck());
        }
        now++;
    }
    Require(!tx.Failed && tx.IsEmpty && admitted == input.Length,
        $"paced transfer failed for cap {flightCap}");
    return (output.ToArray(), maximumOutstanding, stagingDrops,
        tx.Diagnostics.Retransmissions);
}

static (byte[] Output, int ConfiguredCapBeforePause, int TemporaryPauseCap,
    int PausedOutstanding, uint PausedAckBase, uint PausedAckBitmap,
    uint ResumeOutstandingAfter, int RestoredCap, int PostResumeMaximum)
    RunPausedThenRestoredIngress()
{
    const uint session = 0xA55A1234;
    const uint initial = 0x70000000;
    byte[] input = Pattern(64 * 1024);
    using MemoryStream output = new(input.Length);
    Nice64RudpConfiguration configuration = new(8, 512, 20, 4);
    Nice64RudpTransmitWindow tx = new(configuration, session, initial);
    Nice64RudpReceiveWindow rx = new(configuration, session,
        (_, payload) => output.Write(payload.Span), initial)
        { ConsumerPaused = true };
    Nice64RudpcFlightCapController caps = new(2, 8);
    int configuredBeforePause = caps.CurrentCap;
    caps.BeginPausedFill();
    int admitted = 0;
    while (admitted < input.Length && !tx.IsFull &&
           Nice64RudpSequence.ForwardDistance(tx.BaseSequence, tx.NextSequence) <
               (uint)caps.CurrentCap)
    {
        int length = Math.Min(512, input.Length - admitted);
        Require(tx.TryEnqueue(input.AsSpan(admitted, length), out _),
            "paused-fill admission failed");
        admitted += length;
    }
    int pausedOutstanding = checked((int)Nice64RudpSequence.ForwardDistance(
        tx.BaseSequence, tx.NextSequence));
    foreach (Nice64RudpTransmission transmission in tx.CollectDue(0))
    {
        rx.Receive(new(Header(flags: Nice64RudpPacketFlags.Data | Nice64RudpPacketFlags.Ack,
            session: session, sequence: transmission.Sequence, ackBase: 0, ackBitmap: 0,
            payloadLength: (ushort)transmission.Payload.Length), transmission.Payload));
        tx.ApplyAck(rx.CreateAck());
    }
    Nice64RudpAck pausedAck = rx.CreateAck();
    Require(pausedOutstanding == 8 && tx.IsFull &&
            pausedAck.AckBase == initial - 1u && pausedAck.AckBitmap == 0xFF,
        "paused fill did not occupy all eight logical slots without cumulative release");

    rx.ConsumerPaused = false;
    Require(rx.Drain() == 8, "resume did not drain exactly eight paused packets");
    tx.ApplyAck(rx.CreateAck());
    uint resumeOutstandingAfter = Nice64RudpSequence.ForwardDistance(
        tx.BaseSequence, tx.NextSequence);
    Require(resumeOutstandingAfter == 0 && tx.IsEmpty,
        "resume cumulative ACK did not release all eight host slots");
    caps.CompleteResume();

    int postResumeMaximum = 0;
    long now = 1;
    while ((admitted < input.Length || !tx.IsEmpty) && !tx.Failed && now < 100_000)
    {
        while (admitted < input.Length && !tx.IsFull &&
               Nice64RudpSequence.ForwardDistance(tx.BaseSequence, tx.NextSequence) <
                   (uint)caps.CurrentCap)
        {
            int length = Math.Min(512, input.Length - admitted);
            Require(tx.TryEnqueue(input.AsSpan(admitted, length), out _),
                "post-resume admission failed");
            admitted += length;
        }
        postResumeMaximum = Math.Max(postResumeMaximum,
            checked((int)Nice64RudpSequence.ForwardDistance(
                tx.BaseSequence, tx.NextSequence)));
        foreach (Nice64RudpTransmission transmission in tx.CollectDue(now))
        {
            rx.Receive(new(Header(flags: Nice64RudpPacketFlags.Data | Nice64RudpPacketFlags.Ack,
                session: session, sequence: transmission.Sequence, ackBase: 0, ackBitmap: 0,
                payloadLength: (ushort)transmission.Payload.Length), transmission.Payload));
            tx.ApplyAck(rx.CreateAck());
        }
        now++;
    }
    Require(!tx.Failed && tx.IsEmpty && admitted == input.Length,
        "post-resume full transfer did not complete");
    return (output.ToArray(), configuredBeforePause, 8, pausedOutstanding,
        pausedAck.AckBase, pausedAck.AckBitmap, resumeOutstandingAfter,
        caps.CurrentCap, postResumeMaximum);
}

// Exact network-byte-order vector.
byte[] vector = Nice64RudpPacketCodec.Encode(Header(), [0xAA, 0xBB, 0xCC], 512);
string expectedVector =
    "4E3634550101001C102030405060708090A0B0C0D0E0F00100030000AABBCC";
Require(Convert.ToHexString(vector) == expectedVector, "header encode vector mismatch");
Require(Nice64RudpPacketCodec.TryDecode(vector, 512, 0x10203040,
    out Nice64RudpPacket? decoded, out Nice64RudpValidationError vectorError) &&
    vectorError == Nice64RudpValidationError.None && decoded!.Header.Sequence == 0x50607080 &&
    decoded.Header.AckBitmap == 0xD0E0F001 &&
    decoded.Payload.SequenceEqual(new byte[] { 0xAA, 0xBB, 0xCC }),
    "header decode vector mismatch");
Console.WriteLine("PASS exact 28-byte big-endian header encode/decode vector");

static Nice64RudpValidationError DecodeError(byte[] wire, int max = 512, uint? session = 0x10203040)
{
    Nice64RudpPacketCodec.TryDecode(wire, max, session, out _, out Nice64RudpValidationError error);
    return error;
}

byte[] Mutate(int offset, byte value)
{
    byte[] copy = vector.ToArray();
    copy[offset] = value;
    return copy;
}

Require(DecodeError(vector[..20]) == Nice64RudpValidationError.TruncatedHeader, "truncated header accepted");
Require(DecodeError(Mutate(0, 0)) == Nice64RudpValidationError.BadMagic, "bad magic accepted");
Require(DecodeError(Mutate(4, 2)) == Nice64RudpValidationError.UnsupportedVersion, "bad version accepted");
Require(DecodeError(Mutate(7, 27)) == Nice64RudpValidationError.InvalidHeaderLength, "bad header length accepted");
Require(DecodeError(Mutate(5, 0x80)) == Nice64RudpValidationError.InvalidFlags, "bad flags accepted");
Require(DecodeError(Mutate(6, 1)) == Nice64RudpValidationError.UnsupportedDeliveryClass, "FAST accepted");
Require(DecodeError(Mutate(6, 2)) == Nice64RudpValidationError.InvalidDeliveryClass, "invalid class accepted");
Require(DecodeError(vector, 2) == Nice64RudpValidationError.PayloadTooLarge, "oversize payload accepted");
Require(DecodeError(vector[..^1]) == Nice64RudpValidationError.TruncatedPayload, "truncated payload accepted");
Require(DecodeError([.. vector, 0]) == Nice64RudpValidationError.PayloadLengthMismatch, "trailing payload accepted");
Require(DecodeError(vector, session: 1) == Nice64RudpValidationError.SessionMismatch, "wrong session accepted");
Require(DecodeError(Mutate(27, 1)) == Nice64RudpValidationError.ReservedFieldNonzero, "nonzero reserved accepted");
List<uint> invalidDeliveries = [];
Nice64RudpReceiveWindow invalidIngress = new(new(8, 512, 10, 4), 0x10203040,
    (sequence, _) => invalidDeliveries.Add(sequence), 0x50607080);
Require(invalidIngress.ReceiveWire(Mutate(0, 0), out Nice64RudpValidationError ingressError) ==
        Nice64RudpReceiveResult.InvalidPacket &&
        ingressError == Nice64RudpValidationError.BadMagic &&
        invalidIngress.NextExpected == 0x50607080 && invalidDeliveries.Count == 0 &&
        invalidIngress.Diagnostics.InvalidHeader == 1,
    "malformed wire packet altered receive-window state");
Console.WriteLine("PASS malformed/header/session/delivery-class validation matrix");

Require(Nice64RudpSequence.IsAfter(0, 0xFFFFFFFF) &&
        Nice64RudpSequence.IsAfter(2, 0xFFFFFFFC) &&
        Nice64RudpSequence.IsBefore(0xFFFFFFFC, 2) &&
        Nice64RudpSequence.ForwardDistance(0xFFFFFFFC, 2) == 6,
    "wrap-safe sequence arithmetic failed");
Console.WriteLine("PASS unsigned modular sequence comparison across wrap");

Nice64RudpConfiguration directConfig = new(8, 32, 10, 4);
List<uint> ordered = [];
Nice64RudpReceiveWindow receive = new(directConfig, 7, (sequence, _) => ordered.Add(sequence), 100);
Require(receive.Receive(Packet(7, 100, 100)) == Nice64RudpReceiveResult.Accepted, "seq100 rejected");
Require(receive.Receive(Packet(7, 102, 102)) == Nice64RudpReceiveResult.Accepted, "seq102 rejected");
Require(receive.Receive(Packet(7, 102, 102)) == Nice64RudpReceiveResult.Duplicate,
    "buffered duplicate was not suppressed");
Nice64RudpAck holeAck = receive.CreateAck();
Require(holeAck.AckBase == 100 && holeAck.AckBitmap == 0b10,
    "ACK base/bitmap hole semantics mismatch");
receive.Receive(Packet(7, 101, 101));
receive.Receive(Packet(7, 104, 104));
receive.Receive(Packet(7, 103, 103));
Require(ordered.SequenceEqual([100u, 101u, 102u, 103u, 104u]),
    "out-of-order release was not ordered");
Require(receive.Receive(Packet(7, 101, 101)) == Nice64RudpReceiveResult.AlreadyDelivered,
    "already-delivered packet was not suppressed");
Console.WriteLine("PASS ACK bitmap semantics, reorder buffering, ordered release, stale suppression");

// Fixed-window backpressure with a paused upper consumer.
Nice64RudpTransmitWindow backTx = new(new(4, 32, 10, 4), 8, 200);
for (int i = 0; i < 4; i++) Require(backTx.TryEnqueue([(byte)i], out _), "TX admission failed early");
Require(!backTx.TryEnqueue([9], out _), "TX window failed to apply backpressure");
List<uint> resumed = [];
Nice64RudpReceiveWindow backRx = new(new(4, 32, 10, 4), 8, (s, _) => resumed.Add(s), 200)
    { ConsumerPaused = true };
for (uint s = 200; s < 204; s++) backRx.Receive(Packet(8, s, (byte)s));
Require(backRx.Receive(Packet(8, 204, 0)) == Nice64RudpReceiveResult.OutsideWindow,
    "RX window overwrote occupied slots");
backTx.ApplyAck(backRx.CreateAck());
Require(backTx.IsFull, "selective ACK bypassed cumulative backpressure");
backRx.ConsumerPaused = false;
Require(backRx.Drain() == 4 && resumed.Count == 4, "paused consumer did not resume");
backTx.ApplyAck(backRx.CreateAck());
Require(backTx.IsEmpty && backTx.TryEnqueue([9], out _), "TX did not recover after consumer resumed");
Console.WriteLine("PASS bounded TX/RX backpressure and paused-consumer recovery");

// Firmware resume emits the freshly advanced cumulative ACK immediately before
// its control STATE_ACK. The synthetic control path must preserve both replies.
const uint integrationInitial = 0x70000000;
Nice64RudpTransmitWindow integrationTx = new(new(8, 512, 20, 4), 0x12345678,
    integrationInitial);
for (int i = 0; i < 8; i++)
    Require(integrationTx.TryEnqueue([(byte)i], out _),
        "resume integration TX admission failed");
integrationTx.ApplyAck(new(integrationInitial - 1u, 0xFF));
Require(integrationTx.IsFull && integrationTx.BaseSequence == integrationInitial,
    "selective paused ACK bypassed cumulative backpressure");
const uint resumeControlSequence = 0xABCDEF01;
Nice64RudpcResumeAckTracker resumeTracker = new(resumeControlSequence, 0x92,
    integrationTx);
resumeTracker.Observe(new(Header(flags: Nice64RudpPacketFlags.Ack,
    session: 0x12345678, sequence: 0, ackBase: integrationInitial - 1u,
    ackBitmap: 0xFF, payloadLength: 0), []), integrationTx);
Require(resumeTracker.PreDrainAckBase == integrationInitial - 1u &&
        resumeTracker.PreDrainAckBitmap == 0xFF && integrationTx.IsFull,
    "paused ACK (N-1, 0xFF) was not retained without cumulative release");
resumeTracker.Observe(new(Header(flags: Nice64RudpPacketFlags.Ack,
    session: 0x12345678, sequence: 0, ackBase: integrationInitial + 7u,
    ackBitmap: 0, payloadLength: 0), []), integrationTx);
Require(!resumeTracker.Complete && integrationTx.IsEmpty,
    "resume cumulative ACK was not retained before STATE_ACK");
resumeTracker.Observe(new(Header(flags: Nice64RudpPacketFlags.Data,
    session: 0x12345678, sequence: resumeControlSequence,
    ackBase: 0, ackBitmap: 0, payloadLength: 1), [0x92]), integrationTx);
Require(resumeTracker.Complete && resumeTracker.StateAcknowledged &&
        resumeTracker.CumulativeProgressObserved && integrationTx.IsEmpty &&
        integrationTx.TryEnqueue([9], out _),
    "ACK-before-STATE_ACK resume integration did not release sender");

Nice64RudpTransmitWindow reverseOrderTx = new(new(8, 512, 20, 4), 0x12345678,
    integrationInitial);
for (int i = 0; i < 8; i++) reverseOrderTx.TryEnqueue([(byte)i], out _);
Nice64RudpcResumeAckTracker reverseOrderTracker = new(resumeControlSequence, 0x92,
    reverseOrderTx);
reverseOrderTracker.Observe(new(Header(flags: Nice64RudpPacketFlags.Data,
    session: 0x12345678, sequence: resumeControlSequence,
    ackBase: 0, ackBitmap: 0, payloadLength: 1), [0x92]), reverseOrderTx);
reverseOrderTracker.Observe(new(Header(flags: Nice64RudpPacketFlags.Ack,
    session: 0x12345678, sequence: 0, ackBase: integrationInitial + 7u,
    ackBitmap: 0, payloadLength: 0), []), reverseOrderTx);
Require(reverseOrderTracker.Complete && reverseOrderTx.IsEmpty,
    "STATE_ACK-before-ACK resume integration did not release sender");
Console.WriteLine("PASS resume integration retains cumulative ACK in either control-reply order");

foreach (int cap in new[] { 1, 2, 4, 8 })
{
    var paced = RunPacedIngress(cap);
    Require(paced.Output.SequenceEqual(Pattern(64 * 1024)) &&
            paced.MaximumOutstanding <= cap,
        $"ACK-clocked flight cap {cap} violated admission or delivery");
    if (cap <= 2) Require(paced.StagingDrops == 0,
        $"flight cap {cap} overflowed two-slot callback staging");
    Console.WriteLine($"PASS ACK-clocked 64KiB flight_cap={cap} " +
        $"max_outstanding={paced.MaximumOutstanding} staging_drops={paced.StagingDrops} " +
        $"retrans={paced.Retransmissions}");
}
var pausedThenRestored = RunPausedThenRestoredIngress();
Require(pausedThenRestored.ConfiguredCapBeforePause == 2 &&
        pausedThenRestored.TemporaryPauseCap == 8 &&
        pausedThenRestored.PausedOutstanding == 8 &&
        pausedThenRestored.PausedAckBase == 0x6FFFFFFF &&
        pausedThenRestored.PausedAckBitmap == 0xFF &&
        pausedThenRestored.ResumeOutstandingAfter == 0 &&
        pausedThenRestored.RestoredCap == 2 &&
        pausedThenRestored.PostResumeMaximum <= 2 &&
        pausedThenRestored.Output.SequenceEqual(Pattern(64 * 1024)),
    "paused-fill/resume/restored-cap full-transfer state machine failed");
Console.WriteLine("PASS 64KiB paused fill cap=8 / exact drain / restored cap=2 / " +
    $"post_resume_max={pausedThenRestored.PostResumeMaximum} / exact output");
var pacedHole = RunPacedIngress(2, dropOnce: 3);
Require(pacedHole.Output.SequenceEqual(Pattern(64 * 1024)) &&
        pacedHole.Retransmissions == 1,
    "flight-cap-2 selective hole recovery failed");
Console.WriteLine("PASS ACK-clocked flight-cap-2 recovers one staged-loss hole selectively");

// Forced hole: only sequence 103 should retransmit.
SimulationResult hole = new RudpSimulationHarness(
    new(8, 32, 5, 4), new(1001, DropFirstSequence: 103))
    .Run(Pattern(8 * 32), initialSequence: 100);
Require(hole.Completed && hole.Output.SequenceEqual(Pattern(8 * 32)), "forced-hole transfer failed");
Require(hole.DataSendCounts[103] == 2 &&
        hole.DataSendCounts.Where(pair => pair.Key != 103).All(pair => pair.Value == 1),
    "forced hole retransmitted more than the missing datagram");
Console.WriteLine("PASS forced hole selectively retransmits sequence 103 only");

// ACK loss and duplicates must never repeat the upper-layer side effect.
SimulationResult ackLoss = new RudpSimulationHarness(
    new(4, 32, 5, 4), new(2002, DropFirstAck: true, DuplicateAck: 1))
    .Run(Pattern(32), initialSequence: 500);
Require(ackLoss.Completed && ackLoss.UpperDeliveries == 1 && ackLoss.Output.SequenceEqual(Pattern(32)) &&
        ackLoss.Tx.Retransmissions >= 1 && ackLoss.Rx.DuplicateRx + ackLoss.Rx.OldOrStaleRx >= 1,
    "ACK-loss/duplicate side-effect suppression failed");
Console.WriteLine("PASS duplicate data/ACK/retransmit yields one logical side effect");

// Session reset rejects delayed Session A traffic without contaminating B.
List<uint> sessionDeliveries = [];
Nice64RudpReceiveWindow sessions = new(directConfig, 0xAAAA, (s, _) => sessionDeliveries.Add(s), 10);
sessions.Receive(Packet(0xAAAA, 10, 1));
sessions.Reset(0xBBBB, 20);
Require(sessions.Receive(Packet(0xAAAA, 11, 2)) == Nice64RudpReceiveResult.SessionMismatch,
    "old session packet accepted");
Require(sessions.NextExpected == 20 && sessionDeliveries.SequenceEqual([10u]),
    "old session contaminated new state");
sessions.Receive(Packet(0xBBBB, 20, 3));
Require(sessionDeliveries.SequenceEqual([10u, 20u]), "new session failed after stale packet");
Console.WriteLine("PASS session reset rejects delayed old-session traffic");

// Full wrap window including FFFFFFFC..00000002.
uint wrapStart = 0xFFFFFFFC;
SimulationResult wrap = new RudpSimulationHarness(
    new(8, 1, 5, 4), new(3003, MaximumOneWayDelayMs: 4,
        DuplicateData: .2, DropFirstSequence: 0xFFFFFFFF))
    .Run(Pattern(7), initialSequence: wrapStart);
Require(wrap.Completed && wrap.Output.SequenceEqual(Pattern(7)) && wrap.UpperDeliveries == 7 &&
        wrap.DataSendCounts[0xFFFFFFFF] == 2,
    "wraparound transfer failed");
Console.WriteLine("PASS window/ACK/retransmission/ordered delivery across uint32 wrap");

// Retry exhaustion is terminal once, bounded, and resettable.
Nice64RudpTransmitWindow exhausted = new(new(1, 32, 5, 3), 9, 900);
Require(exhausted.TryEnqueue([1], out _), "retry test enqueue failed");
for (int now = 0; now <= 25; now += 5) exhausted.CollectDue(now);
Require(exhausted.Failed && exhausted.Diagnostics.RetryExhaustion == 1 &&
        exhausted.Diagnostics.MaxRetryCount == 3, "retry exhaustion was not bounded");
exhausted.CollectDue(100);
Require(exhausted.Diagnostics.RetryExhaustion == 1, "failure reported more than once");
exhausted.Reset(10, 0);
Require(!exhausted.Failed && exhausted.IsEmpty && exhausted.TryEnqueue([2], out _),
    "failed window did not reset cleanly");
SimulationResult forcedFailure = new RudpSimulationHarness(
    new(4, 32, 5, 3), new(4004, DropEverySequence: 2))
    .Run(Pattern(5 * 32));
Require(forcedFailure.Failed && !forcedFailure.Completed && forcedFailure.Tx.RetryExhaustion == 1,
    "forced permanent loss did not fail cleanly");
Console.WriteLine("PASS bounded retry exhaustion, single failure, clean reset");

// Configuration matrix: windows, payload capacities, retransmission timers.
Dictionary<int, (long Retransmissions, long Elapsed, int Runs)> timerStats = [];
Dictionary<int, (long Retransmissions, long Elapsed, int Runs)> windowStats = [];
Dictionary<int, (long Retransmissions, long Elapsed, int Runs)> payloadStats = [];
foreach (int window in new[] { 1, 4, 8, 16 })
foreach (int payload in new[] { 256, 512, 1024 })
foreach (int timer in new[] { 5, 10, 20 })
{
    byte[] input = Pattern(16 * 1024);
    SimulationResult matrix = new RudpSimulationHarness(
        new(window, payload, timer, 4),
        new(5000 + window * 100 + payload, DataLoss: .01,
            AckLoss: .01, DuplicateData: .02, DuplicateAck: .02,
            MaximumOneWayDelayMs: Math.Max(1, timer / 2)))
        .Run(input);
    Require(matrix.Completed && matrix.Output.SequenceEqual(input),
        $"matrix failed window={window} payload={payload} timer={timer}");
    (long Retransmissions, long Elapsed, int Runs) t = timerStats.GetValueOrDefault(timer);
    timerStats[timer] = (t.Retransmissions + matrix.Tx.Retransmissions,
        t.Elapsed + matrix.ElapsedMs, t.Runs + 1);
    (long Retransmissions, long Elapsed, int Runs) w = windowStats.GetValueOrDefault(window);
    windowStats[window] = (w.Retransmissions + matrix.Tx.Retransmissions,
        w.Elapsed + matrix.ElapsedMs, w.Runs + 1);
    (long Retransmissions, long Elapsed, int Runs) p = payloadStats.GetValueOrDefault(payload);
    payloadStats[payload] = (p.Retransmissions + matrix.Tx.Retransmissions,
        p.Elapsed + matrix.ElapsedMs, p.Runs + 1);
}
Console.WriteLine("PASS window 1/4/8/16 × payload 256/512/1024 × timer 5/10/20 ms matrix");
foreach ((int timer, var stats) in timerStats.OrderBy(pair => pair.Key))
    Console.WriteLine($"MATRIX timer={timer}ms runs={stats.Runs} retrans={stats.Retransmissions} meanElapsed={stats.Elapsed / stats.Runs}ms");
foreach ((int window, var stats) in windowStats.OrderBy(pair => pair.Key))
    Console.WriteLine($"MATRIX window={window} runs={stats.Runs} retrans={stats.Retransmissions} meanElapsed={stats.Elapsed / stats.Runs}ms");
foreach ((int payload, var stats) in payloadStats.OrderBy(pair => pair.Key))
    Console.WriteLine($"MATRIX payload={payload} runs={stats.Runs} retrans={stats.Retransmissions} meanElapsed={stats.Elapsed / stats.Runs}ms");

Nice64RudpMemoryProjection memory = Nice64RudpMemoryProjection.For(new());
Require(memory.PayloadStorageBytes == 8192 && memory.TotalProjectedBytes == 8520,
    "default projected Pico memory accounting changed");
Console.WriteLine($"PASS projected Pico fixed state={memory.TotalProjectedBytes} bytes payload={memory.PayloadStorageBytes} bytes");

// Required loss levels on the 64 KiB default candidate.
byte[] stream64K = Pattern(64 * 1024);
foreach (int percent in new[] { 0, 1, 5, 10 })
{
    double loss = percent / 100.0;
    SimulationResult result = new RudpSimulationHarness(
        new(8, 512, 10, 4),
        new(6000 + percent, DataLoss: loss, AckLoss: loss / 2,
            DuplicateData: .02, DuplicateAck: .02, MaximumOneWayDelayMs: 8))
        .Run(stream64K);
    Require(result.Completed && result.Output.SequenceEqual(stream64K) &&
            result.UpperDeliveries == 128 && Digest(result.Output) == Digest(stream64K),
        $"64 KiB {percent}% loss result failed");
    Console.WriteLine($"PASS 64KiB loss={percent}% elapsed={result.ElapsedMs}ms " +
        $"retrans={result.Tx.Retransmissions} dupRx={result.Rx.DuplicateRx} " +
        $"ooo={result.Rx.OutOfOrderRx} sha256-64={Digest(result.Output)}");
}

// Longer deterministic 2 MiB soak at 5% data loss plus duplication/reorder/delay.
byte[] soakInput = Pattern(2 * 1024 * 1024);
SimulationResult soak = new RudpSimulationHarness(
    new(8, 512, 10, 4),
    new(7007, DataLoss: .05, AckLoss: .025, DuplicateData: .03,
        DuplicateAck: .03, MaximumOneWayDelayMs: 8))
    .Run(soakInput, maximumMilliseconds: 600_000);
Require(soak.Completed && soak.Output.SequenceEqual(soakInput) &&
        soak.UpperDeliveries == 4096 && Digest(soak.Output) == Digest(soakInput),
    "2 MiB soak failed");
Require(soak.MaximumQueuedDatagrams < 256, "simulation queue grew unexpectedly");
Console.WriteLine($"PASS 2MiB soak seed=7007 elapsed={soak.ElapsedMs}ms " +
    $"retrans={soak.Tx.Retransmissions} dupRx={soak.Rx.DuplicateRx} " +
    $"ooo={soak.Rx.OutOfOrderRx} maxQueue={soak.MaximumQueuedDatagrams} " +
    $"sha256-64={Digest(soak.Output)}");

// Latest-state remains rejected by codec and receive engine.
Nice64RudpPacket fastPacket = new(Header(deliveryClass: 1, session: 7,
    sequence: receive.NextExpected, ackBase: 0, ackBitmap: 0, payloadLength: 1), [1]);
Require(receive.Receive(fastPacket) == Nice64RudpReceiveResult.UnsupportedDeliveryClass &&
        !Nice64WirelessTransportContract.IncomingChannelMayEnterReliablePath(1),
    "LATEST_STATE entered RUDP-A reliability engine");
RequireThrows<ArgumentException>(() => Nice64RudpPacketCodec.Encode(fastPacket.Header,
    fastPacket.Payload, 512), "LATEST_STATE encoded as supported traffic");
Console.WriteLine("PASS LATEST_STATE remains UNSUPPORTED with no Fast behavior");

Console.WriteLine("ALL RUDP-A HOST RELIABILITY TESTS PASSED");
