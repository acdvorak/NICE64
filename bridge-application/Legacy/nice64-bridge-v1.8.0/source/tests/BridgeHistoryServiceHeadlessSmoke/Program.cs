using System.Collections;
using System.Text.Json;
using PicoSwitchController;

await RunAsync();

static async Task RunAsync()
{
    string root = Path.Combine(@"D:\NICE64\history-v1", ".history-service-tests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        await MissingAndRoundTripAsync(root);
        await RecordNormalizeAndDeduplicateAsync(root);
        await RetentionAsync(root);
        await RemoveAndClearAsync(root);
        await MissingFilesRemainAsync(root);
        await AtomicReplacementAndWriteFailureAsync(root);
        CorruptionRecovery(root);
        FailedQuarantineBlocksWrites();
        UnsupportedSchema(root);
        await ConcurrentMutationsAsync();
        await ImmutableSnapshotsAsync();
        Console.WriteLine("PASS: BridgeHistoryService headless smoke (24 requirements)");
    }
    finally
    {
        string fullRoot = Path.GetFullPath(root);
        string allowedRoot = Path.GetFullPath(@"D:\NICE64\history-v1\.history-service-tests");
        if (fullRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullRoot))
            Directory.Delete(fullRoot, recursive: true);
    }
}

static async Task MissingAndRoundTripAsync(string root)
{
    string directory = NewDirectory(root, "roundtrip");
    string jsonPath = Path.Combine(directory, "history.json");
    BridgeHistoryService missing = new(new BridgeHistoryJsonStore(jsonPath));
    Assert(missing.InitialLoad.Success && missing.Current.SchemaVersion == 1 &&
        missing.Current.ControllerPak.Count == 0 && missing.Current.TransferPak.Count == 0,
        "missing history.json loads as empty Schema v1");

    await missing.RecordControllerPakAsync(Path.Combine(directory, "Stadium.mpk"));
    await missing.RecordTransferPakAsync(Path.Combine(directory, "Pokemon Gold.gb"));
    BridgeHistoryService reloaded = new(new BridgeHistoryJsonStore(jsonPath));
    Assert(reloaded.InitialLoad.Success && reloaded.Current.ControllerPak.Count == 1 &&
        reloaded.Current.TransferPak.Count == 1, "Schema v1 round-trips");

    using JsonDocument json = JsonDocument.Parse(File.ReadAllText(jsonPath));
    JsonElement rootElement = json.RootElement;
    Assert(rootElement.GetProperty("schemaVersion").GetInt32() == 1 &&
        rootElement.GetProperty("controllerPak")[0].TryGetProperty("path", out _) &&
        rootElement.GetProperty("transferPak")[0].TryGetProperty("romPath", out _),
        "persisted JSON uses the approved Schema v1 property shape");
}

static async Task RecordNormalizeAndDeduplicateAsync(string root)
{
    string directory = NewDirectory(root, "dedupe");
    ManualTimeProvider clock = new(new DateTimeOffset(2026, 9, 3, 1, 20, 0,
        TimeSpan.Zero));
    BridgeHistoryService service = new(new BridgeHistoryJsonStore(
        Path.Combine(directory, "history.json")), timeProvider: clock);

    string first = Path.Combine(directory, "folder", "..", "Stadium.mpk");
    BridgeHistoryResult controller = await service.RecordControllerPakAsync(first);
    Assert(controller.Success && service.Current.ControllerPak.Count == 1,
        "Controller Pak path records successfully");
    Assert(service.Current.ControllerPak[0].Path == Path.GetFullPath(first),
        "Controller Pak path is normalized with Path.GetFullPath");

    clock.UtcNow = clock.UtcNow.AddMinutes(5);
    string second = Path.Combine(directory, "Second.mpk");
    await service.RecordControllerPakAsync(second);
    clock.UtcNow = clock.UtcNow.AddMinutes(5);
    string sameDifferentCase = Path.GetFullPath(first).ToUpperInvariant();
    await service.RecordControllerPakAsync(sameDifferentCase);
    Assert(service.Current.ControllerPak.Count == 2 &&
        string.Equals(service.Current.ControllerPak[0].Path, sameDifferentCase,
            StringComparison.Ordinal) &&
        service.Current.ControllerPak[0].LastUsedUtc == clock.UtcNow,
        "case-insensitive duplicate updates timestamp and moves to index zero");

    BridgeHistoryResult transfer = await service.RecordTransferPakAsync(
        Path.Combine(directory, "Pokemon Crystal.gbc"));
    Assert(transfer.Success && service.Current.TransferPak.Count == 1 &&
        service.Current.ControllerPak.Count == 2,
        "Transfer Pak records independently from Controller Pak");

    BridgeHistoryResult invalidController = await service.RecordControllerPakAsync(
        Path.Combine(directory, "not-a-pak.gb"));
    BridgeHistoryResult invalidTransfer = await service.RecordTransferPakAsync(
        Path.Combine(directory, "not-a-rom.sav"));
    Assert(invalidController.Failure == BridgeHistoryFailure.InvalidPath &&
        invalidTransfer.Failure == BridgeHistoryFailure.InvalidPath,
        "category extensions are enforced without adding SAV History records");
}

