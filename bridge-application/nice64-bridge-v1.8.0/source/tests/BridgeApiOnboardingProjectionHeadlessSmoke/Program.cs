using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PicoSwitchController;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

BridgeApiAccessConfiguration Config(BridgeApiAccessMode mode) => new(
    mode, 6470,
    mode == BridgeApiAccessMode.LocalNetwork ? "stored-interface" : null,
    mode == BridgeApiAccessMode.LocalNetwork ? "192.0.2.99" : null);
BridgeApiAccessRuntimeState Runtime(
    BridgeApiAccessMode mode,
    BridgeApiLoopbackRuntimeStatus loopback,
    BridgeApiLanRuntimeStatus lan,
    string? endpoint = null) => new(
        Config(mode), loopback, lan, BridgeApiLanValidationFailure.None,
        loopback == BridgeApiLoopbackRuntimeStatus.Listening
            ? "http://127.0.0.1:6470" : null,
        endpoint);

using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
CertificateRequest request = new("CN=Onboarding Test", key,
    HashAlgorithmName.SHA256);
using X509Certificate2 certificate = request.CreateSelfSigned(
    DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(1));
string pin = Convert.ToBase64String(SHA256.HashData(
    certificate.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo()));
string fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
BridgeApiTlsIdentitySnapshot identity = new(
    certificate, pin, fingerprint);

BridgeApiAccessRuntimeState runtime = Runtime(
    BridgeApiAccessMode.Off,
    BridgeApiLoopbackRuntimeStatus.NotRequested,
    BridgeApiLanRuntimeStatus.NotRequested);
FakeTlsProvider tls = new(identity);
FakeTokenProvider tokens = new(
    "authoritative-token-value-1234567890-abcdef");
BridgeApiLocalCredentialProvider credentials = new(tokens);
BridgeApiOnboardingProvider provider = new(
    () => runtime, tls, credentials);

BridgeApiOnboardingSnapshot snapshot = provider.GetCurrent();
Check(!snapshot.Available &&
    snapshot.UnavailableReason == BridgeApiOnboardingUnavailableReason.ApiOff,
    "Off is not onboarding-ready");

runtime = Runtime(BridgeApiAccessMode.LocalOnly,
    BridgeApiLoopbackRuntimeStatus.Listening,
    BridgeApiLanRuntimeStatus.NotRequested);
snapshot = provider.GetCurrent();
Check(!snapshot.Available &&
    snapshot.UnavailableReason == BridgeApiOnboardingUnavailableReason.LocalOnly,
    "LocalOnly is not mobile-onboarding-ready");

runtime = Runtime(BridgeApiAccessMode.LocalNetwork,
    BridgeApiLoopbackRuntimeStatus.Listening,
    BridgeApiLanRuntimeStatus.UnavailableSelectedAddress);
snapshot = provider.GetCurrent();
Check(!snapshot.Available &&
    snapshot.UnavailableReason == BridgeApiOnboardingUnavailableReason.LanNotListening &&
    snapshot.Endpoint is null,
    "degraded LocalNetwork exposes no stale configured address");

runtime = Runtime(BridgeApiAccessMode.LocalNetwork,
    BridgeApiLoopbackRuntimeStatus.Listening,
    BridgeApiLanRuntimeStatus.Listening,
    "http://10.20.30.40:6470");
snapshot = provider.GetCurrent();
Check(!snapshot.Available &&
    snapshot.UnavailableReason == BridgeApiOnboardingUnavailableReason.InsecureEndpoint,
    "HTTP LAN runtime is never onboarding-ready");

runtime = Runtime(BridgeApiAccessMode.LocalNetwork,
    BridgeApiLoopbackRuntimeStatus.Listening,
    BridgeApiLanRuntimeStatus.Listening,
    "https://10.20.30.40:6470");
snapshot = provider.GetCurrent();
Check(snapshot.Available &&
    snapshot.UnavailableReason == BridgeApiOnboardingUnavailableReason.None,
    "healthy LocalNetwork HTTPS is onboarding-ready");
Check(snapshot.Endpoint == "https://10.20.30.40:6470" &&
    !snapshot.Endpoint.Contains("192.0.2.99", StringComparison.Ordinal),
    "endpoint comes only from actual runtime state");
