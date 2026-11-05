using PicoSwitchController;

int passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    passed++;
}

FakeSessions sessions = new();
sessions.SetConnected("session-1");
BridgeDeviceStateService device = new();
((IBridgeDeviceStateUpdater)device).Connect(BridgeControlConnectionKind.SerialUsb,
    "NICE64", "1.2.0", new FirmwareModeInfo(FirmwarePersonality.Controller,
        "controller", "none"), null);
FakeEndpoint endpoints = new();
FakeProtection protection = new();
FakeLocal local = new();
await using BridgeControllerPrimaryService service = new(
    sessions, device, endpoints, protection, local);

Check(service.Current.Primary == BridgeControllerPrimary.Bridge,
    "default Bridge Primary");
BridgeControllerPrimaryResult mobile = await service.SelectAsync(
    "session-1", BridgeControllerPrimary.NiceMobile);
Check(mobile.Success && mobile.Snapshot.Primary == BridgeControllerPrimary.NiceMobile,
    "explicit Mobile selection succeeds");
Check(local.SuspendCalls == 1 && local.ResumeCalls == 0,
    "Mobile selection suspends local once");
Check(endpoints.ResolveCalls == 1 && mobile.Snapshot.Endpoint.Address == "192.168.2.81",
    "endpoint resolved exactly at handoff");

await Task.Delay(25);
Check(service.Current.Primary == BridgeControllerPrimary.NiceMobile,
    "no timer or expiry failback");

sessions.SetUnavailable();
Check(service.Current.Primary == BridgeControllerPrimary.NiceMobile &&
    !service.Current.Endpoint.Available && local.ResumeCalls == 0,
    "session loss preserves selected Mobile primary");
sessions.SetConnected("session-1");

BridgeControllerPrimaryResult bridge = await service.SelectAsync(
    "session-1", BridgeControllerPrimary.Bridge);
Check(bridge.Success && bridge.Snapshot.Primary == BridgeControllerPrimary.Bridge,
    "explicit Bridge selection succeeds");
Check(local.ResumeCalls == 1 && local.LastResendCurrent,
    "Bridge selection resumes with resendCurrent true");

protection.CurrentValue = BridgeControllerEndpointProtection.All;
BridgeControllerPrimaryResult blocked = await service.SelectAsync(
    "session-1", BridgeControllerPrimary.NiceMobile);
Check(!blocked.Success && blocked.Failure == BridgeControllerPrimaryFailure.ProtectedOperation,
    "protected operation blocks handoff");
Check(service.Current.Primary == BridgeControllerPrimary.Bridge,
    "failed handoff preserves authority");

BridgeControllerPrimaryResult stale = await service.SelectAsync(
    "stale", BridgeControllerPrimary.NiceMobile);
Check(!stale.Success && stale.Failure == BridgeControllerPrimaryFailure.StaleSession,
    "stale session rejected");

Console.WriteLine($"BridgeControllerPrimaryServiceHeadlessSmoke passed ({passed}/11)");

sealed class FakeSessions : IBridgeSessionProvider
{
    public BridgeSessionSnapshot? Current { get; private set; }
    public event EventHandler<BridgeSessionChangedEventArgs>? CurrentChanged;
    public void SetConnected(string id)
    {
        BridgeSessionSnapshot? previous = Current;
        Current = new(id, BridgeSessionState.Connected, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, BridgeControlConnectionKind.SerialUsb, "COM5",
            "NICE64", FirmwarePersonality.Controller, BridgeAccessoryMode.TransferPak);
        CurrentChanged?.Invoke(this, new(previous, Current));
    }
    public void SetUnavailable()
    {
        BridgeSessionSnapshot? previous = Current;
        Current = Current! with { State = BridgeSessionState.TemporarilyUnavailable };
        CurrentChanged?.Invoke(this, new(previous, Current));
    }
}

sealed class FakeEndpoint : IBridgeControllerEndpointProvider
{
    public int ResolveCalls { get; private set; }
    public Task<BridgeControllerEndpointSnapshot> ResolveCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        ResolveCalls++;
        return Task.FromResult(BridgeControllerEndpointSnapshot.AvailableAt(
            "session-1", "192.168.2.81"));
    }
    public Task<BridgeControllerEndpointSnapshot> ResolveMobileHumanInputAsync(
        CancellationToken cancellationToken = default) =>
        ResolveCurrentAsync(cancellationToken);
}

sealed class FakeProtection : IBridgeControllerEndpointProtectionProvider
{
    public BridgeControllerEndpointProtection CurrentValue { get; set; }
    public BridgeControllerEndpointProtection Current => CurrentValue;
}

sealed class FakeLocal : IBridgeLocalControllerPrimaryPort
{
    public int SuspendCalls { get; private set; }
    public int ResumeCalls { get; private set; }
    public bool LastResendCurrent { get; private set; }
    public ValueTask SuspendLocalAsync(CancellationToken cancellationToken)
    { SuspendCalls++; return ValueTask.CompletedTask; }
    public ValueTask ResumeLocalAsync(bool resendCurrent, CancellationToken cancellationToken)
    { ResumeCalls++; LastResendCurrent = resendCurrent; return ValueTask.CompletedTask; }
}
