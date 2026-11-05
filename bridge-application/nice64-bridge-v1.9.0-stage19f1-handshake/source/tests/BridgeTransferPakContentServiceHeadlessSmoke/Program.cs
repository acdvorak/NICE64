using PicoSwitchController;

await RunAsync();

static async Task RunAsync()
{
    MetadataParserParity();
    await InspectionAndCapacityAsync();
    await SaveNormalizationAsync();
    await FullInstallAndFailureTruthAsync();
    await SerializationAndCancellationAsync();
    await SaveCurrentAndLegacyAsync();
    Console.WriteLine("PASS: BridgeTransferPakContentService headless smoke");
}

static void MetadataParserParity()
{
    byte[] rom = MakeRom(65536, "CLOCK QUEST", 0x10, 0x05, 0x03);
    GameBoyRomMetadata metadata = GameBoyRomMetadataParser.Parse(rom);
    Assert(metadata.Title == "CLOCK QUEST" && metadata.CartType == 0x10 &&
        metadata.CartTypeName == "MBC3+TIMER+RAM+BATTERY" &&
        metadata.Mapper == "MBC3" && metadata.DecodedRomSize == 1024 * 1024 &&
        metadata.DecodedRamSize == 32 * 1024 && metadata.RtcRequired &&
        metadata.FirmwareFlags == 0x01,
        "metadata parser preserves title/cart/mapper/ROM/RAM/RTC projection");
    Assert(GameBoyRomMetadataParser.Parse(
        MakeRom(65536, "MBC ONE", 0x03, 0x01, 0x02)).Mapper == "MBC1",
        "MBC1 mapper parity");
    Assert(GameBoyRomMetadataParser.Parse(
        MakeRom(65536, "MBC FIVE", 0x1E, 0x02, 0x05)).Mapper == "MBC5",
        "MBC5 mapper parity");
    GameBoyRomMetadata unknown = GameBoyRomMetadataParser.Parse(
        MakeRom(70000, "", 0x7F, 0x7F, 0));
    Assert(unknown.Title == "GB ROM" && unknown.CartTypeName == "Unknown" &&
        unknown.Mapper == "ROM" && unknown.DecodedRomSize == 70000,
        "fallback metadata behavior matches Form1 parser");
}

static async Task InspectionAndCapacityAsync()
{
    Fixture inspect = new();
    string romPath = inspect.AddRom("inspect.gbc", 131072, "INSPECT", 0x13, 0x02, 0x03);
    inspect.Files.Files[Path.ChangeExtension(romPath, ".sav")] = new byte[123];
    BridgeTransferPakContentResult result = await inspect.Service.InspectRomAsync(romPath);
    Assert(result.Success && result.Inspection is not null &&
        result.Inspection.RomPath == romPath && result.Inspection.RomLength == 131072 &&
        result.Inspection.ExistingSaveLength == 123 &&
        result.Inspection.Metadata.Title == "INSPECT" &&
        result.Snapshot.LastSuccessfulOperation ==
            BridgeTransferPakContentOperation.InspectRom,
        "inspection returns normalized ROM/save metadata");
    Assert(inspect.Bulk.TotalMutationCount == 0 && inspect.Accessory.AdoptCount == 0 &&
        inspect.Persistence.FlushCount == 0 && inspect.Files.WriteCount == 0,
        "inspection performs no Pico, accessory, persistence, or filesystem mutation");

    Fixture small = new();
    string smallPath = small.AddRom("small.gb", 65535);
    BridgeTransferPakContentResult rejected = await small.Service.InspectRomAsync(smallPath);
    Assert(rejected.Failure == BridgeTransferPakContentFailure.InvalidRom,
        "ROM below 64 KiB is rejected");

    Fixture large = new();
    large.Bulk.Capacity = 128 * 1024;
    string largePath = large.AddRom("large.gb", 256 * 1024);
    BridgeTransferPakContentResult tooLarge = await large.Service.InstallRomAsync(largePath);
    Assert(tooLarge.Failure == BridgeTransferPakContentFailure.RomTooLarge &&
        large.Bulk.FullInstallCount == 0,
        "ROM above effective flash capacity is rejected before destructive work");

    Fixture capacity = new();
    capacity.Bulk.FailCapacity = true;
    string capacityPath = capacity.AddRom("capacity.gb", 65536);
    BridgeTransferPakContentResult failed = await capacity.Service.InstallRomAsync(capacityPath);
    Assert(failed.Failure == BridgeTransferPakContentFailure.CapacityQueryFailure &&
        capacity.Bulk.FullInstallCount == 0,
        "capacity-query failure is typed and non-destructive");
}

