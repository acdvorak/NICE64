namespace PicoSwitchController;

using System.Globalization;
using System.Net;

internal sealed record BridgeApiAccessModeChoice(
    BridgeApiAccessMode Mode,
    string Label);

internal sealed record BridgeApiAccessIpv4Choice(
    string Address,
    bool Available)
{
    public override string ToString() => Available
        ? Address
        : $"{Address} (unavailable)";
}

internal sealed record BridgeApiAccessInterfaceChoice(
    string Id,
    string Name,
    IReadOnlyList<BridgeApiAccessIpv4Choice> Addresses,
    bool Available)
{
    public override string ToString() => Available
        ? Name
        : $"{Name} (unavailable)";
}

internal sealed record BridgeApiAccessSettingsView(
    IReadOnlyList<BridgeApiAccessModeChoice> Modes,
    BridgeApiAccessConfiguration Desired,
    BridgeApiAccessRuntimeState Runtime,
    BridgeApiOnboardingSnapshot Onboarding,
    bool LanSelectionEnabled,
    IReadOnlyList<BridgeApiAccessInterfaceChoice> Interfaces,
    string DesiredSummary,
    string ActualSummary,
    string? ActualEndpoint,
    string? BoundedReason)
{
    public bool PairingReady => Onboarding.Available;
}

internal sealed record BridgeApiAccessCandidateResult(
    bool Success,
    BridgeApiAccessConfiguration? Configuration,
    string? Code,
    string? Detail);

internal sealed record BridgeApiAccessSettingsApplyResult(
    bool Applied,
    bool Busy,
    BridgeApiAccessConfiguration Desired,
    BridgeApiAccessRuntimeState Runtime,
    BridgeApiOnboardingSnapshot Onboarding,
    string? Code,
    string? Detail);

internal interface IBridgeApiAccessSettingsPersistence
{
    void Save(BridgeApiAccessConfiguration configuration);
}

internal sealed class DelegateBridgeApiAccessSettingsPersistence(
    Action<BridgeApiAccessConfiguration> save) :
    IBridgeApiAccessSettingsPersistence
{
    private readonly Action<BridgeApiAccessConfiguration> _save = save ??
        throw new ArgumentNullException(nameof(save));

    public void Save(BridgeApiAccessConfiguration configuration) =>
        _save(configuration);
}

