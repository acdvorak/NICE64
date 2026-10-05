using PicoSwitchController;

await RunAsync();

static async Task RunAsync()
{
    await BlankPakAndUnsavedCreateAsync();
    await CreateWithAtomicFileAssociationAsync();
    await LoadAndValidationFailuresAsync();
    await TransferAndWriteFailuresAsync();
    await DirtyTransitionAndSaveAsAsync();
    await PersistenceActivationFailuresAsync();
    await AdoptionAndSuspensionAsync();
    await BusyAndCancellationAsync();
    Console.WriteLine("PASS: BridgeControllerPakContentService headless smoke");
}

static async Task BlankPakAndUnsavedCreateAsync()
{
    Fixture fixture = new();
    BridgeControllerPakContentResult first = await fixture.Service.CreateAsync("old.mpk");
    Assert(first.Success, "initial associated create succeeds");
    BridgeControllerPakContentResult result = await fixture.Service.CreateAsync(null);
    Assert(result.Success, "unsaved create succeeds");
    Assert(result.Snapshot.Path is null && !result.Snapshot.HasAssociatedFile,
        "unsaved create clears the prior association");
    Assert(result.Snapshot.ContentPresent &&
        result.Snapshot.ImageSize == BridgeControllerPakContentService.ControllerPakImageSize,
        "unsaved snapshot reports content and exact image size");
    Assert(!fixture.Persistence.Current.Active,
        "unsaved create leaves no persistence association");
    byte[] image = fixture.Bulk.LastUploaded!;
    Assert(image.Length == 32768, "blank image is exactly 32 KiB");
    Assert(image.AsSpan(0x20, 32).SequenceEqual(image.AsSpan(0x60, 32)) &&
        image.AsSpan(0x20, 32).SequenceEqual(image.AsSpan(0x80, 32)) &&
        image.AsSpan(0x20, 32).SequenceEqual(image.AsSpan(0xC0, 32)),
        "blank image preserves mirrored ID blocks");
    Assert(image[0x101] == IndexChecksum(image, 0x100) &&
        image[0x201] == IndexChecksum(image, 0x200),
        "blank image preserves index checksum format");
}

