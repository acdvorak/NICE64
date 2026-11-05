using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static BridgeCapabilitySet ReportedModes() =>
    FirmwareModeProtocol.ParseCapabilities(
        "CAPS|CONTROLLER|STANDALONE/DIAGNOSTICS|STANDALONE/NETWORK");

BridgeDeviceStateService service = new();
IBridgeDeviceStateProvider provider = service;
IBridgeCapabilityProvider capabilities = service;
IBridgeDeviceStateUpdater updater = service;

BridgeDeviceStateSnapshot initial = provider.Current;
Require(initial.ControlConnectionState == BridgeConnectionState.Disconnected &&
        initial.DeviceRole == BridgeDeviceRole.Unknown &&
        initial.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None,
    "initial state was not atomically disconnected");
Require(!capabilities.Supports(BridgeCapabilityId.BulkRead),
    "BULK_READ was enabled in Checkpoint B");
Console.WriteLine("PASS initial disconnected state and BULK_READ=false");

updater.SelectNetworkPakTransport(BridgeNetworkPakTransport.ReliableUdpQuick);
Require(provider.Current.SelectedNetworkPakTransport == BridgeNetworkPakTransport.ReliableUdpQuick &&
        provider.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None &&
        !capabilities.Supports(BridgeCapabilityId.BulkRead),
    "Quick selection inferred an active transport or BULK_READ capability");
Console.WriteLine("PASS Quick selected but inactive derives no capability");

updater.Connect(BridgeControlConnectionKind.SerialUsb, "NICE64", "1.1.0",
    new(FirmwarePersonality.Controller, "CONTROLLER", "NONE"), ReportedModes());
updater.SelectNetworkPakTransport(BridgeNetworkPakTransport.UsbSerial);
Require(provider.Current.ControlConnectionKind == BridgeControlConnectionKind.SerialUsb &&
        provider.Current.DeviceRole == BridgeDeviceRole.Controller &&
        provider.Current.DeviceService == BridgeDeviceService.None &&
        provider.Current.SelectedNetworkPakTransport == BridgeNetworkPakTransport.UsbSerial,
    "Serial/USB Controller state mapping failed");
Console.WriteLine("PASS Serial/USB and Controller mapping");

updater.UpdateFirmwareMode(new(FirmwarePersonality.Diagnostics,
    "STANDALONE", "DIAGNOSTICS"));
Require(provider.Current.DeviceRole == BridgeDeviceRole.Standalone &&
        provider.Current.DeviceService == BridgeDeviceService.Diagnostics,
    "Diagnostics mode mapping failed");
Console.WriteLine("PASS Diagnostics mapping");

updater.Disconnect();
updater.SelectNetworkPakTransport(BridgeNetworkPakTransport.Tcp);
updater.Connect(BridgeControlConnectionKind.WifiTcp, "NICE64", "1.1.0",
    new(FirmwarePersonality.Network, "STANDALONE", "NETWORK"), ReportedModes());
Require(provider.Current.ControlConnectionKind == BridgeControlConnectionKind.WifiTcp &&
        provider.Current.DeviceService == BridgeDeviceService.Network &&
        provider.Current.SelectedNetworkPakTransport == BridgeNetworkPakTransport.Tcp &&
        provider.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None,
    "Wi-Fi/TCP Standalone Network mapping failed");
Console.WriteLine("PASS Wi-Fi/TCP, Standalone Network, and TCP-selected state");

updater.ActivateNetworkPakTransport(BridgeNetworkPakTransport.Tcp);
Require(provider.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.Tcp &&
        capabilities.Supports(BridgeCapabilityId.LegacyNetworkPakStream) &&
        !capabilities.Supports(BridgeCapabilityId.BulkRead),
    "TCP activation state or explicit legacy capability failed");

updater.SelectNetworkPakTransport(BridgeNetworkPakTransport.ReliableUdpQuick);
Require(provider.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None &&
        !capabilities.Supports(BridgeCapabilityId.LegacyNetworkPakStream),
    "transport selection did not clear the old active application transport");
updater.ActivateNetworkPakTransport(BridgeNetworkPakTransport.ReliableUdpQuick);
Require(provider.Current.ControlConnectionKind == BridgeControlConnectionKind.WifiTcp &&
        provider.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.ReliableUdpQuick &&
        capabilities.Supports(BridgeCapabilityId.LegacyNetworkPakStream) &&
        !capabilities.Supports(BridgeCapabilityId.BulkRead),
    "Quick activation hid TCP control state or inferred BULK_READ");
Console.WriteLine("PASS TCP and Quick active-state separation");

updater.DeactivateNetworkPakTransport(BridgeNetworkPakTransport.ReliableUdpQuick);
Require(provider.Current.SelectedNetworkPakTransport == BridgeNetworkPakTransport.ReliableUdpQuick &&
        provider.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None &&
        !capabilities.Supports(BridgeCapabilityId.LegacyNetworkPakStream),
    "Quick failure/disposal did not preserve selection while clearing live state");
Console.WriteLine("PASS Quick failure/disposal state");

NetworkPakBridgeService networkPak = new(deviceState: service);
ModuleContext moduleContext = networkPak.CreateModuleContextForTesting();
Require(ReferenceEquals(moduleContext.DeviceState, provider) &&
        ReferenceEquals(moduleContext.Capabilities, provider),
    "NetworkPakBridgeService and ModuleContext did not share the host provider instance");
Console.WriteLine("PASS NetworkPakBridgeService and modules share the provider instance");

long generationBeforeDisconnect = provider.Current.TransitionGeneration;
updater.Disconnect();
BridgeDeviceStateSnapshot disconnected = provider.Current;
Require(disconnected.TransitionGeneration > generationBeforeDisconnect &&
        disconnected.ControlConnectionKind == BridgeControlConnectionKind.None &&
        disconnected.ControlConnectionState == BridgeConnectionState.Disconnected &&
        disconnected.DeviceRole == BridgeDeviceRole.Unknown &&
        disconnected.SelectedNetworkPakTransport == BridgeNetworkPakTransport.None &&
        disconnected.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None &&
        !disconnected.EffectiveCapabilities.Items.Any(item => item.Supported),
    "disconnect did not atomically clear all live state and effective capabilities");
Console.WriteLine("PASS atomic disconnect clearing");

Console.WriteLine("ALL BRIDGE DEVICE STATE TESTS PASSED");
