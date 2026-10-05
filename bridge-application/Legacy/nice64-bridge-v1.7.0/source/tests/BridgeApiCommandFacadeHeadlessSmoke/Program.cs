using PicoSwitchController;
using System.Reflection;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task<BridgeOperationSnapshot> WaitForTerminalAsync(
    BridgeOperationCoordinator operations, long operationId)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline)
    {
        BridgeOperationSnapshot current = operations.Current;
        if (current.OperationId == operationId &&
            current.Status != BridgeOperationStatus.Running)
            return current;
        await Task.Delay(5);
    }
    throw new TimeoutException($"Operation {operationId} did not reach terminal state.");
}

static async Task WaitUntilAsync(Func<bool> condition)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(5);
    while (!condition() && DateTime.UtcNow < deadline)
        await Task.Delay(5);
    if (!condition()) throw new TimeoutException("Expected condition was not reached.");
}

BridgeOperationCoordinator operations = new();
FakeFirmwareService firmware = new();
FakeAccessoryService accessory = new();
FakeModuleService modules = new();
FakeCarrierService carrier = new();
FakeConnectionService connection = new();
BridgeApiCommandFacade facade = new(
    operations, firmware, accessory, modules, carrier, connection);

foreach (BridgeNetworkPakTransport invalidCarrier in new[]
{
    BridgeNetworkPakTransport.None,
    BridgeNetworkPakTransport.UsbSerial,
    (BridgeNetworkPakTransport)999
})
{
    BridgeApiCommandResult invalid = facade.SelectNetworkPakCarrier(invalidCarrier);
    Require(invalid.Disposition == BridgeApiCommandDisposition.Rejected &&
        invalid.OperationId is null && invalid.FailureCode == "invalid-carrier" &&
        operations.Current == BridgeOperationSnapshot.Idle && carrier.CallCount == 0,
        $"Invalid remote carrier {invalidCarrier} was admitted or invoked F13");
}

BridgeApiCommandResult invalidFirmware = facade.SelectFirmware(FirmwarePersonality.Unknown);
Require(invalidFirmware.Disposition == BridgeApiCommandDisposition.Rejected &&
    invalidFirmware.OperationId is null && invalidFirmware.FailureCode == "invalid-firmware" &&
    operations.Current == BridgeOperationSnapshot.Idle && firmware.CallCount == 0,
    "Invalid firmware command was admitted or mutated its service");

TaskCompletionSource<FirmwareModeTransitionResult> firmwareGate =
    new(TaskCreationOptions.RunContinuationsAsynchronously);
firmware.Handler = (_, _) => firmwareGate.Task;
BridgeApiCommandResult acceptedFirmware = facade.SelectFirmware(FirmwarePersonality.Network);
Require(acceptedFirmware.Accepted && acceptedFirmware.OperationId == 1 &&
    acceptedFirmware.OperationKind == BridgeOperationKind.FirmwareModeSwitch &&
    acceptedFirmware.FailureCode is null && acceptedFirmware.SafeDetail is null &&
    operations.Current.OperationId == 1 &&
    operations.Current.Status == BridgeOperationStatus.Running,
    "Firmware command was not accepted with operation ID 1");

BridgeApiCommandResult busyAccessory =
    facade.SelectAccessory(BridgeAccessoryMode.ControllerPak);
Require(busyAccessory.Disposition == BridgeApiCommandDisposition.Busy &&
    busyAccessory.OperationId == 1 &&
    busyAccessory.OperationKind == BridgeOperationKind.FirmwareModeSwitch &&
    busyAccessory.FailureCode == "operation-busy" && accessory.CallCount == 0,
    "Concurrent command did not return the current F8 owner as Busy");

firmwareGate.SetResult(TestResults.FirmwareSuccess(FirmwarePersonality.Network));
BridgeOperationSnapshot firmwareTerminal =
    await WaitForTerminalAsync(operations, acceptedFirmware.OperationId!.Value);
Require(firmwareTerminal.Status == BridgeOperationStatus.Succeeded &&
    firmwareTerminal.DomainResult is FirmwareModeTransitionResult { Success: true },
    "Successful firmware command did not retain its typed terminal result internally");

