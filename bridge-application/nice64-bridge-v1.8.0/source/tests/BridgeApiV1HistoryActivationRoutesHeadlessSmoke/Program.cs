using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using PicoSwitchController;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

const string token = "D6B-history-route-test-token-0123456789";
const string sessionId = "11111111-2222-3333-4444-555555555555";
const string staleSessionId = "99999999-8888-7777-6666-555555555555";
string cpId = new('a', 64);
string tpId = new('b', 64);
string unavailableId = new('c', 64);
string missingId = new('d', 64);
const string cpSecret = @"C:\Users\Test\private.mpk";
const string tpSecret = @"D:\SECRET\PRIVATE\ROM.gbc";
const string uncSecret = @"\\server\share\secret.gb";

MutableSessionProvider sessions = new() { Current = Session(sessionId) };
MutableHistoryResolver resolver = new();
resolver.Set(cpId, new(BridgeHistoryCategory.ControllerPak,
    cpSecret, "private.mpk", true));
resolver.Set(tpId, new(BridgeHistoryCategory.TransferPak,
    tpSecret, "ROM.gbc", true));
resolver.Set(unavailableId, new(BridgeHistoryCategory.TransferPak,
    uncSecret, "secret.gb", false));
FakeHistoryStarter starter = new();
BridgeApiV1HistoryActivationFacade core = new(sessions, resolver, starter);
SwitchableHistoryFacade facade = new(core);

await using WebApplication application = BridgeApiHost.CreateApplication(
    new UnusedStateProvider(), token,
    new BridgeApiHostOptions { Enabled = true, Port = 6470 },
    configureBuilder: builder => builder.WebHost.UseTestServer(),
    historyActivation: facade);
await application.StartAsync();
using HttpClient client = application.GetTestClient();

string PathFor(string session, string item) =>
    $"/api/v1/sessions/{session}/history/{item}/activate";
string cpPath = PathFor(sessionId, cpId);
string tpPath = PathFor(sessionId, tpId);

using (HttpResponseMessage get = await client.SendAsync(
    Request(HttpMethod.Get, cpPath, token)))
{
    Check(get.StatusCode == HttpStatusCode.MethodNotAllowed,
        "exact History activation route is POST only");
}

foreach (string? suppliedToken in new[] { null, "wrong-token" })
{
    using HttpResponseMessage response = await client.SendAsync(Request(
        HttpMethod.Post, cpPath, suppliedToken, Json("""
        {"destructiveInstallConfirmed":false}
        """)));
    Check(response.StatusCode == HttpStatusCode.Unauthorized,
        suppliedToken is null
            ? "missing bearer authentication is rejected"
            : "invalid bearer authentication is rejected");
}

int callsBeforeBinding = facade.Calls;
using (HttpResponseMessage response = await client.SendAsync(Request(
    HttpMethod.Post, cpPath, token,
    new StringContent("false", Encoding.UTF8, "text/plain"))))
{
    Check(response.StatusCode == HttpStatusCode.UnsupportedMediaType &&
        ContainsCode(await response.Content.ReadAsStringAsync(),
            "unsupported-media-type"),
        "non-JSON content is bounded 415");
}

foreach ((string body, string name) in new[]
{
    ("{", "malformed JSON"),
    ("{}", "omitted confirmation"),
    ("{\"destructiveInstallConfirmed\":null}", "null confirmation"),
    ("{\"destructiveInstallConfirmed\":\"true\"}", "string confirmation"),
    ("{\"destructiveInstallConfirmed\":1}", "numeric confirmation"),
    ("{\"path\":\"D:\\\\SECRET\\\\PRIVATE\\\\ROM.gbc\"}", "path-only body"),
    ("{\"destructiveInstallConfirmed\":false,\"path\":\"D:\\\\SECRET\\\\PRIVATE\\\\ROM.gbc\"}", "extra path field"),
    ("{\"DestructiveInstallConfirmed\":false}", "case alias")
})
{
    using HttpResponseMessage response = await client.SendAsync(Request(
        HttpMethod.Post, cpPath, token, Json(body)));
    Check(response.StatusCode == HttpStatusCode.BadRequest &&
        ContainsCode(await response.Content.ReadAsStringAsync(), "invalid-request"),
        $"strict binding rejects {name}");
}
Check(facade.Calls == callsBeforeBinding,
    "invalid or extra fields never reach D6A and cannot alter the target");