static async Task CreateWithAtomicFileAssociationAsync()
{
    string directory = Path.Combine(Path.GetTempPath(),
        $"nice64-f6a-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        FakeBulk bulk = new();
        FakePersistence persistence = new();
        BridgeControllerPakContentService service = new(
            new BridgeControllerPakFileSystem(), bulk, new FakeAccessory(),
            new FakeInputGuard(), persistence);
        string path = Path.Combine(directory, "created.mpk");
        BridgeControllerPakContentResult result = await service.CreateAsync(path);
        Assert(result.Success && File.Exists(path),
            "create with path writes through atomic filesystem boundary");
        Assert(new FileInfo(path).Length == 32768,
            "created associated file has exact image size");
        Assert(result.Snapshot.Path == Path.GetFullPath(path) &&
            persistence.Current.Path == Path.GetFullPath(path),
            "content and persistence commit the same normalized path");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

static async Task LoadAndValidationFailuresAsync()
{
    Fixture fixture = new();
    await fixture.Service.CreateAsync("old.mpk");
    fixture.Files.Files[fixture.Files.NormalizePath("valid.mpk")] = Filled(0x5A);
    BridgeControllerPakContentResult loaded = await fixture.Service.LoadAsync("valid.mpk");
    Assert(loaded.Success && loaded.Snapshot.Path == fixture.Files.NormalizePath("valid.mpk"),
        "valid exact-size image loads and commits");
    Assert(fixture.Bulk.LastUploaded!.All(value => value == 0x5A),
        "load uploads the selected image");

    fixture.Files.Files[fixture.Files.NormalizePath("wrong.mpk")] = new byte[17];
    SnapshotPair beforeWrong = fixture.Capture();
    BridgeControllerPakContentResult wrong = await fixture.Service.LoadAsync("wrong.mpk");
    Assert(wrong.Failure == BridgeControllerPakContentFailure.InvalidImage,
        "wrong-size image returns typed InvalidImage");
    fixture.AssertAssociation(beforeWrong, "wrong-size load preserves prior association");

    fixture.Files.FailRead = true;
    SnapshotPair beforeRead = fixture.Capture();
    BridgeControllerPakContentResult read = await fixture.Service.LoadAsync("missing.mpk");
    Assert(read.Failure == BridgeControllerPakContentFailure.FileReadFailure,
        "file read error is typed");
    fixture.AssertAssociation(beforeRead, "read failure preserves prior association");
}

static async Task TransferAndWriteFailuresAsync()
{
    Fixture fixture = new();
    await fixture.Service.CreateAsync("old.mpk");
    fixture.Files.Files[fixture.Files.NormalizePath("next.mpk")] = Filled(0x33);
    fixture.Bulk.FailUpload = true;
    int adopted = fixture.Accessory.AdoptCount;
    SnapshotPair beforeTransfer = fixture.Capture();
    BridgeControllerPakContentResult transfer = await fixture.Service.LoadAsync("next.mpk");
    Assert(transfer.Failure == BridgeControllerPakContentFailure.TransferFailure,
        "upload failure is typed");
    Assert(fixture.Accessory.AdoptCount == adopted,
        "failed wire transfer does not adopt Controller Pak");
    fixture.AssertAssociation(beforeTransfer,
        "failed transfer does not advance requested path or persistence");

    fixture.Bulk.FailUpload = false;
    fixture.Files.FailWrite = true;
    int uploads = fixture.Bulk.UploadCount;
    BridgeControllerPakContentResult write = await fixture.Service.CreateAsync("write-fail.mpk");
    Assert(write.Failure == BridgeControllerPakContentFailure.FileWriteFailure,
        "atomic write failure is typed");
    Assert(fixture.Bulk.UploadCount == uploads + 1,
        "create writes only after verified destructive upload");
    Assert(write.Snapshot.Path is null && !write.Snapshot.HasAssociatedFile &&
        write.Snapshot.ContentPresent && !fixture.Persistence.Current.Active,
        "create write failure does not commit the new path or retain a stale old association");
}

static async Task DirtyTransitionAndSaveAsAsync()
{
    Fixture fixture = new();
    await fixture.Service.CreateAsync("old.mpk");
    fixture.Persistence.MarkDirty();
    SnapshotPair cancelled = fixture.Capture();
    // Dialog cancellation occurs before invoking the semantic service.
    fixture.AssertAssociation(cancelled,
        "pre-service user cancellation is a lifecycle no-op");

    fixture.Files.Files[fixture.Files.NormalizePath("loaded.mpk")] = Filled(0x44);
    BridgeControllerPakContentResult transition =
        await fixture.Service.LoadAsync("loaded.mpk");
    Assert(transition.Success && fixture.Persistence.FlushCount > 0,
        "dirty prior lifecycle is flushed before successful transition");
    Assert(fixture.Persistence.Current.Path == fixture.Files.NormalizePath("loaded.mpk"),
        "successful dirty transition activates the new association");

    fixture.Persistence.MarkDirty();
    fixture.Bulk.DownloadImage = Filled(0x77);
    BridgeControllerPakContentResult save = await fixture.Service.SaveAsync("save-as.mpk");
    string newPath = fixture.Files.NormalizePath("save-as.mpk");
    Assert(save.Success && save.Snapshot.Path == newPath &&
        fixture.Persistence.Current.Path == newPath &&
        fixture.Files.Files[newPath].All(value => value == 0x77),
        "dirty Save As commits file, content path, and persistence path together");

    fixture.Persistence.MarkDirty();
    fixture.Files.FailWrite = true;
    SnapshotPair beforeFailure = fixture.Capture();
    BridgeControllerPakContentResult failed = await fixture.Service.SaveAsync("failed-save.mpk");
    Assert(failed.Failure == BridgeControllerPakContentFailure.FileWriteFailure,
        "Save As write failure is typed");
    fixture.AssertAssociation(beforeFailure,
        "failed Save As preserves the previous authoritative association");
}

static async Task AdoptionAndSuspensionAsync()
{
    Fixture fixture = new();
    fixture.Accessory.Bulk = fixture.Bulk;
    await fixture.Service.CreateAsync(null);
    Assert(fixture.Accessory.AdoptObservedAfterUpload,
        "accessory adoption occurs only after verified upload completion");
    Assert(fixture.Input.SuspendCount == fixture.Input.ResumeCount &&
        !fixture.Input.Suspended, "successful operation balances input suspension");

    fixture.Files.Files[fixture.Files.NormalizePath("fail.mpk")] = Filled(0x22);
    fixture.Bulk.FailUpload = true;
    await fixture.Service.LoadAsync("fail.mpk");
    Assert(fixture.Input.SuspendCount == fixture.Input.ResumeCount &&
        !fixture.Input.Suspended, "failed transfer balances input suspension");

    fixture.Bulk.FailUpload = false;
    fixture.Accessory.FailAdoption = true;
    BridgeControllerPakContentResult adoption =
        await fixture.Service.CreateAsync("not-associated.mpk");
    Assert(adoption.Failure ==
        BridgeControllerPakContentFailure.AccessoryAdoptionFailure,
        "accessory adapter failure is typed");
    Assert(adoption.Snapshot.Path is null && !fixture.Persistence.Current.Active,
        "post-wire adoption failure clears the now-stale prior association");
    Assert(fixture.Input.SuspendCount == fixture.Input.ResumeCount,
        "adoption failure balances input suspension");
}

static async Task PersistenceActivationFailuresAsync()
{
    Fixture save = new();
    await save.Service.CreateAsync("save-old.mpk");
    save.Persistence.MarkDirty();
    SnapshotPair oldSave = save.Capture();
    string saveDestination = save.Files.NormalizePath("save-activation-fail.mpk");
    save.Persistence.FailActivationPath = saveDestination;
    BridgeControllerPakContentResult saveResult =
        await save.Service.SaveAsync(saveDestination);
    Assert(saveResult.Failure == BridgeControllerPakContentFailure.PersistenceFailure,
        "Save As activation failure returns typed PersistenceFailure");
    save.AssertAssociation(oldSave,
        "Save As activation failure safely restores the old association");
    Assert(save.Persistence.Current.Active &&
        save.Persistence.Current.Path == oldSave.Persistence.Path,
        "Save As rollback restores active old persistence ownership");
    Assert(save.Files.Files.ContainsKey(saveDestination),
        "successful atomic destination write may remain after activation failure");
    Assert(save.Input.SuspendCount == save.Input.ResumeCount && !save.Input.Suspended,
        "Save As activation failure balances controller suspension");

    Fixture unrestorableSave = new();
    await unrestorableSave.Service.CreateAsync("unrestorable-old.mpk");
    unrestorableSave.Persistence.MarkDirty();
    unrestorableSave.Persistence.FailAllActivations = true;
    BridgeControllerPakContentResult unrestorableResult =
        await unrestorableSave.Service.SaveAsync("unrestorable-new.mpk");
    Assert(unrestorableResult.Failure ==
        BridgeControllerPakContentFailure.PersistenceFailure,
        "unrestorable Save As activation failure remains typed");
    Assert(unrestorableResult.Snapshot.ContentPresent &&
        unrestorableResult.Snapshot.Path is null &&
        !unrestorableResult.Snapshot.HasAssociatedFile &&
        !unrestorableSave.Persistence.Current.Active,
        "failed Save As rollback falls back to truthful unassociated state");

    Fixture create = new();
    await create.Service.CreateAsync("create-old.mpk");
    string createDestination = create.Files.NormalizePath("create-activation-fail.mpk");
    create.Persistence.FailActivationPath = createDestination;
    BridgeControllerPakContentResult createResult =
        await create.Service.CreateAsync(createDestination);
    Assert(createResult.Failure == BridgeControllerPakContentFailure.PersistenceFailure,
        "Create activation failure returns typed PersistenceFailure");
    Assert(createResult.Snapshot.ContentPresent && createResult.Snapshot.Path is null &&
        !createResult.Snapshot.HasAssociatedFile && !create.Persistence.Current.Active,
        "Create activation failure records changed device content as unassociated");
    Assert(create.Files.Files.ContainsKey(createDestination),
        "Create activation failure does not treat destination existence as association");
    Assert(create.Input.SuspendCount == create.Input.ResumeCount && !create.Input.Suspended,
        "Create activation failure balances controller suspension");

    Fixture load = new();
    await load.Service.CreateAsync("load-old.mpk");
    string loadPath = load.Files.NormalizePath("load-activation-fail.mpk");
    load.Files.Files[loadPath] = Filled(0x6C);
    load.Persistence.FailActivationPath = loadPath;
    BridgeControllerPakContentResult loadResult = await load.Service.LoadAsync(loadPath);
    Assert(loadResult.Failure == BridgeControllerPakContentFailure.PersistenceFailure,
        "Load activation failure returns typed PersistenceFailure");
    Assert(loadResult.Snapshot.ContentPresent && loadResult.Snapshot.Path is null &&
        !loadResult.Snapshot.HasAssociatedFile && !load.Persistence.Current.Active,
        "Load activation failure records replaced device content as unassociated");
    Assert(load.Input.SuspendCount == load.Input.ResumeCount && !load.Input.Suspended,
        "Load activation failure balances controller suspension");
}

static async Task BusyAndCancellationAsync()
{
    Fixture fixture = new();
    fixture.Bulk.BlockUploads = true;
    Task<BridgeControllerPakContentResult> first = fixture.Service.CreateAsync(null);
    await fixture.Bulk.UploadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    BridgeControllerPakContentResult busy = await fixture.Service.CreateAsync(null);
    Assert(busy.Failure == BridgeControllerPakContentFailure.Busy,
        "concurrent operation returns typed Busy");
    fixture.Bulk.ReleaseUpload.TrySetResult();
    Assert((await first).Success, "serialized owner completes after Busy peer");

    Fixture cancelled = new();
    using CancellationTokenSource stop = new();
    stop.Cancel();
    BridgeControllerPakContentResult result =
        await cancelled.Service.CreateAsync("never.mpk", cancellationToken: stop.Token);
    Assert(result.Failure == BridgeControllerPakContentFailure.Cancelled,
        "pre-cancelled operation returns typed Cancelled");
    Assert(cancelled.Bulk.UploadCount == 0 && cancelled.Persistence.FlushCount == 0 &&
        cancelled.Files.WriteCount == 0 && cancelled.Accessory.AdoptCount == 0,
        "pre-cancelled operation performs no persistence or destructive work");

    Fixture blocked = new();
    blocked.Persistence.FailFlush = true;
    BridgeControllerPakContentResult persistence =
        await blocked.Service.CreateAsync(null);
    Assert(persistence.Failure == BridgeControllerPakContentFailure.PersistenceFailure &&
        blocked.Bulk.UploadCount == 0,
        "persistence failure blocks destructive work with a typed result");
}

static byte[] Filled(byte value)
{
    byte[] image = new byte[32768];
    Array.Fill(image, value);
    return image;
}

static byte IndexChecksum(byte[] image, int tableOffset)
{
    int start = tableOffset == 0x100 ? tableOffset + 10 : tableOffset + 2;
    int sum = 0;
    for (int offset = start; offset < tableOffset + 0x100; offset++)
        sum = (sum + image[offset]) & 0xFF;
    return (byte)sum;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
}

sealed class Fixture
{
    public FakeFiles Files { get; } = new();
    public FakeBulk Bulk { get; } = new();
    public FakeAccessory Accessory { get; } = new();
    public FakeInputGuard Input { get; } = new();
    public FakePersistence Persistence { get; } = new();
    public BridgeControllerPakContentService Service { get; }

    public Fixture() => Service = new(Files, Bulk, Accessory, Input, Persistence);

    public SnapshotPair Capture() => new(Service.Current, Persistence.Current);

    public void AssertAssociation(SnapshotPair expected, string message)
    {
        BridgeControllerPakContentSnapshot actual = Service.Current;
        AccessoryPersistenceSnapshot persisted = Persistence.Current;
        if (!(actual.Path == expected.Content.Path &&
            actual.HasAssociatedFile == expected.Content.HasAssociatedFile &&
            actual.ContentPresent == expected.Content.ContentPresent &&
            persisted.Path == expected.Persistence.Path &&
            persisted.Active == expected.Persistence.Active))
            throw new InvalidOperationException("FAIL: " + message);
    }
}

sealed record SnapshotPair(BridgeControllerPakContentSnapshot Content,
    AccessoryPersistenceSnapshot Persistence);

sealed class FakeFiles : IBridgeControllerPakFileSystem
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FailRead { get; set; }
    public bool FailWrite { get; set; }
    public int WriteCount { get; private set; }

    public string NormalizePath(string path) => Path.GetFullPath(path);

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailRead) throw new IOException("read failed");
        if (!Files.TryGetValue(path, out byte[]? data)) throw new FileNotFoundException(path);
        return Task.FromResult(data.ToArray());
    }

    public Task WriteVerifiedAsync(string path, byte[] data, int expectedLength,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteCount++;
        if (FailWrite) throw new IOException("atomic write failed");
        if (data.Length != expectedLength) throw new InvalidDataException();
        Files[path] = data.ToArray();
        return Task.CompletedTask;
    }
}

