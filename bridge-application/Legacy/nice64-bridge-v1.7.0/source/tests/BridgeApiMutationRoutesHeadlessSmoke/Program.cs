using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using PicoSwitchController;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static HttpRequestMessage Authorized(
    HttpMethod method, string path, string token, HttpContent? content = null)
{
    HttpRequestMessage request = new(method, path) { Content = content };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return request;
}

static async Task<(HttpStatusCode Status, string Body)> SendAsync(
    HttpClient client, HttpMethod method, string path, string token,
    object? body = null, HttpContent? content = null)
{
    content ??= body is null ? null : JsonContent.Create(body);
    using HttpRequestMessage request = Authorized(method, path, token, content);
    using HttpResponseMessage response = await client.SendAsync(request);
    return (response.StatusCode, await response.Content.ReadAsStringAsync());
}

static async Task<BridgeOperationSnapshot> WaitForTerminalAsync(
    BridgeOperationCoordinator operations, long operationId)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline)
    {
        BridgeOperationSnapshot current = operations.Current;
        if (current.OperationId == operationId &&
            current.Status != BridgeOperationStatus.Running)
            return current;
        await Task.Delay(5);
    }
    throw new TimeoutException($"Operation {operationId} did not become terminal.");
}

static long OperationId(string json) =>
    JsonDocument.Parse(json).RootElement.GetProperty("operationId").GetInt64();

const string token = "route-test-token-with-at-least-thirty-two-characters";
const string rawSecret =
    "C:\\private\\implementation COM77 192.168.9.4 SecretDomainResult";
BridgeOperationCoordinator operations = new();
FakeFirmwareService firmware = new();
FakeAccessoryService accessory = new();
FakeModuleService modules = new();
FakeCarrierService carrier = new();
FakeConnectionService connection = new();
BridgeApiCommandFacade commands = new(
    operations, firmware, accessory, modules, carrier, connection);
DynamicStateProvider state = new(operations, carrier);

await using WebApplication application = BridgeApiHost.CreateApplication(
    state, token, new() { Enabled = true, Port = 6470 },
    configureBuilder: builder => builder.WebHost.UseTestServer(),
    commands: commands);
await application.StartAsync();
using HttpClient client = application.GetTestClient();

using (HttpResponseMessage response = await client.PostAsJsonAsync(
    "/api/v1/firmware", new { personality = "network" }))
    Require(response.StatusCode == HttpStatusCode.Unauthorized,
        "Missing bearer token was not rejected");
using (HttpRequestMessage wrong = Authorized(HttpMethod.Post, "/api/v1/accessory",
    "wrong-token-with-at-least-thirty-two-characters",
    JsonContent.Create(new { accessory = "controller-pak" })))
using (HttpResponseMessage response = await client.SendAsync(wrong))
    Require(response.StatusCode == HttpStatusCode.Unauthorized,
        "Incorrect bearer token was not rejected");
using (HttpResponseMessage response = await client.PostAsJsonAsync(
    "/api/v1/network-pak/carrier", new { carrier = "tcp" }))
    Require(response.StatusCode == HttpStatusCode.Unauthorized,
        "Carrier route did not retain mandatory bearer authentication");

TaskCompletionSource<FirmwareModeTransitionResult> firmwareGate =
    new(TaskCreationOptions.RunContinuationsAsynchronously);
firmware.Handler = (_, _) => firmwareGate.Task;
Task<(HttpStatusCode Status, string Body)> admittedTask = SendAsync(
    client, HttpMethod.Post, "/api/v1/firmware", token,
    new { personality = "network" });
(HttpStatusCode Status, string Body) admitted =
    await admittedTask.WaitAsync(TimeSpan.FromSeconds(1));
Require(admitted.Status == HttpStatusCode.Accepted &&
    !firmwareGate.Task.IsCompleted &&
    admitted.Body.Contains("\"accepted\":true", StringComparison.Ordinal) &&
    admitted.Body.Contains("\"operationKind\":\"firmware-mode-switch\"",
        StringComparison.Ordinal),
    "Firmware admission waited for completion or returned the wrong safe DTO");
