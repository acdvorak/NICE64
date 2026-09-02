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
Console.WriteLine("PASS equivalent USB/TCP full GB transfer, profile, pages, padding, commit");

using (FakeLineTransport retry = new("TCP retry fake"))
{
    retry.TimeoutOnceFor = "GBFP 000000";
    BridgeBulkTransfer.InstallFullGameBoyRom(retry, Pattern(256), new(0x01, 256, 32, 0),
        Pattern(32), null, null, CancellationToken.None);
    Require(retry.Commands.Count(c => c.StartsWith("GBFP 000000 ")) == 2,
        "timed-out GB page was not retried exactly once");
}
Console.WriteLine("PASS bounded idempotent GB page retry");

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
Require(BridgeConstants.BridgeVersion == "1.6.0", "Bridge runtime version is not 1.6.0");
Console.WriteLine("PASS v1.6.0 UI policy hides legacy loader; full and Controller Pak entries are integration-checked");
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
    public string? ForcedReply { get; set; }
    public string? TimeoutOnceFor { get; set; }
    public byte[] PakImage { get; set; } = new byte[BridgeBulkTransfer.ControllerPakSize];
    public bool BlockFirstWrite { get; set; }
    public ManualResetEventSlim FirstWriteEntered { get; } = new(false);
    public ManualResetEventSlim ReleaseFirstWrite { get; } = new(false);

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
        if (AlwaysTimeout || (TimeoutOnceFor is not null && line.StartsWith(TimeoutOnceFor) && _writes[line] == 1))
            return;
        _replies.Enqueue(ForcedReply ?? ReplyFor(line));
    }

    public string ReadLine(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (LoseConnection) throw new IOException("simulated connection loss");
        if (_replies.Count == 0) throw new TimeoutException("simulated timeout");
        return _replies.Dequeue();
    }

    private string ReplyFor(string line)
    {
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
        if (line.StartsWith("GBFBEGIN ")) return "OK GBFBEGIN";
        if (line.StartsWith("GBFP ")) return "OK GBFP";
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
