namespace PicoSwitchController;

public sealed class BridgeSettingsNetworkPakModuleSelectionStore(string settingsPath) :
    IBridgeNetworkPakModuleSelectionStore
{
    public ValueTask SaveAsync(
        string moduleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BridgeSettings.SaveActiveNetworkPakModuleId(settingsPath, moduleId);
        return ValueTask.CompletedTask;
    }
}
