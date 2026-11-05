namespace PicoSwitchController;

using System.Collections.ObjectModel;

public enum BridgeAccessoryMode
{
    None,
    ControllerPak,
    RumblePak,
    TransferPak,
    NetworkPak
}

public enum BridgeDeviceRole
{
    Unknown,
    Controller,
    Standalone
}

public enum BridgeDeviceService
{
    Unknown,
    None,
    Diagnostics,
    Network,
    KeyboardMouse,
    Other
}

public enum BridgeControlConnectionKind
{
    None,
    SerialUsb,
    WifiTcp
}

public enum BridgeConnectionState
{
    Disconnected,
    Connected
}

public enum BridgeFirmwareTransitionPhase
{
    Idle,
    TransitionPending,
    RebootExpected,
    Reconnecting,
    Completed,
    Failed
}

public sealed record BridgeFirmwareTransitionSnapshot(
    BridgeFirmwareTransitionPhase Phase,
    FirmwarePersonality Target,
    string Error)
{
    public static BridgeFirmwareTransitionSnapshot Idle { get; } =
        new(BridgeFirmwareTransitionPhase.Idle, FirmwarePersonality.Unknown, string.Empty);
}

public enum BridgeAccessoryTransitionPhase
{
    Idle,
    TransitionPending,
    Completed,
    Failed
}

public sealed record BridgeAccessoryTransitionSnapshot(
    BridgeAccessoryTransitionPhase Phase,
    BridgeAccessoryMode Target,
    string Error)
{
    public static BridgeAccessoryTransitionSnapshot Idle { get; } =
        new(BridgeAccessoryTransitionPhase.Idle, BridgeAccessoryMode.None, string.Empty);
}

public enum BridgeNetworkPakTransport
{
    None,
    UsbSerial,
    Tcp,
    ReliableUdpQuick
}

public enum BridgeCapabilityId
{
    FirmwareModeController,
    FirmwareModeDiagnostics,
    FirmwareModeNetwork,
    FirmwareModeKeyboard,
    FirmwareModeMouse,
    LegacyNetworkPakStream,
    BulkRead
}

public enum BridgeCapabilityProvenance
{
    DeviceReported,
    BridgeImplemented,
    ApplicationNegotiated,
    Effective
}

public sealed record BridgeCapabilityDescriptor(
    BridgeCapabilityId Id,
    bool Supported,
    BridgeCapabilityProvenance Provenance,
    int MaximumLogicalBlockBytes = 0,
    int PreferredLogicalBlockBytes = 0);

public sealed class BridgeCapabilitySet
{
    private readonly IReadOnlyDictionary<BridgeCapabilityId, BridgeCapabilityDescriptor> _items;

    public BridgeCapabilitySet(
        BridgeCapabilityProvenance provenance,
        IEnumerable<BridgeCapabilityDescriptor>? descriptors = null)
    {
        Provenance = provenance;
        Dictionary<BridgeCapabilityId, BridgeCapabilityDescriptor> items = [];
        if (descriptors is not null)
        {
            foreach (BridgeCapabilityDescriptor descriptor in descriptors)
            {
                if (descriptor.Provenance != provenance)
                    throw new ArgumentException("Capability provenance does not match the set.", nameof(descriptors));
                items[descriptor.Id] = descriptor;
            }
        }
        _items = new ReadOnlyDictionary<BridgeCapabilityId, BridgeCapabilityDescriptor>(items);
    }

    public BridgeCapabilityProvenance Provenance { get; }
    public IReadOnlyCollection<BridgeCapabilityDescriptor> Items => _items.Values.ToArray();
    public bool Supports(BridgeCapabilityId id) =>
        _items.TryGetValue(id, out BridgeCapabilityDescriptor? item) && item.Supported;
    public BridgeCapabilityDescriptor? Get(BridgeCapabilityId id) =>
        _items.TryGetValue(id, out BridgeCapabilityDescriptor? item) ? item : null;

    public static BridgeCapabilitySet Empty(BridgeCapabilityProvenance provenance) => new(provenance);
}

