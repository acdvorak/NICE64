namespace PicoSwitchController;

public sealed record FirmwareModeTransitionRequest(
    FirmwarePersonality Target,
    Func<CancellationToken, Task<AccessoryPersistenceResult>> FlushPersistence,
    Func<CancellationToken, Task<FirmwareModeInfo>> QueryCurrent,
    Func<FirmwarePersonality, CancellationToken, Task<FirmwareModeRequestResult>> SendRequest,
    Func<FirmwarePersonality, CancellationToken, Task<FirmwareModeInfo>> ReconnectAndVerify);

public sealed record FirmwareModeTransitionResult(
    bool Success,
    FirmwareModeInfo? VerifiedMode,
    string Error,
    bool RequestSent,
    bool Reconnected)
{
    public FirmwareModeTransitionFailure Failure { get; init; }
}

public enum FirmwareModeTransitionFailure
{
    None,
    InvalidTarget,
    ControlPathUnavailable,
    Persistence,
    RequestRejected,
    RequestNotAcknowledged,
    Verification,
    Reconnect,
    Cancelled,
    QueryCurrent,
    Request,
    Unexpected
}

internal enum FirmwareModeTransitionStage
{
    QueryCurrent,
    Persistence,
    SendRequest,
    AlreadyActiveVerification,
    ReconnectAndVerify
}

public interface IFirmwareModeTransitionService
{
    Task<FirmwareModeTransitionResult> ExecuteAsync(
        FirmwareModeTransitionRequest request,
        CancellationToken cancellationToken = default);
}

public static class FirmwareModeReconnectPolicy
{
    public const int AttemptCount = 3;
    public static readonly TimeSpan RebootGracePeriod = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan AttemptSpacing = TimeSpan.FromSeconds(2);

    public static Task WaitForRebootGraceAsync(CancellationToken cancellationToken,
        TimeSpan? grace = null) =>
        Task.Delay(grace ?? RebootGracePeriod, cancellationToken);

    public static async Task<T> ExecuteAsync<T>(
        Func<int, CancellationToken, Task<T>> attempt,
        CancellationToken cancellationToken,
        TimeSpan? spacing = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        Exception? last = null;
        for (int number = 1; number <= AttemptCount; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await attempt(number, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                if (number < AttemptCount)
                    await Task.Delay(spacing ?? AttemptSpacing, cancellationToken)
                        .ConfigureAwait(false);
            }
        }
        throw new TimeoutException(
            $"NICE64 did not reconnect after {AttemptCount} Wi-Fi/TCP attempts: {last?.Message}",
            last);
    }
}