internal sealed class BridgeApiAccessSettingsPresentation(
    IBridgeApiNetworkInterfaceCatalog catalog,
    IBridgeApiLanAddressValidator validator)
{
    internal static IReadOnlyList<BridgeApiAccessModeChoice> Modes { get; } =
        Array.AsReadOnly(new[]
        {
            new BridgeApiAccessModeChoice(BridgeApiAccessMode.Off, "Off"),
            new BridgeApiAccessModeChoice(BridgeApiAccessMode.LocalOnly,
                "Local Only"),
            new BridgeApiAccessModeChoice(BridgeApiAccessMode.LocalNetwork,
                "Local Network")
        });

    private readonly IBridgeApiNetworkInterfaceCatalog _catalog = catalog ??
        throw new ArgumentNullException(nameof(catalog));
    private readonly IBridgeApiLanAddressValidator _validator = validator ??
        throw new ArgumentNullException(nameof(validator));

    public BridgeApiAccessSettingsView Compose(
        BridgeApiAccessConfiguration desired,
        BridgeApiAccessRuntimeState runtime,
        BridgeApiOnboardingSnapshot onboarding)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(onboarding);
        return new(Modes, desired, runtime, onboarding,
            desired.AccessMode == BridgeApiAccessMode.LocalNetwork,
            EnumerateInterfaces(desired),
            $"Desired: {ModeLabel(desired.AccessMode)}",
            ActualSummary(runtime),
            runtime.LanEndpoint ?? runtime.LoopbackEndpoint,
            BoundedReason(runtime));
    }

    public BridgeApiAccessCandidateResult CreateCandidate(
        BridgeApiAccessMode mode,
        string? portText,
        string? interfaceId,
        string? ipv4Address,
        string? interfaceName,
        BridgeApiAccessConfiguration priorDesired)
    {
        ArgumentNullException.ThrowIfNull(priorDesired);
        if (!int.TryParse(portText, NumberStyles.None,
                CultureInfo.InvariantCulture, out int port))
            return Invalid("invalid-port", "API port must be a number from 1 to 65535.");
        BridgeApiLanValidationFailure portFailure =
            BridgeApiLanAddressValidator.ValidatePort(port);
        if (portFailure == BridgeApiLanValidationFailure.ReservedPort)
            return Invalid("reserved-port", "API port is reserved by NICE64.");
        if (portFailure != BridgeApiLanValidationFailure.None)
            return Invalid("invalid-port", "API port must be from 1 to 65535.");

        if (mode != BridgeApiAccessMode.LocalNetwork)
        {
            return Valid(new(mode, port,
                priorDesired.SelectedInterfaceId,
                priorDesired.SelectedIpv4Address,
                priorDesired.SelectedInterfaceName));
        }

        if (string.IsNullOrWhiteSpace(interfaceId))
            return Invalid("missing-interface",
                "Select a network interface for Local Network mode.");
        if (string.IsNullOrWhiteSpace(ipv4Address))
            return Invalid("missing-address",
                "Select an IPv4 address for Local Network mode.");

        BridgeApiAccessConfiguration candidate = new(mode, port,
            interfaceId, ipv4Address, interfaceName);
        BridgeApiLanValidationResult validation = _validator.Validate(candidate);
        if (!validation.Success || validation.Selection is null)
            return Invalid(Code(validation.Failure), Detail(validation.Failure));
        BridgeApiLanSelection exact = validation.Selection;
        return Valid(new(mode, port, exact.InterfaceId, exact.Ipv4Address,
            exact.InterfaceName));
    }

    private IReadOnlyList<BridgeApiAccessInterfaceChoice> EnumerateInterfaces(
        BridgeApiAccessConfiguration desired)
    {
        Dictionary<string, (string Name, SortedSet<string> Addresses)> valid =
            new(StringComparer.Ordinal);
        try
        {
            foreach (BridgeApiNetworkInterfaceSnapshot network in _catalog.GetAll())
            {
                foreach (IPAddress address in network.UnicastAddresses)
                {
                    BridgeApiAccessConfiguration candidate = new(
                        BridgeApiAccessMode.LocalNetwork,
                        BridgeApiAccessConfiguration.DefaultPort,
                        network.Id, address.ToString(), network.Name);
                    if (!_validator.Validate(candidate).Success) continue;
                    if (!valid.TryGetValue(network.Id, out var entry))
                        entry = (network.Name, new(StringComparer.Ordinal));
                    entry.Addresses.Add(address.ToString());
                    valid[network.Id] = entry;
                }
            }
        }
        catch
        {
            valid.Clear();
        }

        List<BridgeApiAccessInterfaceChoice> choices = valid
            .Select(entry => new BridgeApiAccessInterfaceChoice(
                entry.Key, entry.Value.Name,
                entry.Value.Addresses.Select(address =>
                    new BridgeApiAccessIpv4Choice(address, true)).ToArray(), true))
            .OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Id, StringComparer.Ordinal)
            .ToList();

        AddPersistedUnavailableChoice(choices, desired);
        return choices.AsReadOnly();
    }

    private static void AddPersistedUnavailableChoice(
        List<BridgeApiAccessInterfaceChoice> choices,
        BridgeApiAccessConfiguration desired)
    {
        if (string.IsNullOrWhiteSpace(desired.SelectedInterfaceId)) return;
        BridgeApiAccessInterfaceChoice? exactInterface = choices.FirstOrDefault(
            choice => choice.Id == desired.SelectedInterfaceId);
        string? persistedAddress = desired.SelectedIpv4Address;
        if (exactInterface is null)
        {
            choices.Insert(0, new(desired.SelectedInterfaceId,
                string.IsNullOrWhiteSpace(desired.SelectedInterfaceName)
                    ? desired.SelectedInterfaceId
                    : desired.SelectedInterfaceName,
                string.IsNullOrWhiteSpace(persistedAddress)
                    ? Array.Empty<BridgeApiAccessIpv4Choice>()
                    : new[] { new BridgeApiAccessIpv4Choice(
                        persistedAddress, false) }, false));
            return;
        }
        if (string.IsNullOrWhiteSpace(persistedAddress) ||
            exactInterface.Addresses.Any(address =>
                address.Address == persistedAddress)) return;
        int index = choices.IndexOf(exactInterface);
        choices[index] = exactInterface with
        {
            Addresses = exactInterface.Addresses
                .Prepend(new BridgeApiAccessIpv4Choice(
                    persistedAddress, false)).ToArray()
        };
    }

    private static string ActualSummary(BridgeApiAccessRuntimeState runtime)
    {
        if (runtime.Desired.AccessMode == BridgeApiAccessMode.Off &&
            runtime.LoopbackStatus == BridgeApiLoopbackRuntimeStatus.NotRequested)
            return "Actual: Off";
        if (runtime.LanStatus == BridgeApiLanRuntimeStatus.Listening)
            return "Actual: Listening — secure local network";
        if (runtime.IsDegraded)
            return "Actual: Degraded — loopback only";
        if (runtime.LoopbackStatus == BridgeApiLoopbackRuntimeStatus.Listening)
            return "Actual: Listening — loopback only";
        return $"Actual: Unavailable — {runtime.LoopbackStatus}";
    }

    private static string? BoundedReason(BridgeApiAccessRuntimeState runtime)
    {
        if (runtime.LanStatus == BridgeApiLanRuntimeStatus.UnavailableCertificate)
            return "TLS identity is unavailable.";
        if (runtime.LanStatus == BridgeApiLanRuntimeStatus.BindFailed)
            return "Local Network API could not start securely.";
        if (runtime.LanStatus == BridgeApiLanRuntimeStatus.UnavailableSelectedInterface)
            return "Selected network interface is no longer available.";
        if (runtime.LanStatus == BridgeApiLanRuntimeStatus.UnavailableSelectedAddress)
            return "Selected network address is no longer available.";
        if (runtime.LoopbackStatus == BridgeApiLoopbackRuntimeStatus.BindFailed)
            return "The local API listener could not start.";
        return null;
    }

    private static string ModeLabel(BridgeApiAccessMode mode) => mode switch
    {
        BridgeApiAccessMode.Off => "Off",
        BridgeApiAccessMode.LocalOnly => "Local Only",
        BridgeApiAccessMode.LocalNetwork => "Local Network",
        _ => "Unknown"
    };

    private static string Code(BridgeApiLanValidationFailure failure) => failure switch
    {
        BridgeApiLanValidationFailure.MissingInterfaceId => "missing-interface",
        BridgeApiLanValidationFailure.MissingAddress => "missing-address",
        BridgeApiLanValidationFailure.ReservedPort => "reserved-port",
        BridgeApiLanValidationFailure.InvalidPort => "invalid-port",
        BridgeApiLanValidationFailure.InvalidIpv4 => "invalid-address",
        BridgeApiLanValidationFailure.LoopbackAddress or
        BridgeApiLanValidationFailure.UnspecifiedAddress or
        BridgeApiLanValidationFailure.LinkLocalAddress or
        BridgeApiLanValidationFailure.MulticastAddress or
        BridgeApiLanValidationFailure.BroadcastAddress or
        BridgeApiLanValidationFailure.ReservedAddress => "unsupported-address",
        BridgeApiLanValidationFailure.AddressNotAssignedToInterface =>
            "address-not-assigned",
        _ => "interface-unavailable"
    };

    private static string Detail(BridgeApiLanValidationFailure failure) => failure switch
    {
        BridgeApiLanValidationFailure.MissingInterfaceId =>
            "Select a network interface for Local Network mode.",
        BridgeApiLanValidationFailure.MissingAddress =>
            "Select an IPv4 address for Local Network mode.",
        BridgeApiLanValidationFailure.ReservedPort =>
            "API port is reserved by NICE64.",
        BridgeApiLanValidationFailure.InvalidPort =>
            "API port must be from 1 to 65535.",
        BridgeApiLanValidationFailure.InvalidIpv4 =>
            "The selected IPv4 address is malformed.",
        BridgeApiLanValidationFailure.LoopbackAddress =>
            "Loopback cannot be selected as the Local Network address.",
        BridgeApiLanValidationFailure.AddressNotAssignedToInterface =>
            "The selected network address is no longer available on that interface.",
        _ => "The selected network interface is no longer available."
    };

    private static BridgeApiAccessCandidateResult Valid(
        BridgeApiAccessConfiguration configuration) =>
        new(true, configuration, null, null);

    private static BridgeApiAccessCandidateResult Invalid(
        string code, string detail) => new(false, null, code, detail);
}

