using System.Text;
using System.Text.Json;
using PicoSwitchController;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

string pin = Convert.ToBase64String(Enumerable.Range(0, 32)
    .Select(index => (byte)index).ToArray());
string fingerprint = string.Concat(Enumerable.Repeat("AB", 32));
string token = "explicitly-revealed-token-1234567890";
BridgeApiOnboardingSnapshot snapshot = new(
    true, BridgeApiOnboardingUnavailableReason.None, "v1",
    BridgeApiAccessMode.LocalNetwork, "https://192.168.1.20:6470",
    "https", "Bearer", pin, fingerprint,
    new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc),
    true, false);
BridgeApiOnboardingPayloadBuilder builder = new();
BridgeApiOnboardingPayload payload = builder.Build(snapshot, token);

Check(payload.Schema == "nice64-bridge-onboarding",
    "payload schema is frozen");
Check(payload.Version == 1, "payload version is exactly 1");
Check(payload.ApiVersion == "v1", "API version is exactly v1");
Check(payload.Url == snapshot.Endpoint,
    "payload URL matches authoritative HTTPS runtime endpoint");
Check(payload.Token == token,
    "payload token equals explicitly revealed token");
Check(payload.SpkiSha256 == pin,
    "payload SPKI pin uses frozen standard Base64");
Check(payload.CertificateSha256 == fingerprint,
    "payload certificate fingerprint uses uppercase SHA-256 hex");

string json = builder.BuildJson(snapshot, token);
string expected =
    $"{{\"schema\":\"nice64-bridge-onboarding\",\"version\":1,\"apiVersion\":\"v1\",\"url\":\"https://192.168.1.20:6470\",\"token\":\"{token}\",\"spkiSha256\":\"{pin}\",\"certificateSha256\":\"{fingerprint}\"}}";
Check(json == expected, "JSON serialization is deterministic with frozen order");
Check(!json.Contains('\n') && !json.Contains('\r') &&
    !json.Contains("  ", StringComparison.Ordinal),
    "JSON is compact with no indentation");
Check(builder.BuildJson(snapshot, token) == json,
    "repeated JSON construction is byte-stable");

byte[] utf8 = builder.BuildUtf8(snapshot, token);
Check(Encoding.UTF8.GetString(utf8) == json,
    "UTF-8 payload round-trip succeeds");
using JsonDocument document = JsonDocument.Parse(utf8);
string[] fields = document.RootElement.EnumerateObject()
    .Select(property => property.Name).ToArray();
Check(fields.SequenceEqual([
        "schema", "version", "apiVersion", "url", "token",
        "spkiSha256", "certificateSha256"]),
    "v1 payload has exactly seven frozen fields");

string[] forbidden =
[
    "private", "pfx", "com", "6464", "sessionId", "leaseId",
    "senderId", "ssid", "password", "history", "networkPak"
];
Check(forbidden.All(value => !json.Contains(value,
        StringComparison.OrdinalIgnoreCase)),
    "payload excludes private key, PFX, COM, Pico/session/lease/wire data");

bool unavailableRejected = false;
try { builder.Build(snapshot with { Available = false }, token); }
catch (InvalidOperationException) { unavailableRejected = true; }
Check(unavailableRejected, "unavailable snapshot cannot produce credential payload");

bool httpRejected = false;
try
{
    builder.Build(snapshot with { Endpoint = "http://192.168.1.20:6470" }, token);
}
catch (InvalidOperationException) { httpRejected = true; }
Check(httpRejected, "HTTP endpoint cannot produce onboarding payload");

bool invalidPinRejected = false;
try { builder.Build(snapshot with { SpkiSha256Base64 = "not-a-pin" }, token); }
catch (InvalidOperationException) { invalidPinRejected = true; }
Check(invalidPinRejected, "invalid SPKI encoding is rejected");

bool invalidFingerprintRejected = false;
try
{
    builder.Build(snapshot with {
        CertificateSha256Fingerprint = fingerprint.ToLowerInvariant() }, token);
}
catch (InvalidOperationException) { invalidFingerprintRejected = true; }
Check(invalidFingerprintRejected, "non-uppercase fingerprint is rejected");

FakeTokenProvider tokenProvider = new(token);
BridgeApiLocalCredentialProvider credentials = new(tokenProvider);
BridgeApiOnboardingPresentation presentation = new();
Check(!presentation.TokenRevealed &&
    presentation.VisibleToken is null && presentation.VisiblePayload is null,
    "credential presentation starts hidden and empty");
bool copyBeforeRevealRejected = false;
try { presentation.GetTokenForExplicitCopy(); }
catch (InvalidOperationException) { copyBeforeRevealRejected = true; }
Check(copyBeforeRevealRejected,
    "token copy is impossible before explicit reveal");
Check(presentation.RevealToken(credentials) == token &&
    tokenProvider.GetCount == 1,
    "deliberate reveal returns current authoritative token once");
Check(presentation.GetTokenForExplicitCopy() == token,
    "explicit copy source is available only after reveal");
Check(presentation.BuildPayload(snapshot, builder) == json,
    "credential payload is constructed only after reveal");
presentation.Dispose();
Check(!presentation.TokenRevealed &&
    presentation.VisibleToken is null && presentation.VisiblePayload is null,
    "closing presentation drops visible token and payload state");

Console.WriteLine(
    $"BridgeApiOnboardingPayloadHeadlessSmoke: {passed} checks passed.");

sealed class FakeTokenProvider(string token) : IBridgeApiTokenProvider
{
    public int GetCount { get; private set; }
    public string GetToken()
    {
        GetCount++;
        return token;
    }
}