modules.Handler = (id, _) => Task.FromResult(TestResults.ModuleSuccess(id!));
BridgeApiCommandResult acceptedModule = facade.SelectNetworkPakModule(" diagnostics ");
Require(acceptedModule.Accepted && acceptedModule.OperationId == 2 &&
    acceptedModule.OperationKind == BridgeOperationKind.NetworkPakModuleSelection,
    "Busy or invalid admission consumed an operation ID");
BridgeOperationSnapshot moduleTerminal =
    await WaitForTerminalAsync(operations, acceptedModule.OperationId!.Value);
Require(moduleTerminal.Status == BridgeOperationStatus.Succeeded &&
    modules.LastModuleId == "diagnostics" &&
    moduleTerminal.DomainResult is BridgeNetworkPakModuleSelectionResult { Success: true },
    "Module command did not preserve selection semantics or terminal state");

const string rawFailure =
    "C:\\private\\failure COM88 192.168.5.7 SecretServiceImplementationException";
firmware.Handler = (_, _) => Task.FromResult(TestResults.FirmwareFailure(rawFailure));
BridgeApiCommandResult failedFirmwareAdmission =
    facade.SelectFirmware(FirmwarePersonality.Diagnostics);
Require(failedFirmwareAdmission.Accepted && failedFirmwareAdmission.OperationId == 3 &&
    !failedFirmwareAdmission.ToString().Contains(rawFailure, StringComparison.Ordinal),
    "Firmware failure detail leaked through command admission");
BridgeOperationSnapshot failedFirmware =
    await WaitForTerminalAsync(operations, failedFirmwareAdmission.OperationId!.Value);
Require(failedFirmware.Status == BridgeOperationStatus.Failed &&
    failedFirmware.FailureCode == FirmwareModeTransitionFailure.Verification.ToString() &&
    failedFirmware.DomainResult is FirmwareModeTransitionResult { Success: false },
    "Firmware domain failure did not reach typed F8 terminal state");

int firmwareCallsBeforeAccessory = firmware.CallCount;
long nestedIdBefore = 0;
long nestedIdAfter = 0;
accessory.Handler = async (target, token) =>
{
    nestedIdBefore = operations.Current.OperationId;
    FirmwareModeTransitionResult child =
        await firmware.SelectModeAsync(FirmwarePersonality.Controller, token);
    nestedIdAfter = operations.Current.OperationId;
    return new(true, target, target, child, true, false, string.Empty)
    { Failure = BridgeAccessoryTransitionFailure.None };
};
firmware.Handler = (target, _) => Task.FromResult(TestResults.FirmwareSuccess(target));
BridgeApiCommandResult acceptedAccessory =
    facade.SelectAccessory(BridgeAccessoryMode.ControllerPak);
Require(acceptedAccessory.Accepted && acceptedAccessory.OperationId == 4,
    "Accessory command was not admitted as operation 4");
BridgeOperationSnapshot accessoryTerminal =
    await WaitForTerminalAsync(operations, acceptedAccessory.OperationId!.Value);
Require(accessoryTerminal.Status == BridgeOperationStatus.Succeeded &&
    nestedIdBefore == 4 && nestedIdAfter == 4 &&
    firmware.CallCount == firmwareCallsBeforeAccessory + 1 &&
    accessoryTerminal.DomainResult is BridgeAccessoryTransitionResult { Success: true },
    "Accessory firmware child acquired another F8 operation or lost parent ownership");

BridgeApiCommandResult invalidConnection = facade.Connect(
    new(BridgeControlConnectionKind.WifiTcp, "bridge.local", 0));
Require(invalidConnection.Disposition == BridgeApiCommandDisposition.Rejected &&
    invalidConnection.OperationId is null && connection.ConnectCallCount == 0,
    "Invalid connection was admitted or invoked the connection service");

