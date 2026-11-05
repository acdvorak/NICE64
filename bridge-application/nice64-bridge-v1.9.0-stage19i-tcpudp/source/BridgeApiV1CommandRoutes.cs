namespace PicoSwitchController;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

public sealed record BridgeApiV1FirmwareCommandRequest(string? Personality);

public sealed record BridgeApiV1AccessoryCommandRequest(string? Accessory);

public sealed record BridgeApiV1ModuleCommandRequest(string? ModuleId);

public sealed record BridgeApiV1CarrierCommandRequest(string? Carrier);

public sealed record BridgeApiV1CommandAcceptedResponse(
    bool Accepted, long OperationId, string OperationKind);

public sealed record BridgeApiV1CommandBusyResponse(
    string Code, long CurrentOperationId, string CurrentOperationKind);

public sealed record BridgeApiV1CommandRejectedResponse(string Code, string Detail);

internal static class BridgeApiV1CommandRoutes
{
    internal static void Map(
        WebApplication application,
        IBridgeApiCommandFacade commands)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(commands);

        application.MapPost("/api/v1/firmware",
            (Func<HttpContext, Task<IResult>>)(context =>
            ReadRequestAsync<BridgeApiV1FirmwareCommandRequest>(context, request =>
                TryParseFirmware(request.Personality, out FirmwarePersonality target)
                    ? ToHttpResult(commands.SelectFirmware(target))
                    : Rejected("invalid-firmware",
                        "A supported firmware personality is required."))));

        application.MapPost("/api/v1/accessory",
            (Func<HttpContext, Task<IResult>>)(context =>
            ReadRequestAsync<BridgeApiV1AccessoryCommandRequest>(context, request =>
                TryParseAccessory(request.Accessory, out BridgeAccessoryMode target)
                    ? ToHttpResult(commands.SelectAccessory(target))
                    : Rejected("invalid-accessory",
                        "A supported accessory is required."))));

        application.MapPost("/api/v1/network-pak/module",
            (Func<HttpContext, Task<IResult>>)(context =>
            ReadRequestAsync<BridgeApiV1ModuleCommandRequest>(context, request =>
                ToHttpResult(commands.SelectNetworkPakModule(request.ModuleId)))));