int cpAdmissionBefore = starter.AcceptedAdmissions;
using HttpResponseMessage cpAccepted = await client.SendAsync(Request(
    HttpMethod.Post, cpPath, token, Json("""
    {"destructiveInstallConfirmed":false}
    """)));
string cpAcceptedJson = await cpAccepted.Content.ReadAsStringAsync();
using (JsonDocument document = JsonDocument.Parse(cpAcceptedJson))
{
    JsonElement root = document.RootElement;
    Check(cpAccepted.StatusCode == HttpStatusCode.Accepted &&
        root.GetProperty("accepted").GetBoolean() &&
        root.GetProperty("operationId").GetInt64() == starter.LastOperationId &&
        root.GetProperty("operationKind").GetString() == "history-activation",
        "Controller Pak false returns 202 with the real F8 operation identity");
}
Check(starter.AcceptedAdmissions == cpAdmissionBefore + 1 &&
    starter.LastCompletion is { IsCompleted: false },
    "accepted Controller Pak request returns before semantic completion and admits once");
Check(!ContainsSecret(cpAcceptedJson),
    "accepted response exposes no internal path or domain object");
starter.CompletePending();

int cpBeginsBeforeTrue = starter.ControllerBegins;
using (HttpResponseMessage cpTrue = await client.SendAsync(Request(
    HttpMethod.Post, cpPath, token, Json("""
    {"destructiveInstallConfirmed":true}
    """))))
{
    string json = await cpTrue.Content.ReadAsStringAsync();
    Check(cpTrue.StatusCode == HttpStatusCode.BadRequest &&
        ContainsCode(json, "invalid-confirmation") && !ContainsSecret(json) &&
        starter.ControllerBegins == cpBeginsBeforeTrue,
        "Controller Pak true confirmation is bounded 400 and never starts activation");
}

using (HttpResponseMessage stale = await client.SendAsync(Request(
    HttpMethod.Post, PathFor(staleSessionId, cpId), token, JsonFalse())))
{
    string json = await stale.Content.ReadAsStringAsync();
    Check(stale.StatusCode == HttpStatusCode.NotFound &&
        ContainsCode(json, "session-not-found") &&
        !json.Contains(sessionId, StringComparison.Ordinal),
        "stale session is bounded 404 without current-session disclosure");
}
using (HttpResponseMessage missing = await client.SendAsync(Request(
    HttpMethod.Post, PathFor(sessionId, missingId), token, JsonFalse())))
{
    string json = await missing.Content.ReadAsStringAsync();
    Check(missing.StatusCode == HttpStatusCode.NotFound &&
        ContainsCode(json, "history-item-not-found") && !ContainsSecret(json),
        "unknown opaque item is bounded redacted 404");
}
using (HttpResponseMessage unavailable = await client.SendAsync(Request(
    HttpMethod.Post, PathFor(sessionId, unavailableId), token, JsonFalse())))
{
    string json = await unavailable.Content.ReadAsStringAsync();
    Check(unavailable.StatusCode == HttpStatusCode.Conflict &&
        ContainsCode(json, "history-item-unavailable") && !ContainsSecret(json),
        "retained unavailable item is bounded redacted 409");
}

starter.Busy = true;
using (HttpResponseMessage busy = await client.SendAsync(Request(
    HttpMethod.Post, cpPath, token, JsonFalse())))
{
    string json = await busy.Content.ReadAsStringAsync();
    using JsonDocument document = JsonDocument.Parse(json);
    Check(busy.StatusCode == HttpStatusCode.Conflict &&
        ContainsCode(json, "operation-busy") &&
        document.RootElement.GetProperty("currentOperationId").GetInt64() == 88 &&
        document.RootElement.GetProperty("currentOperationKind").GetString() ==
            "firmware-mode-switch" && !ContainsSecret(json),
        "busy maps only bounded current operation metadata");
}
starter.Busy = false;

