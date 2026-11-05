namespace PicoSwitchController;

public enum BrowserReadableSourceMode
{
    Auto,
    Local,
    Direct,
    FrogFind
}

public static class BrowserReadableSourceModeParser
{
    public static BrowserReadableSourceMode Parse(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "LOCAL" => BrowserReadableSourceMode.Local,
            "DIRECT" => BrowserReadableSourceMode.Direct,
            "FROGFIND" => BrowserReadableSourceMode.FrogFind,
            _ => BrowserReadableSourceMode.Auto
        };
    }
}
