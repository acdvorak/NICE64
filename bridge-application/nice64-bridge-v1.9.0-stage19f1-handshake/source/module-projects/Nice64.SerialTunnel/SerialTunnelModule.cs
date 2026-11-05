namespace PicoSwitchController;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Generic bounded byte mailbox over the existing Network Pak stream commands.
/// The optional N3T1 responder is an application-level diagnostic layered above
/// the byte stream; it does not alter Gold framing or firmware behavior.
/// </summary>
public sealed class SerialTunnelModule : INetworkPakModule
{
    public const string ModuleId = "serial-tunnel";
    public const string ResourceName = "serial://tunnel";
    public const int TestHeaderLength = 16;
    public const int MaximumTestPayload = 1024 * 1024;
    private const int ReceiveCapacity = MaximumTestPayload + 64;
    private const byte UploadCommand = 0x01;
    private const byte DownloadCommand = 0x02;
    private const byte UploadResult = 0x81;
    private const byte DownloadResult = 0x82;
    private const byte ErrorResult = 0xFF;
    private const byte FragmentedPattern = 0xF0;
    private static readonly byte[] Probe = "HELLO NICE64"u8.ToArray();
    private static readonly byte[] ProbeReply = "HELLO N64"u8.ToArray();

    private readonly object _sync = new();
    private readonly byte[] _receive = new byte[ReceiveCapacity];
    private readonly byte[] _header = new byte[TestHeaderLength];
    private readonly PppDiagnosticDecoder _pppDecoder = new();
    private int _receiveCount;
    private int _headerCount;
    private int _fragmentStage = -1;
    private long _fragmentAvailableAt;
    private bool _uploadActive;
    private uint _uploadLength;
    private uint _uploadRemaining;
    private uint _uploadIndex;
    private uint _uploadExpectedCrc;
    private uint _uploadCrc;
    private byte _uploadPattern;
    private bool _uploadPatternValid;
    private long _uploadStarted;
    private IncrementalHash? _uploadSha;

    public string Id => ModuleId;
    public string DisplayName => "Serial Tunnel";

    public static bool IsTunnelResource(string resource) =>
        string.Equals(resource, ResourceName, StringComparison.OrdinalIgnoreCase);

    public NetworkPakResource? OpenResource(string resource, ModuleContext context)
    {
        if (!IsTunnelResource(resource)) return null;
        lock (_sync)
        {
            int exposed = _receiveCount;
            if (_fragmentStage > 0 && Stopwatch.GetTimestamp() < _fragmentAvailableAt)
                exposed = 0;
            else if (_fragmentStage == 0) exposed = Math.Min(exposed, TestHeaderLength + 10);
            else if (_fragmentStage == 1) exposed = Math.Min(exposed, 15);
            else if (_fragmentStage == 2) exposed = Math.Min(exposed, 12);
            byte[] snapshot = _receive.AsSpan(0, exposed).ToArray();
            context.Log($"Serial Tunnel opened: available={snapshot.Length}");
            return new NetworkPakResource(ResourceName, snapshot, CommitRead);
        }
    }