static async Task SaveNormalizationAsync()
{
    Fixture exact = new();
    string exactRom = exact.AddRom("exact.gb", 65536);
    string exactSave = Path.ChangeExtension(exactRom, ".sav");
    byte[] exactData = Filled(32768, 0x31);
    exact.Files.Files[exactSave] = exactData.ToArray();
    await exact.Service.InstallRomAsync(exactRom);
    Assert(exact.Files.WriteCount == 0 &&
        exact.Bulk.LastInstalledSave!.SequenceEqual(exactData),
        "exact-size save is preserved without rewrite");

    Fixture shortSave = new();
    string shortRom = shortSave.AddRom("short.gb", 65536);
    string shortPath = Path.ChangeExtension(shortRom, ".sav");
    shortSave.Files.Files[shortPath] = Filled(100, 0x22);
    BridgeTransferPakContentResult shortResult =
        await shortSave.Service.InstallRomAsync(shortRom);
    byte[] padded = shortSave.Files.Files[shortPath];
    Assert(shortResult.Success && padded.Length == 32768 &&
        padded.AsSpan(0, 100).ToArray().All(value => value == 0x22) &&
        padded.AsSpan(100).ToArray().All(value => value == 0xFF) &&
        shortSave.Bulk.LastInstalledSave!.SequenceEqual(padded),
        "short save is padded with 0xFF, atomically rewritten, and uploaded identically");

    Fixture oversized = new();
    string oversizedRom = oversized.AddRom("oversized.gb", 65536);
    string oversizedPath = Path.ChangeExtension(oversizedRom, ".sav");
    byte[] oversizedData = Enumerable.Range(0, 40000).Select(i => (byte)i).ToArray();
    oversized.Files.Files[oversizedPath] = oversizedData;
    BridgeTransferPakContentResult oversizedResult =
        await oversized.Service.InstallRomAsync(oversizedRom);
    byte[] truncated = oversized.Files.Files[oversizedPath];
    Assert(oversizedResult.Success && truncated.Length == 32768 &&
        truncated.SequenceEqual(oversizedData[..32768]) &&
        oversized.Bulk.LastInstalledSave!.SequenceEqual(truncated),
        "oversized save is truncated, atomically rewritten, and uploaded identically");

    Fixture missing = new();
    string missingRom = missing.AddRom("missing-save.gb", 65536);
    string missingPath = Path.ChangeExtension(missingRom, ".sav");
    await missing.Service.InstallRomAsync(missingRom);
    Assert(missing.Files.Files[missingPath].Length == 32768 &&
        missing.Files.Files[missingPath].All(value => value == 0xFF),
        "missing save creates exact 0xFF-filled sidecar");
}

