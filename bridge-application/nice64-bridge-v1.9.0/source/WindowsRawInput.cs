namespace PicoSwitchController;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public enum WindowsRawInputDeviceKind
{
    Keyboard,
    Pointer
}

public sealed record WindowsRawInputDevice(
    string Id, string DisplayName, WindowsRawInputDeviceKind Kind);

public sealed record WindowsRawKeyboardEvent(
    string DeviceId, ushort VirtualKey, ushort MakeCode, ushort Flags, bool IsDown);

public sealed record WindowsRawPointerEvent(
    string DeviceId, int Dx, int Dy, ushort ButtonFlags);

public enum WindowsRawInputCaptureMode
{
    Released,
    Keyboard,
    Pointer
}

public static class WindowsRawInputRegistrationPolicy
{
    public const uint InputSink = 0x00000100;
    public const uint NoLegacy = 0x00000030;
    public const uint CaptureMouse = 0x00000200;
    public const uint DeviceNotify = 0x00002000;

    public static uint FlagsFor(WindowsRawInputDeviceKind kind,
        WindowsRawInputCaptureMode captureMode)
    {
        if (captureMode == WindowsRawInputCaptureMode.Keyboard &&
            kind == WindowsRawInputDeviceKind.Keyboard)
            return NoLegacy | DeviceNotify;
        if (captureMode == WindowsRawInputCaptureMode.Pointer &&
            kind == WindowsRawInputDeviceKind.Pointer)
            return NoLegacy | CaptureMouse | DeviceNotify;

        // Released input remains observable for source enumeration and the
        // explicit capture UI, but the coordinator does not forward it.
        return captureMode == WindowsRawInputCaptureMode.Released
            ? InputSink | DeviceNotify
            : DeviceNotify;
    }
}

/// <summary>
/// Device-aware Win32 Raw Input registration and decoding. Raw Input supplies
/// relative mouse data without capturing or repositioning the Windows cursor.
/// </summary>
public sealed class WindowsRawInput : IDisposable
{
    private const int WmInput = 0x00FF;
    private const int WmInputDeviceChange = 0x00FE;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;
    private const uint RidevRemove = 0x00000001;
    private const ushort RiKeyBreak = 0x0001;
    private const ushort UsagePageGenericDesktop = 0x01;
    private const ushort UsageMouse = 0x02;
    private const ushort UsageKeyboard = 0x06;

    private readonly Dictionary<nint, WindowsRawInputDevice> _devices = [];
    private nint _windowHandle;
    private WindowsRawInputCaptureMode _captureMode;

    public event Action<WindowsRawKeyboardEvent>? KeyboardInput;
    public event Action<WindowsRawPointerEvent>? PointerInput;
    public event Action? DevicesChanged;

    public IReadOnlyList<WindowsRawInputDevice> Devices =>
        _devices.Values.OrderBy(device => device.Kind)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public WindowsRawInputCaptureMode CaptureMode => _captureMode;

    public void Register(nint windowHandle)
    {
        if (windowHandle == 0) throw new ArgumentOutOfRangeException(nameof(windowHandle));
        if (_windowHandle != 0 && _windowHandle != windowHandle) Unregister();

        _windowHandle = windowHandle;
        RegisterConfiguredDevices();
        RefreshDevices();
    }

    public void SetCaptureMode(WindowsRawInputCaptureMode captureMode)
    {
        if (_captureMode == captureMode) return;
        _captureMode = captureMode;
        if (_windowHandle != 0) RegisterConfiguredDevices();
    }

    public bool ProcessWindowMessage(int message, nint wParam, nint lParam)
    {
        if (message == WmInputDeviceChange)
        {
            RefreshDevices();
            DevicesChanged?.Invoke();
            return false;
        }
        if (message != WmInput) return false;

        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(lParam, RidInput, 0, ref size, headerSize) != 0 || size == 0)
            return true;

        nint buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            uint copied = size;
            if (GetRawInputData(lParam, RidInput, buffer, ref copied, headerSize) != size)
                return true;

            RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (!_devices.TryGetValue(header.Device, out WindowsRawInputDevice? device))
            {
                WindowsRawInputDeviceKind? kind = header.Type switch
                {
                    RimTypeKeyboard => WindowsRawInputDeviceKind.Keyboard,
                    RimTypeMouse => WindowsRawInputDeviceKind.Pointer,
                    _ => null
                };
                if (kind is null) return true;
                string id = GetDeviceName(header.Device);
                device = new(id, MakeDisplayName(id, kind.Value), kind.Value);
                _devices[header.Device] = device;
                DevicesChanged?.Invoke();
            }

