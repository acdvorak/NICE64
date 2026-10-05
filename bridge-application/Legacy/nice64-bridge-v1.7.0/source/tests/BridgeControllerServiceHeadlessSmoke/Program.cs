using PicoSwitchController;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task TestMergedStateIntegrationBoundaryAsync()
{
    FakeControllerPort port = new()
        { Disposition = BridgeControllerPortDisposition.Delivered };
    BridgeControllerService service = new(port);

    BridgeControllerState manualOnly = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.Snes, 0x0001, 0, false, 0, 0);
    BridgeControllerState combined = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.Snes, 0x0001, 0x0002, false, 0, 0);
    BridgeControllerState physicalOnly = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.Snes, 0, 0x0004, false, 0, 0);
    Require(manualOnly.Buttons == 0x0001 && combined.Buttons == 0x0003 &&
        physicalOnly.Buttons == 0x0004,
        "Form input contributions did not retain manual/physical OR semantics");
    await service.SetStateAsync(manualOnly);
    Require(service.Current.LastRequestedState == manualOnly &&
        port.LastCommand == "MASK 0001",
        "Manual button-down state was not submitted as the corresponding SNES bit");
    await service.SetStateAsync(combined);
    Require(service.Current.LastRequestedState == combined &&
        port.LastCommand == "MASK 0003",
        "Manual and physical SNES masks were not submitted with OR semantics");
    await service.SetStateAsync(physicalOnly);
    Require(service.Current.LastRequestedState == physicalOnly &&
        port.LastCommand == "MASK 0004",
        "Physical-only SNES state did not reach the semantic service");
    BridgeControllerState snesReleased = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.Snes, 0, 0, false, 0, 0);
    await service.SetStateAsync(snesReleased);
    Require(service.Current.LastRequestedState == snesReleased &&
        port.LastCommand == "MASK 0000",
        "Manual button-up state did not clear the corresponding SNES bit");

    BridgeControllerState n64Analog = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.N64, 0x0001, 0x0002, true, 37, -46);
    Require(n64Analog.Buttons == 0x0003 && n64Analog.AnalogActive &&
        n64Analog.AnalogX == 37 && n64Analog.AnalogY == -46,
        "N64 merged buttons did not preserve active calibrated analog values");
    await service.SetStateAsync(n64Analog);
    Require(service.Current.LastRequestedState == n64Analog &&
        port.LastCommand == "N64 C00025D2",
        "N64 merged buttons and calibrated analog state were not submitted together");

    BridgeControllerState n64DigitalStick = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.N64, 0, 1u << 17, false, 0, 0);
    await service.SetStateAsync(n64DigitalStick);
    Require(port.LastCommand == "N64 00005000",
        "N64 digital stick direction lost legacy +80 behavior");

    BridgeControllerState neutral = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.N64, 0, 0, false, 0, 0);
    Require(neutral.Neutral && neutral.Buttons == 0 && !neutral.AnalogActive,
        "Zero raw contributions did not produce semantic neutral");
    await service.SetStateAsync(neutral);
    Require(service.Current.LastRequestedState == neutral &&
        port.LastCommand == "N64 00000000",
        "Neutral was not submitted when all merged inputs were zero");
}

