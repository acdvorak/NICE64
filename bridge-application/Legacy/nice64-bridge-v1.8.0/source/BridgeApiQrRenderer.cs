namespace PicoSwitchController;

using System.Drawing;
using QRCoder;

internal sealed class BridgeApiQrRenderResult(
    Bitmap image,
    int moduleCount,
    int pixelsPerModule) : IDisposable
{
    public Bitmap Image { get; } = image ??
        throw new ArgumentNullException(nameof(image));
    public int ModuleCount { get; } = moduleCount;
    public int PixelsPerModule { get; } = pixelsPerModule;
    public int QuietZoneModules => 4;

    public void Dispose() => Image.Dispose();
}

internal sealed class BridgeApiQrRenderer
{
    public const int TargetPixels = 400;
    public const QRCodeGenerator.ECCLevel ErrorCorrection =
        QRCodeGenerator.ECCLevel.M;

    public BridgeApiQrRenderResult Render(string deterministicJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deterministicJson);

        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(
            deterministicJson,
            ErrorCorrection,
            forceUtf8: true,
            utf8BOM: false);
        int moduleCount = data.ModuleMatrix.Count;
        int pixelsPerModule = Math.Max(1, TargetPixels / moduleCount);
        using QRCode qrCode = new(data);
        Bitmap image = qrCode.GetGraphic(
            pixelsPerModule,
            Color.Black,
            Color.White,
            drawQuietZones: true);
        return new BridgeApiQrRenderResult(
            image, moduleCount, pixelsPerModule);
    }
}

internal sealed class BridgeApiPairingQrPresentation : IDisposable
{
    private BridgeApiQrRenderResult? _rendered;

    public bool Visible => _rendered is not null;
    public Bitmap? Image => _rendered?.Image;
    public string? TransientPayload { get; private set; }
    public int PixelsPerModule => _rendered?.PixelsPerModule ?? 0;
    public int QuietZoneModules => _rendered?.QuietZoneModules ?? 0;

    public void Show(
        BridgeApiOnboardingSnapshot snapshot,
        IBridgeApiLocalCredentialProvider credentials,
        BridgeApiOnboardingPayloadBuilder payloadBuilder,
        BridgeApiQrRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(payloadBuilder);
        ArgumentNullException.ThrowIfNull(renderer);
        Clear();

        string token = credentials.RevealCurrentToken();
        string payload = payloadBuilder.BuildJson(snapshot, token);
        BridgeApiQrRenderResult rendered = renderer.Render(payload);
        TransientPayload = payload;
        _rendered = rendered;
    }

    public void Clear()
    {
        _rendered?.Dispose();
        _rendered = null;
        TransientPayload = null;
    }

    public void Dispose() => Clear();
}
