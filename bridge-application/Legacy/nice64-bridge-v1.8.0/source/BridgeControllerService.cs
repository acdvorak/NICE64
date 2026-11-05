namespace PicoSwitchController;

public enum ConsoleControllerType
{
    Snes,
    N64
}

public readonly record struct BridgeControllerState(
    ConsoleControllerType ControllerType,
    uint Buttons,
    sbyte AnalogX = 0,
    sbyte AnalogY = 0,
    bool AnalogActive = false)
{
    public bool Neutral => Buttons == 0 && AnalogX == 0 && AnalogY == 0;

    public static BridgeControllerState CreateNeutral(ConsoleControllerType controllerType) =>
        new(controllerType, 0, 0, 0, controllerType == ConsoleControllerType.N64);
}

public enum BridgeControllerFailure
{
    None,
    NotConnected,
    UnsupportedController,
    FirmwareUnavailable,
    DeliverySuspended,
    Busy,
    Cancelled,
    TransportUnavailable,
    Unexpected
}

public enum BridgeControllerDisposition
{
    Rejected,
    Unchanged,
    AcceptedPending,
    Delivered
}

public sealed record BridgeControllerSnapshot(
    BridgeControllerState LastRequestedState,
    BridgeControllerState? LastDeliveredState,
    bool DeliverySuspended,
    bool DeliveryPending);

public sealed record BridgeControllerResult(
    bool Success,
    BridgeControllerFailure Failure,
    BridgeControllerDisposition Disposition,
    BridgeControllerSnapshot Snapshot,
    string Error);

public interface IBridgeControllerService
{
    BridgeControllerSnapshot Current { get; }

    Task<BridgeControllerResult> SetStateAsync(
        BridgeControllerState state,
        CancellationToken cancellationToken = default);

    Task<BridgeControllerResult> SetNeutralAsync(
        CancellationToken cancellationToken = default);
}

internal enum BridgeControllerPortAvailability
{
    Available,
    NotConnected,
    FirmwareUnavailable,
    Busy,
    TransportUnavailable
}

internal enum BridgeControllerPortDisposition
{
    AcceptedPending,
    Delivered,
    Failed
}

internal sealed record BridgeControllerCommand(
    long Sequence,
    BridgeControllerState State,
    string WireCommand);

internal sealed record BridgeQueuedCommand(
    string WireCommand,
    BridgeControllerCommand? ControllerState = null);

internal sealed class BridgeControllerCommandQueue
{
    private readonly object _sync = new();
    private readonly Queue<BridgeQueuedCommand> _commands = new();

    public int Count
    {
        get { lock (_sync) return _commands.Count; }
    }

    public void EnqueueNonController(string wireCommand)
    {
        lock (_sync) _commands.Enqueue(new BridgeQueuedCommand(wireCommand));
    }

    public int EnqueueLatestController(BridgeControllerCommand command)
    {
        int superseded = 0;
        lock (_sync)
        {
            Queue<BridgeQueuedCommand> retained = new();
            while (_commands.Count > 0)
            {
                BridgeQueuedCommand queued = _commands.Dequeue();
                if (queued.ControllerState is null) retained.Enqueue(queued);
                else superseded++;
            }
            while (retained.Count > 0) _commands.Enqueue(retained.Dequeue());
            _commands.Enqueue(new BridgeQueuedCommand(command.WireCommand, command));
        }
        return superseded;
    }

    public int ClearPendingControllerState()
    {
        int dropped = 0;
        lock (_sync)
        {
            Queue<BridgeQueuedCommand> retained = new();
            while (_commands.Count > 0)
            {
                BridgeQueuedCommand queued = _commands.Dequeue();
                if (queued.ControllerState is null) retained.Enqueue(queued);
                else dropped++;
            }
            while (retained.Count > 0) _commands.Enqueue(retained.Dequeue());
        }
        return dropped;
    }

    public bool TryDequeue(out BridgeQueuedCommand? command)
    {
        lock (_sync)
        {
            if (_commands.Count == 0)
            {
                command = null;
                return false;
            }
            command = _commands.Dequeue();
            return true;
        }
    }
}

internal sealed record BridgeControllerPortResult(
    BridgeControllerPortDisposition Disposition,
    string Error = "")
{
    public static BridgeControllerPortResult AcceptedPending { get; } =
        new(BridgeControllerPortDisposition.AcceptedPending);
    public static BridgeControllerPortResult Delivered { get; } =
        new(BridgeControllerPortDisposition.Delivered);
}

internal interface IBridgeControllerCommandPort
{
    BridgeControllerPortAvailability Availability { get; }

    ValueTask<BridgeControllerPortResult> SubmitLatestAsync(
        BridgeControllerCommand command,
        CancellationToken cancellationToken);

