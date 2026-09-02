namespace PicoSwitchController;

public static class NetworkPakTransportSettings
{
    private const string Key = "NetworkPakTransport";

    public static NetworkPakWifiTransport Load(string path)
    {
        if (!File.Exists(path)) return NetworkPakWifiTransport.Tcp;
        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            int separator = line.IndexOf('=');
            if (separator <= 0 ||
                !string.Equals(line[..separator].Trim(), Key,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            return Enum.TryParse(line[(separator + 1)..].Trim(), ignoreCase: true,
                    out NetworkPakWifiTransport transport)
                ? transport : NetworkPakWifiTransport.Tcp;
        }
        return NetworkPakWifiTransport.Tcp;
    }

    public static void Save(string path, NetworkPakWifiTransport transport)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        List<string> lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        for (int index = 0; index < lines.Count; index++)
        {
            string line = lines[index].Trim();
            int separator = line.IndexOf('=');
            if (separator > 0 && string.Equals(line[..separator].Trim(), Key,
                    StringComparison.OrdinalIgnoreCase))
            {
                lines[index] = $"{Key}={transport}";
                File.WriteAllLines(path, lines);
                return;
            }
        }
        if (lines.Count > 0 && lines[^1].Length != 0) lines.Add(string.Empty);
        lines.Add("[NetworkPak]");
        lines.Add($"{Key}={transport}");
        File.WriteAllLines(path, lines);
    }
}
