using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static FirmwareModeInfo Mode(FirmwarePersonality personality) =>
    new(personality,
        personality == FirmwarePersonality.Controller ? "CONTROLLER" : "STANDALONE",
        personality == FirmwarePersonality.Network ? "NETWORK" :
        personality == FirmwarePersonality.Diagnostics ? "DIAGNOSTICS" : "NONE");

static void ApplyDevice(BridgeDeviceStateService device,
    BridgeControlConnectionKind kind, FirmwarePersonality personality,
    BridgeAccessoryMode accessory = BridgeAccessoryMode.None)
{
    IBridgeDeviceStateUpdater updater = device;
    updater.Connect(kind, "NICE64", "", Mode(personality));
    updater.SetActiveAccessory(accessory);
}

static BridgeSessionConnectionFacts Usb(string endpoint = "COM7") =>
    new(BridgeControlConnectionKind.SerialUsb, endpoint, "NICE64");

static BridgeSessionConnectionFacts Tcp(string endpoint = "NICE64 192.0.2.7:6464") =>
    new(BridgeControlConnectionKind.WifiTcp, endpoint, "NICE64");

Guid firstId = Guid.Parse("11111111-1111-4111-8111-111111111111");
Guid secondId = Guid.Parse("22222222-2222-4222-8222-222222222222");
Queue<Guid> ids = new([firstId, secondId]);
ManualTimeProvider time = new(new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));
BridgeDeviceStateService device = new();
using BridgeSessionService sessions = new(device, time, () => ids.Dequeue());
IBridgeSessionLifecycle lifecycle = sessions;

Require(sessions.Current is null, "1 no connection created a session");
ApplyDevice(device, BridgeControlConnectionKind.SerialUsb,
    FirmwarePersonality.Controller);
lifecycle.FreshConnected(Usb());
BridgeSessionSnapshot first = sessions.Current ??
    throw new InvalidOperationException("2 fresh connection did not create a session");
Require(Guid.TryParse(first.SessionId, out Guid parsed) && parsed != Guid.Empty &&
    first.SessionId == firstId.ToString("D"),
    "3 session ID was not a non-empty canonical UUID");
Require(first.State == BridgeSessionState.Connected &&
    first.ConnectionKind == BridgeControlConnectionKind.SerialUsb &&
    first.DisplayEndpoint == "COM7" && first.DeviceIdentity == "NICE64",
    "25 fresh connection facts were not represented truthfully");
Require(first.CreatedUtc == time.GetUtcNow() &&
    first.LastConnectedUtc == time.GetUtcNow(),
    "relationship timestamps were not initialized truthfully");

lifecycle.FreshConnected(Usb());
Require(sessions.Current!.SessionId == first.SessionId && ids.Count == 1,
    "20 duplicate fresh signal created a second session");

IBridgeDeviceStateUpdater updater = device;
updater.UpdateFirmwareMode(Mode(FirmwarePersonality.Network));
Require(sessions.Current!.SessionId == first.SessionId &&
    sessions.Current.FirmwarePersonality == FirmwarePersonality.Network,
    "5/26 normal personality update replaced the session");
updater.SetActiveAccessory(BridgeAccessoryMode.NetworkPak);
Require(sessions.Current!.SessionId == first.SessionId &&
    sessions.Current.ActiveAccessory == BridgeAccessoryMode.NetworkPak,
    "26 accessory update replaced or failed to update the session");

lifecycle.TemporarilyUnavailable();
BridgeSessionSnapshot unavailable = sessions.Current!;
Require(unavailable.SessionId == first.SessionId,
    "9 temporary loss replaced the session ID");
Require(unavailable.State == BridgeSessionState.TemporarilyUnavailable,
    "10 temporary loss did not publish TemporarilyUnavailable");
lifecycle.TemporarilyUnavailable();
Require(Equals(unavailable, sessions.Current),
    "21 duplicate temporary-loss signal was not idempotent");

time.Advance(TimeSpan.FromSeconds(3));
updater.UpdateFirmwareMode(Mode(FirmwarePersonality.Controller));
lifecycle.Restored(Usb());
BridgeSessionSnapshot restored = sessions.Current!;
Require(restored.SessionId == first.SessionId &&
    restored.State == BridgeSessionState.Connected &&
    restored.LastConnectedUtc == time.GetUtcNow(),
    "11/12 restore or serial auto-reconnect did not preserve the session");
Require(restored.ConnectionKind == BridgeControlConnectionKind.SerialUsb,
    "30 USB continuity migrated its control transport");
BridgeSessionSnapshot duplicateRestore = restored;
lifecycle.Restored(Usb());
Require(Equals(duplicateRestore, sessions.Current),
    "22 duplicate restore signal was not idempotent");

lifecycle.TemporarilyUnavailable();
updater.UpdateFirmwareMode(Mode(FirmwarePersonality.Network));
lifecycle.Restored(Usb());
Require(sessions.Current!.SessionId == first.SessionId &&
    sessions.Current.FirmwarePersonality == FirmwarePersonality.Network &&
    sessions.Current.ConnectionKind == BridgeControlConnectionKind.SerialUsb,
    "6/7 Controller-to-Network firmware replacement broke USB continuity");
