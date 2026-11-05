namespace PicoSwitchController;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record BridgeApiOnboardingPayload(
    [property: JsonPropertyName("schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyName("version"), JsonPropertyOrder(1)] int Version,
    [property: JsonPropertyName("apiVersion"), JsonPropertyOrder(2)] string ApiVersion,
    [property: JsonPropertyName("url"), JsonPropertyOrder(3)] string Url,
    [property: JsonPropertyName("token"), JsonPropertyOrder(4)] string Token,
    [property: JsonPropertyName("spkiSha256"), JsonPropertyOrder(5)] string SpkiSha256,
    [property: JsonPropertyName("certificateSha256"), JsonPropertyOrder(6)] string CertificateSha256);

public sealed class BridgeApiOnboardingPayloadBuilder
{
    public const string Schema = "nice64-bridge-onboarding";
    public const int Version = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public BridgeApiOnboardingPayload Build(
        BridgeApiOnboardingSnapshot snapshot,
        string revealedToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Available ||
            !Uri.TryCreate(snapshot.Endpoint, UriKind.Absolute, out Uri? endpoint) ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Onboarding requires an authoritative active HTTPS endpoint.");
        ArgumentException.ThrowIfNullOrWhiteSpace(revealedToken);
        if (string.IsNullOrWhiteSpace(snapshot.SpkiSha256Base64) ||
            !IsSha256Base64(snapshot.SpkiSha256Base64))
            throw new InvalidOperationException("The SPKI pin is invalid.");
        if (string.IsNullOrWhiteSpace(snapshot.CertificateSha256Fingerprint) ||
            snapshot.CertificateSha256Fingerprint.Length != 64 ||
            snapshot.CertificateSha256Fingerprint.Any(character =>
                !char.IsAsciiHexDigitUpper(character)))
            throw new InvalidOperationException(
                "The certificate fingerprint is invalid.");

        return new(Schema, Version, snapshot.ApiVersion, snapshot.Endpoint,
            revealedToken, snapshot.SpkiSha256Base64,
            snapshot.CertificateSha256Fingerprint);
    }

    public string BuildJson(
        BridgeApiOnboardingSnapshot snapshot,
        string revealedToken) => JsonSerializer.Serialize(
            Build(snapshot, revealedToken), JsonOptions);

    public byte[] BuildUtf8(
        BridgeApiOnboardingSnapshot snapshot,
        string revealedToken) => Encoding.UTF8.GetBytes(
            BuildJson(snapshot, revealedToken));

    private static bool IsSha256Base64(string value)
    {
        Span<byte> decoded = stackalloc byte[32];
        return Convert.TryFromBase64String(value, decoded, out int written) &&
            written == 32;
    }
}