public sealed class FirmwareModeTransitionService(
    IBridgeDeviceStateProvider stateProvider) : IFirmwareModeTransitionService
{
    private readonly IBridgeDeviceStateUpdater _updater =
        stateProvider as IBridgeDeviceStateUpdater
        ?? throw new ArgumentException("The host state provider is not mutable by the service layer.",
            nameof(stateProvider));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<FirmwareModeTransitionResult> ExecuteAsync(
        FirmwareModeTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool sent = false;
        bool reconnected = false;
        FirmwareModeInfo? current = null;
        FirmwareModeTransitionStage stage = FirmwareModeTransitionStage.QueryCurrent;
        try
        {
            _updater.SetFirmwareTransition(new(
                BridgeFirmwareTransitionPhase.TransitionPending, request.Target, string.Empty));
            current = await request.QueryCurrent(cancellationToken).ConfigureAwait(false);
            if (current.Personality == request.Target)
            {
                _updater.UpdateFirmwareMode(current);
                _updater.SetFirmwareTransition(new(
                    BridgeFirmwareTransitionPhase.Completed, request.Target, string.Empty));
                return new(true, current, string.Empty, false, false);
            }

            stage = FirmwareModeTransitionStage.Persistence;
            AccessoryPersistenceResult persistence =
                await request.FlushPersistence(cancellationToken).ConfigureAwait(false);
            if (!persistence.Success)
                return Fail(current, request.Target,
                    $"Persistence flush failed: {persistence.Error}", false, false,
                    FirmwareModeTransitionFailure.Persistence);

            stage = FirmwareModeTransitionStage.SendRequest;
            FirmwareModeRequestResult response =
                await request.SendRequest(request.Target, cancellationToken).ConfigureAwait(false);
            sent = true;
            if (response.Kind == FirmwareModeRequestKind.Unsupported)
                return Fail(current, request.Target, "Pico rejected the requested mode.", sent, false,
                    FirmwareModeTransitionFailure.RequestRejected);
            if (response.Kind == FirmwareModeRequestKind.AlreadyActive)
            {
                stage = FirmwareModeTransitionStage.AlreadyActiveVerification;
                FirmwareModeInfo verified = await request.QueryCurrent(cancellationToken)
                    .ConfigureAwait(false);
                if (verified.Personality != request.Target)
                    return Fail(verified, request.Target,
                        $"Already-active response disagreed with verified mode {verified.DisplayName}.", sent, false,
                        FirmwareModeTransitionFailure.Verification);
                _updater.UpdateFirmwareMode(verified);
                _updater.SetFirmwareTransition(new(
                    BridgeFirmwareTransitionPhase.Completed, request.Target, string.Empty));
                return new(true, verified, string.Empty, sent, false);
            }
            if (response.Kind != FirmwareModeRequestKind.Ack)
                return Fail(current, request.Target,
                    $"Mode-switch command was not acknowledged ({response.Kind}).", sent, false,
                    FirmwareModeTransitionFailure.RequestNotAcknowledged);

            _updater.SetFirmwareTransition(new(
                BridgeFirmwareTransitionPhase.RebootExpected, request.Target, string.Empty));
            _updater.SetFirmwareTransition(new(
                BridgeFirmwareTransitionPhase.Reconnecting, request.Target, string.Empty));
            stage = FirmwareModeTransitionStage.ReconnectAndVerify;
            FirmwareModeInfo returned = await request.ReconnectAndVerify(
                request.Target, cancellationToken).ConfigureAwait(false);
            reconnected = true;
            if (returned.Personality != request.Target)
                return Fail(returned, request.Target,
                    $"Pico reconnected in {returned.DisplayName}, not {request.Target}.", sent, true,
                    FirmwareModeTransitionFailure.Verification);
            _updater.UpdateFirmwareMode(returned);
            _updater.SetFirmwareTransition(new(
                BridgeFirmwareTransitionPhase.Completed, request.Target, string.Empty));
            return new(true, returned, string.Empty, sent, true);
        }
        catch (OperationCanceledException)
        {
            return Fail(current, request.Target, "Firmware mode transition was cancelled.",
                sent, reconnected, FirmwareModeTransitionFailure.Cancelled);
        }
        catch (Exception ex)
        {
            return Fail(current, request.Target, ex.Message, sent, reconnected,
                FailureForStage(stage));
        }
        finally { _gate.Release(); }
    }

    private static FirmwareModeTransitionFailure FailureForStage(
        FirmwareModeTransitionStage stage) => stage switch
        {
            FirmwareModeTransitionStage.QueryCurrent =>
                FirmwareModeTransitionFailure.QueryCurrent,
            FirmwareModeTransitionStage.Persistence =>
                FirmwareModeTransitionFailure.Persistence,
            FirmwareModeTransitionStage.SendRequest =>
                FirmwareModeTransitionFailure.Request,
            FirmwareModeTransitionStage.AlreadyActiveVerification =>
                FirmwareModeTransitionFailure.Verification,
            FirmwareModeTransitionStage.ReconnectAndVerify =>
                FirmwareModeTransitionFailure.Reconnect,
            _ => FirmwareModeTransitionFailure.Unexpected
        };

    private FirmwareModeTransitionResult Fail(FirmwareModeInfo? actual,
        FirmwarePersonality target, string error, bool sent, bool reconnected,
        FirmwareModeTransitionFailure failure)
    {
        if (actual is not null) _updater.UpdateFirmwareMode(actual);
        _updater.SetFirmwareTransition(new(
            BridgeFirmwareTransitionPhase.Failed, target, error));
        return new(false, actual, error, sent, reconnected) { Failure = failure };
    }
}