Check(snapshot.Protocol == "https" &&
    snapshot.AuthenticationScheme == "Bearer",
    "snapshot freezes HTTPS and Bearer semantics");
Check(snapshot.SpkiSha256Base64 == pin,
    "SPKI pin comes from TLS identity metadata");
Check(snapshot.CertificateSha256Fingerprint == fingerprint,
    "certificate fingerprint comes from TLS identity metadata");
Check(snapshot.CertificateNotAfterUtc == identity.NotAfterUtc,
    "certificate expiration is projected");
Check(snapshot.TokenAvailable && !snapshot.QrAvailable,
    "token availability is projected without claiming QR rendering");

FakeTlsProvider missingTls = new(null);
BridgeApiOnboardingProvider missingTlsProvider = new(
    () => runtime, missingTls, credentials);
snapshot = missingTlsProvider.GetCurrent();
Check(!snapshot.Available &&
    snapshot.UnavailableReason ==
        BridgeApiOnboardingUnavailableReason.TlsIdentityUnavailable,
    "TLS identity absence blocks onboarding");

FakeTokenProvider unavailableToken = new(null);
BridgeApiLocalCredentialProvider missingCredential = new(unavailableToken);
BridgeApiOnboardingProvider missingTokenProvider = new(
    () => runtime, tls, missingCredential);
snapshot = missingTokenProvider.GetCurrent();
Check(!snapshot.Available &&
    snapshot.UnavailableReason ==
        BridgeApiOnboardingUnavailableReason.TokenUnavailable,
    "token-store absence blocks onboarding");

string[] propertyNames = typeof(BridgeApiOnboardingSnapshot)
    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
    .Select(property => property.Name).ToArray();
string[] forbiddenProperties =
[
    "Token", "PrivateKey", "Certificate", "Pfx", "ProtectedBytes",
    "SessionId", "LeaseId", "ComPort", "ManagementEndpoint",
    "LatestStateSenderId"
];
Check(forbiddenProperties.All(forbidden =>
        !propertyNames.Contains(forbidden, StringComparer.OrdinalIgnoreCase)),
    "ordinary onboarding snapshot excludes credentials and internal authority");
Check(!snapshot.ToString().Contains(
        "authoritative-token-value", StringComparison.Ordinal),
    "ordinary snapshot text contains no bearer token");
string revealedOnce = credentials.RevealCurrentToken();
string revealedTwice = credentials.RevealCurrentToken();
Check(revealedOnce == revealedTwice &&
    revealedOnce == "authoritative-token-value-1234567890-abcdef" &&
    tokens.GetCount == 3,
    "local reveal returns the same authoritative token without rotation");

string sourceRoot = FindSourceDirectory();
string hostSource = File.ReadAllText(Path.Combine(sourceRoot,
    "BridgeApiHost.cs"));
Check(!hostSource.Contains("/api/v1/onboarding", StringComparison.Ordinal) &&
    !hostSource.Contains("/api/v1/token", StringComparison.Ordinal) &&
    !hostSource.Contains("/api/v1/pin", StringComparison.Ordinal),
    "onboarding metadata and token have no API route");

Console.WriteLine(
    $"BridgeApiOnboardingProjectionHeadlessSmoke: {passed} checks passed.");

static string FindSourceDirectory()
{
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
        directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "BridgeApiHost.cs")))
            return directory.FullName;
    }
    throw new DirectoryNotFoundException();
}

sealed class FakeTlsProvider(BridgeApiTlsIdentitySnapshot? identity) :
    IBridgeApiTlsIdentityProvider
{
    public BridgeApiTlsIdentityResult GetOrCreate() => identity is null
        ? BridgeApiTlsIdentityResult.Unavailable
        : BridgeApiTlsIdentityResult.Available(identity);
    public void Dispose() { }
}

sealed class FakeTokenProvider(string? token) : IBridgeApiTokenProvider
{
    public int GetCount { get; private set; }
    public string GetToken()
    {
        GetCount++;
        return token ?? throw new InvalidOperationException("Unavailable");
    }
}
