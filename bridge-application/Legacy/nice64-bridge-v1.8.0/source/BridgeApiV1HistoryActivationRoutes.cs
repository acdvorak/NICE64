namespace PicoSwitchController;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

public sealed record BridgeApiV1HistoryActivationHttpConfirmation(
    string Category,
    string FileName,
    string ContentKind,
    string? Title);

public sealed record BridgeApiV1HistoryActivationResponse(
    bool Accepted,
    long? OperationId,
    string? OperationKind,
    long? CurrentOperationId,
    string? CurrentOperationKind,
    string? Code,
    string? Detail,
    BridgeApiV1HistoryActivationHttpConfirmation? Confirmation);

internal static class BridgeApiV1HistoryActivationRoutes
{
    internal const string Route =
        "/api/v1/sessions/{sessionId}/history/{itemId}/activate";

    internal static void Map(
        WebApplication application,
        IBridgeApiV1HistoryActivationFacade activation)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(activation);

        application.MapPost(Route,
            async (string sessionId, string itemId, HttpContext context) =>
            {
                (BridgeApiV1HistoryActivationRequest? request, IResult? invalid) =
                    await ReadRequestAsync(context).ConfigureAwait(false);
                if (invalid is not null) return invalid;

                try
                {
                    BridgeApiV1HistoryActivationResult result =
                        await activation.StartAsync(sessionId, itemId, request!,
                            context.RequestAborted).ConfigureAwait(false);
                    return ToHttpResult(result);
                }
                catch (OperationCanceledException)
                    when (context.RequestAborted.IsCancellationRequested)
                {
                    return Failure(StatusCodes.Status409Conflict,
                        "request-cancelled",
                        "The History activation request was cancelled.");
                }
                catch
                {
                    return Failure(StatusCodes.Status500InternalServerError,
                        "internal-error",
                        "The History activation request could not be completed.");
                }
            });
    }

    private static async Task<(BridgeApiV1HistoryActivationRequest? Request,
        IResult? Invalid)> ReadRequestAsync(HttpContext context)
    {
        if (!context.Request.HasJsonContentType())
            return (null, Failure(StatusCodes.Status415UnsupportedMediaType,
                "unsupported-media-type", "A JSON request body is required."));

        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                context.Request.Body, cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return InvalidRequest();

            JsonProperty[] properties = root.EnumerateObject().ToArray();
            if (properties.Length != 1 ||
                properties[0].Name != "destructiveInstallConfirmed" ||
                properties[0].Value.ValueKind is not
                    (JsonValueKind.True or JsonValueKind.False))
                return InvalidRequest();

            return (new(properties[0].Value.GetBoolean()), null);
        }
        catch (JsonException)
        {
            return InvalidRequest();
        }
        catch (BadHttpRequestException)
        {
            return InvalidRequest();
        }
    }

    private static (BridgeApiV1HistoryActivationRequest? Request,
        IResult? Invalid) InvalidRequest() =>
        (null, Failure(StatusCodes.Status400BadRequest, "invalid-request",
            "Exactly one destructiveInstallConfirmed boolean is required."));

    private static IResult ToHttpResult(
        BridgeApiV1HistoryActivationResult result) => result.Disposition switch
        {
            BridgeApiV1HistoryActivationDisposition.Accepted
                when result.Accepted && result.OperationId is > 0 &&
                    result.OperationKind ==
                        BridgeApiV1HistoryActivationFacade.HistoryOperationKind =>
                Results.Json(new BridgeApiV1HistoryActivationResponse(
                    true, result.OperationId,
                    BridgeApiV1HistoryActivationFacade.HistoryOperationKind,
                    null, null, null, null, null),
                    statusCode: StatusCodes.Status202Accepted),

            BridgeApiV1HistoryActivationDisposition.ConfirmationRequired
                when result.Confirmation is not null =>
                Results.Json(new BridgeApiV1HistoryActivationResponse(
                    false, null, null, null, null, "confirmation-required",
                    "Transfer Pak full-ROM installation requires explicit confirmation.",
                    SafeConfirmation(result.Confirmation)),
                    statusCode: StatusCodes.Status409Conflict),

            BridgeApiV1HistoryActivationDisposition.Busy =>
                Results.Json(new BridgeApiV1HistoryActivationResponse(
                    false, null, null, result.CurrentOperationId,
                    SafeOperationKind(result.CurrentOperationKind),
                    "operation-busy",
                    "Another Bridge operation is already in progress.", null),
                    statusCode: StatusCodes.Status409Conflict),

            BridgeApiV1HistoryActivationDisposition.SessionNotFound =>
                Failure(StatusCodes.Status404NotFound, "session-not-found",
                    "The requested Bridge session is not active."),

            BridgeApiV1HistoryActivationDisposition.HistoryItemNotFound =>
                Failure(StatusCodes.Status404NotFound, "history-item-not-found",
                    "The requested History item does not exist."),

            BridgeApiV1HistoryActivationDisposition.HistoryItemUnavailable =>
                Failure(StatusCodes.Status409Conflict, "history-item-unavailable",
                    "The requested History item is currently unavailable."),

            BridgeApiV1HistoryActivationDisposition.Rejected =>
                Rejected(result.Code),

            _ => Failure(StatusCodes.Status500InternalServerError,
                "internal-error",
                "The History activation result could not be represented.")
        };

    private static IResult Rejected(string? code) => code switch
    {
        "invalid-confirmation" => Failure(StatusCodes.Status400BadRequest,
            code, "Destructive installation confirmation is valid only for Transfer Pak History."),
        "normal-mode-required" => Failure(StatusCodes.Status409Conflict,
            code, "History activation requires Controller/Normal firmware."),
        "inspection-failed" => Failure(StatusCodes.Status409Conflict,
            code, "The Transfer Pak History item could not be inspected safely."),
        "request-cancelled" => Failure(StatusCodes.Status409Conflict,
            code, "The History activation request was cancelled."),
        "activation-prerequisite-changed" => Failure(StatusCodes.Status409Conflict,
            code, "History activation prerequisites changed before admission."),
        "activation-unavailable" => Failure(StatusCodes.Status503ServiceUnavailable,
            code, "History activation could not be prepared safely."),
        _ => Failure(StatusCodes.Status409Conflict, "activation-rejected",
            "The History activation request could not be accepted safely.")
    };

    private static BridgeApiV1HistoryActivationHttpConfirmation SafeConfirmation(
        BridgeApiV1HistoryActivationConfirmation source) => new(
            source.Category == "transfer-pak" ? "transfer-pak" : "unknown",
            SafeFileName(source.FileName),
            source.ContentKind == "game-boy-rom" ? "game-boy-rom" : "unknown",
            SafePresentationText(source.Title));

    private static string SafeFileName(string? value)
    {
        string? safe = SafePresentationText(value);
        return safe is not null &&
            !safe.Contains('/') && !safe.Contains('\\') && !safe.Contains(':')
            ? safe
            : "unavailable";
    }

    private static string? SafePresentationText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string safe = new string(value.Where(character => !char.IsControl(character))
            .ToArray()).Trim();
        if (safe.Contains("\\\\", StringComparison.Ordinal) ||
            safe.Contains(":\\", StringComparison.Ordinal) ||
            safe.Contains(":/", StringComparison.Ordinal) ||
            safe.StartsWith('/'))
            return null;
        return safe.Length <= 128 ? safe : safe[..128];
    }

    private static string SafeOperationKind(string? value) => value switch
    {
        "connect" or "disconnect" or "firmware-query" or
        "firmware-mode-switch" or "accessory-selection" or
        "controller-pak-create" or "controller-pak-load" or
        "controller-pak-save" or "transfer-pak-install" or
        "transfer-pak-save" or "transfer-pak-legacy-load" or
        "wifi-configure-and-connect" or "wifi-connect-retained" or
        "wifi-disconnect" or "wifi-forget" or
        "network-pak-module-selection" or
        "network-pak-carrier-selection" or "history-activation" => value,
        _ => "unknown"
    };

    private static IResult Failure(int statusCode, string code, string detail) =>
        Results.Json(new BridgeApiV1HistoryActivationResponse(
            false, null, null, null, null, code, detail, null),
            statusCode: statusCode);
}
