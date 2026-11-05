namespace PicoSwitchController;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public sealed record BridgeApiV1KeyboardStateRequest(int[]? HeldKeys);
public sealed record BridgeApiV1PointerStateRequest(
    bool Left, bool Right, int Dx, int Dy);
public sealed record BridgeApiV1KeyboardMouseStateResponse(
    bool Accepted, string Disposition, uint SessionId);
public sealed record BridgeApiV1KeyboardLedsResponse(
    byte Raw, bool Power, bool CapsLock, bool NumLock);
public sealed record BridgeApiV1KeyboardMouseFailure(string Code, string Detail);

public interface IBridgeKeyboardMouseStateFacade
{
    BridgeApiV1KeyboardMouseStateResponse SendKeyboard(int[] heldKeys);
    BridgeApiV1KeyboardMouseStateResponse SendPointer(
        bool left, bool right, int dx, int dy);
    BridgeApiV1KeyboardLedsResponse ReadKeyboardLeds();
}

public sealed class BridgeKeyboardMouseStateFacade : IBridgeKeyboardMouseStateFacade
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(1);
    private readonly object _transportLock;
    private readonly Func<IBridgeLineTransport?> _selectTransportLocked;
    private IBridgeLineTransport? _clientTransport;
    private BridgeKeyboardMouseStateClient? _client;

    public BridgeKeyboardMouseStateFacade(object transportLock,
        Func<IBridgeLineTransport?> selectTransportLocked)
    {
        _transportLock = transportLock ?? throw new ArgumentNullException(nameof(transportLock));
        _selectTransportLocked = selectTransportLocked ??
            throw new ArgumentNullException(nameof(selectTransportLocked));
    }

    public BridgeApiV1KeyboardMouseStateResponse SendKeyboard(int[] heldKeys)
    {
        ArgumentNullException.ThrowIfNull(heldKeys);
        Nice64LogicalKey[] keys = heldKeys.Select(value =>
            value > (int)Nice64LogicalKey.None && value < (int)Nice64LogicalKey.Count
                ? (Nice64LogicalKey)value
                : throw new ArgumentOutOfRangeException(nameof(heldKeys),
                    "A logical key identifier is outside the NICE64 key range."))
            .ToArray();
        return WithClient(client => ToResponse(client,
            client.SendKeyboard(keys, CommandTimeout)));
    }

    public BridgeApiV1KeyboardMouseStateResponse SendPointer(
        bool left, bool right, int dx, int dy)
    {
        Nice64PointerButtons buttons = Nice64PointerButtons.None;
        if (left) buttons |= Nice64PointerButtons.Left;
        if (right) buttons |= Nice64PointerButtons.Right;
        return WithClient(client => ToResponse(client,
            client.SendPointerDelta(buttons, dx, dy, CommandTimeout)));
    }

    public BridgeApiV1KeyboardLedsResponse ReadKeyboardLeds() =>
        WithClient<BridgeApiV1KeyboardLedsResponse>(client =>
        {
            byte value = client.ReadKeyboardLeds(CommandTimeout);
            return new(value, (value & 0x04) != 0, (value & 0x02) != 0,
                (value & 0x01) != 0);
        });

    private T WithClient<T>(Func<BridgeKeyboardMouseStateClient, T> operation)
    {
        lock (_transportLock)
        {
            IBridgeLineTransport transport = _selectTransportLocked() ??
                throw new InvalidOperationException("No active NICE64 line transport is available.");
            if (!ReferenceEquals(transport, _clientTransport))
            {
                _clientTransport = transport;
                _client = new BridgeKeyboardMouseStateClient(transport);
            }
            return operation(_client!);
        }
    }

    private static BridgeApiV1KeyboardMouseStateResponse ToResponse(
        BridgeKeyboardMouseStateClient client,
        KeyboardMouseStateDisposition disposition) =>
        new(disposition == KeyboardMouseStateDisposition.Accepted,
            disposition.ToString().ToLowerInvariant(), client.SessionId);
}

internal static class BridgeApiV1KeyboardMouseRoutes
{
    internal static void Map(WebApplication application,
        IBridgeKeyboardMouseStateFacade keyboardMouse)
    {
        application.MapPut("/api/v1/keyboard-mouse/keyboard-state",
            (BridgeApiV1KeyboardStateRequest request) => Execute(() =>
                keyboardMouse.SendKeyboard(request.HeldKeys ?? [])));
        application.MapPost("/api/v1/keyboard-mouse/pointer-delta",
            (BridgeApiV1PointerStateRequest request) => Execute(() =>
                keyboardMouse.SendPointer(request.Left, request.Right,
                    request.Dx, request.Dy)));
        application.MapGet("/api/v1/keyboard-mouse/keyboard-leds",
            () => Execute(keyboardMouse.ReadKeyboardLeds));
    }

    private static IResult Execute<T>(Func<T> action)
    {
        try
        {
            return Results.Json(action());
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new BridgeApiV1KeyboardMouseFailure(
                "invalid-state", ex.Message), statusCode: 400);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            return Results.Json(new BridgeApiV1KeyboardMouseFailure(
                "transport-unavailable", ex.Message), statusCode: 503);
        }
    }
}
