using PicoSwitchController;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

if (typeof(SerialTunnelModule).Assembly.GetName().Name != "Nice64.SerialTunnel")
    throw new InvalidOperationException("Serial implementation is not owned by Nice64.SerialTunnel.dll");
using (var discovery = new NetworkPakBridgeService())
{
    INetworkPakModule? loaded = discovery.ListModules().FirstOrDefault(
        module => module.Id == SerialTunnelModule.ModuleId);
    if (loaded?.GetType().Assembly.GetName().Name != "Nice64.SerialTunnel")
        throw new InvalidOperationException("serial://tunnel was not discovered through the external module loader");
}

const byte Upload = 0x01;
const byte Download = 0x02;
const byte UploadResult = 0x81;
const byte DownloadResult = 0x82;
const byte FragmentedPattern = 0xF0;
uint[] requiredSizes = [1024, 4096, 16384, 65536, 262144, 1048576];
uint[] boundaries = [0, 1, 15, 16, 17, 20, 21, 22, 23, 31, 32, 33,
                     63, 64, 65, 127, 128, 129, 255, 256, 257,
                     511, 512, 513, 1023, 1024, 1025];
List<string> log = [];
ModuleContext context = new(AppContext.BaseDirectory, log.Add, "test", 23, 15360, "");
SerialTunnelModule module = new();
Stopwatch total = Stopwatch.StartNew();

Probe(module, context);
AssertEmpty(module, context, "post-probe empty read");

foreach (uint size in requiredSizes)
{
    TimeTransfer("upload", size, () => UploadPattern(module, context, size, 0x04));
    TimeTransfer("download", size, () => DownloadPattern(module, context, size, 0x04));
}

foreach (uint size in boundaries)
{
    UploadPattern(module, context, size, 0x04);
    DownloadPattern(module, context, size, 0x04);
}

foreach (byte pattern in new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0xF0 })
{
    UploadPattern(module, context, 257, pattern);
    DownloadPattern(module, context, 257, pattern);
}

byte[] fragmented = DownloadPattern(module, context, 37, FragmentedPattern,
                                    expectedFragments: [26, 15, 12]);
Require(fragmented.Length == SerialTunnelModule.TestHeaderLength + 37,
        "37-byte fragmented response length mismatch");

for (int cycle = 0; cycle < 100; ++cycle)
    Probe(module, context);
Console.WriteLine("HOST-SIM open/use/commit cycles=100 PASS");

Stopwatch repeated = Stopwatch.StartNew();
for (int cycle = 0; cycle < 100; ++cycle)
{
    UploadPattern(module, context, 4096, 0xF0);
    DownloadPattern(module, context, 4096, 0xF0);
}
repeated.Stop();
Console.WriteLine($"HOST-SIM repeated=100x4KiB-each-direction " +
                  $"elapsedMs={repeated.Elapsed.TotalMilliseconds:F1} PASS");

AssertRejected(module.HandleWrite(new NetworkPakWriteChunk(0x03,
    MakeHeader(Download, 0x04, 1048577, 0)), context), "oversized request");
AssertRejected(module.HandleWrite(new NetworkPakWriteChunk(0x03,
    MakeHeader(Download, 0x04, 32, 0x12345678)), context), "bad CRC request");
AssertEmpty(module, context, "final empty read");

total.Stop();
Console.WriteLine($"SerialTunnelHeadlessSmoke PASS elapsedMs={total.Elapsed.TotalMilliseconds:F1}");
Console.WriteLine($"Summary log entries={log.Count}; timings above are host simulation, not N64 hardware results.");

static void Probe(SerialTunnelModule module, ModuleContext context)
{
    NetworkPakWriteResult write = module.HandleWrite(
        new NetworkPakWriteChunk(0x03, Encoding.ASCII.GetBytes("HELLO NICE64")), context);
    Require(write.Success && write.Payload.AsSpan().SequenceEqual(new byte[] { 1 }),
            "serial probe write was not acknowledged");
    byte[] reply = ReadQueued(module, context);
    Require(reply.AsSpan().SequenceEqual("HELLO N64"u8),
            "deterministic probe reply mismatch");
}

static void UploadPattern(SerialTunnelModule module, ModuleContext context,
                          uint length, byte pattern)
{
    uint crc = SerialTunnelModule.ComputePatternCrc(pattern, length);
    byte[] frame = new byte[SerialTunnelModule.TestHeaderLength + checked((int)length)];
    MakeHeader(Upload, pattern, length, crc).CopyTo(frame, 0);
    for (uint index = 0; index < length; ++index)
        frame[SerialTunnelModule.TestHeaderLength + (int)index] =
            SerialTunnelModule.GeneratePatternByte(pattern, index);
    WriteFragmented(module, context, frame);
    byte[] result = ReadQueued(module, context);
    VerifyHeader(result, UploadResult, pattern, length, crc);
    Require(result.Length == SerialTunnelModule.TestHeaderLength,
            "upload result contained unexpected payload");
}

