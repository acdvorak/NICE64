namespace PicoSwitchController;

using System.Net;
using System.Text;

public sealed record WifiStatus(
    string State,
    string Mode,
    string Ssid,
    string Link,
    string IpAddress,
    string Netmask,
    string Gateway,
    string Dns,
    string Failure)
{
    public bool IsTerminal =>
        State.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase) ||
        State.Equals("FAILED", StringComparison.OrdinalIgnoreCase);
}

public static class WifiProvisioningProtocol
{
    public const string StatusCommand = "WIFI_STATUS?";
    public const string DhcpCommand = "WIFI_CONFIG|MODE|DHCP";
    public const string ConnectCommand = "WIFI_CONNECT";
    public const string DisconnectCommand = "WIFI_DISCONNECT";
    public const string ClearCommand = "WIFI_CLEAR";
    public const string InvalidEncodedSsid = "(invalid encoded SSID)";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string BuildSsidCommand(string ssid) =>
        "WIFI_CONFIG|SSID_B64|" + EncodeUtf8(ssid);

    public static string BuildPasswordCommand(string password) =>
        "WIFI_CONFIG|PASSWORD_B64|" + EncodeUtf8(password);

    public static string EncodeUtf8(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

    public static void ValidateCredentials(string ssid, string password)
    {
        int ssidBytes = Encoding.UTF8.GetByteCount(ssid ?? string.Empty);
        int passwordBytes = Encoding.UTF8.GetByteCount(password ?? string.Empty);
        if (ssidBytes is < 1 or > 32)
        {
            throw new ArgumentException("SSID must contain 1–32 UTF-8 bytes.", nameof(ssid));
        }

        if (passwordBytes != 0 && passwordBytes is < 8 or > 64)
        {
            throw new ArgumentException("Password must be empty for an open network or contain 8–64 UTF-8 bytes.", nameof(password));
        }
    }

    public static bool IsExpectedReply(string command, string line)
    {
        if (command == StatusCommand || command == "WIFI?")
        {
            return line.StartsWith("WIFI|STATE=", StringComparison.Ordinal);
        }

        if (command.StartsWith("WIFI_CONFIG|", StringComparison.Ordinal))
        {
            return line.StartsWith("WIFI_CONFIG|", StringComparison.Ordinal);
        }

        return command switch
        {
            ConnectCommand => line.StartsWith("WIFI|CONNECT|", StringComparison.Ordinal),
            DisconnectCommand => line.StartsWith("WIFI|DISCONNECT|", StringComparison.Ordinal),
            ClearCommand => line.StartsWith("WIFI|CLEAR|", StringComparison.Ordinal),
            _ => false
        };
    }

    public static bool IsSuccessfulReply(string command, string line) => command switch
    {
        StatusCommand or "WIFI?" => TryParseStatus(line, out _),
        DhcpCommand => line == "WIFI_CONFIG|OK|MODE=DHCP",
        ConnectCommand => line == "WIFI|CONNECT|ACCEPTED",
        DisconnectCommand => line is "WIFI|DISCONNECT|ACCEPTED" or "WIFI|DISCONNECT|OK|NO_LINK",
        ClearCommand => line == "WIFI|CLEAR|ACCEPTED",
        _ when command.StartsWith("WIFI_CONFIG|SSID_B64|", StringComparison.Ordinal) =>
            line == "WIFI_CONFIG|OK|SSID",
        _ when command.StartsWith("WIFI_CONFIG|PASSWORD_B64|", StringComparison.Ordinal) =>
            line == "WIFI_CONFIG|OK|PASSWORD",
        _ => false
    };

    public static string SafeCommandName(string command)
    {
        if (command.StartsWith("WIFI_CONFIG|SSID_B64|", StringComparison.Ordinal)) return "WIFI_CONFIG SSID";
        if (command.StartsWith("WIFI_CONFIG|PASSWORD_B64|", StringComparison.Ordinal)) return "WIFI_CONFIG PASSWORD";
        return command;
    }

    public static string SafeReplyName(string command) => command switch
    {
        StatusCommand or "WIFI?" => "WIFI_STATUS",
        ConnectCommand => "WIFI_CONNECT_ACK",
        DisconnectCommand => "WIFI_DISCONNECT_ACK",
        ClearCommand => "WIFI_CLEAR_ACK",
        _ when command.StartsWith("WIFI_CONFIG|", StringComparison.Ordinal) =>
            "WIFI_CONFIG_ACK",
        _ => "WIFI_REPLY"
    };

    public static string FriendlyError(string reply)
    {
        if (reply.Contains("PASSWORD_LENGTH", StringComparison.Ordinal)) return "The Wi-Fi password length is invalid.";
        if (reply.Contains("SSID_LENGTH", StringComparison.Ordinal)) return "The SSID is invalid.";
        if (reply.Contains("INVALID_BASE64_OR_LENGTH", StringComparison.Ordinal)) return "The encoded Wi-Fi credential is invalid or too long.";
        if (reply.Contains("NO_CONFIG", StringComparison.Ordinal)) return "No complete Wi-Fi configuration is available.";
        if (reply.Contains("AUTH_FAILED", StringComparison.Ordinal)) return "Wi-Fi authentication failed.";
        if (reply.Contains("NETWORK_NOT_FOUND", StringComparison.Ordinal)) return "The Wi-Fi network was not found.";
        if (reply.Contains("ASSOCIATION_TIMEOUT", StringComparison.Ordinal)) return "Wi-Fi association timed out.";
        if (reply.Contains("DHCP_TIMEOUT", StringComparison.Ordinal)) return "DHCP address assignment timed out.";
        if (reply.Contains("LINK_LOST", StringComparison.Ordinal)) return "The Wi-Fi link was lost.";
        if (reply.Contains("CYW43_INIT_FAILED", StringComparison.Ordinal)) return "The NICE64 Wi-Fi hardware could not initialize.";
        if (reply.Contains("BUSY", StringComparison.Ordinal)) return "NICE64 is busy with another Wi-Fi operation.";
        return "NICE64 rejected the Wi-Fi request.";
    }

    public static bool TryParseStatus(string line, out WifiStatus status)
    {
        status = UnknownStatus();
        if (string.IsNullOrWhiteSpace(line) || line.Length > 1024)
        {
            return false;
        }

        string[] parts = line.Trim().Split('|');
        if (parts.Length is < 2 or > 32 || parts[0] != "WIFI")
        {
            return false;
        }

        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in parts.Skip(1))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1 || part.Length > 256)
            {
                continue;
            }

