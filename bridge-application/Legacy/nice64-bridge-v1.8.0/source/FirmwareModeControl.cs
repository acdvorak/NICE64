namespace PicoSwitchController;

public enum FirmwarePersonality
{
    Unknown,
    Controller,
    Diagnostics,
    Network
}

public enum FirmwareModeSwitchState
{
    Disconnected,
    Connecting,
    Connected,
    QueryingMode,
    ReadyController,
    ReadyDiagnostics,
    ReadyNetwork,
    RequestingMode,
    WaitingForDisconnect,
    WaitingForReconnect,
    VerifyingMode,
    ModeSwitchFailed
}

public enum FirmwareModeRequestKind
{
    Ack,
    AlreadyActive,
    Unsupported,
    Unknown
}

public sealed record FirmwareModeInfo(FirmwarePersonality Personality, string Role, string Service)
{
    public string DisplayName => Personality switch
    {
        FirmwarePersonality.Controller => "Controller",
        FirmwarePersonality.Diagnostics => "Diagnostics",
        FirmwarePersonality.Network => "Network",
        _ => "Unknown"
    };
}

public sealed record FirmwareModeRequestResult(FirmwareModeRequestKind Kind, FirmwareModeInfo? Mode);

public sealed class FirmwareModeStateMachine
{
    public FirmwareModeSwitchState State { get; private set; } = FirmwareModeSwitchState.Disconnected;
    public FirmwarePersonality CurrentMode { get; private set; } = FirmwarePersonality.Unknown;
    public FirmwarePersonality TargetMode { get; private set; } = FirmwarePersonality.Unknown;

    public void Disconnected()
    {
        State = FirmwareModeSwitchState.Disconnected;
        CurrentMode = FirmwarePersonality.Unknown;
        TargetMode = FirmwarePersonality.Unknown;
    }

    public void Connected()
    {
        State = FirmwareModeSwitchState.Connected;
    }

    public void Querying()
    {
        State = FirmwareModeSwitchState.QueryingMode;
    }

    public void Ready(FirmwarePersonality mode)
    {
        CurrentMode = mode;
        TargetMode = FirmwarePersonality.Unknown;
        State = mode switch
        {
            FirmwarePersonality.Controller => FirmwareModeSwitchState.ReadyController,
            FirmwarePersonality.Diagnostics => FirmwareModeSwitchState.ReadyDiagnostics,
            FirmwarePersonality.Network => FirmwareModeSwitchState.ReadyNetwork,
            _ => FirmwareModeSwitchState.Connected
        };
    }

    public void Requesting(FirmwarePersonality target)
    {
        TargetMode = target;
        State = FirmwareModeSwitchState.RequestingMode;
    }

    public void WaitingForDisconnect()
    {
        State = FirmwareModeSwitchState.WaitingForDisconnect;
    }

    public void WaitingForReconnect()
    {
        State = FirmwareModeSwitchState.WaitingForReconnect;
    }

    public void Verifying()
    {
        State = FirmwareModeSwitchState.VerifyingMode;
    }

    public void Failed()
    {
        State = FirmwareModeSwitchState.ModeSwitchFailed;
    }
}

public static class FirmwareModeProtocol
{
    public const string WhoIdentity = "WHO|NICE64|CTRL=1|FW=TIMING_CORE_V2";

    public static BridgeCapabilitySet ParseCapabilities(string line)
    {
        if (!line.StartsWith("CAPS|", StringComparison.Ordinal))
            return BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.DeviceReported);

        HashSet<string> tokens = line["CAPS|".Length..]
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new BridgeCapabilitySet(BridgeCapabilityProvenance.DeviceReported,
        [
            new(BridgeCapabilityId.FirmwareModeController, tokens.Contains("CONTROLLER"),
                BridgeCapabilityProvenance.DeviceReported),
            new(BridgeCapabilityId.FirmwareModeDiagnostics, tokens.Contains("STANDALONE/DIAGNOSTICS"),
                BridgeCapabilityProvenance.DeviceReported),
            new(BridgeCapabilityId.FirmwareModeNetwork, tokens.Contains("STANDALONE/NETWORK"),
                BridgeCapabilityProvenance.DeviceReported)
        ]);
    }

    public static string RequestCommand(FirmwarePersonality target) => target switch
    {
        FirmwarePersonality.Controller => "REQUEST_MODE CONTROLLER",
        FirmwarePersonality.Diagnostics => "REQUEST_MODE DIAGNOSTICS",
        FirmwarePersonality.Network => "REQUEST_MODE NETWORK",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported firmware mode target.")
    };

    public static bool IsExpectedMode(FirmwarePersonality target, FirmwareModeInfo? mode) =>
        mode is not null && mode.Personality == target;

    public static bool TryParseMode(string line, out FirmwareModeInfo mode)
    {
        mode = new FirmwareModeInfo(FirmwarePersonality.Unknown, string.Empty, string.Empty);
        string[] parts = line.Trim().Split('|');
        if (parts.Length != 3 || parts[0] != "MODE")
        {
            return false;
        }

        if (parts[1] == "CONTROLLER" && parts[2] == "NONE")
        {
            mode = new FirmwareModeInfo(FirmwarePersonality.Controller, parts[1], parts[2]);
            return true;
        }

        if (parts[1] == "STANDALONE" && parts[2] == "DIAGNOSTICS")
        {
            mode = new FirmwareModeInfo(FirmwarePersonality.Diagnostics, parts[1], parts[2]);
            return true;
        }

        if (parts[1] == "STANDALONE" && parts[2] == "NETWORK")
        {
            mode = new FirmwareModeInfo(FirmwarePersonality.Network, parts[1], parts[2]);
            return true;
        }

        return false;
    }

    public static FirmwareModeRequestResult ParseRequestResponse(string line)
    {
        string[] parts = line.Trim().Split('|');
        if (parts.Length == 2 && parts[0] == "REQUEST_MODE" && parts[1] == "ERR")
        {
            return new FirmwareModeRequestResult(FirmwareModeRequestKind.Unsupported, null);
        }

        if (parts.Length == 3 && parts[0] == "REQUEST_MODE" && parts[1] == "ERR" && parts[2] == "UNSUPPORTED")
        {
            return new FirmwareModeRequestResult(FirmwareModeRequestKind.Unsupported, null);
        }

        if (parts.Length != 4 || parts[0] != "REQUEST_MODE")
        {
            return new FirmwareModeRequestResult(FirmwareModeRequestKind.Unknown, null);
        }

        FirmwareModeRequestKind kind = parts[1] switch
        {
            "ACK" => FirmwareModeRequestKind.Ack,
            "ALREADY_ACTIVE" => FirmwareModeRequestKind.AlreadyActive,
            _ => FirmwareModeRequestKind.Unknown
        };

        if (kind == FirmwareModeRequestKind.Unknown)
        {
            return new FirmwareModeRequestResult(kind, null);
        }

        string modeLine = $"MODE|{parts[2]}|{parts[3]}";
        return TryParseMode(modeLine, out FirmwareModeInfo mode)
            ? new FirmwareModeRequestResult(kind, mode)
            : new FirmwareModeRequestResult(FirmwareModeRequestKind.Unknown, null);
    }
}