sealed class FakeBulk : IBridgeControllerPakBulkPort
{
    public bool IsConnected { get; set; } = true;
    public bool FailUpload { get; set; }
    public bool BlockUploads { get; set; }
    public int UploadCount { get; private set; }
    public int DownloadCount { get; private set; }
    public byte[]? LastUploaded { get; private set; }
    public byte[] DownloadImage { get; set; } = FilledForFake(0xA5);
    public TaskCompletionSource UploadEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseUpload { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task UploadAsync(byte[] image, Action<BridgeBulkProgress>? progress,
        CancellationToken cancellationToken)
    {
        UploadCount++;
        if (FailUpload) throw new IOException("upload failed");
        UploadEntered.TrySetResult();
        if (BlockUploads) await ReleaseUpload.Task.WaitAsync(cancellationToken);
        LastUploaded = image.ToArray();
        progress?.Invoke(new("ControllerPakUpload", image.Length, image.Length, 1024, 1024));
    }

    public Task<byte[]> DownloadAsync(Action<BridgeBulkProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DownloadCount++;
        progress?.Invoke(new("ControllerPakDownload", 32768, 32768, 1024, 1024));
        return Task.FromResult(DownloadImage.ToArray());
    }

    public Task<uint?> QueryAccessoryWriteCounterAsync(CancellationToken cancellationToken) =>
        Task.FromResult<uint?>(42);

    private static byte[] FilledForFake(byte value)
    {
        byte[] image = new byte[32768];
        Array.Fill(image, value);
        return image;
    }
}

sealed class FakeAccessory : IBridgeControllerPakAccessoryPort
{
    public FakeBulk? Bulk { get; set; }
    public int AdoptCount { get; private set; }
    public bool AdoptObservedAfterUpload { get; private set; }
    public bool FailAdoption { get; set; }