static byte[] DownloadPattern(SerialTunnelModule module, ModuleContext context,
                              uint length, byte pattern,
                              int[]? expectedFragments = null)
{
    uint crc = SerialTunnelModule.ComputePatternCrc(pattern, length);
    WriteFragmented(module, context, MakeHeader(Download, pattern, length, crc));
    byte[] result = ReadQueued(module, context, expectedFragments);
    VerifyHeader(result, DownloadResult, pattern, length, crc);
    Require(result.Length == SerialTunnelModule.TestHeaderLength + checked((int)length),
            "download response length mismatch");
    uint actualCrc = 0xFFFFFFFF;
    for (uint index = 0; index < length; ++index)
    {
        byte value = result[SerialTunnelModule.TestHeaderLength + (int)index];
        Require(value == SerialTunnelModule.GeneratePatternByte(pattern, index),
                $"download pattern mismatch at {index}");
        actualCrc = UpdateCrc(actualCrc, value);
    }
    Require(~actualCrc == crc, "download CRC mismatch");
    return result;
}

static void WriteFragmented(SerialTunnelModule module, ModuleContext context,
                            byte[] bytes)
{
    int[] pieces = [1, 7, 3, 21, 2, 13, 5, 19];
    int offset = 0;
    int pieceIndex = 0;
    if (bytes.Length == 0) return;
    while (offset < bytes.Length)
    {
        int count = Math.Min(pieces[pieceIndex++ % pieces.Length], bytes.Length - offset);
        byte flags = 0;
        if (offset == 0) flags |= 0x01;
        if (offset + count == bytes.Length) flags |= 0x02;
        NetworkPakWriteResult write = module.HandleWrite(
            new NetworkPakWriteChunk(flags, bytes.AsSpan(offset, count).ToArray()), context);
        Require(write.Success && write.Payload.AsSpan().SequenceEqual(new byte[] { 1 }),
                $"fragmented write failed at offset {offset}: {write.Error}");
        offset += count;
    }
}

static byte[] ReadQueued(SerialTunnelModule module, ModuleContext context,
                         int[]? expectedFragments = null)
{
    List<byte> result = [];
    List<int> fragments = [];
    for (int attempt = 0; attempt < 12; ++attempt)
    {
        NetworkPakResource resource = module.OpenResource(SerialTunnelModule.ResourceName, context)
            ?? throw new InvalidOperationException("serial resource did not open");
        if (resource.Data.Length == 0)
        {
            if (expectedFragments is null || fragments.Count == expectedFragments.Length)
                break;
            Thread.Sleep(60);
            continue;
        }
        fragments.Add(resource.Data.Length);
        result.AddRange(resource.Data);
        resource.CommitRead?.Invoke(resource.Data.Length);
        if (expectedFragments is null) break;
    }
    if (expectedFragments is not null)
        Require(fragments.SequenceEqual(expectedFragments),
                $"fragment exposure mismatch: [{string.Join(',', fragments)}]");
    return result.ToArray();
}

static void AssertEmpty(SerialTunnelModule module, ModuleContext context,
                        string operation)
{
    NetworkPakResource resource = module.OpenResource(SerialTunnelModule.ResourceName, context)
        ?? throw new InvalidOperationException("serial resource did not open");
    Require(resource.Data.Length == 0, operation + " returned queued data");
}

static void AssertRejected(NetworkPakWriteResult result, string operation) =>
    Require(!result.Success, operation + " was unexpectedly accepted");

static byte[] MakeHeader(byte command, byte pattern, uint length, uint crc)
{
    byte[] header = new byte[SerialTunnelModule.TestHeaderLength];
    "N3T1"u8.CopyTo(header);
    header[4] = command;
    header[5] = pattern;
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), length);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), crc);
    return header;
}

static void VerifyHeader(byte[] bytes, byte command, byte pattern,
                         uint length, uint crc)
{
    Require(bytes.Length >= SerialTunnelModule.TestHeaderLength, "response header truncated");
    Require(bytes.AsSpan(0, 4).SequenceEqual("N3T1"u8), "response magic mismatch");
    Require(bytes[4] == command && bytes[5] == pattern, "response command/pattern mismatch");
    Require(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)) == length,
            "response length field mismatch");
    Require(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)) == crc,
            "response CRC field mismatch");
}

static uint UpdateCrc(uint crc, byte value)
{
    crc ^= value;
    for (int bit = 0; bit < 8; ++bit)
        crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320 : 0);
    return crc;
}

static void TimeTransfer(string direction, uint size, Action transfer)
{
    Stopwatch timer = Stopwatch.StartNew();
    transfer();
    timer.Stop();
    double seconds = Math.Max(timer.Elapsed.TotalSeconds, 0.000001);
    Console.WriteLine($"HOST-SIM {direction} bytes={size} elapsedMs={timer.Elapsed.TotalMilliseconds:F1} " +
                      $"bytesPerSec={size / seconds:F0} PASS");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
