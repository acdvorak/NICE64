using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using PicoSwitchController;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static HttpRequestMessage Authorized(
    HttpMethod method, string path, string token)
{
    HttpRequestMessage request = new(method, path);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return request;
}

static async Task<string> ReadAsync(
    HttpClient client, HttpMethod method, string path, string token,
    HttpStatusCode expected = HttpStatusCode.OK)
{
    using HttpRequestMessage request = Authorized(method, path, token);
    using HttpResponseMessage response = await client.SendAsync(request);
    string body = await response.Content.ReadAsStringAsync();
    Require(response.StatusCode == expected,
        $"{method} {path} returned {(int)response.StatusCode}, expected {(int)expected}: {body}");
    return body;
}

static int ReserveAvailablePort()
{
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

const string token = "test-token-with-at-least-thirty-two-characters-12345";
const string rawSecret =
    "C:\\Users\\private\\secret COM91 192.168.77.9 SecretImplementationException";
DateTimeOffset captured = new(2026, 8, 27, 20, 0, 0, TimeSpan.Zero);
BridgeCapabilitySet emptyReported =
    BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.DeviceReported);
BridgeCapabilitySet emptyImplemented =
    BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.BridgeImplemented);
BridgeCapabilitySet emptyNegotiated =
    BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.ApplicationNegotiated);
BridgeCapabilitySet effective = new(BridgeCapabilityProvenance.Effective,
[
    new(BridgeCapabilityId.BulkRead, true,
        BridgeCapabilityProvenance.Effective, 8192, 4096)
]);
BridgeDeviceStateSnapshot device = new(
    "NICE64", "1.6.0", BridgeDeviceRole.Standalone, BridgeDeviceService.Network,
    FirmwarePersonality.Network, BridgeControlConnectionKind.WifiTcp,
    BridgeConnectionState.Connected, BridgeNetworkPakTransport.Tcp,
    BridgeNetworkPakTransport.ReliableUdpQuick, emptyReported, emptyImplemented,
    emptyNegotiated, effective, 9, captured)
{
    FirmwareTransition = new(BridgeFirmwareTransitionPhase.Completed,
        FirmwarePersonality.Network, rawSecret),
    ActiveAccessory = BridgeAccessoryMode.NetworkPak,
    AccessoryTransition = new(BridgeAccessoryTransitionPhase.Completed,
        BridgeAccessoryMode.NetworkPak, rawSecret)
};
BridgeOperationProgressEnvelope progress = new(
    4, BridgeOperationKind.AccessorySelection, "Reconnecting", 2, 3, 1, 1,
    "bytes", "unsafe message " + rawSecret,
    new { Password = "domain-progress-secret" }, captured);
BridgeOperationSnapshot operation = new(
    4, BridgeOperationKind.AccessorySelection, BridgeOperationStatus.Failed,
    captured.AddSeconds(-4), captured, progress,
    BridgeOperationCancellationCapability.None, false, "Accessory",
    "UnexpectedFailure", rawSecret,
    new { Implementation = "SecretDomainResultImplementation" });
BridgeStateSnapshot snapshot = new(
    12, captured, device,
    new(new(ConsoleControllerType.N64, 3, 10, -11, true),
        new(ConsoleControllerType.N64, 1, 10, -11, true), false, false),
    new("C:\\Users\\private\\active.mpk", true, true, 32768,
        BridgeControllerPakContentOperation.Load, rawSecret),
    new("D:\\Private\\game.gbc", "D:\\Private\\game.sav", true, true, true,
        BridgeTransferPakInstallKind.FullFlash, 1048576, 32768,
        new("SAFE GAME", 0x10, "MBC3", "MBC3", 1048576, 32768, true),
        BridgeTransferPakContentOperation.InstallFullRom, rawSecret),
    new(AccessoryPersistenceKind.GameBoySave, "D:\\Private\\game.sav", 32768,
        true, true, false, 5, captured, rawSecret),
    new("network-portal", "Network Portal",
        [new("network-portal", "Network Portal")],
        BridgeNetworkPakModulePersistenceState.Confirmed,
        BridgeNetworkPakModuleSelectionOutcome.RequestedModuleSelected, rawSecret),
    new(true, true, true, "CONNECTED", "DHCP", "PrivateSsid", "UP",
        "192.168.77.44", "255.255.255.0", "192.168.77.1", "192.168.77.2",
        "WIFI_CONFIG|PASSWORD_B64|c2VjcmV0",
        BridgeWifiProvisioningOperation.GetStatus,
        BridgeWifiProvisioningOutcome.Succeeded, rawSecret),
    operation)
{
    Bridge = new("6ca247df-d38a-4e98-936e-c1cd907d4302", "Living Room")
};

