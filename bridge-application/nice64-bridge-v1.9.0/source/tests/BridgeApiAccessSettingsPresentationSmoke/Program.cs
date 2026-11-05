using System.Net;
using System.Net.NetworkInformation;
using PicoSwitchController;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

FakeCatalog catalog = new(
[
    Interface("ethernet-id", "Ethernet", OperationalStatus.Up,
        NetworkInterfaceType.Ethernet, "192.168.1.20", "192.168.1.21"),
    Interface("vpn-id", "VPN", OperationalStatus.Up,
        NetworkInterfaceType.Ppp, "10.8.0.4"),
    Interface("down-id", "Down adapter", OperationalStatus.Down,
        NetworkInterfaceType.Wireless80211, "192.168.50.2"),
    Interface("loop-id", "Loopback", OperationalStatus.Up,
        NetworkInterfaceType.Loopback, "127.0.0.1")
]);
BridgeApiLanAddressValidator validator = new(catalog);
BridgeApiAccessSettingsPresentation presentation = new(catalog, validator);
BridgeApiAccessConfiguration desired = new(
    BridgeApiAccessMode.LocalNetwork, 6470,
    "ethernet-id", "192.168.1.21", "Old display metadata");
BridgeApiAccessRuntimeState healthy = new(desired,
    BridgeApiLoopbackRuntimeStatus.Listening,
    BridgeApiLanRuntimeStatus.Listening,
    BridgeApiLanValidationFailure.None,
    "http://127.0.0.1:6470", "https://192.168.1.21:6470");
BridgeApiOnboardingSnapshot healthyOnboarding = Onboarding(
    desired, available: true, "https://192.168.1.21:6470");

BridgeApiAccessSettingsView view = presentation.Compose(
    desired, healthy, healthyOnboarding);
Check(view.Modes.Count == 3 &&
    view.Modes.Select(choice => choice.Mode).SequenceEqual(new[]
    {
        BridgeApiAccessMode.Off,
        BridgeApiAccessMode.LocalOnly,
        BridgeApiAccessMode.LocalNetwork
    }) && view.Modes.Select(choice => choice.Label).SequenceEqual(new[]
    { "Off", "Local Only", "Local Network" }),
    "exactly three frozen access modes and labels are presented");
Check(view.Desired == desired && view.Desired.Port == 6470,
    "persisted desired configuration and port load unchanged");
Check(view.LanSelectionEnabled,
    "LocalNetwork enables exact interface and address selection");
Check(!presentation.Compose(desired with { AccessMode = BridgeApiAccessMode.Off },
        BridgeApiAccessRuntimeState.NotStarted(desired with
            { AccessMode = BridgeApiAccessMode.Off }),
        Onboarding(desired with { AccessMode = BridgeApiAccessMode.Off }, false)).LanSelectionEnabled &&
    !presentation.Compose(desired with { AccessMode = BridgeApiAccessMode.LocalOnly },
        BridgeApiAccessRuntimeState.NotStarted(desired with
            { AccessMode = BridgeApiAccessMode.LocalOnly }),
        Onboarding(desired with { AccessMode = BridgeApiAccessMode.LocalOnly }, false)).LanSelectionEnabled,
    "Off and LocalOnly disable LAN selection controls");

BridgeApiAccessInterfaceChoice ethernet = view.Interfaces.Single(
    choice => choice.Id == "ethernet-id");
Check(ethernet.Name == "Ethernet" && ethernet.Available,
    "interface selection authority is exact NetworkInterface.Id");
Check(ethernet.Addresses.Count == 2 &&
    ethernet.Addresses.Select(item => item.Address).SequenceEqual(
        new[] { "192.168.1.20", "192.168.1.21" }) &&
    ethernet.Addresses.All(item => item.Available),
    "multiple valid IPv4 addresses remain separately selectable");
Check(view.Interfaces.Any(choice => choice.Id == "vpn-id") &&
    view.Interfaces.All(choice => choice.Id != "down-id" && choice.Id != "loop-id"),
    "validator-approved VPN remains available while down and loopback candidates do not");

