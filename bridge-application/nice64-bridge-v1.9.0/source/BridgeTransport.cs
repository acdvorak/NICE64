namespace PicoSwitchController;

using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;

public enum BridgeConnectionKind
{
    Usb,
    NetworkTcp
}

public static class Nice64NetworkEndpoint
{
    public const int DefaultPort = 6464;

    public static bool TryValidate(string address, int port, out IPAddress? ip, out string error)
    {
        ip = null;
        if (!IPAddress.TryParse(address?.Trim(), out IPAddress? parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "Enter a valid NICE64 IPv4 address.";
            return false;
        }
        if (port is < 1 or > 65535)
        {
            error = "TCP port must be between 1 and 65535.";
            return false;
        }
        ip = parsed;
        error = string.Empty;
        return true;
    }
}

public interface IBridgeLineTransport : IDisposable
{
    bool IsConnected { get; }
    string DisplayName { get; }
    void WriteLine(string line);
    string ReadLine(TimeSpan timeout, CancellationToken cancellationToken);
    string ReadExisting();
    void DiscardInput();
}

public sealed class SerialBridgeTransport(SerialPort serial) : IBridgeLineTransport
{
    public bool IsConnected => serial.IsOpen;
    public string DisplayName => serial.PortName;
    public void WriteLine(string line) => serial.WriteLine(line);
    public string ReadLine(TimeSpan timeout, CancellationToken cancellationToken)
    {
        int previousTimeout = serial.ReadTimeout;
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan remaining = deadline - DateTime.UtcNow;
                serial.ReadTimeout = Math.Max(1, (int)Math.Min(250, remaining.TotalMilliseconds));
                try
                {
                    return serial.ReadLine().TrimEnd('\r', '\n');
                }
                catch (TimeoutException)
                {
                }
            }
        }
        finally
        {
            serial.ReadTimeout = previousTimeout;
        }
        throw new TimeoutException($"Timed out waiting for a line from {DisplayName}.");
    }
    public string ReadExisting() => serial.ReadExisting();
    public void DiscardInput() => serial.DiscardInBuffer();
    public void Dispose() { }
}

public sealed record Nice64TcpHandshake(FirmwareModeInfo Mode, string Capabilities);

public sealed class BridgeTransportSessionOwner<T> where T : class, IDisposable
{
    private readonly object _sync = new();
    private T? _current;
    private long _generation;
    private bool _transitionOwnsLifecycle;

    public T? Current { get { lock (_sync) return _current; } }
    public T? Active { get { lock (_sync) return _transitionOwnsLifecycle ? null : _current; } }
    public long Generation { get { lock (_sync) return _generation; } }

    public long InstallFresh(T transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        T? previous;
        long generation;
        lock (_sync)
        {
            previous = _current;
            _current = transport;
            _transitionOwnsLifecycle = false;
            generation = ++_generation;
        }
        if (!ReferenceEquals(previous, transport)) previous?.Dispose();
        return generation;
    }

    public long BeginTransitionRetirement()
    {
        T? retired;
        long generation;
        lock (_sync)
        {
            retired = _current;
            _current = null;
            _transitionOwnsLifecycle = true;
            generation = ++_generation;
        }
        retired?.Dispose();
        return generation;
    }

    public bool TryRetireObserved(T observed, out long retiredGeneration)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_current, observed))
            {
                retiredGeneration = _generation;
                return false;
            }
            _current = null;
            retiredGeneration = ++_generation;
        }
        observed.Dispose();
        return true;
    }

    public long RetireCurrent()
    {
        T? retired;
        long generation;
        lock (_sync)
        {
            retired = _current;
            _current = null;
            _transitionOwnsLifecycle = false;
            generation = ++_generation;
        }
        retired?.Dispose();
        return generation;
    }

    public void EndTransitionWithoutReplacement()
    {
        lock (_sync) _transitionOwnsLifecycle = false;
    }

    public bool IsStillRetired(long generation)
    {
        lock (_sync) return _generation == generation && _current is null;
    }
}

