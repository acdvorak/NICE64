namespace PicoSwitchController;

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

internal sealed class BridgeApiTlsIdentityProvider :
    IBridgeApiTlsIdentityProvider
{
    public const int LifetimeYears = 4;
    public const int RenewalThresholdDays = 30;
    public const string SpkiPinEncoding = "SHA-256 / standard Base64";

    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("NICE64.Bridge.ApiTlsIdentity.v1");
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    private readonly object _sync = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string>? _log;
    private readonly List<X509Certificate2> _ownedCertificates = [];
    private BridgeApiTlsIdentitySnapshot? _cached;
    private bool _disposed;

    internal BridgeApiTlsIdentityProvider(
        string path,
        Action<string>? log = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException(
                "A TLS identity-store path is required.", nameof(path))
            : Path.GetFullPath(path);
        _log = log;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public BridgeApiTlsIdentityResult GetOrCreate()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                DateTimeOffset now = _utcNow();
                if (_cached is not null && IsReusable(
                        _cached.Certificate, now))
                    return BridgeApiTlsIdentityResult.Available(_cached);

                if (File.Exists(_path))
                {
                    try
                    {
                        X509Certificate2 loaded = LoadProtectedCertificate();
                        if (IsReusable(loaded, now))
                        {
                            _ownedCertificates.Add(loaded);
                            _cached = Snapshot(loaded);
                            Log($"Bridge API TLS identity reused; expires {_cached.NotAfterUtc:O}.");
                            return BridgeApiTlsIdentityResult.Available(_cached);
                        }
                        loaded.Dispose();
                        Log("Bridge API TLS identity is near expiry; generating a replacement identity.");
                    }
                    catch
                    {
                        Log("Bridge API TLS identity storage is invalid; generating a replacement identity.");
                    }
                }

                X509Certificate2 generated = Generate(now);
                PersistProtectedCertificate(generated);
                _ownedCertificates.Add(generated);
                _cached = Snapshot(generated);
                Log($"Bridge API TLS identity created; expires {_cached.NotAfterUtc:O}.");
                return BridgeApiTlsIdentityResult.Available(_cached);
            }
            catch
            {
                Log("Bridge API TLS identity is unavailable.");
                return BridgeApiTlsIdentityResult.Unavailable;
            }
        }
    }

    private X509Certificate2 LoadProtectedCertificate()
    {
        byte[] protectedBytes = File.ReadAllBytes(_path);
        byte[] pfx = ProtectedData.Unprotect(
            protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return LoadServerCertificate(pfx);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private void PersistProtectedCertificate(X509Certificate2 certificate)
    {
        byte[] pfx = certificate.Export(X509ContentType.Pfx);
        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectedData.Protect(
                pfx, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }

        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException(
                "The TLS identity-store path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static X509Certificate2 Generate(DateTimeOffset now)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new(
            new X500DistinguishedName("CN=NICE64 Bridge API"),
            key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature, true));
        OidCollection usages = new();
        usages.Add(new Oid(ServerAuthenticationOid));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(usages, true));

        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        try
        {
            string machineName = Dns.GetHostName().Trim();
            if (machineName.Length > 0 &&
                !string.Equals(machineName, "localhost",
                    StringComparison.OrdinalIgnoreCase))
                san.AddDnsName(machineName);
        }
        catch
        {
            // localhost and 127.0.0.1 remain deterministic SAN identities.
        }
        request.CertificateExtensions.Add(san.Build(critical: false));

        using X509Certificate2 created = request.CreateSelfSigned(
            now.AddMinutes(-5), now.AddYears(LifetimeYears));
        byte[] pfx = created.Export(X509ContentType.Pfx);
        try
        {
            return LoadServerCertificate(pfx);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private static X509Certificate2 LoadServerCertificate(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(
            pfx, password: null,
            X509KeyStorageFlags.Exportable |
            X509KeyStorageFlags.UserKeySet);

    private static bool IsReusable(
        X509Certificate2 certificate,
        DateTimeOffset now)
    {
        if (!certificate.HasPrivateKey ||
            certificate.NotAfter.ToUniversalTime() <=
                now.UtcDateTime.AddDays(RenewalThresholdDays))
            return false;
        using ECDsa? key = certificate.GetECDsaPrivateKey();
        if (key?.KeySize != 256) return false;
        X509BasicConstraintsExtension? constraints = certificate.Extensions
            .OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        if (constraints?.CertificateAuthority != false) return false;
        X509EnhancedKeyUsageExtension? eku = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return eku is not null && eku.EnhancedKeyUsages
            .Cast<Oid>().Any(oid => oid.Value == ServerAuthenticationOid);
    }

    private static BridgeApiTlsIdentitySnapshot Snapshot(
        X509Certificate2 certificate)
    {
        using ECDsa publicKey = certificate.GetECDsaPublicKey()
            ?? throw new CryptographicException(
                "The TLS identity does not contain an ECDSA public key.");
        string pin = Convert.ToBase64String(SHA256.HashData(
            publicKey.ExportSubjectPublicKeyInfo()));
        string fingerprint = Convert.ToHexString(
            SHA256.HashData(certificate.RawData));
        return new(certificate, pin, fingerprint);
    }

    private void Log(string message)
    {
        try { _log?.Invoke(message); } catch { }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _cached = null;
            foreach (X509Certificate2 certificate in _ownedCertificates)
                certificate.Dispose();
            _ownedCertificates.Clear();
        }
    }
}
