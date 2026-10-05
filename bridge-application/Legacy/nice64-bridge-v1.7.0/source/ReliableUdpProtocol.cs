namespace PicoSwitchController;

using System.Buffers.Binary;

[Flags]
public enum Nice64RudpPacketFlags : byte
{
    Data = 1,
    Ack = 2
}

public readonly record struct Nice64RudpHeader(
    uint Magic,
    byte Version,
    Nice64RudpPacketFlags Flags,
    byte DeliveryClass,
    byte HeaderLength,
    uint SessionId,
    uint Sequence,
    uint AckBase,
    uint AckBitmap,
    ushort PayloadLength,
    ushort HeaderCrcOrReserved);

public sealed record Nice64RudpPacket(Nice64RudpHeader Header, byte[] Payload);

public enum Nice64RudpValidationError
{
    None,
    TruncatedHeader,
    BadMagic,
    UnsupportedVersion,
    InvalidHeaderLength,
    InvalidFlags,
    UnsupportedDeliveryClass,
    InvalidDeliveryClass,
    PayloadTooLarge,
    TruncatedPayload,
    PayloadLengthMismatch,
    SessionMismatch,
    ReservedFieldNonzero
}

public static class Nice64RudpPacketCodec
{
    public const byte KnownFlags =
        (byte)(Nice64RudpPacketFlags.Data | Nice64RudpPacketFlags.Ack);

    public static byte[] Encode(Nice64RudpHeader header, ReadOnlySpan<byte> payload,
                                int maximumPayloadBytes)
    {
        Nice64RudpValidationError error = ValidateHeader(
            header, payload.Length, maximumPayloadBytes, expectedSessionId: null);
        if (error != Nice64RudpValidationError.None)
            throw new ArgumentException($"Invalid NICE64 RUDP packet: {error}.", nameof(header));

        byte[] wire = new byte[Nice64WirelessTransportContract.HeaderLength + payload.Length];
        Span<byte> span = wire;
        BinaryPrimitives.WriteUInt32BigEndian(span[0..4], header.Magic);
        span[4] = header.Version;
        span[5] = (byte)header.Flags;
        span[6] = header.DeliveryClass;
        span[7] = header.HeaderLength;
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], header.SessionId);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..16], header.Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(span[16..20], header.AckBase);
        BinaryPrimitives.WriteUInt32BigEndian(span[20..24], header.AckBitmap);
        BinaryPrimitives.WriteUInt16BigEndian(span[24..26], header.PayloadLength);
        BinaryPrimitives.WriteUInt16BigEndian(span[26..28], header.HeaderCrcOrReserved);
        payload.CopyTo(span[Nice64WirelessTransportContract.HeaderLength..]);
        return wire;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> wire,
        int maximumPayloadBytes,
        uint? expectedSessionId,
        out Nice64RudpPacket? packet,
        out Nice64RudpValidationError error)
    {
        packet = null;
        if (wire.Length < Nice64WirelessTransportContract.HeaderLength)
        {
            error = Nice64RudpValidationError.TruncatedHeader;
            return false;
        }

        Nice64RudpHeader header = new(
            BinaryPrimitives.ReadUInt32BigEndian(wire[0..4]),
            wire[4],
            (Nice64RudpPacketFlags)wire[5],
            wire[6],
            wire[7],
            BinaryPrimitives.ReadUInt32BigEndian(wire[8..12]),
            BinaryPrimitives.ReadUInt32BigEndian(wire[12..16]),
            BinaryPrimitives.ReadUInt32BigEndian(wire[16..20]),
            BinaryPrimitives.ReadUInt32BigEndian(wire[20..24]),
            BinaryPrimitives.ReadUInt16BigEndian(wire[24..26]),
            BinaryPrimitives.ReadUInt16BigEndian(wire[26..28]));

        int requiredLength = Nice64WirelessTransportContract.HeaderLength + header.PayloadLength;
        error = ValidateHeader(header, wire.Length - Nice64WirelessTransportContract.HeaderLength,
                               maximumPayloadBytes, expectedSessionId);
        if (error == Nice64RudpValidationError.None && wire.Length < requiredLength)
            error = Nice64RudpValidationError.TruncatedPayload;
        if (error == Nice64RudpValidationError.None && wire.Length != requiredLength)
            error = Nice64RudpValidationError.PayloadLengthMismatch;
        if (error != Nice64RudpValidationError.None) return false;

        packet = new Nice64RudpPacket(header,
            wire[Nice64WirelessTransportContract.HeaderLength..requiredLength].ToArray());
        return true;
    }

    private static Nice64RudpValidationError ValidateHeader(
        Nice64RudpHeader header,
        int actualPayloadLength,
        int maximumPayloadBytes,
        uint? expectedSessionId)
    {
        if (maximumPayloadBytes is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        if (header.Magic != Nice64WirelessTransportContract.Magic)
            return Nice64RudpValidationError.BadMagic;
        if (header.Version != Nice64WirelessTransportContract.TransportVersion)
            return Nice64RudpValidationError.UnsupportedVersion;
        if (header.HeaderLength != Nice64WirelessTransportContract.HeaderLength)
            return Nice64RudpValidationError.InvalidHeaderLength;

        byte flags = (byte)header.Flags;
        if (flags == 0 || (flags & ~KnownFlags) != 0 ||
            ((header.Flags & Nice64RudpPacketFlags.Data) == 0 && header.PayloadLength != 0))
            return Nice64RudpValidationError.InvalidFlags;

        Nice64WirelessSelection selection =
            Nice64WirelessTransportContract.Select(header.DeliveryClass);
        if (selection.Result == Nice64WirelessSelectionResult.Unsupported)
            return Nice64RudpValidationError.UnsupportedDeliveryClass;
        if (selection.Result == Nice64WirelessSelectionResult.Invalid)
            return Nice64RudpValidationError.InvalidDeliveryClass;
        if (header.PayloadLength > maximumPayloadBytes)
            return Nice64RudpValidationError.PayloadTooLarge;
        if (header.PayloadLength != actualPayloadLength)
            return actualPayloadLength < header.PayloadLength
                ? Nice64RudpValidationError.TruncatedPayload
                : Nice64RudpValidationError.PayloadLengthMismatch;
        if (expectedSessionId.HasValue && header.SessionId != expectedSessionId.Value)
            return Nice64RudpValidationError.SessionMismatch;
        if (header.HeaderCrcOrReserved != 0)
            return Nice64RudpValidationError.ReservedFieldNonzero;
        return Nice64RudpValidationError.None;
    }
}