static async Task FullInstallAndFailureTruthAsync()
{
    Fixture success = new();
    string path = success.AddRom("success.gbc", 131072, "SUCCESS", 0x1B, 0x02, 0x03);
    BridgeTransferPakContentResult installed = await success.Service.InstallRomAsync(path);
    string savePath = Path.ChangeExtension(path, ".sav");
    Assert(installed.Success && installed.Snapshot.ContentPresent &&
        installed.Snapshot.InstallKind == BridgeTransferPakInstallKind.FullFlash &&
        installed.Snapshot.RomPath == path && installed.Snapshot.SavePath == savePath &&
        installed.Snapshot.HasAssociatedRom && installed.Snapshot.HasAssociatedSave &&
        installed.Snapshot.RomLength == 131072 &&
        installed.Snapshot.ExpectedSaveLength == 32768 &&
        installed.Snapshot.Metadata?.Title == "SUCCESS",
        "full install commits complete semantic snapshot");
    Assert(success.Accessory.AdoptObservedAfterVerifiedWire &&
        success.Persistence.Current.Active &&
        success.Persistence.Current.Kind == AccessoryPersistenceKind.GameBoySave &&
        success.Persistence.Current.Path == savePath,
        "verified install adopts before committing active sidecar persistence");
    Assert(success.Input.Balanced, "successful install balances controller suspension");
    Assert(success.Persistence.LastFlushReason ==
        AccessoryPersistenceFlushReason.RomReplacement,
        "full install did not identify the ROM-replacement persistence boundary");

    Fixture dirty = new();
    await dirty.Service.InstallRomAsync(dirty.AddRom("dirty-old.gb", 65536));
    dirty.Persistence.MarkDirty();
    string dirtyNew = dirty.AddRom("dirty-new.gb", 65536);
    BridgeTransferPakContentResult dirtyResult =
        await dirty.Service.InstallRomAsync(dirtyNew);
    Assert(dirtyResult.Success && dirty.Persistence.FlushCount >= 2 &&
        dirty.Persistence.Current.Path == Path.ChangeExtension(dirtyNew, ".sav"),
        "dirty old save lifecycle flushes before successful destructive transition");

    Fixture flushFailure = new();
    string retainedRom = flushFailure.AddRom("flush-old.gb", 65536);
    await flushFailure.Service.InstallRomAsync(retainedRom);
    flushFailure.Persistence.FlushResult = new(false, true, "save read failed");
    int installsBeforeFailure = flushFailure.Bulk.FullInstallCount;
    BridgeTransferPakContentResult blockedReplacement =
        await flushFailure.Service.InstallRomAsync(
            flushFailure.AddRom("flush-new.gb", 65536));
    Assert(blockedReplacement.Failure == BridgeTransferPakContentFailure.PersistenceFailure &&
        flushFailure.Bulk.FullInstallCount == installsBeforeFailure &&
        blockedReplacement.Snapshot.RomPath == retainedRom &&
        flushFailure.Persistence.Current.Active,
        "failed event-driven save flush did not block ROM replacement and retain association");

    Fixture transfer = new();
    await transfer.Service.InstallRomAsync(transfer.AddRom("old.gb", 65536));
    transfer.Bulk.FailFullInstall = true;
    BridgeTransferPakContentResult transferFailure =
        await transfer.Service.InstallRomAsync(transfer.AddRom("transfer-fail.gb", 65536));
    Assert(transferFailure.Failure == BridgeTransferPakContentFailure.TransferFailure &&
        !transferFailure.Snapshot.ContentPresent &&
        !transferFailure.Snapshot.HasAssociatedRom &&
        !transfer.Persistence.Current.Active && transfer.Accessory.AdoptCount == 1,
        "destructive transfer failure clears stale associations and does not falsely adopt");
    Assert(transfer.Input.Balanced, "transfer failure balances controller suspension");

    Fixture adoption = new();
    await adoption.Service.InstallRomAsync(adoption.AddRom("adoption-old.gb", 65536));
    adoption.Accessory.Fail = true;
    BridgeTransferPakContentResult adoptionFailure =
        await adoption.Service.InstallRomAsync(adoption.AddRom("adoption-new.gb", 65536));
    Assert(adoptionFailure.Failure ==
        BridgeTransferPakContentFailure.AccessoryAdoptionFailure &&
        adoptionFailure.Snapshot.ContentPresent &&
        !adoptionFailure.Snapshot.HasAssociatedRom &&
        !adoptionFailure.Snapshot.HasAssociatedSave &&
        !adoption.Persistence.Current.Active,
        "post-wire adoption failure records verified content as unassociated");
    Assert(adoption.Input.Balanced, "adoption failure balances controller suspension");

    Fixture activation = new();
    await activation.Service.InstallRomAsync(activation.AddRom("activation-old.gb", 65536));
    string activationRom = activation.AddRom("activation-new.gb", 65536);
    activation.Persistence.FailActivationPath = Path.ChangeExtension(activationRom, ".sav");
    BridgeTransferPakContentResult activationFailure =
        await activation.Service.InstallRomAsync(activationRom);
    Assert(activationFailure.Failure == BridgeTransferPakContentFailure.PersistenceFailure &&
        activationFailure.Snapshot.ContentPresent &&
        activationFailure.Snapshot.RomPath is null &&
        activationFailure.Snapshot.SavePath is null &&
        !activation.Persistence.Current.Active,
        "install persistence activation failure never restores stale old association");
    Assert(activation.Input.Balanced,
        "persistence activation failure balances controller suspension");
}

