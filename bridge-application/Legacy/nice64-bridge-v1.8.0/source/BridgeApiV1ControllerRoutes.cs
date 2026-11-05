namespace PicoSwitchController;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

public sealed record BridgeApiV1ControllerPrimaryRequest(string? Primary);

internal static class BridgeApiV1ControllerRoutes
{
    internal static void Map(WebApplication application,
        IBridgeApiV1ControllerFacade controller)
    {
        application.MapGet("/api/v1/sessions/{sessionId}/controller/primary",
            async (string sessionId, CancellationToken cancellationToken) =>
                ToHttpResult(await controller.GetPrimaryAsync(
                    sessionId, cancellationToken).ConfigureAwait(false)));

        application.MapPut("/api/v1/sessions/{sessionId}/controller/primary",
            async (string sessionId, HttpContext context) =>
            {
                (BridgeControllerPrimary? primary, BridgeApiV1ControllerApiResult? invalid) =
                    await ReadRequestAsync(context).ConfigureAwait(false);
                return invalid is null
                    ? ToHttpResult(await controller.SetPrimaryAsync(sessionId,
                        primary!.Value, context.RequestAborted).ConfigureAwait(false))
                    : ToHttpResult(invalid);
            });
    }

    private static async Task<(BridgeControllerPrimary?, BridgeApiV1ControllerApiResult?)>
        ReadRequestAsync(HttpContext context)
    {
        if (!context.Request.HasJsonContentType())
            return (null, Error(StatusCodes.Status415UnsupportedMediaType,
                "unsupported-media-type", "A JSON request body is required."));
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                context.Request.Body, cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);
            JsonProperty[] properties = document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().ToArray() : [];
            if (properties.Length != 1 || properties[0].Name != "primary" ||
                properties[0].Value.ValueKind != JsonValueKind.String)
                return (null, Invalid());
            return properties[0].Value.GetString() switch
            {
                "bridge" => (BridgeControllerPrimary.Bridge, null),
                "nice-mobile" => (BridgeControllerPrimary.NiceMobile, null),
                _ => (null, Invalid())
            };
        }
        catch (JsonException) { return (null, Invalid()); }
        catch (BadHttpRequestException) { return (null, Invalid()); }
    }

    private static BridgeApiV1ControllerApiResult Invalid() => Error(
        StatusCodes.Status400BadRequest, "invalid-request",
        "Exactly one primary value of 'bridge' or 'nice-mobile' is required.");
    private static BridgeApiV1ControllerApiResult Error(
        int status, string code, string detail) =>
        new(status, new BridgeApiV1ControllerFailure(code, detail));
    private static IResult ToHttpResult(BridgeApiV1ControllerApiResult result) =>
        Results.Json(result.Body, statusCode: result.StatusCode);
}
