using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static bool WaitUntil(Func<bool> predicate, int milliseconds = 2000) =>
    SpinWait.SpinUntil(predicate, milliseconds);

const string keyboardA = "keyboard-a";
const string keyboardB = "keyboard-b";
const string mouseA = "mouse-a";

Require(Nice64WindowsKeyMap.Map(0x41, 0x1E, 0) == Nice64LogicalKey.A,
    "A mapping failed");
Require(Nice64WindowsKeyMap.Map(0x51, 0x10, 0) == Nice64LogicalKey.Q,
    "Q mapping failed");
Require(Nice64WindowsKeyMap.Map(0x10, 0x2A, 0) == Nice64LogicalKey.LeftShift,
    "left Shift mapping failed");
Require(Nice64WindowsKeyMap.Map(0x10, 0x36, 0) == Nice64LogicalKey.RightShift,
    "right Shift mapping failed");
Require((WindowsRawInputRegistrationPolicy.FlagsFor(
        WindowsRawInputDeviceKind.Keyboard,
        WindowsRawInputCaptureMode.Keyboard) &
    WindowsRawInputRegistrationPolicy.NoLegacy) != 0,
    "keyboard capture did not suppress its usage class");
Require((WindowsRawInputRegistrationPolicy.FlagsFor(
        WindowsRawInputDeviceKind.Pointer,
        WindowsRawInputCaptureMode.Pointer) &
    WindowsRawInputRegistrationPolicy.CaptureMouse) != 0,
    "pointer capture did not prevent mouse activation");
Require((WindowsRawInputRegistrationPolicy.FlagsFor(
        WindowsRawInputDeviceKind.Keyboard,
        WindowsRawInputCaptureMode.Keyboard) &
    WindowsRawInputRegistrationPolicy.InputSink) == 0,
    "active keyboard capture was not foreground-only");

Require(WindowsNice64PointerTransform.Apply(10, -5, false, false) == (10, -5),
    "known-good Windows natural orientation changed");
Require(WindowsNice64PointerTransform.Apply(10, -5, true, false) == (-10, -5),
    "Windows Invert X changed the wrong axes");
Require(WindowsNice64PointerTransform.Apply(10, -5, false, true) == (10, 5),
    "Windows Invert Y changed the wrong axes");
Require(WindowsNice64PointerTransform.Apply(10, -5, true, true) == (-10, 5),
    "Windows combined inversion failed");

FirmwareModeStateMachine mode = new();
mode.Connected();
mode.Ready(FirmwarePersonality.Keyboard);
Require(BridgeUiPolicy.CanMarkFirmwareModeActive(mode.State,
    mode.CurrentMode, FirmwarePersonality.Keyboard),
    "keyboard mode was not presented as active");
mode.Requesting(FirmwarePersonality.Mouse);
mode.WaitingForReconnect();
mode.Ready(FirmwarePersonality.Mouse);
Require(BridgeUiPolicy.CanMarkFirmwareModeActive(mode.State,
    mode.CurrentMode, FirmwarePersonality.Mouse),
    "keyboard-to-mouse mode transition failed");
mode.Requesting(FirmwarePersonality.Keyboard);
mode.Ready(FirmwarePersonality.Keyboard);
Require(mode.CurrentMode == FirmwarePersonality.Keyboard,
    "mouse-to-keyboard mode transition failed");
mode.Requesting(FirmwarePersonality.Controller);
mode.Ready(FirmwarePersonality.Controller);
Require(BridgeUiPolicy.CanMarkFirmwareModeActive(mode.State,
    mode.CurrentMode, FirmwarePersonality.Controller),
    "return to Controller mode failed");

KeyboardMouseInputState state = new();
Require(state.ApplyKey(keyboardA, Nice64LogicalKey.A, true), "A press lost");
Require(state.Snapshot().HeldKeys.SequenceEqual([Nice64LogicalKey.A]),
    "A held state mismatch");
Require(state.ApplyKey(keyboardA, Nice64LogicalKey.A, false), "A release lost");
Require(state.Snapshot().HeldKeys.Count == 0, "A release did not clear state");

state.ApplyKey(keyboardA, Nice64LogicalKey.LeftShift, true);
state.ApplyKey(keyboardA, Nice64LogicalKey.A, true);
Require(state.Snapshot().HeldKeys.ToHashSet().SetEquals(
    [Nice64LogicalKey.LeftShift, Nice64LogicalKey.A]), "Shift+A mismatch");