static void TestN64StickOwnershipComposition()
{
    const uint stickUp = 1u << 14;
    const uint stickDown = 1u << 15;
    const uint stickLeft = 1u << 16;
    const uint stickRight = 1u << 17;
    const uint dpadUp = 1u << 0;
    const uint a = 1u << 4;
    const uint cUp = 1u << 12;

    BridgeControllerState analogRight = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.N64, 0, stickRight, true, 37, -11);
    Require(analogRight.Buttons == 0 && analogRight.AnalogActive &&
        BridgeControllerCommandBuilder.BuildCommand(analogRight) == "N64 000025F5",
        "Calibrated analog did not exclusively own the physical N64 stick");

    BridgeControllerState analogDiagonal = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.N64, 0, stickUp | stickRight, true, 44, 51);
    Require((analogDiagonal.Buttons & BridgeControllerStateComposer.N64PhysicalPseudoStickMask) == 0 &&
        BridgeControllerCommandBuilder.BuildCommand(analogDiagonal) == "N64 00002C33",
        "Physical diagonal pseudo-stick bits competed with calibrated analog output");

    BridgeControllerState allPhysicalStickDirections = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.N64, 0, stickUp | stickDown | stickLeft | stickRight, true, -9, 12);
    Require((allPhysicalStickDirections.Buttons &
        BridgeControllerStateComposer.N64PhysicalPseudoStickMask) == 0,
        "Not all physical N64 pseudo-stick directions were suppressed under analog ownership");

    BridgeControllerState analogNeutral = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.N64, 0, 0, true, 0, 0);
    Require(analogNeutral.AnalogX == 0 && analogNeutral.AnalogY == 0 &&
        BridgeControllerCommandBuilder.BuildCommand(analogNeutral) == "N64 00000000",
        "Calibrated values inside the existing deadzone did not remain neutral");

    BridgeControllerState digitalRight = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.N64, 0, stickRight, false, 0, 0);
    BridgeControllerState digitalLeft = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.N64, 0, stickLeft, false, 0, 0);
    Require(BridgeControllerCommandBuilder.BuildCommand(digitalRight) == "N64 00005000" &&
        BridgeControllerCommandBuilder.BuildCommand(digitalLeft) == "N64 0000B000",
        "Digital N64 stick fallback lost its established +/-80 behavior");

    BridgeControllerState manualStick = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.N64, stickLeft, 0, false, 0, 0);
    Require(manualStick.Buttons == stickLeft &&
        BridgeControllerCommandBuilder.BuildCommand(manualStick) == "N64 0000B000",
        "Manual/on-screen digital stick contribution was suppressed");

    BridgeControllerState mixed = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.N64,
        stickLeft,
        stickRight | dpadUp | a | cUp,
        true,
        25,
        -30);
    Require((mixed.Buttons & stickLeft) != 0 && (mixed.Buttons & stickRight) == 0 &&
        (mixed.Buttons & dpadUp) != 0 && (mixed.Buttons & a) != 0 &&
        (mixed.Buttons & cUp) != 0 && mixed.AnalogX == 25 && mixed.AnalogY == -30,
        "Source-aware stick arbitration changed manual stick, D-pad, C-button, or button state");
    Require(BridgeControllerCommandBuilder.BuildCommand(mixed) == "N64 880819E2",
        "Mixed buttons and calibrated analog did not retain canonical parity");

    BridgeControllerState snes = BridgeControllerStateComposer.Merge(
        ConsoleControllerType.Snes, 0x0001, 0x0002, true, 40, -40);
    Require(snes.Buttons == 0x0003 && !snes.AnalogActive &&
        BridgeControllerCommandBuilder.BuildCommand(snes) == "MASK 0003",
        "N64 stick ownership arbitration changed SNES composition");
}

static async Task TestCommandParityAsync()
{
    FakeControllerPort port = new() { Disposition = BridgeControllerPortDisposition.Delivered };
    BridgeControllerService service = new(port);

    BridgeControllerState snes = new(ConsoleControllerType.Snes, 0x0801);
    BridgeControllerResult snesResult = await service.SetStateAsync(snes);
    Require(snesResult.Success && port.LastCommand == "MASK 0801",
        "SNES digital grammar changed");

    BridgeControllerResult snesNeutral = await service.SetNeutralAsync();
    Require(snesNeutral.Success && port.LastCommand == "MASK 0000" &&
        service.Current.LastDeliveredState?.Neutral == true,
        "SNES semantic neutral did not map to MASK 0000");

    BridgeControllerState n64Digital = new(
        ConsoleControllerType.N64, (1u << 0) | (1u << 17));
    await service.SetStateAsync(n64Digital);
    Require(port.LastCommand == "N64 80005000",
        "N64 digital-only grammar changed");

    BridgeControllerState n64Analog = new(
        ConsoleControllerType.N64, 0, 31, -22, true);
    await service.SetStateAsync(n64Analog);
    Require(port.LastCommand == "N64 00001FEA" &&
        service.Current.LastDeliveredState == n64Analog,
        "N64 analog values were not preserved exactly");

    BridgeControllerState n64AnalogNeutral =
        BridgeControllerState.CreateNeutral(ConsoleControllerType.N64);
    await service.SetStateAsync(n64AnalogNeutral);
    Require(port.LastCommand == "N64 00000000" &&
        service.Current.LastDeliveredState?.AnalogActive == true,
        "N64 neutral analog state changed");

    await service.SetStateAsync(new BridgeControllerState(ConsoleControllerType.Snes,
        1, 63, -64, true));
    Require(port.LastCommand == "MASK 0001" &&
        service.Current.LastRequestedState is
            { ControllerType: ConsoleControllerType.Snes, AnalogActive: false,
              AnalogX: 0, AnalogY: 0 },
        "SNES state retained inapplicable analog data");
}

