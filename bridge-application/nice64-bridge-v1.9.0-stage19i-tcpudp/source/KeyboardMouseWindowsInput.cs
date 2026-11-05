namespace PicoSwitchController;

public static class Nice64WindowsKeyMap
{
    private const ushort RiKeyE0 = 0x0002;

    public static Nice64LogicalKey Map(ushort virtualKey, ushort makeCode,
        ushort flags)
    {
        bool extended = (flags & RiKeyE0) != 0;
        if (virtualKey == 0x10) // VK_SHIFT
            return makeCode == 0x36 ? Nice64LogicalKey.RightShift : Nice64LogicalKey.LeftShift;
        if (virtualKey == 0x11) // VK_CONTROL
            return extended ? Nice64LogicalKey.RightControl : Nice64LogicalKey.LeftControl;
        if (virtualKey == 0x12) // VK_MENU / Alt
            return extended ? Nice64LogicalKey.RightAlt : Nice64LogicalKey.LeftAlt;

        return virtualKey switch
        {
            0x1B => Nice64LogicalKey.Escape,
            >= 0x70 and <= 0x7B => (Nice64LogicalKey)((int)Nice64LogicalKey.F1 +
                virtualKey - 0x70),
            0x90 => Nice64LogicalKey.NumLock,
            0x91 => Nice64LogicalKey.ScrollLock,
            0x14 => Nice64LogicalKey.CapsLock,
            0xC0 => Nice64LogicalKey.Grave,
            >= 0x31 and <= 0x39 => (Nice64LogicalKey)((int)Nice64LogicalKey.D1 +
                virtualKey - 0x31),
            0x30 => Nice64LogicalKey.D0,
            0xBD => Nice64LogicalKey.Minus,
            0xBB => Nice64LogicalKey.Equals,
            0x08 => Nice64LogicalKey.Backspace,
            0x09 => Nice64LogicalKey.Tab,
            0x0D => Nice64LogicalKey.Enter,
            0x20 => Nice64LogicalKey.Space,
            0x41 => Nice64LogicalKey.A,
            0x42 => Nice64LogicalKey.B,
            0x43 => Nice64LogicalKey.C,
            0x44 => Nice64LogicalKey.D,
            0x45 => Nice64LogicalKey.E,
            0x46 => Nice64LogicalKey.F,
            0x47 => Nice64LogicalKey.G,
            0x48 => Nice64LogicalKey.H,
            0x49 => Nice64LogicalKey.I,
            0x4A => Nice64LogicalKey.J,
            0x4B => Nice64LogicalKey.K,
            0x4C => Nice64LogicalKey.L,
            0x4D => Nice64LogicalKey.M,
            0x4E => Nice64LogicalKey.N,
            0x4F => Nice64LogicalKey.O,
            0x50 => Nice64LogicalKey.P,
            0x51 => Nice64LogicalKey.Q,
            0x52 => Nice64LogicalKey.R,
            0x53 => Nice64LogicalKey.S,
            0x54 => Nice64LogicalKey.T,
            0x55 => Nice64LogicalKey.U,
            0x56 => Nice64LogicalKey.V,
            0x57 => Nice64LogicalKey.W,
            0x58 => Nice64LogicalKey.X,
            0x59 => Nice64LogicalKey.Y,
            0x5A => Nice64LogicalKey.Z,
            0xDE => Nice64LogicalKey.Apostrophe,
            0xDB => Nice64LogicalKey.LeftBracket,
            0xDD => Nice64LogicalKey.RightBracket,
            0xBA => Nice64LogicalKey.Semicolon,
            0xBC => Nice64LogicalKey.Comma,
            0xBE => Nice64LogicalKey.Period,
            0xBF => Nice64LogicalKey.Slash,
            0x6D => Nice64LogicalKey.KeypadMinus,
            0x6B => Nice64LogicalKey.KeypadPlus,
            0x6A => Nice64LogicalKey.KeypadMultiply,
            0xA0 => Nice64LogicalKey.LeftShift,
            0xA1 => Nice64LogicalKey.RightShift,
            0xA2 => Nice64LogicalKey.LeftControl,
            0xA3 => Nice64LogicalKey.RightControl,
            0xA4 => Nice64LogicalKey.LeftAlt,
            0xA5 => Nice64LogicalKey.RightAlt,
            0x5D => Nice64LogicalKey.Option,
            0x2C => Nice64LogicalKey.PrintScreen,
            0x21 => Nice64LogicalKey.PageUp,
            0x26 => Nice64LogicalKey.Up,
            0x25 => Nice64LogicalKey.Left,
            0x28 => Nice64LogicalKey.Down,
            0x27 => Nice64LogicalKey.Right,
            0x23 => Nice64LogicalKey.End,
            0x24 => Nice64LogicalKey.Home,
            0x15 => Nice64LogicalKey.Japanese1, // Kana
            0x19 => Nice64LogicalKey.Japanese2, // Kanji
            0x1C => Nice64LogicalKey.Japanese3, // Convert
            0x1D => Nice64LogicalKey.Japanese4, // NonConvert
            0xF2 => Nice64LogicalKey.Japanese5,
            0xF3 => Nice64LogicalKey.Japanese6,
            0xF4 => Nice64LogicalKey.Japanese7,
            0xF5 => Nice64LogicalKey.Japanese8,
            _ => Nice64LogicalKey.None
        };
    }
}

