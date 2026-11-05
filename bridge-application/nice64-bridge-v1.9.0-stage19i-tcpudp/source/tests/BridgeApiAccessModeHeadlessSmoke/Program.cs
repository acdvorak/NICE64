using System.Reflection;
using PicoSwitchController;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

BridgeApiAccessConfiguration defaults = BridgeApiAccessConfiguration.Default;
Check(defaults.AccessMode == BridgeApiAccessMode.Off &&
    defaults.Port == 6470 && !defaults.RequiresLanSelection,
    "default access mode is Off on port 6470");
Check(Enum.GetValues<BridgeApiAccessMode>().SequenceEqual(
    [BridgeApiAccessMode.Off, BridgeApiAccessMode.LocalOnly,
        BridgeApiAccessMode.LocalNetwork]),
    "exactly three frozen access modes exist");
Check(typeof(BridgeApiAccessConfiguration).GetProperties().All(property =>
        property.Name is not "Enabled" and not "LanEnabled"),
    "configuration has one mode and no independent enable/LAN toggles");

BridgeApiAccessConfiguration off = new(
    BridgeApiAccessMode.Off, 6470, null, null);
BridgeApiAccessConfiguration local = new(
    BridgeApiAccessMode.LocalOnly, 6470, null, null);
BridgeApiAccessConfiguration lanMissingInterface = new(
    BridgeApiAccessMode.LocalNetwork, 6470, null, "192.168.1.20");
BridgeApiAccessConfiguration lanMissingAddress = new(
    BridgeApiAccessMode.LocalNetwork, 6470, "ethernet-id", null);
BridgeApiAccessConfiguration lan = new(
    BridgeApiAccessMode.LocalNetwork, 6470, "ethernet-id",
    "192.168.1.20", "Ethernet Display");
Check(!off.RequiresLanSelection && !off.HasCompleteLanSelection,
    "Off requires no LAN selection");
Check(!local.RequiresLanSelection && !local.HasCompleteLanSelection,
    "LocalOnly requires no LAN selection");
Check(lanMissingInterface.RequiresLanSelection &&
    !lanMissingInterface.HasCompleteLanSelection,
    "LocalNetwork requires interface ID");
Check(lanMissingAddress.RequiresLanSelection &&
    !lanMissingAddress.HasCompleteLanSelection,
    "LocalNetwork requires IPv4 preference");
Check(lan.HasCompleteLanSelection,
    "LocalNetwork recognizes only a complete exact tuple");

BridgeApiAccessResolution persisted =
    BridgeApiAccessSettingsResolver.Resolve(lan, null, null);
Check(persisted.Desired == lan && persisted.Effective == lan &&
    persisted.EffectiveSource == BridgeApiAccessConfigurationSource.Persisted,
    "persisted LocalNetwork remains effective without override");
foreach (string enabled in new[] { "1", "true", "YES", " on " })
{
    BridgeApiAccessResolution resolution =
        BridgeApiAccessSettingsResolver.Resolve(lan, enabled, "6480");
    Check(resolution.Desired == lan &&
        resolution.Effective.AccessMode == BridgeApiAccessMode.LocalOnly &&
        resolution.Effective.Port == 6480 &&
        resolution.Effective.SelectedInterfaceId is null &&
        resolution.Effective.SelectedIpv4Address is null &&
        resolution.EffectiveSource ==
            BridgeApiAccessConfigurationSource.LegacyEnvironment,
        $"legacy enable {enabled.Trim()} maps only to observable LocalOnly");
}
BridgeApiAccessResolution disabledEnvironment =
    BridgeApiAccessSettingsResolver.Resolve(lan, "false", "6480");
Check(disabledEnvironment.Effective.AccessMode == BridgeApiAccessMode.LocalNetwork &&
    disabledEnvironment.EffectiveSource ==
        BridgeApiAccessConfigurationSource.Persisted,
    "legacy disabled value does not become a competing toggle");
BridgeApiAccessResolution defaultLegacyPort =
    BridgeApiAccessSettingsResolver.Resolve(lan, "true", "not-a-port");
Check(defaultLegacyPort.Effective.AccessMode == BridgeApiAccessMode.LocalOnly &&
    defaultLegacyPort.Effective.Port == 6470,
    "legacy enable with malformed port uses legacy-safe default");

BridgeApiAccessRuntimeState degraded =
    BridgeApiAccessRuntimeState.DegradedLocalNetwork(
        lan,
        BridgeApiLanRuntimeStatus.UnavailableSelectedAddress,
        BridgeApiLanValidationFailure.AddressNotAssignedToInterface,
        "http://127.0.0.1:6470");
