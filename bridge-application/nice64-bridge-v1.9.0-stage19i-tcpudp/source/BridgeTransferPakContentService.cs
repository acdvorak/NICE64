namespace PicoSwitchController;

public enum BridgeTransferPakInstallKind
{
    None,
    FullFlash,
    LegacyFirst64K
}

public enum BridgeTransferPakContentOperation
{
    None,
    InspectRom,
    InstallFullRom,
    LoadLegacyRom,
    SaveCurrent
}

public enum BridgeTransferPakContentFailure
{
    None,
    Busy,
    Cancelled,
    NotConnected,
    InvalidPath,
    InvalidRom,
    RomTooLarge,
    FileReadFailure,
    FileWriteFailure,
    PersistenceFailure,
    CapacityQueryFailure,
    TransferFailure,
    AccessoryAdoptionFailure,
    NoCurrentContent,
    UnexpectedFailure
}

public enum BridgeTransferPakContentPhase
{
    ReadingRom,
    ParsingMetadata,
    PreparingSave,
    NormalizingSave,
    QueryingCapacity,
    FlushingPersistence,
    InstallingRom,
    UploadingSave,
    DownloadingSave,
    WritingSave,
    AdoptingAccessory,
    ActivatingPersistence,
    Completed
}

public sealed record BridgeTransferPakContentProgress(
    BridgeTransferPakContentOperation Operation,
    BridgeTransferPakContentPhase Phase,
    int CompletedBytes,
    int TotalBytes,
    int ItemIndex,
    int ItemCount);

public sealed record BridgeTransferPakRomInspection(
    string RomPath,
    string SavePath,
    int RomLength,
    long? ExistingSaveLength,
    GameBoyRomMetadata Metadata);

public sealed record BridgeTransferPakContentSnapshot(
    string? RomPath,
    string? SavePath,
    bool HasAssociatedRom,
    bool HasAssociatedSave,
    bool ContentPresent,
    BridgeTransferPakInstallKind InstallKind,
    int RomLength,
    int ExpectedSaveLength,
    GameBoyRomMetadata? Metadata,
    BridgeTransferPakContentOperation LastSuccessfulOperation,
    string LastError);

public sealed record BridgeTransferPakContentResult(
    bool Success,
    BridgeTransferPakContentFailure Failure,
    BridgeTransferPakContentOperation Operation,
    BridgeTransferPakContentSnapshot Snapshot,
    BridgeTransferPakRomInspection? Inspection,
    string Error);

