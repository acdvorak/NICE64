using PicoSwitchController;
using System.Net;
using System.Net.Sockets;
using System.Text;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task<string> ReadLineAsync(NetworkStream stream)
{
    List<byte> bytes = [];
    byte[] one = new byte[1];
    while (true)
    {
        int read = await stream.ReadAsync(one);
        if (read == 0) throw new IOException("client closed");
        if (one[0] == (byte)'\n') return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
        bytes.Add(one[0]);
    }
}

static int StartServer(string firstReply, bool validHandshake, CancellationToken token)
{
    TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    _ = Task.Run(async () =>
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(token);
        listener.Stop();
        NetworkStream stream = client.GetStream();
        Require(await ReadLineAsync(stream) == "WHO", "WHO command mismatch");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(firstReply + "\r\n"), token);
        if (!validHandshake) return;
        Require(await ReadLineAsync(stream) == "MODE?", "MODE? command mismatch");
        byte[] mode = Encoding.ASCII.GetBytes("MODE|STANDALONE|NETWORK\n");
        await stream.WriteAsync(mode.AsMemory(0, 8), token);
        await stream.WriteAsync(mode.AsMemory(8), token);
        Require(await ReadLineAsync(stream) == "CAPS?", "CAPS? command mismatch");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("CAPS|CONTROLLER|STANDALONE/NETWORK\n"), token);
        await Task.Delay(100, token);
    }, token);
    return port;
}

Require(Nice64NetworkEndpoint.DefaultPort == 6464, "default port changed");
Require(Nice64NetworkEndpoint.TryValidate("192.0.2.45", 6464, out IPAddress? parsed, out _) &&
        parsed!.ToString() == "192.0.2.45", "valid IPv4 rejected");
Require(!Nice64NetworkEndpoint.TryValidate("not-an-ip", 6464, out _, out _), "invalid IPv4 accepted");
Require(!Nice64NetworkEndpoint.TryValidate("192.0.2.45", 0, out _, out _), "invalid port accepted");
Console.WriteLine("PASS manual IPv4 and TCP port validation");

using CancellationTokenSource source = new(TimeSpan.FromSeconds(30));
int goodPort = StartServer(FirmwareModeProtocol.WhoIdentity, true, source.Token);
(TcpBridgeTransport transport, Nice64TcpHandshake handshake) = await TcpBridgeTransport.ConnectVerifiedAsync(
    IPAddress.Loopback, goodPort, TimeSpan.FromSeconds(2), source.Token);
Require(handshake.Mode.Personality == FirmwarePersonality.Network, "MODE? handshake parse failed");
Require(handshake.Capabilities.StartsWith("CAPS|"), "CAPS? handshake parse failed");
transport.Dispose();
Console.WriteLine("PASS TCP connect and WHO/MODE?/CAPS? verification with fragmented reply");

int busyPort = StartServer("NICE64|BUSY", false, source.Token);
try
{
    await TcpBridgeTransport.ConnectVerifiedAsync(IPAddress.Loopback, busyPort, TimeSpan.FromSeconds(2), source.Token);
    throw new InvalidOperationException("BUSY was accepted");
}
catch (IOException ex) when (ex.Message.Contains("active TCP Bridge owner", StringComparison.Ordinal))
{
    Console.WriteLine("PASS deterministic BUSY handling");
}

int invalidPort = StartServer("HTTP/1.1 200 OK", false, source.Token);
try
{
    await TcpBridgeTransport.ConnectVerifiedAsync(IPAddress.Loopback, invalidPort, TimeSpan.FromSeconds(2), source.Token);
    throw new InvalidOperationException("invalid identity was accepted");
}
catch (IOException ex) when (ex.Message.Contains("expected NICE64 identity", StringComparison.Ordinal))
{
    Console.WriteLine("PASS invalid endpoint identity rejection");
}

TcpListener unused = new(IPAddress.Loopback, 0);
unused.Start();
int refusedPort = ((IPEndPoint)unused.LocalEndpoint).Port;
unused.Stop();
try
{
    await TcpBridgeTransport.ConnectVerifiedAsync(IPAddress.Loopback, refusedPort, TimeSpan.FromSeconds(1), source.Token);
    throw new InvalidOperationException("refused connection was accepted");
}
catch (SocketException)
{
    Console.WriteLine("PASS connection-refused handling");
}
catch (OperationCanceledException)
{
    Console.WriteLine("PASS bounded TCP connection-timeout handling");
}

Require(typeof(SerialBridgeTransport).GetConstructors().Single().GetParameters()[0].ParameterType == typeof(System.IO.Ports.SerialPort),
        "serial adapter no longer wraps the authoritative SerialPort");
Require(typeof(IBridgeLineTransport).IsAssignableFrom(typeof(TcpBridgeTransport)), "TCP transport abstraction missing");
Console.WriteLine("PASS narrow transport abstraction and serial adapter preservation");
Console.WriteLine("PASS TcpTransportSmoke");