BridgeApiAccessCandidateResult exact = presentation.CreateCandidate(
    BridgeApiAccessMode.LocalNetwork, "6470", "ethernet-id",
    "192.168.1.20", "forged display name", desired);
Check(exact.Success && exact.Configuration?.SelectedInterfaceId == "ethernet-id" &&
    exact.Configuration.SelectedIpv4Address == "192.168.1.20" &&
    exact.Configuration.SelectedInterfaceName == "Ethernet",
    "exact ID and IPv4 are authoritative while display name is metadata only");

BridgeApiAccessConfiguration unavailableDesired = new(
    BridgeApiAccessMode.LocalNetwork, 6470,
    "missing-id", "192.168.99.9", "Former Ethernet");
BridgeApiAccessSettingsView unavailableView = presentation.Compose(
    unavailableDesired,
    BridgeApiAccessRuntimeState.DegradedLocalNetwork(unavailableDesired,
        BridgeApiLanRuntimeStatus.UnavailableSelectedInterface,
        BridgeApiLanValidationFailure.InterfaceNotFound,
        "http://127.0.0.1:6470"),
    Onboarding(unavailableDesired, false));
BridgeApiAccessInterfaceChoice unavailableChoice =
    unavailableView.Interfaces.Single(choice => choice.Id == "missing-id");
Check(!unavailableChoice.Available &&
    unavailableChoice.Addresses.Single().Address == "192.168.99.9" &&
    !unavailableChoice.Addresses.Single().Available,
    "persisted unavailable LocalNetwork tuple remains visible as unavailable");
Check(unavailableView.Desired.SelectedInterfaceId == "missing-id" &&
    unavailableView.Desired.SelectedIpv4Address == "192.168.99.9" &&
    unavailableView.Interfaces.First().Id == "missing-id",
    "unavailable desired tuple is retained without automatic substitution");
Check(unavailableView.Runtime.IsDegraded &&
    unavailableView.ActualSummary.Contains("Degraded") &&
    unavailableView.BoundedReason?.Contains("interface") == true &&
    !unavailableView.PairingReady,
    "desired LocalNetwork and actual degraded state remain distinct");

foreach ((BridgeApiAccessMode mode, bool success) in new[]
{
    (BridgeApiAccessMode.Off, true),
    (BridgeApiAccessMode.LocalOnly, true),
    (BridgeApiAccessMode.LocalNetwork, true)
})
{
    BridgeApiAccessCandidateResult result = presentation.CreateCandidate(
        mode, "6470", "ethernet-id", "192.168.1.20", "metadata", desired);
    Check(result.Success == success, $"valid {mode} configuration is accepted");
}

foreach ((BridgeApiAccessCandidateResult Result, string Code, string Name) item in new[]
{
    (presentation.CreateCandidate(BridgeApiAccessMode.LocalOnly, "bad", null, null, null, desired),
        "invalid-port", "malformed port"),
    (presentation.CreateCandidate(BridgeApiAccessMode.Off, "0", null, null, null, desired),
        "invalid-port", "out-of-range port"),
    (presentation.CreateCandidate(BridgeApiAccessMode.LocalOnly, "6464", null, null, null, desired),
        "reserved-port", "management port"),
    (presentation.CreateCandidate(BridgeApiAccessMode.LocalOnly, "6465", null, null, null, desired),
        "reserved-port", "controller/RUDP port"),
    (presentation.CreateCandidate(BridgeApiAccessMode.LocalNetwork, "6470", null, "192.168.1.20", null, desired),
        "missing-interface", "missing interface"),
    (presentation.CreateCandidate(BridgeApiAccessMode.LocalNetwork, "6470", "ethernet-id", null, null, desired),
        "missing-address", "missing address"),
    (presentation.CreateCandidate(BridgeApiAccessMode.LocalNetwork, "6470", "ethernet-id", "192.168.2.9", null, desired),
        "address-not-assigned", "wrong address for interface"),
    (presentation.CreateCandidate(BridgeApiAccessMode.LocalNetwork, "6470", "down-id", "192.168.50.2", null, desired),
        "interface-unavailable", "down interface"),
    (presentation.CreateCandidate(BridgeApiAccessMode.LocalNetwork, "6470", "ethernet-id", "not-an-ip", null, desired),
        "invalid-address", "malformed IPv4"),
    (presentation.CreateCandidate(BridgeApiAccessMode.LocalNetwork, "6470", "ethernet-id", "0.0.0.0", null, desired),
        "unsupported-address", "wildcard IPv4"),
    (presentation.CreateCandidate(BridgeApiAccessMode.LocalNetwork, "6470", "loop-id", "127.0.0.1", null, desired),
        "interface-unavailable", "loopback LAN selection")
})
{
    Check(!item.Result.Success && item.Result.Code == item.Code,
        $"validation rejects {item.Name} with bounded code");
}

