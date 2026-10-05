namespace PicoSwitchController;

public enum Nice64WirelessDeliveryClass : byte
{
    ReliableOrdered = 0,
    LatestState = 1
}

public enum Nice64WirelessSelectionResult
{
    Accepted,
    Unsupported,
    Invalid
}

public readonly record struct Nice64WirelessSelection(
    Nice64WirelessSelectionResult Result,
    Nice64WirelessDeliveryClass EffectiveClass,
    string Response);

/// <summary>
/// Shared v1 UDP envelope. RELIABLE_ORDERED and one-way controller
/// LATEST_STATE remain separate delivery semantics.
/// </summary>
public static class Nice64WirelessTransportContract
{
    public const uint Magic = 0x4E363455; // Network-order bytes: "N64U"
    public const byte TransportVersion = 1;
    public const byte HeaderLength = 28;
    public const byte MagicOffset = 0;
    public const byte VersionOffset = 4;
    public const byte FlagsOffset = 5;
    public const byte DeliveryClassOffset = 6;
    public const byte HeaderLengthOffset = 7;
    public const byte SessionIdOffset = 8;
    public const byte SequenceOffset = 12;
    public const byte AckBaseOffset = 16;
    public const byte AckBitmapOffset = 20;
    public const byte PayloadLengthOffset = 24;
    public const byte HeaderCrcOrReservedOffset = 26;

    public const uint ReliableOrderedCapability = 1u << 0;
    public const uint LatestStateCapability = 1u << 1;
    public const uint ReservedDeliveryCapabilities =
        ReliableOrderedCapability | LatestStateCapability;

    public const uint SelectableDeliveryCapabilities =
        ReliableOrderedCapability | LatestStateCapability;
    public const uint AdvertisedTransportCapabilities = LatestStateCapability;

    public const bool FastModeVisible = false;
    public static Nice64WirelessDeliveryClass DefaultDeliveryClass =>
        Nice64WirelessDeliveryClass.ReliableOrdered;

    public static Nice64WirelessSelection Select(byte requestedChannel) =>
        requestedChannel switch
        {
            (byte)Nice64WirelessDeliveryClass.ReliableOrdered => new(
                Nice64WirelessSelectionResult.Accepted,
                Nice64WirelessDeliveryClass.ReliableOrdered,
                "OK RELIABLE_ORDERED"),
            (byte)Nice64WirelessDeliveryClass.LatestState => new(
                Nice64WirelessSelectionResult.Accepted,
                Nice64WirelessDeliveryClass.LatestState,
                "OK LATEST_STATE"),
            _ => new(
                Nice64WirelessSelectionResult.Invalid,
                Nice64WirelessDeliveryClass.ReliableOrdered,
                "INVALID DELIVERY_CLASS")
        };

    public static bool IncomingChannelMayEnterReliablePath(byte channel) =>
        channel == (byte)Nice64WirelessDeliveryClass.ReliableOrdered;
}
