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
    public string? KeyboardInputDeviceId { get; init; }
    public string? PointerInputDeviceId { get; init; }
    public bool MouseInvertX { get; init; }
    public bool MouseInvertY { get; init; }
    public string ConsoleMode { get; init; } = "Snes";
    public bool DebugMode { get; init; }
    public bool EnableOnScreenControls { get; init; } = true;
    public bool AskToNameNice64SessionWhenConnecting { get; init; } = true;
    public string BridgeInstanceName { get; init; } =
        BridgeInstanceNamingPolicy.DefaultUnconnectedName;
    public BridgeApiAccessMode ApiAccessMode { get; init; } =
        BridgeApiAccessMode.Off;
    public int ApiPort { get; init; } = BridgeApiAccessConfiguration.DefaultPort;
    public string? ApiSelectedInterfaceId { get; init; }
    public string? ApiSelectedIpv4Address { get; init; }
    public string? ApiSelectedInterfaceName { get; init; }

    // Internal delivery-class setting retained for current Reliable UDP Quick
    // and LATEST_STATE transport behavior. No INI key or UI control changes it.
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
            KeyboardInputDeviceId = ReadOptionalString(values,
                nameof(KeyboardInputDeviceId)),
            PointerInputDeviceId = ReadOptionalString(values,
                nameof(PointerInputDeviceId)),
            MouseInvertX = ReadBool(values, nameof(MouseInvertX),
                settings.MouseInvertX),
            MouseInvertY = ReadBool(values, nameof(MouseInvertY),
                settings.MouseInvertY),
            ConsoleMode = ReadString(values, nameof(ConsoleMode), settings.ConsoleMode),
            DebugMode = ReadBool(values, nameof(DebugMode), settings.DebugMode),
            EnableOnScreenControls = ReadBool(values, nameof(EnableOnScreenControls), settings.EnableOnScreenControls),
            AskToNameNice64SessionWhenConnecting = ReadBool(values,
                nameof(AskToNameNice64SessionWhenConnecting),
                settings.AskToNameNice64SessionWhenConnecting),
            BridgeInstanceName = ReadString(values, nameof(BridgeInstanceName),
                settings.BridgeInstanceName),
            ApiAccessMode = ReadApiAccessMode(values),
            ApiPort = ReadClampedInt(values, nameof(ApiPort),
                BridgeApiAccessConfiguration.DefaultPort, 1, 65535),
            ApiSelectedInterfaceId = ReadOptionalString(values,
                nameof(ApiSelectedInterfaceId)),
            ApiSelectedIpv4Address = ReadOptionalString(values,
                nameof(ApiSelectedIpv4Address)),
            ApiSelectedInterfaceName = ReadOptionalString(values,
                nameof(ApiSelectedInterfaceName))
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

    public static void SaveKeyboardInputDeviceId(string path, string? deviceId)
    {
        WriteKeyValue(path, nameof(KeyboardInputDeviceId),
            deviceId?.Trim() ?? string.Empty, "KeyboardMouse");
    }

    public static void SavePointerInputDeviceId(string path, string? deviceId)
    {
        WriteKeyValue(path, nameof(PointerInputDeviceId),
            deviceId?.Trim() ?? string.Empty, "KeyboardMouse");
    }

    public static void SaveMouseInversion(string path, bool invertX, bool invertY)
    {
        WriteKeyValues(path, "KeyboardMouse",
        [
            new(nameof(MouseInvertX), invertX ? "true" : "false"),
            new(nameof(MouseInvertY), invertY ? "true" : "false")
        ]);
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

    public static void SaveAskToNameNice64SessionWhenConnecting(string path, bool enabled)
    {
        WriteKeyValue(path, nameof(AskToNameNice64SessionWhenConnecting),
            enabled ? "true" : "false", "Bridge");
    }

    public static void SaveBridgeInstanceName(string path, string name)
    {
        WriteKeyValue(path, nameof(BridgeInstanceName),
            BridgeInstanceNamingPolicy.NormalizeName(name,
                BridgeInstanceNamingPolicy.DefaultUnconnectedName), "Bridge");
    }

    public static void SaveApiAccessConfiguration(
        string path,
        BridgeApiAccessConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        WriteKeyValues(path, "Api",
        [
            new(nameof(ApiAccessMode), configuration.AccessMode.ToString()),
            new(nameof(ApiPort), configuration.Port.ToString(
                CultureInfo.InvariantCulture)),
            new(nameof(ApiSelectedInterfaceId),
                configuration.SelectedInterfaceId?.Trim() ?? string.Empty),
            new(nameof(ApiSelectedIpv4Address),
                configuration.SelectedIpv4Address?.Trim() ?? string.Empty),
            new(nameof(ApiSelectedInterfaceName),
                configuration.SelectedInterfaceName?.Trim() ?? string.Empty)
        ]);
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

    private static string? ReadOptionalString(
        Dictionary<string, string> values,
        string key) =>
        values.TryGetValue(key, out string? value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static BridgeApiAccessMode ReadApiAccessMode(
        Dictionary<string, string> values) =>
        values.TryGetValue(nameof(ApiAccessMode), out string? value) &&
        Enum.TryParse(value, ignoreCase: true,
            out BridgeApiAccessMode parsed) &&
        Enum.IsDefined(parsed)
            ? parsed
            : BridgeApiAccessMode.Off;

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

    private static void WriteKeyValue(
        string path, string key, string value, string section = "NetworkPak")
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
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
        }

        File.WriteAllLines(path, lines);
    }

    private static void WriteKeyValues(
        string path,
        string section,
        IReadOnlyList<KeyValuePair<string, string>> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        List<string> lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : [];
        Dictionary<string, int> indexes = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < lines.Count; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith(';') ||
                line.StartsWith('#') || line.StartsWith('['))
                continue;
            int equalsIndex = line.IndexOf('=');
            if (equalsIndex > 0)
                indexes[line[..equalsIndex].Trim()] = index;
        }

        List<KeyValuePair<string, string>> additions = [];
        foreach (KeyValuePair<string, string> value in values)
        {
            if (indexes.TryGetValue(value.Key, out int index))
                lines[index] = $"{value.Key}={value.Value}";
            else
                additions.Add(value);
        }
        if (additions.Count > 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0)
                lines.Add(string.Empty);
            lines.Add($"[{section}]");
            lines.AddRange(additions.Select(value =>
                $"{value.Key}={value.Value}"));
        }
        File.WriteAllLines(path, lines);
    }
}