connection.ConnectHandler = (request, _) =>
{
    connection.LastRequest = request;
    return Task.FromResult(new BridgeConnectionResult(true, request.ConnectionKind,
        request.Endpoint, "NICE64", FirmwarePersonality.Network,
        BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.DeviceReported), string.Empty));
};
BridgeApiCommandResult acceptedConnect = facade.Connect(
    new(BridgeControlConnectionKind.WifiTcp, " bridge.local ", 6464,
        TimeSpan.FromSeconds(5)));
Require(acceptedConnect.Accepted && acceptedConnect.OperationId == 5,
    "Valid explicit connection request was not admitted");
BridgeOperationSnapshot connectTerminal =
    await WaitForTerminalAsync(operations, acceptedConnect.OperationId!.Value);
Require(connectTerminal.Status == BridgeOperationStatus.Succeeded &&
    connection.LastRequest?.Endpoint == "bridge.local" &&
    connection.LastRequest.Port == 6464,
    "Connect command read implicit configuration or changed its explicit request");

connection.DisconnectHandler = (reason, _) =>
{
    connection.LastDisconnectReason = reason;
    return Task.FromResult(new BridgeDisconnectResult(true, reason,
        AccessoryPersistenceResult.Clean, true, true, string.Empty));
};
BridgeApiCommandResult acceptedDisconnect = facade.Disconnect();
Require(acceptedDisconnect.Accepted && acceptedDisconnect.OperationId == 6,
    "Disconnect command was not admitted");
BridgeOperationSnapshot disconnectTerminal =
    await WaitForTerminalAsync(operations, acceptedDisconnect.OperationId!.Value);
Require(disconnectTerminal.Status == BridgeOperationStatus.Succeeded &&
    connection.LastDisconnectReason == BridgeDisconnectReason.UserRequest &&
    connection.DisconnectCallCount == 1,
    "Disconnect command duplicated cleanup ownership or used shutdown semantics");

modules.Handler = (_, _) => throw new InvalidOperationException(rawFailure);
BridgeApiCommandResult exceptionAdmission = facade.SelectNetworkPakModule("diagnostics");
Require(exceptionAdmission.Accepted && exceptionAdmission.OperationId == 7 &&
    !exceptionAdmission.ToString().Contains(rawFailure, StringComparison.Ordinal),
    "Background exception leaked through command admission");
BridgeOperationSnapshot exceptionTerminal =
    await WaitForTerminalAsync(operations, exceptionAdmission.OperationId!.Value);
Require(exceptionTerminal.Status == BridgeOperationStatus.Failed &&
    exceptionTerminal.FailureCode == "UnexpectedFailure" &&
    exceptionTerminal.DomainResult is null,
    "Observed service exception left the operation running or fabricated a domain result");

modules.Handler = (id, _) => Task.FromResult(TestResults.ModuleSuccess(id!));
BridgeApiCommandResult afterException = facade.SelectNetworkPakModule("network-portal");
Require(afterException.Accepted && afterException.OperationId == 8,
    "Observed exception blocked the next monotonic operation");
await WaitForTerminalAsync(operations, afterException.OperationId!.Value);

TaskCompletionSource<BridgeNetworkPakCarrierResult> carrierGate =
    new(TaskCreationOptions.RunContinuationsAsynchronously);
carrier.Handler = (_, _) => carrierGate.Task;
BridgeApiCommandResult acceptedTcp =
    facade.SelectNetworkPakCarrier(BridgeNetworkPakTransport.Tcp);
await WaitUntilAsync(() => carrier.CallCount == 1);
Require(acceptedTcp.Accepted && acceptedTcp.OperationId == 9 &&
    acceptedTcp.OperationKind == BridgeOperationKind.NetworkPakCarrierSelection &&
    operations.Current.CancellationCapability ==
        BridgeOperationCancellationCapability.BeforeCommitOnly &&
    operations.Current.Domain == "NetworkPakCarrier" && carrier.CallCount == 1 &&
    carrier.LastTarget == BridgeNetworkPakTransport.Tcp,
    "TCP carrier did not acquire exactly one BeforeCommitOnly F8 operation");

BridgeApiCommandResult busyCarrier = facade.SelectNetworkPakCarrier(
    BridgeNetworkPakTransport.ReliableUdpQuick);
