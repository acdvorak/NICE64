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
        Nice64WirelessTransportContract.ReservedDeliveryCapabilities,
    "LATEST_STATE was not enabled beside RELIABLE_ORDERED");
Require(Nice64WirelessTransportContract.AdvertisedTransportCapabilities ==
        Nice64WirelessTransportContract.LatestStateCapability,
    "LATEST_STATE controller capability was not advertised");
Require(!Nice64WirelessTransportContract.FastModeVisible,
    "Fast Mode became visible before implementation");
Console.WriteLine("PASS LATEST_STATE is enabled while generic Fast UI remains hidden");

Nice64WirelessSelection reliable = Nice64WirelessTransportContract.Select(0);
Require(reliable.Result == Nice64WirelessSelectionResult.Accepted &&
        reliable.EffectiveClass == Nice64WirelessDeliveryClass.ReliableOrdered &&
        reliable.Response == "OK RELIABLE_ORDERED",
    "RELIABLE_ORDERED selection contract failed");

Nice64WirelessSelection fast = Nice64WirelessTransportContract.Select(1);
Require(fast.Result == Nice64WirelessSelectionResult.Accepted &&
        fast.EffectiveClass == Nice64WirelessDeliveryClass.LatestState &&
        fast.Response == "OK LATEST_STATE" &&
        !Nice64WirelessTransportContract.IncomingChannelMayEnterReliablePath(1),
    "LATEST_STATE was not accepted separately from the reliable path");

Nice64WirelessSelection unknown = Nice64WirelessTransportContract.Select(2);
Require(unknown.Result == Nice64WirelessSelectionResult.Invalid &&
        unknown.EffectiveClass == Nice64WirelessDeliveryClass.ReliableOrdered &&
        !Nice64WirelessTransportContract.IncomingChannelMayEnterReliablePath(2),
    "unknown delivery class entered the reliable path");
Console.WriteLine("PASS LATEST_STATE acceptance and reliable-engine isolation");

BridgeSettings settings = new();
Require(settings.NetworkPakWirelessDeliveryClass ==
        Nice64WirelessDeliveryClass.ReliableOrdered,
    "Bridge settings did not default to RELIABLE_ORDERED");
Require(typeof(BridgeSettings).GetProperty(nameof(BridgeSettings.NetworkPakWirelessDeliveryClass))!
        .SetMethod?.IsPrivate == true,
    "RUDP-0 configuration scaffold became externally mutable");
Console.WriteLine("PASS hidden immutable Bridge configuration scaffold");

Require(settings.AskToNameNice64SessionWhenConnecting,
    "connect-time NICE64 naming prompt did not default enabled");
string settingsDirectory = Path.Combine(Path.GetTempPath(),
    "nice64-bridge-identity-" + Guid.NewGuid().ToString("N"));
string settingsPath = Path.Combine(settingsDirectory, "picoswitchcontroller.ini");
try
{
    BridgeSettings.SaveAskToNameNice64SessionWhenConnecting(settingsPath, false);
    BridgeSettings.SaveBridgeInstanceName(settingsPath, "Player 3");
    BridgeSettings retained = BridgeSettings.Load(settingsPath);
    Require(!retained.AskToNameNice64SessionWhenConnecting &&
            retained.BridgeInstanceName == "Player 3",
        "Bridge prompt preference or friendly name was not retained");
}
finally
{
    if (Directory.Exists(settingsDirectory))
        Directory.Delete(settingsDirectory, recursive: true);
}
Console.WriteLine("PASS Bridge naming preference and friendly-name persistence");

string firmwareHeader = File.ReadAllText(
    Path.Combine(AppContext.BaseDirectory, "firmware-wireless-transport-contract.h"));
string[] requiredFirmwareContract =
[
    "#define NICE64_WIRELESS_MAGIC 0x4E363455u",
    "#define NICE64_WIRELESS_TRANSPORT_VERSION 1u",
    "#define NICE64_WIRELESS_HEADER_LENGTH 28u",
    "NICE64_DELIVERY_RELIABLE_ORDERED = 0u",
    "NICE64_DELIVERY_LATEST_STATE = 1u",
    "NICE64_WIRELESS_CAP_LATEST_STATE",
    "NICE64_WIRELESS_ADVERTISED_TRANSPORT_CAPABILITIES",
    "return NICE64_DELIVERY_SELECTION_ACCEPTED",
    "nice64_wireless_channel_may_enter_reliable_path"
];
foreach (string required in requiredFirmwareContract)
{
    Require(firmwareHeader.Contains(required, StringComparison.Ordinal),
        $"firmware contract missing: {required}");
}
Console.WriteLine("PASS Bridge/firmware contract parity for separate LATEST_STATE delivery");

string firmwareLatestState = File.ReadAllText(
    Path.Combine(AppContext.BaseDirectory, "firmware-nice64-latest-state.c"));
string firmwareUdpService = File.ReadAllText(
    Path.Combine(AppContext.BaseDirectory, "firmware-nice64-udp-service.c"));
Require(firmwareLatestState.Contains("distance != 0u && distance < 0x80000000u",
            StringComparison.Ordinal) &&
        firmwareLatestState.Contains("controller_state_publish_raw(payload)",
            StringComparison.Ordinal) &&
        firmwareLatestState.Contains("session_is_retired(session_id)",
            StringComparison.Ordinal) &&
        firmwareLatestState.Contains("NICE64_LATEST_STATE_RETIRED_SESSIONS",
            StringComparison.Ordinal) &&
        firmwareUdpService.Contains("process_latest_state", StringComparison.Ordinal) &&
        firmwareUdpService.Contains("header.delivery_class == NICE64_DELIVERY_LATEST_STATE",
            StringComparison.Ordinal),
    "firmware LATEST_STATE ingress lost wrap-safe newest-only publication");
string[] forbiddenLatestStateReliability =
[
    "nice64_rudpc_tx_try_enqueue", "nice64_rudpc_tx_poll",
    "nice64_rudpc_rx_receive", "send_reliable_ack", "send_control"
];
Require(forbiddenLatestStateReliability.All(value =>
        !firmwareLatestState.Contains(value, StringComparison.Ordinal)),
    "firmware LATEST_STATE module acquired ACK/window/retransmission behavior");
Console.WriteLine("PASS firmware LATEST_STATE publishes through the existing nonblocking state boundary without reliability");

Require(BridgeConstants.BridgeVersion == "1.8.0",
    "RUDP-0 unexpectedly changed Bridge version");
Require(BridgeConstants.ProductDisplayName == "PicoSwitchController v1.8.0",
    "Bridge product title/version reporting is inconsistent");
Console.WriteLine("ALL WIRELESS TRANSPORT CONTRACT TESTS PASSED");