    public NetworkPakWriteResult HandleWrite(NetworkPakWriteChunk chunk,
                                               ModuleContext context)
    {
        lock (_sync)
        {
            if (!_uploadActive && _headerCount == 0 && chunk.IsFirst &&
                chunk.IsLast && chunk.Data.AsSpan().SequenceEqual("NICE64"u8))
            {
                if (!TryQueue("ACK64"u8))
                    return NetworkPakWriteResult.Fail("SERIAL_BUFFER_FULL");
                context.Log("Serial Tunnel diagnostic RX 6 bytes: " +
                            "4E 49 43 45 36 34 ASCII=NICE64");
                context.Log("Serial Tunnel diagnostic TX queued 5 bytes: " +
                            "41 43 4B 36 34 ASCII=ACK64");
                return NetworkPakWriteResult.Ack([1]);
            }

            if (!_uploadActive && _headerCount == 0 && chunk.IsFirst &&
                chunk.IsLast && chunk.Data.AsSpan().SequenceEqual(Probe))
            {
                if (!TryQueue(ProbeReply))
                    return NetworkPakWriteResult.Fail("SERIAL_BUFFER_FULL");
                context.Log("Serial Tunnel received 12 bytes: HELLO NICE64");
                context.Log("Serial Tunnel queued 9 bytes: HELLO N64");
                return NetworkPakWriteResult.Ack([1]);
            }

            int consumed = 0;
            while (consumed < chunk.Data.Length)
            {
                if (_uploadActive)
                {
                    int take = (int)Math.Min(_uploadRemaining,
                        (uint)(chunk.Data.Length - consumed));
                    ConsumeUpload(chunk.Data.AsSpan(consumed, take));
                    consumed += take;
                    if (_uploadRemaining == 0 && !FinishUpload(context))
                        return NetworkPakWriteResult.Fail("SERIAL_BUFFER_FULL");
                    continue;
                }

                int target = _headerCount < 4 ? 4 : TestHeaderLength;
                int headerTake = Math.Min(target - _headerCount,
                                          chunk.Data.Length - consumed);
                chunk.Data.AsSpan(consumed, headerTake)
                    .CopyTo(_header.AsSpan(_headerCount));
                _headerCount += headerTake;
                consumed += headerTake;
                if (_headerCount == 4 &&
                    !_header.AsSpan(0, 4).SequenceEqual("N3T1"u8))
                {
                    _pppDecoder.Feed(_header.AsSpan(0, 4), context.Log);
                    _headerCount = 0;
                    continue;
                }
                if (_headerCount == TestHeaderLength && !BeginCommand(context))
                    return NetworkPakWriteResult.Fail("SERIAL_BAD_TEST_FRAME");
            }
            if (chunk.IsLast && !_uploadActive && _headerCount != 0)
            {
                _pppDecoder.Feed(_header.AsSpan(0, _headerCount), context.Log);
                _headerCount = 0;
            }
            return NetworkPakWriteResult.Ack([1]);
        }
    }

    public static byte GeneratePatternByte(byte pattern, uint index) => pattern switch
    {
        0x01 => 0x00,
        0x02 => 0xFF,
        0x03 => (byte)((index & 1) == 0 ? 0x00 : 0xFF),
        0x04 => (byte)index,
        0x05 => (byte)(0xFF - (byte)index),
        0x06 => new byte[] { 0x4E, 0x50, 0x01, 0xA4, 0x00, 0x0A, 0x0D, 0xFF }[(int)(index & 7)],
        FragmentedPattern => (byte)((index * 37 + 11) & 0xFF),
        _ => (byte)((index * 37 + 11) & 0xFF)
    };

    public static uint ComputePatternCrc(byte pattern, uint length)
    {
        uint crc = 0xFFFFFFFF;
        for (uint index = 0; index < length; ++index)
            crc = UpdateCrc(crc, GeneratePatternByte(pattern, index));
        return ~crc;
    }

    private bool BeginCommand(ModuleContext context)
    {
        byte command;
        byte pattern;
        uint length;
        uint expectedCrc;
        if (!_header.AsSpan(0, 4).SequenceEqual("N3T1"u8))
        {
            _headerCount = 0;
            context.Log("Serial Tunnel ignored non-test raw bytes");
            return true;
        }

        command = _header[4];
        pattern = _header[5];
        length = ReadU32(_header, 8);
        expectedCrc = ReadU32(_header, 12);
        _headerCount = 0;
        if (length > MaximumTestPayload ||
            expectedCrc != ComputePatternCrc(pattern, length))
            return false;

        if (command == UploadCommand)
        {
            _uploadActive = true;
            _uploadLength = length;
            _uploadRemaining = length;
            _uploadIndex = 0;
            _uploadExpectedCrc = expectedCrc;
            _uploadCrc = 0xFFFFFFFF;
            _uploadPattern = pattern;
            _uploadPatternValid = true;
            _uploadStarted = Stopwatch.GetTimestamp();
            _uploadSha?.Dispose();
            _uploadSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            context.Log($"Serial Tunnel upload begin bytes={length} crc32={expectedCrc:X8}");
            if (length == 0) return FinishUpload(context);
            return true;
        }

        if (command == DownloadCommand)
            return QueueDownload(pattern, length, expectedCrc, context);
        return false;
    }