public sealed record BridgeDeviceStateSnapshot(
    string DeviceIdentity,
    string DeviceVersion,
    BridgeDeviceRole DeviceRole,
    BridgeDeviceService DeviceService,
    FirmwarePersonality FirmwarePersonality,
    BridgeControlConnectionKind ControlConnectionKind,
    BridgeConnectionState ControlConnectionState,
    BridgeNetworkPakTransport SelectedNetworkPakTransport,
    BridgeNetworkPakTransport ActiveNetworkPakTransport,
    BridgeCapabilitySet DeviceReportedCapabilities,
    BridgeCapabilitySet BridgeImplementedCapabilities,
    BridgeCapabilitySet ApplicationNegotiatedCapabilities,
    BridgeCapabilitySet EffectiveCapabilities,
    long TransitionGeneration,
    DateTimeOffset TransitionTimestampUtc)
{
    public BridgeFirmwareTransitionSnapshot FirmwareTransition { get; init; } =
        BridgeFirmwareTransitionSnapshot.Idle;
    public BridgeAccessoryMode ActiveAccessory { get; init; } = BridgeAccessoryMode.None;
    public BridgeAccessoryTransitionSnapshot AccessoryTransition { get; init; } =
        BridgeAccessoryTransitionSnapshot.Idle;
}

public sealed class BridgeDeviceStateChangedEventArgs(
    BridgeDeviceStateSnapshot previous,
    BridgeDeviceStateSnapshot current) : EventArgs
{
    public BridgeDeviceStateSnapshot Previous { get; } = previous;
    public BridgeDeviceStateSnapshot Current { get; } = current;
}

public interface IBridgeDeviceStateProvider
{
    BridgeDeviceStateSnapshot Current { get; }
    event EventHandler<BridgeDeviceStateChangedEventArgs>? StateChanged;
}

public interface IBridgeCapabilityProvider
{
    BridgeCapabilitySet Capabilities { get; }
    bool Supports(BridgeCapabilityId capability);
    BridgeCapabilityDescriptor? Get(BridgeCapabilityId capability);
}

public interface IBridgeHostStateProvider :
    IBridgeDeviceStateProvider, IBridgeCapabilityProvider
{
}

internal interface IBridgeDeviceStateUpdater
{
    void Connect(BridgeControlConnectionKind connectionKind, string identity, string version,
        FirmwareModeInfo? mode, BridgeCapabilitySet? deviceCapabilities = null);
    void ReplaceControlSession(BridgeControlConnectionKind connectionKind,
        string identity, string version, FirmwareModeInfo? mode,
        BridgeCapabilitySet? deviceCapabilities = null);
    void UpdateFirmwareMode(FirmwareModeInfo mode, BridgeCapabilitySet? deviceCapabilities = null);
    void SetControlConnectionState(BridgeControlConnectionKind kind,
        BridgeConnectionState state);
    void SelectNetworkPakTransport(BridgeNetworkPakTransport transport);
    void ActivateNetworkPakTransport(BridgeNetworkPakTransport transport);
    void DeactivateNetworkPakTransport(BridgeNetworkPakTransport transport);
    void SetApplicationCapabilities(BridgeCapabilitySet capabilities);
    void SetFirmwareTransition(BridgeFirmwareTransitionSnapshot transition);
    void SetActiveAccessory(BridgeAccessoryMode accessory);
    void SetAccessoryTransition(BridgeAccessoryTransitionSnapshot transition);
    void Disconnect();
}