            nint data = buffer + Marshal.SizeOf<RawInputHeader>();
            if (header.Type == RimTypeKeyboard)
            {
                RawKeyboard keyboard = Marshal.PtrToStructure<RawKeyboard>(data);
                KeyboardInput?.Invoke(new(device.Id, keyboard.VirtualKey,
                    keyboard.MakeCode, keyboard.Flags,
                    (keyboard.Flags & RiKeyBreak) == 0));
            }
            else if (header.Type == RimTypeMouse)
            {
                RawMouse mouse = Marshal.PtrToStructure<RawMouse>(data);
                // Absolute packets are deliberately ignored: the NICE64 mouse
                // protocol is relative and most Windows mice/trackpads report
                // ordinary movement through the relative form. Button changes
                // remain valid even when an interface emits absolute motion.
                bool relative = (mouse.Flags & 0x0001) == 0;
                PointerInput?.Invoke(new(device.Id,
                    relative ? mouse.LastX : 0,
                    relative ? mouse.LastY : 0,
                    mouse.ButtonFlags));
            }
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void RefreshDevices()
    {
        uint count = 0;
        uint entrySize = (uint)Marshal.SizeOf<RawInputDeviceList>();
        if (GetRawInputDeviceList(null, ref count, entrySize) != 0 || count == 0)
        {
            _devices.Clear();
            return;
        }

        RawInputDeviceList[] entries = new RawInputDeviceList[count];
        uint returned = GetRawInputDeviceList(entries, ref count, entrySize);
        if (returned == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows Raw Input device enumeration failed.");

        Dictionary<nint, WindowsRawInputDevice> refreshed = [];
        foreach (RawInputDeviceList entry in entries.Take(checked((int)returned)))
        {
            WindowsRawInputDeviceKind? kind = entry.Type switch
            {
                RimTypeKeyboard => WindowsRawInputDeviceKind.Keyboard,
                RimTypeMouse => WindowsRawInputDeviceKind.Pointer,
                _ => null
            };
            if (kind is null) continue;
            string id = GetDeviceName(entry.Device);
            if (id.Length == 0) continue;
            refreshed[entry.Device] = new(id, MakeDisplayName(id, kind.Value), kind.Value);
        }
        _devices.Clear();
        foreach ((nint handle, WindowsRawInputDevice device) in refreshed)
            _devices[handle] = device;
    }

    public void Dispose() => Unregister();

    private void Unregister()
    {
        if (_windowHandle == 0) return;
        RawInputDevice[] registrations =
        [
            new(UsagePageGenericDesktop, UsageKeyboard, RidevRemove, 0),
            new(UsagePageGenericDesktop, UsageMouse, RidevRemove, 0)
        ];
        _ = RegisterRawInputDevices(registrations, (uint)registrations.Length,
            (uint)Marshal.SizeOf<RawInputDevice>());
        _windowHandle = 0;
        _captureMode = WindowsRawInputCaptureMode.Released;
        _devices.Clear();
    }

    private void RegisterConfiguredDevices()
    {
        RawInputDevice[] registrations =
        [
            new(UsagePageGenericDesktop, UsageKeyboard,
                WindowsRawInputRegistrationPolicy.FlagsFor(
                    WindowsRawInputDeviceKind.Keyboard, _captureMode),
                _windowHandle),
            new(UsagePageGenericDesktop, UsageMouse,
                WindowsRawInputRegistrationPolicy.FlagsFor(
                    WindowsRawInputDeviceKind.Pointer, _captureMode),
                _windowHandle)
        ];
        if (!RegisterRawInputDevices(registrations, (uint)registrations.Length,
                (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows Raw Input registration failed.");
    }

    private static string GetDeviceName(nint device)
    {
        uint length = 0;
        _ = GetRawInputDeviceInfo(device, RidiDeviceName, null, ref length);
        if (length == 0) return $"raw:{device:X}";
        StringBuilder value = new(checked((int)length + 1));
        uint copied = GetRawInputDeviceInfo(device, RidiDeviceName, value, ref length);
        return copied == uint.MaxValue ? $"raw:{device:X}" : value.ToString();
    }

    internal static string MakeDisplayName(string id, WindowsRawInputDeviceKind kind)
    {
        string label = kind == WindowsRawInputDeviceKind.Keyboard ? "Keyboard" : "Mouse";
        string upper = id.ToUpperInvariant();
        string bus = upper.Contains("BTH", StringComparison.Ordinal) ? "Bluetooth" :
            upper.Contains("VID_", StringComparison.Ordinal) ? "USB/HID" : "Windows";
        string vid = ExtractToken(upper, "VID_", 4);
        string pid = ExtractToken(upper, "PID_", 4);
        string identity = vid.Length == 4 && pid.Length == 4
            ? $" VID {vid} PID {pid}"
            : string.Empty;
        uint suffix = StableIdentity(id);
        return $"{label} — {bus}{identity} [{suffix:X8}]";
    }

    private static uint StableIdentity(string value)
    {
        uint hash = 2166136261;
        foreach (char character in value.ToUpperInvariant())
        {
            hash ^= character;
            hash *= 16777619;
        }
        return hash;
    }

    private static string ExtractToken(string value, string marker, int length)
    {
        int index = value.IndexOf(marker, StringComparison.Ordinal);
        index += marker.Length;
        return index >= marker.Length && index + length <= value.Length
            ? value.Substring(index, length)
            : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RawInputDevice(ushort usagePage, ushort usage,
        uint flags, nint target)
    {
        public readonly ushort UsagePage = usagePage;
        public readonly ushort Usage = usage;
        public readonly uint Flags = flags;
        public readonly nint Target = target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList
    {
        public nint Device;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RawMouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(4)] public uint Buttons;
        [FieldOffset(4)] public ushort ButtonFlags;
        [FieldOffset(6)] public ushort ButtonData;
        [FieldOffset(8)] public uint RawButtons;
        [FieldOffset(12)] public int LastX;
        [FieldOffset(16)] public int LastY;
        [FieldOffset(20)] public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint rawInput, uint command,
        nint data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(
        [Out] RawInputDeviceList[]? devices, ref uint count, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfo(nint device, uint command,
        StringBuilder? data, ref uint size);
}