long firmwareOperationId = OperationId(admitted.Body);

(HttpStatusCode Status, string Body) busy = await SendAsync(
    client, HttpMethod.Post, "/api/v1/accessory", token,
    new { accessory = "controller-pak" });
using (JsonDocument document = JsonDocument.Parse(busy.Body))
{
    JsonElement root = document.RootElement;
    Require(busy.Status == HttpStatusCode.Conflict &&
        root.GetProperty("code").GetString() == "operation-busy" &&
        root.GetProperty("currentOperationId").GetInt64() == firmwareOperationId &&
        root.GetProperty("currentOperationKind").GetString() == "firmware-mode-switch" &&
        accessory.CallCount == 0,
        "Busy did not report the current F8 owner without invoking the domain");
}

(HttpStatusCode Status, string Body) running = await SendAsync(
    client, HttpMethod.Get, "/api/v1/operation", token);
using (JsonDocument document = JsonDocument.Parse(running.Body))
    Require(running.Status == HttpStatusCode.OK &&
        document.RootElement.GetProperty("operationId").GetInt64() == firmwareOperationId &&
        document.RootElement.GetProperty("status").GetString() == "running",
        "GET operation did not expose the admitted operation ID");

firmwareGate.SetResult(TestResults.FirmwareSuccess(FirmwarePersonality.Network));
BridgeOperationSnapshot firmwareTerminal =
    await WaitForTerminalAsync(operations, firmwareOperationId);
Require(firmwareTerminal.Status == BridgeOperationStatus.Succeeded,
    "Accepted firmware operation did not later reach terminal state");
(HttpStatusCode Status, string Body) completed = await SendAsync(
    client, HttpMethod.Get, "/api/v1/operation", token);
using (JsonDocument document = JsonDocument.Parse(completed.Body))
    Require(document.RootElement.GetProperty("operationId").GetInt64() ==
            firmwareOperationId &&
        document.RootElement.GetProperty("status").GetString() == "succeeded",
        "GET operation did not retain the same terminal operation ID");

(HttpStatusCode Status, string Body) accessoryAccepted = await SendAsync(
    client, HttpMethod.Post, "/api/v1/accessory", token,
    new { accessory = "transfer-pak" });
Require(accessoryAccepted.Status == HttpStatusCode.Accepted &&
    accessoryAccepted.Body.Contains("accessory-selection", StringComparison.Ordinal),
    "Authenticated accessory symbol was not accepted");
await WaitForTerminalAsync(operations, OperationId(accessoryAccepted.Body));
Require(accessory.LastAccessory == BridgeAccessoryMode.TransferPak,
    "Accessory symbol did not reach the frozen facade");

(HttpStatusCode Status, string Body) moduleAccepted = await SendAsync(
    client, HttpMethod.Post, "/api/v1/network-pak/module", token,
    new { moduleId = " diagnostics " });
Require(moduleAccepted.Status == HttpStatusCode.Accepted &&
    moduleAccepted.Body.Contains("network-pak-module-selection", StringComparison.Ordinal),
    "Authenticated module ID was not accepted");
await WaitForTerminalAsync(operations, OperationId(moduleAccepted.Body));
Require(modules.LastModuleId == "diagnostics",
    "Module ID did not flow through frozen facade normalization");

carrier.Handler = (target, _) => Task.FromResult(TestResults.CarrierResult(
    true, target, BridgeNetworkPakTransport.None,
    BridgeNetworkPakCarrierOutcome.ActivationPendingHandshake));
(HttpStatusCode Status, string Body) tcpAccepted = await SendAsync(
    client, HttpMethod.Post, "/api/v1/network-pak/carrier", token,
    new { carrier = " tcp " });
Require(tcpAccepted.Status == HttpStatusCode.Accepted &&
    tcpAccepted.Body.Contains("network-pak-carrier-selection", StringComparison.Ordinal),
    "Authenticated TCP carrier was not accepted immediately");