public sealed class BridgeDeviceStateService :
    IBridgeHostStateProvider, IBridgeDeviceStateUpdater
{
    private readonly object _sync = new();
    private BridgeDeviceStateSnapshot _current = CreateDisconnected(0, DateTimeOffset.UtcNow);

    public BridgeDeviceStateSnapshot Current
    {
        get { lock (_sync) return _current; }
    }

    public BridgeCapabilitySet Capabilities => Current.EffectiveCapabilities;
    public bool Supports(BridgeCapabilityId capability) => Capabilities.Supports(capability);
    public BridgeCapabilityDescriptor? Get(BridgeCapabilityId capability) => Capabilities.Get(capability);
    public event EventHandler<BridgeDeviceStateChangedEventArgs>? StateChanged;

    void IBridgeDeviceStateUpdater.Connect(
        BridgeControlConnectionKind connectionKind, string identity, string version,
        FirmwareModeInfo? mode, BridgeCapabilitySet? deviceCapabilities)
    {
        Mutate(current => Build(
            identity.Trim(), version.Trim(), mode,
            connectionKind, BridgeConnectionState.Connected,
            current.SelectedNetworkPakTransport, BridgeNetworkPakTransport.None,
            deviceCapabilities ?? current.DeviceReportedCapabilities,
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.ApplicationNegotiated),
            current.TransitionGeneration + 1, BridgeFirmwareTransitionSnapshot.Idle));
    }

    void IBridgeDeviceStateUpdater.ReplaceControlSession(
        BridgeControlConnectionKind connectionKind, string identity, string version,
        FirmwareModeInfo? mode, BridgeCapabilitySet? deviceCapabilities)
    {
        Mutate(current => Build(
            identity.Trim(), version.Trim(), mode,
            connectionKind, BridgeConnectionState.Connected,
            current.SelectedNetworkPakTransport, BridgeNetworkPakTransport.None,
            deviceCapabilities ?? current.DeviceReportedCapabilities,
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.ApplicationNegotiated),
            current.TransitionGeneration + 1, current.FirmwareTransition,
            current.ActiveAccessory, current.AccessoryTransition));
    }

    void IBridgeDeviceStateUpdater.UpdateFirmwareMode(
        FirmwareModeInfo mode, BridgeCapabilitySet? deviceCapabilities)
    {
        Mutate(current => Build(
            current.DeviceIdentity, current.DeviceVersion, mode,
            current.ControlConnectionKind, current.ControlConnectionState,
            current.SelectedNetworkPakTransport, current.ActiveNetworkPakTransport,
            deviceCapabilities ?? current.DeviceReportedCapabilities,
            current.ApplicationNegotiatedCapabilities,
            current.TransitionGeneration + 1, current.FirmwareTransition,
            current.ActiveAccessory, current.AccessoryTransition));
    }

    void IBridgeDeviceStateUpdater.SetControlConnectionState(
        BridgeControlConnectionKind kind, BridgeConnectionState state)
    {
        Mutate(current => Build(current.DeviceIdentity, current.DeviceVersion, ToMode(current),
            kind, state, current.SelectedNetworkPakTransport,
            state == BridgeConnectionState.Connected
                ? current.ActiveNetworkPakTransport : BridgeNetworkPakTransport.None,
            current.DeviceReportedCapabilities,
            state == BridgeConnectionState.Connected
                ? current.ApplicationNegotiatedCapabilities
                : BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.ApplicationNegotiated),
            current.TransitionGeneration + 1, current.FirmwareTransition,
            current.ActiveAccessory, current.AccessoryTransition));
    }

    void IBridgeDeviceStateUpdater.SelectNetworkPakTransport(BridgeNetworkPakTransport transport)
    {
        Mutate(current => Build(
            current.DeviceIdentity, current.DeviceVersion, ToMode(current),
            current.ControlConnectionKind, current.ControlConnectionState,
            transport,
            current.ActiveNetworkPakTransport == transport
                ? current.ActiveNetworkPakTransport : BridgeNetworkPakTransport.None,
            current.DeviceReportedCapabilities,
            current.ActiveNetworkPakTransport == transport
                ? current.ApplicationNegotiatedCapabilities
                : BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.ApplicationNegotiated),
            current.TransitionGeneration + 1, current.FirmwareTransition,
            current.ActiveAccessory, current.AccessoryTransition));
    }

    void IBridgeDeviceStateUpdater.ActivateNetworkPakTransport(BridgeNetworkPakTransport transport)
    {
        Mutate(current => Build(
            current.DeviceIdentity, current.DeviceVersion, ToMode(current),
            current.ControlConnectionKind, current.ControlConnectionState,
            current.SelectedNetworkPakTransport, transport,
            current.DeviceReportedCapabilities,
            new BridgeCapabilitySet(BridgeCapabilityProvenance.ApplicationNegotiated,
            [
                new(BridgeCapabilityId.LegacyNetworkPakStream, true,
                    BridgeCapabilityProvenance.ApplicationNegotiated)
            ]),
            current.TransitionGeneration + 1, current.FirmwareTransition,
            current.ActiveAccessory, current.AccessoryTransition));
    }

    void IBridgeDeviceStateUpdater.DeactivateNetworkPakTransport(BridgeNetworkPakTransport transport)
    {
        Mutate(current => current.ActiveNetworkPakTransport != transport
            ? current with { TransitionGeneration = current.TransitionGeneration + 1,
                TransitionTimestampUtc = DateTimeOffset.UtcNow }
            : Build(current.DeviceIdentity, current.DeviceVersion, ToMode(current),
                current.ControlConnectionKind, current.ControlConnectionState,
                current.SelectedNetworkPakTransport, BridgeNetworkPakTransport.None,
                current.DeviceReportedCapabilities,
                BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.ApplicationNegotiated),
                current.TransitionGeneration + 1, current.FirmwareTransition,
                current.ActiveAccessory, current.AccessoryTransition));
    }

    void IBridgeDeviceStateUpdater.SetApplicationCapabilities(
        BridgeCapabilitySet capabilities)
    {
        if (capabilities.Provenance != BridgeCapabilityProvenance.ApplicationNegotiated)
            throw new ArgumentException("Application capability provenance is required.", nameof(capabilities));
        Mutate(current => Build(current.DeviceIdentity, current.DeviceVersion, ToMode(current),
            current.ControlConnectionKind, current.ControlConnectionState,
            current.SelectedNetworkPakTransport, current.ActiveNetworkPakTransport,
            current.DeviceReportedCapabilities, capabilities,
            current.TransitionGeneration + 1, current.FirmwareTransition,
            current.ActiveAccessory, current.AccessoryTransition));
    }

    void IBridgeDeviceStateUpdater.SetFirmwareTransition(
        BridgeFirmwareTransitionSnapshot transition)
    {
        Mutate(current => current with
        {
            FirmwareTransition = transition,
            TransitionGeneration = current.TransitionGeneration + 1,
            TransitionTimestampUtc = DateTimeOffset.UtcNow
        });
    }

    void IBridgeDeviceStateUpdater.SetActiveAccessory(BridgeAccessoryMode accessory)
    {
        Mutate(current => current with
        {
            ActiveAccessory = accessory,
            TransitionGeneration = current.TransitionGeneration + 1,
            TransitionTimestampUtc = DateTimeOffset.UtcNow
        });
    }

    void IBridgeDeviceStateUpdater.SetAccessoryTransition(
        BridgeAccessoryTransitionSnapshot transition)
    {
        Mutate(current => current with
        {
            AccessoryTransition = transition,
            TransitionGeneration = current.TransitionGeneration + 1,
            TransitionTimestampUtc = DateTimeOffset.UtcNow
        });
    }

    void IBridgeDeviceStateUpdater.Disconnect()
    {
        Mutate(current => CreateDisconnected(current.TransitionGeneration + 1, DateTimeOffset.UtcNow));
    }

    private void Mutate(Func<BridgeDeviceStateSnapshot, BridgeDeviceStateSnapshot> update)
    {
        BridgeDeviceStateSnapshot previous;
        BridgeDeviceStateSnapshot current;
        lock (_sync)
        {
            previous = _current;
            current = update(previous);
            _current = current;
        }
        StateChanged?.Invoke(this, new(previous, current));
    }

    private static BridgeDeviceStateSnapshot Build(
        string identity, string version, FirmwareModeInfo? mode,
        BridgeControlConnectionKind connectionKind, BridgeConnectionState connectionState,
        BridgeNetworkPakTransport selectedTransport, BridgeNetworkPakTransport activeTransport,
        BridgeCapabilitySet deviceCapabilities,
        BridgeCapabilitySet applicationCapabilities,
        long generation,
        BridgeFirmwareTransitionSnapshot? firmwareTransition = null,
        BridgeAccessoryMode activeAccessory = BridgeAccessoryMode.None,
        BridgeAccessoryTransitionSnapshot? accessoryTransition = null)
    {
        (BridgeDeviceRole role, BridgeDeviceService service, FirmwarePersonality personality) = MapMode(mode);
        BridgeCapabilitySet implemented = new(BridgeCapabilityProvenance.BridgeImplemented,
        [
            new(BridgeCapabilityId.LegacyNetworkPakStream, true,
                BridgeCapabilityProvenance.BridgeImplemented),
            new(BridgeCapabilityId.BulkRead, true,
                BridgeCapabilityProvenance.BridgeImplemented)
        ]);
        BridgeCapabilitySet effective = new(BridgeCapabilityProvenance.Effective,
        [
            new(BridgeCapabilityId.LegacyNetworkPakStream,
                implemented.Supports(BridgeCapabilityId.LegacyNetworkPakStream) &&
                applicationCapabilities.Supports(BridgeCapabilityId.LegacyNetworkPakStream),
                BridgeCapabilityProvenance.Effective),
            new(BridgeCapabilityId.BulkRead,
                implemented.Supports(BridgeCapabilityId.BulkRead) &&
                applicationCapabilities.Supports(BridgeCapabilityId.BulkRead),
                BridgeCapabilityProvenance.Effective)
        ]);
        return new(identity, version, role, service, personality, connectionKind,
            connectionState, selectedTransport, activeTransport, deviceCapabilities,
            implemented, applicationCapabilities, effective, generation, DateTimeOffset.UtcNow)
        {
            FirmwareTransition = firmwareTransition ?? BridgeFirmwareTransitionSnapshot.Idle,
            ActiveAccessory = activeAccessory,
            AccessoryTransition = accessoryTransition ?? BridgeAccessoryTransitionSnapshot.Idle
        };
    }

    internal static BridgeDeviceStateSnapshot CreateDisconnected(long generation, DateTimeOffset timestamp) =>
        new(string.Empty, string.Empty, BridgeDeviceRole.Unknown, BridgeDeviceService.Unknown,
            FirmwarePersonality.Unknown, BridgeControlConnectionKind.None,
            BridgeConnectionState.Disconnected, BridgeNetworkPakTransport.None,
            BridgeNetworkPakTransport.None,
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.DeviceReported),
            new BridgeCapabilitySet(BridgeCapabilityProvenance.BridgeImplemented,
            [
                new(BridgeCapabilityId.LegacyNetworkPakStream, true,
                    BridgeCapabilityProvenance.BridgeImplemented),
                new(BridgeCapabilityId.BulkRead, false,
                    BridgeCapabilityProvenance.BridgeImplemented)
            ]),
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.ApplicationNegotiated),
            new BridgeCapabilitySet(BridgeCapabilityProvenance.Effective,
            [
                new(BridgeCapabilityId.LegacyNetworkPakStream, false,
                    BridgeCapabilityProvenance.Effective),
                new(BridgeCapabilityId.BulkRead, false, BridgeCapabilityProvenance.Effective)
            ]), generation, timestamp);

    private static (BridgeDeviceRole, BridgeDeviceService, FirmwarePersonality) MapMode(FirmwareModeInfo? mode) =>
        mode?.Personality switch
        {
            FirmwarePersonality.Controller =>
                (BridgeDeviceRole.Controller, BridgeDeviceService.None, FirmwarePersonality.Controller),
            FirmwarePersonality.Diagnostics =>
                (BridgeDeviceRole.Standalone, BridgeDeviceService.Diagnostics, FirmwarePersonality.Diagnostics),
            FirmwarePersonality.Network =>
                (BridgeDeviceRole.Standalone, BridgeDeviceService.Network, FirmwarePersonality.Network),
            FirmwarePersonality.Keyboard =>
                (BridgeDeviceRole.Standalone, BridgeDeviceService.KeyboardMouse, FirmwarePersonality.Keyboard),
            FirmwarePersonality.Mouse =>
                (BridgeDeviceRole.Standalone, BridgeDeviceService.KeyboardMouse, FirmwarePersonality.Mouse),
            _ => (BridgeDeviceRole.Unknown, BridgeDeviceService.Unknown, FirmwarePersonality.Unknown)
        };

    private static FirmwareModeInfo? ToMode(BridgeDeviceStateSnapshot state) =>
        state.FirmwarePersonality == FirmwarePersonality.Unknown
            ? null
            : state.FirmwarePersonality switch
            {
                FirmwarePersonality.Keyboard => new(state.FirmwarePersonality,
                    "STANDALONE", "KEYBOARD_MOUSE", "KEYBOARD"),
                FirmwarePersonality.Mouse => new(state.FirmwarePersonality,
                    "STANDALONE", "KEYBOARD_MOUSE", "MOUSE"),
                _ => new(state.FirmwarePersonality,
                    state.DeviceRole == BridgeDeviceRole.Controller
                        ? "CONTROLLER" : "STANDALONE",
                    state.DeviceService.ToString().ToUpperInvariant())
            };
}

public static class BridgeDeviceStateProviders
{
    public static IBridgeHostStateProvider Disconnected { get; } =
        new DisconnectedBridgeDeviceStateProvider();
}

internal sealed class DisconnectedBridgeDeviceStateProvider : IBridgeHostStateProvider
{
    public BridgeDeviceStateSnapshot Current { get; } =
        BridgeDeviceStateService.CreateDisconnected(0, DateTimeOffset.UnixEpoch);
    public BridgeCapabilitySet Capabilities => Current.EffectiveCapabilities;
    public bool Supports(BridgeCapabilityId capability) => false;
    public BridgeCapabilityDescriptor? Get(BridgeCapabilityId capability) =>
        Capabilities.Get(capability);
    public event EventHandler<BridgeDeviceStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }
}