FakeLifecycle lifecycle = new(BridgeApiAccessRuntimeState.NotStarted(
    BridgeApiAccessConfiguration.Default));
FakeOnboardingProvider onboarding = new(lifecycle);
FakePersistence persistence = new();
BridgeApiAccessSettingsCoordinator coordinator = new(
    lifecycle, onboarding, persistence, presentation,
    configuration => new(configuration, configuration,
        BridgeApiAccessConfigurationSource.Persisted));
Check(lifecycle.ApplyCalls == 0 && persistence.SaveCalls == 0,
    "opening/capturing settings performs no implicit apply or persistence");
_ = coordinator.Capture(desired);
Check(lifecycle.ApplyCalls == 0 && persistence.SaveCalls == 0,
    "presentation changes do not restart the API host");

foreach (BridgeApiAccessMode mode in new[]
{
    BridgeApiAccessMode.Off,
    BridgeApiAccessMode.LocalOnly,
    BridgeApiAccessMode.LocalNetwork
})
{
    BridgeApiAccessConfiguration candidate = exact.Configuration! with
        { AccessMode = mode };
    lifecycle.Next = RuntimeFor(candidate);
    int before = lifecycle.ApplyCalls;
    BridgeApiAccessSettingsApplyResult applied = await coordinator.ApplyAsync(candidate);
    Check(applied.Applied && lifecycle.ApplyCalls == before + 1,
        $"explicit Apply {mode} invokes lifecycle exactly once");
    Check(persistence.Last == candidate &&
        persistence.Last?.SelectedInterfaceId == "ethernet-id" &&
        persistence.Last.SelectedIpv4Address == "192.168.1.20" &&
        persistence.Last.SelectedInterfaceName == "Ethernet",
        $"{mode} persists mode, port, exact tuple, and metadata without clearing dormant LAN intent");
}

BridgeApiAccessConfiguration localNetwork = exact.Configuration!;
lifecycle.Next = RuntimeFor(localNetwork);
int onboardingBefore = onboarding.Calls;
BridgeApiAccessSettingsApplyResult healthyApplied =
    await coordinator.ApplyAsync(localNetwork);
Check(healthyApplied.Applied &&
    healthyApplied.Runtime.LanStatus == BridgeApiLanRuntimeStatus.Listening &&
    healthyApplied.Onboarding.Available && onboarding.Calls > onboardingBefore,
    "successful Apply refreshes authoritative runtime and onboarding state");

lifecycle.BlockNext = true;
lifecycle.Next = RuntimeFor(localNetwork);
Task<BridgeApiAccessSettingsApplyResult> first = coordinator.ApplyAsync(localNetwork);
await lifecycle.Entered.Task;
BridgeApiAccessSettingsApplyResult duplicate = await coordinator.ApplyAsync(localNetwork);
Check(duplicate.Busy && !duplicate.Applied,
    "duplicate Apply is blocked while the first lifecycle apply is active");
lifecycle.Release();
Check((await first).Applied,
    "original serialized Apply completes after duplicate rejection");

BridgeApiAccessRuntimeState degraded = new(localNetwork,
    BridgeApiLoopbackRuntimeStatus.Listening,
    BridgeApiLanRuntimeStatus.UnavailableCertificate,
    BridgeApiLanValidationFailure.None,
    "http://127.0.0.1:6470", null);
