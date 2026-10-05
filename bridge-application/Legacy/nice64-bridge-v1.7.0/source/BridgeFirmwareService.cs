namespace PicoSwitchController;

public interface IBridgeFirmwareService
{
    Task<FirmwareModeTransitionResult> SelectModeAsync(
        FirmwarePersonality target,
        CancellationToken cancellationToken = default);
}

internal interface IBridgeFirmwareServiceAfterPersistence : IBridgeFirmwareService
{
    Task<FirmwareModeTransitionResult> SelectModeAfterPersistenceAsync(
        FirmwarePersonality target,
        CancellationToken cancellationToken = default);
}

internal interface IFirmwareModeControlPath
{
    bool IsAvailable { get; }
    Task<FirmwareModeInfo> QueryCurrentAsync(CancellationToken cancellationToken);
    Task<FirmwareModeRequestResult> SendRequestAsync(
        FirmwarePersonality target,
        CancellationToken cancellationToken);
    Task<FirmwareModeInfo> ReconnectAndVerifyAsync(
        FirmwarePersonality target,
        CancellationToken cancellationToken);
}

internal sealed class SerialFirmwareModeControlPath(
    Func<bool> isAvailable,
    Func<CancellationToken, Task<FirmwareModeInfo>> queryCurrent,
    Func<FirmwarePersonality, CancellationToken, Task<FirmwareModeRequestResult>> sendRequest,
    Func<FirmwarePersonality, CancellationToken, Task<FirmwareModeInfo>> reconnectOnce,
    Action<string>? log = null) : IFirmwareModeControlPath
{
    private bool _retryOnceIfOldMode;

    public bool IsAvailable => isAvailable();

    public Task<FirmwareModeInfo> QueryCurrentAsync(CancellationToken cancellationToken) =>
        queryCurrent(cancellationToken);

    public async Task<FirmwareModeRequestResult> SendRequestAsync(
        FirmwarePersonality target,
        CancellationToken cancellationToken)
    {
        FirmwareModeRequestResult result =
            await sendRequest(target, cancellationToken).ConfigureAwait(false);
        _retryOnceIfOldMode = result.Kind != FirmwareModeRequestKind.Ack;
        if (result.Kind != FirmwareModeRequestKind.Unknown) return result;

        // The serial device can reboot before the ACK is observed. Preserve the
        // accepted behavior: reconnect, verify, and retry once only if the old
        // mode is still active.
        log?.Invoke("FIRMWARE MODE serial ACK not observed; reconnect verification will determine acceptance");
        return new FirmwareModeRequestResult(FirmwareModeRequestKind.Ack, null);
    }

    public async Task<FirmwareModeInfo> ReconnectAndVerifyAsync(
        FirmwarePersonality target,
        CancellationToken cancellationToken)
    {
        FirmwareModeInfo mode = await reconnectOnce(target, cancellationToken)
            .ConfigureAwait(false);
        if (mode.Personality == target || !_retryOnceIfOldMode) return mode;

        log?.Invoke($"FIRMWARE MODE serial lost-ACK retry target={target} " +
            $"observed={mode.Role}/{mode.Service}");
        FirmwareModeRequestResult retry =
            await sendRequest(target, cancellationToken).ConfigureAwait(false);
        if (retry.Kind == FirmwareModeRequestKind.AlreadyActive)
            return await queryCurrent(cancellationToken).ConfigureAwait(false);
        if (retry.Kind != FirmwareModeRequestKind.Ack)
            throw new InvalidOperationException(
                $"Serial firmware-mode retry did not produce ACK: {retry.Kind}.");

        return await reconnectOnce(target, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class TcpFirmwareModeControlPath : IFirmwareModeControlPath
{
    private readonly Func<bool> _isAvailable;
    private readonly Func<CancellationToken, Task<FirmwareModeInfo>> _queryCurrent;
    private readonly Func<FirmwarePersonality, CancellationToken,
        Task<FirmwareModeRequestResult>> _sendRequest;
    private readonly Func<FirmwarePersonality, CancellationToken,
        Task<FirmwareModeInfo>> _reconnectAndVerify;
    private readonly Action<string>? _log;

    internal TcpFirmwareModeControlPath(
        Func<bool> isAvailable,
        Func<CancellationToken, Task<FirmwareModeInfo>> queryCurrent,
        Func<FirmwarePersonality, CancellationToken, Task<FirmwareModeRequestResult>> sendRequest,
        Func<FirmwarePersonality, CancellationToken, Task<FirmwareModeInfo>> reconnectAndVerify,
        Action<string>? log = null)
    {
        _isAvailable = isAvailable;
        _queryCurrent = queryCurrent;
        _sendRequest = sendRequest;
        _reconnectAndVerify = reconnectAndVerify;
        _log = log;
    }

    internal TcpFirmwareModeControlPath(
        Func<bool> isAvailable,
        Func<CancellationToken, Task<FirmwareModeInfo>> queryCurrent,
        Func<FirmwarePersonality, CancellationToken, Task<FirmwareModeRequestResult>> sendRequest,
        Func<CancellationToken, Task> prepareReconnect,
        Func<int, CancellationToken, Task<FirmwareModeInfo>> connectFreshAttempt,
        Action endTransitionWithoutReplacement,
        Action<string>? log = null)
        : this(isAvailable, queryCurrent, sendRequest, prepareReconnect,
            connectFreshAttempt, endTransitionWithoutReplacement,
            token => FirmwareModeReconnectPolicy.WaitForRebootGraceAsync(token),
            (attempt, token) => FirmwareModeReconnectPolicy.ExecuteAsync(attempt, token),
            log)
    {
    }

    internal TcpFirmwareModeControlPath(
        Func<bool> isAvailable,
        Func<CancellationToken, Task<FirmwareModeInfo>> queryCurrent,
        Func<FirmwarePersonality, CancellationToken, Task<FirmwareModeRequestResult>> sendRequest,
        Func<CancellationToken, Task> prepareReconnect,
        Func<int, CancellationToken, Task<FirmwareModeInfo>> connectFreshAttempt,
        Action endTransitionWithoutReplacement,
        Func<CancellationToken, Task> waitForRebootGrace,
        Func<Func<int, CancellationToken, Task<FirmwareModeInfo>>, CancellationToken,
            Task<FirmwareModeInfo>> executeReconnectPolicy,
        Action<string>? log = null)
        : this(isAvailable, queryCurrent, sendRequest,
            async (_, token) =>
            {
                await prepareReconnect(token).ConfigureAwait(false);
                try
                {
                    await waitForRebootGrace(token).ConfigureAwait(false);
                    return await executeReconnectPolicy(connectFreshAttempt, token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    endTransitionWithoutReplacement();
                    throw;
                }
            }, log)
    {
    }

    public bool IsAvailable => _isAvailable();

    public Task<FirmwareModeInfo> QueryCurrentAsync(CancellationToken cancellationToken) =>
        _queryCurrent(cancellationToken);

    public async Task<FirmwareModeRequestResult> SendRequestAsync(
        FirmwarePersonality target,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _sendRequest(target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // An accepted REQUEST_MODE can close the old socket before its ACK
            // arrives. A fresh verified session remains the success authority.
            _log?.Invoke($"FIRMWARE MODE TCP response lost during expected reboot: {ex.Message}");
            return new FirmwareModeRequestResult(FirmwareModeRequestKind.Ack, null);
        }
    }

    public Task<FirmwareModeInfo> ReconnectAndVerifyAsync(
        FirmwarePersonality target,
        CancellationToken cancellationToken) =>
        _reconnectAndVerify(target, cancellationToken);
}

public sealed class BridgeFirmwareService : IBridgeFirmwareServiceAfterPersistence
{
    private readonly IFirmwareModeTransitionService _transitionService;
    private readonly IAccessoryPersistenceService _persistence;
    private readonly Func<IFirmwareModeControlPath> _selectControlPath;

    internal BridgeFirmwareService(
        IBridgeDeviceStateProvider stateProvider,
        IAccessoryPersistenceService persistence,
        Func<IFirmwareModeControlPath> selectControlPath)
    {
        ArgumentNullException.ThrowIfNull(stateProvider);
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _selectControlPath = selectControlPath ??
            throw new ArgumentNullException(nameof(selectControlPath));
        _transitionService = new FirmwareModeTransitionService(stateProvider);
    }

    public async Task<FirmwareModeTransitionResult> SelectModeAsync(
        FirmwarePersonality target,
        CancellationToken cancellationToken = default)
        => await SelectModeCoreAsync(target,
            token => _persistence.FlushAsync(
                AccessoryPersistenceFlushReason.FirmwareChange, token),
            cancellationToken)
            .ConfigureAwait(false);

    Task<FirmwareModeTransitionResult>
        IBridgeFirmwareServiceAfterPersistence.SelectModeAfterPersistenceAsync(
            FirmwarePersonality target,
            CancellationToken cancellationToken) =>
        SelectModeCoreAsync(target, _ => Task.FromResult(AccessoryPersistenceResult.Clean),
            cancellationToken);

    private async Task<FirmwareModeTransitionResult> SelectModeCoreAsync(
        FirmwarePersonality target,
        Func<CancellationToken, Task<AccessoryPersistenceResult>> flushPersistence,
        CancellationToken cancellationToken)
    {
        if (target == FirmwarePersonality.Unknown)
            return Reject(FirmwareModeTransitionFailure.InvalidTarget,
                "Unknown is not a selectable firmware mode.");

        if (cancellationToken.IsCancellationRequested)
            return Reject(FirmwareModeTransitionFailure.Cancelled,
                "Firmware mode transition was cancelled.");
        IFirmwareModeControlPath controlPath = _selectControlPath();
        if (!controlPath.IsAvailable)
            return Reject(FirmwareModeTransitionFailure.ControlPathUnavailable,
                "The selected NICE64 firmware control path is unavailable.");

        return await _transitionService.ExecuteAsync(new(
            target,
            flushPersistence,
            controlPath.QueryCurrentAsync,
            controlPath.SendRequestAsync,
            controlPath.ReconnectAndVerifyAsync), cancellationToken).ConfigureAwait(false);
    }

    private static FirmwareModeTransitionResult Reject(
        FirmwareModeTransitionFailure failure,
        string error) =>
        new(false, null, error, false, false) { Failure = failure };
}