CountingStateProvider state = new(snapshot);
BridgeApiHostOptions inMemoryOptions = new() { Enabled = true, Port = 6470 };
await using WebApplication application = BridgeApiHost.CreateApplication(
    state, token, inMemoryOptions,
    configureBuilder: builder => builder.WebHost.UseTestServer());
await application.StartAsync();
using HttpClient client = application.GetTestClient();

using (HttpResponseMessage missing = await client.GetAsync("/api/v1/state"))
    Require(missing.StatusCode == HttpStatusCode.Unauthorized,
        "Missing bearer token was not rejected");
using (HttpResponseMessage wrong = await client.SendAsync(
    Authorized(HttpMethod.Get, "/api/v1/state", "wrong-token-with-enough-length-123456789")))
    Require(wrong.StatusCode == HttpStatusCode.Unauthorized,
        "Incorrect bearer token was not rejected");
using (HttpRequestMessage malformed = new(HttpMethod.Get, "/api/v1/health"))
{
    malformed.Headers.Authorization = new AuthenticationHeaderValue("Basic", "abc");
    using HttpResponseMessage response = await client.SendAsync(malformed);
    Require(response.StatusCode == HttpStatusCode.Unauthorized,
        "Malformed authorization scheme was not rejected");
}

string stateJson = await ReadAsync(client, HttpMethod.Get, "/api/v1/state", token);
using JsonDocument stateDocument = JsonDocument.Parse(stateJson);
JsonElement root = stateDocument.RootElement;
Require(root.GetProperty("apiVersion").GetString() == "v1" &&
    root.GetProperty("captureSequence").GetInt64() == 12 &&
    root.GetProperty("capturedUtc").GetDateTimeOffset() == captured &&
    root.GetProperty("bridge").GetProperty("instanceId").GetString() ==
        "6ca247df-d38a-4e98-936e-c1cd907d4302" &&
    root.GetProperty("bridge").GetProperty("name").GetString() == "Living Room" &&
    root.GetProperty("device").GetProperty("selectedNetworkPakTransport").GetString() == "tcp" &&
    root.GetProperty("device").GetProperty("activeNetworkPakTransport").GetString() ==
        "reliable-udp-quick" &&
    root.GetProperty("wifi").GetProperty("deviceStatusKnown").GetBoolean() &&
    root.GetProperty("wifi").GetProperty("connected").GetBoolean() &&
    root.GetProperty("operation").GetProperty("failureCode").GetString() ==
        "operation-failed",
    "Serialized v1 state did not use the frozen camelCase safe projection");

string operationJson = await ReadAsync(
    client, HttpMethod.Get, "/api/v1/operation", token);
using JsonDocument operationDocument = JsonDocument.Parse(operationJson);
Require(operationDocument.RootElement.GetProperty("operationId").GetInt64() == 4 &&
    operationDocument.RootElement.GetProperty("kind").GetString() ==
        "accessory-selection" &&
    operationDocument.RootElement.GetProperty("progress").GetProperty("phase")
        .GetString() == "reconnecting",
    "Operation endpoint did not reuse the F10a safe operation projection");

int readsBeforeHealth = state.ReadCount;
string healthJson = await ReadAsync(client, HttpMethod.Get, "/api/v1/health", token);
using JsonDocument healthDocument = JsonDocument.Parse(healthJson);
Require(healthDocument.RootElement.GetProperty("apiVersion").GetString() == "v1" &&
    healthDocument.RootElement.GetProperty("status").GetString() == "alive" &&
    state.ReadCount == readsBeforeHealth,
    "Health implied or queried device health instead of host liveness");

