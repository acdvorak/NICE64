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

static byte[] Pattern(int size) => Enumerable.Range(0, size).Select(i => (byte)(i * 37)).ToArray();

static List<string> RunGameBoy(string name, Action<FakeLineTransport>? configure = null)
{
    using FakeLineTransport transport = new(name);
    configure?.Invoke(transport);
    BridgeBulkTransfer.InstallFullGameBoyRom(
        transport, Pattern(300), new(0x1B, 0x0000012C, 0x00000020, 0x03),
        Pattern(32), null, null, CancellationToken.None);
    return transport.Commands;
}

List<string> usbGb = RunGameBoy("SerialBridgeTransport fake");
List<string> tcpGb = RunGameBoy("TcpBridgeTransport fake");
Require(usbGb.SequenceEqual(tcpGb), "USB/TCP GB command streams differ");
Require(usbGb[0] == "GBCLR", "GBCLR ordering changed");
Require(usbGb[1] == "GBP 1B 0000012C 00000020 03", "GBP command mismatch");
Require(usbGb[2] == "GBFBEGIN 0000012C", "GBFBEGIN command mismatch");
Require(usbGb[3].StartsWith("GBFP 000000 ") && usbGb[4].StartsWith("GBFP 000100 "),
    "GBFP page ordering changed");
Require(usbGb[4].Length == "GBFP 000100 ".Length + 512 && usbGb[4].EndsWith(new string('F', 424)),
    "final GB page was not padded to 256 bytes");
Require(usbGb[5] == "GBFCOMMIT" && usbGb[^1] == "PAKMODE TRANSFER",
    "GB commit/final mode ordering changed");
Require(usbGb[6] == "GBFCRC?", "GB flash CRC verification ordering changed");
Console.WriteLine("PASS equivalent USB/TCP full GB transfer, profile, pages, padding, commit");

