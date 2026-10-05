namespace PicoSwitchController;

public interface IBridgeAccessoryService
{
    Task<BridgeAccessoryTransitionResult> SelectAccessoryAsync(
        BridgeAccessoryMode accessory,
        CancellationToken cancellationToken = default);
}

public enum BridgeAccessoryTransitionFailure
{
    None,
    InvalidAccessory,
    NotConnected,
    Persistence,
    FirmwareTransition,
    Verification,
    AccessoryCommand,
    NetworkPakActivation,
    NetworkPakRelease,
    Cancelled,
    Unexpected
}

public sealed record BridgeAccessoryTransitionResult(
    bool Success,
    BridgeAccessoryMode RequestedAccessory,
    BridgeAccessoryMode ActiveAccessory,
    FirmwareModeTransitionResult? FirmwareResult,
    bool AccessoryCommandSent,
    bool NetworkPakActivated,
    string Error)
{
    public BridgeAccessoryTransitionFailure Failure { get; init; }
}

internal sealed record BridgeAccessoryPortResult(bool Success, string Error)
{
    internal static BridgeAccessoryPortResult Succeeded { get; } = new(true, string.Empty);
}

internal interface IControllerAccessoryCommandPort
{
    bool IsAvailable { get; }
    Task<BridgeAccessoryPortResult> SendPakModeAsync(
        string command,
        CancellationToken cancellationToken);
}

internal interface INetworkPakAccessoryPort
{
    Task<BridgeAccessoryPortResult> ActivateSelectedCarrierAsync(
        CancellationToken cancellationToken);
    Task<BridgeAccessoryPortResult> ReleaseActiveCarrierAsync(
        CancellationToken cancellationToken);
}

internal static class BridgeAccessoryProtocol
{
    internal static string PakModeCommand(BridgeAccessoryMode accessory) => accessory switch
    {
        BridgeAccessoryMode.None => "PAKMODE NONE",
        BridgeAccessoryMode.ControllerPak => "PAKMODE MEM",
        BridgeAccessoryMode.RumblePak => "PAKMODE RUMBLE",
        BridgeAccessoryMode.TransferPak => "PAKMODE TRANSFER",
        _ => throw new ArgumentOutOfRangeException(nameof(accessory), accessory,
            "Network Pak does not use the Controller PAKMODE command path.")
    };
}

internal sealed class DelegateControllerAccessoryCommandPort(
    Func<bool> isAvailable,
    Func<string, CancellationToken, Task<BridgeAccessoryPortResult>> send) :
    IControllerAccessoryCommandPort
{
    public bool IsAvailable => isAvailable();

    public Task<BridgeAccessoryPortResult> SendPakModeAsync(
        string command,
        CancellationToken cancellationToken) => send(command, cancellationToken);
}

internal sealed class DelegateNetworkPakAccessoryPort(
    Func<CancellationToken, Task<BridgeAccessoryPortResult>> activate,
    Func<CancellationToken, Task<BridgeAccessoryPortResult>> release) :
    INetworkPakAccessoryPort
{
    public Task<BridgeAccessoryPortResult> ActivateSelectedCarrierAsync(
        CancellationToken cancellationToken) => activate(cancellationToken);

    public Task<BridgeAccessoryPortResult> ReleaseActiveCarrierAsync(
        CancellationToken cancellationToken) => release(cancellationToken);
}

public sealed class BridgeAccessoryService : IBridgeAccessoryService
{
    private readonly IBridgeDeviceStateProvider _state;
    private readonly IBridgeDeviceStateUpdater _updater;
    private readonly IAccessoryPersistenceService _persistence;
    private readonly IBridgeFirmwareServiceAfterPersistence _firmware;
    private readonly IControllerAccessoryCommandPort _controller;
    private readonly INetworkPakAccessoryPort _networkPak;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BridgeAccessoryMode _controllerAccessoryBeforeStandalone = BridgeAccessoryMode.None;