int tpAdmissionsBefore = starter.AcceptedAdmissions;
int resolveBeforePreflight = resolver.ResolveCalls;
using HttpResponseMessage confirmation = await client.SendAsync(Request(
    HttpMethod.Post, tpPath, token, JsonFalse()));
string confirmationJson = await confirmation.Content.ReadAsStringAsync();
using (JsonDocument document = JsonDocument.Parse(confirmationJson))
{
    JsonElement root = document.RootElement;
    JsonElement detail = root.GetProperty("confirmation");
    Check(confirmation.StatusCode == HttpStatusCode.Conflict &&
        ContainsCode(confirmationJson, "confirmation-required") &&
        detail.GetProperty("category").GetString() == "transfer-pak" &&
        detail.GetProperty("fileName").GetString() == "ROM.gbc" &&
        detail.GetProperty("contentKind").GetString() == "game-boy-rom" &&
        detail.GetProperty("title").GetString() == "SAFE GAME",
        "Transfer Pak false returns bounded confirmation metadata");
}
Check(starter.AcceptedAdmissions == tpAdmissionsBefore &&
    !ContainsSecret(confirmationJson),
    "Transfer Pak confirmation performs preflight without F8 admission or path exposure");

using HttpResponseMessage tpAccepted = await client.SendAsync(Request(
    HttpMethod.Post, tpPath, token, JsonTrue()));
string tpAcceptedJson = await tpAccepted.Content.ReadAsStringAsync();
Check(tpAccepted.StatusCode == HttpStatusCode.Accepted &&
    ContainsOperation(tpAcceptedJson, starter.LastOperationId,
        "history-activation") &&
    resolver.ResolveCalls >= resolveBeforePreflight + 3 &&
    starter.LastCompletion is { IsCompleted: false },
    "confirmed Transfer Pak reuses D6A revalidation and returns before install completion");
Check(starter.AcceptedAdmissions == tpAdmissionsBefore + 1,
    "confirmed Transfer Pak creates exactly one semantic admission");
starter.CompletePending();

using (HttpResponseMessage preflight = await client.SendAsync(Request(
    HttpMethod.Post, tpPath, token, JsonFalse())))
{
    Check(preflight.StatusCode == HttpStatusCode.Conflict,
        "Transfer Pak can be preflighted again without mutation");
}
resolver.SetAvailability(tpId, false);
using (HttpResponseMessage disappeared = await client.SendAsync(Request(
    HttpMethod.Post, tpPath, token, JsonTrue())))
{
    Check(disappeared.StatusCode == HttpStatusCode.Conflict &&
        ContainsCode(await disappeared.Content.ReadAsStringAsync(),
            "history-item-unavailable"),
        "file disappearing between confirmation calls is bounded 409");
}
resolver.SetAvailability(tpId, true);

using (HttpResponseMessage preflight = await client.SendAsync(Request(
    HttpMethod.Post, tpPath, token, JsonFalse())))
{
    Check(preflight.StatusCode == HttpStatusCode.Conflict,
        "Transfer Pak preflight succeeds before session replacement");
}
sessions.Current = Session("22222222-3333-4444-5555-666666666666");
using (HttpResponseMessage replaced = await client.SendAsync(Request(
    HttpMethod.Post, tpPath, token, JsonTrue())))
{
    Check(replaced.StatusCode == HttpStatusCode.NotFound &&
        ContainsCode(await replaced.Content.ReadAsStringAsync(), "session-not-found"),
        "session replacement between confirmation calls is bounded 404");
}
sessions.Current = Session(sessionId);

