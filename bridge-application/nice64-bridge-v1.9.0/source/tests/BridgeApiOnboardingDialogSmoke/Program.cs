using PicoSwitchController;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

string pin = Convert.ToBase64String(new byte[32]);
string fingerprint = new('A', 64);
string token = "local-deliberate-token-1234567890";
BridgeApiOnboardingSnapshot snapshot = new(
    true, BridgeApiOnboardingUnavailableReason.None, "v1",
    BridgeApiAccessMode.LocalNetwork, "https://192.168.1.20:6470",
    "https", "Bearer", pin, fingerprint, DateTime.UtcNow.AddYears(1),
    true, false);
BridgeApiOnboardingPresentation presentation = new();
FakeCredentials credentials = new(token);
Check(presentation.VisibleToken is null && !presentation.TokenRevealed,
    "token is hidden by default");
Check(credentials.RevealCount == 0,
    "no automatic token reveal occurs");
presentation.RevealToken(credentials);
Check(presentation.TokenRevealed && credentials.RevealCount == 1,
    "token reveal requires explicit presentation action");
Check(presentation.GetTokenForExplicitCopy() == token,
    "copy source exists only after explicit reveal");
presentation.BuildPayload(snapshot, new BridgeApiOnboardingPayloadBuilder());
presentation.Dispose();
Check(presentation.VisibleToken is null &&
    presentation.VisiblePayload is null && !presentation.TokenRevealed,
    "dialog-close model clears token and payload references");

string sourceRoot = FindSourceDirectory();
string dialogSource = File.ReadAllText(Path.Combine(
    sourceRoot, "BridgeApiOnboardingDialog.cs"));
string formSource = File.ReadAllText(Path.Combine(sourceRoot, "Form1.cs"));
Check(dialogSource.Contains("TokenWarning", StringComparison.Ordinal) &&
    dialogSource.Contains("Keep it private", StringComparison.Ordinal) &&
    dialogSource.Contains("MessageBoxButtons.YesNo", StringComparison.Ordinal),
    "token reveal security warning and deliberate confirmation are present");
Check(dialogSource.Contains("Clipboard.SetText", StringComparison.Ordinal) &&
    dialogSource.Contains("Other applications on this computer",
        StringComparison.Ordinal),
    "explicit clipboard action includes clipboard warning");
Check(dialogSource.Contains("SetupPayloadWarning", StringComparison.Ordinal) &&
    dialogSource.Contains("Show it only to devices you trust",
        StringComparison.Ordinal),
    "credential payload security warning is present");
Check(dialogSource.Contains("ClearCredentialPresentation",
        StringComparison.Ordinal) &&
    dialogSource.Contains("_presentation.Dispose()", StringComparison.Ordinal),
    "dialog close/dispose clears credential presentation");
Check(!dialogSource.Contains("SaveApiAccessConfiguration",
        StringComparison.Ordinal) &&
    !dialogSource.Contains("BridgeApiNetworkInterfaceCatalog",
        StringComparison.Ordinal),
    "onboarding dialog has no access-mode or adapter mutation");
Check(!dialogSource.Contains("Firewall", StringComparison.OrdinalIgnoreCase) &&
    !dialogSource.Contains("X509Store", StringComparison.Ordinal),
    "onboarding dialog has no firewall or trust-store UI");
Check(formSource.Contains("Text = \"Mobile / API...\"", StringComparison.Ordinal) &&
    formSource.Contains(
        "mobileApiMenuItem.Click += (_, _) => ShowMobileApiOnboarding()",
        StringComparison.Ordinal),
    "onboarding dialog opens only from explicit local Settings action");
Check(dialogSource.Contains("Show Pairing QR", StringComparison.Ordinal) &&
    dialogSource.Contains("PictureBox", StringComparison.Ordinal) &&
    !dialogSource.Contains("SaveFileDialog", StringComparison.Ordinal),
    "pairing QR is presented locally with no image export");
Check(!dialogSource.Contains("File.Write", StringComparison.Ordinal) &&
    !dialogSource.Contains("LogDebug", StringComparison.Ordinal),
    "token and payload are neither persisted nor logged");

Console.WriteLine(
    $"BridgeApiOnboardingDialogSmoke: {passed} checks passed.");

static string FindSourceDirectory()
{
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
        directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(
                directory.FullName, "BridgeApiOnboardingDialog.cs")))
            return directory.FullName;
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