string combinedJson = stateJson + operationJson + healthJson;
string[] forbidden =
[
    token, "C:\\", "D:\\", "PrivateSsid", "192.168.77", "255.255.255.0",
    "WIFI_CONFIG", "PASSWORD_B64", rawSecret, "SecretImplementationException",
    "SecretDomainResultImplementation", "domain-progress-secret", "unsafe message",
    "DomainResult", "DomainProgress", "Ssid", "IpAddress", "Netmask", "Gateway",
    "Dns", "Password"
];
foreach (string value in forbidden)
    Require(!combinedJson.Contains(value, StringComparison.OrdinalIgnoreCase),
        $"Serialized API response leaked sensitive/internal value: {value}");
Require(stateJson.Contains("active.mpk", StringComparison.Ordinal) &&
    stateJson.Contains("game.gbc", StringComparison.Ordinal) &&
    stateJson.Contains("game.sav", StringComparison.Ordinal),
    "Filename-only associations were not retained in serialized state");

using (HttpRequestMessage corsRequest = Authorized(
    HttpMethod.Get, "/api/v1/state", token))
{
    corsRequest.Headers.Add("Origin", "https://unapproved.example");
    using HttpResponseMessage corsResponse = await client.SendAsync(corsRequest);
    Require(corsResponse.StatusCode == HttpStatusCode.OK &&
        !corsResponse.Headers.Contains("Access-Control-Allow-Origin"),
        "CORS was enabled for an unapproved origin");
}

string[] mutationPaths =
[
    "/api/v1/state", "/api/v1/firmware", "/api/v1/accessory",
    "/api/v1/module", "/api/v1/connect", "/api/v1/disconnect",
    "/api/v1/wifi", "/api/v1/carrier"
];
foreach (string path in mutationPaths)
{
    using HttpRequestMessage request = Authorized(HttpMethod.Post, path, token);
    using HttpResponseMessage response = await client.SendAsync(request);
    Require(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
        $"Mutation route unexpectedly exists: POST {path}");
}

state.ThrowOnRead = true;
string failureJson = await ReadAsync(client, HttpMethod.Get, "/api/v1/state", token,
    HttpStatusCode.InternalServerError);
Require(failureJson.Contains("internal-error", StringComparison.Ordinal) &&
    !failureJson.Contains(rawSecret, StringComparison.Ordinal) &&
    !failureJson.Contains("InvalidOperationException", StringComparison.Ordinal),
    "Request exception leaked diagnostic details");
state.ThrowOnRead = false;
await application.StopAsync();

string tokenDirectory = Path.Combine(Path.GetTempPath(),
    "nice64-api-token-smoke-" + Guid.NewGuid().ToString("N"));
try
{
    string tokenPath = Path.Combine(tokenDirectory, "token.dat");
    BridgeApiProtectedTokenStore firstStore = new(tokenPath);
    string firstToken = firstStore.GetToken();
    string repeatedToken = firstStore.GetToken();
    string restartedToken = new BridgeApiProtectedTokenStore(tokenPath).GetToken();
    byte[] protectedFile = File.ReadAllBytes(tokenPath);
    Require(firstToken.Length >= 32 && firstToken == repeatedToken &&
        firstToken == restartedToken &&
        !Encoding.UTF8.GetString(protectedFile).Contains(firstToken,
            StringComparison.Ordinal),
        "Per-user protected token was not stable or was persisted in plaintext");
    string rotatedToken = firstStore.RotateToken();
    string reloadedRotatedToken = new BridgeApiProtectedTokenStore(tokenPath).GetToken();
    Require(rotatedToken != firstToken && rotatedToken == reloadedRotatedToken,
        "Token rotation did not persist a new protected token");
}
finally
{
    if (Directory.Exists(tokenDirectory)) Directory.Delete(tokenDirectory, recursive: true);
}

