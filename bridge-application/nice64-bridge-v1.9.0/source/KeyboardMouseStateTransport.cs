namespace PicoSwitchController;

using System.Globalization;
using System.Security.Cryptography;

public enum Nice64LogicalKey : byte
{
    None = 0,
    Escape,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    NumLock, ScrollLock, CapsLock, Grave,
    D1, D2, D3, D4, D5, D6, D7, D8, D9, D0,
    Minus, Equals, Backspace, Tab, Enter, Space,
    Q, W, E, R, T, Y, U, I, O, P,
    A, S, D, F, G, H, J, K, L,
    Z, X, C, V, B, N, M,
    Apostrophe, LeftBracket, RightBracket, Semicolon, Comma, Period, Slash,
    KeypadMinus, KeypadPlus, KeypadMultiply,
    LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt,
    Option, PrintScreen, PageUp, Up, Left, Down, Right, End, Home,
    Japanese1, Japanese2, Japanese3, Japanese4,
    Japanese5, Japanese6, Japanese7, Japanese8,
    Count
}

[Flags]
public enum Nice64PointerButtons : byte
{
    None = 0,
    Left = 1,
    Right = 2
}

public enum KeyboardMouseStateDisposition
{
    Accepted,
    Stale,
    Inactive,
    Invalid,
    UnexpectedResponse
}

public static class KeyboardMouseStateProtocol
{
    public static string EncodeKeyboard(uint session, uint sequence,
        IEnumerable<Nice64LogicalKey> heldKeys)
    {
        if (session == 0) throw new ArgumentOutOfRangeException(nameof(session));
        ArgumentNullException.ThrowIfNull(heldKeys);
        Nice64LogicalKey[] keys = heldKeys.Distinct().OrderBy(key => key).ToArray();
        if (keys.Length >= (int)Nice64LogicalKey.Count ||
            keys.Any(key => key is <= Nice64LogicalKey.None or >= Nice64LogicalKey.Count))
            throw new ArgumentOutOfRangeException(nameof(heldKeys));
        string payload = string.Concat(keys.Select(key => ((byte)key).ToString("X2")));
        return $"KEYBOARD_STATE_V1 {session:X8} {sequence:X8} {keys.Length:X2}{payload}";
    }

    public static string EncodePointer(uint session, uint sequence,
        Nice64PointerButtons buttons, int cumulativeX, int cumulativeY)
    {
        if (session == 0) throw new ArgumentOutOfRangeException(nameof(session));
        if ((buttons & ~(Nice64PointerButtons.Left | Nice64PointerButtons.Right)) != 0)
            throw new ArgumentOutOfRangeException(nameof(buttons));
        return $"POINTER_STATE_V1 {session:X8} {sequence:X8} {(byte)buttons:X2} " +
            $"{unchecked((uint)cumulativeX):X8} {unchecked((uint)cumulativeY):X8}";
    }

    public static KeyboardMouseStateDisposition ParseResponse(string line,
        string command) => line.Trim() switch
    {
        var value when value == $"{command}|ACK" =>
            KeyboardMouseStateDisposition.Accepted,
        var value when value == $"{command}|STALE" =>
            KeyboardMouseStateDisposition.Stale,
        var value when value == $"{command}|ERR|INACTIVE" =>
            KeyboardMouseStateDisposition.Inactive,
        var value when value == $"{command}|ERR|FORMAT" =>
            KeyboardMouseStateDisposition.Invalid,
        _ => KeyboardMouseStateDisposition.UnexpectedResponse
    };
}

public sealed class BridgeKeyboardMouseStateClient
{
    private readonly IBridgeLineTransport _transport;
    private readonly object _sync = new();
    private uint _sequence;
    private int _cumulativeX;
    private int _cumulativeY;

    public BridgeKeyboardMouseStateClient(IBridgeLineTransport transport,
        uint? sessionId = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        SessionId = sessionId is > 0 ? sessionId.Value : CreateSessionId();
    }

    public uint SessionId { get; }

    public KeyboardMouseStateDisposition SendKeyboard(
        IEnumerable<Nice64LogicalKey> heldKeys, TimeSpan timeout)
    {
        lock (_sync)
        {
            string line = KeyboardMouseStateProtocol.EncodeKeyboard(
                SessionId, _sequence++, heldKeys);
            return Exchange(line, "KEYBOARD_STATE_V1", timeout);
        }
    }

    public KeyboardMouseStateDisposition SendPointerDelta(
        Nice64PointerButtons buttons, int dx, int dy, TimeSpan timeout)
    {
        lock (_sync)
        {
            _cumulativeX = unchecked(_cumulativeX + dx);
            _cumulativeY = unchecked(_cumulativeY + dy);
            string line = KeyboardMouseStateProtocol.EncodePointer(
                SessionId, _sequence++, buttons, _cumulativeX, _cumulativeY);
            return Exchange(line, "POINTER_STATE_V1", timeout);
        }
    }

    public byte ReadKeyboardLeds(TimeSpan timeout)
    {
        lock (_sync)
        {
            _transport.WriteLine("KEYBOARD_LEDS_V1?");
            string line = _transport.ReadLine(timeout, CancellationToken.None).Trim();
            const string prefix = "KEYBOARD_LEDS_V1|";
            return line.StartsWith(prefix, StringComparison.Ordinal) &&
                byte.TryParse(line[prefix.Length..], NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out byte value)
                ? value
                : throw new IOException("Unexpected NICE64 keyboard LED response.");
        }
    }

    private KeyboardMouseStateDisposition Exchange(string line, string command,
        TimeSpan timeout)
    {
        _transport.WriteLine(line);
        return KeyboardMouseStateProtocol.ParseResponse(
            _transport.ReadLine(timeout, CancellationToken.None), command);
    }

    private static uint CreateSessionId()
    {
        Span<byte> bytes = stackalloc byte[4];
        uint value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BitConverter.ToUInt32(bytes);
        } while (value == 0);
        return value;
    }
}