long tcpOperationId = OperationId(tcpAccepted.Body);
BridgeOperationSnapshot tcpTerminal = await WaitForTerminalAsync(operations, tcpOperationId);
Require(tcpTerminal.Status == BridgeOperationStatus.Succeeded &&
    tcpTerminal.CancellationCapability == BridgeOperationCancellationCapability.None &&
    tcpTerminal.DomainResult is BridgeNetworkPakCarrierResult
    {
        Outcome: BridgeNetworkPakCarrierOutcome.ActivationPendingHandshake
    },
    "TCP pending-handshake semantic result did not complete F8 successfully");
(HttpStatusCode Status, string Body) tcpState = await SendAsync(
    client, HttpMethod.Get, "/api/v1/state", token);
using (JsonDocument document = JsonDocument.Parse(tcpState.Body))
{
    JsonElement device = document.RootElement.GetProperty("device");
    Require(device.GetProperty("selectedNetworkPakTransport").GetString() == "tcp" &&
        device.GetProperty("activeNetworkPakTransport").GetString() == "none",
        "Selected TCP / active None was not observable after pending handshake");
}

carrier.Handler = (target, _) => Task.FromResult(TestResults.CarrierResult(
    true, target, target, BridgeNetworkPakCarrierOutcome.Active));
(HttpStatusCode Status, string Body) quickAccepted = await SendAsync(
    client, HttpMethod.Post, "/api/v1/network-pak/carrier", token,
    new { carrier = "reliable-udp-quick" });
Require(quickAccepted.Status == HttpStatusCode.Accepted,
    "Authenticated Reliable UDP Quick carrier was not accepted");
await WaitForTerminalAsync(operations, OperationId(quickAccepted.Body));
(HttpStatusCode Status, string Body) quickState = await SendAsync(
    client, HttpMethod.Get, "/api/v1/state", token);
using (JsonDocument document = JsonDocument.Parse(quickState.Body))
{
    JsonElement device = document.RootElement.GetProperty("device");
    Require(device.GetProperty("selectedNetworkPakTransport").GetString() ==
            "reliable-udp-quick" &&
        device.GetProperty("activeNetworkPakTransport").GetString() ==
            "reliable-udp-quick",
        "Quick success was not projected through existing selected/active fields");
}

var invalidCases = new (string Path, object Body, string Code)[]
{
    ("/api/v1/firmware", new { personality = "Controller" }, "invalid-firmware"),
    ("/api/v1/accessory", new { accessory = "memory-pak" }, "invalid-accessory"),
    ("/api/v1/network-pak/module", new { moduleId = "" }, "invalid-module"),
    ("/api/v1/network-pak/module", new { moduleId = new string('x', 257) },
        "invalid-module"),
    ("/api/v1/network-pak/carrier", new { carrier = "usb" }, "invalid-carrier"),
    ("/api/v1/network-pak/carrier", new { carrier = "none" }, "invalid-carrier"),
    ("/api/v1/network-pak/carrier", new { carrier = "quick" }, "invalid-carrier"),
    ("/api/v1/network-pak/carrier", new { carrier = "udp" }, "invalid-carrier"),
    ("/api/v1/network-pak/carrier", new { carrier = "rudp" }, "invalid-carrier"),
    ("/api/v1/network-pak/carrier", new { carrier = "ReliableUdpQuick" },
        "invalid-carrier")
};
foreach ((string path, object body, string code) in invalidCases)
{
    (HttpStatusCode Status, string Body) invalid =
        await SendAsync(client, HttpMethod.Post, path, token, body);
    Require(invalid.Status == HttpStatusCode.BadRequest &&
        invalid.Body.Contains($"\"code\":\"{code}\"", StringComparison.Ordinal),
        $"Invalid request did not return stable 400 code {code}");
}

(HttpStatusCode Status, string Body) missingProperty = await SendAsync(
    client, HttpMethod.Post, "/api/v1/firmware", token, new { });
Require(missingProperty.Status == HttpStatusCode.BadRequest &&
    missingProperty.Body.Contains("invalid-firmware", StringComparison.Ordinal),
    "Missing symbolic value did not return safe 400");
(HttpStatusCode Status, string Body) malformed = await SendAsync(
    client, HttpMethod.Post, "/api/v1/network-pak/carrier", token, content:
        new StringContent("{not-json", Encoding.UTF8, "application/json"));