static async Task TestLatestStateAndDuplicateSuppressionAsync()
{
    FakeControllerPort port = new();
    port.NonControllerCommands.Add("STAT");
    BridgeControllerService service = new(port);
    BridgeControllerState first = new(ConsoleControllerType.Snes, 1);
    BridgeControllerState newest = new(ConsoleControllerType.Snes, 2);

    BridgeControllerResult firstResult = await service.SetStateAsync(first);
    BridgeControllerResult duplicate = await service.SetStateAsync(first);
    BridgeControllerResult newestResult = await service.SetStateAsync(newest);

    Require(firstResult.Disposition == BridgeControllerDisposition.AcceptedPending &&
        duplicate.Disposition == BridgeControllerDisposition.AcceptedPending &&
        newestResult.Disposition == BridgeControllerDisposition.AcceptedPending &&
        port.SubmitCount == 2,
        "Repeated identical state created duplicate delivery");
    Require(port.Pending?.State == newest && service.Current.LastRequestedState == newest &&
        service.Current.DeliveryPending,
        "Newer pending state did not supersede the older state");
    Require(port.NonControllerCommands.SequenceEqual(["STAT"]),
        "Controller latest-state submission affected a non-controller command");

    service.ClearPendingState();
    Require(port.Pending is null && !service.Current.DeliveryPending &&
        service.Current.LastRequestedState == newest,
        "Clearing pending delivery changed requested semantic truth");
}

static void TestSharedCommandQueueCoalescing()
{
    BridgeControllerCommandQueue queue = new();
    BridgeControllerState older = new(ConsoleControllerType.Snes, 1);
    BridgeControllerState newer = new(ConsoleControllerType.Snes, 2);
    queue.EnqueueLatestController(new BridgeControllerCommand(1, older, "MASK 0001"));
    queue.EnqueueNonController("STAT");
    int superseded = queue.EnqueueLatestController(
        new BridgeControllerCommand(2, newer, "MASK 0002"));

    Require(superseded == 1 && queue.TryDequeue(out BridgeQueuedCommand? first) &&
        first is { WireCommand: "STAT", ControllerState: null } &&
        queue.TryDequeue(out BridgeQueuedCommand? second) &&
        second?.ControllerState?.State == newer && !queue.TryDequeue(out _),
        "Latest-state coalescing dropped/reordered non-controller work or retained stale state");

    queue.EnqueueNonController("FORCE OFF");
    queue.EnqueueLatestController(new BridgeControllerCommand(3, older, "MASK 0001"));
    Require(queue.ClearPendingControllerState() == 1 &&
        queue.TryDequeue(out BridgeQueuedCommand? retained) &&
        retained is { WireCommand: "FORCE OFF", ControllerState: null },
        "Clearing controller state removed a non-controller command");
}

static async Task TestCancellationAndAvailabilityAsync()
{
    FakeControllerPort port = new();
    BridgeControllerService service = new(port);
    BridgeControllerSnapshot before = service.Current;
    using CancellationTokenSource cancelled = new();
    cancelled.Cancel();
    BridgeControllerResult cancelledResult = await service.SetStateAsync(
        new BridgeControllerState(ConsoleControllerType.Snes, 4), cancelled.Token);
    Require(cancelledResult.Failure == BridgeControllerFailure.Cancelled &&
        port.SubmitCount == 0 && service.Current == before,
        "Pre-cancelled submission changed state or reached delivery");

    port.Availability = BridgeControllerPortAvailability.NotConnected;
    BridgeControllerState disconnectedState = new(ConsoleControllerType.Snes, 8);
    BridgeControllerResult disconnected = await service.SetStateAsync(disconnectedState);
    Require(disconnected.Failure == BridgeControllerFailure.NotConnected &&
        service.Current.LastRequestedState == disconnectedState &&
        service.Current.LastDeliveredState is null && service.Current.DeliveryPending,
        "Disconnected request was not represented truthfully");

    port.Availability = BridgeControllerPortAvailability.FirmwareUnavailable;
    BridgeControllerResult firmware = await service.SetStateAsync(
        new BridgeControllerState(ConsoleControllerType.N64, 1));
    Require(firmware.Failure == BridgeControllerFailure.FirmwareUnavailable,
        "Unavailable controller firmware was not typed");

    port.Availability = BridgeControllerPortAvailability.Busy;
    BridgeControllerResult busy = await service.SetStateAsync(
        new BridgeControllerState(ConsoleControllerType.N64, 2));
    Require(busy.Failure == BridgeControllerFailure.Busy,
        "Protected-operation busy state was not typed");

    BridgeControllerResult unsupported = await service.SetStateAsync(
        new BridgeControllerState((ConsoleControllerType)99, 0));
    Require(unsupported.Failure == BridgeControllerFailure.UnsupportedController,
        "Unsupported controller type was not rejected semantically");
}