public sealed record KeyboardMouseInputSnapshot(
    IReadOnlyList<Nice64LogicalKey> HeldKeys,
    Nice64PointerButtons PointerButtons);

public static class WindowsNice64PointerTransform
{
    // The hardware-validated Windows Raw Input path already uses the natural
    // NICE64 convention. User inversion is layered on that known-good mapping.
    public static (int Dx, int Dy) Apply(int dx, int dy,
        bool invertX, bool invertY) =>
        (invertX ? unchecked(-dx) : dx,
         invertY ? unchecked(-dy) : dy);
}

public sealed record KeyboardCaptureChordResult(
    bool ReleaseCapture, IReadOnlyList<WindowsRawKeyboardEvent> ForwardedEvents);

public sealed class KeyboardCaptureChordFilter
{
    private WindowsRawKeyboardEvent? _pendingAltDown;
    private bool _altHeld;
    private bool _altForwarded;

    public KeyboardCaptureChordResult Process(WindowsRawKeyboardEvent input)
    {
        bool alt = input.VirtualKey is 0x12 or 0xA4 or 0xA5;
        bool left = input.VirtualKey == 0x25;

        if (alt && input.IsDown)
        {
            _altHeld = true;
            if (!_altForwarded) _pendingAltDown ??= input;
            return new(false, []);
        }

        if (left && input.IsDown && _altHeld)
        {
            Reset();
            return new(true, []);
        }

        if (alt && !input.IsDown)
        {
            List<WindowsRawKeyboardEvent> released = [];
            if (_pendingAltDown is not null) released.Add(_pendingAltDown);
            if (_altForwarded || _pendingAltDown is not null) released.Add(input);
            Reset();
            return new(false, released);
        }

        if (_pendingAltDown is not null)
        {
            WindowsRawKeyboardEvent pending = _pendingAltDown;
            _pendingAltDown = null;
            _altForwarded = true;
            return new(false, [pending, input]);
        }

        return new(false, [input]);
    }

    public void Reset()
    {
        _pendingAltDown = null;
        _altHeld = false;
        _altForwarded = false;
    }
}

/// <summary>
/// Source-neutral held-key and pointer-button state. Device IDs are provider
/// metadata only and never cross the Bridge/Pico protocol boundary.
/// </summary>
public sealed class KeyboardMouseInputState
{
    private readonly Dictionary<string, HashSet<Nice64LogicalKey>> _keys =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Nice64PointerButtons> _buttons =
        new(StringComparer.OrdinalIgnoreCase);

    public string? KeyboardDeviceId { get; private set; }
    public string? PointerDeviceId { get; private set; }

    public void SelectKeyboard(string? deviceId)
    {
        KeyboardDeviceId = Normalize(deviceId);
        _keys.Clear();
    }

    public void SelectPointer(string? deviceId)
    {
        PointerDeviceId = Normalize(deviceId);
        _buttons.Clear();
    }

    public bool ApplyKey(string deviceId, Nice64LogicalKey key, bool isDown)
    {
        if (key == Nice64LogicalKey.None || !Matches(KeyboardDeviceId, deviceId))
            return false;
        if (!_keys.TryGetValue(deviceId, out HashSet<Nice64LogicalKey>? held))
        {
            held = [];
            _keys[deviceId] = held;
        }
        bool changed = isDown ? held.Add(key) : held.Remove(key);
        if (held.Count == 0) _keys.Remove(deviceId);
        return changed;
    }

    public bool ApplyPointerButtons(string deviceId, ushort rawButtonFlags)
    {
        if (!Matches(PointerDeviceId, deviceId)) return false;
        _buttons.TryGetValue(deviceId, out Nice64PointerButtons value);
        Nice64PointerButtons before = value;
        if ((rawButtonFlags & 0x0001) != 0) value |= Nice64PointerButtons.Left;
        if ((rawButtonFlags & 0x0002) != 0) value &= ~Nice64PointerButtons.Left;
        if ((rawButtonFlags & 0x0004) != 0) value |= Nice64PointerButtons.Right;
        if ((rawButtonFlags & 0x0008) != 0) value &= ~Nice64PointerButtons.Right;
        if (value == Nice64PointerButtons.None) _buttons.Remove(deviceId);
        else _buttons[deviceId] = value;
        return value != before;
    }

