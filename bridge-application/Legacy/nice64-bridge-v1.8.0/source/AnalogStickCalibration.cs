namespace PicoSwitchController;

using System.Text.Json;

public sealed class AnalogStickCalibration
{
    public const int DirectInputCenter = 32767;
    public const int DirectInputMin = 0;
    public const int DirectInputMax = 65535;
    public const int XInputCenter = 0;
    public const int XInputMin = -32768;
    public const int XInputMax = 32767;
    public const int DefaultDeadzone = 2200;
    public const int N64MaxMagnitude = 80;

    public int CenterX { get; set; } = DirectInputCenter;
    public int CenterY { get; set; } = DirectInputCenter;
    public int MinX { get; set; } = DirectInputMin;
    public int MaxX { get; set; } = DirectInputMax;
    public int MinY { get; set; } = DirectInputMin;
    public int MaxY { get; set; } = DirectInputMax;
    public int Deadzone { get; set; } = DefaultDeadzone;
    public int CenterNoiseX { get; set; }
    public int CenterNoiseY { get; set; }
    public int UniqueXObserved { get; set; }
    public int UniqueYObserved { get; set; }
    public string SourceKind { get; set; } = "DirectInput";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";

    public static AnalogStickCalibration CreateDefault(string sourceKind, string deviceId, string deviceName)
    {
        bool xinput = sourceKind.Equals("XInput", StringComparison.OrdinalIgnoreCase);
        return new AnalogStickCalibration
        {
            SourceKind = sourceKind,
            DeviceId = deviceId,
            DeviceName = deviceName,
            CenterX = xinput ? XInputCenter : DirectInputCenter,
            CenterY = xinput ? XInputCenter : DirectInputCenter,
            MinX = xinput ? XInputMin : DirectInputMin,
            MaxX = xinput ? XInputMax : DirectInputMax,
            MinY = xinput ? XInputMin : DirectInputMin,
            MaxY = xinput ? XInputMax : DirectInputMax,
            Deadzone = xinput ? 2400 : DefaultDeadzone
        };
    }

    public static AnalogStickCalibration? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AnalogStickCalibration>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonSerializerOptions options = new() { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }

    public AnalogStickOutput Map(int rawX, int rawY, int positiveXRawDirection, int positiveYRawDirection)
    {
        AnalogAxisOutput x = MapAxis(rawX, CenterX, MinX, MaxX, Deadzone, positiveXRawDirection);
        AnalogAxisOutput y = MapAxis(rawY, CenterY, MinY, MaxY, Deadzone, positiveYRawDirection);
        return new AnalogStickOutput(x, y);
    }

    public static AnalogAxisOutput MapAxis(
        int raw,
        int center,
        int min,
        int max,
        int deadzone,
        int positiveRawDirection,
        int outputMagnitude = N64MaxMagnitude)
    {
        int direction = positiveRawDirection < 0 ? -1 : 1;
        int signedRaw = (raw - center) * direction;
        int positiveRange = Math.Max(1, direction > 0 ? max - center : center - min);
        int negativeRange = Math.Max(1, direction > 0 ? center - min : max - center);
        int usableRange = signedRaw >= 0 ? positiveRange : negativeRange;
        int abs = Math.Abs(signedRaw);
        int clampedDeadzone = Math.Clamp(deadzone, 0, Math.Max(0, usableRange - 1));

        if (abs <= clampedDeadzone)
        {
            return new AnalogAxisOutput(raw, raw - center, 0, 0);
        }

        double normalizedMagnitude = (double)(abs - clampedDeadzone) / Math.Max(1, usableRange - clampedDeadzone);
        double normalized = Math.Clamp(normalizedMagnitude, 0, 1) * Math.Sign(signedRaw);
        int n64 = Math.Clamp((int)Math.Round(normalized * outputMagnitude), -outputMagnitude, outputMagnitude);
        return new AnalogAxisOutput(raw, raw - center, normalized, (sbyte)n64);
    }
}

public readonly record struct AnalogAxisOutput(int Raw, int Centered, double Normalized, sbyte N64);

public readonly record struct AnalogStickOutput(AnalogAxisOutput X, AnalogAxisOutput Y)
{
    public string ToN64Command(uint mask) => ControllerProfile.BuildCommand(ConsoleControllerType.N64, mask, X.N64, Y.N64);
}

public sealed record AnalogStickSnapshot(
    string SourceKind,
    string DeviceId,
    string DeviceName,
    int RawX,
    int RawY,
    int PositiveXRawDirection,
    int PositiveYRawDirection);
