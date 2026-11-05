using System.Drawing;
using System.Text.Json;
using PicoSwitchController;
using QRCoder;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

string pin = Convert.ToBase64String(Enumerable.Range(0, 32)
    .Select(value => (byte)value).ToArray());
string fingerprint = new('A', 64);
string token = "current-explicit-bearer-token-1234567890";
BridgeApiOnboardingSnapshot snapshot = new(
    true, BridgeApiOnboardingUnavailableReason.None, "v1",
    BridgeApiAccessMode.LocalNetwork, "https://192.168.1.20:6470",
    "https", "Bearer", pin, fingerprint, DateTime.UtcNow.AddYears(1),
    true, false);
BridgeApiOnboardingPayloadBuilder builder = new();
string json = builder.BuildJson(snapshot, token);
string[] filesBefore = Directory.GetFiles(AppContext.BaseDirectory);
BridgeApiQrRenderer renderer = new();
using BridgeApiQrRenderResult first = renderer.Render(json);
using BridgeApiQrRenderResult second = renderer.Render(json);

Check(BridgeApiOnboardingPayloadBuilder.Schema ==
    "nice64-bridge-onboarding", "frozen schema is unchanged");
Check(BridgeApiOnboardingPayloadBuilder.Version == 1,
    "frozen version is unchanged");
using (JsonDocument document = JsonDocument.Parse(json))
{
    JsonElement root = document.RootElement;
    Check(root.GetProperty("apiVersion").GetString() == "v1",
        "API version remains v1");
    Check(root.GetProperty("url").GetString() == snapshot.Endpoint,
        "authoritative HTTPS endpoint is preserved");
    Check(root.GetProperty("token").GetString() == token,
        "current explicit bearer credential is preserved");
    Check(root.GetProperty("spkiSha256").GetString() == pin,
        "SPKI pin is unchanged");
    Check(root.GetProperty("certificateSha256").GetString() == fingerprint,
        "certificate fingerprint is unchanged");
    Check(root.EnumerateObject().Select(property => property.Name).SequenceEqual(
        ["schema", "version", "apiVersion", "url", "token",
         "spkiSha256", "certificateSha256"]),
        "no frozen payload field is added, removed, or reordered");
}
Check(!json.Contains('\n') && !json.Contains('\r') && !json.Contains("  "),
    "JSON remains compact");
Check(first.Image.Width == first.Image.Height && first.Image.Width > 0,
    "rendered QR is nonempty and square");
Check(first.Image.Width is >= 300 and <= 450,
    "rendered QR is phone-scannable desktop size");
Check(first.PixelsPerModule > 0 &&
    first.Image.Width % first.PixelsPerModule == 0,
    "integer module scaling is used");
Check(first.QuietZoneModules == 4 &&
    HasQuietZone(first.Image, first.PixelsPerModule * 4),
    "four-module white quiet zone is preserved");
Check(HasCrispModules(first.Image, first.PixelsPerModule),
    "module blocks are rendered without interpolation blur");
Check(BitmapsEqual(first.Image, second.Image),
    "same payload produces equivalent QR module content");
Check(BridgeApiQrRenderer.ErrorCorrection == QRCodeGenerator.ECCLevel.M,
    "error correction level is M");
Check(Directory.GetFiles(AppContext.BaseDirectory).SequenceEqual(filesBefore),
    "renderer creates no disk file");

FakeCredentials credentials = new(token);
BridgeApiPairingQrPresentation presentation = new();
Check(!presentation.Visible && presentation.Image is null &&
    credentials.RevealCount == 0,
    "pairing presentation is hidden and does not retrieve token by default");
presentation.Show(snapshot, credentials, builder, renderer);
Check(presentation.Visible && presentation.Image is not null &&
    credentials.RevealCount == 1,
    "explicit pairing action retrieves the credential and renders QR");
Check(presentation.TransientPayload == json,
    "QR source is the exact frozen deterministic JSON string");
Bitmap displayed = presentation.Image!;
presentation.Dispose();
Check(!presentation.Visible && presentation.Image is null &&
    presentation.TransientPayload is null,
    "close/dispose clears transient payload and QR presentation state");
bool disposed = false;
try { _ = displayed.GetPixel(0, 0); }
catch (ArgumentException) { disposed = true; }
Check(disposed, "close/dispose releases the rendered bitmap");

string rendererSource = File.ReadAllText(Path.Combine(
    FindSourceDirectory(), "BridgeApiQrRenderer.cs"));
Check(!rendererSource.Contains("File.", StringComparison.Ordinal) &&
    !rendererSource.Contains("Http", StringComparison.Ordinal) &&
    !rendererSource.Contains("Socket", StringComparison.Ordinal),
    "renderer has no filesystem, network, or API dependency");

Console.WriteLine($"BridgeApiQrRendererHeadlessSmoke: {passed} checks passed.");

static bool HasQuietZone(Bitmap bitmap, int requiredPixels)
{
    int minX = bitmap.Width;
    int minY = bitmap.Height;
    int maxX = -1;
    int maxY = -1;
    for (int y = 0; y < bitmap.Height; y++)
    for (int x = 0; x < bitmap.Width; x++)
    {
        if (bitmap.GetPixel(x, y).ToArgb() == Color.Black.ToArgb())
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
    }
    return minX >= requiredPixels && minY >= requiredPixels &&
        bitmap.Width - 1 - maxX >= requiredPixels &&
        bitmap.Height - 1 - maxY >= requiredPixels;
}

static bool HasCrispModules(Bitmap bitmap, int scale)
{
    for (int y = 0; y < bitmap.Height; y += scale)
    for (int x = 0; x < bitmap.Width; x += scale)
    {
        int expected = bitmap.GetPixel(x, y).ToArgb();
        for (int yy = y; yy < y + scale; yy++)
        for (int xx = x; xx < x + scale; xx++)
            if (bitmap.GetPixel(xx, yy).ToArgb() != expected) return false;
    }
    return true;
}

static bool BitmapsEqual(Bitmap left, Bitmap right)
{
    if (left.Size != right.Size) return false;
    for (int y = 0; y < left.Height; y++)
    for (int x = 0; x < left.Width; x++)
        if (left.GetPixel(x, y) != right.GetPixel(x, y)) return false;
    return true;
}

static string FindSourceDirectory()
{
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
        directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName,
                "BridgeApiQrRenderer.cs"))) return directory.FullName;
    }
    throw new DirectoryNotFoundException();
}

sealed class FakeCredentials(string token) : IBridgeApiLocalCredentialProvider
{
    public int RevealCount { get; private set; }
    public bool IsAvailable() => true;
    public string RevealCurrentToken()
    {
        RevealCount++;
        return token;
    }
}