    public void AdoptVerifiedControllerPak()
    {
        if (FailAdoption) throw new InvalidOperationException("adoption failed");
        AdoptCount++;
        AdoptObservedAfterUpload = Bulk is null || Bulk.LastUploaded is not null;
    }
}

sealed class FakeInputGuard : IBridgeControllerPakInputGuard
{
    public int SuspendCount { get; private set; }
    public int ResumeCount { get; private set; }
    public bool Suspended { get; private set; }

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
    private Func<CancellationToken, Task<byte[]>>? _reader;
    public int FlushCount { get; private set; }
    public bool FailFlush { get; set; }
    public string? FailActivationPath { get; set; }
    public bool FailAllActivations { get; set; }
    public AccessoryPersistenceSnapshot Current { get; private set; } = Empty();

    public void Activate(AccessoryPersistenceKind kind, string path, int expectedLength,
        Func<CancellationToken, Task<byte[]>> readCurrent, uint initialWriteCounter)
    {
        if (FailAllActivations || string.Equals(Path.GetFullPath(path), FailActivationPath,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("activation failed");
        if (Current.Active && Current.Dirty) throw new InvalidOperationException("dirty");
        _reader = readCurrent;
        Current = new(kind, Path.GetFullPath(path), expectedLength, true, false,
            false, 0, null, string.Empty);
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
        if (FailFlush)
            return Task.FromResult(new AccessoryPersistenceResult(
                false, Current.Dirty, "persistence blocked"));
        bool dirty = Current.Dirty;
        Current = Current with { Dirty = false };
        return Task.FromResult(new AccessoryPersistenceResult(true, dirty, string.Empty));
    }

    public bool TryDeactivateClean()
    {
        if (Current.Dirty || Current.FlushInProgress) return false;
        _reader = null;
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
    public sealed record BridgeBulkProgress(
        string Phase,
        int CompletedBytes,
        int TotalBytes,
        int ItemIndex,
        int ItemCount);
}
