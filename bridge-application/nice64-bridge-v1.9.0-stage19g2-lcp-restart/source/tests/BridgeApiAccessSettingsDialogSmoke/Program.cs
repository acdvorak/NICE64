int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

string root = FindSourceRoot();
string dialog = File.ReadAllText(Path.Combine(
    root, "BridgeApiAccessSettingsDialog.cs"));
string presentation = File.ReadAllText(Path.Combine(
    root, "BridgeApiAccessSettingsPresentation.cs"));
string form = File.ReadAllText(Path.Combine(root, "Form1.cs"));
string settings = File.ReadAllText(Path.Combine(root, "BridgeSettings.cs"));
string accessMode = File.ReadAllText(Path.Combine(root, "BridgeApiAccessMode.cs"));
string lifecycle = File.ReadAllText(Path.Combine(root, "BridgeApiAccessLifecycle.cs"));
string host = File.ReadAllText(Path.Combine(root, "BridgeApiHost.cs"));
string onboarding = File.ReadAllText(Path.Combine(
    root, "BridgeApiOnboardingDialog.cs"));

Check(dialog.Contains("Text = \"Off\"", StringComparison.Ordinal) &&
    dialog.Contains("Text = \"Local Only\"", StringComparison.Ordinal) &&
    dialog.Contains("Text = \"Local Network\"", StringComparison.Ordinal),
    "dialog presents exactly the three frozen user-facing modes");
Check(dialog.Contains("NumericUpDown", StringComparison.Ordinal) &&
    dialog.Contains("Minimum = 1", StringComparison.Ordinal) &&
    dialog.Contains("Maximum = 65535", StringComparison.Ordinal),
    "API port is explicit and bounded in the UI");
Check(dialog.Contains("Network Interface", StringComparison.Ordinal) &&
    dialog.Contains("IPv4 Address", StringComparison.Ordinal) &&
    dialog.Contains("DropDownStyle = ComboBoxStyle.DropDownList",
        StringComparison.Ordinal),
    "exact interface and IPv4 are explicit bounded selectors");
Check(dialog.Contains("_interfaces.Enabled = enabled", StringComparison.Ordinal) &&
    dialog.Contains("_addresses.Enabled = enabled", StringComparison.Ordinal) &&
    dialog.Contains("_localNetwork.Checked && !_applying",
        StringComparison.Ordinal),
    "LAN selectors enable only for Local Network outside Apply");
Check(dialog.Contains("LocalNetworkSecurityNote", StringComparison.Ordinal) &&
    dialog.Contains("HTTPS on the selected LAN address", StringComparison.Ordinal) &&
    dialog.Contains("bearer token and pinned Bridge certificate",
        StringComparison.Ordinal),
    "Local Network security explanation is explicit and bounded");
Check(dialog.Contains("DesiredSummary", StringComparison.Ordinal) &&
    dialog.Contains("ActualSummary", StringComparison.Ordinal) &&
    dialog.Contains("ActualEndpoint", StringComparison.Ordinal) &&
    dialog.Contains("BoundedReason", StringComparison.Ordinal),
    "desired configuration and authoritative runtime state are displayed separately");
Check(dialog.Contains("Text = \"Apply\"", StringComparison.Ordinal) &&
    dialog.Contains("_apply.Click += async", StringComparison.Ordinal) &&
    Count(dialog, "_coordinator.ApplyAsync") == 1,
    "only explicit Apply invokes the lifecycle coordinator");
Check(dialog.Contains("DialogResult = DialogResult.Cancel",
        StringComparison.Ordinal) &&
    dialog.Contains("CancelButton = cancel", StringComparison.Ordinal) &&
    !dialog.Contains("FormClosing +=", StringComparison.Ordinal) &&
    !dialog.Contains("FormClosed +=", StringComparison.Ordinal),
    "Cancel and window close have no persistence or lifecycle mutation handler");
Check(dialog.Contains("if (_applying) return", StringComparison.Ordinal) &&
    dialog.Contains("_apply.Enabled = false", StringComparison.Ordinal) &&
    dialog.Contains("Applying API settings...", StringComparison.Ordinal) &&
    dialog.Contains("_apply.Enabled = true", StringComparison.Ordinal),
    "Apply reentrancy is blocked with bounded progress presentation");
Check(!dialog.Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal) &&
    !dialog.Contains("Task.Run", StringComparison.Ordinal),
    "WinForms Apply uses normal async flow without blocking or fake workers");
Check(dialog.Contains("BridgeApiOnboardingDialog", StringComparison.Ordinal) &&
    dialog.Contains("refreshed.Onboarding", StringComparison.Ordinal) &&
    dialog.Contains("Pairing / Credentials...", StringComparison.Ordinal),
    "settings surface reuses authoritative frozen onboarding presentation");
Check(dialog.Contains("Bearer token remains hidden until explicitly revealed.",
        StringComparison.Ordinal) &&
    !dialog.Contains("RevealCurrentToken", StringComparison.Ordinal) &&
    !dialog.Contains("Clipboard.SetText", StringComparison.Ordinal),
    "settings dialog does not reveal or copy token implicitly");
Check(dialog.Contains("Local Network mode is required for mobile pairing.",
        StringComparison.Ordinal) &&
    dialog.Contains("API is disabled.", StringComparison.Ordinal),
    "Off and LocalOnly onboarding unavailability is explained");
