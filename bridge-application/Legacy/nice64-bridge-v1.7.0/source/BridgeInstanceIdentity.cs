namespace PicoSwitchController;

public sealed record BridgeInstanceSnapshot(string InstanceId, string Name);

public interface IBridgeInstanceStateProvider
{
    BridgeInstanceSnapshot Current { get; }
}

public sealed class BridgeInstanceIdentity : IBridgeInstanceStateProvider
{
    private readonly object _sync = new();
    private readonly string _instanceId;
    private string _name;

    public BridgeInstanceIdentity()
        : this(Guid.NewGuid(), BridgeInstanceNamingPolicy.DefaultUnconnectedName)
    {
    }

    internal BridgeInstanceIdentity(Guid instanceId, string? name)
    {
        if (instanceId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(instanceId));
        _instanceId = instanceId.ToString("D");
        _name = BridgeInstanceNamingPolicy.NormalizeName(name,
            BridgeInstanceNamingPolicy.DefaultUnconnectedName);
    }

    public BridgeInstanceSnapshot Current
    {
        get { lock (_sync) return new(_instanceId, _name); }
    }

    public void SetName(string? name)
    {
        lock (_sync)
            _name = BridgeInstanceNamingPolicy.NormalizeName(name,
                BridgeInstanceNamingPolicy.DefaultUnconnectedName);
    }
}

public static class BridgeInstanceNamingPolicy
{
    public const string DefaultUnconnectedName = "NICE64 Bridge";
    public const int MaximumNameLength = 80;

    public static string EndpointDefault(
        BridgeControlConnectionKind connectionKind,
        string endpoint)
    {
        string cleanEndpoint = NormalizeName(endpoint, "Unknown");
        string result = connectionKind switch
        {
            BridgeControlConnectionKind.WifiTcp => $"NICE64 - {cleanEndpoint}",
            BridgeControlConnectionKind.SerialUsb => $"NICE64 - {cleanEndpoint}",
            _ => DefaultUnconnectedName
        };
        return NormalizeName(result, DefaultUnconnectedName);
    }

    public static string ResolveConnectName(
        BridgeControlConnectionKind connectionKind,
        string endpoint,
        bool askToName,
        Func<string, string?>? prompt)
    {
        string endpointDefault = EndpointDefault(connectionKind, endpoint);
        if (!askToName) return endpointDefault;
        string? entered = prompt?.Invoke(endpointDefault);
        return NormalizeName(entered, endpointDefault);
    }

    public static string NormalizeName(string? value, string fallback)
    {
        string safe = new((value ?? string.Empty)
            .Where(character => !char.IsControl(character)).ToArray());
        safe = safe.Trim();
        if (safe.Length == 0) safe = fallback;
        return safe.Length <= MaximumNameLength ? safe : safe[..MaximumNameLength];
    }
}
