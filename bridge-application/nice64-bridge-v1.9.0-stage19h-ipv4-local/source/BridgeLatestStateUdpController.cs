namespace PicoSwitchController;

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

internal readonly record struct BridgeLatestStateUdpSenderSnapshot(
    uint SessionId,
    uint LastSentSequence,
    bool CurrentStatePresent,
    int PendingHistoricalStates,
    long ReportsSent,
    long CurrentStateReplacements,
    long SendFailures);

internal enum BridgeControllerTransportPath
{
    LineFallback,
    LatestStateUdp
}

internal readonly record struct BridgeControllerTransportSelection(
    BridgeControllerTransportPath Path,
    string LineFallbackReason);

internal enum BridgeControllerDeliveryGuard
{
    None,
    LineOnly,
    All
}

internal static class BridgeControllerDeliveryGuardPolicy
{
    public static BridgeControllerDeliveryGuard Select(
        bool allDeliveryGuardActive,
        bool protectedContentActive) =>
        allDeliveryGuardActive
            ? BridgeControllerDeliveryGuard.All
            : protectedContentActive
                ? BridgeControllerDeliveryGuard.LineOnly
                : BridgeControllerDeliveryGuard.None;

    public static bool Allows(
        BridgeControllerDeliveryGuard guard,
        BridgeControllerTransportSelection transport) =>
        guard == BridgeControllerDeliveryGuard.None ||
        guard == BridgeControllerDeliveryGuard.LineOnly &&
            transport.Path == BridgeControllerTransportPath.LatestStateUdp;
}

internal static class BridgeLatestStateControllerPathPolicy
{
    public static BridgeControllerTransportSelection Select(
        BridgeControllerState state,
        BridgeDeviceStateSnapshot device,
        bool tcpSessionConnected,
        string networkAddress)
    {
        if (state.ControllerType != ConsoleControllerType.N64)
            return new(BridgeControllerTransportPath.LineFallback,
                "controller-profile-not-n64");
        if (device.FirmwarePersonality != FirmwarePersonality.Controller)
            return new(BridgeControllerTransportPath.LineFallback,
                "controller-firmware-not-active");
        if (device.ControlConnectionKind != BridgeControlConnectionKind.WifiTcp)
            return new(BridgeControllerTransportPath.LineFallback,
                "control-connection-not-wifi-tcp");
        if (!tcpSessionConnected)
            return new(BridgeControllerTransportPath.LineFallback,
                "tcp-session-not-connected");
        if (!IPAddress.TryParse(networkAddress, out _))
            return new(BridgeControllerTransportPath.LineFallback,
                "pico-ipv4-unavailable");
        return new(BridgeControllerTransportPath.LatestStateUdp, string.Empty);
    }
}

internal sealed class BridgeLatestStateAcceptancePolicy
{
    public const int RetiredSessionCapacity = 4;
    private bool _initialized;
    private uint _sessionId;
    private uint _sequence;
    private readonly uint[] _retiredSessions = new uint[RetiredSessionCapacity];
    private int _retiredCount;
    private int _retiredNext;

    public bool TryAccept(uint sessionId, uint sequence)
    {
        if (sessionId == 0) return false;
        if (_initialized)
        {
            if (sessionId == _sessionId)
            {
                if (!Nice64RudpSequence.IsAfter(sequence, _sequence)) return false;
            }
            else
            {
                for (int index = 0; index < _retiredCount; index++)
                    if (_retiredSessions[index] == sessionId) return false;
                _retiredSessions[_retiredNext] = _sessionId;
                _retiredNext = (_retiredNext + 1) % RetiredSessionCapacity;
                if (_retiredCount < RetiredSessionCapacity) _retiredCount++;
            }
        }
        _initialized = true;
        _sessionId = sessionId;
        _sequence = sequence;
        return true;
    }
}

internal static class BridgeLatestStateUdpPacket
{
    public const int ControllerStateBytes = 4;
    public const int PacketBytes =
        Nice64WirelessTransportContract.HeaderLength + ControllerStateBytes;

    public static void Write(Span<byte> destination, uint sessionId,
        uint sequence, BridgeControllerState state)
    {
        if (destination.Length < PacketBytes)
            throw new ArgumentException($"A {PacketBytes}-byte packet buffer is required.",
                nameof(destination));
        if (sessionId == 0)
            throw new ArgumentOutOfRangeException(nameof(sessionId));

        destination[..PacketBytes].Clear();
        BinaryPrimitives.WriteUInt32BigEndian(destination[0..4],
            Nice64WirelessTransportContract.Magic);
        destination[4] = Nice64WirelessTransportContract.TransportVersion;
        destination[5] = (byte)Nice64RudpPacketFlags.Data;
        destination[6] = (byte)Nice64WirelessDeliveryClass.LatestState;
        destination[7] = Nice64WirelessTransportContract.HeaderLength;
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..12], sessionId);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..16], sequence);
        BinaryPrimitives.WriteUInt16BigEndian(destination[24..26], ControllerStateBytes);
        BridgeControllerCommandBuilder.WriteCanonicalN64State(state,
            destination.Slice(Nice64WirelessTransportContract.HeaderLength,
                ControllerStateBytes));
    }
}