static async Task RetentionAsync(string root)
{
    string directory = NewDirectory(root, "retention");
    ManualTimeProvider clock = new(DateTimeOffset.UnixEpoch);
    BridgeHistoryService service = new(new BridgeHistoryJsonStore(
        Path.Combine(directory, "history.json")), new BridgeHistoryOptions(), clock);
    for (int i = 0; i < 23; i++)
    {
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        await service.RecordControllerPakAsync(Path.Combine(directory, $"pak-{i:D2}.mpk"));
        await service.RecordTransferPakAsync(Path.Combine(directory, $"rom-{i:D2}.gbc"));
    }
    Assert(service.Current.ControllerPak.Count == 20 &&
        service.Current.ControllerPak[0].Path.EndsWith("pak-22.mpk") &&
        service.Current.ControllerPak[^1].Path.EndsWith("pak-03.mpk"),
        "Controller Pak retention is 20, most-recent-first");
    Assert(service.Current.TransferPak.Count == 20 &&
        service.Current.TransferPak[0].RomPath.EndsWith("rom-22.gbc") &&
        service.Current.TransferPak[^1].RomPath.EndsWith("rom-03.gbc"),
        "Transfer Pak retention is 20, most-recent-first");
}

static async Task RemoveAndClearAsync(string root)
{
    string directory = NewDirectory(root, "clear");
    BridgeHistoryService service = new(new BridgeHistoryJsonStore(
        Path.Combine(directory, "history.json")));
    string cp1 = Path.Combine(directory, "one.mpk");
    string cp2 = Path.Combine(directory, "two.mpk");
    string tp1 = Path.Combine(directory, "one.gb");
    string tp2 = Path.Combine(directory, "two.gbc");
    await service.RecordControllerPakAsync(cp1);
    await service.RecordControllerPakAsync(cp2);
    await service.RecordTransferPakAsync(tp1);
    await service.RecordTransferPakAsync(tp2);

    BridgeHistoryResult removed = await service.RemoveAsync(
        BridgeHistoryCategory.ControllerPak, cp1);
    Assert(removed.Success && service.Current.ControllerPak.Count == 1 &&
        service.Current.TransferPak.Count == 2,
        "remove deletes exactly one matching category entry");

    await service.ClearAsync(BridgeHistoryCategory.ControllerPak);
    Assert(service.Current.ControllerPak.Count == 0 &&
        service.Current.TransferPak.Count == 2,
        "clear Controller Pak leaves Transfer Pak unchanged");
    await service.RecordControllerPakAsync(cp1);
    await service.ClearAsync(BridgeHistoryCategory.TransferPak);
    Assert(service.Current.ControllerPak.Count == 1 &&
        service.Current.TransferPak.Count == 0,
        "clear Transfer Pak leaves Controller Pak unchanged");
    await service.RecordTransferPakAsync(tp1);
    await service.ClearAllAsync();
    Assert(service.Current.ControllerPak.Count == 0 &&
        service.Current.TransferPak.Count == 0,
        "clear all clears both categories only");
}

