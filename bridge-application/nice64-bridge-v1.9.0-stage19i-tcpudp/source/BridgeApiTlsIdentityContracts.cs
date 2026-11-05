namespace PicoSwitchController;

using System.Security.Cryptography.X509Certificates;

public sealed class BridgeApiTlsIdentitySnapshot
{
    internal BridgeApiTlsIdentitySnapshot(
        X509Certificate2 certificate,
        string spkiSha256Base64,
        string certificateSha256Fingerprint)
    {
        Certificate = certificate;
        SpkiSha256Base64 = spkiSha256Base64;
        CertificateSha256Fingerprint = certificateSha256Fingerprint;
        Thumbprint = certificate.Thumbprint;
        NotBeforeUtc = certificate.NotBefore.ToUniversalTime();
        NotAfterUtc = certificate.NotAfter.ToUniversalTime();
    }

    internal X509Certificate2 Certificate { get; }
    public string Thumbprint { get; }
    public string SpkiSha256Base64 { get; }
    public string CertificateSha256Fingerprint { get; }
    public DateTime NotBeforeUtc { get; }
    public DateTime NotAfterUtc { get; }
}

internal sealed record BridgeApiTlsIdentityResult(
    bool Success,
    BridgeApiTlsIdentitySnapshot? Identity)
{
    public static BridgeApiTlsIdentityResult Available(
        BridgeApiTlsIdentitySnapshot identity) => new(true, identity);
    public static BridgeApiTlsIdentityResult Unavailable { get; } =
        new(false, null);
}

internal interface IBridgeApiTlsIdentityProvider : IDisposable
{
    BridgeApiTlsIdentityResult GetOrCreate();
}