static async Task TestSuspensionAndResumeAsync()
{
    FakeControllerPort port = new();
    BridgeControllerService service = new(port);
    service.SuspendDelivery();
    BridgeControllerState latest = new(ConsoleControllerType.N64, 1, -80, 80, true);
    BridgeControllerResult suspended = await service.SetStateAsync(latest);
    Require(suspended.Failure == BridgeControllerFailure.DeliverySuspended &&
        suspended.Snapshot.DeliverySuspended && suspended.Snapshot.DeliveryPending &&
        suspended.Snapshot.LastRequestedState == latest && port.SubmitCount == 0,
        "Suspended delivery lost pending/requested semantic truth");

    BridgeControllerResult resumed = await service.ResumeDeliveryAsync(resendCurrent: true);
    Require(resumed.Disposition == BridgeControllerDisposition.AcceptedPending &&
        !service.Current.DeliverySuspended && port.SubmitCount == 1 &&
        port.Pending?.State == latest,
        "Resume did not resend the latest semantic state exactly once");

    FakeControllerPort protectedPort = new();
    BridgeControllerService protectedService = new(protectedPort);
    protectedService.SuspendDelivery();
    BridgeControllerResult protectedResult = await protectedService.SetStateAsync(latest);
    Require(protectedResult.Failure == BridgeControllerFailure.DeliverySuspended &&
        protectedPort.SubmitCount == 0,
        "Explicit Pak content suspension allowed gameplay delivery");
    await protectedService.ResumeDeliveryAsync(resendCurrent: false);
    BridgeControllerState nextGameplay = latest with { Buttons = 2 };
    BridgeControllerResult afterProtected =
        await protectedService.SetStateAsync(nextGameplay);
    Require(afterProtected.Disposition == BridgeControllerDisposition.AcceptedPending &&
        !protectedService.Current.DeliverySuspended && protectedPort.SubmitCount == 1 &&
        protectedPort.Pending?.State == nextGameplay,
        "Normal gameplay did not resume through the latest-state submission boundary " +
        "after a protected Pak operation");
}

static async Task TestDeliveryTruthAndDisconnectNeutralAsync()
{
    FakeControllerPort port = new() { Disposition = BridgeControllerPortDisposition.Delivered };
    BridgeControllerService service = new(port);
    BridgeControllerState deliveredState = new(ConsoleControllerType.N64, 1, 12, -13, true);
    await service.SetStateAsync(deliveredState);

    port.Disposition = BridgeControllerPortDisposition.Failed;
    port.Error = "write failed";
    BridgeControllerState failedState = new(ConsoleControllerType.N64, 2, 33, -44, true);
    BridgeControllerResult failed = await service.SetStateAsync(failedState);
    Require(failed.Failure == BridgeControllerFailure.TransportUnavailable &&
        failed.Snapshot.LastRequestedState == failedState &&
        failed.Snapshot.LastDeliveredState == deliveredState &&
        failed.Snapshot.DeliveryPending,
        "Transport failure falsely advanced delivered semantic state");

    port.Disposition = BridgeControllerPortDisposition.Delivered;
    BridgeControllerResult neutral = await service.NeutralizeForDisconnectAsync(
        ConsoleControllerType.N64);
    Require(neutral.Success && neutral.Disposition == BridgeControllerDisposition.Delivered &&
        port.ImmediateCount == 1 && port.LastCommand == "N64 00000000" &&
        service.Current.LastRequestedState.Neutral &&
        service.Current.LastDeliveredState?.Neutral == true &&
        !service.Current.DeliveryPending,
        "Disconnect cleanup seam did not deliver semantic neutral truthfully");

    BridgeControllerState deliveredAgain = new(ConsoleControllerType.N64, 4);
    await service.SetStateAsync(deliveredAgain);
    port.Disposition = BridgeControllerPortDisposition.Failed;
    BridgeControllerResult failedNeutral = await service.NeutralizeForDisconnectAsync(
        ConsoleControllerType.N64);
    Require(failedNeutral.Failure == BridgeControllerFailure.TransportUnavailable &&
        failedNeutral.Snapshot.LastRequestedState.Neutral &&
        failedNeutral.Snapshot.LastDeliveredState == deliveredAgain &&
        !failedNeutral.Snapshot.DeliveryPending,
        "Failed disconnect neutralization falsely changed delivered state or retained pending work");
}

