namespace PicoSwitchController;

using System.Globalization;

public sealed class BridgeSettings
{
    public int BaudRate { get; init; } = 115200;
    public int GamepadPollIntervalMs { get; init; } = 8;
    public int SerialWriterWaitMs { get; init; } = 8;
    public int MinimumPressDurationMs { get; init; }

    public static BridgeSettings Load(string path)
    {
        BridgeSettings settings = new();
        if (!File.Exists(path))
        {
            return settings;
        }

        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith('['))
            {
                continue;
            }

            int equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            string key = line[..equalsIndex].Trim();
            string value = line[(equalsIndex + 1)..].Trim();
            values[key] = value;
        }

        return new BridgeSettings
        {
            BaudRate = ReadClampedInt(values, nameof(BaudRate), settings.BaudRate, 1200, 2_000_000),
            GamepadPollIntervalMs = ReadClampedInt(values, nameof(GamepadPollIntervalMs), settings.GamepadPollIntervalMs, 1, 100),
            SerialWriterWaitMs = ReadClampedInt(values, nameof(SerialWriterWaitMs), settings.SerialWriterWaitMs, 0, 100),
            MinimumPressDurationMs = ReadClampedInt(values, nameof(MinimumPressDurationMs), settings.MinimumPressDurationMs, 0, 250)
        };
    }

    private static int ReadClampedInt(
        Dictionary<string, string> values,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        if (!values.TryGetValue(key, out string? value)
            || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, minimum, maximum);
    }
}
