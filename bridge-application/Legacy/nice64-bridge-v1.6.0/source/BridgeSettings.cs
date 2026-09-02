namespace PicoSwitchController;

using System.Globalization;

public sealed class BridgeSettings
{
    public const string DefaultNetworkPakModuleId = "network-portal";
    public const string DefaultSearchProviderName = "Google";
    public const string DefaultSearchProviderUrl = "https://www.google.com/search?q={query}";

    public int BaudRate { get; init; } = 115200;
    public int GamepadPollIntervalMs { get; init; } = 8;
    public int SerialWriterWaitMs { get; init; } = 8;
    public int MinimumPressDurationMs { get; init; }
    public string ActiveNetworkPakModuleId { get; init; } = DefaultNetworkPakModuleId;
    public string ReadableSourceMode { get; init; } = "AUTO";
    public string SearchProviderName { get; init; } = DefaultSearchProviderName;
    public string SearchProviderUrl { get; init; } = DefaultSearchProviderUrl;
    public string InputSourceMode { get; init; } = "Auto";
    public string ConsoleMode { get; init; } = "Snes";
    public bool DebugMode { get; init; }
    public bool EnableOnScreenControls { get; init; } = true;

    // Hidden RUDP-0 configuration scaffold. No INI key or UI control can change
    // this until a later checkpoint implements and authorizes wireless RUDP.
    public Nice64WirelessDeliveryClass NetworkPakWirelessDeliveryClass { get; private init; } =
        Nice64WirelessTransportContract.DefaultDeliveryClass;

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
            MinimumPressDurationMs = ReadClampedInt(values, nameof(MinimumPressDurationMs), settings.MinimumPressDurationMs, 0, 250),
            ActiveNetworkPakModuleId = ReadString(values, nameof(ActiveNetworkPakModuleId), DefaultNetworkPakModuleId),
            ReadableSourceMode = ReadString(values, nameof(ReadableSourceMode), "AUTO"),
            SearchProviderName = ReadString(values, nameof(SearchProviderName), DefaultSearchProviderName),
            SearchProviderUrl = ReadString(values, nameof(SearchProviderUrl), DefaultSearchProviderUrl),
            InputSourceMode = ReadString(values, nameof(InputSourceMode), settings.InputSourceMode),
            ConsoleMode = ReadString(values, nameof(ConsoleMode), settings.ConsoleMode),
            DebugMode = ReadBool(values, nameof(DebugMode), settings.DebugMode),
            EnableOnScreenControls = ReadBool(values, nameof(EnableOnScreenControls), settings.EnableOnScreenControls)
        };
    }

    public static void SaveActiveNetworkPakModuleId(string path, string moduleId)
    {
        WriteKeyValue(path, nameof(ActiveNetworkPakModuleId), string.IsNullOrWhiteSpace(moduleId)
            ? DefaultNetworkPakModuleId
            : moduleId.Trim());
    }

    public static void SaveInputSourceMode(string path, string mode)
    {
        WriteKeyValue(path, nameof(InputSourceMode), string.IsNullOrWhiteSpace(mode) ? "Auto" : mode.Trim());
    }

    public static void SaveConsoleMode(string path, string mode)
    {
        WriteKeyValue(path, nameof(ConsoleMode), string.IsNullOrWhiteSpace(mode) ? "Snes" : mode.Trim());
    }

    public static void SaveDebugMode(string path, bool enabled)
    {
        WriteKeyValue(path, nameof(DebugMode), enabled ? "true" : "false");
    }

    public static void SaveEnableOnScreenControls(string path, bool enabled)
    {
        WriteKeyValue(path, nameof(EnableOnScreenControls), enabled ? "true" : "false");
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

    private static string ReadString(Dictionary<string, string> values, string key, string fallback)
    {
        return values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;
    }

    private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out string? value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
    }

    private static void WriteKeyValue(string path, string key, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        List<string> lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        bool updated = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith('['))
            {
                continue;
            }

            int equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            string existingKey = line[..equalsIndex].Trim();
            if (!string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines[i] = $"{key}={value}";
            updated = true;
            break;
        }

        if (!updated)
        {
            if (lines.Count > 0 && lines[^1].Length != 0)
            {
                lines.Add(string.Empty);
            }
            lines.Add("[NetworkPak]");
            lines.Add($"{key}={value}");
        }

        File.WriteAllLines(path, lines);
    }
}