            fields[part[..separator]] = part[(separator + 1)..];
        }

        if (!fields.TryGetValue("STATE", out string? state) ||
            string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        string ssid = string.Empty;
        if (fields.TryGetValue("SSID_B64", out string? encodedSsid) && encodedSsid.Length > 0)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(encodedSsid);
                ssid = StrictUtf8.GetString(bytes);
            }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
            {
                ssid = InvalidEncodedSsid;
            }
        }

        status = new WifiStatus(
            Value(fields, "STATE"),
            Value(fields, "MODE"),
            ssid,
            Value(fields, "LINK"),
            SafeIp(fields, "IP"),
            SafeIp(fields, "NETMASK"),
            SafeIp(fields, "GW"),
            SafeIp(fields, "DNS"),
            Value(fields, "FAIL"));
        return true;
    }

    private static WifiStatus UnknownStatus() =>
        new("UNKNOWN", "UNKNOWN", string.Empty, "UNKNOWN", "Unknown", "Unknown", "Unknown", "Unknown", "UNKNOWN");

    private static string Value(IReadOnlyDictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out string? value) && value.Length > 0 ? value : "UNKNOWN";

    private static string SafeIp(IReadOnlyDictionary<string, string> fields, string name)
    {
        if (!fields.TryGetValue(name, out string? value) || value.Length == 0)
        {
            return "Unknown";
        }

        return IPAddress.TryParse(value, out IPAddress? address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? value
            : $"Invalid ({value})";
    }
}

public interface IWifiCommandTransport
{
    bool IsConnected { get; }
    Task<string> SendAsync(string command, CancellationToken cancellationToken);
}

public sealed class WifiProvisioningDeviceRejectedException(string message) :
    InvalidOperationException(message);

public sealed class WifiProvisioningMalformedReplyException(string message) :
    InvalidOperationException(message);

public sealed class WifiProvisioningSession(IWifiCommandTransport transport)
{
    public bool IsConnected => transport.IsConnected;

    public async Task ConfigureAndConnectAsync(string ssid, string password, CancellationToken cancellationToken)
    {
        WifiProvisioningProtocol.ValidateCredentials(ssid, password);
        await ExpectAsync(WifiProvisioningProtocol.BuildSsidCommand(ssid), cancellationToken).ConfigureAwait(false);
        await ExpectAsync(WifiProvisioningProtocol.BuildPasswordCommand(password), cancellationToken).ConfigureAwait(false);
        await ExpectAsync(WifiProvisioningProtocol.DhcpCommand, cancellationToken).ConfigureAwait(false);
        await ExpectAsync(WifiProvisioningProtocol.ConnectCommand, cancellationToken).ConfigureAwait(false);
    }

    public Task ConnectRetainedAsync(CancellationToken cancellationToken) =>
        ExpectAsync(WifiProvisioningProtocol.ConnectCommand, cancellationToken);

    public async Task<WifiStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        string line = await ExpectAsync(WifiProvisioningProtocol.StatusCommand, cancellationToken).ConfigureAwait(false);
        if (!WifiProvisioningProtocol.TryParseStatus(line, out WifiStatus status))
        {
            throw new WifiProvisioningMalformedReplyException(
                "NICE64 returned a malformed Wi-Fi status.");
        }
        return status;
    }

    public async Task<WifiStatus> DisconnectAsync(CancellationToken cancellationToken)
    {
        await DisconnectCommandAsync(cancellationToken).ConfigureAwait(false);
        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task DisconnectCommandAsync(CancellationToken cancellationToken) =>
        ExpectAsync(WifiProvisioningProtocol.DisconnectCommand, cancellationToken);

    public async Task<WifiStatus> ClearAsync(CancellationToken cancellationToken)
    {
        await ClearCommandAsync(cancellationToken).ConfigureAwait(false);
        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ClearCommandAsync(CancellationToken cancellationToken) =>
        ExpectAsync(WifiProvisioningProtocol.ClearCommand, cancellationToken);

    public static bool ShouldContinuePolling(WifiStatus status) => !status.IsTerminal;

    private async Task<string> ExpectAsync(string command, CancellationToken cancellationToken)
    {
        if (!transport.IsConnected)
        {
            throw new IOException("NICE64 is not connected over USB.");
        }

        string reply = await transport.SendAsync(command, cancellationToken).ConfigureAwait(false);
        if (!WifiProvisioningProtocol.IsSuccessfulReply(command, reply))
        {
            if (reply.Contains("|ERR|", StringComparison.Ordinal))
            {
                throw new WifiProvisioningDeviceRejectedException(
                    WifiProvisioningProtocol.FriendlyError(reply));
            }

            throw new WifiProvisioningMalformedReplyException(
                "NICE64 returned a malformed Wi-Fi reply.");
        }
        return reply;
    }
}