Require(busyCarrier.Disposition == BridgeApiCommandDisposition.Busy &&
    busyCarrier.OperationId == acceptedTcp.OperationId &&
    busyCarrier.OperationKind == BridgeOperationKind.NetworkPakCarrierSelection &&
    carrier.CallCount == 1,
    "Busy carrier admission did not preserve the single active F8 owner");

carrierGate.SetResult(TestResults.CarrierResult(
    success: true, selected: BridgeNetworkPakTransport.Tcp,
    active: BridgeNetworkPakTransport.None,
    outcome: BridgeNetworkPakCarrierOutcome.ActivationPendingHandshake));
BridgeOperationSnapshot tcpTerminal =
    await WaitForTerminalAsync(operations, acceptedTcp.OperationId!.Value);
Require(tcpTerminal.Status == BridgeOperationStatus.Succeeded &&
    tcpTerminal.DomainResult is BridgeNetworkPakCarrierResult
    {
        Success: true,
        Outcome: BridgeNetworkPakCarrierOutcome.ActivationPendingHandshake,
        Snapshot.SelectedCarrier: BridgeNetworkPakTransport.Tcp,
        Snapshot.ActiveCarrier: BridgeNetworkPakTransport.None
    },
    "TCP pending-handshake selection did not complete F8 successfully and truthfully");

carrier.Handler = (target, _) => Task.FromResult(TestResults.CarrierResult(
    success: true, selected: target, active: target,
    outcome: BridgeNetworkPakCarrierOutcome.Active));
BridgeApiCommandResult acceptedQuick = facade.SelectNetworkPakCarrier(
    BridgeNetworkPakTransport.ReliableUdpQuick);
BridgeOperationSnapshot quickTerminal =
    await WaitForTerminalAsync(operations, acceptedQuick.OperationId!.Value);
Require(acceptedQuick.OperationId == 10 &&
    quickTerminal.Status == BridgeOperationStatus.Succeeded &&
    quickTerminal.DomainResult is BridgeNetworkPakCarrierResult
    {
        Snapshot.SelectedCarrier: BridgeNetworkPakTransport.ReliableUdpQuick,
        Snapshot.ActiveCarrier: BridgeNetworkPakTransport.ReliableUdpQuick
    },
    "Reliable UDP Quick carrier did not complete through the typed F8 path");

carrier.Handler = (target, _) => Task.FromResult(TestResults.CarrierResult(
    success: false, selected: target, active: BridgeNetworkPakTransport.None,
    outcome: BridgeNetworkPakCarrierOutcome.Failed,
    failure: BridgeNetworkPakCarrierFailure.Cancelled));
BridgeApiCommandResult cancelledCarrier = facade.SelectNetworkPakCarrier(
    BridgeNetworkPakTransport.ReliableUdpQuick);
BridgeOperationSnapshot cancelledTerminal =
    await WaitForTerminalAsync(operations, cancelledCarrier.OperationId!.Value);
Require(cancelledCarrier.OperationId == 11 &&
    cancelledTerminal.Status == BridgeOperationStatus.Cancelled &&
    cancelledTerminal.FailureCode == BridgeNetworkPakCarrierFailure.Cancelled.ToString() &&
    cancelledTerminal.DomainResult is BridgeNetworkPakCarrierResult
    {
        Failure: BridgeNetworkPakCarrierFailure.Cancelled
    },
    "Carrier cancellation did not preserve typed F13 result and F8 cancellation");

MethodInfo[] commandMethods = typeof(IBridgeApiCommandFacade).GetMethods();
Require(commandMethods.All(method =>
        !method.Name.Contains("Wifi", StringComparison.OrdinalIgnoreCase) &&
        !method.Name.Contains("Transport", StringComparison.OrdinalIgnoreCase)) &&
    commandMethods.Count(method => method.Name == "SelectNetworkPakCarrier") == 1 &&
    typeof(IBridgeApiCommandFacade).Assembly.GetTypes()
        .Where(type => type.Name.StartsWith("BridgeApiCommand", StringComparison.Ordinal))
        .SelectMany(type => type.GetProperties())
        .All(property => property.Name is not ("DomainResult" or "Error") &&
            property.PropertyType != typeof(object)),
    "Facade exposes unsupported mutation, raw domain result, or raw error surface");