Check(form.Contains("Text = \"Mobile / API...\"", StringComparison.Ordinal) &&
    form.Contains("mobileApiMenuItem.Click += (_, _) => ShowMobileApiOnboarding()",
        StringComparison.Ordinal) &&
    form.Contains("using BridgeApiAccessSettingsDialog dialog",
        StringComparison.Ordinal),
    "existing Settings Mobile/API entry opens the evolved configuration surface");
Check(form.Contains("BridgeSettings.Load(_settingsPath)", StringComparison.Ordinal) &&
    form.Contains("BridgeSettings.SaveApiAccessConfiguration(",
        StringComparison.Ordinal) &&
    form.Contains("BridgeApiAccessSettingsResolver.Resolve(",
        StringComparison.Ordinal),
    "dialog reloads and persists existing settings through frozen resolver semantics");
Check(form.Contains("new BridgeApiNetworkInterfaceCatalog()",
        StringComparison.Ordinal) ||
    form.Contains("BridgeApiNetworkInterfaceCatalog catalog = new()",
        StringComparison.Ordinal),
    "UI uses the existing network-interface catalog");
Check(form.Contains("BridgeApiLanAddressValidator validator = new(catalog)",
        StringComparison.Ordinal),
    "UI validation reuses the frozen exact-interface validator");

Check(EnumDeclarations(accessMode).SequenceEqual(
        new[] { "Off", "LocalOnly", "LocalNetwork" }),
    "frozen access enum still contains exactly three modes");
Check(settings.Contains("ApiSelectedInterfaceId", StringComparison.Ordinal) &&
    settings.Contains("ApiSelectedIpv4Address", StringComparison.Ordinal) &&
    settings.Contains("ApiSelectedInterfaceName", StringComparison.Ordinal) &&
    !presentation.Contains("WriteAll", StringComparison.Ordinal),
    "existing settings fields remain the only persistence authority");
Check(lifecycle.Contains("LocalNetworkHttps", StringComparison.Ordinal) &&
    lifecycle.Contains("UnavailableCertificate", StringComparison.Ordinal) &&
    lifecycle.Contains("StartLocalOnlyAsync", StringComparison.Ordinal) &&
    !presentation.Contains("LocalNetwork(", StringComparison.Ordinal),
    "settings UI preserves HTTPS LocalNetwork and frozen secure degradation");
Check(host.Contains("BridgeApiListenerSecurity.Http", StringComparison.Ordinal) &&
    host.Contains("endpoint.UseHttps", StringComparison.Ordinal) &&
    host.Contains("Authorized(context, token)", StringComparison.Ordinal),
    "loopback HTTP, LAN HTTPS, and bearer middleware remain present");
Check(onboarding.Contains("Enabled = snapshot.Available && snapshot.TokenAvailable",
        StringComparison.Ordinal) &&
    onboarding.Contains("Show Pairing QR", StringComparison.Ordinal),
    "QR stays explicit and available only for healthy authoritative onboarding");
Check(!dialog.Contains("/api/", StringComparison.Ordinal) &&
    !presentation.Contains("/api/", StringComparison.Ordinal),
    "D6C adds no API route");
Check(!dialog.Contains("Firewall", StringComparison.OrdinalIgnoreCase) &&
    !presentation.Contains("Firewall", StringComparison.OrdinalIgnoreCase) &&
    !dialog.Contains("netsh", StringComparison.OrdinalIgnoreCase) &&
    !presentation.Contains("netsh", StringComparison.OrdinalIgnoreCase),
    "D6C adds no firewall automation");
Check(!dialog.Contains("NetworkAddressChanged", StringComparison.Ordinal) &&
    !presentation.Contains("NetworkAddressChanged", StringComparison.Ordinal) &&
    !dialog.Contains("Timer", StringComparison.Ordinal) &&
    !presentation.Contains("Timer", StringComparison.Ordinal),
    "D6C adds no address observer or polling timer");
Check(!dialog.Contains("mDNS", StringComparison.OrdinalIgnoreCase) &&
    !presentation.Contains("mDNS", StringComparison.OrdinalIgnoreCase) &&
    !dialog.Contains("Android", StringComparison.OrdinalIgnoreCase) &&
    !presentation.Contains("Android", StringComparison.OrdinalIgnoreCase),
    "D6C adds no discovery or Android implementation");
Check(!dialog.Contains("BridgeConnectionService", StringComparison.Ordinal) &&
    !dialog.Contains("BridgeFirmwareService", StringComparison.Ordinal) &&
    !dialog.Contains("BridgeAccessoryService", StringComparison.Ordinal) &&
    !dialog.Contains("BridgeControllerOwnership", StringComparison.Ordinal),
    "dialog has no Pico, firmware, accessory, or ownership dependency");

Console.WriteLine($"BridgeApiAccessSettingsDialogSmoke: {passed} checks passed.");

static int Count(string source, string value)
{
    int count = 0;
    for (int offset = 0;
        (offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0;
        offset += value.Length) count++;
    return count;
}

static IReadOnlyList<string> EnumDeclarations(string source)
{
    int start = source.IndexOf("public enum BridgeApiAccessMode",
        StringComparison.Ordinal);
    int open = source.IndexOf('{', start);
    int close = source.IndexOf('}', open);
    return source[(open + 1)..close]
        .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
}

static string FindSourceRoot()
{
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
        directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Form1.cs")))
            return directory.FullName;
    }
    throw new DirectoryNotFoundException();
}