internal enum BridgeLatestStateUdpDiagnosticKind
{
    FailureStarted,
    FailureSummary,
    Recovered
}

internal readonly record struct BridgeLatestStateUdpDiagnostic(
    BridgeLatestStateUdpDiagnosticKind Kind,
    long TotalFailures,
    long SuppressedFailures,
    TimeSpan Interval,
    string ErrorClassification,
    string ErrorMessage);

internal sealed class BridgeLatestStateUdpFailureDiagnosticPolicy
{
    public static readonly TimeSpan DefaultSummaryInterval = TimeSpan.FromSeconds(5);
    private const int MaximumErrorTextLength = 512;
    private readonly TimeSpan _summaryInterval;
    private bool _failureActive;
    private long _totalFailures;
    private long _suppressedFailures;
    private long _lastDiagnosticTimestamp;
    private string _lastErrorClassification = string.Empty;
    private string _lastErrorMessage = string.Empty;

    public BridgeLatestStateUdpFailureDiagnosticPolicy(TimeSpan summaryInterval)
    {
        if (summaryInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(summaryInterval));
        _summaryInterval = summaryInterval;
    }

    public BridgeLatestStateUdpDiagnostic? RecordFailure(Exception error,
        long timestamp)
    {
        ArgumentNullException.ThrowIfNull(error);
        _totalFailures++;
        _lastErrorClassification = Limit(error.GetType().Name);
        _lastErrorMessage = Limit(error.Message);
        if (!_failureActive)
        {
            _failureActive = true;
            _lastDiagnosticTimestamp = timestamp;
            return Create(BridgeLatestStateUdpDiagnosticKind.FailureStarted,
                suppressedFailures: 0, TimeSpan.Zero);
        }

        _suppressedFailures++;
        TimeSpan elapsed = Stopwatch.GetElapsedTime(_lastDiagnosticTimestamp, timestamp);
        if (elapsed < _summaryInterval) return null;

        BridgeLatestStateUdpDiagnostic summary = Create(
            BridgeLatestStateUdpDiagnosticKind.FailureSummary,
            _suppressedFailures, elapsed);
        _suppressedFailures = 0;
        _lastDiagnosticTimestamp = timestamp;
        return summary;
    }

    public BridgeLatestStateUdpDiagnostic? RecordSuccess(long timestamp)
    {
        if (!_failureActive) return null;
        BridgeLatestStateUdpDiagnostic recovered = Create(
            BridgeLatestStateUdpDiagnosticKind.Recovered,
            _suppressedFailures,
            Stopwatch.GetElapsedTime(_lastDiagnosticTimestamp, timestamp));
        _failureActive = false;
        _totalFailures = 0;
        _suppressedFailures = 0;
        _lastDiagnosticTimestamp = 0;
        _lastErrorClassification = string.Empty;
        _lastErrorMessage = string.Empty;
        return recovered;
    }

    private BridgeLatestStateUdpDiagnostic Create(
        BridgeLatestStateUdpDiagnosticKind kind,
        long suppressedFailures,
        TimeSpan interval) =>
        new(kind, _totalFailures, suppressedFailures, interval,
            _lastErrorClassification, _lastErrorMessage);

    private static string Limit(string value) =>
        value.Length <= MaximumErrorTextLength
            ? value
            : value[..MaximumErrorTextLength];
}

internal static class BridgeLatestStateUdpDiagnosticFormatter
{
    public static string Format(BridgeLatestStateUdpDiagnostic diagnostic) =>
        diagnostic.Kind switch
        {
            BridgeLatestStateUdpDiagnosticKind.FailureStarted =>
                $"LATEST_STATE UDP send failed ({diagnostic.ErrorClassification}): " +
                diagnostic.ErrorMessage,
            BridgeLatestStateUdpDiagnosticKind.FailureSummary =>
                $"LATEST_STATE UDP send failures continue: " +
                $"{diagnostic.SuppressedFailures} suppressed in the last " +
                $"{diagnostic.Interval.TotalSeconds:F1} seconds. Last error " +
                $"({diagnostic.ErrorClassification}): {diagnostic.ErrorMessage}",
            BridgeLatestStateUdpDiagnosticKind.Recovered =>
                $"LATEST_STATE UDP send recovered after " +
                $"{diagnostic.TotalFailures} failures.",
            _ => throw new ArgumentOutOfRangeException(nameof(diagnostic))
        };
}

internal sealed class BridgeLatestStateUdpControllerSender : IAsyncDisposable
{
    public const int UpdatesPerSecond = 120;
    public const int DefaultPort = 6465;