Require(BridgeBulkTransfer.ComputeCrc32(System.Text.Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926u,
    "CRC-32 implementation does not match the standard check vector");
using (FakeLineTransport corruptCrc = new("CRC mismatch fake") { CorruptFlashCrc = true })
{
    RequireThrows<InvalidOperationException>(() => BridgeBulkTransfer.InstallFullGameBoyRom(
        corruptCrc, Pattern(300), new(0x1B, 300, 32, 0x03), Pattern(32), null, null,
        CancellationToken.None), "GB flash CRC mismatch was accepted");
    Require(!corruptCrc.Commands.Any(c => c.StartsWith("GBS ")) &&
            !corruptCrc.Commands.Any(c => c == "PAKMODE TRANSFER"),
        "save upload or Transfer Pak activation continued after CRC mismatch");
}
Console.WriteLine("PASS committed ROM CRC verification and mismatch containment");

foreach (string failedCommand in new[] { "GBFBEGIN", "GBFP", "GBFCOMMIT" })
{
    using FakeLineTransport rejected = new($"{failedCommand} rejection fake")
    {
        FailCommandPrefix = failedCommand
    };
    RequireThrows<InvalidOperationException>(() => BridgeBulkTransfer.InstallFullGameBoyRom(
        rejected, Pattern(300), new(0x1B, 300, 32, 0x03), Pattern(32), null, null,
        CancellationToken.None), $"{failedCommand} firmware rejection was accepted");
    Require(!rejected.Commands.Any(c => c.StartsWith("GBS ")) &&
            !rejected.Commands.Any(c => c == "PAKMODE TRANSFER"),
        $"install continued to save/activation after {failedCommand} rejection");
}
Console.WriteLine("PASS GBFBEGIN/GBFP/GBFCOMMIT ERR propagation");

using (FakeLineTransport retry = new("TCP retry fake"))
{
    retry.TimeoutOnceFor = "GBFP 000000";
    BridgeBulkTransfer.InstallFullGameBoyRom(retry, Pattern(256), new(0x01, 256, 32, 0),
        Pattern(32), null, null, CancellationToken.None);
    Require(retry.Commands.Count(c => c.StartsWith("GBFP 000000 ")) == 2,
        "timed-out GB page was not retried exactly once");
    Require(retry.GameBoyPageApplyCount == 1,
        "processed page was physically applied more than once after ACK loss");
    Require(retry.GameBoyPageReplayCount == 1,
        "second identical page was not recognized as an idempotent replay");
    Require(retry.Commands.Contains("GBFCOMMIT") && retry.Commands.Contains("GBFCRC?") &&
            retry.Commands.Any(c => c.StartsWith("GBS ")) &&
            retry.Commands.Contains("PAKMODE TRANSFER"),
        "upload did not complete after processed-page ACK loss and exact retry");
}
Console.WriteLine("PASS processed GB page + lost ACK + exact retry is idempotent");

using (FakeLineTransport delayedAck = new("delayed ACK correlation fake")
{
    TimeoutOnceFor = "GBFP 000000",
    RetainTimedOutReply = true,
    InjectUnrelatedBeforeSecondPageAck = true
})
{
    BridgeBulkTransfer.InstallFullGameBoyRom(delayedAck, Pattern(300),
        new(0x01, 300, 32, 0), Pattern(32), null, null, CancellationToken.None);
    Require(delayedAck.Commands.Count(c => c.StartsWith("GBFP 000000 ")) == 2,
        "delayed-ACK case did not retry processed page 000000 exactly once");
    Require(delayedAck.Commands.Count(c => c.StartsWith("GBFP 000100 ")) == 1,
        "page 000100 command count changed during ACK correlation test");
    Require(delayedAck.SecondPageResponses.SequenceEqual(new[]
        {
            "OK GBFP 000000",
            "OK PAK 0000",
            "STAT harmless",
            "OK GBFP 000100"
        }), "second-page response ordering did not exercise stale/unrelated correlation");
    Require(delayedAck.SecondPageExactAckObserved,
        "matching page 000100 ACK was not observed");
    Require(!delayedAck.CommitAttemptedBeforeSecondPageExactAck,
        "GBFCOMMIT occurred before the exact page 000100 ACK was observed");
    Require(delayedAck.Commands.Contains("GBFCOMMIT") &&
            delayedAck.Commands.Contains("GBFCRC?") &&
            delayedAck.Commands.Any(c => c.StartsWith("GBS ")) &&
            delayedAck.Commands.Contains("PAKMODE TRANSFER"),
        "verified upload did not finish after delayed/stale ACK correlation");
}
Console.WriteLine("PASS stale delayed and unrelated responses cannot acknowledge the next GBFP page");

using (FakeLineTransport errorAfterStale = new("ERR after stale ACK fake")
{
    TimeoutOnceFor = "GBFP 000000",
    RetainTimedOutReply = true,
    FailSecondPage = true
})
{
    RequireThrows<InvalidOperationException>(() => BridgeBulkTransfer.InstallFullGameBoyRom(
        errorAfterStale, Pattern(300), new(0x01, 300, 32, 0), Pattern(32),
        null, null, CancellationToken.None), "ERR after stale GBFP ACK was ignored");
    Require(errorAfterStale.SecondPageResponses.SequenceEqual(new[]
        {
            "OK GBFP 000000",
            "ERR GBFP FLASH_EXECUTION_FAILED"
        }), "ERR test did not deliver stale ACK followed by firmware failure");
    Require(!errorAfterStale.Commands.Contains("GBFCOMMIT") &&
            !errorAfterStale.Commands.Contains("GBFCRC?") &&
            !errorAfterStale.Commands.Any(c => c.StartsWith("GBS ")) &&
            !errorAfterStale.Commands.Contains("PAKMODE TRANSFER"),
        "upload continued after ERR following stale ACK");
}
Console.WriteLine("PASS stale ACK is ignored and following ERR aborts immediately");

using (FakeLineTransport mismatchedOnly = new("mismatched ACK timeout fake")
{
    OnlyMismatchedGbfpAcks = true
})
{
    RequireThrows<TimeoutException>(() => BridgeBulkTransfer.InstallFullGameBoyRom(
        mismatchedOnly, Pattern(256), new(0x01, 256, 32, 0), Pattern(32),
        null, null, CancellationToken.None),
        "mismatched GBFP acknowledgements counted as success");
    Require(mismatchedOnly.Commands.Count(c => c.StartsWith("GBFP 000000 ")) == 2,
        "mismatched-ACK timeout did not preserve the two-attempt retry policy");
    Require(!mismatchedOnly.Commands.Contains("GBFCOMMIT") &&
            !mismatchedOnly.Commands.Contains("GBFCRC?") &&
            !mismatchedOnly.Commands.Any(c => c.StartsWith("GBS ")) &&
            !mismatchedOnly.Commands.Contains("PAKMODE TRANSFER"),
        "upload continued without an exact GBFP acknowledgement");
}
Console.WriteLine("PASS mismatched-only GBFP acknowledgements time out after two attempts");

using (FakeLineTransport alteredRetry = new("altered replay fake")
{
    TimeoutOnceFor = "GBFP 000000",
    AlterDuplicateReplay = true
})
{
    RequireThrows<InvalidOperationException>(() => BridgeBulkTransfer.InstallFullGameBoyRom(
        alteredRetry, Pattern(256), new(0x01, 256, 32, 0), Pattern(32), null, null,
        CancellationToken.None), "altered processed-page replay was accepted");
    Require(alteredRetry.Commands.Count(c => c.StartsWith("GBFP 000000 ")) == 2,
        "altered replay test did not exercise the retry");
    Require(alteredRetry.GameBoyPageApplyCount == 1,
        "altered replay caused another page application");
    Require(!alteredRetry.Commands.Contains("GBFCOMMIT") &&
            !alteredRetry.Commands.Any(c => c.StartsWith("GBS ")) &&
            !alteredRetry.Commands.Contains("PAKMODE TRANSFER"),
        "upload continued after altered replay rejection");
}
Console.WriteLine("PASS altered processed-page replay is rejected without rewrite");

using (FakeLineTransport timeout = new("timeout fake") { AlwaysTimeout = true })
{
    RequireThrows<TimeoutException>(() => BridgeBulkTransfer.SendAndExpect(timeout, "GBCLR",
        line => line == "OK GBCLR", TimeSpan.FromMilliseconds(2), CancellationToken.None),
        "GB timeout was accepted");
}
using (FakeLineTransport malformed = new("malformed fake") { ForcedReply = "ERR MALFORMED" })
{
    RequireThrows<InvalidOperationException>(() => BridgeBulkTransfer.SendAndExpect(malformed,
        "GBFCOMMIT", line => line == "OK GBFCOMMIT", TimeSpan.FromSeconds(1), CancellationToken.None),
        "GB error reply was accepted");
}
using (FakeLineTransport malformed = new("malformed fake") { ForcedReply = "NOT_A_GB_REPLY" })
{
    RequireThrows<TimeoutException>(() => BridgeBulkTransfer.SendAndExpect(malformed,
        "GBFCOMMIT", line => line == "OK GBFCOMMIT", TimeSpan.FromMilliseconds(2), CancellationToken.None),
        "malformed GB reply was accepted");
}
using (FakeLineTransport lost = new("lost fake") { LoseConnection = true })
{
    RequireThrows<IOException>(() => BridgeBulkTransfer.SendAndExpect(lost, "GBCLR",
        line => line == "OK GBCLR", TimeSpan.FromSeconds(1), CancellationToken.None),
        "GB connection loss was accepted");
}
using (FakeLineTransport cancelled = new("cancel fake"))
using (CancellationTokenSource source = new())
{
    source.Cancel();
    RequireThrows<OperationCanceledException>(() => BridgeBulkTransfer.SendAndExpect(cancelled,
        "GBCLR", line => true, TimeSpan.FromSeconds(1), source.Token),
        "GB cancellation was ignored");
}
Console.WriteLine("PASS GB timeout, malformed/error, connection-loss, cancellation handling");

byte[] pak = Pattern(BridgeBulkTransfer.ControllerPakSize);
using FakeLineTransport usbPak = new("SerialBridgeTransport fake") { PakImage = pak };
using FakeLineTransport tcpPak = new("TcpBridgeTransport fake") { PakImage = pak };
BridgeBulkTransfer.UploadControllerPak(usbPak, pak, null, CancellationToken.None);
BridgeBulkTransfer.UploadControllerPak(tcpPak, pak, null, CancellationToken.None);
Require(usbPak.Commands.SequenceEqual(tcpPak.Commands), "USB/TCP Controller Pak upload streams differ");
Require(usbPak.Commands[0] == "PAKMODE MEM" && usbPak.Commands[1].StartsWith("PAK 0000 ") &&
        usbPak.Commands[^1].StartsWith("PAK 7FE0 "), "Controller Pak upload block ordering changed");
Console.WriteLine("PASS equivalent USB/TCP Controller Pak upload and block ordering");

using FakeLineTransport usbDump = new("SerialBridgeTransport fake") { PakImage = pak };
using FakeLineTransport tcpDump = new("TcpBridgeTransport fake") { PakImage = pak };
byte[] usbImage = BridgeBulkTransfer.DownloadControllerPak(usbDump, null, CancellationToken.None);
byte[] tcpImage = BridgeBulkTransfer.DownloadControllerPak(tcpDump, null, CancellationToken.None);
Require(usbImage.SequenceEqual(pak) && tcpImage.SequenceEqual(pak), "Controller Pak dump data differs");
Require(usbDump.Commands.SequenceEqual(tcpDump.Commands), "USB/TCP Controller Pak dump streams differ");
Console.WriteLine("PASS equivalent USB/TCP Controller Pak dump/save");

using (FakeLineTransport malformedPak = new("malformed PAK fake") { MalformedPakDump = true })
{
    RequireThrows<InvalidOperationException>(() =>
        BridgeBulkTransfer.DownloadControllerPak(malformedPak, null, CancellationToken.None),
        "malformed Controller Pak block was accepted");
}
using (FakeLineTransport timeoutPak = new("timeout PAK fake") { AlwaysTimeout = true })
{
    RequireThrows<TimeoutException>(() =>
        BridgeBulkTransfer.DownloadControllerPak(timeoutPak, null, CancellationToken.None),
        "Controller Pak timeout was accepted");
}
using (FakeLineTransport lostPak = new("lost PAK fake") { LoseConnection = true })
{
    RequireThrows<IOException>(() =>
        BridgeBulkTransfer.UploadControllerPak(lostPak, pak, null, CancellationToken.None),
        "Controller Pak connection loss was accepted");
}
Console.WriteLine("PASS Controller Pak malformed-response and connection-loss handling");

object ownershipGate = new();
using FakeLineTransport owned = new("ownership fake") { BlockFirstWrite = true };
bool competitorEntered = false;
Task bulk = Task.Run(() =>
{
    lock (ownershipGate)
    {
        BridgeBulkTransfer.SendAndExpect(owned, "GBCLR", line => line == "OK GBCLR",
            TimeSpan.FromSeconds(2), CancellationToken.None);
    }
});
Require(owned.FirstWriteEntered.Wait(TimeSpan.FromSeconds(1)), "bulk owner did not enter transport");
Task competitor = Task.Run(() =>
{
    lock (ownershipGate) competitorEntered = true;
});
Thread.Sleep(50);
Require(!competitorEntered, "background reader entered during bulk ownership");
owned.ReleaseFirstWrite.Set();
Task.WaitAll(bulk, competitor);
Require(competitorEntered, "background reader did not resume after bulk ownership");
Console.WriteLine("PASS shared ownership gate excludes competing background reader");

Require(!BridgeUiPolicy.ShowLegacyFirst64KGameBoyLoad,
    "legacy first-64-KiB loader is exposed in normal UI");
Require(BridgeUiPolicy.FullGameBoyLoadMenuText == "Load full GB/GBC ROM..." &&
        BridgeUiPolicy.ControllerPakLoadMenuText == "Load .mpk..." &&
        BridgeUiPolicy.ControllerPakSaveMenuText == "Save .mpk...",
    "normal full-GB or Controller Pak UI entries changed");
Require(BridgeConstants.BridgeVersion == "1.9.0", "Bridge runtime version is not 1.9.0");
Console.WriteLine("PASS v1.9.0 UI policy hides legacy loader; full and Controller Pak entries are integration-checked");
Console.WriteLine("ALL WIRELESS BULK TRANSFER SMOKE TESTS PASSED");

sealed class FakeLineTransport(string displayName) : IBridgeLineTransport
{
    private readonly Queue<string> _replies = new();
    private readonly Dictionary<string, int> _writes = new();
    public List<string> Commands { get; } = [];
    public bool IsConnected => !LoseConnection;
    public string DisplayName { get; } = displayName;
    public bool AlwaysTimeout { get; set; }
    public bool LoseConnection { get; set; }
    public bool MalformedPakDump { get; set; }
    public bool CorruptFlashCrc { get; set; }
    public bool AlterDuplicateReplay { get; set; }
    public bool RetainTimedOutReply { get; set; }
    public bool InjectUnrelatedBeforeSecondPageAck { get; set; }
    public bool FailSecondPage { get; set; }
    public bool OnlyMismatchedGbfpAcks { get; set; }
    public string? ForcedReply { get; set; }
    public string? FailCommandPrefix { get; set; }
    public string? TimeoutOnceFor { get; set; }
    public byte[] PakImage { get; set; } = new byte[BridgeBulkTransfer.ControllerPakSize];
    public bool BlockFirstWrite { get; set; }
    public ManualResetEventSlim FirstWriteEntered { get; } = new(false);
    public ManualResetEventSlim ReleaseFirstWrite { get; } = new(false);
    private byte[] _gameBoyRom = [];
    private int _gameBoyRomLength;
    private int _nextGameBoyPageOffset;
    private int _lastGameBoyPageOffset = -1;
    private uint _lastGameBoyPageCrc;
    private bool _gameBoyTransactionFailed;
    public int GameBoyPageApplyCount { get; private set; }
    public int GameBoyPageReplayCount { get; private set; }
    public List<string> SecondPageResponses { get; } = [];
    public bool SecondPageExactAckObserved { get; private set; }
    public bool CommitAttemptedBeforeSecondPageExactAck { get; private set; }
    private bool _awaitingSecondPageAck;
    private string? _retainedTimedOutReply;

    public void WriteLine(string line)
    {
        if (LoseConnection) throw new IOException("simulated connection loss");
        Commands.Add(line);
        _writes[line] = _writes.GetValueOrDefault(line) + 1;
        if (BlockFirstWrite && Commands.Count == 1)
        {
            FirstWriteEntered.Set();
            ReleaseFirstWrite.Wait(TimeSpan.FromSeconds(2));
        }
        if (line == "GBFCOMMIT" && !SecondPageExactAckObserved &&
            Commands.Any(c => c.StartsWith("GBFP 000100 ")))
            CommitAttemptedBeforeSecondPageExactAck = true;
        if (line.StartsWith("GBFP 000100 ")) _awaitingSecondPageAck = true;
        string reply = ForcedReply ?? ReplyFor(line);
        if (OnlyMismatchedGbfpAcks && line.StartsWith("GBFP ") &&
            reply.StartsWith("OK GBFP "))
            reply = "OK GBFP FFFFFF";
        if (AlwaysTimeout || (TimeoutOnceFor is not null &&
            line.StartsWith(TimeoutOnceFor) && _writes[line] == 1))
        {
            if (RetainTimedOutReply) _retainedTimedOutReply = reply;
            return;
        }
        if (InjectUnrelatedBeforeSecondPageAck && line.StartsWith("GBFP 000100 "))
        {
            _replies.Enqueue("OK PAK 0000");
            _replies.Enqueue("STAT harmless");
        }
        _replies.Enqueue(reply);
        if (RetainTimedOutReply && TimeoutOnceFor is not null &&
            line.StartsWith(TimeoutOnceFor) && _writes[line] == 2 &&
            _retainedTimedOutReply is not null)
        {
            _replies.Enqueue(_retainedTimedOutReply);
            _retainedTimedOutReply = null;
        }
    }

    public string ReadLine(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (LoseConnection) throw new IOException("simulated connection loss");
        if (_replies.Count == 0) throw new TimeoutException("simulated timeout");
        string response = _replies.Dequeue();
        if (_awaitingSecondPageAck)
        {
            SecondPageResponses.Add(response);
            if (response.Equals("OK GBFP 000100", StringComparison.OrdinalIgnoreCase))
            {
                SecondPageExactAckObserved = true;
                _awaitingSecondPageAck = false;
            }
        }
        return response;
    }

    private string ReplyFor(string line)
    {
        if (FailCommandPrefix is not null && line.StartsWith(FailCommandPrefix, StringComparison.Ordinal))
            return $"ERR {line.Split(' ')[0]} SIMULATED";
        if (line.StartsWith("PAKDUMP "))
        {
            string offsetText = line[8..];
            int offset = Convert.ToInt32(offsetText, 16);
            string data = MalformedPakDump ? "00" : Convert.ToHexString(PakImage, offset, BridgeBulkTransfer.BlockSize);
            return $"PAK {offsetText} {data}";
        }
        if (line.StartsWith("PAKMODE ")) return "OK PAKMODE";
        if (line.StartsWith("PAK ")) return "OK PAK";
        if (line.StartsWith("GBP ")) return "OK GBP";
        if (line == "GBCLR")
        {
            _gameBoyRom = [];
            _gameBoyRomLength = 0;
            _nextGameBoyPageOffset = 0;
            _lastGameBoyPageOffset = -1;
            _lastGameBoyPageCrc = 0;
            _gameBoyTransactionFailed = false;
            return "OK GBCLR";
        }
        if (line.StartsWith("GBFBEGIN "))
        {
            _gameBoyRomLength = Convert.ToInt32(line[10..], 16);
            _gameBoyRom = new byte[(_gameBoyRomLength + 255) & ~255];
            _nextGameBoyPageOffset = 0;
            _lastGameBoyPageOffset = -1;
            _lastGameBoyPageCrc = 0;
            _gameBoyTransactionFailed = false;
            GameBoyPageApplyCount = 0;
            GameBoyPageReplayCount = 0;
            return "OK GBFBEGIN";
        }
        if (line.StartsWith("GBFP "))
        {
            if (_gameBoyTransactionFailed) return "ERR GBFP TRANSACTION_FAILED";
            int offset = Convert.ToInt32(line.Substring(5, 6), 16);
            byte[] page = Convert.FromHexString(line[12..]);
            if (FailSecondPage && offset == 0x000100)
                return "ERR GBFP FLASH_EXECUTION_FAILED";
            if (offset == _nextGameBoyPageOffset)
            {
                page.CopyTo(_gameBoyRom, offset);
                _lastGameBoyPageOffset = offset;
                _lastGameBoyPageCrc = BridgeBulkTransfer.ComputeCrc32(page);
                _nextGameBoyPageOffset += Math.Min(page.Length,
                    _gameBoyRomLength - _nextGameBoyPageOffset);
                GameBoyPageApplyCount++;
                return $"OK GBFP {offset:X6}";
            }
            if (offset == _lastGameBoyPageOffset)
            {
                if (AlterDuplicateReplay) page[0] ^= 1;
                uint incomingCrc = BridgeBulkTransfer.ComputeCrc32(page);
                if (incomingCrc == _lastGameBoyPageCrc)
                {
                    GameBoyPageReplayCount++;
                    return $"OK GBFP {offset:X6}";
                }
                _gameBoyTransactionFailed = true;
                return "ERR GBFP REPLAY_MISMATCH";
            }
            _gameBoyTransactionFailed = true;
            return "ERR GBFP OUT_OF_ORDER";
        }
        if (line == "GBFCOMMIT")
            return !_gameBoyTransactionFailed &&
                _nextGameBoyPageOffset == _gameBoyRomLength
                ? "OK GBFCOMMIT" : "ERR GBFCOMMIT INCOMPLETE";
        if (line == "GBFCRC?")
        {
            uint crc = BridgeBulkTransfer.ComputeCrc32(
                _gameBoyRom.AsSpan(0, _gameBoyRomLength));
            if (CorruptFlashCrc) crc ^= 1u;
            return $"GBFCRC {_gameBoyRomLength:X8} {crc:X8}";
        }
        if (line.StartsWith("GBS ")) return "OK GBS";
        return $"OK {line.Split(' ')[0]}";
    }

    public string ReadExisting() => string.Empty;
    public void DiscardInput() => _replies.Clear();
    public void Dispose()
    {
        FirstWriteEntered.Dispose();
        ReleaseFirstWrite.Dispose();
    }
}