Console.WriteLine("PASS Bridge API command facade F8 ownership and admission semantics");

sealed class FakeFirmwareService : IBridgeFirmwareService
{
    public int CallCount { get; private set; }
    public Func<FirmwarePersonality, CancellationToken,
        Task<FirmwareModeTransitionResult>> Handler { get; set; } =
        (target, _) => Task.FromResult(TestResults.FirmwareSuccess(target));

    public Task<FirmwareModeTransitionResult> SelectModeAsync(
        FirmwarePersonality target, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Handler(target, cancellationToken);
    }
}

sealed class FakeAccessoryService : IBridgeAccessoryService
{
    public int CallCount { get; private set; }
    public Func<BridgeAccessoryMode, CancellationToken,
        Task<BridgeAccessoryTransitionResult>> Handler { get; set; } =
        (target, _) => Task.FromResult(new BridgeAccessoryTransitionResult(
            true, target, target, null, true, false, string.Empty));

    public Task<BridgeAccessoryTransitionResult> SelectAccessoryAsync(
        BridgeAccessoryMode accessory, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Handler(accessory, cancellationToken);
    }
}

sealed class FakeModuleService : IBridgeNetworkPakModuleService
{
    private readonly BridgeNetworkPakModuleSnapshot _snapshot = new(
        "network-portal", "Network Portal", [new("network-portal", "Network Portal")],
        BridgeNetworkPakModulePersistenceState.Confirmed,
        BridgeNetworkPakModuleSelectionOutcome.None, null);

    public string? LastModuleId { get; private set; }
    public Func<string?, CancellationToken,
        Task<BridgeNetworkPakModuleSelectionResult>> Handler { get; set; } =
        (id, _) => Task.FromResult(TestResults.ModuleSuccess(id ?? "network-portal"));
    public BridgeNetworkPakModuleSnapshot Current => _snapshot;
    public IReadOnlyList<BridgeNetworkPakModuleInfo> ListModules() =>
        _snapshot.AvailableModules;

    public Task<BridgeNetworkPakModuleSelectionResult> SelectModuleAsync(
        string? moduleId, CancellationToken cancellationToken = default)
    {
        LastModuleId = moduleId;
        return Handler(moduleId, cancellationToken);
    }
}

sealed class FakeConnectionService : IBridgeConnectionService
{
    public int ConnectCallCount { get; private set; }
    public int DisconnectCallCount { get; private set; }
    public BridgeConnectionRequest? LastRequest { get; set; }
    public BridgeDisconnectReason? LastDisconnectReason { get; set; }
    public Func<BridgeConnectionRequest, CancellationToken,
        Task<BridgeConnectionResult>> ConnectHandler { get; set; } =
        (request, _) => Task.FromResult(new BridgeConnectionResult(false,
            request.ConnectionKind, string.Empty, string.Empty,
            FirmwarePersonality.Unknown,
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.DeviceReported),
            "Not configured") { Failure = BridgeConnectionFailure.Unexpected });
    public Func<BridgeDisconnectReason, CancellationToken,
        Task<BridgeDisconnectResult>> DisconnectHandler { get; set; } =
        (reason, _) => Task.FromResult(new BridgeDisconnectResult(false, reason,
            AccessoryPersistenceResult.Clean, false, false, "Not configured")
        { Failure = BridgeDisconnectFailure.Unexpected });

    public Task<BridgeConnectionResult> ConnectAsync(
        BridgeConnectionRequest request, CancellationToken cancellationToken = default)
    {
        ConnectCallCount++;
        return ConnectHandler(request, cancellationToken);
    }

    public Task<BridgeDisconnectResult> DisconnectAsync(
        BridgeDisconnectReason reason, CancellationToken cancellationToken = default)
    {
        DisconnectCallCount++;
        return DisconnectHandler(reason, cancellationToken);
    }
}