static async Task MissingFilesRemainAsync(string root)
{
    string directory = NewDirectory(root, "missing-files");
    string jsonPath = Path.Combine(directory, "history.json");
    string missingPak = Path.Combine(directory, "offline-drive", "missing.mpk");
    string missingRom = Path.Combine(directory, "offline-drive", "missing.gbc");
    BridgeHistoryService service = new(new BridgeHistoryJsonStore(jsonPath));
    await service.RecordControllerPakAsync(missingPak);
    await service.RecordTransferPakAsync(missingRom);
    Assert(!File.Exists(missingPak) && !File.Exists(missingRom),
        "test History targets are genuinely absent");
    BridgeHistoryService reloaded = new(new BridgeHistoryJsonStore(jsonPath));
    Assert(reloaded.Current.ControllerPak.Count == 1 &&
        reloaded.Current.TransferPak.Count == 1,
        "missing referenced files are retained and startup does not prune");
}

static async Task AtomicReplacementAndWriteFailureAsync(string root)
{
    string directory = NewDirectory(root, "atomic");
    string jsonPath = Path.Combine(directory, "history.json");
    BridgeHistoryService real = new(new BridgeHistoryJsonStore(jsonPath));
    await real.RecordControllerPakAsync(Path.Combine(directory, "first.mpk"));
    await real.RecordControllerPakAsync(Path.Combine(directory, "second.mpk"));
    Assert(File.Exists(jsonPath) &&
        Directory.GetFiles(directory, ".history.json.*.tmp").Length == 0,
        "successful atomic replacement leaves one authoritative JSON and no temp file");
    using (JsonDocument.Parse(File.ReadAllText(jsonPath))) { }

    MemoryHistoryStore memory = new();
    BridgeHistoryService service = new(memory);
    await service.RecordControllerPakAsync(@"D:\Paks\first.mpk");
    string persistedBefore = memory.Text!;
    BridgeHistorySnapshot snapshotBefore = service.Current;
    memory.FailWrite = true;
    BridgeHistoryResult failed = await service.RecordControllerPakAsync(
        @"D:\Paks\second.mpk");
    Assert(!failed.Success && failed.Failure == BridgeHistoryFailure.PersistenceFailure &&
        memory.Text == persistedBefore &&
        service.Current.ControllerPak.SequenceEqual(snapshotBefore.ControllerPak) &&
        service.Current.TransferPak.SequenceEqual(snapshotBefore.TransferPak),
        "simulated write failure preserves prior disk and in-memory authority");
}

static void CorruptionRecovery(string root)
{
    string directory = NewDirectory(root, "corruption");
    string jsonPath = Path.Combine(directory, "history.json");
    const string damaged = "{ definitely not valid JSON";
    File.WriteAllText(jsonPath, damaged);
    List<string> logs = [];
    BridgeHistoryService service = new(new BridgeHistoryJsonStore(jsonPath),
        log: logs.Add);
    Assert(!service.InitialLoad.Success &&
        service.InitialLoad.Failure == BridgeHistoryFailure.CorruptData &&
        service.Current.ControllerPak.Count == 0 && service.Current.TransferPak.Count == 0,
        "malformed JSON is detected and startup recovers to empty Schema v1");
    Assert(!File.Exists(jsonPath) && service.InitialLoad.PreservedCorruptPath is not null &&
        File.ReadAllText(service.InitialLoad.PreservedCorruptPath) == damaged && logs.Count == 1,
        "malformed JSON is preserved in a timestamped quarantine file");
}