lifecycle.Next = degraded;
BridgeApiAccessSettingsApplyResult degradedApplied =
    await coordinator.ApplyAsync(localNetwork);
Check(degradedApplied.Applied && degradedApplied.Runtime.IsDegraded &&
    degradedApplied.Desired.AccessMode == BridgeApiAccessMode.LocalNetwork &&
    !degradedApplied.Onboarding.Available,
    "degraded secure LAN retains desired LocalNetwork but does not claim pairing readiness");
Check(degradedApplied.Runtime.LanEndpoint is null &&
    degradedApplied.Runtime.LoopbackEndpoint?.StartsWith("http://127.0.0.1:") == true,
    "TLS failure exposes no LAN HTTP fallback and preserves HTTP loopback truth");

BridgeApiAccessConfiguration reopened = persistence.Last!;
BridgeApiAccessSettingsView reopenedView = coordinator.Capture(reopened);
Check(reopenedView.Desired == reopened,
    "reopen reloads the persisted desired configuration exactly");

string sourceRoot = FindSourceRoot();
string source = File.ReadAllText(Path.Combine(
    sourceRoot, "BridgeApiAccessSettingsPresentation.cs"));
Check(!source.Contains("BridgeConnectionService", StringComparison.Ordinal) &&
    !source.Contains("BridgeFirmwareService", StringComparison.Ordinal) &&
    !source.Contains("BridgeAccessoryService", StringComparison.Ordinal) &&
    !source.Contains("BridgeControllerOwnership", StringComparison.Ordinal) &&
    !source.Contains("SerialPort", StringComparison.Ordinal) &&
    !source.Contains("TcpBridgeTransport", StringComparison.Ordinal),
    "settings apply has no Pico, firmware, accessory, controller, or transport dependency");
Check(!source.Contains("NetworkChange", StringComparison.Ordinal) &&
    !source.Contains("Timer", StringComparison.Ordinal) &&
    !source.Contains("Firewall", StringComparison.Ordinal) &&
    !source.Contains("netsh", StringComparison.OrdinalIgnoreCase) &&
    !source.Contains("mDNS", StringComparison.OrdinalIgnoreCase),
    "settings core adds no address observer, firewall automation, or discovery");

Console.WriteLine($"BridgeApiAccessSettingsPresentationSmoke: {passed} checks passed.");

static BridgeApiNetworkInterfaceSnapshot Interface(
    string id, string name, OperationalStatus status,
    NetworkInterfaceType type, params string[] addresses) =>
    new(id, name, status, type,
        addresses.Select(IPAddress.Parse).ToArray());

static BridgeApiAccessRuntimeState RuntimeFor(
    BridgeApiAccessConfiguration configuration) => configuration.AccessMode switch
    {
        BridgeApiAccessMode.Off => new(configuration,
            BridgeApiLoopbackRuntimeStatus.NotRequested,
            BridgeApiLanRuntimeStatus.NotRequested,
            BridgeApiLanValidationFailure.None, null, null),
        BridgeApiAccessMode.LocalOnly => new(configuration,
            BridgeApiLoopbackRuntimeStatus.Listening,
            BridgeApiLanRuntimeStatus.NotRequested,
            BridgeApiLanValidationFailure.None,
            $"http://127.0.0.1:{configuration.Port}", null),
        _ => new(configuration,
            BridgeApiLoopbackRuntimeStatus.Listening,
            BridgeApiLanRuntimeStatus.Listening,
            BridgeApiLanValidationFailure.None,
            $"http://127.0.0.1:{configuration.Port}",
            $"https://{configuration.SelectedIpv4Address}:{configuration.Port}")
    };