    ValueTask<BridgeControllerPortResult> DeliverImmediatelyAsync(
        BridgeControllerCommand command,
        CancellationToken cancellationToken);

    void ClearPendingControllerState();
}

internal sealed class DelegateBridgeControllerCommandPort(
    Func<BridgeControllerPortAvailability> availability,
    Func<BridgeControllerCommand, CancellationToken,
        ValueTask<BridgeControllerPortResult>> submitLatest,
    Func<BridgeControllerCommand, CancellationToken,
        ValueTask<BridgeControllerPortResult>> deliverImmediately,
    Action clearPending) : IBridgeControllerCommandPort
{
    public BridgeControllerPortAvailability Availability => availability();

    public ValueTask<BridgeControllerPortResult> SubmitLatestAsync(
        BridgeControllerCommand command,
        CancellationToken cancellationToken) => submitLatest(command, cancellationToken);

    public ValueTask<BridgeControllerPortResult> DeliverImmediatelyAsync(
        BridgeControllerCommand command,
        CancellationToken cancellationToken) => deliverImmediately(command, cancellationToken);

    public void ClearPendingControllerState() => clearPending();
}

public sealed class BridgeControllerService : IBridgeControllerService
{
    private readonly IBridgeControllerCommandPort _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private BridgeControllerState _requested =
        BridgeControllerState.CreateNeutral(ConsoleControllerType.Snes);
    private BridgeControllerState? _delivered;
    private bool _hasRequested;
    private bool _deliveryPending;
    private bool _acceptedPending;
    private int _suspensionDepth;
    private long _nextSequence;
    private long _pendingSequence;