state.ApplyKey(keyboardA, Nice64LogicalKey.B, true);
Require(state.Snapshot().HeldKeys.Count == 3, "three-key state mismatch");
state.ApplyKey(keyboardA, Nice64LogicalKey.C, true);
Require(state.Snapshot().HeldKeys.Count == 4,
    "four-key host state was truncated before firmware");
state.RemoveDevice(keyboardA);
Require(state.Snapshot().HeldKeys.Count == 0,
    "keyboard device loss did not release keys");

state.SelectKeyboard(keyboardB);
Require(!state.ApplyKey(keyboardA, Nice64LogicalKey.A, true),
    "specific keyboard selection accepted another keyboard");
Require(state.ApplyKey(keyboardB, Nice64LogicalKey.A, true),
    "specific keyboard selection rejected selected keyboard");

state.SelectPointer(null);
Require(state.ApplyPointerButtons(mouseA, 0x0001), "left press lost");
Require(state.Snapshot().PointerButtons == Nice64PointerButtons.Left,
    "left button state mismatch");
Require(state.ApplyPointerButtons(mouseA, 0x0004), "right press lost");
Require(state.Snapshot().PointerButtons ==
    (Nice64PointerButtons.Left | Nice64PointerButtons.Right),
    "two-button state mismatch");
Require(state.ApplyPointerButtons(mouseA, 0x0002), "left release lost");
Require(state.Snapshot().PointerButtons == Nice64PointerButtons.Right,
    "right button did not persist while movement/buttons changed");
state.RemoveDevice(mouseA);
Require(state.Snapshot().PointerButtons == Nice64PointerButtons.None,
    "pointer device loss did not release buttons");

RecordingFacade sink = new();
using (KeyboardMouseInputCoordinator coordinator = new(sink))
{
    coordinator.SetActiveMode(FirmwarePersonality.Keyboard);
    coordinator.ApplyKeyboard(new(keyboardA, 0x41, 0x1E, 0, true));
    Thread.Sleep(100);
    Require(sink.KeyboardStates.Count == 0,
        "keyboard input forwarded before explicit capture entry");
    Require(coordinator.EnterCapture(), "keyboard capture entry failed");
    coordinator.ApplyKeyboard(new(keyboardA, 0x41, 0x1E, 0, true));
    Require(WaitUntil(() => sink.KeyboardStates.Any(keys =>
        keys.SequenceEqual([(int)Nice64LogicalKey.A]))),
        "keyboard coordinator did not deliver A");
    coordinator.ApplyKeyboard(new(keyboardA, 0x41, 0x1E, 0, false));
    Require(WaitUntil(() => sink.KeyboardStates.Count >= 2 &&
        sink.KeyboardStates.Last().Length == 0),
        "keyboard coordinator did not deliver release");

    KeyboardCaptureChordFilter chord = new();
    KeyboardCaptureChordResult altDown = chord.Process(
        new(keyboardA, 0x12, 0x38, 0, true));
    Require(!altDown.ReleaseCapture && altDown.ForwardedEvents.Count == 0,
        "Alt was forwarded before release-chord resolution");
    KeyboardCaptureChordResult leftDown = chord.Process(
        new(keyboardA, 0x25, 0x4B, 0x0002, true));
    Require(leftDown.ReleaseCapture && leftDown.ForwardedEvents.Count == 0,
        "Alt+Left Arrow did not request release or was forwarded");
    await coordinator.ReleaseCaptureAsync();
    Require(WaitUntil(() => sink.KeyboardStates.Last().Length == 0),
        "capture release did not emit neutral keyboard state");
    int releasedKeyboardCount = sink.KeyboardStates.Count;
    coordinator.ApplyKeyboard(new(keyboardA, 0x42, 0x30, 0, true));
    Thread.Sleep(100);
    Require(sink.KeyboardStates.Count == releasedKeyboardCount,
        "keyboard forwarded after capture release");
    Require(coordinator.EnterCapture(), "keyboard capture re-entry failed");
    coordinator.ApplyKeyboard(new(keyboardA, 0x42, 0x30, 0, true));
    Require(WaitUntil(() => sink.KeyboardStates.Last().Contains(
        (int)Nice64LogicalKey.B)), "keyboard did not forward after capture re-entry");
    await coordinator.ReleaseCaptureAsync();

    coordinator.SetActiveMode(FirmwarePersonality.Mouse);
    Require(coordinator.EnterCapture(), "pointer capture entry failed");
    coordinator.ApplyPointer(new(mouseA, 10, -5, 0x0001));
    coordinator.ApplyPointer(new(mouseA, 300, -300, 0));
    coordinator.ApplyPointer(new(mouseA, 100000, -100000, 0));
    Require(WaitUntil(() => sink.PointerStates.Count > 0),
        "pointer coordinator did not deliver movement");
    Require(WaitUntil(() => sink.PointerStates.Sum(item => (long)item.Dx) ==
        10 + 300 + 100000 &&
        sink.PointerStates.Sum(item => (long)item.Dy) ==
        -5 - 300 - 100000),
        "pointer movement was lost while newer state replaced older state");
    Require(sink.PointerStates.Last().Left,
        "left button did not persist across movement");

    coordinator.ApplyPointer(new(mouseA, 0, 0, 0x0004));
    Require(WaitUntil(() => sink.PointerStates.Last().Right),
        "right press was not delivered");
    await coordinator.ReleaseCaptureAsync();
    Require(WaitUntil(() => !sink.PointerStates.Last().Left &&
        !sink.PointerStates.Last().Right),
        "capture release did not release pointer buttons");
    int releasedPointerCount = sink.PointerStates.Count;
    coordinator.ApplyPointer(new(mouseA, 50, 25, 0x0001));
    Thread.Sleep(100);
    Require(sink.PointerStates.Count == releasedPointerCount,
        "pointer movement forwarded after capture release");
    Require(coordinator.EnterCapture(), "pointer capture re-entry failed");
    coordinator.ApplyPointer(new(mouseA, 1, 2, 0x0001));
    Require(WaitUntil(() => sink.PointerStates.Count > releasedPointerCount),
        "pointer did not forward after capture re-entry");
    await coordinator.ReleaseCaptureAsync();
    coordinator.SelectPointer("mouse-b");
    Require(!coordinator.CaptureActive,
        "pointer source change did not leave capture released");
}