Require(malformed.Status == HttpStatusCode.BadRequest,
    "Malformed JSON did not return 400");
Require(malformed.Body.Contains("\"code\":\"invalid-request\"", StringComparison.Ordinal),
    "Malformed JSON did not return an API-owned safe error");
(HttpStatusCode Status, string Body) wrongContentType = await SendAsync(
    client, HttpMethod.Post, "/api/v1/network-pak/carrier", token, content:
        new StringContent("network", Encoding.UTF8, "text/plain"));
Require(wrongContentType.Status == HttpStatusCode.UnsupportedMediaType,
    "Inappropriate content type was not rejected");
Require(wrongContentType.Body.Contains("unsupported-media-type", StringComparison.Ordinal),
    "Inappropriate content type did not return an API-owned safe error");

List<string> carrierFailureBodies = [];
foreach ((BridgeNetworkPakCarrierFailure failure, string safeCode) in new[]
{
    (BridgeNetworkPakCarrierFailure.CarrierUnavailable, "bridge-unavailable"),
    (BridgeNetworkPakCarrierFailure.PrerequisiteNotMet, "bridge-unavailable"),
    (BridgeNetworkPakCarrierFailure.PersistenceFailed, "persistence-failure"),
    (BridgeNetworkPakCarrierFailure.ActivationFailed, "operation-failed")
})
{
    carrier.Handler = (target, _) => Task.FromResult(TestResults.CarrierResult(
        false, target, BridgeNetworkPakTransport.None,
        BridgeNetworkPakCarrierOutcome.Failed, failure, rawSecret));
    (HttpStatusCode Status, string Body) failureAccepted = await SendAsync(
        client, HttpMethod.Post, "/api/v1/network-pak/carrier", token,
        new { carrier = "reliable-udp-quick" });
    Require(failureAccepted.Status == HttpStatusCode.Accepted,
        $"Carrier failure {failure} was not admitted asynchronously");
    long failureOperationId = OperationId(failureAccepted.Body);
    await WaitForTerminalAsync(operations, failureOperationId);
    (HttpStatusCode Status, string Body) failureOperation = await SendAsync(
        client, HttpMethod.Get, "/api/v1/operation", token);
    carrierFailureBodies.Add(failureOperation.Body);
    using JsonDocument document = JsonDocument.Parse(failureOperation.Body);
    JsonElement root = document.RootElement;
    Require(root.GetProperty("status").GetString() == "failed" &&
        root.GetProperty("failureCode").GetString() == safeCode &&
        !failureOperation.Body.Contains(rawSecret, StringComparison.OrdinalIgnoreCase),
        $"Carrier failure {failure} did not map to safe code {safeCode}");
}

string[] forbiddenPaths =
[
    "/api/v1/connect", "/api/v1/disconnect", "/api/v1/wifi",
    "/api/v1/wifi/connect",
    "/api/v1/controller", "/api/v1/controller-pak/content",
    "/api/v1/transfer-pak/content"
];
foreach (string path in forbiddenPaths)
{
    (HttpStatusCode Status, _) = await SendAsync(
        client, HttpMethod.Post, path, token, new { });
    Require(Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
        $"Excluded mutation route exists: POST {path}");
}

string allBodies = admitted.Body + busy.Body + running.Body + completed.Body +
    accessoryAccepted.Body + moduleAccepted.Body + tcpAccepted.Body + tcpState.Body +
    quickAccepted.Body + quickState.Body + missingProperty.Body + malformed.Body;
allBodies += string.Concat(carrierFailureBodies);
Require(!allBodies.Contains(rawSecret, StringComparison.OrdinalIgnoreCase) &&
    !allBodies.Contains("DomainResult", StringComparison.OrdinalIgnoreCase) &&
    !allBodies.Contains("FirmwareModeTransitionResult", StringComparison.OrdinalIgnoreCase) &&
    connection.ConnectCallCount == 0 && connection.DisconnectCallCount == 0,
    "Mutation API leaked internal data or exposed connection mutation");

await application.StopAsync();
Console.WriteLine("PASS selected authenticated mutation routes and operation lifetime semantics");

