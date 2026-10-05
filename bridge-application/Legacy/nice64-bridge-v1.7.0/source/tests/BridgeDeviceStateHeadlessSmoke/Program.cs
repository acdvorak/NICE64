using PicoSwitchController;

BridgeDeviceStateService service = new();
IBridgeDeviceStateProvider provider = service;
IBridgeDeviceStateUpdater updater = service;
updater.Connect(BridgeControlConnectionKind.WifiTcp, "NICE64", "1.1.0",
    new(FirmwarePersonality.Network, "STANDALONE", "NETWORK"));
if (provider.Current.ControlConnectionKind != BridgeControlConnectionKind.WifiTcp ||
    service.Supports(BridgeCapabilityId.BulkRead))
    throw new InvalidOperationException("Headless state contract behavior failed.");
Console.WriteLine("PASS state/capability contracts run under plain net9.0 without WinForms");
