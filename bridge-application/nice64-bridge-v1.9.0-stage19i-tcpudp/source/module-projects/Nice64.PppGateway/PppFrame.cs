namespace PicoSwitchController;

/// <summary>
/// One canonical PPP frame after asynchronous framing, escaping, and FCS have
/// been removed. Protocol interpretation belongs to higher protocol layers.
/// </summary>
public sealed record PppFrame(ushort Protocol, ReadOnlyMemory<byte> Information);
