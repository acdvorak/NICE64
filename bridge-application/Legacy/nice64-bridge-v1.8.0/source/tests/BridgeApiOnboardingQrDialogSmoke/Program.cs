int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

string sourceRoot = FindSourceDirectory();
string dialog = File.ReadAllText(Path.Combine(
    sourceRoot, "BridgeApiOnboardingDialog.cs"));
string renderer = File.ReadAllText(Path.Combine(
    sourceRoot, "BridgeApiQrRenderer.cs"));

Check(dialog.Contains("_qrBox.Visible = false", StringComparison.Ordinal),
    "QR is hidden by default");
Check(dialog.Contains("Text = \"Show Pairing QR\"", StringComparison.Ordinal) &&
    dialog.Contains("qrButton.Click += (_, _) => ShowPairingQr()",
        StringComparison.Ordinal),
    "explicit local pairing action is required");
Check(dialog.Contains("PairingQrWarning", StringComparison.Ordinal) &&
    dialog.Contains("Anyone who scans or photographs it",
        StringComparison.Ordinal) &&
    dialog.Contains("MessageBoxDefaultButton.Button2", StringComparison.Ordinal),
    "credential warning appears with negative default before exposure");
int hideIndex = dialog.IndexOf("HidePairingQr();", dialog.IndexOf(
    "private void ShowPairingQr", StringComparison.Ordinal),
    StringComparison.Ordinal);
int confirmIndex = dialog.IndexOf("MessageBox.Show", dialog.IndexOf(
    "private void ShowPairingQr", StringComparison.Ordinal),
    StringComparison.Ordinal);
Check(hideIndex >= 0 && hideIndex < confirmIndex &&
    dialog.Contains("if (confirmation != DialogResult.Yes) return;",
        StringComparison.Ordinal),
    "cancellation leaves pairing QR hidden");
Check(dialog.Contains("Enabled = snapshot.Available && snapshot.TokenAvailable",
        StringComparison.Ordinal),
    "unavailable or degraded onboarding cannot request QR");
Check(dialog.Contains("_qrPresentation.Show(\n                _snapshot, _credentials, _payloadBuilder, _qrRenderer)",
        StringComparison.Ordinal),
    "healthy pairing uses frozen snapshot, credential, and payload builder");
Check(dialog.Contains("_qrBox.Image = _qrPresentation.Image",
        StringComparison.Ordinal) &&
    dialog.Contains("_qrBox.Visible = true", StringComparison.Ordinal),
    "successful explicit action displays the in-memory QR");
Check(dialog.Contains("_qrBox.Image = null", StringComparison.Ordinal) &&
    dialog.Contains("_qrPresentation.Clear()", StringComparison.Ordinal) &&
    dialog.Contains("_qrPresentation.Dispose()", StringComparison.Ordinal),
    "close path removes and disposes QR resources");
Check(!dialog.Contains("Clipboard.SetImage", StringComparison.Ordinal) &&
    !dialog.Contains("SaveFileDialog", StringComparison.Ordinal) &&
    !dialog.Contains("Image.Save", StringComparison.Ordinal) &&
    !dialog.Contains("File.Write", StringComparison.Ordinal),
    "QR is neither copied, exported, nor persisted");
int constructorStart = dialog.IndexOf("internal BridgeApiOnboardingDialog(",
    StringComparison.Ordinal);
int constructorEnd = dialog.IndexOf("private static void AddValue",
    constructorStart, StringComparison.Ordinal);
string constructor = dialog[constructorStart..constructorEnd];
Check(!constructor.Contains("_qrPresentation.Show(", StringComparison.Ordinal) &&
    !constructor.Contains("OnShown", StringComparison.Ordinal),
    "dialog open does not generate QR");
Check(!renderer.Contains("File.", StringComparison.Ordinal) &&
    !renderer.Contains("Clipboard", StringComparison.Ordinal) &&
    !renderer.Contains("Log", StringComparison.Ordinal),
    "QR payload is not persisted, copied, or logged by renderer");
Check(!dialog.Contains("/api/v1/", StringComparison.Ordinal) &&
    !renderer.Contains("/api/v1/", StringComparison.Ordinal),
    "pairing action is local desktop presentation only");

Console.WriteLine($"BridgeApiOnboardingQrDialogSmoke: {passed} checks passed.");

static string FindSourceDirectory()
{
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
        directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName,
                "BridgeApiOnboardingDialog.cs"))) return directory.FullName;
    }
    throw new DirectoryNotFoundException();
}