starter.Busy = true;
using (HttpResponseMessage busyTp = await client.SendAsync(Request(
    HttpMethod.Post, tpPath, token, JsonTrue())))
{
    Check(busyTp.StatusCode == HttpStatusCode.Conflict &&
        ContainsCode(await busyTp.Content.ReadAsStringAsync(), "operation-busy"),
        "busy confirmed Transfer Pak request is bounded 409 without nested F8");
}
starter.Busy = false;

starter.NextImmediateFailure = BridgeHistoryActivationFailure.NormalModeRequired;
using (HttpResponseMessage normal = await client.SendAsync(Request(
    HttpMethod.Post, cpPath, token, JsonFalse())))
{
    Check(normal.StatusCode == HttpStatusCode.Conflict &&
        ContainsCode(await normal.Content.ReadAsStringAsync(), "normal-mode-required"),
        "normal-mode-required maps to bounded 409");
}
starter.NextImmediateFailure = BridgeHistoryActivationFailure.InspectionFailure;
using (HttpResponseMessage inspection = await client.SendAsync(Request(
    HttpMethod.Post, tpPath, token, JsonFalse())))
{
    string json = await inspection.Content.ReadAsStringAsync();
    Check(inspection.StatusCode == HttpStatusCode.Conflict &&
        ContainsCode(json, "inspection-failed") && !ContainsSecret(json),
        "inspection failure maps to bounded redacted non-500 response");
}

starter.ConfirmationTitle = tpSecret;
using (HttpResponseMessage adversarialConfirmation = await client.SendAsync(Request(
    HttpMethod.Post, tpPath, token, JsonFalse())))
{
    string json = await adversarialConfirmation.Content.ReadAsStringAsync();
    using JsonDocument document = JsonDocument.Parse(json);
    Check(adversarialConfirmation.StatusCode == HttpStatusCode.Conflict &&
        document.RootElement.GetProperty("confirmation").GetProperty("title")
            .ValueKind == JsonValueKind.Null && !ContainsSecret(json),
        "path-shaped metadata is removed at the HTTP projection boundary");
}
starter.ConfirmationTitle = "SAFE GAME";

facade.ThrowNext = new InvalidOperationException(
    tpSecret + " " + uncSecret + " at Secret.Stack()");
using (HttpResponseMessage unexpected = await client.SendAsync(Request(
    HttpMethod.Post, cpPath, token, JsonFalse())))
{
    string json = await unexpected.Content.ReadAsStringAsync();
    Check(unexpected.StatusCode == HttpStatusCode.InternalServerError &&
        ContainsCode(json, "internal-error") && !ContainsSecret(json) &&
        !json.Contains("Stack", StringComparison.OrdinalIgnoreCase) &&
        !json.Contains("InvalidOperationException", StringComparison.OrdinalIgnoreCase),
        "unexpected facade exception is bounded 500 without raw error or stack text");
}

foreach (string forbiddenPath in new[]
{
    "/api/v1/history-operation/1",
    "/api/v1/history/item/status",
    "/api/v2/sessions/x/history/y/activate",
    "/api/v1/connect",
    "/api/v1/disconnect",
    "/api/v1/wifi",
    "/api/v1/controller/state"
})
{
    using HttpResponseMessage response = await client.SendAsync(Request(
        HttpMethod.Post, forbiddenPath, token, Json("{}")));
    Check(response.StatusCode is HttpStatusCode.NotFound or
        HttpStatusCode.MethodNotAllowed,
        $"unapproved API capability remains absent: {forbiddenPath}");
}

using (HttpRequestMessage cors = Request(HttpMethod.Post, cpPath, token, JsonFalse()))
{
    cors.Headers.Add("Origin", "https://unapproved.example");
    using HttpResponseMessage response = await client.SendAsync(cors);
    Check(!response.Headers.Contains("Access-Control-Allow-Origin"),
        "History route adds no CORS policy");
    starter.CompletePending();
}

string sourceRoot = FindSourceRoot();
string routeSource = File.ReadAllText(Path.Combine(
    sourceRoot, "BridgeApiV1HistoryActivationRoutes.cs"));