static void TestLatestStateSequenceAndPacketContract()
{
    BridgeLatestStateAcceptancePolicy policy = new();
    const uint session = 0x10203040;
    Require(policy.TryAccept(session, 100) && policy.TryAccept(session, 101) &&
            policy.TryAccept(session, 103) && !policy.TryAccept(session, 102) &&
            policy.TryAccept(session, 104),
        "LATEST_STATE did not implement 100/101/103/102-stale/104 newest-only semantics");

    BridgeLatestStateAcceptancePolicy ownership = new();
    const uint sessionA = 0xaaaaaaaa;
    const uint sessionB = 0xbbbbbbbb;
    Require(ownership.TryAccept(sessionA, 100) &&
            ownership.TryAccept(sessionA, 101) &&
            ownership.TryAccept(sessionB, 1) &&
            !ownership.TryAccept(sessionA, 102) &&
            ownership.TryAccept(sessionB, 2),
        "retired session regained ownership after the replacement session began");

    BridgeLatestStateAcceptancePolicy wrap = new();
    Require(wrap.TryAccept(session, 0xfffffffe) &&
            wrap.TryAccept(session, 0xffffffff) && wrap.TryAccept(session, 0) &&
            wrap.TryAccept(session, 1) && !wrap.TryAccept(session, 0xfffffffd),
        "LATEST_STATE sequence comparison was not wrap-safe");

    BridgeControllerState state = new(ConsoleControllerType.N64,
        (1u << 0) | (1u << 10), 23, -45, true);
    byte[] wire = new byte[BridgeLatestStateUdpPacket.PacketBytes];
    BridgeLatestStateUdpPacket.Write(wire, session, 104, state);
    Require(Nice64RudpPacketCodec.TryDecode(wire,
            BridgeLatestStateUdpPacket.ControllerStateBytes, session,
            out Nice64RudpPacket? packet, out Nice64RudpValidationError error) &&
            error == Nice64RudpValidationError.None && packet is not null &&
            packet.Header.Flags == Nice64RudpPacketFlags.Data &&
            packet.Header.DeliveryClass ==
                (byte)Nice64WirelessDeliveryClass.LatestState &&
            packet.Header.AckBase == 0 && packet.Header.AckBitmap == 0 &&
            packet.Payload.SequenceEqual(new byte[] { 0x80, 0x20, 23, 211 }),
        "LATEST_STATE packet did not carry one complete canonical N64 state without ACK fields");
    Require(BridgeLatestStateAcceptancePolicy.RetiredSessionCapacity == 4,
        "retired-session ownership cache is not fixed and bounded");
    Console.WriteLine("PASS LATEST_STATE newest-only, retired-owner rejection, wrap, and 32-byte wire contract");
}

