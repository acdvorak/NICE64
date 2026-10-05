using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

NetworkPakWifiTransport[] choices =
[
    NetworkPakWifiTransport.UsbSerial,
    NetworkPakWifiTransport.Tcp,
    NetworkPakWifiTransport.ReliableUdpQuick
];
Require(choices.Select(NetworkPakCarrierSelection.ToBridgeTransport).Distinct().Count() == 3,
    "USB, TCP, and Quick do not have unique carrier identities");
Console.WriteLine("PASS USB/TCP/Quick are three explicit, mutually exclusive carriers");

Require(NetworkPakCarrierSelection.ResolveAvailableLineCarrier(
            NetworkPakWifiTransport.UsbSerial, usbConnected: true, tcpConnected: true) ==
        BridgeNetworkPakTransport.UsbSerial &&
        NetworkPakCarrierSelection.ResolveAvailableLineCarrier(
            NetworkPakWifiTransport.Tcp, usbConnected: true, tcpConnected: true) ==
        BridgeNetworkPakTransport.Tcp &&
        NetworkPakCarrierSelection.ResolveAvailableLineCarrier(
            NetworkPakWifiTransport.ReliableUdpQuick, usbConnected: true, tcpConnected: true) ==
        BridgeNetworkPakTransport.None,
    "live connection priority overrode the selected application carrier");
Console.WriteLine("PASS selected application carrier is independent of live connection priority");

Require(NetworkPakCarrierSelection.ResolveAvailableLineCarrier(
            NetworkPakWifiTransport.Tcp, usbConnected: true, tcpConnected: false) ==
        BridgeNetworkPakTransport.None &&
        NetworkPakCarrierSelection.ResolveAvailableLineCarrier(
            NetworkPakWifiTransport.UsbSerial, usbConnected: false, tcpConnected: true) ==
        BridgeNetworkPakTransport.None,
    "an unavailable selected carrier silently fell back to another connection");
Console.WriteLine("PASS unavailable carriers do not silently fall back");

foreach (NetworkPakWifiTransport selected in choices)
{
    BridgeNetworkPakTransport expected = NetworkPakCarrierSelection.ToBridgeTransport(selected);
    foreach (BridgeNetworkPakTransport source in new[]
             {
                 BridgeNetworkPakTransport.UsbSerial,
                 BridgeNetworkPakTransport.Tcp
             })
    {
        Require(NetworkPakCarrierSelection.AcceptsLineCarrier(selected, source) ==
                (source == expected && selected != NetworkPakWifiTransport.ReliableUdpQuick),
            $"line routing accepted the wrong source for {selected}");
    }
}
Console.WriteLine("PASS line requests are accepted only from the selected USB or TCP carrier");

NetworkPakCarrierTransitionPlan tcpToUsb = NetworkPakCarrierSelection.Plan(
    NetworkPakWifiTransport.Tcp, NetworkPakWifiTransport.UsbSerial);
Require(tcpToUsb.ReleasedCarrier == BridgeNetworkPakTransport.Tcp &&
        tcpToUsb.StartedCarrier == BridgeNetworkPakTransport.UsbSerial &&
        !tcpToUsb.ReleasesReliableUdp && !tcpToUsb.StartsReliableUdp,
    "TCP to USB did not release TCP before selecting USB");
NetworkPakCarrierTransitionPlan quickToUsb = NetworkPakCarrierSelection.Plan(
    NetworkPakWifiTransport.ReliableUdpQuick, NetworkPakWifiTransport.UsbSerial);
Require(quickToUsb.ReleasesReliableUdp &&
        quickToUsb.StartedCarrier == BridgeNetworkPakTransport.UsbSerial,
    "Quick to USB did not release RUDP before selecting USB");
NetworkPakCarrierTransitionPlan usbToTcp = NetworkPakCarrierSelection.Plan(
    NetworkPakWifiTransport.UsbSerial, NetworkPakWifiTransport.Tcp);
Require(usbToTcp.ReleasedCarrier == BridgeNetworkPakTransport.UsbSerial &&
        usbToTcp.StartedCarrier == BridgeNetworkPakTransport.Tcp,
    "USB to TCP did not route the next session to TCP");
NetworkPakCarrierTransitionPlan usbToQuick = NetworkPakCarrierSelection.Plan(
    NetworkPakWifiTransport.UsbSerial, NetworkPakWifiTransport.ReliableUdpQuick);
Require(usbToQuick.ReleasedCarrier == BridgeNetworkPakTransport.UsbSerial &&
        usbToQuick.StartsReliableUdp,
    "USB to Quick did not release USB before starting RUDP");
Console.WriteLine("PASS TCP/Quick/USB transition cleanup and next-carrier routing plans");

string settingsPath = Path.Combine(Path.GetTempPath(),
    $"nice64-carrier-selection-{Guid.NewGuid():N}.ini");
try
{
    foreach (NetworkPakWifiTransport selected in choices)
    {
        NetworkPakTransportSettings.Save(settingsPath, selected);
        Require(NetworkPakTransportSettings.Load(settingsPath) == selected,
            $"{selected} did not persist and reload exactly");
    }
}
finally
{
    if (File.Exists(settingsPath)) File.Delete(settingsPath);
}
Console.WriteLine("PASS USB/TCP/Quick persistence reloads exact carrier identity");
Console.WriteLine("ALL NETWORK PAK CARRIER SELECTION TESTS PASSED");