string hostSource = File.ReadAllText(Path.Combine(sourceRoot, "BridgeApiHost.cs"));
Check(Count(hostSource, "BridgeApiV1HistoryActivationRoutes.Map") == 1 &&
    Count(routeSource, "application.MapPost(Route") == 1 &&
    !routeSource.Contains("MapGet", StringComparison.Ordinal),
    "host composes exactly one POST-only History activation route");
Check(!routeSource.Contains("TryBegin", StringComparison.Ordinal) &&
    !routeSource.Contains("BridgeOperationCoordinator", StringComparison.Ordinal) &&
    !routeSource.Contains("BridgeApiCommandFacade", StringComparison.Ordinal) &&
    !routeSource.Contains("Task.Run", StringComparison.Ordinal),
    "HTTP route creates no outer or nested F8 operation and does not wait in a worker");
Check(!routeSource.Contains("Results.Json(result", StringComparison.Ordinal) &&
    !routeSource.Contains("BridgeHistoryActivationResult", StringComparison.Ordinal) &&
    !routeSource.Contains("BridgeHistoryActivationProgress", StringComparison.Ordinal) &&
    !routeSource.Contains("BridgeOperationSnapshot", StringComparison.Ordinal) &&
    !routeSource.Contains("BridgeApiV1ResolvedHistoryItem", StringComparison.Ordinal),
    "raw internal History and operation domain objects cannot be serialized by route");
Check(!routeSource.Contains("UseCors", StringComparison.Ordinal) &&
    !routeSource.Contains("AllowAnonymous", StringComparison.Ordinal) &&
    !routeSource.Contains("ListenAnyIP", StringComparison.Ordinal) &&
    !routeSource.Contains("UseUrls", StringComparison.Ordinal) &&
    !routeSource.Contains("Console.", StringComparison.Ordinal),
    "route leaves bearer middleware, TLS, bindings, CORS, and logging unchanged");

await application.StopAsync();
Console.WriteLine($"BridgeApiV1HistoryActivationRoutesHeadlessSmoke: {passed} checks passed.");

static HttpRequestMessage Request(
    HttpMethod method, string path, string? token, HttpContent? content = null)
{
    HttpRequestMessage request = new(method, path) { Content = content };
    if (token is not null)
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return request;
}

static HttpContent Json(string body) =>
    new StringContent(body, Encoding.UTF8, "application/json");

static HttpContent JsonFalse() => Json("""
    {"destructiveInstallConfirmed":false}
    """);

static HttpContent JsonTrue() => Json("""
    {"destructiveInstallConfirmed":true}
    """);

static bool ContainsCode(string json, string code)
{
    using JsonDocument document = JsonDocument.Parse(json);
    return document.RootElement.GetProperty("code").GetString() == code;
}

static bool ContainsOperation(string json, long operationId, string kind)
{
    using JsonDocument document = JsonDocument.Parse(json);
    JsonElement root = document.RootElement;
    return root.GetProperty("accepted").GetBoolean() &&
        root.GetProperty("operationId").GetInt64() == operationId &&
        root.GetProperty("operationKind").GetString() == kind;
}

static bool ContainsSecret(string value) => new[]
{
    @"C:\Users\Test\private.mpk", @"D:\SECRET\PRIVATE\ROM.gbc",
    @"\\server\share\secret.gb", "Secret.Stack", "InvalidOperationException"
}.Any(secret => value.Contains(secret, StringComparison.OrdinalIgnoreCase));

static int Count(string source, string value)
{
    int count = 0;
    int offset = 0;
    while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
    {
        count++;
        offset += value.Length;
    }
    return count;
}

static string FindSourceRoot()
{
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "BridgeApiHost.cs")))
            return directory.FullName;
        directory = directory.Parent;
    }
    throw new DirectoryNotFoundException("Bridge source root was not found.");
}

static BridgeSessionSnapshot Session(string id) => new(
    id, BridgeSessionState.Connected,
    new DateTimeOffset(2030, 1, 2, 3, 0, 0, TimeSpan.Zero),
    new DateTimeOffset(2030, 1, 2, 3, 4, 0, TimeSpan.Zero),
    BridgeControlConnectionKind.SerialUsb, "COM91", "NICE64",
    FirmwarePersonality.Controller, BridgeAccessoryMode.ControllerPak);