public sealed class TcpBridgeTransport : IBridgeLineTransport
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly object _ioLock = new();
    private readonly byte[] _readBuffer = new byte[2048];
    private string _received = string.Empty;
    private bool _disposed;

    private TcpBridgeTransport(TcpClient client, IPAddress address, int port)
    {
        _client = client;
        _stream = client.GetStream();
        DisplayName = $"{address}:{port}";
    }

    public bool IsConnected
    {
        get
        {
            if (_disposed || !_client.Connected) return false;
            try
            {
                return !(_client.Client.Poll(0, SelectMode.SelectRead) && _client.Client.Available == 0);
            }
            catch
            {
                return false;
            }
        }
    }
    public string DisplayName { get; }

    public static async Task<(TcpBridgeTransport Transport, Nice64TcpHandshake Handshake)> ConnectVerifiedAsync(
        IPAddress address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        TcpClient client = new(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await client.ConnectAsync(address, port, timeoutSource.Token).ConfigureAwait(false);
            TcpBridgeTransport transport = new(client, address, port);
            Nice64TcpHandshake handshake = await Task.Run(
                () => transport.VerifyNice64(timeout), cancellationToken).ConfigureAwait(false);
            return (transport, handshake);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private Nice64TcpHandshake VerifyNice64(TimeSpan timeout)
    {
        WriteLine("WHO");
        string who = ReadLine(timeout, CancellationToken.None);
        if (who == "NICE64|BUSY")
        {
            throw new IOException("NICE64 already has an active TCP Bridge owner.");
        }
        if (!string.Equals(who, FirmwareModeProtocol.WhoIdentity, StringComparison.Ordinal))
        {
            throw new IOException("The TCP endpoint did not return the expected NICE64 identity.");
        }

        WriteLine("MODE?");
        string modeLine = ReadLineMatching(timeout, CancellationToken.None,
            line => FirmwareModeProtocol.TryParseMode(line, out _));
        _ = FirmwareModeProtocol.TryParseMode(modeLine, out FirmwareModeInfo mode);

        WriteLine("CAPS?");
        string capabilities = ReadLineMatching(timeout, CancellationToken.None,
            line => line.StartsWith("CAPS|", StringComparison.Ordinal));
        return new Nice64TcpHandshake(mode, capabilities);
    }

    public string ReadLineMatching(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Predicate<string> expected,
        Action<string>? unrelated = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (true)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException(
                    $"Timed out waiting for an expected response from {DisplayName}.");

            string line = ReadLine(remaining, cancellationToken).Trim();
            if (expected(line)) return line;
            if (line.StartsWith("ERR", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(line);
            unrelated?.Invoke(line);
        }
    }

    public void WriteLine(string line)
    {
        byte[] data = Encoding.ASCII.GetBytes(line + "\n");
        lock (_ioLock)
        {
            ThrowIfDisconnected();
            _stream.Write(data);
        }
    }

    public string ReadExisting()
    {
        lock (_ioLock)
        {
            ThrowIfDisconnected();
            while (_stream.DataAvailable)
            {
                int read = _stream.Read(_readBuffer);
                if (read == 0) throw new IOException("NICE64 closed the TCP connection.");
                _received += Encoding.ASCII.GetString(_readBuffer, 0, read);
            }
            string available = _received;
            _received = string.Empty;
            return available;
        }
    }

    public string ReadLine(TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (_ioLock)
        {
            ThrowIfDisconnected();
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int newline = _received.IndexOf('\n');
                if (newline >= 0)
                {
                    string line = _received[..newline].TrimEnd('\r');
                    _received = _received[(newline + 1)..];
                    return line;
                }
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Timed out waiting for a line from {DisplayName}.");
                }
                using CancellationTokenSource timeoutSource =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(remaining);
                int read;
                try
                {
                    read = _stream.ReadAsync(_readBuffer.AsMemory(), timeoutSource.Token)
                        .AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timed out waiting for a line from {DisplayName}.");
                }
                if (read == 0) throw new IOException("NICE64 closed the TCP connection.");
                _received += Encoding.ASCII.GetString(_readBuffer, 0, read);
                if (_received.Length > 4096)
                {
                    throw new IOException("NICE64 TCP receive line exceeded its 4096-character bound.");
                }
            }
        }
    }

    public void DiscardInput()
    {
        lock (_ioLock)
        {
            ThrowIfDisconnected();
            while (_stream.DataAvailable)
            {
                int read = _stream.Read(_readBuffer);
                if (read == 0) throw new IOException("NICE64 closed the TCP connection.");
            }
            _received = string.Empty;
        }
    }

    private void ThrowIfDisconnected()
    {
        if (_disposed || !_client.Connected) throw new IOException("NICE64 TCP connection is closed.");
    }

    public void Dispose()
    {
        lock (_ioLock)
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Dispose();
            _client.Dispose();
        }
    }
}
