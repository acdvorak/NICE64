using PicoSwitchController;
using System.Security.Cryptography;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string Mutate(byte[] wire, int offset, byte value)
{
    wire[offset] = value;
    return Convert.ToHexString(wire);
}

static void PrintRtt(string label, int sent, IReadOnlyList<double> samples, long duplicates)
{
    double[] ordered = samples.Order().ToArray();
    if (ordered.Length == 0)
    {
        Console.WriteLine($"{label}: sent={sent} received=0 lost={sent} duplicates={duplicates}");
        return;
    }
    double Percentile(double percentile) =>
        ordered[(int)Math.Ceiling((ordered.Length - 1) * percentile)];
    Console.WriteLine(
        $"{label}: sent={sent} received={ordered.Length} lost={sent - ordered.Length} " +
        $"duplicates={duplicates} " +
        $"min={ordered[0]:F3}ms mean={ordered.Average():F3}ms " +
        $"median={Percentile(0.50):F3}ms p95={Percentile(0.95):F3}ms " +
        $"p99={Percentile(0.99):F3}ms max={ordered[^1]:F3}ms");
}

static async Task<List<double>> RunPings(
    Nice64RudpbDiagnosticClient client, int count, TimeSpan timeout)
{
    List<double> results = new(count);
    for (uint index = 0; index < count; index++)
    {
        try
        {
            results.Add((await client.PingAsync(index, timeout)).TotalMilliseconds);
        }
        catch (TimeoutException)
        {
            // A diagnostic loss is recorded and the characterization continues.
        }
    }
    return results;
}

string? target = null;
int port = Nice64RudpbDiagnosticClient.DefaultPort;
bool runLong = false;
for (int index = 0; index < args.Length; index++)
{
    if (args[index] == "--target" && index + 1 < args.Length) target = args[++index];
    else if (args[index] == "--port" && index + 1 < args.Length) port = int.Parse(args[++index]);
    else if (args[index] == "--long") runLong = true;
    else throw new ArgumentException($"Unknown/incomplete argument: {args[index]}");
}

if (target is null)
{
    Console.WriteLine("RUDP-B hardware diagnostic built successfully.");
    Console.WriteLine("Run with: --target <manual-pico-ip> [--port 6465] [--long]");
    return;
}

TimeSpan timeout = TimeSpan.FromSeconds(2);
await using Nice64RudpbDiagnosticClient owner = new(target, port);
Console.WriteLine($"Target={target}:{port} session=0x{owner.SessionId:X8}");
Require(await owner.EstablishAsync(timeout) == Nice64RudpbHelloResult.Accepted,
        "Owner HELLO was not accepted.");
Console.WriteLine("HELLO/HELLO_ACK: PASS");

await using (Nice64RudpbDiagnosticClient contender = new(target, port))
{
    Console.WriteLine($"Contender session=0x{contender.SessionId:X8}");
    Require(await contender.EstablishAsync(timeout) == Nice64RudpbHelloResult.Busy,
            "Second owner did not receive BUSY.");
}
Console.WriteLine("single-owner BUSY: PASS");

long ignoredBefore = owner.IgnoredResponses;
List<double> ping100 = await RunPings(owner, 100, timeout);
PrintRtt("100-ping RTT", 100, ping100, owner.IgnoredResponses - ignoredBefore);
if (runLong)
{
    ignoredBefore = owner.IgnoredResponses;
    List<double> ping1000 = await RunPings(owner, 1000, timeout);
    PrintRtt("1000-ping RTT", 1000, ping1000,
             owner.IgnoredResponses - ignoredBefore);
}

foreach (int length in new[] { 0, 32, 128, 256, 512 })
{
    byte[] payload = new byte[length];
    RandomNumberGenerator.Fill(payload);
    await owner.EchoAsync(payload, timeout);
    Console.WriteLine($"payload echo {length} bytes: PASS");
}

Require(await owner.ProbeDeliveryClassAsync(1, timeout) ==
        Nice64RudpbDiagnosticClient.UnsupportedClass,
        "LATEST_STATE did not return UNSUPPORTED.");
Require(await owner.ProbeDeliveryClassAsync(2, timeout) ==
        Nice64RudpbDiagnosticClient.InvalidClass,
        "Class 2 did not return INVALID.");
Console.WriteLine("class 1 UNSUPPORTED / class 2 INVALID: PASS");

Nice64RudpbFirmwareDiagnostics beforeMalformed =
    await owner.GetDiagnosticsAsync(timeout);
await owner.SendMalformedAsync(wire => { Mutate(wire, 0, 0); return wire; });
await Task.Delay(25);
await owner.SendMalformedAsync(wire => { Mutate(wire, 4, 2); return wire; });
await Task.Delay(25);
await owner.SendMalformedAsync(wire => { Mutate(wire, 7, 27); return wire; });
await Task.Delay(25);
await owner.SendMalformedAsync(wire => { Mutate(wire, 5, 0x81); return wire; });
await Task.Delay(25);
await owner.SendMalformedAsync(wire => { wire[24] = 0x02; wire[25] = 0x02; return wire; });
await Task.Delay(25);
await owner.SendMalformedAsync(wire => wire[..^1]);
await Task.Delay(25);
await owner.SendMalformedAsync(wire => { Mutate(wire, 27, 1); return wire; });
await Task.Delay(100);
Nice64RudpbFirmwareDiagnostics afterMalformed =
    await owner.GetDiagnosticsAsync(timeout);
Require(afterMalformed["invalid_headers"] >= beforeMalformed["invalid_headers"] + 7,
        "Malformed datagrams did not increment invalid-header diagnostics.");
Console.WriteLine("real malformed-datagram rejection counters: PASS");

foreach (int count in new[] { 8, 16, 32 })
{
    (int sent, int received) = await owner.BurstPingAsync(count, TimeSpan.FromSeconds(2));
    Console.WriteLine($"burst {count}: sent={sent} received={received} dropped={sent - received}");
}

Nice64RudpbFirmwareDiagnostics firmware = await owner.GetDiagnosticsAsync(timeout);
Console.WriteLine("Firmware diagnostics:");
foreach ((string key, uint value) in firmware.Values)
    Console.WriteLine($"  {key}={value}");
Require(firmware["codec_self_test_pass"] == 1, "Firmware codec self-test failed.");

await owner.ReleaseAsync(timeout);
Console.WriteLine("GOODBYE/RELEASED: PASS");

await using Nice64RudpbDiagnosticClient nextOwner = new(target, port);
Require(await nextOwner.EstablishAsync(timeout) == Nice64RudpbHelloResult.Accepted,
        "New owner HELLO was not accepted after release.");
bool staleRejected = false;
try
{
    await owner.PingAsync(0xFFFFFFFF, TimeSpan.FromMilliseconds(300));
}
catch (TimeoutException)
{
    staleRejected = true;
}
Require(staleRejected, "Released session received a PONG after replacement.");
Nice64RudpbFirmwareDiagnostics afterStale = await nextOwner.GetDiagnosticsAsync(timeout);
Require(afterStale["session_mismatch"] > firmware["session_mismatch"],
        "Stale session did not increment session-mismatch diagnostics.");
await nextOwner.ReleaseAsync(timeout);
Console.WriteLine("release/reconnect/stale-session rejection: PASS");
Console.WriteLine("RUDP-B automated LAN diagnostic: PASS");