static async Task SerializationAndCancellationAsync()
{
    Fixture busy = new();
    busy.Bulk.BlockInstall = true;
    string path = busy.AddRom("busy.gb", 65536);
    Task<BridgeTransferPakContentResult> owner = busy.Service.InstallRomAsync(path);
    await busy.Bulk.InstallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    BridgeTransferPakContentResult peer = await busy.Service.InstallRomAsync(path);
    Assert(peer.Failure == BridgeTransferPakContentFailure.Busy,
        "concurrent operation returns typed Busy");
    busy.Bulk.ReleaseInstall.TrySetResult();
    Assert((await owner).Success && busy.Input.Balanced,
        "serialized owner completes with balanced suspension");

    Fixture cancelled = new();
    string cancelledPath = cancelled.AddRom("cancelled.gb", 65536);
    using CancellationTokenSource stop = new();
    stop.Cancel();
    BridgeTransferPakContentResult result =
        await cancelled.Service.InstallRomAsync(cancelledPath, cancellationToken: stop.Token);
    Assert(result.Failure == BridgeTransferPakContentFailure.Cancelled &&
        cancelled.Files.ReadCount == 0 && cancelled.Files.WriteCount == 0 &&
        cancelled.Bulk.TotalMutationCount == 0 && cancelled.Persistence.FlushCount == 0 &&
        cancelled.Accessory.AdoptCount == 0,
        "pre-cancelled install performs no destructive or persistence work");

    Fixture midCancel = new();
    midCancel.Bulk.BlockInstall = true;
    string midPath = midCancel.AddRom("mid-cancel.gb", 65536);
    using CancellationTokenSource midStop = new();
    Task<BridgeTransferPakContentResult> midTask =
        midCancel.Service.InstallRomAsync(midPath, cancellationToken: midStop.Token);
    await midCancel.Bulk.InstallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    midStop.Cancel();
    BridgeTransferPakContentResult midResult = await midTask;
    Assert(midResult.Failure == BridgeTransferPakContentFailure.Cancelled &&
        midCancel.Input.Balanced && !midCancel.Persistence.Current.Active &&
        !midResult.Snapshot.ContentPresent,
        "mid-transfer cancellation balances suspension and clears unverifiable content truth");
}