public static class Nice64RudpSequence
{
    public const uint HalfRange = 0x80000000u;

    public static uint ForwardDistance(uint from, uint to) => unchecked(to - from);

    public static bool IsAfter(uint candidate, uint reference)
    {
        uint distance = ForwardDistance(reference, candidate);
        return distance != 0 && distance < HalfRange;
    }

    public static bool IsBefore(uint candidate, uint reference) =>
        IsAfter(reference, candidate);

    public static bool IsWithinForwardWindow(uint sequence, uint first, int windowSize)
    {
        if (windowSize is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(windowSize));
        return ForwardDistance(first, sequence) < (uint)windowSize;
    }
}

public sealed class Nice64RudpDiagnostics
{
    public long TxDatagrams { get; internal set; }
    public long RxDatagrams { get; internal set; }
    public long TxPayloadBytes { get; internal set; }
    public long RxPayloadBytes { get; internal set; }
    public long AcksSent { get; internal set; }
    public long AcksReceived { get; internal set; }
    public long Retransmissions { get; internal set; }
    public long DuplicateRx { get; internal set; }
    public long OutOfOrderRx { get; internal set; }
    public long OldOrStaleRx { get; internal set; }
    public long InvalidHeader { get; internal set; }
    public long InvalidDeliveryClass { get; internal set; }
    public long UnsupportedDeliveryClass { get; internal set; }
    public long SessionMismatch { get; internal set; }
    public long WindowFullEvents { get; internal set; }
    public long RetryExhaustion { get; internal set; }
    public long SessionResets { get; internal set; }
    public int MaxRetryCount { get; internal set; }
}
