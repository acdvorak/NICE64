namespace PicoSwitchController;

using Microsoft.AspNetCore.Http;

public sealed record BridgeApiV1ControllerApiResult(int StatusCode, object Body);

public interface IBridgeApiV1ControllerFacade
{
    Task<BridgeApiV1ControllerApiResult> GetPrimaryAsync(
        string sessionId, CancellationToken cancellationToken = default);
    Task<BridgeApiV1ControllerApiResult> SetPrimaryAsync(
        string sessionId, BridgeControllerPrimary primary,
        CancellationToken cancellationToken = default);
}

public sealed class BridgeApiV1ControllerFacade : IBridgeApiV1ControllerFacade
{
    private readonly IBridgeSessionProvider _sessions;
    private readonly IBridgeControllerPrimaryService _primary;

    public BridgeApiV1ControllerFacade(
        IBridgeSessionProvider sessions,
        IBridgeControllerPrimaryService primary)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
    }

    public Task<BridgeApiV1ControllerApiResult> GetPrimaryAsync(
        string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(IsCurrent(sessionId)
            ? Ok(BridgeApiV1ControllerProjector.Project(sessionId, _primary.Current))
            : SessionNotFound());

    public async Task<BridgeApiV1ControllerApiResult> SetPrimaryAsync(
        string sessionId, BridgeControllerPrimary primary,
        CancellationToken cancellationToken = default)
    {
        if (!IsCurrent(sessionId)) return SessionNotFound();
        BridgeControllerPrimaryResult result = await _primary.SelectAsync(
            sessionId, primary, cancellationToken).ConfigureAwait(false);
        return result.Success
            ? Ok(BridgeApiV1ControllerProjector.Project(sessionId, result.Snapshot))
            : Failure(result.Failure);
    }

    private bool IsCurrent(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) &&
        string.Equals(_sessions.Current?.SessionId, sessionId, StringComparison.Ordinal);

    private static BridgeApiV1ControllerApiResult Failure(
        BridgeControllerPrimaryFailure failure) => failure switch
        {
            BridgeControllerPrimaryFailure.SessionNotFound or
            BridgeControllerPrimaryFailure.StaleSession => SessionNotFound(),
            BridgeControllerPrimaryFailure.SessionNotConnected => Conflict(
                "session-not-connected", "The requested Bridge session is not connected."),
            BridgeControllerPrimaryFailure.ControllerFirmwareUnavailable => Conflict(
                "controller-firmware-unavailable",
                "Controller firmware is not authoritative for this session."),
            BridgeControllerPrimaryFailure.EndpointUnavailable => Conflict(
                "endpoint-unavailable", "The direct controller endpoint is unavailable."),
            BridgeControllerPrimaryFailure.ProtectedOperation => Conflict(
                "protected-operation",
                "Controller primary cannot change during the current protected operation."),
            BridgeControllerPrimaryFailure.LocalDeliveryFailure =>
                new(StatusCodes.Status503ServiceUnavailable,
                    new BridgeApiV1ControllerFailure("controller-handoff-failed",
                        "Controller primary could not be transferred safely.")),
            BridgeControllerPrimaryFailure.Cancelled =>
                new(StatusCodes.Status400BadRequest,
                    new BridgeApiV1ControllerFailure("request-cancelled",
                        "The controller primary request was cancelled.")),
            _ => new(StatusCodes.Status500InternalServerError,
                new BridgeApiV1ControllerFailure("internal-error",
                    "The controller primary request could not be completed."))
        };

    private static BridgeApiV1ControllerApiResult Ok(object body) =>
        new(StatusCodes.Status200OK, body);
    private static BridgeApiV1ControllerApiResult SessionNotFound() =>
        new(StatusCodes.Status404NotFound,
            new BridgeApiV1ControllerFailure("session-not-found",
                "The requested Bridge session is not active."));
    private static BridgeApiV1ControllerApiResult Conflict(string code, string detail) =>
        new(StatusCodes.Status409Conflict, new BridgeApiV1ControllerFailure(code, detail));
}