RecordingFacade invertedSink = new();
using (KeyboardMouseInputCoordinator coordinator = new(invertedSink))
{
    coordinator.SetActiveMode(FirmwarePersonality.Mouse);
    Require(coordinator.EnterCapture(), "inversion pointer capture entry failed");
    coordinator.SetPointerInversion(true, false);
    int beforePreferenceOnly = invertedSink.PointerStates.Count;
    Thread.Sleep(100);
    Require(invertedSink.PointerStates.Count == beforePreferenceOnly,
        "changing inversion created phantom movement");
    coordinator.ApplyPointer(new(mouseA, 10, -5, 0));
    Require(WaitUntil(() => invertedSink.PointerStates.Any(item =>
        item.Dx == -10 && item.Dy == -5)),
        "coordinator did not apply X inversion before accumulation");
    coordinator.SetPointerInversion(false, true);
    coordinator.ApplyPointer(new(mouseA, 3, -2, 0));
    Require(WaitUntil(() => invertedSink.PointerStates.Any(item =>
        item.Dx == 3 && item.Dy == 2)),
        "coordinator did not apply Y inversion before accumulation");
}

Console.WriteLine("KeyboardMouseWindowsInputSmoke: PASS");

sealed class RecordingFacade : IBridgeKeyboardMouseStateFacade
{
    private readonly object _sync = new();
    private readonly List<int[]> _keyboardStates = [];
    private readonly List<PointerRecord> _pointerStates = [];
    public IReadOnlyList<int[]> KeyboardStates
    {
        get { lock (_sync) return _keyboardStates.Select(value => value.ToArray()).ToArray(); }
    }
    public IReadOnlyList<PointerRecord> PointerStates
    {
        get { lock (_sync) return _pointerStates.ToArray(); }
    }
    public BridgeApiV1KeyboardMouseStateResponse SendKeyboard(int[] heldKeys)
    {
        lock (_sync) _keyboardStates.Add(heldKeys.ToArray());
        return new(true, "accepted", 1);
    }
    public BridgeApiV1KeyboardMouseStateResponse SendPointer(
        bool left, bool right, int dx, int dy)
    {
        lock (_sync) _pointerStates.Add(new(left, right, dx, dy));
        return new(true, "accepted", 1);
    }
    public BridgeApiV1KeyboardLedsResponse ReadKeyboardLeds() =>
        new(0, false, false, false);
}

sealed record PointerRecord(bool Left, bool Right, int Dx, int Dy);