sealed class FakeCarrierService : IBridgeNetworkPakCarrierService
{
    public int CallCount { get; private set; }
    public BridgeNetworkPakTransport LastTarget { get; private set; }
    public Func<BridgeNetworkPakTransport, CancellationToken,
        Task<BridgeNetworkPakCarrierResult>> Handler { get; set; } =
        (target, _) => Task.FromResult(TestResults.CarrierResult(
            true, target, target, BridgeNetworkPakCarrierOutcome.Active));

    public BridgeNetworkPakCarrierSnapshot Current { get; private set; } =
        TestResults.CarrierSnapshot(BridgeNetworkPakTransport.Tcp,
            BridgeNetworkPakTransport.None);
    public long? PendingLineActivationGeneration => null;
    public long? ActiveQuickGeneration => null;

    public async Task<BridgeNetworkPakCarrierResult> SelectCarrierAsync(
        BridgeNetworkPakTransport carrier,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastTarget = carrier;
        BridgeNetworkPakCarrierResult result = await Handler(carrier, cancellationToken);
        Current = result.Snapshot;
        return result;
    }

    public Task<BridgeNetworkPakCarrierResult> InitializeAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> ActivateSelectedAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> ReleaseActiveAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> ObserveLineHandshakeAcknowledgedAsync(
        BridgeNetworkPakTransport carrier, long generation,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> ReportLineActivationFailureAsync(
        BridgeNetworkPakTransport carrier, long generation,
        BridgeNetworkPakCarrierFailure failure, string? diagnostic = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> ReportQuickFaultAsync(
        long generation, string? diagnostic = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> RetirePhysicalCarrierAsync(
        string? reason = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> ConvergeDisconnectedAsync(
        string? reason = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<BridgeNetworkPakCarrierResult> ShutdownAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

static class TestResults
{
    public static FirmwareModeTransitionResult FirmwareSuccess(
        FirmwarePersonality target) =>
        new(true, new(target, target == FirmwarePersonality.Controller
                ? "CONTROLLER" : "STANDALONE", target.ToString().ToUpperInvariant()),
            string.Empty, true, true)
        { Failure = FirmwareModeTransitionFailure.None };

    public static FirmwareModeTransitionResult FirmwareFailure(string rawError) =>
        new(false, null, rawError, true, false)
        { Failure = FirmwareModeTransitionFailure.Verification };

    public static BridgeNetworkPakModuleSelectionResult ModuleSuccess(string moduleId)
    {
        BridgeNetworkPakModuleSnapshot snapshot = new(moduleId, "Selected Module",
            [new(moduleId, "Selected Module")],
            BridgeNetworkPakModulePersistenceState.Confirmed,
            BridgeNetworkPakModuleSelectionOutcome.RequestedModuleSelected, null);
        return new(true, moduleId, moduleId, "Selected Module", false, true,
            BridgeNetworkPakModuleSelectionFailure.None, null, snapshot);
    }

    public static BridgeNetworkPakCarrierSnapshot CarrierSnapshot(
        BridgeNetworkPakTransport selected, BridgeNetworkPakTransport active) =>
        new(selected, active, BridgeNetworkPakCarrierTransitionPhase.Idle,
            BridgeNetworkPakTransport.None,
            BridgeNetworkPakCarrierPersistenceState.Confirmed,
            BridgeNetworkPakCarrierOutcome.None,
            BridgeNetworkPakCarrierFailure.None, null);

    public static BridgeNetworkPakCarrierResult CarrierResult(
        bool success,
        BridgeNetworkPakTransport selected,
        BridgeNetworkPakTransport active,
        BridgeNetworkPakCarrierOutcome outcome,
        BridgeNetworkPakCarrierFailure failure = BridgeNetworkPakCarrierFailure.None,
        string? diagnostic = null) =>
        new(success, Committed: true, outcome, failure, diagnostic,
            CarrierSnapshot(selected, active) with
            {
                LastOutcome = outcome,
                Failure = failure,
                Diagnostic = diagnostic
            });
}