static async Task SaveCurrentAndLegacyAsync()
{
    Fixture save = new();
    string rom = save.AddRom("save-current.gb", 65536);
    await save.Service.InstallRomAsync(rom);
    string sidecar = Path.ChangeExtension(rom, ".sav");
    save.Persistence.MarkDirty();
    save.Bulk.DownloadData = Filled(32768, 0x7A);
    BridgeTransferPakContentResult saved = await save.Service.SaveCurrentAsync();
    Assert(saved.Success && saved.Snapshot.SavePath == sidecar &&
        save.Files.Files[sidecar].All(value => value == 0x7A) &&
        save.Persistence.Current.Path == sidecar,
        "save-current downloads, atomically writes, and retains consistent persistence");

    Fixture noCurrent = new();
    BridgeTransferPakContentResult absent = await noCurrent.Service.SaveCurrentAsync();
    Assert(absent.Failure == BridgeTransferPakContentFailure.NoCurrentContent &&
        noCurrent.Bulk.DownloadCount == 0,
        "save-current without known content returns typed NoCurrentContent");

    save.Files.FailWritePath = sidecar;
    SnapshotPair beforeWrite = save.Capture();
    BridgeTransferPakContentResult writeFailure = await save.Service.SaveCurrentAsync();
    Assert(writeFailure.Failure == BridgeTransferPakContentFailure.FileWriteFailure,
        "save-current file failure is typed");
    save.AssertAssociation(beforeWrite,
        "save-current write failure retains prior truthful association");

    Fixture saveActivation = new();
    string oldRom = saveActivation.AddRom("save-as-old.gb", 65536);
    await saveActivation.Service.InstallRomAsync(oldRom);
    string oldSave = Path.ChangeExtension(oldRom, ".sav");
    string newSave = saveActivation.Files.NormalizePath("save-as-new.sav");
    saveActivation.Persistence.FailActivationPath = newSave;
    BridgeTransferPakContentResult activationFailure =
        await saveActivation.Service.SaveCurrentAsync(newSave);
    Assert(activationFailure.Failure == BridgeTransferPakContentFailure.PersistenceFailure &&
        activationFailure.Snapshot.SavePath == oldSave &&
        saveActivation.Persistence.Current.Active &&
        saveActivation.Persistence.Current.Path == oldSave &&
        saveActivation.Files.Files.ContainsKey(newSave),
        "non-destructive Save As activation failure restores old save association");
    Assert(saveActivation.Input.Balanced,
        "save-current activation failure balances controller suspension");

    Fixture legacy = new();
    string legacyRom = legacy.AddRom("legacy.gb", 131072, "LEGACY", 0x03, 0x02, 0x02);
    IBridgeTransferPakLegacyContentService legacyService = legacy.Service;
    BridgeTransferPakContentResult legacyResult =
        await legacyService.LoadLegacyAsync(legacyRom);
    Assert(legacyResult.Success && legacy.Bulk.LegacyInstallCount == 1 &&
        legacy.Bulk.CapacityQueryCount == 0 &&
        legacyResult.Snapshot.InstallKind == BridgeTransferPakInstallKind.LegacyFirst64K &&
        legacy.Bulk.LastProfile?.CartType == 0x03 &&
        legacy.Bulk.LastInstalledSave?.Length == 32768,
        "legacy path preserves profile/save preparation without full-flash capacity query");
    Assert(legacy.Accessory.AdoptObservedAfterVerifiedWire && legacy.Input.Balanced,
        "legacy verified wire completion adopts and balances suspension");
}

static byte[] MakeRom(int length, string title = "TEST ROM", byte cart = 0x00,
    byte romCode = 0x01, byte ramCode = 0x00)
{
    byte[] rom = new byte[length];
    byte[] titleBytes = System.Text.Encoding.ASCII.GetBytes(title);
    Buffer.BlockCopy(titleBytes, 0, rom, 0x0134, Math.Min(16, titleBytes.Length));
    rom[0x0147] = cart;
    rom[0x0148] = romCode;
    rom[0x0149] = ramCode;
    return rom;
}

static byte[] Filled(int length, byte value)
{
    byte[] data = new byte[length];
    Array.Fill(data, value);
    return data;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
}

sealed class Fixture
{
    public FakeFiles Files { get; } = new();
    public FakeBulk Bulk { get; } = new();
    public FakeAccessory Accessory { get; }
    public FakeInput Input { get; } = new();
    public FakePersistence Persistence { get; } = new();
    public BridgeTransferPakContentService Service { get; }

    public Fixture()
    {
        Accessory = new(Bulk);
        Service = new(Files, Bulk, Accessory, Input, Persistence);
    }

    public string AddRom(string path, int length, string title = "TEST ROM",
        byte cart = 0x00, byte romCode = 0x01, byte ramCode = 0x00)
    {
        string normalized = Files.NormalizePath(path);
        Files.Files[normalized] = ProgramData.MakeRom(
            length, title, cart, romCode, ramCode);
        return normalized;
    }

    public SnapshotPair Capture() => new(Service.Current, Persistence.Current);

    public void AssertAssociation(SnapshotPair expected, string message)
    {
        BridgeTransferPakContentSnapshot content = Service.Current;
        AccessoryPersistenceSnapshot persistence = Persistence.Current;
        if (!(content.RomPath == expected.Content.RomPath &&
            content.SavePath == expected.Content.SavePath &&
            content.HasAssociatedRom == expected.Content.HasAssociatedRom &&
            content.HasAssociatedSave == expected.Content.HasAssociatedSave &&
            content.ContentPresent == expected.Content.ContentPresent &&
            persistence.Active == expected.Persistence.Active &&
            persistence.Path == expected.Persistence.Path))
            throw new InvalidOperationException("FAIL: " + message);
    }
}

