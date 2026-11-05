using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

FakeTransport keyboard = new("KEYBOARD_STATE_V1|ACK", "KEYBOARD_STATE_V1|ACK",
    "KEYBOARD_LEDS_V1|05");
BridgeKeyboardMouseStateClient keyboardClient = new(keyboard, 0x12345678);
Require(keyboardClient.SendKeyboard(
    [Nice64LogicalKey.LeftShift, Nice64LogicalKey.A], TimeSpan.FromSeconds(1)) ==
    KeyboardMouseStateDisposition.Accepted, "keyboard state was not accepted");
Require(keyboard.Writes[0] == "KEYBOARD_STATE_V1 12345678 00000000 022C46",
    $"keyboard wire mismatch: {keyboard.Writes[0]}");
Require(keyboardClient.SendKeyboard([], TimeSpan.FromSeconds(1)) ==
    KeyboardMouseStateDisposition.Accepted, "keyboard release was not accepted");
Require(keyboard.Writes[1] == "KEYBOARD_STATE_V1 12345678 00000001 00",
    "keyboard release wire mismatch");
Require(keyboardClient.ReadKeyboardLeds(TimeSpan.FromSeconds(1)) == 0x05,
    "keyboard LED response mismatch");

FakeTransport pointer = new("POINTER_STATE_V1|ACK", "POINTER_STATE_V1|ACK");
BridgeKeyboardMouseStateClient pointerClient = new(pointer, 0x89ABCDEF);
Require(pointerClient.SendPointerDelta(Nice64PointerButtons.Left, 10, -5,
    TimeSpan.FromSeconds(1)) == KeyboardMouseStateDisposition.Accepted,
    "pointer state was not accepted");
Require(pointer.Writes[0] ==
    "POINTER_STATE_V1 89ABCDEF 00000000 01 0000000A FFFFFFFB",
    $"pointer wire mismatch: {pointer.Writes[0]}");
Require(pointerClient.SendPointerDelta(Nice64PointerButtons.Left, 300, -300,
    TimeSpan.FromSeconds(1)) == KeyboardMouseStateDisposition.Accepted,
    "large pointer state was not accepted");
Require(pointer.Writes[1].EndsWith("00000136 FFFFFECF", StringComparison.Ordinal),
    $"pointer accumulation mismatch: {pointer.Writes[1]}");

Require(FirmwareModeProtocol.RequestCommand(FirmwarePersonality.Keyboard) ==
    "REQUEST_MODE KEYBOARD", "keyboard mode command mismatch");
Require(FirmwareModeProtocol.RequestCommand(FirmwarePersonality.Mouse) ==
    "REQUEST_MODE MOUSE", "mouse mode command mismatch");
Require(FirmwareModeProtocol.TryParseMode(
    "MODE|STANDALONE|KEYBOARD_MOUSE|KEYBOARD", out FirmwareModeInfo keyboardMode) &&
    keyboardMode.Personality == FirmwarePersonality.Keyboard,
    "keyboard mode parse mismatch");
Require(FirmwareModeProtocol.ParseRequestResponse(
    "REQUEST_MODE|ACK|STANDALONE|KEYBOARD_MOUSE|MOUSE").Mode?.Personality ==
    FirmwarePersonality.Mouse, "mouse request response mismatch");

object transportLock = new();
FakeTransport apiTransport = new("KEYBOARD_STATE_V1|ACK",
    "POINTER_STATE_V1|ACK", "KEYBOARD_LEDS_V1|03");
BridgeKeyboardMouseStateFacade facade = new(transportLock, () => apiTransport);
Require(facade.SendKeyboard([(int)Nice64LogicalKey.A]).Accepted,
    "API keyboard state was not accepted");
Require(facade.SendPointer(true, true, 10, -5).Accepted,
    "API pointer state was not accepted");
BridgeApiV1KeyboardLedsResponse leds = facade.ReadKeyboardLeds();
Require(!leds.Power && leds.CapsLock && leds.NumLock,
    "API keyboard LED projection mismatch");
bool invalidKeyRejected = false;
try { facade.SendKeyboard([255]); }
catch (ArgumentOutOfRangeException) { invalidKeyRejected = true; }
Require(invalidKeyRejected, "API accepted an invalid logical key");

Console.WriteLine("KeyboardMouseStateTransportSmoke: PASS");

sealed class FakeTransport(params string[] responses) : IBridgeLineTransport
{
    private readonly Queue<string> _responses = new(responses);
    public List<string> Writes { get; } = [];
    public bool IsConnected => true;
    public string DisplayName => "fake";
    public void WriteLine(string line) => Writes.Add(line);
    public string ReadLine(TimeSpan timeout, CancellationToken cancellationToken) =>
        _responses.Dequeue();
    public string ReadExisting() => string.Empty;
    public void DiscardInput() { }
    public void Dispose() { }
}