        application.MapPost("/api/v1/network-pak/carrier",
            (Func<HttpContext, Task<IResult>>)(context =>
            ReadRequestAsync<BridgeApiV1CarrierCommandRequest>(context, request =>
                TryParseNetworkPakCarrier(request.Carrier,
                    out BridgeNetworkPakTransport target)
                    ? ToHttpResult(commands.SelectNetworkPakCarrier(target))
                    : Rejected("invalid-carrier",
                        "A supported remote Network Pak carrier is required."))));
    }

    private static async Task<IResult> ReadRequestAsync<TRequest>(
        HttpContext context, Func<TRequest, IResult> handler)
        where TRequest : class
    {
        if (!context.Request.HasJsonContentType())
        {
            return Results.Json(new BridgeApiV1CommandRejectedResponse(
                "unsupported-media-type", "A JSON request body is required."),
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        try
        {
            TRequest? request = await context.Request.ReadFromJsonAsync<TRequest>(
                context.RequestAborted).ConfigureAwait(false);
            return request is null
                ? Rejected("invalid-request", "A valid JSON request body is required.")
                : handler(request);
        }
        catch (JsonException)
        {
            return Rejected("invalid-request", "A valid JSON request body is required.");
        }
        catch (BadHttpRequestException)
        {
            return Rejected("invalid-request", "A valid JSON request body is required.");
        }
    }

    private static bool TryParseFirmware(
        string? value, out FirmwarePersonality personality)
    {
        personality = value?.Trim() switch
        {
            "controller" => FirmwarePersonality.Controller,
            "diagnostics" => FirmwarePersonality.Diagnostics,
            "network" => FirmwarePersonality.Network,
            "keyboard" => FirmwarePersonality.Keyboard,
            "mouse" => FirmwarePersonality.Mouse,
            _ => FirmwarePersonality.Unknown
        };
        return personality != FirmwarePersonality.Unknown;
    }

    private static bool TryParseAccessory(
        string? value, out BridgeAccessoryMode accessory)
    {
        bool recognized = true;
        accessory = value?.Trim() switch
        {
            "none" => BridgeAccessoryMode.None,
            "controller-pak" => BridgeAccessoryMode.ControllerPak,
            "rumble-pak" => BridgeAccessoryMode.RumblePak,
            "transfer-pak" => BridgeAccessoryMode.TransferPak,
            "network-pak" => BridgeAccessoryMode.NetworkPak,
            _ => Unknown()
        };
        return recognized;

        BridgeAccessoryMode Unknown()
        {
            recognized = false;
            return BridgeAccessoryMode.None;
        }
    }

    private static bool TryParseNetworkPakCarrier(
        string? value, out BridgeNetworkPakTransport carrier)
    {
        carrier = value?.Trim() switch
        {
            "tcp" => BridgeNetworkPakTransport.Tcp,
            "reliable-udp-quick" => BridgeNetworkPakTransport.ReliableUdpQuick,
            _ => BridgeNetworkPakTransport.None
        };
        return carrier != BridgeNetworkPakTransport.None;
    }

    private static IResult ToHttpResult(BridgeApiCommandResult result) =>
        result.Disposition switch
        {
            BridgeApiCommandDisposition.Accepted when result.OperationId is long operationId =>
                Results.Json(new BridgeApiV1CommandAcceptedResponse(
                    true, operationId, OperationKind(result.OperationKind)),
                    statusCode: StatusCodes.Status202Accepted),
            BridgeApiCommandDisposition.Busy when result.OperationId is long operationId =>
                Results.Json(new BridgeApiV1CommandBusyResponse(
                    "operation-busy", operationId, OperationKind(result.OperationKind)),
                    statusCode: StatusCodes.Status409Conflict),
            BridgeApiCommandDisposition.Rejected => Rejected(
                result.FailureCode ?? "command-rejected",
                result.SafeDetail ?? "The command was rejected."),
            _ => Results.Json(new BridgeApiV1CommandRejectedResponse(
                "command-result-invalid", "The command result could not be represented."),
                statusCode: StatusCodes.Status500InternalServerError)
        };

    private static IResult Rejected(string code, string detail) =>
        Results.Json(new BridgeApiV1CommandRejectedResponse(code, detail),
            statusCode: StatusCodes.Status400BadRequest);

    private static string OperationKind(BridgeOperationKind kind) => kind switch
    {
        BridgeOperationKind.None => "none",
        BridgeOperationKind.Connect => "connect",
        BridgeOperationKind.Disconnect => "disconnect",
        BridgeOperationKind.FirmwareQuery => "firmware-query",
        BridgeOperationKind.FirmwareModeSwitch => "firmware-mode-switch",
        BridgeOperationKind.AccessorySelection => "accessory-selection",
        BridgeOperationKind.ControllerPakCreate => "controller-pak-create",
        BridgeOperationKind.ControllerPakLoad => "controller-pak-load",
        BridgeOperationKind.ControllerPakSave => "controller-pak-save",
        BridgeOperationKind.TransferPakInstall => "transfer-pak-install",
        BridgeOperationKind.TransferPakSave => "transfer-pak-save",
        BridgeOperationKind.TransferPakLegacyLoad => "transfer-pak-legacy-load",
        BridgeOperationKind.WifiConfigureAndConnect => "wifi-configure-and-connect",
        BridgeOperationKind.WifiConnectRetained => "wifi-connect-retained",
        BridgeOperationKind.WifiDisconnect => "wifi-disconnect",
        BridgeOperationKind.WifiForget => "wifi-forget",
        BridgeOperationKind.NetworkPakModuleSelection => "network-pak-module-selection",
        BridgeOperationKind.NetworkPakCarrierSelection => "network-pak-carrier-selection",
        _ => "unknown"
    };
}