sealed class DynamicStateProvider(
    BridgeOperationCoordinator operations,
    FakeCarrierService carrier) : IBridgeStateProvider
{
    private long _sequence;

    public BridgeStateSnapshot GetSnapshot()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BridgeCapabilitySet reported =
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.DeviceReported);
        BridgeCapabilitySet implemented =
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.BridgeImplemented);
        BridgeCapabilitySet negotiated =
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.ApplicationNegotiated);
        BridgeCapabilitySet effective =
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.Effective);
        return new(Interlocked.Increment(ref _sequence), now,
            new("NICE64", "1.6.0", BridgeDeviceRole.Controller,
                BridgeDeviceService.None, FirmwarePersonality.Controller,
                BridgeControlConnectionKind.SerialUsb, BridgeConnectionState.Connected,
                carrier.Current.SelectedCarrier, carrier.Current.ActiveCarrier,
                reported, implemented, negotiated, effective, 1, now),
            new(new(ConsoleControllerType.N64, 0, 0, 0, false),
                new(ConsoleControllerType.N64, 0, 0, 0, false), false, false),
            new(string.Empty, false, false, 0, BridgeControllerPakContentOperation.None,
                string.Empty),
            new(string.Empty, string.Empty, false, false, false, BridgeTransferPakInstallKind.None,
                0, 0, null, BridgeTransferPakContentOperation.None, string.Empty),
            new(AccessoryPersistenceKind.None, string.Empty, 0, false, false,
                false, 0, null, string.Empty),
            new(string.Empty, string.Empty, [], BridgeNetworkPakModulePersistenceState.Unverified,
                BridgeNetworkPakModuleSelectionOutcome.None, null),
            BridgeWifiProvisioningSnapshot.Unknown,
            operations.Current);
    }
}

sealed class FakeFirmwareService : IBridgeFirmwareService
{
    public Func<FirmwarePersonality, CancellationToken,
        Task<FirmwareModeTransitionResult>> Handler { get; set; } =
        (target, _) => Task.FromResult(TestResults.FirmwareSuccess(target));

    public Task<FirmwareModeTransitionResult> SelectModeAsync(
        FirmwarePersonality target, CancellationToken cancellationToken = default) =>
        Handler(target, cancellationToken);
}

sealed class FakeAccessoryService : IBridgeAccessoryService
{
    public int CallCount { get; private set; }
    public BridgeAccessoryMode LastAccessory { get; private set; }

    public Task<BridgeAccessoryTransitionResult> SelectAccessoryAsync(
        BridgeAccessoryMode accessory, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastAccessory = accessory;
        return Task.FromResult(new BridgeAccessoryTransitionResult(
            true, accessory, accessory, null, true, false, string.Empty));
    }
}

sealed class FakeModuleService : IBridgeNetworkPakModuleService
{
    public string? LastModuleId { get; private set; }
    public BridgeNetworkPakModuleSnapshot Current { get; } = new(
        string.Empty, string.Empty, [], BridgeNetworkPakModulePersistenceState.Unverified,
        BridgeNetworkPakModuleSelectionOutcome.None, null);
    public IReadOnlyList<BridgeNetworkPakModuleInfo> ListModules() => [];

    public Task<BridgeNetworkPakModuleSelectionResult> SelectModuleAsync(
        string? moduleId, CancellationToken cancellationToken = default)
    {
        LastModuleId = moduleId;
        return Task.FromResult(TestResults.ModuleSuccess(moduleId!));
    }
}

sealed class FakeCarrierService : IBridgeNetworkPakCarrierService
{
    public Func<BridgeNetworkPakTransport, CancellationToken,
        Task<BridgeNetworkPakCarrierResult>> Handler { get; set; } =
        (target, _) => Task.FromResult(TestResults.CarrierResult(
            true, target, target, BridgeNetworkPakCarrierOutcome.Active));

    public BridgeNetworkPakCarrierSnapshot Current { get; private set; } =
        TestResults.CarrierSnapshot(BridgeNetworkPakTransport.Tcp,
            BridgeNetworkPakTransport.None);
    public long? PendingLineActivationGeneration => null;
    public long? ActiveQuickGeneration => null;