lifecycle.TemporarilyUnavailable();
updater.UpdateFirmwareMode(Mode(FirmwarePersonality.Controller));
lifecycle.Restored(Usb());
Require(sessions.Current!.SessionId == first.SessionId &&
    sessions.Current.FirmwarePersonality == FirmwarePersonality.Controller,
    "8 Network-to-Controller firmware replacement replaced the session");

lifecycle.Ended();
Require(sessions.Current is null, "13 genuine disconnect retained a session");
lifecycle.Restored(Usb());
Require(sessions.Current is null, "23 restore resurrected an ended session");

ApplyDevice(device, BridgeControlConnectionKind.SerialUsb,
    FirmwarePersonality.Controller);
lifecycle.FreshConnected(Usb("COM7"));
BridgeSessionSnapshot second = sessions.Current!;
Require(second.SessionId != first.SessionId,
    "4/16/17 same COM after ended relationship reused the old ID");
lifecycle.Ended();
Require(sessions.Current is null, "14 explicit disconnect retained a session");

BridgeDeviceStateService tcpDevice = new();
ApplyDevice(tcpDevice, BridgeControlConnectionKind.WifiTcp,
    FirmwarePersonality.Network);
using BridgeSessionService tcpSessions = new(tcpDevice, time,
    () => Guid.Parse("33333333-3333-4333-8333-333333333333"));
IBridgeSessionLifecycle tcpLifecycle = tcpSessions;
tcpLifecycle.FreshConnected(Tcp());
string tcpId = tcpSessions.Current!.SessionId;
tcpLifecycle.TemporarilyUnavailable();
updater = tcpDevice;
updater.UpdateFirmwareMode(Mode(FirmwarePersonality.Controller));
tcpLifecycle.Restored(Tcp());
Require(tcpSessions.Current!.SessionId == tcpId &&
    tcpSessions.Current.ConnectionKind == BridgeControlConnectionKind.WifiTcp,
    "31 Wi-Fi TCP firmware continuity migrated or replaced the relationship");
tcpLifecycle.Ended();
Require(tcpSessions.Current is null,
    "32 failed same-transport recovery did not end the relationship");
tcpLifecycle.Restored(Tcp());
Require(tcpSessions.Current is null,
    "32 failed recovery later resurrected through endpoint equality");
ApplyDevice(tcpDevice, BridgeControlConnectionKind.WifiTcp,
    FirmwarePersonality.Controller);
using BridgeSessionService sameIpFresh = new(tcpDevice, time,
    () => Guid.Parse("44444444-4444-4444-8444-444444444444"));
((IBridgeSessionLifecycle)sameIpFresh).FreshConnected(Tcp());
Require(sameIpFresh.Current!.SessionId != tcpId,
    "18 same IP after genuine disconnect resurrected the old ID");

BridgeDeviceStateService restartedDevice = new();
ApplyDevice(restartedDevice, BridgeControlConnectionKind.SerialUsb,
    FirmwarePersonality.Controller);
using BridgeSessionService restarted = new(restartedDevice, time,
    () => Guid.Parse("55555555-5555-4555-8555-555555555555"));
((IBridgeSessionLifecycle)restarted).FreshConnected(Usb());
Require(restarted.Current!.SessionId != first.SessionId &&
    restarted.Current.SessionId != second.SessionId,
    "19 Bridge restart model persisted/reused a relationship ID");

BridgeDeviceStateService concurrentDevice = new();
ApplyDevice(concurrentDevice, BridgeControlConnectionKind.SerialUsb,
    FirmwarePersonality.Controller);
int generated = 0;
using BridgeSessionService concurrent = new(concurrentDevice, time, () =>
{
    Interlocked.Increment(ref generated);
    return Guid.NewGuid();
});
IBridgeSessionLifecycle concurrentLifecycle = concurrent;
Parallel.For(0, 32, _ => concurrentLifecycle.FreshConnected(Usb()));
Require(concurrent.Current is not null && generated == 1,
    "20 concurrent duplicate fresh signals raced into duplicate creation");

Require(typeof(BridgeSessionSnapshot).GetProperties().All(property =>
        property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers()
            .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true),
    "24 session snapshot does not expose init-only immutable properties");
Require(!first.SessionId.Equals("12345678", StringComparison.Ordinal) &&
    !first.SessionId.Equals("87654321", StringComparison.Ordinal),
    "27/28 transport sender identity was reused as Bridge session identity");

lifecycle.Ended();
Require(sessions.Current is null, "15 application shutdown retained a session");

Console.WriteLine("PASS Bridge session identity, continuity, end, thread-safety, and transport-neutral semantics");

sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
{
    private DateTimeOffset _current = current;
    public override DateTimeOffset GetUtcNow() => _current;
    public void Advance(TimeSpan elapsed) => _current = _current.Add(elapsed);
}
