namespace PicoSwitchController;

public enum BridgeApiOnboardingUnavailableReason
{
    None,
    ApiOff,
    LocalOnly,
    LanNotListening,
    InsecureEndpoint,
    TlsIdentityUnavailable,
    TokenUnavailable
}

public sealed record BridgeApiOnboardingSnapshot(
    bool Available,
    BridgeApiOnboardingUnavailableReason UnavailableReason,
    string ApiVersion,
    BridgeApiAccessMode AccessMode,
    string? Endpoint,
    string Protocol,
    string AuthenticationScheme,
    string? SpkiSha256Base64,
    string? CertificateSha256Fingerprint,
    DateTime? CertificateNotAfterUtc,
    bool TokenAvailable,
    bool QrAvailable);

public interface IBridgeApiOnboardingProvider
{
    BridgeApiOnboardingSnapshot GetCurrent();
}

internal sealed class BridgeApiOnboardingProvider(
    Func<BridgeApiAccessRuntimeState> runtimeState,
    IBridgeApiTlsIdentityProvider tlsIdentity,
    IBridgeApiLocalCredentialProvider credentials) :
    IBridgeApiOnboardingProvider
{
    public const string ApiVersion = "v1";
    public const string Protocol = "https";
    public const string AuthenticationScheme = "Bearer";

    private readonly Func<BridgeApiAccessRuntimeState> _runtimeState =
        runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
    private readonly IBridgeApiTlsIdentityProvider _tlsIdentity =
        tlsIdentity ?? throw new ArgumentNullException(nameof(tlsIdentity));
    private readonly IBridgeApiLocalCredentialProvider _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    public BridgeApiOnboardingSnapshot GetCurrent()
    {
        BridgeApiAccessRuntimeState runtime = _runtimeState();
        BridgeApiAccessMode mode = runtime.Desired.AccessMode;
        if (mode == BridgeApiAccessMode.Off)
            return Unavailable(mode, BridgeApiOnboardingUnavailableReason.ApiOff);
        if (mode != BridgeApiAccessMode.LocalNetwork)
            return Unavailable(mode, BridgeApiOnboardingUnavailableReason.LocalOnly);
        if (runtime.LanStatus != BridgeApiLanRuntimeStatus.Listening ||
            string.IsNullOrWhiteSpace(runtime.LanEndpoint))
            return Unavailable(mode,
                BridgeApiOnboardingUnavailableReason.LanNotListening);
        if (!Uri.TryCreate(runtime.LanEndpoint, UriKind.Absolute, out Uri? endpoint) ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
            return Unavailable(mode,
                BridgeApiOnboardingUnavailableReason.InsecureEndpoint);

        BridgeApiTlsIdentityResult tls = _tlsIdentity.GetOrCreate();
        if (!tls.Success || tls.Identity is null)
            return Unavailable(mode,
                BridgeApiOnboardingUnavailableReason.TlsIdentityUnavailable);
        bool tokenAvailable = _credentials.IsAvailable();
        if (!tokenAvailable)
            return Unavailable(mode,
                BridgeApiOnboardingUnavailableReason.TokenUnavailable);

        BridgeApiTlsIdentitySnapshot identity = tls.Identity;
        return new(true, BridgeApiOnboardingUnavailableReason.None,
            ApiVersion, mode, runtime.LanEndpoint, Protocol,
            AuthenticationScheme, identity.SpkiSha256Base64,
            identity.CertificateSha256Fingerprint,
            identity.NotAfterUtc, true, false);
    }

    private static BridgeApiOnboardingSnapshot Unavailable(
        BridgeApiAccessMode mode,
        BridgeApiOnboardingUnavailableReason reason) => new(
            false, reason, ApiVersion, mode, null, Protocol,
            AuthenticationScheme, null, null, null, false, false);
}

internal interface IBridgeApiLocalCredentialProvider
{
    bool IsAvailable();
    string RevealCurrentToken();
}

internal sealed class BridgeApiLocalCredentialProvider(
    IBridgeApiTokenProvider tokenProvider) : IBridgeApiLocalCredentialProvider
{
    private readonly IBridgeApiTokenProvider _tokenProvider =
        tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));

    public bool IsAvailable()
    {
        try { return _tokenProvider.GetToken().Length >= 32; }
        catch { return false; }
    }

    public string RevealCurrentToken()
    {
        string token = _tokenProvider.GetToken();
        if (token.Length < 32)
            throw new InvalidDataException(
                "The protected local API token is invalid.");
        return token;
    }
}

internal sealed class BridgeApiOnboardingPresentation : IDisposable
{
    public string? VisibleToken { get; private set; }
    public string? VisiblePayload { get; private set; }
    public bool TokenRevealed => VisibleToken is not null;

    public string RevealToken(IBridgeApiLocalCredentialProvider credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return VisibleToken = credentials.RevealCurrentToken();
    }

    public string BuildPayload(
        BridgeApiOnboardingSnapshot snapshot,
        BridgeApiOnboardingPayloadBuilder builder)
    {
        if (VisibleToken is null)
            throw new InvalidOperationException(
                "The token must be deliberately revealed first.");
        ArgumentNullException.ThrowIfNull(builder);
        return VisiblePayload = builder.BuildJson(snapshot, VisibleToken);
    }

    public string GetTokenForExplicitCopy() => VisibleToken ??
        throw new InvalidOperationException(
            "The token must be deliberately revealed before copying.");

    public void Clear()
    {
        VisibleToken = null;
        VisiblePayload = null;
    }

    public void Dispose() => Clear();
}