    public async Task<BridgeNetworkPakCarrierResult> SelectCarrierAsync(
        BridgeNetworkPakTransport carrier,
        CancellationToken cancellationToken = default)
    {
        BridgeNetworkPakCarrierResult result = await Handler(carrier, cancellationToken);
        Current = result.Snapshot;
        return result;
    }

    public Task<BridgeNetworkPakCarrierResult> InitializeAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> ActivateSelectedAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> ReleaseActiveAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> ObserveLineHandshakeAcknowledgedAsync(
        BridgeNetworkPakTransport carrier, long generation,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> ReportLineActivationFailureAsync(
        BridgeNetworkPakTransport carrier, long generation,
        BridgeNetworkPakCarrierFailure failure, string? diagnostic = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> ReportQuickFaultAsync(
        long generation, string? diagnostic = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> RetirePhysicalCarrierAsync(
        string? reason = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> ConvergeDisconnectedAsync(
        string? reason = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> ShutdownAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

sealed class FakeConnectionService : IBridgeConnectionService
{
    public int ConnectCallCount { get; private set; }
    public int DisconnectCallCount { get; private set; }

    public Task<BridgeConnectionResult> ConnectAsync(
        BridgeConnectionRequest request, CancellationToken cancellationToken = default)
    {
        ConnectCallCount++;
        throw new InvalidOperationException("Connection must not be exposed by HTTP.");
    }

    public Task<BridgeDisconnectResult> DisconnectAsync(
        BridgeDisconnectReason reason, CancellationToken cancellationToken = default)
    {
        DisconnectCallCount++;
        throw new InvalidOperationException("Disconnect must not be exposed by HTTP.");
    }
}

static class TestResults
{
    public static FirmwareModeTransitionResult FirmwareSuccess(
        FirmwarePersonality target) =>
        new(true, new(target, target == FirmwarePersonality.Controller
                ? "CONTROLLER" : "STANDALONE", target.ToString().ToUpperInvariant()),
            string.Empty, true, true)
        { Failure = FirmwareModeTransitionFailure.None };

    public static BridgeNetworkPakModuleSelectionResult ModuleSuccess(string moduleId)
    {
        BridgeNetworkPakModuleSnapshot snapshot = new(moduleId, "Selected Module",
            [new(moduleId, "Selected Module")],
            BridgeNetworkPakModulePersistenceState.Confirmed,
            BridgeNetworkPakModuleSelectionOutcome.RequestedModuleSelected, null);
        return new(true, moduleId, moduleId, "Selected Module", false, true,
            BridgeNetworkPakModuleSelectionFailure.None, null, snapshot);
    }

    public static BridgeNetworkPakCarrierSnapshot CarrierSnapshot(
        BridgeNetworkPakTransport selected, BridgeNetworkPakTransport active) =>
        new(selected, active, BridgeNetworkPakCarrierTransitionPhase.Idle,
            BridgeNetworkPakTransport.None,
            BridgeNetworkPakCarrierPersistenceState.Confirmed,
            BridgeNetworkPakCarrierOutcome.None,
            BridgeNetworkPakCarrierFailure.None, null);

    public static BridgeNetworkPakCarrierResult CarrierResult(
        bool success,
        BridgeNetworkPakTransport selected,
        BridgeNetworkPakTransport active,
        BridgeNetworkPakCarrierOutcome outcome,
        BridgeNetworkPakCarrierFailure failure = BridgeNetworkPakCarrierFailure.None,
        string? diagnostic = null) =>
        new(success, Committed: true, outcome, failure, diagnostic,
            CarrierSnapshot(selected, active) with
            {
                LastOutcome = outcome,
                Failure = failure,
                Diagnostic = diagnostic
            });
}

namespace PicoSwitchController
{
    public sealed record BridgeBulkProgress(
        string Phase, int CompletedBytes, int TotalBytes, int ItemIndex, int ItemCount);

    public sealed record GameBoyTransferProfile(
        byte CartType, int RomSize, int RamSize, byte FirmwareFlags);
}
