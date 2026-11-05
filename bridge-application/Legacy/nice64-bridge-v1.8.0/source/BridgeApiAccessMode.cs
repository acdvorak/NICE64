namespace PicoSwitchController;

public enum BridgeApiAccessMode
{
    Off,
    LocalOnly,
    LocalNetwork
}

public sealed record BridgeApiAccessConfiguration(
    BridgeApiAccessMode AccessMode,
    int Port,
    string? SelectedInterfaceId,
    string? SelectedIpv4Address,
    string? SelectedInterfaceName = null)
{
    public const int DefaultPort = 6470;

    public static BridgeApiAccessConfiguration Default { get; } = new(
        BridgeApiAccessMode.Off, DefaultPort, null, null, null);

    public bool RequiresLanSelection =>
        AccessMode == BridgeApiAccessMode.LocalNetwork;

    public bool HasCompleteLanSelection =>
        RequiresLanSelection &&
        !string.IsNullOrWhiteSpace(SelectedInterfaceId) &&
        !string.IsNullOrWhiteSpace(SelectedIpv4Address);
}

public enum BridgeApiLoopbackRuntimeStatus
{
    NotRequested,
    Starting,
    Listening,
    BindFailed,
    Stopped
}

public enum BridgeApiLanRuntimeStatus
{
    NotRequested,
    Starting,
    Listening,
    UnavailableSelectedInterface,
    UnavailableSelectedAddress,
    UnavailableCertificate,
    BindFailed,
    Stopped
}

public sealed record BridgeApiAccessRuntimeState(
    BridgeApiAccessConfiguration Desired,
    BridgeApiLoopbackRuntimeStatus LoopbackStatus,
    BridgeApiLanRuntimeStatus LanStatus,
    BridgeApiLanValidationFailure LanFailure,
    string? LoopbackEndpoint,
    string? LanEndpoint)
{
    public bool IsDegraded =>
        Desired.AccessMode == BridgeApiAccessMode.LocalNetwork &&
        LoopbackStatus == BridgeApiLoopbackRuntimeStatus.Listening &&
        LanStatus != BridgeApiLanRuntimeStatus.Listening;

    public static BridgeApiAccessRuntimeState NotStarted(
        BridgeApiAccessConfiguration desired) => new(
            desired,
            desired.AccessMode == BridgeApiAccessMode.Off
                ? BridgeApiLoopbackRuntimeStatus.NotRequested
                : BridgeApiLoopbackRuntimeStatus.Stopped,
            desired.AccessMode == BridgeApiAccessMode.LocalNetwork
                ? BridgeApiLanRuntimeStatus.Stopped
                : BridgeApiLanRuntimeStatus.NotRequested,
            BridgeApiLanValidationFailure.None,
            null,
            null);

    public static BridgeApiAccessRuntimeState DegradedLocalNetwork(
        BridgeApiAccessConfiguration desired,
        BridgeApiLanRuntimeStatus lanStatus,
        BridgeApiLanValidationFailure failure,
        string loopbackEndpoint)
    {
        if (desired.AccessMode != BridgeApiAccessMode.LocalNetwork)
            throw new ArgumentException(
                "Degraded LAN state requires LocalNetwork user intent.",
                nameof(desired));
        if (lanStatus is BridgeApiLanRuntimeStatus.NotRequested or
            BridgeApiLanRuntimeStatus.Starting or
            BridgeApiLanRuntimeStatus.Listening)
            throw new ArgumentOutOfRangeException(nameof(lanStatus));
        if (failure == BridgeApiLanValidationFailure.None)
            throw new ArgumentOutOfRangeException(nameof(failure));
        ArgumentException.ThrowIfNullOrWhiteSpace(loopbackEndpoint);
        return new(desired, BridgeApiLoopbackRuntimeStatus.Listening,
            lanStatus, failure, loopbackEndpoint, null);
    }
}

public enum BridgeApiAccessConfigurationSource
{
    Persisted,
    LegacyEnvironment
}

public sealed record BridgeApiAccessResolution(
    BridgeApiAccessConfiguration Desired,
    BridgeApiAccessConfiguration Effective,
    BridgeApiAccessConfigurationSource EffectiveSource);

public static class BridgeApiAccessSettingsResolver
{
    public const string LegacyEnabledEnvironmentVariable =
        "NICE64_BRIDGE_API_ENABLED";
    public const string LegacyPortEnvironmentVariable =
        "NICE64_BRIDGE_API_PORT";

    public static BridgeApiAccessResolution Resolve(
        BridgeApiAccessConfiguration persisted,
        string? legacyEnabled,
        string? legacyPort)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        if (!IsEnabled(legacyEnabled))
            return new(persisted, persisted,
                BridgeApiAccessConfigurationSource.Persisted);

        int port = int.TryParse(legacyPort, out int parsedPort)
            ? parsedPort
            : BridgeApiAccessConfiguration.DefaultPort;
        BridgeApiAccessConfiguration effective = new(
            BridgeApiAccessMode.LocalOnly, port, null, null, null);
        return new(persisted, effective,
            BridgeApiAccessConfigurationSource.LegacyEnvironment);
    }

    private static bool IsEnabled(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