    internal BridgeControllerService(IBridgeControllerCommandPort port)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
    }

    public BridgeControllerSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return SnapshotLocked();
            }
        }
    }

    public Task<BridgeControllerResult> SetStateAsync(
        BridgeControllerState state,
        CancellationToken cancellationToken = default) =>
        SetStateCoreAsync(Normalize(state), forceDelivery: false, cancellationToken);

    public Task<BridgeControllerResult> SetNeutralAsync(
        CancellationToken cancellationToken = default)
    {
        ConsoleControllerType type;
        lock (_sync) type = _requested.ControllerType;
        return SetStateAsync(BridgeControllerState.CreateNeutral(type), cancellationToken);
    }

    internal void SuspendDelivery(bool clearPendingState = true)
    {
        lock (_sync)
        {
            _suspensionDepth++;
            if (clearPendingState)
            {
                _deliveryPending = false;
                _acceptedPending = false;
                _pendingSequence = 0;
            }
        }

        if (clearPendingState) _port.ClearPendingControllerState();
    }

    internal async Task<BridgeControllerResult> ResumeDeliveryAsync(
        bool resendCurrent,
        CancellationToken cancellationToken = default)
    {
        BridgeControllerState requested;
        bool stillSuspended;
        lock (_sync)
        {
            if (_suspensionDepth > 0) _suspensionDepth--;
            stillSuspended = _suspensionDepth > 0;
            requested = _requested;
        }

        if (stillSuspended || !resendCurrent)
            return Success(BridgeControllerDisposition.Unchanged);
        return await SetStateCoreAsync(requested, forceDelivery: true, cancellationToken)
            .ConfigureAwait(false);
    }

    internal void ClearPendingState()
    {
        _port.ClearPendingControllerState();
        lock (_sync)
        {
            _deliveryPending = false;
            _acceptedPending = false;
            _pendingSequence = 0;
        }
    }

    internal async Task<BridgeControllerResult> NeutralizeForDisconnectAsync(
        ConsoleControllerType controllerType,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Failure(BridgeControllerFailure.Cancelled, "Controller neutralization was cancelled.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BridgeControllerState neutral = BridgeControllerState.CreateNeutral(controllerType);
            BridgeControllerCommand command;
            lock (_sync)
            {
                _requested = neutral;
                _hasRequested = true;
                _deliveryPending = true;
                _acceptedPending = false;
                command = CreateCommandLocked(neutral);
            }

            _port.ClearPendingControllerState();
            BridgeControllerResult result;
            try
            {
                BridgeControllerPortResult portResult = await _port
                    .DeliverImmediatelyAsync(command, cancellationToken)
                    .ConfigureAwait(false);
                result = ApplyPortResult(command, portResult);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = Failure(BridgeControllerFailure.Cancelled,
                    "Controller neutralization was cancelled.");
            }
            catch (Exception ex)
            {
                result = Failure(BridgeControllerFailure.Unexpected, ex.Message);
            }

            lock (_sync)
            {
                // A destructive disconnect cannot retain a deliverable queue item.
                // Requested and delivered remain distinct if the final write failed.
                _deliveryPending = false;
                _acceptedPending = false;
                _pendingSequence = 0;
                _suspensionDepth = 0;
                return result with { Snapshot = SnapshotLocked() };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void ConfirmDelivered(BridgeControllerCommand command)
    {
        lock (_sync)
        {
            _delivered = command.State;
            if (_pendingSequence == command.Sequence)
            {
                _deliveryPending = false;
                _acceptedPending = false;
                _pendingSequence = 0;
            }
        }
    }

    internal void ReportNotDelivered(BridgeControllerCommand command)
    {
        lock (_sync)
        {
            if (_pendingSequence != command.Sequence) return;
            _deliveryPending = true;
            _acceptedPending = false;
        }
    }

    internal string BuildCurrentWireCommand()
    {
        lock (_sync)
            return BridgeControllerCommandBuilder.BuildCommand(_requested);
    }

    private async Task<BridgeControllerResult> SetStateCoreAsync(
        BridgeControllerState state,
        bool forceDelivery,
        CancellationToken cancellationToken)
    {
        if (!IsSupported(state.ControllerType))
            return Failure(BridgeControllerFailure.UnsupportedController,
                $"Unsupported controller type: {state.ControllerType}.");
        if (cancellationToken.IsCancellationRequested)
            return Failure(BridgeControllerFailure.Cancelled,
                "Controller state submission was cancelled.");

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failure(BridgeControllerFailure.Cancelled,
                "Controller state submission was cancelled.");
        }

        try
        {
            lock (_sync)
            {
                bool identical = state == _requested;
                if (!forceDelivery && _hasRequested && identical &&
                    (!_deliveryPending || _acceptedPending))
                {
                    return SuccessLocked(_deliveryPending
                        ? BridgeControllerDisposition.AcceptedPending
                        : BridgeControllerDisposition.Unchanged);
                }

                _requested = state;
                _hasRequested = true;
                _deliveryPending = true;
                _acceptedPending = false;
                if (_suspensionDepth > 0)
                    return FailureLocked(BridgeControllerFailure.DeliverySuspended,
                        "Controller delivery is suspended.");
            }

            BridgeControllerFailure availabilityFailure =
                MapAvailability(_port.Availability);
            if (availabilityFailure != BridgeControllerFailure.None)
                return Failure(availabilityFailure, AvailabilityError(availabilityFailure));

            BridgeControllerCommand command;
            lock (_sync) command = CreateCommandLocked(state);

            BridgeControllerPortResult portResult;
            try
            {
                portResult = await _port.SubmitLatestAsync(command, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(BridgeControllerFailure.Cancelled,
                    "Controller state submission was cancelled.");
            }
            catch (Exception ex)
            {
                return Failure(BridgeControllerFailure.Unexpected, ex.Message);
            }

            return ApplyPortResult(command, portResult);
        }
        finally
        {
            _gate.Release();
        }
    }

    private BridgeControllerCommand CreateCommandLocked(BridgeControllerState state)
    {
        long sequence = ++_nextSequence;
        _pendingSequence = sequence;
        return new BridgeControllerCommand(sequence, state,
            BridgeControllerCommandBuilder.BuildCommand(state));
    }

    private BridgeControllerResult ApplyPortResult(
        BridgeControllerCommand command,
        BridgeControllerPortResult portResult)
    {
        lock (_sync)
        {
            switch (portResult.Disposition)
            {
                case BridgeControllerPortDisposition.Delivered:
                    _delivered = command.State;
                    if (_pendingSequence == command.Sequence)
                    {
                        _deliveryPending = false;
                        _acceptedPending = false;
                        _pendingSequence = 0;
                    }
                    return SuccessLocked(BridgeControllerDisposition.Delivered);
                case BridgeControllerPortDisposition.AcceptedPending:
                    if (_pendingSequence == command.Sequence)
                    {
                        _deliveryPending = true;
                        _acceptedPending = true;
                    }
                    return SuccessLocked(BridgeControllerDisposition.AcceptedPending);
                default:
                    _deliveryPending = true;
                    _acceptedPending = false;
                    return FailureLocked(BridgeControllerFailure.TransportUnavailable,
                        portResult.Error);
            }
        }
    }

    private BridgeControllerResult Success(BridgeControllerDisposition disposition)
    {
        lock (_sync) return SuccessLocked(disposition);
    }

    private BridgeControllerResult SuccessLocked(BridgeControllerDisposition disposition) =>
        new(true, BridgeControllerFailure.None, disposition, SnapshotLocked(), string.Empty);

    private BridgeControllerResult Failure(BridgeControllerFailure failure, string error)
    {
        lock (_sync) return FailureLocked(failure, error);
    }

    private BridgeControllerResult FailureLocked(
        BridgeControllerFailure failure,
        string error) =>
        new(false, failure, BridgeControllerDisposition.Rejected, SnapshotLocked(), error);

    private BridgeControllerSnapshot SnapshotLocked() =>
        new(_requested, _delivered, _suspensionDepth > 0, _deliveryPending);

    private static BridgeControllerState Normalize(BridgeControllerState state) =>
        state.ControllerType == ConsoleControllerType.Snes
            ? state with { AnalogX = 0, AnalogY = 0, AnalogActive = false }
            : state;

    private static bool IsSupported(ConsoleControllerType type) =>
        type is ConsoleControllerType.Snes or ConsoleControllerType.N64;

    private static BridgeControllerFailure MapAvailability(
        BridgeControllerPortAvailability availability) => availability switch
    {
        BridgeControllerPortAvailability.Available => BridgeControllerFailure.None,
        BridgeControllerPortAvailability.NotConnected => BridgeControllerFailure.NotConnected,
        BridgeControllerPortAvailability.FirmwareUnavailable =>
            BridgeControllerFailure.FirmwareUnavailable,
        BridgeControllerPortAvailability.Busy => BridgeControllerFailure.Busy,
        _ => BridgeControllerFailure.TransportUnavailable
    };

    private static string AvailabilityError(BridgeControllerFailure failure) => failure switch
    {
        BridgeControllerFailure.NotConnected => "The NICE64 Pico is not connected.",
        BridgeControllerFailure.FirmwareUnavailable =>
            "Controller input is unavailable in the active firmware personality.",
        BridgeControllerFailure.Busy => "A protected Bridge operation is active.",
        _ => "The active controller transport is unavailable."
    };
}

internal static class BridgeControllerCommandBuilder
{
    public static string BuildCommand(BridgeControllerState state)
    {
        if (state.ControllerType == ConsoleControllerType.Snes)
            return $"MASK {state.Buttons & 0x0fffu:X4}";
        Span<byte> canonical = stackalloc byte[4];
        WriteCanonicalN64State(state, canonical);
        return $"N64 {canonical[0]:X2}{canonical[1]:X2}{canonical[2]:X2}{canonical[3]:X2}";
    }

    public static string BuildCommand(ConsoleControllerType type, uint buttons) =>
        BuildCommand(new BridgeControllerState(type, buttons));

    public static string BuildCommand(
        ConsoleControllerType type,
        uint buttons,
        sbyte analogX,
        sbyte analogY) =>
        BuildCommand(new BridgeControllerState(type, buttons, analogX, analogY,
            type == ConsoleControllerType.N64));

    internal static void WriteCanonicalN64State(
        BridgeControllerState state,
        Span<byte> destination)
    {
        if (state.ControllerType != ConsoleControllerType.N64)
            throw new ArgumentException("Canonical UDP state requires an N64 controller state.",
                nameof(state));
        if (destination.Length < 4)
            throw new ArgumentException("Four destination bytes are required.",
                nameof(destination));
        uint buttons = state.Buttons;
        sbyte stickX = state.AnalogX;
        sbyte stickY = state.AnalogY;
        if (!state.AnalogActive)
        {
            stickX = 0;
            stickY = 0;
            if (IsPressed(buttons, 16)) stickX = -80;
            else if (IsPressed(buttons, 17)) stickX = 80;
            if (IsPressed(buttons, 14)) stickY = 80;
            else if (IsPressed(buttons, 15)) stickY = -80;
        }
        byte byte0 = 0;
        byte byte1 = 0;
        SetByteBit(buttons, 0, 7, ref byte0);
        SetByteBit(buttons, 1, 6, ref byte0);
        SetByteBit(buttons, 2, 5, ref byte0);
        SetByteBit(buttons, 3, 4, ref byte0);
        SetByteBit(buttons, 4, 3, ref byte0);
        SetByteBit(buttons, 5, 2, ref byte0);
        SetByteBit(buttons, 6, 1, ref byte0);
        SetByteBit(buttons, 7, 0, ref byte0);
        SetByteBit(buttons, 10, 5, ref byte1);
        SetByteBit(buttons, 11, 4, ref byte1);
        SetByteBit(buttons, 12, 3, ref byte1);
        SetByteBit(buttons, 13, 2, ref byte1);
        SetByteBit(buttons, 18, 1, ref byte1);
        SetByteBit(buttons, 19, 0, ref byte1);
        destination[0] = byte0;
        destination[1] = byte1;
        destination[2] = unchecked((byte)stickX);
        destination[3] = unchecked((byte)stickY);
    }

    private static void SetByteBit(
        uint buttons,
        int logicalBit,
        int targetBit,
        ref byte value)
    {
        if (IsPressed(buttons, logicalBit)) value |= (byte)(1 << targetBit);
    }

    private static bool IsPressed(uint buttons, int bit) =>
        (buttons & (1u << bit)) != 0;
}