static void FailedQuarantineBlocksWrites()
{
    MemoryHistoryStore store = new()
    {
        Text = "{ broken",
        FailPreserve = true
    };
    BridgeHistoryService service = new(store);
    Assert(!service.InitialLoad.Success &&
        service.InitialLoad.Failure == BridgeHistoryFailure.PersistenceFailure &&
        store.Text == "{ broken",
        "failed quarantine preserves the only damaged source");
    BridgeHistoryResult result = service.RecordControllerPakAsync(
        @"D:\Paks\blocked.mpk").GetAwaiter().GetResult();
    Assert(!result.Success && result.Failure == BridgeHistoryFailure.PersistenceFailure &&
        store.WriteCount == 0 && store.Text == "{ broken",
        "failed quarantine blocks later writes from overwriting the damaged source");
}

static void UnsupportedSchema(string root)
{
    string directory = NewDirectory(root, "schema");
    string jsonPath = Path.Combine(directory, "history.json");
    const string future = """
        {
          "schemaVersion": 2,
          "controllerPak": [],
          "transferPak": []
        }
        """;
    File.WriteAllText(jsonPath, future);
    BridgeHistoryService service = new(new BridgeHistoryJsonStore(jsonPath));
    Assert(!service.InitialLoad.Success &&
        service.InitialLoad.Failure == BridgeHistoryFailure.UnsupportedSchemaVersion &&
        service.InitialLoad.PreservedCorruptPath is not null &&
        File.ReadAllText(service.InitialLoad.PreservedCorruptPath) == future,
        "unsupported schema is reported truthfully and preserved without reinterpretation");
}

static async Task ConcurrentMutationsAsync()
{
    MemoryHistoryStore store = new();
    BridgeHistoryService service = new(store,
        new BridgeHistoryOptions(ControllerPakLimit: 100, TransferPakLimit: 100));
    Task<BridgeHistoryResult>[] tasks = Enumerable.Range(0, 80)
        .Select(index => service.RecordControllerPakAsync(
            $@"D:\Paks\concurrent-{index:D2}.mpk"))
        .ToArray();
    BridgeHistoryResult[] results = await Task.WhenAll(tasks);
    Assert(results.All(result => result.Success) &&
        service.Current.ControllerPak.Count == 80 &&
        service.Current.ControllerPak.Select(entry => entry.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 80,
        "concurrent mutations serialize without lost or duplicate entries");
    BridgeHistoryService reloaded = new(new MemoryHistoryStore { Text = store.Text });
    Assert(reloaded.InitialLoad.Success && reloaded.Current.ControllerPak.Count == 20,
        "concurrently persisted JSON remains valid and obeys reader retention options");
}

static async Task ImmutableSnapshotsAsync()
{
    BridgeHistoryService service = new(new MemoryHistoryStore());
    await service.RecordControllerPakAsync(@"D:\Paks\immutable.mpk");
    BridgeHistorySnapshot snapshot = service.Current;
    bool rejected = false;
    try
    {
        ((IList)snapshot.ControllerPak).Add(
            new BridgeControllerPakHistoryEntry(@"D:\Paks\injected.mpk",
                DateTimeOffset.UtcNow));
    }
    catch (NotSupportedException)
    {
        rejected = true;
    }
    Assert(rejected && service.Current.ControllerPak.Count == 1,
        "returned snapshot collections cannot mutate authoritative state");
}

static string NewDirectory(string root, string name)
{
    string path = Path.Combine(root, name);
    Directory.CreateDirectory(path);
    return path;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

sealed class MemoryHistoryStore : IBridgeHistoryStore
{
    public string Path => @"D:\NICE64\history-v1\.history-service-tests\memory-history.json";
    public bool Exists => Text is not null;
    public string? Text { get; set; }
    public bool FailWrite { get; set; }
    public bool FailPreserve { get; set; }
    public int WriteCount { get; private set; }
    public string? Preserved { get; private set; }

    public string ReadAllText() => Text ?? throw new FileNotFoundException();

    public Task WriteAllTextAtomicallyAsync(string content,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWrite) throw new IOException("simulated write failure");
        WriteCount++;
        Text = content;
        return Task.CompletedTask;
    }

    public string PreserveCorruptFile(DateTimeOffset timestampUtc)
    {
        if (FailPreserve) throw new IOException("simulated quarantine failure");
        Preserved = Text;
        Text = null;
        return Path + ".corrupt-test";
    }
}
