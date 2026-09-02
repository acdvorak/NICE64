using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

Require(Nice64WirelessTransportContract.Magic == 0x4E363455u,
    "wire magic changed");
Require(Nice64WirelessTransportContract.TransportVersion == 1,
    "transport version changed");
Require(Nice64WirelessTransportContract.HeaderLength == 28 &&
        Nice64WirelessTransportContract.DeliveryClassOffset == 6 &&
        Nice64WirelessTransportContract.HeaderCrcOrReservedOffset == 26,
    "reserved header layout changed");
Require((byte)Nice64WirelessDeliveryClass.ReliableOrdered == 0 &&
        (byte)Nice64WirelessDeliveryClass.LatestState == 1,
    "delivery-class wire values changed");
Console.WriteLine("PASS v1 header and generic delivery-class reservation");

Require(Nice64WirelessTransportContract.ReservedDeliveryCapabilities == 0x3u,
    "reserved delivery capability mask changed");
Require(Nice64WirelessTransportContract.SelectableDeliveryCapabilities ==
        Nice64WirelessTransportContract.ReliableOrderedCapability,
    "LATEST_STATE became selectable");
Require(Nice64WirelessTransportContract.AdvertisedTransportCapabilities == 0,
    "RUDP capability was advertised before implementation");
Require(!Nice64WirelessTransportContract.FastModeVisible,
    "Fast Mode became visible before implementation");
Console.WriteLine("PASS RUDP and Fast Mode remain unadvertised/hidden");

Nice64WirelessSelection reliable = Nice64WirelessTransportContract.Select(0);
Require(reliable.Result == Nice64WirelessSelectionResult.Accepted &&
        reliable.EffectiveClass == Nice64WirelessDeliveryClass.ReliableOrdered &&
        reliable.Response == "OK RELIABLE_ORDERED",
    "RELIABLE_ORDERED selection contract failed");

Nice64WirelessSelection fast = Nice64WirelessTransportContract.Select(1);
Require(fast.Result == Nice64WirelessSelectionResult.Unsupported &&
        fast.EffectiveClass == Nice64WirelessDeliveryClass.ReliableOrdered &&
        fast.Response == "UNSUPPORTED LATEST_STATE" &&
        !Nice64WirelessTransportContract.IncomingChannelMayEnterReliablePath(1),
    "reserved FAST traffic entered or downgraded the reliable path");

Nice64WirelessSelection unknown = Nice64WirelessTransportContract.Select(2);
Require(unknown.Result == Nice64WirelessSelectionResult.Invalid &&
        unknown.EffectiveClass == Nice64WirelessDeliveryClass.ReliableOrdered &&
        !Nice64WirelessTransportContract.IncomingChannelMayEnterReliablePath(2),
    "unknown delivery class entered the reliable path");
Console.WriteLine("PASS deterministic FAST=UNSUPPORTED and unknown=INVALID safety");

BridgeSettings settings = new();
Require(settings.NetworkPakWirelessDeliveryClass ==
        Nice64WirelessDeliveryClass.ReliableOrdered,
    "Bridge settings did not default to RELIABLE_ORDERED");
Require(typeof(BridgeSettings).GetProperty(nameof(BridgeSettings.NetworkPakWirelessDeliveryClass))!
        .SetMethod?.IsPrivate == true,
    "RUDP-0 configuration scaffold became externally mutable");
Console.WriteLine("PASS hidden immutable Bridge configuration scaffold");

string firmwareHeader = File.ReadAllText(
    Path.Combine(AppContext.BaseDirectory, "firmware-wireless-transport-contract.h"));
string[] requiredFirmwareContract =
[
    "#define NICE64_WIRELESS_MAGIC 0x4E363455u",
    "#define NICE64_WIRELESS_TRANSPORT_VERSION 1u",
    "#define NICE64_WIRELESS_HEADER_LENGTH 28u",
    "NICE64_DELIVERY_RELIABLE_ORDERED = 0u",
    "NICE64_DELIVERY_LATEST_STATE = 1u",
    "NICE64_WIRELESS_ADVERTISED_TRANSPORT_CAPABILITIES 0u",
    "NICE64_DELIVERY_SELECTION_UNSUPPORTED",
    "nice64_wireless_channel_may_enter_reliable_path"
];
foreach (string required in requiredFirmwareContract)
{
    Require(firmwareHeader.Contains(required, StringComparison.Ordinal),
        $"firmware contract missing: {required}");
}
Console.WriteLine("PASS Bridge/firmware contract parity and firmware FAST rejection scaffold");

Require(BridgeConstants.BridgeVersion == "1.6.0",
    "RUDP-0 unexpectedly changed Bridge version");
Console.WriteLine("ALL RUDP-0 WIRELESS TRANSPORT CONTRACT TESTS PASSED");
