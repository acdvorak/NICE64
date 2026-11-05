namespace PicoSwitchController;

public sealed class Nice64PppGatewayModule : INetworkPakModule
{
    public const string ModuleId = "ppp-gateway";
    public const string ResourceName = "ppp://gateway";

    private static readonly PppAddressAssignment DefaultAddresses = new(
        Ipv4Address.FromOctets(10, 64, 0, 1),
        Ipv4Address.FromOctets(10, 64, 0, 2));

    public string Id => ModuleId;
    public string DisplayName => "NICE64 PPP Gateway";

    public static bool IsGatewayResource(string resource) =>
        string.Equals(resource, ResourceName, StringComparison.OrdinalIgnoreCase);

    public NetworkPakResource? OpenResource(string resource, ModuleContext context)
    {
        if (!IsGatewayResource(resource)) return null;
        if (context.ConnectionId == Guid.Empty)
        {
            context.Log("PPP live resource open failed: no Bridge connection identity");
            return null;
        }

        uint magic = DeriveMagic(context.ConnectionId);
        context.Log($"PPP live resource open connection={context.ConnectionId:D}");
        return NetworkPakResource.CreateLive(
            ResourceName,
            new PppLiveResource(
                context.ConnectionId,
                DefaultAddresses,
                magic,
                context));
    }

    public NetworkPakWriteResult HandleWrite(
        NetworkPakWriteChunk chunk,
        ModuleContext context) =>
        NetworkPakWriteResult.Fail("PPP_LIVE_STREAM_REQUIRED");

    private static uint DeriveMagic(Guid connectionId)
    {
        Span<byte> bytes = stackalloc byte[16];
        connectionId.TryWriteBytes(bytes);
        uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return value == 0 ? 1u : value;
    }
}