public interface IBridgeTransferPakContentService
{
    BridgeTransferPakContentSnapshot Current { get; }
    Task<BridgeTransferPakContentResult> InspectRomAsync(string path,
        CancellationToken cancellationToken = default);
    Task<BridgeTransferPakContentResult> InstallRomAsync(string path,
        IProgress<BridgeTransferPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<BridgeTransferPakContentResult> SaveCurrentAsync(string? path = null,
        IProgress<BridgeTransferPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

internal interface IBridgeTransferPakLegacyContentService
{
    Task<BridgeTransferPakContentResult> LoadLegacyAsync(string path,
        IProgress<BridgeTransferPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IBridgeTransferPakFileSystem
{
    string NormalizePath(string path);
    bool FileExists(string path);
    long GetFileLength(string path);
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken);
    Task WriteVerifiedAsync(string path, byte[] data, int expectedLength,
        CancellationToken cancellationToken);
}

public sealed class BridgeTransferPakFileSystem(
    IAtomicPersistenceWriter? writer = null) : IBridgeTransferPakFileSystem
{
    private readonly IAtomicPersistenceWriter _writer = writer ?? new AtomicPersistenceWriter();

    public string NormalizePath(string path) => Path.GetFullPath(path);
    public bool FileExists(string path) => File.Exists(path);
    public long GetFileLength(string path) => new FileInfo(path).Length;
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(path, cancellationToken);
    public Task WriteVerifiedAsync(string path, byte[] data, int expectedLength,
        CancellationToken cancellationToken) =>
        _writer.WriteVerifiedAsync(path, data, expectedLength, cancellationToken);
}

public interface IBridgeTransferPakBulkPort
{
    bool IsConnected { get; }
    Task<int> QueryFlashCapacityAsync(CancellationToken cancellationToken);
    Task InstallFullAsync(byte[] rom, GameBoyTransferProfile profile, byte[] save,
        Action<BridgeBulkProgress>? progress, CancellationToken cancellationToken);
    Task InstallLegacyAsync(byte[] rom, GameBoyTransferProfile profile, byte[] save,
        Action<BridgeBulkProgress>? progress, CancellationToken cancellationToken);
    Task<byte[]> DownloadSaveAsync(int expectedLength,
        Action<BridgeBulkProgress>? progress, CancellationToken cancellationToken);
    Task<uint?> QueryAccessoryWriteCounterAsync(CancellationToken cancellationToken);
}

public interface IBridgeTransferPakAccessoryPort
{
    void AdoptVerifiedTransferPak();
}

public interface IBridgeTransferPakInputGuard
{
    void Suspend();
    Task ResumeAsync(CancellationToken cancellationToken);
}

public sealed class DelegateBridgeTransferPakBulkPort(
    Func<bool> isConnected,
    Func<CancellationToken, Task<int>> queryCapacity,
    Func<byte[], GameBoyTransferProfile, byte[], Action<BridgeBulkProgress>?,
        CancellationToken, Task> installFull,
    Func<byte[], GameBoyTransferProfile, byte[], Action<BridgeBulkProgress>?,
        CancellationToken, Task> installLegacy,
    Func<int, Action<BridgeBulkProgress>?, CancellationToken, Task<byte[]>> downloadSave,
    Func<CancellationToken, Task<uint?>> queryAccessoryWriteCounter)
    : IBridgeTransferPakBulkPort
{
    public bool IsConnected => isConnected();
    public Task<int> QueryFlashCapacityAsync(CancellationToken cancellationToken) =>
        queryCapacity(cancellationToken);
    public Task InstallFullAsync(byte[] rom, GameBoyTransferProfile profile, byte[] save,
        Action<BridgeBulkProgress>? progress, CancellationToken cancellationToken) =>
        installFull(rom, profile, save, progress, cancellationToken);
    public Task InstallLegacyAsync(byte[] rom, GameBoyTransferProfile profile, byte[] save,
        Action<BridgeBulkProgress>? progress, CancellationToken cancellationToken) =>
        installLegacy(rom, profile, save, progress, cancellationToken);
    public Task<byte[]> DownloadSaveAsync(int expectedLength,
        Action<BridgeBulkProgress>? progress, CancellationToken cancellationToken) =>
        downloadSave(expectedLength, progress, cancellationToken);
    public Task<uint?> QueryAccessoryWriteCounterAsync(CancellationToken cancellationToken) =>
        queryAccessoryWriteCounter(cancellationToken);
}

public sealed class DelegateBridgeTransferPakAccessoryPort(Action adopt)
    : IBridgeTransferPakAccessoryPort
{
    public void AdoptVerifiedTransferPak() => adopt();
}

public sealed class DelegateBridgeTransferPakInputGuard(
    Action suspend, Func<CancellationToken, Task> resume) : IBridgeTransferPakInputGuard
{
    public void Suspend() => suspend();
    public Task ResumeAsync(CancellationToken cancellationToken) => resume(cancellationToken);
}

public sealed class BridgeTransferPakContentService : IBridgeTransferPakContentService,
    IBridgeTransferPakLegacyContentService
{
    public const int MinimumRomLength = 65536;
    public const int MaximumSupportedRomLength = 2 * 1024 * 1024;
    public const int ExpectedSaveLength = 32768;

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IBridgeTransferPakFileSystem _files;
    private readonly IBridgeTransferPakBulkPort _bulk;
    private readonly IBridgeTransferPakAccessoryPort _accessory;
    private readonly IBridgeTransferPakInputGuard _input;
    private readonly IAccessoryPersistenceService _persistence;
    private readonly Action<string>? _log;
    private BridgeTransferPakContentSnapshot _current = Empty();

    public BridgeTransferPakContentService(
        IBridgeTransferPakFileSystem files,
        IBridgeTransferPakBulkPort bulk,
        IBridgeTransferPakAccessoryPort accessory,
        IBridgeTransferPakInputGuard input,
        IAccessoryPersistenceService persistence,
        Action<string>? log = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _bulk = bulk ?? throw new ArgumentNullException(nameof(bulk));
        _accessory = accessory ?? throw new ArgumentNullException(nameof(accessory));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _log = log;
    }

    public BridgeTransferPakContentSnapshot Current
    {
        get { lock (_stateLock) return _current; }
    }

    public Task<BridgeTransferPakContentResult> InspectRomAsync(string path,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(BridgeTransferPakContentOperation.InspectRom,
            requiresConnection: false, async token =>
            {
                RomWorkItem item = await ReadRomAsync(path,
                    BridgeTransferPakContentOperation.InspectRom, progress: null, token)
                    .ConfigureAwait(false);
                return SuccessInspection(item.Inspection);
            }, cancellationToken);

    public Task<BridgeTransferPakContentResult> InstallRomAsync(string path,
        IProgress<BridgeTransferPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(BridgeTransferPakContentOperation.InstallFullRom,
            requiresConnection: true, async token =>
            {
                RomWorkItem item = await ReadRomAsync(path,
                    BridgeTransferPakContentOperation.InstallFullRom, progress, token)
                    .ConfigureAwait(false);
                byte[] save = await PrepareSaveAsync(item.Inspection.SavePath,
                    BridgeTransferPakContentOperation.InstallFullRom, progress, token)
                    .ConfigureAwait(false);
                Report(progress, BridgeTransferPakContentOperation.InstallFullRom,
                    BridgeTransferPakContentPhase.QueryingCapacity);
                int capacity = await QueryCapacityAsync(token).ConfigureAwait(false);
                int supported = Math.Min(MaximumSupportedRomLength, capacity);
                if (item.Rom.Length > supported)
                    throw new TransferPakContentException(
                        BridgeTransferPakContentFailure.RomTooLarge,
                        $"This bridge supports Transfer Pak ROMs up to {supported / 1024}KB.");
                await FlushPersistenceAsync(BridgeTransferPakContentOperation.InstallFullRom,
                    progress, token).ConfigureAwait(false);

                bool destructiveStarted = false;
                bool verified = false;
                try
                {
                    await WithInputSuspendedAsync(async () =>
                    {
                        destructiveStarted = true;
                        Report(progress, BridgeTransferPakContentOperation.InstallFullRom,
                            BridgeTransferPakContentPhase.InstallingRom);
                        await InstallFullAsync(item, save, progress, token)
                            .ConfigureAwait(false);
                        verified = true;
                        Adopt(BridgeTransferPakContentOperation.InstallFullRom, progress);
                        await ReplaceSavePersistenceAsync(item.Inspection.SavePath,
                            BridgeTransferPakContentOperation.InstallFullRom, progress,
                            restorePreviousAssociation: false, token: token)
                            .ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                catch
                {
                    if (destructiveStarted)
                    {
                        _persistence.TryDeactivateClean();
                        if (verified) RecordInstalledUnassociated(item,
                            BridgeTransferPakInstallKind.FullFlash);
                        else RecordUnknownAfterDestructiveAttempt();
                    }
                    throw;
                }

                return CommitInstall(item, BridgeTransferPakInstallKind.FullFlash,
                    BridgeTransferPakContentOperation.InstallFullRom);
            }, cancellationToken);

    public Task<BridgeTransferPakContentResult> SaveCurrentAsync(string? path = null,
        IProgress<BridgeTransferPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(BridgeTransferPakContentOperation.SaveCurrent,
            requiresConnection: true, async token =>
            {
                BridgeTransferPakContentSnapshot previous = Current;
                if (!previous.ContentPresent || !previous.HasAssociatedSave ||
                    string.IsNullOrWhiteSpace(previous.SavePath))
                    throw new TransferPakContentException(
                        BridgeTransferPakContentFailure.NoCurrentContent,
                        "No associated Transfer Pak save is available.");
                string destination = string.IsNullOrWhiteSpace(path)
                    ? previous.SavePath
                    : NormalizeRequiredPath(path);
                await FlushPersistenceAsync(BridgeTransferPakContentOperation.SaveCurrent,
                    progress, token).ConfigureAwait(false);
                await WithInputSuspendedAsync(async () =>
                {
                    Report(progress, BridgeTransferPakContentOperation.SaveCurrent,
                        BridgeTransferPakContentPhase.DownloadingSave);
                    byte[] save = await DownloadSaveAsync(
                        BridgeTransferPakContentOperation.SaveCurrent, progress, token)
                        .ConfigureAwait(false);
                    ValidateSave(save);
                    Report(progress, BridgeTransferPakContentOperation.SaveCurrent,
                        BridgeTransferPakContentPhase.WritingSave);
                    await WriteFileAsync(destination, save, token).ConfigureAwait(false);
                    await ReplaceSavePersistenceAsync(destination,
                        BridgeTransferPakContentOperation.SaveCurrent, progress,
                        restorePreviousAssociation: true, token: token)
                        .ConfigureAwait(false);
                }).ConfigureAwait(false);
                return CommitSave(destination);
            }, cancellationToken);

    Task<BridgeTransferPakContentResult> IBridgeTransferPakLegacyContentService.LoadLegacyAsync(
        string path, IProgress<BridgeTransferPakContentProgress>? progress,
        CancellationToken cancellationToken) =>
        ExecuteAsync(BridgeTransferPakContentOperation.LoadLegacyRom,
            requiresConnection: true, async token =>
            {
                RomWorkItem item = await ReadRomAsync(path,
                    BridgeTransferPakContentOperation.LoadLegacyRom, progress, token)
                    .ConfigureAwait(false);
                byte[] save = await PrepareSaveAsync(item.Inspection.SavePath,
                    BridgeTransferPakContentOperation.LoadLegacyRom, progress, token)
                    .ConfigureAwait(false);
                await FlushPersistenceAsync(BridgeTransferPakContentOperation.LoadLegacyRom,
                    progress, token).ConfigureAwait(false);
                bool destructiveStarted = false;
                bool verified = false;
                try
                {
                    await WithInputSuspendedAsync(async () =>
                    {
                        destructiveStarted = true;
                        Report(progress, BridgeTransferPakContentOperation.LoadLegacyRom,
                            BridgeTransferPakContentPhase.InstallingRom);
                        await InstallLegacyAsync(item, save, progress, token)
                            .ConfigureAwait(false);
                        verified = true;
                        Adopt(BridgeTransferPakContentOperation.LoadLegacyRom, progress);
                        await ReplaceSavePersistenceAsync(item.Inspection.SavePath,
                            BridgeTransferPakContentOperation.LoadLegacyRom, progress,
                            restorePreviousAssociation: false, token: token)
                            .ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                catch
                {
                    if (destructiveStarted)
                    {
                        _persistence.TryDeactivateClean();
                        if (verified) RecordInstalledUnassociated(item,
                            BridgeTransferPakInstallKind.LegacyFirst64K);
                        else RecordUnknownAfterDestructiveAttempt();
                    }
                    throw;
                }
                return CommitInstall(item, BridgeTransferPakInstallKind.LegacyFirst64K,
                    BridgeTransferPakContentOperation.LoadLegacyRom);
            }, cancellationToken);

    private async Task<BridgeTransferPakContentResult> ExecuteAsync(
        BridgeTransferPakContentOperation operation, bool requiresConnection,
        Func<CancellationToken, Task<BridgeTransferPakContentResult>> action,
        CancellationToken token)
    {
        try
        {
            if (!await _operationGate.WaitAsync(0, token).ConfigureAwait(false))
                return Fail(operation, BridgeTransferPakContentFailure.Busy,
                    "Another Transfer Pak content operation is already in progress.");
        }
        catch (OperationCanceledException)
        {
            return Fail(operation, BridgeTransferPakContentFailure.Cancelled,
                "The Transfer Pak content operation was cancelled.");
        }
        try
        {
            token.ThrowIfCancellationRequested();
            if (requiresConnection && !_bulk.IsConnected)
                return Fail(operation, BridgeTransferPakContentFailure.NotConnected,
                    "The bridge control connection is not available.");
            return await action(token).ConfigureAwait(false);
        }
        catch (TransferPakContentException ex)
        {
            return Fail(operation, ex.Failure, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail(operation, BridgeTransferPakContentFailure.Cancelled,
                "The Transfer Pak content operation was cancelled.");
        }
        catch (Exception ex)
        {
            return Fail(operation, BridgeTransferPakContentFailure.UnexpectedFailure,
                ex.Message);
        }
        finally { _operationGate.Release(); }
    }

    private async Task<RomWorkItem> ReadRomAsync(string path,
        BridgeTransferPakContentOperation operation,
        IProgress<BridgeTransferPakContentProgress>? progress,
        CancellationToken token)
    {
        string normalized = NormalizeRequiredPath(path);
        Report(progress, operation, BridgeTransferPakContentPhase.ReadingRom);
        byte[] rom;
        try { rom = await _files.ReadAllBytesAsync(normalized, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.FileReadFailure, ex.Message, ex);
        }
        if (rom.Length < MinimumRomLength)
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.InvalidRom,
                "Transfer Pak ROM must be at least 64KB.");
        Report(progress, operation, BridgeTransferPakContentPhase.ParsingMetadata);
        GameBoyRomMetadata metadata = GameBoyRomMetadataParser.Parse(rom);
        string savePath = Path.ChangeExtension(normalized, ".sav");
        long? existingLength = null;
        try
        {
            if (_files.FileExists(savePath)) existingLength = _files.GetFileLength(savePath);
        }
        catch (Exception ex)
        {
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.FileReadFailure, ex.Message, ex);
        }
        return new(rom, new(normalized, savePath, rom.Length, existingLength, metadata));
    }

    private async Task<byte[]> PrepareSaveAsync(string path,
        BridgeTransferPakContentOperation operation,
        IProgress<BridgeTransferPakContentProgress>? progress,
        CancellationToken token)
    {
        Report(progress, operation, BridgeTransferPakContentPhase.PreparingSave);
        if (!_files.FileExists(path))
        {
            byte[] blank = new byte[ExpectedSaveLength];
            Array.Fill(blank, (byte)0xFF);
            await WriteFileAsync(path, blank, token).ConfigureAwait(false);
            _log?.Invoke($"TRANSFER SAVE CREATED path={path} bytes={blank.Length}");
            return blank;
        }
        byte[] existing;
        try { existing = await _files.ReadAllBytesAsync(path, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.FileReadFailure, ex.Message, ex);
        }
        if (existing.Length == ExpectedSaveLength) return existing;
        Report(progress, operation, BridgeTransferPakContentPhase.NormalizingSave);
        byte[] normalized = new byte[ExpectedSaveLength];
        Array.Fill(normalized, (byte)0xFF);
        Buffer.BlockCopy(existing, 0, normalized, 0,
            Math.Min(existing.Length, normalized.Length));
        await WriteFileAsync(path, normalized, token).ConfigureAwait(false);
        _log?.Invoke($"TRANSFER SAVE NORMALIZED path={path} " +
            $"fromBytes={existing.Length} toBytes={normalized.Length}");
        return normalized;
    }

    private async Task<int> QueryCapacityAsync(CancellationToken token)
    {
        try { return await _bulk.QueryFlashCapacityAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.CapacityQueryFailure, ex.Message, ex);
        }
    }

    private async Task InstallFullAsync(RomWorkItem item, byte[] save,
        IProgress<BridgeTransferPakContentProgress>? progress, CancellationToken token)
    {
        try
        {
            await _bulk.InstallFullAsync(item.Rom,
                item.Inspection.Metadata.ToFirmwareProfile(), save,
                bulk => ReportBulk(progress,
                    BridgeTransferPakContentOperation.InstallFullRom, bulk), token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.TransferFailure, ex.Message, ex);
        }
    }

    private async Task InstallLegacyAsync(RomWorkItem item, byte[] save,
        IProgress<BridgeTransferPakContentProgress>? progress, CancellationToken token)
    {
        try
        {
            await _bulk.InstallLegacyAsync(item.Rom,
                item.Inspection.Metadata.ToFirmwareProfile(), save,
                bulk => ReportBulk(progress,
                    BridgeTransferPakContentOperation.LoadLegacyRom, bulk), token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.TransferFailure, ex.Message, ex);
        }
    }

    private async Task<byte[]> DownloadSaveAsync(
        BridgeTransferPakContentOperation operation,
        IProgress<BridgeTransferPakContentProgress>? progress, CancellationToken token)
    {
        try
        {
            return await _bulk.DownloadSaveAsync(ExpectedSaveLength,
                bulk => ReportBulk(progress, operation, bulk), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.TransferFailure, ex.Message, ex);
        }
    }

    private async Task WriteFileAsync(string path, byte[] data, CancellationToken token)
    {
        try
        {
            await _files.WriteVerifiedAsync(path, data, ExpectedSaveLength, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.FileWriteFailure, ex.Message, ex);
        }
    }

    private async Task FlushPersistenceAsync(BridgeTransferPakContentOperation operation,
        IProgress<BridgeTransferPakContentProgress>? progress, CancellationToken token)
    {
        Report(progress, operation, BridgeTransferPakContentPhase.FlushingPersistence);
        AccessoryPersistenceResult result;
        try
        {
            result = operation is BridgeTransferPakContentOperation.InstallFullRom or
                BridgeTransferPakContentOperation.LoadLegacyRom
                ? await _persistence.FlushAsync(
                    AccessoryPersistenceFlushReason.RomReplacement, token)
                    .ConfigureAwait(false)
                : await _persistence.FlushAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.PersistenceFailure, ex.Message, ex);
        }
        if (!result.Success)
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.PersistenceFailure, result.Error);
    }

    private void Adopt(BridgeTransferPakContentOperation operation,
        IProgress<BridgeTransferPakContentProgress>? progress)
    {
        Report(progress, operation, BridgeTransferPakContentPhase.AdoptingAccessory);
        try { _accessory.AdoptVerifiedTransferPak(); }
        catch (Exception ex)
        {
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.AccessoryAdoptionFailure, ex.Message, ex);
        }
    }

    private async Task ReplaceSavePersistenceAsync(string path,
        BridgeTransferPakContentOperation operation,
        IProgress<BridgeTransferPakContentProgress>? progress,
        bool restorePreviousAssociation, CancellationToken token)
    {
        Report(progress, operation, BridgeTransferPakContentPhase.ActivatingPersistence);
        AccessoryPersistenceSnapshot previousPersistence = _persistence.Current;
        BridgeTransferPakContentSnapshot previousContent = Current;
        uint baseline = 0;
        bool baselineKnown = false;
        try
        {
            uint? value = await _bulk.QueryAccessoryWriteCounterAsync(token)
                .ConfigureAwait(false);
            if (value.HasValue) { baseline = value.Value; baselineKnown = true; }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        if (!_persistence.TryDeactivateClean())
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.PersistenceFailure,
                "The previous accessory persistence lifecycle is not clean.");
        try
        {
            _persistence.Activate(AccessoryPersistenceKind.GameBoySave, path,
                ExpectedSaveLength, ReadCurrentSaveForPersistenceAsync, baseline);
            if (!baselineKnown) _persistence.MarkDirty();
        }
        catch (Exception ex)
        {
            bool restored = restorePreviousAssociation &&
                TryRestorePreviousPersistence(previousContent, previousPersistence,
                    baseline, baselineKnown);
            if (!restored)
            {
                _persistence.TryDeactivateClean();
                ClearSaveAssociation();
            }
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.PersistenceFailure, ex.Message, ex);
        }
    }

    private bool TryRestorePreviousPersistence(
        BridgeTransferPakContentSnapshot content,
        AccessoryPersistenceSnapshot persistence,
        uint baseline, bool baselineKnown)
    {
        if (!content.ContentPresent || !content.HasAssociatedSave ||
            string.IsNullOrWhiteSpace(content.SavePath) || !persistence.Active ||
            persistence.Kind != AccessoryPersistenceKind.GameBoySave ||
            persistence.ExpectedLength != ExpectedSaveLength ||
            !string.Equals(content.SavePath, persistence.Path,
                StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            _persistence.Activate(AccessoryPersistenceKind.GameBoySave,
                persistence.Path, ExpectedSaveLength,
                ReadCurrentSaveForPersistenceAsync, baseline);
            if (!baselineKnown) _persistence.MarkDirty();
            return true;
        }
        catch { return false; }
    }

    private async Task<byte[]> ReadCurrentSaveForPersistenceAsync(CancellationToken token)
    {
        byte[]? save = null;
        await WithInputSuspendedAsync(async () =>
        {
            save = await DownloadSaveAsync(BridgeTransferPakContentOperation.SaveCurrent,
                progress: null, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        ValidateSave(save!);
        return save!;
    }

    private async Task WithInputSuspendedAsync(Func<Task> action)
    {
        _input.Suspend();
        try { await action().ConfigureAwait(false); }
        finally { await _input.ResumeAsync(CancellationToken.None).ConfigureAwait(false); }
    }

    private static void ValidateSave(byte[] save)
    {
        if (save.Length != ExpectedSaveLength)
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.TransferFailure,
                $"Expected a {ExpectedSaveLength} byte Transfer Pak save, got {save.Length} bytes.");
    }

    private string NormalizeRequiredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.InvalidPath, "A ROM path is required.");
        try { return _files.NormalizePath(path); }
        catch (Exception ex)
        {
            throw new TransferPakContentException(
                BridgeTransferPakContentFailure.InvalidPath, ex.Message, ex);
        }
    }

    private BridgeTransferPakContentResult SuccessInspection(
        BridgeTransferPakRomInspection inspection)
    {
        BridgeTransferPakContentSnapshot snapshot;
        lock (_stateLock)
        {
            _current = _current with
            {
                LastSuccessfulOperation = BridgeTransferPakContentOperation.InspectRom,
                LastError = string.Empty
            };
            snapshot = _current;
        }
        return new(true, BridgeTransferPakContentFailure.None,
            BridgeTransferPakContentOperation.InspectRom, snapshot, inspection, string.Empty);
    }

    private BridgeTransferPakContentResult CommitInstall(RomWorkItem item,
        BridgeTransferPakInstallKind kind, BridgeTransferPakContentOperation operation)
    {
        BridgeTransferPakContentSnapshot snapshot;
        lock (_stateLock)
        {
            _current = new(item.Inspection.RomPath, item.Inspection.SavePath,
                true, true, true, kind, item.Rom.Length, ExpectedSaveLength,
                item.Inspection.Metadata, operation, string.Empty);
            snapshot = _current;
        }
        return new(true, BridgeTransferPakContentFailure.None, operation,
            snapshot, item.Inspection, string.Empty);
    }

    private BridgeTransferPakContentResult CommitSave(string path)
    {
        BridgeTransferPakContentSnapshot snapshot;
        lock (_stateLock)
        {
            _current = _current with
            {
                SavePath = path,
                HasAssociatedSave = true,
                LastSuccessfulOperation = BridgeTransferPakContentOperation.SaveCurrent,
                LastError = string.Empty
            };
            snapshot = _current;
        }
        return new(true, BridgeTransferPakContentFailure.None,
            BridgeTransferPakContentOperation.SaveCurrent, snapshot, null, string.Empty);
    }

    private void RecordInstalledUnassociated(RomWorkItem item,
        BridgeTransferPakInstallKind kind)
    {
        lock (_stateLock)
            _current = new(null, null, false, false, true, kind, item.Rom.Length,
                ExpectedSaveLength, item.Inspection.Metadata,
                _current.LastSuccessfulOperation, _current.LastError);
    }

    private void RecordUnknownAfterDestructiveAttempt()
    {
        lock (_stateLock)
            _current = new(null, null, false, false, false,
                BridgeTransferPakInstallKind.None, 0, ExpectedSaveLength, null,
                _current.LastSuccessfulOperation, _current.LastError);
    }

    private void ClearSaveAssociation()
    {
        lock (_stateLock)
            _current = _current with { SavePath = null, HasAssociatedSave = false };
    }

    private BridgeTransferPakContentResult Fail(BridgeTransferPakContentOperation operation,
        BridgeTransferPakContentFailure failure, string error)
    {
        BridgeTransferPakContentSnapshot snapshot;
        lock (_stateLock)
        {
            _current = _current with { LastError = error };
            snapshot = _current;
        }
        return new(false, failure, operation, snapshot, null, error);
    }

    private static void Report(IProgress<BridgeTransferPakContentProgress>? progress,
        BridgeTransferPakContentOperation operation, BridgeTransferPakContentPhase phase) =>
        progress?.Report(new(operation, phase, 0, 0, 0, 0));

    private static void ReportBulk(IProgress<BridgeTransferPakContentProgress>? progress,
        BridgeTransferPakContentOperation operation, BridgeBulkProgress bulk)
    {
        BridgeTransferPakContentPhase phase = bulk.Phase switch
        {
            "GB_SAVE_UPLOAD" => BridgeTransferPakContentPhase.UploadingSave,
            "GB_SAVE_DOWNLOAD" => BridgeTransferPakContentPhase.DownloadingSave,
            _ => BridgeTransferPakContentPhase.InstallingRom
        };
        progress?.Report(new(operation, phase, bulk.CompletedBytes, bulk.TotalBytes,
            bulk.ItemIndex, bulk.ItemCount));
    }

    private static BridgeTransferPakContentSnapshot Empty() =>
        new(null, null, false, false, false, BridgeTransferPakInstallKind.None,
            0, ExpectedSaveLength, null, BridgeTransferPakContentOperation.None,
            string.Empty);

    private sealed record RomWorkItem(byte[] Rom, BridgeTransferPakRomInspection Inspection);

    private sealed class TransferPakContentException : Exception
    {
        public TransferPakContentException(BridgeTransferPakContentFailure failure,
            string message, Exception? inner = null) : base(message, inner) => Failure = failure;
        public BridgeTransferPakContentFailure Failure { get; }
    }
}
