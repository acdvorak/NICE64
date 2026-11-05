using PicoSwitchController;
using System.Reflection;
using System.Text.Json;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

DateTimeOffset created = new(2026, 9, 3, 16, 0, 0, TimeSpan.Zero);
DateTimeOffset restored = created.AddSeconds(23);
const string firstId = "8e916ed4-94bf-4a7a-b70f-8a666a661f73";
MutableSessionProvider provider = new();

BridgeApiV1Sessions empty = BridgeApiV1SessionProjector.ProjectList(provider);
Require(empty.ApiVersion == "v1" && empty.Sessions.Count == 0,
    "No active session did not project an empty v1 collection");

provider.Current = Snapshot(firstId, BridgeSessionState.Connected, created, created,
    BridgeControlConnectionKind.SerialUsb, "COM91", FirmwarePersonality.Controller,
    BridgeAccessoryMode.ControllerPak);
BridgeApiV1Sessions fresh = BridgeApiV1SessionProjector.ProjectList(provider);
Require(fresh.Sessions.Count == 1 &&
    fresh.Sessions[0].SessionId == firstId &&
    fresh.Sessions[0].State == "connected" &&
    fresh.Sessions[0].CreatedUtc == created &&
    fresh.Sessions[0].LastConnectedUtc == created &&
    fresh.Sessions[0].ConnectionKind == "usb" &&
    fresh.Sessions[0].Device.Identity == "NICE64" &&
    fresh.Sessions[0].Device.FirmwarePersonality == "controller" &&
    fresh.Sessions[0].Device.Accessory == "controller-pak",
    "Fresh USB session did not project the explicit safe contract");

provider.Current = provider.Current with
{
    FirmwarePersonality = FirmwarePersonality.Network,
    ActiveAccessory = BridgeAccessoryMode.NetworkPak
};
Require(BridgeApiV1SessionProjector.ProjectList(provider).Sessions[0].SessionId == firstId,
    "Normal semantic update changed projected session identity");

provider.Current = provider.Current with
{
    State = BridgeSessionState.TemporarilyUnavailable
};
BridgeApiV1Session temporary = BridgeApiV1SessionProjector.ProjectList(provider).Sessions[0];
Require(temporary.SessionId == firstId &&
    temporary.State == "temporarily-unavailable" &&
    temporary.Device.FirmwarePersonality == "network" &&
    temporary.Device.Accessory == "network-pak",
    "Temporary relationship did not retain identity and semantic state");

provider.Current = provider.Current with
{
    State = BridgeSessionState.Connected,
    LastConnectedUtc = restored,
    ConnectionKind = BridgeControlConnectionKind.WifiTcp,
    DisplayEndpoint = "NICE64 192.168.77.44:6464"
};
BridgeApiV1Session recovered = BridgeApiV1SessionProjector.ProjectList(provider).Sessions[0];
Require(recovered.SessionId == firstId && recovered.State == "connected" &&
    recovered.LastConnectedUtc == restored && recovered.ConnectionKind == "wifi-tcp",
    "Restored relationship did not project authoritative identity/timestamps/kind");

Require(BridgeApiV1SessionProjector.TryProjectCurrent(provider, firstId,
        out BridgeApiV1Session? resolved) && resolved == recovered,
    "Current session lookup did not resolve the authoritative session");
Require(!BridgeApiV1SessionProjector.TryProjectCurrent(provider,
        "3f9d8ed0-a03c-4a1e-9981-d50090fc5a8e", out _),
    "Stale session lookup resolved to the current relationship");

string json = JsonSerializer.Serialize(fresh,
    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
string[] forbidden =
[
    "DisplayEndpoint", "displayEndpoint", "COM91", "192.168.77.44", "6464",
    "6465", "IpAddress", "controllerEndpoint", "SerialUsb", "ControllerPak"
];
foreach (string value in forbidden)
    Require(!json.Contains(value, StringComparison.OrdinalIgnoreCase),
        $"Session projection leaked internal or endpoint value: {value}");
Require(json.Contains("\"state\":\"connected\"", StringComparison.Ordinal) &&
    json.Contains("\"connectionKind\":\"usb\"", StringComparison.Ordinal),
    "Session projection exposed enum ordinals rather than explicit symbols");

PropertyInfo[] dtoProperties = typeof(BridgeApiV1Session).GetProperties();
Require(dtoProperties.All(property => property.SetMethod is null ||
        property.SetMethod.ReturnParameter.GetRequiredCustomModifiers()
            .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit))),
    "Session DTO exposes mutable setters");
Require(fresh.Sessions.GetType().Name.Contains("ReadOnly", StringComparison.Ordinal),
    "Session list aliases mutable/internal state");

provider.Current = null;
Require(BridgeApiV1SessionProjector.ProjectList(provider).Sessions.Count == 0 &&
    !BridgeApiV1SessionProjector.TryProjectCurrent(provider, firstId, out _),
    "Ended relationship remained externally resolvable");

const string secondId = "31ecce0f-09a2-4b41-bd19-a4211ca4e845";
provider.Current = Snapshot(secondId, BridgeSessionState.Connected,
    created.AddMinutes(1), created.AddMinutes(1),
    BridgeControlConnectionKind.SerialUsb, "COM92",
    FirmwarePersonality.Diagnostics, BridgeAccessoryMode.TransferPak);
BridgeApiV1Session later = BridgeApiV1SessionProjector.ProjectList(provider).Sessions[0];
Require(later.SessionId == secondId && later.SessionId != firstId &&
    later.Device.FirmwarePersonality == "diagnostics" &&
    later.Device.Accessory == "transfer-pak",
    "Fresh later relationship did not project its new identity and symbols");

BridgeSessionSnapshot beforeProjection = provider.Current;
_ = BridgeApiV1SessionProjector.ProjectList(provider);
Require(ReferenceEquals(beforeProjection, provider.Current),
    "Read-only session projection mutated authoritative state");

Console.WriteLine("PASS Bridge API v1 session safe read-only projection");

static BridgeSessionSnapshot Snapshot(
    string id, BridgeSessionState state, DateTimeOffset created,
    DateTimeOffset lastConnected, BridgeControlConnectionKind kind,
    string endpoint, FirmwarePersonality firmware, BridgeAccessoryMode accessory) =>
    new(id, state, created, lastConnected, kind, endpoint, "NICE64", firmware, accessory);

sealed class MutableSessionProvider : IBridgeSessionProvider
{
    public BridgeSessionSnapshot? Current { get; set; }
    public event EventHandler<BridgeSessionChangedEventArgs>? CurrentChanged;

    public void RaiseChanged(BridgeSessionSnapshot? previous) =>
        CurrentChanged?.Invoke(this, new(previous, Current));
}