    private void ConsumeUpload(ReadOnlySpan<byte> bytes)
    {
        _uploadSha!.AppendData(bytes);
        foreach (byte value in bytes)
        {
            if (value != GeneratePatternByte(_uploadPattern, _uploadIndex))
                _uploadPatternValid = false;
            _uploadCrc = UpdateCrc(_uploadCrc, value);
            ++_uploadIndex;
        }
        _uploadRemaining -= (uint)bytes.Length;
    }

    private bool FinishUpload(ModuleContext context)
    {
        uint actualCrc = ~_uploadCrc;
        bool pass = _uploadPatternValid && actualCrc == _uploadExpectedCrc;
        byte[] sha = _uploadSha!.GetHashAndReset();
        _uploadSha.Dispose();
        _uploadSha = null;
        _uploadActive = false;
        byte[] result = MakeHeader(pass ? UploadResult : ErrorResult,
                                   _uploadPattern, _uploadLength, actualCrc);
        double elapsedMs = Stopwatch.GetElapsedTime(_uploadStarted).TotalMilliseconds;
        context.Log($"Serial Tunnel upload {(pass ? "PASS" : "FAIL")} " +
                    $"bytes={_uploadLength} crc32={actualCrc:X8} " +
                    $"sha256={Convert.ToHexString(sha)} elapsedMs={elapsedMs:F1}");
        return TryQueue(result);
    }

    private bool QueueDownload(byte pattern, uint length, uint crc,
                               ModuleContext context)
    {
        if (_receiveCount != 0 ||
            (ulong)length + TestHeaderLength > ReceiveCapacity)
            return false;
        byte[] header = MakeHeader(DownloadResult, pattern, length, crc);
        header.CopyTo(_receive, 0);
        for (uint index = 0; index < length; ++index)
            _receive[TestHeaderLength + (int)index] = GeneratePatternByte(pattern, index);
        _receiveCount = checked((int)length + TestHeaderLength);
        _fragmentStage = pattern == FragmentedPattern && length == 37 ? 0 : -1;
        _fragmentAvailableAt = 0;
        byte[] sha = SHA256.HashData(_receive.AsSpan(TestHeaderLength, (int)length));
        context.Log($"Serial Tunnel download queued bytes={length} crc32={crc:X8} " +
                    $"sha256={Convert.ToHexString(sha)}");
        return true;
    }

    private bool TryQueue(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > ReceiveCapacity - _receiveCount) return false;
        bytes.CopyTo(_receive.AsSpan(_receiveCount));
        _receiveCount += bytes.Length;
        return true;
    }

    private void CommitRead(int count)
    {
        lock (_sync)
        {
            count = Math.Clamp(count, 0, _receiveCount);
            if (count == 0) return;
            _receive.AsSpan(count, _receiveCount - count).CopyTo(_receive);
            _receiveCount -= count;
            if (_fragmentStage >= 0)
            {
                ++_fragmentStage;
                if (_fragmentStage > 2 || _receiveCount == 0) _fragmentStage = -1;
                else _fragmentAvailableAt = Stopwatch.GetTimestamp() +
                    Stopwatch.Frequency / 20;
            }
        }
    }

    private static byte[] MakeHeader(byte command, byte pattern,
                                     uint length, uint crc)
    {
        byte[] result = new byte[TestHeaderLength];
        "N3T1"u8.CopyTo(result);
        result[4] = command;
        result[5] = pattern;
        WriteU32(result, 8, length);
        WriteU32(result, 12, crc);
        return result;
    }

    private static uint ReadU32(byte[] bytes, int offset) =>
        ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
        ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];

    private static void WriteU32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; ++bit)
            crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320 : 0);
        return crc;
    }
}