Check(degraded.Desired == lan && degraded.IsDegraded &&
    degraded.LoopbackStatus == BridgeApiLoopbackRuntimeStatus.Listening &&
    degraded.LanEndpoint is null &&
    degraded.LanFailure ==
        BridgeApiLanValidationFailure.AddressNotAssignedToInterface,
    "degraded LocalNetwork preserves intent and truthful loopback availability");
Check(BridgeApiAccessRuntimeState.NotStarted(off).LoopbackStatus ==
        BridgeApiLoopbackRuntimeStatus.NotRequested &&
    BridgeApiAccessRuntimeState.NotStarted(local).LanStatus ==
        BridgeApiLanRuntimeStatus.NotRequested &&
    BridgeApiAccessRuntimeState.NotStarted(lan).LanStatus ==
        BridgeApiLanRuntimeStatus.Stopped,
    "not-started runtime state distinguishes desired exposure");

string tempDirectory = Path.Combine(Path.GetTempPath(),
    "nice64-d5a-settings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDirectory);
try
{
    string path = Path.Combine(tempDirectory, "picoswitchcontroller.ini");
    foreach (BridgeApiAccessConfiguration configuration in
        new[] { off, local, lan })
    {
        BridgeSettings.SaveApiAccessConfiguration(path, configuration);
        BridgeSettings loaded = BridgeSettings.Load(path);
        Check(loaded.ApiAccessMode == configuration.AccessMode &&
            loaded.ApiPort == configuration.Port &&
            loaded.ApiSelectedInterfaceId == configuration.SelectedInterfaceId &&
            loaded.ApiSelectedIpv4Address == configuration.SelectedIpv4Address &&
            loaded.ApiSelectedInterfaceName == configuration.SelectedInterfaceName,
            $"settings round-trip {configuration.AccessMode}");
    }

    string text = File.ReadAllText(path);
    string[] forbidden =
    [
        "BearerToken", "ApiToken", "CertificatePrivateKey", "PrivateKey",
        "LeaseId", "WireSenderId", "QrPayload"
    ];
    Check(forbidden.All(value => !text.Contains(value,
            StringComparison.OrdinalIgnoreCase)),
        "INI contains no token, private key, lease, sender, or QR state");

    File.WriteAllText(path,
        "[Api]\nApiAccessMode=invalid-mode\nApiPort=6470\n" +
        "ApiSelectedInterfaceId=remembered-id\n" +
        "ApiSelectedIpv4Address=malformed-preference\n");
    BridgeSettings invalid = BridgeSettings.Load(path);
    Check(invalid.ApiAccessMode == BridgeApiAccessMode.Off,
        "invalid persisted mode falls safely to Off");
    Check(invalid.ApiSelectedInterfaceId == "remembered-id" &&
        invalid.ApiSelectedIpv4Address == "malformed-preference",
        "malformed address remains explicit preference without substitution");

    File.WriteAllText(path, "[Api]\nApiPort=6470\n");
    Check(BridgeSettings.Load(path).ApiAccessMode == BridgeApiAccessMode.Off,
        "missing persisted mode falls safely to Off");
}
finally
{
    Directory.Delete(tempDirectory, recursive: true);
}

string sourceDirectory = FindSourceDirectory();
string hostSource = File.ReadAllText(Path.Combine(sourceDirectory,
    "BridgeApiHost.cs"));
Check(hostSource.Contains("kestrel.Listen(listener.Address, listener.Port)",
        StringComparison.Ordinal) &&
    !hostSource.Contains("IPAddress.Any", StringComparison.Ordinal) &&
    !hostSource.Contains("ListenAnyIP", StringComparison.Ordinal) &&
    !hostSource.Contains("0.0.0.0", StringComparison.Ordinal),
    "BridgeApiHost remains explicit-address-only without wildcard binding");
string accessSource = File.ReadAllText(Path.Combine(sourceDirectory,
    "BridgeApiAccessMode.cs")) +
    File.ReadAllText(Path.Combine(sourceDirectory,
        "BridgeApiLanAddressValidator.cs"));
string[] forbiddenDependencies =
[
    "BridgeConnectionService", "BridgeSession", "SerialPort",
    "TcpBridgeTransport", "FirmwareService", "AccessoryService",
    "NetworkPak", "WifiProvisioning", "Kestrel", "MapGet(", "MapPost("
];
Check(forbiddenDependencies.All(value => !accessSource.Contains(value,
        StringComparison.Ordinal)),
    "D5a core has no Pico/session/transport/listener/route dependency");

Console.WriteLine($"BridgeApiAccessModeHeadlessSmoke: {passed} checks passed.");

static string FindSourceDirectory()
{
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
        directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "BridgeApiHost.cs")))
            return directory.FullName;
    }
    throw new DirectoryNotFoundException("Bridge source directory not found.");
}