static void TestLatestStateAccessoryTransportSelection()
{
    static BridgeDeviceStateSnapshot Device(BridgeAccessoryMode accessory)
    {
        BridgeCapabilitySet reported =
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.DeviceReported);
        BridgeCapabilitySet implemented =
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.BridgeImplemented);
        BridgeCapabilitySet negotiated =
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.ApplicationNegotiated);
        BridgeCapabilitySet effective =
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.Effective);
        return new("NICE64", "1.1.0", BridgeDeviceRole.Controller,
            BridgeDeviceService.None, FirmwarePersonality.Controller,
            BridgeControlConnectionKind.WifiTcp, BridgeConnectionState.Connected,
            BridgeNetworkPakTransport.Tcp, BridgeNetworkPakTransport.None,
            reported, implemented, negotiated, effective, 1, DateTimeOffset.UtcNow)
        {
            ActiveAccessory = accessory
        };
    }

    BridgeControllerState state = new(ConsoleControllerType.N64, 1, 12, -13, true);
    BridgeAccessoryMode[] gameplayAccessories =
    [
        BridgeAccessoryMode.None,
        BridgeAccessoryMode.ControllerPak,
        BridgeAccessoryMode.RumblePak,
        BridgeAccessoryMode.TransferPak
    ];
    foreach (BridgeAccessoryMode accessory in gameplayAccessories)
    {
        Require(BridgeLatestStateControllerPathPolicy.Select(
                state, Device(accessory), tcpSessionConnected: true, "192.168.2.81").Path ==
            BridgeControllerTransportPath.LatestStateUdp,
            $"{accessory} incorrectly forced normal N64 gameplay onto the line queue");
    }

    BridgeControllerTransportSelection controllerPakUdp =
        BridgeLatestStateControllerPathPolicy.Select(
            state, Device(BridgeAccessoryMode.ControllerPak), true, "192.168.2.81");
    BridgeControllerTransportSelection transferPakUdp =
        BridgeLatestStateControllerPathPolicy.Select(
            state, Device(BridgeAccessoryMode.TransferPak), true, "192.168.2.81");
    BridgeControllerDeliveryGuard lineOnly =
        BridgeControllerDeliveryGuardPolicy.Select(
            allDeliveryGuardActive: false, protectedContentActive: true);
    Require(lineOnly == BridgeControllerDeliveryGuard.LineOnly &&
        BridgeControllerDeliveryGuardPolicy.Allows(lineOnly, controllerPakUdp) &&
        BridgeControllerDeliveryGuardPolicy.Allows(lineOnly, transferPakUdp),
        "Protected Pak content blocked independent eligible LATEST_STATE gameplay");

    BridgeControllerTransportSelection disconnectedTcp =
        BridgeLatestStateControllerPathPolicy.Select(
            state, Device(BridgeAccessoryMode.ControllerPak),
            tcpSessionConnected: false, "192.168.2.81");
    Require(disconnectedTcp is
        { Path: BridgeControllerTransportPath.LineFallback,
          LineFallbackReason: "tcp-session-not-connected" } &&
        !BridgeControllerDeliveryGuardPolicy.Allows(lineOnly, disconnectedTcp) &&
        BridgeLatestStateControllerPathPolicy.Select(
            new BridgeControllerState(ConsoleControllerType.Snes, 1),
            Device(BridgeAccessoryMode.TransferPak), true, "192.168.2.81") is
        { Path: BridgeControllerTransportPath.LineFallback,
          LineFallbackReason: "controller-profile-not-n64" },
        "LATEST_STATE ignored its TCP-session or N64-profile prerequisites");
    BridgeControllerDeliveryGuard all =
        BridgeControllerDeliveryGuardPolicy.Select(
            allDeliveryGuardActive: true, protectedContentActive: false);
    Require(all == BridgeControllerDeliveryGuard.All &&
        !BridgeControllerDeliveryGuardPolicy.Allows(all, controllerPakUdp) &&
        !BridgeControllerDeliveryGuardPolicy.Allows(all, transferPakUdp),
        "Firmware/accessory transition all-delivery blocking was weakened");
    Console.WriteLine(
        "PASS None/Controller Pak/Rumble Pak/Transfer Pak gameplay selects LATEST_STATE UDP");
}