    public bool RemoveDevice(string deviceId)
    {
        bool changed = _keys.Remove(deviceId);
        changed |= _buttons.Remove(deviceId);
        return changed;
    }

    public KeyboardMouseInputSnapshot Snapshot() => new(
        _keys.Values.SelectMany(keys => keys).Distinct().OrderBy(key => key).ToArray(),
        _buttons.Values.Aggregate(Nice64PointerButtons.None,
            (combined, value) => combined | value));

    public void Clear()
    {
        _keys.Clear();
        _buttons.Clear();
    }

    private static bool Matches(string? selected, string actual) =>
        selected is null || string.Equals(selected, actual,
            StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Coalescing delivery worker. It preserves every relative movement count while
/// allowing newer keyboard/button state to replace older unsent state.
/// </summary>
public sealed class KeyboardMouseInputCoordinator : IDisposable
{
    private readonly IBridgeKeyboardMouseStateFacade _sink;
    private readonly Action<string>? _log;
    private readonly object _sync = new();
    private readonly object _deliveryGate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly KeyboardMouseInputState _state = new();
    private readonly Task _worker;
    private FirmwarePersonality _activeMode = FirmwarePersonality.Unknown;
    private bool _captureActive;
    private bool _keyboardDirty;
    private bool _pointerDirty;
    private int _pendingX;
    private int _pendingY;
    private bool _invertX;
    private bool _invertY;
    private long _generation;

    public KeyboardMouseInputCoordinator(IBridgeKeyboardMouseStateFacade sink,
        string? keyboardDeviceId = null, string? pointerDeviceId = null,
        Action<string>? log = null, bool invertX = false, bool invertY = false)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _log = log;
        _state.SelectKeyboard(keyboardDeviceId);
        _state.SelectPointer(pointerDeviceId);
        _invertX = invertX;
        _invertY = invertY;
        _worker = Task.Run(WorkerLoop);
    }

    public string? KeyboardDeviceId { get { lock (_sync) return _state.KeyboardDeviceId; } }
    public string? PointerDeviceId { get { lock (_sync) return _state.PointerDeviceId; } }
    public FirmwarePersonality ActiveMode { get { lock (_sync) return _activeMode; } }
    public bool CaptureActive { get { lock (_sync) return _captureActive; } }

    public void SetActiveMode(FirmwarePersonality mode)
    {
        lock (_sync)
        {
            _generation++;
            _state.Clear();
            _pendingX = 0;
            _pendingY = 0;
            _activeMode = mode;
            _captureActive = false;
            _keyboardDirty = false;
            _pointerDirty = false;
        }
    }

    public bool EnterCapture()
    {
        lock (_sync)
        {
            if (_activeMode is not (FirmwarePersonality.Keyboard or
                FirmwarePersonality.Mouse)) return false;
            _generation++;
            _state.Clear();
            _pendingX = 0;
            _pendingY = 0;
            _captureActive = true;
            _keyboardDirty = _activeMode == FirmwarePersonality.Keyboard;
            _pointerDirty = _activeMode == FirmwarePersonality.Mouse;
        }
        _signal.Set();
        return true;
    }

    public void SelectKeyboard(string? deviceId)
    {
        lock (_sync)
        {
            _generation++;
            _state.SelectKeyboard(deviceId);
            _captureActive = false;
            _keyboardDirty = false;
        }
        _signal.Set();
    }

    public void SelectPointer(string? deviceId)
    {
        lock (_sync)
        {
            _generation++;
            _state.SelectPointer(deviceId);
            _pendingX = 0;
            _pendingY = 0;
            _captureActive = false;
            _pointerDirty = false;
        }
        _signal.Set();
    }

    public void ApplyKeyboard(WindowsRawKeyboardEvent input)
    {
        Nice64LogicalKey key = Nice64WindowsKeyMap.Map(input.VirtualKey,
            input.MakeCode, input.Flags);
        lock (_sync)
        {
            if (!_captureActive || _activeMode != FirmwarePersonality.Keyboard ||
                !_state.ApplyKey(input.DeviceId, key, input.IsDown)) return;
            _keyboardDirty = true;
        }
        _signal.Set();
    }

    public void ApplyPointer(WindowsRawPointerEvent input)
    {
        lock (_sync)
        {
            if (!_captureActive || _activeMode != FirmwarePersonality.Mouse ||
                (_state.PointerDeviceId is not null &&
                 !string.Equals(_state.PointerDeviceId, input.DeviceId,
                     StringComparison.OrdinalIgnoreCase))) return;
            bool changed = _state.ApplyPointerButtons(input.DeviceId,
                input.ButtonFlags);
            (int dx, int dy) = WindowsNice64PointerTransform.Apply(
                input.Dx, input.Dy, _invertX, _invertY);
            if (dx != 0 || dy != 0)
            {
                _pendingX = unchecked(_pendingX + dx);
                _pendingY = unchecked(_pendingY + dy);
                changed = true;
            }
            if (!changed) return;
            _pointerDirty = true;
        }
        _signal.Set();
    }

    public void SetPointerInversion(bool invertX, bool invertY)
    {
        lock (_sync)
        {
            _invertX = invertX;
            _invertY = invertY;
        }
    }

    public void RemoveDevice(string deviceId)
    {
        lock (_sync)
        {
            if (!_state.RemoveDevice(deviceId)) return;
            _generation++;
            _pendingX = 0;
            _pendingY = 0;
            _keyboardDirty |= _captureActive &&
                _activeMode == FirmwarePersonality.Keyboard;
            _pointerDirty |= _captureActive &&
                _activeMode == FirmwarePersonality.Mouse;
        }
        _signal.Set();
    }

    public Task ReleaseCaptureAsync() => ReleaseCoreAsync(clearMode: false);

    public async Task ClearAndSendNeutralAsync()
    {
        await ReleaseCoreAsync(clearMode: true);
    }

    private async Task ReleaseCoreAsync(bool clearMode)
    {
        FirmwarePersonality prior;
        bool shouldSend;
        lock (_sync)
        {
            prior = _activeMode;
            shouldSend = _captureActive;
            _generation++;
            if (clearMode) _activeMode = FirmwarePersonality.Unknown;
            _captureActive = false;
            _state.Clear();
            _pendingX = 0;
            _pendingY = 0;
            _keyboardDirty = false;
            _pointerDirty = false;
        }
        try
        {
            await Task.Run(() =>
            {
                lock (_deliveryGate)
                {
                    if (shouldSend && prior == FirmwarePersonality.Keyboard)
                        _sink.SendKeyboard([]);
                    else if (shouldSend && prior == FirmwarePersonality.Mouse)
                        _sink.SendPointer(false, false, 0, 0);
                }
            });
        }
        catch (Exception ex)
        {
            _log?.Invoke($"HID neutral delivery unavailable: {ex.Message}");
        }
    }

    public void ClearWithoutDelivery()
    {
        lock (_sync)
        {
            _generation++;
            _activeMode = FirmwarePersonality.Unknown;
            _captureActive = false;
            _state.Clear();
            _pendingX = 0;
            _pendingY = 0;
            _keyboardDirty = false;
            _pointerDirty = false;
        }
    }

    public KeyboardMouseInputSnapshot Snapshot()
    {
        lock (_sync) return _state.Snapshot();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _signal.Set();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _signal.Dispose();
        _cancellation.Dispose();
    }

    private void WorkerLoop()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            _signal.WaitOne(100);
            if (_cancellation.IsCancellationRequested) break;
            while (TryTake(out Delivery delivery))
            {
                try
                {
                    lock (_deliveryGate)
                    {
                        lock (_sync)
                        {
                            if (delivery.Generation != _generation) continue;
                        }
                        if (delivery.Mode == FirmwarePersonality.Keyboard)
                            _sink.SendKeyboard(delivery.Keys.Select(key => (int)key).ToArray());
                        else if (delivery.Mode == FirmwarePersonality.Mouse)
                            _sink.SendPointer(
                                (delivery.Buttons & Nice64PointerButtons.Left) != 0,
                                (delivery.Buttons & Nice64PointerButtons.Right) != 0,
                                delivery.Dx, delivery.Dy);
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Windows HID delivery unavailable: {ex.Message}");
                }
            }
        }
    }

    private bool TryTake(out Delivery delivery)
    {
        lock (_sync)
        {
            KeyboardMouseInputSnapshot snapshot = _state.Snapshot();
            if (_captureActive && _activeMode == FirmwarePersonality.Keyboard &&
                _keyboardDirty)
            {
                _keyboardDirty = false;
                delivery = new(_generation, _activeMode, snapshot.HeldKeys,
                    snapshot.PointerButtons, 0, 0);
                return true;
            }
            if (_captureActive && _activeMode == FirmwarePersonality.Mouse &&
                _pointerDirty)
            {
                _pointerDirty = false;
                int dx = _pendingX;
                int dy = _pendingY;
                _pendingX = 0;
                _pendingY = 0;
                delivery = new(_generation, _activeMode, snapshot.HeldKeys,
                    snapshot.PointerButtons, dx, dy);
                return true;
            }
            delivery = default;
            return false;
        }
    }

    private readonly record struct Delivery(long Generation, FirmwarePersonality Mode,
        IReadOnlyList<Nice64LogicalKey> Keys, Nice64PointerButtons Buttons,
        int Dx, int Dy);
}
