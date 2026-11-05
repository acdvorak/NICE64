using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using PicoSwitchController;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

static async Task<(bool Handshake, string? Fingerprint)> RoundTripTlsAsync(
    X509Certificate2 certificate)
{
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    Task server = Task.Run(async () =>
    {
        using TcpClient accepted = await listener.AcceptTcpClientAsync();
        using SslStream stream = new(accepted.GetStream(), false);
        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        });
    });
    string? fingerprint = null;
    using TcpClient client = new();
    await client.ConnectAsync(IPAddress.Loopback, port);
    using (SslStream stream = new(client.GetStream(), false,
        (_, presented, _, _) =>
        {
            if (presented is not null)
            {
                using X509Certificate2 captured = new(presented);
                fingerprint = Convert.ToHexString(
                    SHA256.HashData(captured.RawData));
            }
            return true;
        }))
    {
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        });
    }
    await server;
    return (true, fingerprint);
}

string testRoot = Path.Combine(AppContext.BaseDirectory,
    $"tls-identity-{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);
try
{
    string identityPath = Path.Combine(testRoot, "bridge-api-tls.pfx.dat");
    List<string> logs = [];
    BridgeApiTlsIdentitySnapshot first;
    byte[] firstPublicKey;
    using (BridgeApiTlsIdentityProvider provider = new(identityPath, logs.Add))
    {
        BridgeApiTlsIdentityResult result = provider.GetOrCreate();
        Check(result.Success && result.Identity is not null,
            "first use generates TLS identity");
        first = result.Identity!;
        Check(first.Certificate.HasPrivateKey,
            "generated certificate has private key");
        X509BasicConstraintsExtension? constraints = first.Certificate.Extensions
            .OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        Check(constraints is not null && !constraints.CertificateAuthority,
            "certificate is not a CA");
        X509EnhancedKeyUsageExtension? eku = first.Certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        Check(eku is not null && eku.EnhancedKeyUsages.Cast<Oid>()
            .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1"),
            "Server Authentication EKU is present");
        using ECDsa? key = first.Certificate.GetECDsaPrivateKey();
        Check(key?.KeySize == 256 &&
            first.Certificate.PublicKey.Oid.Value == "1.2.840.10045.2.1",
            "key algorithm is exactly ECDSA P-256");
        double lifetimeDays =
            (first.NotAfterUtc - first.NotBeforeUtc).TotalDays;
        Check(lifetimeDays is > 1450 and < 1470,
            "certificate lifetime is bounded to four years");
        Check(first.Certificate.Extensions
            .OfType<X509SubjectAlternativeNameExtension>().Any(extension =>
                extension.EnumerateDnsNames().Contains("localhost") &&
                extension.EnumerateIPAddresses().Any(address =>
                    address.ToString() == "127.0.0.1")),
            "SAN contains localhost and 127.0.0.1");
        using ECDsa firstPublic = first.Certificate.GetECDsaPublicKey()!;
        firstPublicKey = firstPublic.ExportSubjectPublicKeyInfo();
        Check(File.Exists(identityPath),
            "protected identity storage is created at exact path");
    }

    string firstThumbprint = first.Thumbprint;
    string firstPin = first.SpkiSha256Base64;
    string firstFingerprint = first.CertificateSha256Fingerprint;
    using (BridgeApiTlsIdentityProvider provider = new(identityPath))
    {
        BridgeApiTlsIdentityResult reload = provider.GetOrCreate();
        Check(reload.Success && reload.Identity is not null,
            "protected identity reload succeeds");
        BridgeApiTlsIdentitySnapshot second = reload.Identity!;
        using ECDsa publicKey = second.Certificate.GetECDsaPublicKey()!;
        byte[] secondPublicKey = publicKey.ExportSubjectPublicKeyInfo();
        Check(second.Thumbprint == firstThumbprint,
            "certificate identity persists across reload");
        Check(second.SpkiSha256Base64 == firstPin,
            "SPKI pin persists across reload");
        Check(secondPublicKey.SequenceEqual(firstPublicKey),
            "public key persists across reload");
        Check(second.CertificateSha256Fingerprint == firstFingerprint,
            "certificate fingerprint persists across reload");
        Check(Convert.ToBase64String(SHA256.HashData(firstPublicKey)) == firstPin,
            "SPKI pin is deterministic SHA-256 standard Base64");
        Check(Convert.ToHexString(SHA256.HashData(second.Certificate.RawData)) ==
            firstFingerprint && firstFingerprint.Length == 64,
            "certificate fingerprint is deterministic uppercase SHA-256 hex");
        using ECDsa privateKey = second.Certificate.GetECDsaPrivateKey()!;
        byte[] digest = SHA256.HashData("listener-runtime"u8.ToArray());
        byte[] signature = privateKey.SignHash(digest);
        using ECDsa verifyKey = second.Certificate.GetECDsaPublicKey()!;
        Check(verifyKey.VerifyHash(digest, signature),
            "reloaded private key remains usable at listener runtime");
        (bool handshake, string? presentedFingerprint) =
            await RoundTripTlsAsync(second.Certificate);
        Check(handshake && presentedFingerprint == firstFingerprint,
            "reloaded identity completes TLS handshake and presents expected certificate");
    }

    byte[] protectedFile = File.ReadAllBytes(identityPath);
    Check(protectedFile.Length > 0 && protectedFile[0] != 0x30 &&
        !Encoding.ASCII.GetString(protectedFile).Contains(
            "PRIVATE KEY", StringComparison.Ordinal),
        "private key is not stored as plaintext PFX or PEM");
    Check(Directory.GetFiles(testRoot).SequenceEqual([identityPath]),
        "identity storage leaves no temporary artifact");

    File.WriteAllBytes(identityPath, [1, 2, 3, 4, 5]);
    using (BridgeApiTlsIdentityProvider provider = new(identityPath))
    {
        BridgeApiTlsIdentityResult regenerated = provider.GetOrCreate();
        Check(regenerated.Success && regenerated.Identity is not null,
            "corrupt protected storage regenerates without crash");
        Check(regenerated.Identity!.Thumbprint != firstThumbprint,
            "corruption regeneration creates a new identity");
        Check(File.ReadAllBytes(identityPath).Length > 5,
            "corrupt storage is atomically replaced");
    }

    string renewalPath = Path.Combine(testRoot, "renewal.pfx.dat");
    DateTimeOffset initialTime = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    string originalRenewalPin;
    DateTime renewalExpiry;
    using (BridgeApiTlsIdentityProvider provider = new(
        renewalPath, utcNow: () => initialTime))
    {
        BridgeApiTlsIdentitySnapshot identity =
            provider.GetOrCreate().Identity!;
        originalRenewalPin = identity.SpkiSha256Base64;
        renewalExpiry = identity.NotAfterUtc;
    }
    DateTimeOffset nearExpiry = new DateTimeOffset(
        renewalExpiry, TimeSpan.Zero).AddDays(-29);
    using (BridgeApiTlsIdentityProvider provider = new(
        renewalPath, utcNow: () => nearExpiry))
    {
        BridgeApiTlsIdentitySnapshot renewed =
            provider.GetOrCreate().Identity!;
        Check(renewed.SpkiSha256Base64 != originalRenewalPin,
            "identity renews inside 30-day threshold");
        Check(renewed.NotAfterUtc > renewalExpiry,
            "renewed certificate receives a fresh bounded lifetime");
    }

    string sourceRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    string providerSource = File.ReadAllText(Path.Combine(
        sourceRoot, "BridgeApiTlsIdentity.cs"));
    string settingsSource = File.ReadAllText(Path.Combine(
        sourceRoot, "BridgeSettings.cs"));
    Check(providerSource.Contains("DataProtectionScope.CurrentUser",
            StringComparison.Ordinal) &&
        providerSource.Contains("FileOptions.WriteThrough", StringComparison.Ordinal) &&
        providerSource.Contains("Flush(flushToDisk: true)", StringComparison.Ordinal) &&
        providerSource.Contains("File.Move(temporary, _path, overwrite: true)",
            StringComparison.Ordinal),
        "identity persistence is DPAPI CurrentUser protected and atomic");
    Check(!settingsSource.Contains("Pfx", StringComparison.OrdinalIgnoreCase) &&
        !settingsSource.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase) &&
        !settingsSource.Contains("Spki", StringComparison.OrdinalIgnoreCase),
        "INI settings contain no TLS private key, PFX, or pin field");
    Check(logs.All(log =>
        !log.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase) &&
        !log.Contains(firstPin, StringComparison.Ordinal)),
        "TLS logging exposes no private key or full SPKI pin");
}
finally
{
    if (Directory.Exists(testRoot))
        Directory.Delete(testRoot, recursive: true);
}

Console.WriteLine($"BridgeApiTlsIdentityHeadlessSmoke: {passed} checks passed.");