static class ProgramData
{
    public static byte[] MakeRom(int length, string title, byte cart,
        byte romCode, byte ramCode)
    {
        byte[] rom = new byte[length];
        byte[] titleBytes = System.Text.Encoding.ASCII.GetBytes(title);
        Buffer.BlockCopy(titleBytes, 0, rom, 0x0134, Math.Min(16, titleBytes.Length));
        rom[0x0147] = cart;
        rom[0x0148] = romCode;
        rom[0x0149] = ramCode;
        return rom;
    }
}

sealed record SnapshotPair(BridgeTransferPakContentSnapshot Content,
    AccessoryPersistenceSnapshot Persistence);

sealed class FakeFiles : IBridgeTransferPakFileSystem
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int ReadCount { get; private set; }
    public int WriteCount { get; private set; }
    public string? FailWritePath { get; set; }

    public string NormalizePath(string path) => Path.GetFullPath(path);
    public bool FileExists(string path) => Files.ContainsKey(path);
    public long GetFileLength(string path) => Files[path].LongLength;

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        if (!Files.TryGetValue(path, out byte[]? data)) throw new FileNotFoundException(path);
        return Task.FromResult(data.ToArray());
    }

    public Task WriteVerifiedAsync(string path, byte[] data, int expectedLength,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteCount++;
        if (string.Equals(path, FailWritePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("atomic write failed");
        if (data.Length != expectedLength) throw new InvalidDataException();
        Files[path] = data.ToArray();
        return Task.CompletedTask;
    }
}

sealed class FakeBulk : IBridgeTransferPakBulkPort
{
    public bool IsConnected { get; set; } = true;
    public int Capacity { get; set; } = 2 * 1024 * 1024;
    public bool FailCapacity { get; set; }
    public bool FailFullInstall { get; set; }
    public bool BlockInstall { get; set; }
    public int CapacityQueryCount { get; private set; }
    public int FullInstallCount { get; private set; }
    public int LegacyInstallCount { get; private set; }
    public int DownloadCount { get; private set; }
    public bool InstallVerified { get; private set; }
    public byte[]? LastInstalledSave { get; private set; }
    public GameBoyTransferProfile? LastProfile { get; private set; }
    public byte[] DownloadData { get; set; } = NewFilled(32768, 0x55);
    public TaskCompletionSource InstallEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseInstall { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int TotalMutationCount => FullInstallCount + LegacyInstallCount + DownloadCount;

    public Task<int> QueryFlashCapacityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CapacityQueryCount++;
        if (FailCapacity) throw new IOException("capacity failed");
        return Task.FromResult(Capacity);
    }

    public async Task InstallFullAsync(byte[] rom, GameBoyTransferProfile profile,
        byte[] save, Action<BridgeBulkProgress>? progress,
        CancellationToken cancellationToken)
    {
        FullInstallCount++;
        InstallVerified = false;
        InstallEntered.TrySetResult();
        if (BlockInstall) await ReleaseInstall.Task.WaitAsync(cancellationToken);
        if (FailFullInstall) throw new IOException("full install failed");
        LastProfile = profile;
        LastInstalledSave = save.ToArray();
        progress?.Invoke(new("GB_ROM_UPLOAD", rom.Length, rom.Length, 1, 1));
        progress?.Invoke(new("GB_SAVE_UPLOAD", save.Length, save.Length, 1, 1));
        InstallVerified = true;
    }

    public Task InstallLegacyAsync(byte[] rom, GameBoyTransferProfile profile,
        byte[] save, Action<BridgeBulkProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LegacyInstallCount++;
        InstallVerified = false;
        LastProfile = profile;
        LastInstalledSave = save.ToArray();
        progress?.Invoke(new("GB_ROM_UPLOAD", 65536, 65536, 2048, 2048));
        progress?.Invoke(new("GB_SAVE_UPLOAD", save.Length, save.Length, 1024, 1024));
        InstallVerified = true;
        return Task.CompletedTask;
    }

    public Task<byte[]> DownloadSaveAsync(int expectedLength,
        Action<BridgeBulkProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DownloadCount++;
        progress?.Invoke(new("GB_SAVE_DOWNLOAD", expectedLength, expectedLength, 1, 1));
        return Task.FromResult(DownloadData.ToArray());
    }

    public Task<uint?> QueryAccessoryWriteCounterAsync(CancellationToken cancellationToken) =>
        Task.FromResult<uint?>(17);

    private static byte[] NewFilled(int length, byte value)
    {
        byte[] data = new byte[length];
        Array.Fill(data, value);
        return data;
    }
}

sealed class FakeAccessory(FakeBulk bulk) : IBridgeTransferPakAccessoryPort
{
    public int AdoptCount { get; private set; }
    public bool AdoptObservedAfterVerifiedWire { get; private set; }
    public bool Fail { get; set; }

    public void AdoptVerifiedTransferPak()
    {
        if (Fail) throw new InvalidOperationException("adoption failed");
        AdoptCount++;
        AdoptObservedAfterVerifiedWire = bulk.InstallVerified;
    }
}

sealed class FakeInput : IBridgeTransferPakInputGuard
{
    public int SuspendCount { get; private set; }
    public int ResumeCount { get; private set; }
    public bool Suspended { get; private set; }
    public bool Balanced => !Suspended && SuspendCount == ResumeCount;

    public void Suspend()
    {
        if (Suspended) throw new InvalidOperationException("nested suspension");
        Suspended = true;
        SuspendCount++;
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        if (!Suspended) throw new InvalidOperationException("unbalanced resume");
        Suspended = false;
        ResumeCount++;
        return Task.CompletedTask;
    }
}

sealed class FakePersistence : IAccessoryPersistenceService
{
    public AccessoryPersistenceSnapshot Current { get; private set; } = Empty();
    public int FlushCount { get; private set; }
    public AccessoryPersistenceFlushReason LastFlushReason { get; private set; }
    public AccessoryPersistenceResult FlushResult { get; set; } =
        AccessoryPersistenceResult.Clean;
    public string? FailActivationPath { get; set; }

    public void Activate(AccessoryPersistenceKind kind, string path, int expectedLength,
        Func<CancellationToken, Task<byte[]>> readCurrent, uint initialWriteCounter)
    {
        string normalized = Path.GetFullPath(path);
        if (string.Equals(normalized, FailActivationPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("activation failed");
        if (Current.Active && Current.Dirty) throw new InvalidOperationException("dirty");
        Current = new(kind, normalized, expectedLength, true, false, false,
            0, null, string.Empty);
    }

    public void ObserveAccessoryWriteCounter(uint counter) { }
    public void MarkDirty()
    {
        if (Current.Active) Current = Current with
        { Dirty = true, DirtyGeneration = Current.DirtyGeneration + 1 };
    }

    public Task<AccessoryPersistenceResult> FlushAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FlushCount++;
        if (!FlushResult.Success) return Task.FromResult(FlushResult);
        bool dirty = Current.Dirty;
        Current = Current with { Dirty = false };
        return Task.FromResult(new AccessoryPersistenceResult(true, dirty, string.Empty));
    }

    public Task<AccessoryPersistenceResult> FlushAsync(
        AccessoryPersistenceFlushReason reason,
        CancellationToken cancellationToken = default)
    {
        LastFlushReason = reason;
        return FlushAsync(cancellationToken);
    }

    public bool TryDeactivateClean()
    {
        if (Current.Dirty || Current.FlushInProgress) return false;
        Current = Empty();
        return true;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static AccessoryPersistenceSnapshot Empty() =>
        new(AccessoryPersistenceKind.None, string.Empty, 0, false, false, false,
            0, null, string.Empty);
}

namespace PicoSwitchController
{
    public sealed record GameBoyTransferProfile(
        byte CartType,
        int RomSize,
        int RamSize,
        byte FirmwareFlags);

    public sealed record BridgeBulkProgress(
        string Phase,
        int CompletedBytes,
        int TotalBytes,
        int ItemIndex,
        int ItemCount);
}