static async Task TestLatestStateCurrentReportStreamAsync()
{
    using UdpClient receiver = new(AddressFamily.InterNetwork);
    receiver.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
    ConcurrentQueue<BridgeControllerCommand> delivered = new();
    ConcurrentQueue<Exception> failures = new();
    ConcurrentQueue<(uint SessionId, uint Sequence, string State)> reports = new();
    await using BridgeLatestStateUdpControllerSender sender = new(
        IPAddress.Loopback.ToString(), port, delivered.Enqueue,
        (_, error) => failures.Enqueue(error),
        (sessionId, sequence, command) =>
            reports.Enqueue((sessionId, sequence, command.WireCommand)));

    BridgeControllerState heldA = new(ConsoleControllerType.N64, 1u << 4);
    BridgeControllerCommand heldACommand = new(1, heldA,
        BridgeControllerCommandBuilder.BuildCommand(heldA));
    sender.SubmitLatest(heldACommand);

    List<(Nice64RudpPacket Packet, long Ticks)> heldReports = [];
    for (int index = 0; index < 4; index++)
    {
        using CancellationTokenSource wait = new(TimeSpan.FromMilliseconds(250));
        UdpReceiveResult received = await receiver.ReceiveAsync(wait.Token);
        Require(Nice64RudpPacketCodec.TryDecode(received.Buffer, 4, sender.SessionId,
            out Nice64RudpPacket? packet, out _), "sender emitted an invalid packet");
        heldReports.Add((packet!, System.Diagnostics.Stopwatch.GetTimestamp()));
    }
    Require(heldReports.All(item => item.Packet.Payload.SequenceEqual(
                new byte[] { 0x08, 0x00, 0x00, 0x00 })) &&
            heldReports.Zip(heldReports.Skip(1), (left, right) =>
                Nice64RudpSequence.IsAfter(right.Packet.Header.Sequence,
                    left.Packet.Header.Sequence)).All(value => value),
        "One held current state was not periodically reported with fresh sequences");
    double[] heldSpacingMs = heldReports.Zip(heldReports.Skip(1), (left, right) =>
        (right.Ticks - left.Ticks) * 1000.0 /
            System.Diagnostics.Stopwatch.Frequency).ToArray();
    Require(heldSpacingMs.All(spacing => spacing >= 5 && spacing < 80),
        "Current-state reporting violated its bounded 120 Hz cadence");

    // Ignoring one disposable report requires no resubmission: the next report
    // independently carries the same current truth.
    Require(heldReports[2].Packet.Payload.SequenceEqual(heldReports[3].Packet.Payload),
        "A dropped current-state report would not be repaired by the next report");

    BridgeControllerState right = new(ConsoleControllerType.N64, 1u << 17);
    sender.SubmitLatest(new BridgeControllerCommand(2, right,
        BridgeControllerCommandBuilder.BuildCommand(right)));
    using (CancellationTokenSource wait = new(TimeSpan.FromMilliseconds(250)))
    {
        UdpReceiveResult received = await receiver.ReceiveAsync(wait.Token);
        Require(received.Buffer[^2] == 80 && received.Buffer[^1] == 0,
            "New RIGHT state did not replace the prior current state");
    }

    BridgeControllerState neutral = BridgeControllerState.CreateNeutral(
        ConsoleControllerType.N64);
    sender.SubmitLatest(new BridgeControllerCommand(3, neutral,
        BridgeControllerCommandBuilder.BuildCommand(neutral)));
    List<UdpReceiveResult> neutralReports = [];
    for (int index = 0; index < 3; index++)
    {
        using CancellationTokenSource wait = new(TimeSpan.FromMilliseconds(250));
        neutralReports.Add(await receiver.ReceiveAsync(wait.Token));
    }
    Require(neutralReports.All(item => item.Buffer[^4..].SequenceEqual(
                new byte[] { 0, 0, 0, 0 })),
        "Neutral did not immediately replace RIGHT or was followed by stale history");

    Nice64RudpPacket contract = heldReports[0].Packet;
    Require(contract.Header.Flags == Nice64RudpPacketFlags.Data &&
            contract.Header.DeliveryClass == (byte)Nice64WirelessDeliveryClass.LatestState &&
            contract.Header.AckBase == 0 && contract.Header.AckBitmap == 0 &&
            BridgeLatestStateUdpPacket.PacketBytes == 32 && contract.Payload.Length == 4,
        "Periodic sender changed the 32-byte disposable LATEST_STATE packet contract");

    BridgeLatestStateUdpSenderSnapshot snapshot = sender.Snapshot();
    Require(snapshot.CurrentStatePresent && snapshot.PendingHistoricalStates == 0 &&
            snapshot.ReportsSent >= 8 && snapshot.CurrentStateReplacements == 2 &&
            snapshot.SendFailures == 0 && failures.IsEmpty &&
            delivered.Count == snapshot.ReportsSent &&
            reports.Count == snapshot.ReportsSent &&
            reports.All(report => report.SessionId == sender.SessionId &&
                report.State.StartsWith("N64 ",
                    StringComparison.Ordinal)),
        $"LATEST_STATE sender counters were untruthful: {snapshot}; " +
        $"delivered={delivered.Count} reports={reports.Count} failures={failures.Count}");

    Require(sender.ClearPending() == 1,
        "Clearing the sender did not remove its one authoritative current state");
    await Task.Delay(30);
    while (receiver.Client.Available > 0)
        await receiver.ReceiveAsync();
    using CancellationTokenSource silence = new(TimeSpan.FromMilliseconds(20));
    try
    {
        await receiver.ReceiveAsync(silence.Token);
        throw new InvalidOperationException(
            "LATEST_STATE sender continued after its current state was cleared");
    }
    catch (OperationCanceledException) when (silence.IsCancellationRequested)
    {
    }
    await using BridgeLatestStateUdpControllerSender newLifetime = new(
        IPAddress.Loopback.ToString(), port, _ => { }, (_, _) => { });
    Require(newLifetime.SessionId != 0 && newLifetime.SessionId != sender.SessionId,
        "A new sender lifetime did not receive an independent nonzero session ID");
    Console.WriteLine($"PASS one-current-state 120Hz periodic reports, replacement, " +
        $"neutral, no history spacingMs={heldSpacingMs.Min():F1}/" +
        $"{heldSpacingMs.Average():F1}/{heldSpacingMs.Max():F1}");
}

