using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

string root = Path.Combine(Path.GetTempPath(), $"nice64-b5-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    foreach (BridgeAccessoryMode accessory in Enum.GetValues<BridgeAccessoryMode>())
    {
        Require(BridgeUiPolicy.CanRequestAccessoryTransition(true, false, false),
            $"{accessory} was disabled while Network firmware was connected and idle");
    }
    Require(BridgeUiPolicy.CanRequestAccessoryTransition(true, false, false),
        "Network Pak was disabled while Controller firmware was connected and idle");
    Require(!BridgeUiPolicy.CanRequestAccessoryTransition(true, true, false) &&
            !BridgeUiPolicy.CanRequestAccessoryTransition(true, false, true) &&
            !BridgeUiPolicy.CanRequestAccessoryTransition(false, false, false),
        "accessory selections remained enabled during a transition, conflicting operation, or disconnect");
    Require(BridgeUiPolicy.RequiredFirmwareForAccessory(BridgeAccessoryMode.NetworkPak) ==
                FirmwarePersonality.Network &&
            Enum.GetValues<BridgeAccessoryMode>()
                .Where(mode => mode != BridgeAccessoryMode.NetworkPak)
                .All(mode => BridgeUiPolicy.RequiredFirmwareForAccessory(mode) ==
                    FirmwarePersonality.Controller),
        "semantic accessory-to-firmware policy changed");
    Console.WriteLine("PASS all accessories remain selectable across personalities when idle; transitions gate safely");

    Require(AccessoryPersistenceService.DefaultDebounce == TimeSpan.FromSeconds(20),
        "production autosave quiet period is not 20 seconds");
    Console.WriteLine("PASS production autosave quiet period is 20 seconds");

    string coalescePath = Path.Combine(root, "coalesced.sav");
    CountingWriter coalescingWriter = new();
    await using (AccessoryPersistenceService coalescing = new(
        coalescingWriter, TimeSpan.FromMilliseconds(40)))
    {
        coalescing.Activate(AccessoryPersistenceKind.ControllerPak, coalescePath, 32768,
            _ => Task.FromResult(new byte[32768]), 0);
        coalescing.MarkDirty();
        await Task.Delay(15);
        coalescing.MarkDirty();
        await Task.Delay(15);
        coalescing.MarkDirty();
        await Task.Delay(120);
        Require(coalescingWriter.WriteCount == 1 && coalescingWriter.MaximumConcurrent == 1 &&
                !coalescing.Current.Dirty,
            "autosave requests were not coalesced onto one non-overlapping worker");
    }
    Console.WriteLine("PASS dedicated autosave worker coalesces resets with no overlap");

    string eventDrivenPath = Path.Combine(root, "event-driven.sav");
    CountingWriter eventDrivenWriter = new();
    List<string> eventDrivenLog = [];
    int eventDrivenReads = 0;
    await using (AccessoryPersistenceService eventDriven = new(
        eventDrivenWriter, TimeSpan.FromMilliseconds(30), eventDrivenLog.Add))
    {
        eventDriven.Activate(AccessoryPersistenceKind.GameBoySave, eventDrivenPath,
            32768, _ =>
            {
                eventDrivenReads++;
                return Task.FromResult(Enumerable.Repeat((byte)0x6B, 32768).ToArray());
            }, 10);
        eventDriven.ObserveAccessoryWriteCounter(11);
        await Task.Delay(100);
        Require(!eventDriven.Current.Dirty && eventDrivenReads == 0 &&
                eventDrivenWriter.WriteCount == 0,
            "Game Boy write-counter observation scheduled background persistence");

        eventDriven.MarkDirty();
        await Task.Delay(100);
        Require(eventDriven.Current.Dirty && eventDrivenReads == 0 &&
                eventDrivenWriter.WriteCount == 0,
            "Game Boy dirty state scheduled debounced autosave/GBSDUMP");

        AccessoryPersistenceResult flushed = await eventDriven.FlushAsync(
            AccessoryPersistenceFlushReason.AccessoryChange);
        Require(flushed.Success && !eventDriven.Current.Dirty &&
                eventDrivenReads == 1 && eventDrivenWriter.WriteCount == 1 &&
                eventDrivenLog.SequenceEqual(new[]
                {
                    "TRANSFER SAVE FLUSH START reason=accessory-change",
                    "TRANSFER SAVE FLUSH SUCCESS bytes=32768 reason=accessory-change"
                }),
            "event-driven Transfer Pak flush did not read/write/log exactly once");
    }
    Console.WriteLine("PASS GameBoySave ignores periodic observation/debounce and flushes only at an explicit event");

    string firstWritePath = Path.Combine(root, "first-write-only.mpk");
    byte[] firstWriteData = Enumerable.Repeat((byte)0xA6, 32768).ToArray();
    await using (AccessoryPersistenceService firstWrite = new(debounce: TimeSpan.FromHours(1)))
    {
        firstWrite.Activate(AccessoryPersistenceKind.ControllerPak, firstWritePath,
            firstWriteData.Length, _ => Task.FromResult(firstWriteData), 100);
        firstWrite.ObserveAccessoryWriteCounter(101);
        Require(firstWrite.Current.Dirty,
            "the first and only post-activation accessory write was missed");
        AccessoryPersistenceResult persisted = await firstWrite.FlushAsync();
        Require(persisted.Success && !firstWrite.Current.Dirty &&
                (await File.ReadAllBytesAsync(firstWritePath)).SequenceEqual(firstWriteData),
            "the first-write forced flush did not persist exact changed data");
    }
    Console.WriteLine("PASS explicit activation baseline detects one first write and forced flushes exact data");

    foreach ((AccessoryPersistenceKind kind, string extension) in new[]
    {
        (AccessoryPersistenceKind.ControllerPak, ".mpk")
    })
    {
        int length = kind == AccessoryPersistenceKind.GameBoySave ? 32768 : 32768;
        string path = Path.Combine(root, kind + extension);
        byte[] current = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(path, new byte[length]);
        await using AccessoryPersistenceService persistence = new(
            debounce: TimeSpan.FromMilliseconds(30));
        persistence.Activate(kind, path, length, _ => Task.FromResult(current.ToArray()), 10);
        persistence.ObserveAccessoryWriteCounter(11);
        Require(persistence.Current.Dirty, $"{kind} write did not mark dirty");
        await Task.Delay(150);
        Require(!persistence.Current.Dirty &&
                (await File.ReadAllBytesAsync(path)).SequenceEqual(current),
            $"{kind} autosave did not persist exact bytes and clear dirty");
        current[100] ^= 0xFF;
        persistence.ObserveAccessoryWriteCounter(12);
        AccessoryPersistenceResult forced = await persistence.FlushAsync();
        Require(forced.Success && !persistence.Current.Dirty &&
                (await File.ReadAllBytesAsync(path)).SequenceEqual(current),
            $"{kind} forced flush failed");
        Require(persistence.TryDeactivateClean(), $"{kind} clean deactivation failed");
        Console.WriteLine($"PASS {kind} activation, dirty, autosave, forced flush, exact persistence, deactivation");
    }

    string failedPath = Path.Combine(root, "failure.sav");
    byte[] knownGood = Enumerable.Repeat((byte)0x5A, 32768).ToArray();
    await File.WriteAllBytesAsync(failedPath, knownGood);
    await using (AccessoryPersistenceService failed = new(new FailingWriter(), TimeSpan.FromHours(1)))
    {
        failed.Activate(AccessoryPersistenceKind.GameBoySave, failedPath, knownGood.Length,
            _ => Task.FromResult(Enumerable.Repeat((byte)0xA5, knownGood.Length).ToArray()), 0);
        failed.MarkDirty();
        AccessoryPersistenceResult result = await failed.FlushAsync();
        Require(!result.Success && failed.Current.Dirty &&
                (await File.ReadAllBytesAsync(failedPath)).SequenceEqual(knownGood) &&
                !failed.TryDeactivateClean(),
            "failed persistence cleared dirty state, replaced known-good data, or allowed deactivation");
    }
    Console.WriteLine("PASS persistence failure preserves dirty state and known-good file");

    string disconnectPath = Path.Combine(root, "disconnect-retry.mpk");
    ToggleWriter disconnectWriter = new() { Fail = true };
    await using (AccessoryPersistenceService disconnectPersistence = new(
        disconnectWriter, TimeSpan.FromHours(1)))
    {
        disconnectPersistence.Activate(AccessoryPersistenceKind.ControllerPak,
            disconnectPath, 32768, _ => Task.FromResult(new byte[32768]), 0);
        disconnectPersistence.MarkDirty();
        bool connected = true;
        BridgeDisconnectAuthorization blocked =
            await BridgeDisconnectPersistencePolicy.AuthorizeAsync(
                disconnectPersistence, formClosing: false);
        if (blocked.DisconnectAllowed) connected = false;
        Require(!blocked.DisconnectAllowed && connected &&
                disconnectPersistence.Current is { Active: true, Dirty: true },
            "failed user disconnect cleared connection, dirty state, or persistence lifecycle");

        disconnectWriter.Fail = false;
        BridgeDisconnectAuthorization retry =
            await BridgeDisconnectPersistencePolicy.AuthorizeAsync(
                disconnectPersistence, formClosing: false);
        if (retry.DisconnectAllowed)
        {
            connected = false;
            disconnectPersistence.TryDeactivateClean();
        }
        Require(retry.DisconnectAllowed && !connected &&
                disconnectPersistence.Current is { Active: false, Dirty: false } &&
                File.Exists(disconnectPath),
            "successful persistence retry did not authorize subsequent disconnect");
    }
    Console.WriteLine("PASS failed user disconnect remains connected/dirty/active; retry permits disconnect");

    string transferDisconnectPath = Path.Combine(root, "transfer-disconnect.sav");
    CountingWriter transferDisconnectWriter = new();
    List<string> transferDisconnectLog = [];
    int transferDisconnectReads = 0;
    await using (AccessoryPersistenceService transferDisconnect = new(
        transferDisconnectWriter, TimeSpan.FromHours(1), transferDisconnectLog.Add))
    {
        transferDisconnect.Activate(AccessoryPersistenceKind.GameBoySave,
            transferDisconnectPath, 32768, _ =>
            {
                transferDisconnectReads++;
                return Task.FromResult(new byte[32768]);
            }, 0);
        BridgeDisconnectAuthorization authorization =
            await BridgeDisconnectPersistencePolicy.AuthorizeAsync(
                transferDisconnect, formClosing: false);
        Require(authorization.DisconnectAllowed && transferDisconnectReads == 1 &&
                transferDisconnectWriter.WriteCount == 1 &&
                transferDisconnectLog.Contains(
                    "TRANSFER SAVE FLUSH START reason=disconnect") &&
                transferDisconnectLog.Contains(
                    "TRANSFER SAVE FLUSH SUCCESS bytes=32768 reason=disconnect"),
            "normal disconnect did not force and log a clean Transfer Pak save flush");
    }
    Console.WriteLine("PASS normal disconnect force-reads the associated Transfer Pak save before authorization");

    List<string> shutdownLog = [];
    await using (AccessoryPersistenceService shutdownPersistence = new(
        new FailingWriter(), TimeSpan.FromHours(1), shutdownLog.Add))
    {
        shutdownPersistence.Activate(AccessoryPersistenceKind.GameBoySave,
            Path.Combine(root, "shutdown.sav"), 32768,
            _ => Task.FromResult(new byte[32768]), 0);
        shutdownPersistence.MarkDirty();
        BridgeDisconnectAuthorization shutdown =
            await BridgeDisconnectPersistencePolicy.AuthorizeAsync(
                shutdownPersistence, formClosing: true);
        Require(shutdown.DisconnectAllowed && !shutdown.Persistence.Success &&
                shutdownPersistence.Current is { Active: true, Dirty: true } &&
                shutdownLog.Contains(
                    "TRANSFER SAVE FLUSH START reason=application-close") &&
                shutdownLog.Any(line => line.StartsWith(
                    "TRANSFER SAVE FLUSH FAILED reason=application-close error=",
                    StringComparison.Ordinal)),
            "shutdown failure was hidden, cleared dirty state, or blocked shutdown");
    }
    Console.WriteLine("PASS failed shutdown flush remains explicit/dirty but permits cleanup to continue");

    TaskCompletionSource<byte[]> readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource releaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    string overlapPath = Path.Combine(root, "overlap.mpk");
    await using (AccessoryPersistenceService overlap = new(debounce: TimeSpan.FromHours(1)))
    {
        overlap.Activate(AccessoryPersistenceKind.ControllerPak, overlapPath, 32768, async _ =>
        {
            readStarted.TrySetResult(new byte[32768]);
            await releaseRead.Task;
            return new byte[32768];
        }, 0);
        overlap.MarkDirty();
        Task<AccessoryPersistenceResult> flush = overlap.FlushAsync();
        await readStarted.Task;
        overlap.MarkDirty();
        releaseRead.TrySetResult();
        Require((await flush).Success && overlap.Current.Dirty,
            "write occurring during flush was incorrectly cleared");
    }
    Console.WriteLine("PASS write around autosave remains dirty for another flush");

    await RunModeCase(FirmwarePersonality.Controller, FirmwarePersonality.Network, true, false);
    await RunModeCase(FirmwarePersonality.Network, FirmwarePersonality.Controller, true, false);
    await RunModeCase(FirmwarePersonality.Controller, FirmwarePersonality.Network, false, false);
    await RunModeCase(FirmwarePersonality.Controller, FirmwarePersonality.Network, true, true);
    await RunCombinedDirtyCase(AccessoryPersistenceKind.GameBoySave, ".sav");
    await RunCombinedDirtyCase(AccessoryPersistenceKind.ControllerPak, ".mpk");
    await RunReconnectTimeoutCase();
    for (int succeedsOn = 1; succeedsOn <= 3; succeedsOn++)
    {
        int attemptsForCase = 0;
        string reconnected = await FirmwareModeReconnectPolicy.ExecuteAsync<string>(
            (attempt, _) =>
            {
                attemptsForCase++;
                return attempt < succeedsOn
                    ? Task.FromException<string>(new IOException("Pico rebooting"))
                    : Task.FromResult("verified");
            }, CancellationToken.None, TimeSpan.Zero);
        Require(reconnected == "verified" && attemptsForCase == succeedsOn,
            $"Wi-Fi reconnect did not succeed on requested attempt {succeedsOn}");
    }
    await FirmwareModeReconnectPolicy.WaitForRebootGraceAsync(
        CancellationToken.None, TimeSpan.Zero);
    Require(FirmwareModeReconnectPolicy.RebootGracePeriod == TimeSpan.FromSeconds(20) &&
            FirmwareModeReconnectPolicy.AttemptCount == 3 &&
            FirmwareModeReconnectPolicy.AttemptSpacing == TimeSpan.FromSeconds(2),
        "production reconnect policy is not 20-second grace plus three attempts/two-second spacing");
    int reconnectAttempts = 0;
    try
    {
        await FirmwareModeReconnectPolicy.ExecuteAsync<string>((_, _) =>
        {
            reconnectAttempts++;
            return Task.FromException<string>(new IOException("still offline"));
        }, CancellationToken.None, TimeSpan.Zero);
        throw new InvalidOperationException("reconnect exhaustion was accepted");
    }
    catch (TimeoutException) when (reconnectAttempts == 3) { }
    Console.WriteLine("PASS Wi-Fi reconnect succeeds on attempts 1/2/3 and fails only after 3 attempts");

    BridgeTransportSessionOwner<TrackedDisposable> owner = new();
    TrackedDisposable oldSession = new("old");
    owner.InstallFresh(oldSession);
    long retirement = owner.BeginTransitionRetirement();
    Require(oldSession.Disposed && owner.Current is null && owner.Active is null,
        "accepted transition did not explicitly retire the old active session");
    TrackedDisposable freshSession = new("fresh");
    owner.InstallFresh(freshSession);
    BridgeDeviceStateService raceState = new();
    IBridgeDeviceStateUpdater raceUpdater = raceState;
    raceUpdater.SetFirmwareTransition(new(BridgeFirmwareTransitionPhase.Reconnecting,
        FirmwarePersonality.Network, string.Empty));
    raceUpdater.ReplaceControlSession(BridgeControlConnectionKind.WifiTcp, "NICE64", "1.1.0",
        Mode(FirmwarePersonality.Network));
    bool lateCleanupRetired = owner.TryRetireObserved(oldSession, out _);
    if (owner.IsStillRetired(retirement)) raceUpdater.Disconnect();
    Require(!lateCleanupRetired && ReferenceEquals(owner.Current, freshSession) &&
            !freshSession.Disposed && !owner.IsStillRetired(retirement) &&
            raceState.Current is { ControlConnectionState: BridgeConnectionState.Connected,
                FirmwarePersonality: FirmwarePersonality.Network },
        "late old-session cleanup disposed or cleared the fresh replacement");
    Console.WriteLine("PASS stale TCP retirement is explicit/idempotent and late cleanup preserves fresh session");
    string formSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Form1.cs"));
    string firmwareServiceSource = File.ReadAllText(Path.Combine(
        Directory.GetCurrentDirectory(), "BridgeFirmwareService.cs"));
    string accessoryServiceSource = File.ReadAllText(Path.Combine(
        Directory.GetCurrentDirectory(), "BridgeAccessoryService.cs"));
    string connectionServiceSource = File.ReadAllText(Path.Combine(
        Directory.GetCurrentDirectory(), "BridgeConnectionService.cs"));
    string obsoleteCredentialClaim = "Wi-Fi credentials are " + "temporary";
    Require(!formSource.Contains(obsoleteCredentialClaim, StringComparison.Ordinal) &&
            formSource.Contains("_accessoryService.SelectAccessoryAsync(",
                StringComparison.Ordinal) &&
            accessoryServiceSource.Contains("SelectModeAfterPersistenceAsync(",
                StringComparison.Ordinal),
        "accessory selection does not use the common verified firmware transition path");
    Require(formSource.Split("OpenTcpControlSessionAsync(", StringSplitOptions.None).Length - 1 >= 2 &&
            connectionServiceSource.Contains("PrepareTcpFirmwareReplacementAsync",
                StringComparison.Ordinal) &&
            connectionServiceSource.Contains("ReplaceTcpControlSessionAsync",
                StringComparison.Ordinal) &&
            firmwareServiceSource.Contains("FirmwareModeReconnectPolicy.WaitForRebootGraceAsync",
                StringComparison.Ordinal) &&
            firmwareServiceSource.Contains("FirmwareModeReconnectPolicy.ExecuteAsync",
                StringComparison.Ordinal),
        "manual connect and firmware reconnect do not share the fresh-connect lifecycle");
    Console.WriteLine("PASS manual TCP connect and firmware reconnect share fresh handshake/state/UI path");
    Console.WriteLine("PASS accessory-driven Wi-Fi personality change uses common verified transition path");
    Console.WriteLine("PASS Wi-Fi/USB-neutral mode orchestration, reconnect verification, wrong mode, and persistence blocking");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static async Task RunModeCase(FirmwarePersonality currentMode,
    FirmwarePersonality target, bool persistenceSuccess, bool wrongMode)
{
    BridgeDeviceStateService state = new();
    IBridgeDeviceStateUpdater updater = state;
    updater.Connect(BridgeControlConnectionKind.WifiTcp, "NICE64", "1.1.0",
        Mode(currentMode));
    FirmwareModeTransitionService service = new(state);
    bool requestSent = false;
    FirmwareModeTransitionResult result = await service.ExecuteAsync(new(target,
        _ => Task.FromResult(persistenceSuccess
            ? AccessoryPersistenceResult.Clean
            : new AccessoryPersistenceResult(false, true, "disk full")),
        _ => Task.FromResult(Mode(currentMode)),
        (requested, _) => { requestSent = true; return Task.FromResult(
            new FirmwareModeRequestResult(FirmwareModeRequestKind.Ack, Mode(requested))); },
        (requested, _) => Task.FromResult(Mode(wrongMode ? currentMode : requested))));
    if (!persistenceSuccess)
        Require(!result.Success && !requestSent &&
                state.Current.FirmwareTransition.Phase == BridgeFirmwareTransitionPhase.Failed,
            "persistence failure did not block mode command");
    else if (wrongMode)
        Require(!result.Success && requestSent &&
                state.Current.FirmwareTransition.Phase == BridgeFirmwareTransitionPhase.Failed &&
                state.Current.FirmwarePersonality == currentMode,
            "wrong-mode reconnect was accepted");
    else
        Require(result.Success && requestSent && result.Reconnected &&
                state.Current.FirmwarePersonality == target &&
                state.Current.FirmwareTransition.Phase == BridgeFirmwareTransitionPhase.Completed,
            "ack/reconnect/verified target mode did not complete");
}

static async Task RunCombinedDirtyCase(AccessoryPersistenceKind kind, string extension)
{
    string path = Path.Combine(Path.GetTempPath(), $"nice64-combined-{Guid.NewGuid():N}{extension}");
    try
    {
        byte[] latest = Enumerable.Repeat(kind == AccessoryPersistenceKind.GameBoySave
            ? (byte)0x47 : (byte)0x64, 32768).ToArray();
        await using AccessoryPersistenceService persistence = new(debounce: TimeSpan.FromHours(1));
        persistence.Activate(kind, path, latest.Length, _ => Task.FromResult(latest), 0);
        persistence.MarkDirty();
        bool requestObservedPersistedBytes = false;
        BridgeDeviceStateService state = new();
        IBridgeDeviceStateUpdater updater = state;
        updater.Connect(BridgeControlConnectionKind.WifiTcp, "NICE64", "1.1.0",
            Mode(FirmwarePersonality.Controller));
        FirmwareModeTransitionResult result = await new FirmwareModeTransitionService(state)
            .ExecuteAsync(new(FirmwarePersonality.Network,
                token => persistence.FlushAsync(token),
                _ => Task.FromResult(Mode(FirmwarePersonality.Controller)),
                async (target, _) =>
                {
                    requestObservedPersistedBytes = File.Exists(path) &&
                        (await File.ReadAllBytesAsync(path)).SequenceEqual(latest);
                    return new(FirmwareModeRequestKind.Ack, Mode(target));
                },
                (target, _) => Task.FromResult(Mode(target))));
        Require(result.Success && requestObservedPersistedBytes && !persistence.Current.Dirty,
            $"{kind} was not persisted before Wi-Fi mode command");
    }
    finally { try { File.Delete(path); } catch { } }
}

static async Task RunReconnectTimeoutCase()
{
    BridgeDeviceStateService state = new();
    IBridgeDeviceStateUpdater updater = state;
    updater.Connect(BridgeControlConnectionKind.WifiTcp, "NICE64", "1.1.0",
        Mode(FirmwarePersonality.Controller));
    FirmwareModeTransitionResult result = await new FirmwareModeTransitionService(state)
        .ExecuteAsync(new(FirmwarePersonality.Network,
            _ => Task.FromResult(AccessoryPersistenceResult.Clean),
            _ => Task.FromResult(Mode(FirmwarePersonality.Controller)),
            (target, _) => Task.FromResult(new FirmwareModeRequestResult(
                FirmwareModeRequestKind.Ack, Mode(target))),
            (_, _) => throw new TimeoutException("device did not return")));
    Require(!result.Success && result.RequestSent && !result.Reconnected &&
            state.Current.FirmwareTransition.Phase == BridgeFirmwareTransitionPhase.Failed,
        "bounded reconnect timeout was not classified failed/incomplete");
}

static FirmwareModeInfo Mode(FirmwarePersonality mode) => mode switch
{
    FirmwarePersonality.Controller => new(mode, "CONTROLLER", "NONE"),
    FirmwarePersonality.Diagnostics => new(mode, "STANDALONE", "DIAGNOSTICS"),
    _ => new(mode, "STANDALONE", "NETWORK")
};

sealed class FailingWriter : IAtomicPersistenceWriter
{
    public Task WriteVerifiedAsync(string path, byte[] data, int expectedLength,
        CancellationToken cancellationToken) => throw new IOException("simulated disk full");
}

sealed class ToggleWriter : IAtomicPersistenceWriter
{
    private readonly AtomicPersistenceWriter _inner = new();
    public bool Fail { get; set; }

    public Task WriteVerifiedAsync(string path, byte[] data, int expectedLength,
        CancellationToken cancellationToken) => Fail
        ? throw new IOException("simulated retryable disk failure")
        : _inner.WriteVerifiedAsync(path, data, expectedLength, cancellationToken);
}

sealed class CountingWriter : IAtomicPersistenceWriter
{
    private readonly AtomicPersistenceWriter _inner = new();
    private int _concurrent;
    public int WriteCount { get; private set; }
    public int MaximumConcurrent { get; private set; }

    public async Task WriteVerifiedAsync(string path, byte[] data, int expectedLength,
        CancellationToken cancellationToken)
    {
        int concurrent = Interlocked.Increment(ref _concurrent);
        MaximumConcurrent = Math.Max(MaximumConcurrent, concurrent);
        WriteCount++;
        try
        {
            await Task.Delay(10, cancellationToken);
            await _inner.WriteVerifiedAsync(path, data, expectedLength, cancellationToken);
        }
        finally { Interlocked.Decrement(ref _concurrent); }
    }
}

sealed class TrackedDisposable(string name) : IDisposable
{
    public string Name { get; } = name;
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}