    internal BridgeAccessoryService(
        IBridgeDeviceStateProvider state,
        IAccessoryPersistenceService persistence,
        IBridgeFirmwareService firmware,
        IControllerAccessoryCommandPort controller,
        INetworkPakAccessoryPort networkPak)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _updater = state as IBridgeDeviceStateUpdater
            ?? throw new ArgumentException(
                "The host state provider is not mutable by the service layer.", nameof(state));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _firmware = firmware as IBridgeFirmwareServiceAfterPersistence
            ?? throw new ArgumentException(
                "Accessory transitions require the firmware service's persistence-authorized path.",
                nameof(firmware));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _networkPak = networkPak ?? throw new ArgumentNullException(nameof(networkPak));
    }

    public async Task<BridgeAccessoryTransitionResult> SelectAccessoryAsync(
        BridgeAccessoryMode accessory,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(accessory))
            return Reject(accessory, BridgeAccessoryTransitionFailure.InvalidAccessory,
                "The requested accessory is not recognized.");
        if (cancellationToken.IsCancellationRequested)
            return Reject(accessory, BridgeAccessoryTransitionFailure.Cancelled,
                "Accessory transition was cancelled.");
        if (_state.Current.ControlConnectionState != BridgeConnectionState.Connected)
            return Reject(accessory, BridgeAccessoryTransitionFailure.NotConnected,
                "Connect to the NICE64 Pico first.");

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Reject(accessory, BridgeAccessoryTransitionFailure.Cancelled,
                "Accessory transition was cancelled before ownership was acquired.");
        }

        FirmwareModeTransitionResult? firmwareResult = null;
        bool commandSent = false;
        bool networkActivated = false;
        BridgeAccessoryMode previous = _state.Current.ActiveAccessory;
        try
        {
            _updater.SetAccessoryTransition(new(
                BridgeAccessoryTransitionPhase.TransitionPending, accessory, string.Empty));

            FirmwarePersonality required =
                BridgeUiPolicy.RequiredFirmwareForAccessory(accessory);
            if (previous == accessory && _state.Current.FirmwarePersonality == required)
                return Complete(accessory, firmwareResult, false,
                    accessory == BridgeAccessoryMode.NetworkPak);

            if (previous != accessory && _persistence.Current.Active)
            {
                AccessoryPersistenceResult persistence =
                    await _persistence.FlushAsync(
                        AccessoryPersistenceFlushReason.AccessoryChange,
                        cancellationToken).ConfigureAwait(false);
                if (!persistence.Success)
                    return Fail(accessory, previous, firmwareResult, false, false,
                        BridgeAccessoryTransitionFailure.Persistence,
                        $"Persistence flush failed: {persistence.Error}");
            }

            FirmwarePersonality beforeFirmware = _state.Current.FirmwarePersonality;
            firmwareResult = await _firmware.SelectModeAfterPersistenceAsync(
                required, cancellationToken).ConfigureAwait(false);
            if (!firmwareResult.Success)
                return Fail(accessory, previous, firmwareResult, false, false,
                    firmwareResult.Failure == FirmwareModeTransitionFailure.Cancelled
                        ? BridgeAccessoryTransitionFailure.Cancelled
                        : BridgeAccessoryTransitionFailure.FirmwareTransition,
                    firmwareResult.Error);
            if (firmwareResult.VerifiedMode?.Personality != required)
                return Fail(accessory, previous, firmwareResult, false, false,
                    BridgeAccessoryTransitionFailure.Verification,
                    $"Required {required} firmware was not verified.");

            bool previousInvalidated = beforeFirmware != required &&
                previous != BridgeAccessoryMode.None;
            if (previousInvalidated)
                _updater.SetActiveAccessory(BridgeAccessoryMode.None);

            if (cancellationToken.IsCancellationRequested)
                return Fail(accessory,
                    previousInvalidated ? BridgeAccessoryMode.None : previous,
                    firmwareResult, false, false,
                    BridgeAccessoryTransitionFailure.Cancelled,
                    "Accessory transition was cancelled before activation.");

            if (required == FirmwarePersonality.Network)
            {
                BridgeAccessoryPortResult activation =
                    await _networkPak.ActivateSelectedCarrierAsync(cancellationToken)
                        .ConfigureAwait(false);
                if (!activation.Success)
                    return Fail(accessory,
                        previousInvalidated ? BridgeAccessoryMode.None : previous,
                        firmwareResult, false, false,
                        BridgeAccessoryTransitionFailure.NetworkPakActivation,
                        activation.Error);
                networkActivated = true;
                if (previous != BridgeAccessoryMode.NetworkPak)
                    RememberControllerAccessory(previous);
                _updater.SetActiveAccessory(BridgeAccessoryMode.NetworkPak);
            }
            else
            {
                if (!_controller.IsAvailable)
                    return Fail(accessory,
                        previousInvalidated ? BridgeAccessoryMode.None : previous,
                        firmwareResult, false, false,
                        BridgeAccessoryTransitionFailure.AccessoryCommand,
                        "The Controller accessory command path is unavailable.");

                // The accepted wire operation is intentionally non-cancellable once issued.
                BridgeAccessoryPortResult command = await _controller.SendPakModeAsync(
                    BridgeAccessoryProtocol.PakModeCommand(accessory), CancellationToken.None)
                    .ConfigureAwait(false);
                if (!command.Success)
                    return Fail(accessory,
                        previousInvalidated ? BridgeAccessoryMode.None : previous,
                        firmwareResult, false, false,
                        BridgeAccessoryTransitionFailure.AccessoryCommand, command.Error);
                commandSent = true;
                _updater.SetActiveAccessory(accessory);
                RememberControllerAccessory(accessory);

                // Preserve desktop ownership ordering: verified Controller firmware,
                // verified PAKMODE, then release the former Network Pak carrier.
                if (previous == BridgeAccessoryMode.NetworkPak)
                {
                    BridgeAccessoryPortResult release =
                        await _networkPak.ReleaseActiveCarrierAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    if (!release.Success)
                        return Fail(accessory, accessory, firmwareResult, true, false,
                            BridgeAccessoryTransitionFailure.NetworkPakRelease,
                            release.Error);
                }
            }

            if (previous != accessory)
                _persistence.TryDeactivateClean();
            return Complete(accessory, firmwareResult, commandSent, networkActivated);
        }
        catch (OperationCanceledException)
        {
            return Fail(accessory, _state.Current.ActiveAccessory, firmwareResult,
                commandSent, networkActivated, BridgeAccessoryTransitionFailure.Cancelled,
                "Accessory transition was cancelled.");
        }
        catch (Exception ex)
        {
            return Fail(accessory, _state.Current.ActiveAccessory, firmwareResult,
                commandSent, networkActivated, BridgeAccessoryTransitionFailure.Unexpected,
                ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task ApplyVerifiedFirmwareAsync(
        FirmwarePersonality firmware,
        CancellationToken cancellationToken = default)
    {
        if (firmware == FirmwarePersonality.Network)
        {
            await SelectAccessoryAsync(BridgeAccessoryMode.NetworkPak, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (firmware == FirmwarePersonality.Controller)
        {
            BridgeAccessoryMode active = _state.Current.ActiveAccessory;
            if (active == BridgeAccessoryMode.NetworkPak || active == BridgeAccessoryMode.None)
                await SelectAccessoryAsync(_controllerAccessoryBeforeStandalone,
                    cancellationToken).ConfigureAwait(false);
            return;
        }
        if (firmware != FirmwarePersonality.Diagnostics) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BridgeAccessoryMode active = _state.Current.ActiveAccessory;
            RememberControllerAccessory(active);
            if (active == BridgeAccessoryMode.NetworkPak)
                await _networkPak.ReleaseActiveCarrierAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            _updater.SetActiveAccessory(BridgeAccessoryMode.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void AdoptVerifiedAccessory(BridgeAccessoryMode accessory)
    {
        if (!Enum.IsDefined(accessory))
            throw new ArgumentOutOfRangeException(nameof(accessory));
        RememberControllerAccessory(accessory);
        _updater.SetActiveAccessory(accessory);
        _updater.SetAccessoryTransition(new(
            BridgeAccessoryTransitionPhase.Completed, accessory, string.Empty));
    }

    private void RememberControllerAccessory(BridgeAccessoryMode accessory)
    {
        if (BridgeUiPolicy.RequiredFirmwareForAccessory(accessory) ==
            FirmwarePersonality.Controller)
            _controllerAccessoryBeforeStandalone = accessory;
    }

    private BridgeAccessoryTransitionResult Complete(
        BridgeAccessoryMode accessory,
        FirmwareModeTransitionResult? firmwareResult,
        bool commandSent,
        bool networkActivated)
    {
        _updater.SetAccessoryTransition(new(
            BridgeAccessoryTransitionPhase.Completed, accessory, string.Empty));
        return new(true, accessory, _state.Current.ActiveAccessory, firmwareResult,
            commandSent, networkActivated, string.Empty);
    }

    private BridgeAccessoryTransitionResult Fail(
        BridgeAccessoryMode requested,
        BridgeAccessoryMode active,
        FirmwareModeTransitionResult? firmwareResult,
        bool commandSent,
        bool networkActivated,
        BridgeAccessoryTransitionFailure failure,
        string error)
    {
        _updater.SetAccessoryTransition(new(
            BridgeAccessoryTransitionPhase.Failed, requested, error));
        return new(false, requested, active, firmwareResult, commandSent,
            networkActivated, error) { Failure = failure };
    }

    private BridgeAccessoryTransitionResult Reject(
        BridgeAccessoryMode requested,
        BridgeAccessoryTransitionFailure failure,
        string error) =>
        new(false, requested, _state.Current.ActiveAccessory, null, false, false, error)
        { Failure = failure };
}