internal sealed class BridgeApiAccessSettingsCoordinator(
    IBridgeApiAccessLifecycle lifecycle,
    IBridgeApiOnboardingProvider onboarding,
    IBridgeApiAccessSettingsPersistence persistence,
    BridgeApiAccessSettingsPresentation presentation,
    Func<BridgeApiAccessConfiguration, BridgeApiAccessResolution> resolve)
{
    private readonly IBridgeApiAccessLifecycle _lifecycle = lifecycle ??
        throw new ArgumentNullException(nameof(lifecycle));
    private readonly IBridgeApiOnboardingProvider _onboarding = onboarding ??
        throw new ArgumentNullException(nameof(onboarding));
    private readonly IBridgeApiAccessSettingsPersistence _persistence = persistence ??
        throw new ArgumentNullException(nameof(persistence));
    private readonly BridgeApiAccessSettingsPresentation _presentation = presentation ??
        throw new ArgumentNullException(nameof(presentation));
    private readonly Func<BridgeApiAccessConfiguration,
        BridgeApiAccessResolution> _resolve = resolve ??
        throw new ArgumentNullException(nameof(resolve));
    private int _applying;

    public BridgeApiAccessSettingsView Capture(
        BridgeApiAccessConfiguration desired) => _presentation.Compose(
            desired, _lifecycle.Current, SafeOnboarding());

    public BridgeApiAccessCandidateResult CreateCandidate(
        BridgeApiAccessMode mode,
        string? portText,
        string? interfaceId,
        string? ipv4Address,
        string? interfaceName,
        BridgeApiAccessConfiguration priorDesired) =>
        _presentation.CreateCandidate(mode, portText, interfaceId,
            ipv4Address, interfaceName, priorDesired);

    public async Task<BridgeApiAccessSettingsApplyResult> ApplyAsync(
        BridgeApiAccessConfiguration candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (Interlocked.CompareExchange(ref _applying, 1, 0) != 0)
            return Result(false, true, candidate, _lifecycle.Current,
                "apply-busy", "API settings are already being applied.");
        try
        {
            BridgeApiAccessCandidateResult validation = _presentation.CreateCandidate(
                candidate.AccessMode, candidate.Port.ToString(CultureInfo.InvariantCulture),
                candidate.SelectedInterfaceId, candidate.SelectedIpv4Address,
                candidate.SelectedInterfaceName, candidate);
            if (!validation.Success)
                return Result(false, false, candidate, _lifecycle.Current,
                    validation.Code, validation.Detail);
            BridgeApiAccessConfiguration exact = validation.Configuration!;
            try { _persistence.Save(exact); }
            catch
            {
                return Result(false, false, candidate, _lifecycle.Current,
                    "settings-save-failed", "API settings could not be saved.");
            }

            try
            {
                BridgeApiAccessRuntimeState runtime = await _lifecycle.ApplyAsync(
                    _resolve(exact), cancellationToken).ConfigureAwait(false);
                return Result(true, false, exact, runtime,
                    runtime.IsDegraded ? "local-network-degraded" : null,
                    runtime.IsDegraded ? BoundedApplyDetail(runtime) : null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Result(false, false, exact, _lifecycle.Current,
                    "apply-cancelled", "API settings apply was cancelled.");
            }
            catch
            {
                return Result(false, false, exact, _lifecycle.Current,
                    "api-start-failed", "The API host could not be updated.");
            }
        }
        finally
        {
            Volatile.Write(ref _applying, 0);
        }
    }

    private BridgeApiAccessSettingsApplyResult Result(
        bool applied,
        bool busy,
        BridgeApiAccessConfiguration desired,
        BridgeApiAccessRuntimeState runtime,
        string? code,
        string? detail) => new(applied, busy, desired, runtime,
            SafeOnboarding(), code, detail);

    private BridgeApiOnboardingSnapshot SafeOnboarding()
    {
        try { return _onboarding.GetCurrent(); }
        catch
        {
            return new(false,
                BridgeApiOnboardingUnavailableReason.LanNotListening,
                BridgeApiOnboardingProvider.ApiVersion,
                _lifecycle.Current.Desired.AccessMode, null,
                BridgeApiOnboardingProvider.Protocol,
                BridgeApiOnboardingProvider.AuthenticationScheme,
                null, null, null, false, false);
        }
    }

    private static string BoundedApplyDetail(BridgeApiAccessRuntimeState runtime) =>
        runtime.LanStatus switch
        {
            BridgeApiLanRuntimeStatus.UnavailableCertificate =>
                "TLS identity is unavailable; the API remains loopback-only.",
            BridgeApiLanRuntimeStatus.UnavailableSelectedInterface =>
                "Selected network interface is unavailable; the API remains loopback-only.",
            BridgeApiLanRuntimeStatus.UnavailableSelectedAddress =>
                "Selected network address is unavailable; the API remains loopback-only.",
            _ => "Local Network API could not start securely; the API remains loopback-only."
        };
}