static BridgeApiOnboardingSnapshot Onboarding(
    BridgeApiAccessConfiguration desired,
    bool available,
    string? endpoint = null) => new(available,
        available ? BridgeApiOnboardingUnavailableReason.None :
            desired.AccessMode == BridgeApiAccessMode.Off
                ? BridgeApiOnboardingUnavailableReason.ApiOff
                : desired.AccessMode == BridgeApiAccessMode.LocalOnly
                    ? BridgeApiOnboardingUnavailableReason.LocalOnly
                    : BridgeApiOnboardingUnavailableReason.LanNotListening,
        "v1", desired.AccessMode, endpoint, "https", "Bearer",
        available ? Convert.ToBase64String(new byte[32]) : null,
        available ? new string('A', 64) : null,
        available ? DateTime.UtcNow.AddYears(1) : null,
        available, false);

static string FindSourceRoot()
{
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
        directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName,
                "BridgeApiAccessSettingsPresentation.cs"))) return directory.FullName;
    }
    throw new DirectoryNotFoundException();
}

sealed class FakeCatalog(
    IReadOnlyList<BridgeApiNetworkInterfaceSnapshot> interfaces) :
    IBridgeApiNetworkInterfaceCatalog
{
    public IReadOnlyList<BridgeApiNetworkInterfaceSnapshot> GetAll() => interfaces;
}

sealed class FakePersistence : IBridgeApiAccessSettingsPersistence
{
    public int SaveCalls { get; private set; }
    public BridgeApiAccessConfiguration? Last { get; private set; }
    public void Save(BridgeApiAccessConfiguration configuration)
    {
        SaveCalls++;
        Last = configuration;
    }
}

sealed class FakeLifecycle(BridgeApiAccessRuntimeState initial) :
    IBridgeApiAccessLifecycle
{
    private TaskCompletionSource? _release;
    public BridgeApiAccessRuntimeState Current { get; private set; } = initial;
    public BridgeApiAccessRuntimeState Next { get; set; } = initial;
    public int ApplyCalls { get; private set; }
    public bool BlockNext { get; set; }
    public TaskCompletionSource Entered { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<BridgeApiAccessRuntimeState> ApplyAsync(
        BridgeApiAccessResolution resolution,
        CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        if (BlockNext)
        {
            BlockNext = false;
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Current = Next;
        return Current;
    }

    public void Release() => _release?.TrySetResult();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeOnboardingProvider(FakeLifecycle lifecycle) :
    IBridgeApiOnboardingProvider
{
    public int Calls { get; private set; }
    public BridgeApiOnboardingSnapshot GetCurrent()
    {
        Calls++;
        BridgeApiAccessRuntimeState runtime = lifecycle.Current;
        bool available = runtime.Desired.AccessMode == BridgeApiAccessMode.LocalNetwork &&
            runtime.LanStatus == BridgeApiLanRuntimeStatus.Listening &&
            runtime.LanEndpoint?.StartsWith("https://", StringComparison.Ordinal) == true;
        BridgeApiOnboardingUnavailableReason reason = available
            ? BridgeApiOnboardingUnavailableReason.None
            : runtime.Desired.AccessMode == BridgeApiAccessMode.Off
                ? BridgeApiOnboardingUnavailableReason.ApiOff
                : runtime.Desired.AccessMode == BridgeApiAccessMode.LocalOnly
                    ? BridgeApiOnboardingUnavailableReason.LocalOnly
                    : BridgeApiOnboardingUnavailableReason.LanNotListening;
        return new(available, reason, "v1", runtime.Desired.AccessMode,
            available ? runtime.LanEndpoint : null, "https", "Bearer",
            available ? Convert.ToBase64String(new byte[32]) : null,
            available ? new string('A', 64) : null,
            available ? DateTime.UtcNow.AddYears(1) : null,
            available, false);
    }
}

namespace PicoSwitchController
{
    public interface IBridgeApiAccessLifecycle : IAsyncDisposable
    {
        BridgeApiAccessRuntimeState Current { get; }
        Task<BridgeApiAccessRuntimeState> ApplyAsync(
            BridgeApiAccessResolution resolution,
            CancellationToken cancellationToken = default);
    }

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

    internal static class BridgeApiOnboardingProvider
    {
        public const string ApiVersion = "v1";
        public const string Protocol = "https";
        public const string AuthenticationScheme = "Bearer";
    }
}