int realPort = ReserveAvailablePort();
CountingStateProvider realState = new(snapshot);
List<string> lifecycleLog = [];
await using (BridgeApiHost host = new(realState, new FixedTokenProvider(token),
    new() { Enabled = true, Port = realPort }, lifecycleLog.Add))
{
    BridgeApiHostStartResult started = await host.StartAsync();
    Require(started.Success && started.Running && host.IsRunning,
        "Real loopback host did not start");
    BridgeApiHostStartResult repeated = await host.StartAsync();
    Require(repeated.Disposition == BridgeApiHostStartDisposition.AlreadyRunning,
        "Repeated host start was not idempotent");

    using HttpClient realClient = new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{realPort}")
    };
    string realHealth = await ReadAsync(
        realClient, HttpMethod.Get, "/api/v1/health", token);
    Require(realHealth.Contains("\"status\":\"alive\"", StringComparison.Ordinal),
        "Real loopback host did not serve an authorized request");
    await host.StopAsync();
    Require(!host.IsRunning && !realState.Disposed,
        "Host stop disposed or claimed ownership of semantic state");
    await host.StopAsync();
}
Require(lifecycleLog.All(line => !line.Contains(token, StringComparison.Ordinal)),
    "Bearer token appeared in lifecycle logging");

int conflictPort = ReserveAvailablePort();
using TcpListener conflict = new(IPAddress.Loopback, conflictPort);
conflict.Start();
CountingStateProvider conflictState = new(snapshot);
List<string> conflictLog = [];
await using (BridgeApiHost failedHost = new(
    conflictState, new FixedTokenProvider(token),
    new() { Enabled = true, Port = conflictPort }, conflictLog.Add))
{
    BridgeApiHostStartResult failedStart = await failedHost.StartAsync();
    Require(failedStart.Disposition == BridgeApiHostStartDisposition.Failed &&
        !failedStart.Running && !failedHost.IsRunning && !conflictState.Disposed,
        "Port conflict was not isolated as an optional-host failure");
    int readsBefore = conflictState.ReadCount;
    Require(conflictState.GetSnapshot() == snapshot &&
        conflictState.ReadCount == readsBefore + 1,
        "Desktop-equivalent semantic state could not continue after host failure");
    await failedHost.StopAsync();
}
Require(conflictLog.Count > 0 &&
    conflictLog.All(line => !line.Contains(token, StringComparison.Ordinal)),
    "Controlled host-start failure was not loggable or leaked the token");

await using (BridgeApiHost disabledHost = new(
    new CountingStateProvider(snapshot), new FixedTokenProvider(token),
    new() { Enabled = false, Port = BridgeApiHostOptions.DefaultPort }))
{
    BridgeApiHostStartResult disabled = await disabledHost.StartAsync();
    Require(disabled.Disposition == BridgeApiHostStartDisposition.Disabled &&
        !disabledHost.IsRunning,
        "Disabled optional host attempted to run");
    await disabledHost.StopAsync();
}

Require(BridgeApiHostOptions.DefaultPort != 6464 &&
    BridgeApiHostOptions.DefaultPort != 6465,
    "Local API default port collides with a NICE64 device/control port");

Console.WriteLine("PASS Bridge API host authentication, wire redaction, and lifecycle semantics");

sealed class FixedTokenProvider(string token) : IBridgeApiTokenProvider
{
    public string GetToken() => token;
}

sealed class CountingStateProvider(BridgeStateSnapshot snapshot) : IBridgeStateProvider
{
    public int ReadCount { get; private set; }
    public bool ThrowOnRead { get; set; }
    public bool Disposed { get; private set; }

    public BridgeStateSnapshot GetSnapshot()
    {
        ReadCount++;
        if (ThrowOnRead)
            throw new InvalidOperationException(
                "C:\\private\\state 192.168.77.9 SecretImplementationException");
        return snapshot;
    }
}

namespace PicoSwitchController
{
    public sealed record BridgeBulkProgress(
        string Phase, int CompletedBytes, int TotalBytes, int ItemIndex, int ItemCount);

    public sealed record GameBoyTransferProfile(
        byte CartType, int RomSize, int RamSize, byte FirmwareFlags);
}