sealed class MutableSessionProvider : IBridgeSessionProvider
{
    public BridgeSessionSnapshot? Current { get; set; }
    public event EventHandler<BridgeSessionChangedEventArgs>? CurrentChanged;
    public void Raise(BridgeSessionSnapshot? previous) =>
        CurrentChanged?.Invoke(this, new(previous, Current));
}

sealed class MutableHistoryResolver : IBridgeApiV1HistoryItemResolver
{
    private readonly Dictionary<string, BridgeApiV1ResolvedHistoryItem> _items =
        new(StringComparer.Ordinal);

    public int ResolveCalls { get; private set; }

    public void Set(string id, BridgeApiV1ResolvedHistoryItem item) =>
        _items[id] = item;

    public void SetAvailability(string id, bool available) =>
        _items[id] = _items[id] with { Available = available };

    public BridgeApiV1HistoryResolution Resolve(string? itemId)
    {
        ResolveCalls++;
        return itemId is not null && _items.TryGetValue(itemId, out var item)
            ? new(BridgeApiV1HistoryResolutionStatus.Resolved, item)
            : new(BridgeApiV1HistoryResolutionStatus.NotFound, null);
    }
}

sealed class FakeHistoryStarter : IBridgeHistoryActivationStarter
{
    private long _nextOperationId = 700;
    private TaskCompletionSource<BridgeHistoryActivationResult>? _pending;

    public bool Busy { get; set; }
    public BridgeHistoryActivationFailure NextImmediateFailure { get; set; }
    public string ConfirmationTitle { get; set; } = "SAFE GAME";
    public int ControllerBegins { get; private set; }
    public int TransferBegins { get; private set; }
    public int AcceptedAdmissions { get; private set; }
    public long LastOperationId { get; private set; }
    public Task? LastCompletion => _pending?.Task;

    public BridgeHistoryActivationStart BeginControllerPak(
        string path,
        IProgress<BridgeHistoryActivationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<bool>? admissionGuard = null)
    {
        ControllerBegins++;
        if (admissionGuard is not null && !admissionGuard()) return GuardRejected();
        if (Busy) return BusyResult(BridgeHistoryCategory.ControllerPak, path);
        if (NextImmediateFailure != BridgeHistoryActivationFailure.None)
            return Immediate(BridgeHistoryCategory.ControllerPak, path);
        return Accepted(BridgeHistoryCategory.ControllerPak, path);
    }

    public Task<BridgeHistoryActivationStart> BeginTransferPakAsync(
        string romPath,
        bool destructiveInstallConfirmed,
        IProgress<BridgeHistoryActivationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<bool>? admissionGuard = null)
    {
        TransferBegins++;
        if (NextImmediateFailure != BridgeHistoryActivationFailure.None)
            return Task.FromResult(Immediate(BridgeHistoryCategory.TransferPak, romPath));
        if (!destructiveInstallConfirmed)
            return Task.FromResult(Confirmation(romPath));
        if (admissionGuard is not null && !admissionGuard())
            return Task.FromResult(GuardRejected());
        return Task.FromResult(Busy
            ? BusyResult(BridgeHistoryCategory.TransferPak, romPath)
            : Accepted(BridgeHistoryCategory.TransferPak, romPath));
    }

    public void CompletePending()
    {
        TaskCompletionSource<BridgeHistoryActivationResult>? pending = _pending;
        if (pending is null) return;
        pending.TrySetResult(Result(BridgeHistoryCategory.ControllerPak,
            @"C:\Users\Test\private.mpk", BridgeHistoryActivationFailure.None));
        _pending = null;
    }