static void TestLatestStateFailureDiagnosticCoalescing()
{
    TimeSpan summaryInterval = TimeSpan.FromSeconds(5);
    BridgeLatestStateUdpFailureDiagnosticPolicy policy = new(summaryInterval);
    InvalidOperationException failure = new("simulated UDP send failure");
    long start = System.Diagnostics.Stopwatch.GetTimestamp();
    List<BridgeLatestStateUdpDiagnostic> diagnostics = [];

    BridgeLatestStateUdpDiagnostic? first = policy.RecordFailure(failure, start);
    Require(first is
        { Kind: BridgeLatestStateUdpDiagnosticKind.FailureStarted,
          TotalFailures: 1,
          SuppressedFailures: 0 },
        "First LATEST_STATE UDP send failure was not surfaced immediately");
    diagnostics.Add(first.GetValueOrDefault());

    for (int index = 0; index < 599; index++)
    {
        BridgeLatestStateUdpDiagnostic? repeated = policy.RecordFailure(failure,
            start + System.Diagnostics.Stopwatch.Frequency);
        if (repeated is not null) diagnostics.Add(repeated.Value);
    }
    Require(diagnostics.Count == 1,
        "Equivalent send failures emitted one diagnostic per 120 Hz report");

    BridgeLatestStateUdpDiagnostic? summary = policy.RecordFailure(failure,
        start + 5 * System.Diagnostics.Stopwatch.Frequency);
    Require(summary is
        { Kind: BridgeLatestStateUdpDiagnosticKind.FailureSummary,
          TotalFailures: 601,
          SuppressedFailures: 600 } &&
        BridgeLatestStateUdpDiagnosticFormatter.Format(summary.Value).Contains(
            "600 suppressed", StringComparison.Ordinal),
        "Bounded failure summary did not expose the suppressed-failure count");
    diagnostics.Add(summary.GetValueOrDefault());

    BridgeLatestStateUdpDiagnostic? suppressedAfterSummary =
        policy.RecordFailure(failure,
            start + 6 * System.Diagnostics.Stopwatch.Frequency);
    Require(suppressedAfterSummary is null,
        "Failure coalescing did not start a new bounded summary interval");

    BridgeLatestStateUdpDiagnostic? recovered = policy.RecordSuccess(
        start + 7 * System.Diagnostics.Stopwatch.Frequency);
    Require(recovered is
        { Kind: BridgeLatestStateUdpDiagnosticKind.Recovered,
          TotalFailures: 602,
          SuppressedFailures: 1 } &&
        BridgeLatestStateUdpDiagnosticFormatter.Format(recovered.Value).Contains(
            "recovered after 602 failures", StringComparison.Ordinal),
        "Successful sending did not emit one truthful recovery diagnostic");
    Require(policy.RecordSuccess(
            start + 8 * System.Diagnostics.Stopwatch.Frequency) is null,
        "Healthy sending emitted a diagnostic after recovery state was reset");

    Console.WriteLine(
        "PASS LATEST_STATE failure diagnostics immediate/coalesced/summary/recovery");
}

await TestMergedStateIntegrationBoundaryAsync();
TestN64StickOwnershipComposition();
await TestCommandParityAsync();
await TestLatestStateAndDuplicateSuppressionAsync();
TestSharedCommandQueueCoalescing();
await TestCancellationAndAvailabilityAsync();
await TestSuspensionAndResumeAsync();
await TestDeliveryTruthAndDisconnectNeutralAsync();
TestLatestStateSequenceAndPacketContract();
TestLatestStateAccessoryTransportSelection();
await TestLatestStateCurrentReportStreamAsync();
TestLatestStateFailureDiagnosticCoalescing();
Console.WriteLine("PASS Bridge controller service headless semantics");

sealed class FakeControllerPort : IBridgeControllerCommandPort
{
    public BridgeControllerPortAvailability Availability { get; set; } =
        BridgeControllerPortAvailability.Available;
    public BridgeControllerPortDisposition Disposition { get; set; } =
        BridgeControllerPortDisposition.AcceptedPending;
    public string Error { get; set; } = string.Empty;
    public int SubmitCount { get; private set; }
    public int ImmediateCount { get; private set; }
    public BridgeControllerCommand? Pending { get; private set; }
    public string LastCommand { get; private set; } = string.Empty;
    public List<string> NonControllerCommands { get; } = [];

    public ValueTask<BridgeControllerPortResult> SubmitLatestAsync(
        BridgeControllerCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SubmitCount++;
        LastCommand = command.WireCommand;
        Pending = Disposition == BridgeControllerPortDisposition.AcceptedPending
            ? command
            : null;
        return ValueTask.FromResult(new BridgeControllerPortResult(Disposition, Error));
    }

    public ValueTask<BridgeControllerPortResult> DeliverImmediatelyAsync(
        BridgeControllerCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImmediateCount++;
        LastCommand = command.WireCommand;
        Pending = null;
        return ValueTask.FromResult(new BridgeControllerPortResult(Disposition, Error));
    }

    public void ClearPendingControllerState() => Pending = null;
}