    private readonly object _sync = new();
    private readonly UdpClient _udp;
    private readonly SemaphoreSlim _pendingSignal = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<BridgeControllerCommand> _delivered;
    private readonly Action<BridgeControllerCommand, Exception> _failed;
    private readonly Action<uint, uint, BridgeControllerCommand>? _reported;
    private readonly Action<BridgeLatestStateUdpDiagnostic>? _diagnostic;
    private readonly BridgeLatestStateUdpFailureDiagnosticPolicy _failureDiagnostics;
    private readonly Task _worker;
    private readonly byte[] _packet = new byte[BridgeLatestStateUdpPacket.PacketBytes];
    private readonly long _cadenceTicks =
        Math.Max(1, (long)Math.Ceiling(Stopwatch.Frequency / (double)UpdatesPerSecond));
    private BridgeControllerCommand? _current;
    private bool _reportingLoopActive;
    private bool _stopped;
    private uint _nextSequence;
    private uint _lastSentSequence;
    private long _reportsSent;
    private long _currentStateReplacements;
    private long _sendFailures;

    public BridgeLatestStateUdpControllerSender(string targetIp, int port,
        Action<BridgeControllerCommand> delivered,
        Action<BridgeControllerCommand, Exception> failed,
        Action<uint, uint, BridgeControllerCommand>? reported = null,
        Action<BridgeLatestStateUdpDiagnostic>? diagnostic = null)
    {
        if (!IPAddress.TryParse(targetIp, out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("A literal Pico IPv4 address is required.",
                nameof(targetIp));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        _delivered = delivered ?? throw new ArgumentNullException(nameof(delivered));
        _failed = failed ?? throw new ArgumentNullException(nameof(failed));
        _reported = reported;
        _diagnostic = diagnostic;
        _failureDiagnostics = new(
            BridgeLatestStateUdpFailureDiagnosticPolicy.DefaultSummaryInterval);
        SessionId = RandomNonzeroUInt32();
        _nextSequence = RandomNonzeroUInt32();
        Target = address.ToString();
        Port = port;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Connect(new IPEndPoint(address, port));
        _worker = Task.Run(SenderLoopAsync);
    }

    public uint SessionId { get; }
    public string Target { get; }
    public int Port { get; }

    public int SubmitLatest(BridgeControllerCommand command)
    {
        if (command.State.ControllerType != ConsoleControllerType.N64)
            throw new ArgumentException("LATEST_STATE accepts canonical N64 state only.",
                nameof(command));
        lock (_sync)
        {
            if (_stopped) return -1;
            int replaced = _current is null ? 0 : 1;
            if (replaced != 0) _currentStateReplacements++;
            _current = command;
            if (!_reportingLoopActive)
            {
                _reportingLoopActive = true;
                _pendingSignal.Release();
            }
            return replaced;
        }
    }

    public int ClearPending()
    {
        lock (_sync)
        {
            int cleared = _current is null ? 0 : 1;
            _current = null;
            return cleared;
        }
    }

    public BridgeLatestStateUdpSenderSnapshot Snapshot()
    {
        lock (_sync)
            return new(SessionId, _lastSentSequence, _current is not null, 0,
                _reportsSent, _currentStateReplacements, _sendFailures);
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true;
            _current = null;
            _reportingLoopActive = false;
        }
        _lifetime.Cancel();
        _udp.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        try { await _worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        _pendingSignal.Dispose();
        _lifetime.Dispose();
    }

    private async Task SenderLoopAsync()
    {
        while (true)
        {
            await _pendingSignal.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            long nextReportAt = Stopwatch.GetTimestamp();
            while (true)
            {
                long now = Stopwatch.GetTimestamp();
                if (nextReportAt > now)
                {
                    TimeSpan delay = TimeSpan.FromSeconds(
                        (nextReportAt - now) / (double)Stopwatch.Frequency);
                    await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
                }

                BridgeControllerCommand? command;
                lock (_sync)
                {
                    if (_stopped) return;
                    command = _current;
                }
                if (command is null)
                {
                    lock (_sync)
                    {
                        if (_current is null) _reportingLoopActive = false;
                        else continue;
                    }
                    break;
                }

                uint sequence = unchecked(_nextSequence++);
                BridgeLatestStateUdpPacket.Write(_packet, SessionId, sequence,
                    command.State);
                try
                {
                    await _udp.SendAsync(_packet, _lifetime.Token).ConfigureAwait(false);
                    lock (_sync)
                    {
                        _lastSentSequence = sequence;
                        _reportsSent++;
                    }
                    _delivered(command);
                    _reported?.Invoke(SessionId, sequence, command);
                    EmitDiagnostic(_failureDiagnostics.RecordSuccess(
                        Stopwatch.GetTimestamp()));
                }
                catch (Exception ex) when (ex is not OperationCanceledException ||
                                           !_lifetime.IsCancellationRequested)
                {
                    lock (_sync) _sendFailures++;
                    EmitDiagnostic(_failureDiagnostics.RecordFailure(ex,
                        Stopwatch.GetTimestamp()));
                    _failed(command, ex);
                }
                nextReportAt = Stopwatch.GetTimestamp() + _cadenceTicks;
            }
        }
    }

    private void EmitDiagnostic(BridgeLatestStateUdpDiagnostic? diagnostic)
    {
        if (diagnostic is null || _diagnostic is null) return;
        try { _diagnostic(diagnostic.Value); }
        catch
        {
            // Diagnostics must never change controller delivery behavior.
        }
    }

    private static uint RandomNonzeroUInt32()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        uint value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        } while (value == 0);
        return value;
    }
}