    private BridgeHistoryActivationStart Accepted(
        BridgeHistoryCategory category, string path)
    {
        AcceptedAdmissions++;
        LastOperationId = ++_nextOperationId;
        _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BridgeOperationSnapshot operation = new(LastOperationId,
            BridgeOperationKind.HistoryActivation, BridgeOperationStatus.Running,
            DateTimeOffset.UtcNow, null, null,
            BridgeOperationCancellationCapability.BeforeCommitOnly, false,
            "HistoryActivation", null, null, new { Secret = path });
        return new(BridgeHistoryActivationStartDisposition.Accepted,
            LastOperationId, _pending.Task, null, operation);
    }

    private BridgeHistoryActivationStart BusyResult(
        BridgeHistoryCategory category, string path)
    {
        BridgeOperationSnapshot operation = new(88,
            BridgeOperationKind.FirmwareModeSwitch, BridgeOperationStatus.Running,
            DateTimeOffset.UtcNow, null, null,
            BridgeOperationCancellationCapability.BeforeCommitOnly, false,
            "Firmware", null, @"D:\SECRET\busy-error",
            new { Secret = path });
        return new(BridgeHistoryActivationStartDisposition.Busy,
            null, null,
            Result(category, path, BridgeHistoryActivationFailure.Busy), operation);
    }

    private BridgeHistoryActivationStart Immediate(
        BridgeHistoryCategory category, string path)
    {
        BridgeHistoryActivationFailure failure = NextImmediateFailure;
        NextImmediateFailure = BridgeHistoryActivationFailure.None;
        return new(BridgeHistoryActivationStartDisposition.Immediate,
            null, null, Result(category, path, failure), BridgeOperationSnapshot.Idle);
    }

    private BridgeHistoryActivationStart Confirmation(string path)
    {
        GameBoyRomMetadata metadata = new(ConfirmationTitle, 0x10,
            "MBC3+TIMER+RAM+BATTERY", "MBC3", 2 * 1024 * 1024,
            32 * 1024, true);
        BridgeTransferPakRomInspection inspection = new(path,
            @"D:\SECRET\PRIVATE\ROM.sav", 2 * 1024 * 1024, null, metadata);
        BridgeHistoryActivationResult result = Result(
            BridgeHistoryCategory.TransferPak, path,
            BridgeHistoryActivationFailure.ConfirmationRequired) with
        { Inspection = inspection };
        return new(BridgeHistoryActivationStartDisposition.Immediate,
            null, null, result, BridgeOperationSnapshot.Idle);
    }

    private static BridgeHistoryActivationStart GuardRejected() => new(
        BridgeHistoryActivationStartDisposition.AdmissionGuardRejected,
        null, null, null, BridgeOperationSnapshot.Idle);

    private static BridgeHistoryActivationResult Result(
        BridgeHistoryCategory category, string path,
        BridgeHistoryActivationFailure failure) => new(
            failure == BridgeHistoryActivationFailure.None, false,
            failure == BridgeHistoryActivationFailure.None, category, path,
            failure, @"D:\SECRET\PRIVATE\raw-error InvalidOperationException");
}

sealed class SwitchableHistoryFacade(
    IBridgeApiV1HistoryActivationFacade inner) :
    IBridgeApiV1HistoryActivationFacade
{
    public int Calls { get; private set; }
    public Exception? ThrowNext { get; set; }

    public Task<BridgeApiV1HistoryActivationResult> StartAsync(
        string? sessionId,
        string? itemId,
        BridgeApiV1HistoryActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        if (ThrowNext is Exception exception)
        {
            ThrowNext = null;
            throw exception;
        }
        return inner.StartAsync(sessionId, itemId, request, cancellationToken);
    }
}

sealed class UnusedStateProvider : IBridgeStateProvider
{
    public BridgeStateSnapshot GetSnapshot() =>
        throw new InvalidOperationException("D6B route must not read aggregate state");
}

namespace PicoSwitchController
{
    public sealed record BridgeBulkProgress(
        string Phase, int CompletedBytes, int TotalBytes, int ItemIndex, int ItemCount);

    public sealed record GameBoyTransferProfile(
        byte CartType, int RomSize, int RamSize, byte FirmwareFlags);
}
