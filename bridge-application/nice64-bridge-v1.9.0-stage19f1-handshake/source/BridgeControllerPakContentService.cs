namespace PicoSwitchController;

public enum BridgeControllerPakContentOperation
{
    None,
    Create,
    Load,
    Save
}

public enum BridgeControllerPakContentFailure
{
    None,
    Busy,
    Cancelled,
    NotConnected,
    InvalidPath,
    InvalidImage,
    FileReadFailure,
    FileWriteFailure,
    PersistenceFailure,
    TransferFailure,
    AccessoryAdoptionFailure,
    UnexpectedFailure
}

public enum BridgeControllerPakContentPhase
{
    Preparing,
    ReadingFile,
    FlushingPersistence,
    Uploading,
    Downloading,
    WritingFile,
    AdoptingAccessory,
    ActivatingPersistence,
    Completed
}

public sealed record BridgeControllerPakContentProgress(
    BridgeControllerPakContentOperation Operation,
    BridgeControllerPakContentPhase Phase,
    int CompletedBytes,
    int TotalBytes,
    int ItemIndex,
    int ItemCount);

public sealed record BridgeControllerPakContentSnapshot(
    string? Path,
    bool HasAssociatedFile,
    bool ContentPresent,
    int ImageSize,
    BridgeControllerPakContentOperation LastSuccessfulOperation,
    string LastError);

public sealed record BridgeControllerPakContentResult(
    bool Success,
    BridgeControllerPakContentFailure Failure,
    BridgeControllerPakContentOperation Operation,
    BridgeControllerPakContentSnapshot Snapshot,
    string Error);

public interface IBridgeControllerPakContentService
{
    BridgeControllerPakContentSnapshot Current { get; }
    Task<BridgeControllerPakContentResult> CreateAsync(string? path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<BridgeControllerPakContentResult> LoadAsync(string path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<BridgeControllerPakContentResult> SaveAsync(string path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IBridgeControllerPakFileSystem
{
    string NormalizePath(string path);
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken);
    Task WriteVerifiedAsync(string path, byte[] data, int expectedLength,
        CancellationToken cancellationToken);
}

public sealed class BridgeControllerPakFileSystem(
    IAtomicPersistenceWriter? writer = null) : IBridgeControllerPakFileSystem
{
    private readonly IAtomicPersistenceWriter _writer = writer ?? new AtomicPersistenceWriter();

    public string NormalizePath(string path) => System.IO.Path.GetFullPath(path);

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(path, cancellationToken);

    public Task WriteVerifiedAsync(string path, byte[] data, int expectedLength,
        CancellationToken cancellationToken) =>
        _writer.WriteVerifiedAsync(path, data, expectedLength, cancellationToken);
}

public interface IBridgeControllerPakBulkPort
{
    bool IsConnected { get; }
    Task UploadAsync(byte[] image, Action<BridgeBulkProgress>? progress,
        CancellationToken cancellationToken);
    Task<byte[]> DownloadAsync(Action<BridgeBulkProgress>? progress,
        CancellationToken cancellationToken);
    Task<uint?> QueryAccessoryWriteCounterAsync(CancellationToken cancellationToken);
}

public interface IBridgeControllerPakAccessoryPort
{
    void AdoptVerifiedControllerPak();
}

public interface IBridgeControllerPakInputGuard
{
    void Suspend();
    Task ResumeAsync(CancellationToken cancellationToken);
}

public sealed class DelegateBridgeControllerPakBulkPort(
    Func<bool> isConnected,
    Func<byte[], Action<BridgeBulkProgress>?, CancellationToken, Task> upload,
    Func<Action<BridgeBulkProgress>?, CancellationToken, Task<byte[]>> download,
    Func<CancellationToken, Task<uint?>> queryAccessoryWriteCounter)
    : IBridgeControllerPakBulkPort
{
    public bool IsConnected => isConnected();
    public Task UploadAsync(byte[] image, Action<BridgeBulkProgress>? progress,
        CancellationToken cancellationToken) => upload(image, progress, cancellationToken);
    public Task<byte[]> DownloadAsync(Action<BridgeBulkProgress>? progress,
        CancellationToken cancellationToken) => download(progress, cancellationToken);
    public Task<uint?> QueryAccessoryWriteCounterAsync(CancellationToken cancellationToken) =>
        queryAccessoryWriteCounter(cancellationToken);
}

public sealed class DelegateBridgeControllerPakAccessoryPort(Action adopt)
    : IBridgeControllerPakAccessoryPort
{
    public void AdoptVerifiedControllerPak() => adopt();
}

public sealed class DelegateBridgeControllerPakInputGuard(
    Action suspend,
    Func<CancellationToken, Task> resume) : IBridgeControllerPakInputGuard
{
    public void Suspend() => suspend();
    public Task ResumeAsync(CancellationToken cancellationToken) => resume(cancellationToken);
}

public sealed class BridgeControllerPakContentService : IBridgeControllerPakContentService
{
    public const int ControllerPakImageSize = 32768;
    private const int BlockSize = 32;

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IBridgeControllerPakFileSystem _files;
    private readonly IBridgeControllerPakBulkPort _bulk;
    private readonly IBridgeControllerPakAccessoryPort _accessory;
    private readonly IBridgeControllerPakInputGuard _input;
    private readonly IAccessoryPersistenceService _persistence;
    private BridgeControllerPakContentSnapshot _current =
        new(null, false, false, ControllerPakImageSize,
            BridgeControllerPakContentOperation.None, string.Empty);

    public BridgeControllerPakContentService(
        IBridgeControllerPakFileSystem files,
        IBridgeControllerPakBulkPort bulk,
        IBridgeControllerPakAccessoryPort accessory,
        IBridgeControllerPakInputGuard input,
        IAccessoryPersistenceService persistence)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _bulk = bulk ?? throw new ArgumentNullException(nameof(bulk));
        _accessory = accessory ?? throw new ArgumentNullException(nameof(accessory));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    public BridgeControllerPakContentSnapshot Current
    {
        get { lock (_stateLock) return _current; }
    }

    public Task<BridgeControllerPakContentResult> CreateAsync(string? path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(BridgeControllerPakContentOperation.Create, async token =>
        {
            Report(progress, BridgeControllerPakContentOperation.Create,
                BridgeControllerPakContentPhase.Preparing);
            string? normalizedPath = NormalizeOptionalPath(path);
            byte[] image = CreateBlankImage();
            await FlushPersistenceAsync(token).ConfigureAwait(false);

            bool wireCommitted = false;
            try
            {
                await WithInputSuspendedAsync(async () =>
                {
                    Report(progress, BridgeControllerPakContentOperation.Create,
                        BridgeControllerPakContentPhase.Uploading);
                    await UploadAsync(image, BridgeControllerPakContentOperation.Create,
                        progress, token).ConfigureAwait(false);
                    wireCommitted = true;
                    Adopt(BridgeControllerPakContentOperation.Create, progress);
                    if (normalizedPath is not null)
                    {
                        Report(progress, BridgeControllerPakContentOperation.Create,
                            BridgeControllerPakContentPhase.WritingFile);
                        await WriteFileAsync(normalizedPath, image, token)
                            .ConfigureAwait(false);
                    }
                    await ReplacePersistenceAsync(normalizedPath,
                        BridgeControllerPakContentOperation.Create, progress,
                        restorePreviousAssociation: false, token: token)
                        .ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            catch
            {
                if (wireCommitted)
                {
                    // The new image is already authoritative on the device. The old
                    // association can no longer truthfully own it.
                    _persistence.TryDeactivateClean();
                    RecordUnassociatedContent();
                }
                throw;
            }

            return Commit(BridgeControllerPakContentOperation.Create, normalizedPath);
        }, cancellationToken);

    public Task<BridgeControllerPakContentResult> LoadAsync(string path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(BridgeControllerPakContentOperation.Load, async token =>
        {
            Report(progress, BridgeControllerPakContentOperation.Load,
                BridgeControllerPakContentPhase.ReadingFile);
            string normalizedPath = NormalizeRequiredPath(path);
            byte[] image = await ReadFileAsync(normalizedPath, token).ConfigureAwait(false);
            ValidateImage(image);
            await FlushPersistenceAsync(token).ConfigureAwait(false);

            bool wireCommitted = false;
            try
            {
                await WithInputSuspendedAsync(async () =>
                {
                    Report(progress, BridgeControllerPakContentOperation.Load,
                        BridgeControllerPakContentPhase.Uploading);
                    await UploadAsync(image, BridgeControllerPakContentOperation.Load,
                        progress, token).ConfigureAwait(false);
                    wireCommitted = true;
                    Adopt(BridgeControllerPakContentOperation.Load, progress);
                    await ReplacePersistenceAsync(normalizedPath,
                        BridgeControllerPakContentOperation.Load, progress,
                        restorePreviousAssociation: false, token: token)
                        .ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            catch
            {
                if (wireCommitted)
                {
                    _persistence.TryDeactivateClean();
                    RecordUnassociatedContent();
                }
                throw;
            }

            return Commit(BridgeControllerPakContentOperation.Load, normalizedPath);
        }, cancellationToken);

    public Task<BridgeControllerPakContentResult> SaveAsync(string path,
        IProgress<BridgeControllerPakContentProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(BridgeControllerPakContentOperation.Save, async token =>
        {
            string normalizedPath = NormalizeRequiredPath(path);
            await FlushPersistenceAsync(token).ConfigureAwait(false);
            await WithInputSuspendedAsync(async () =>
            {
                Report(progress, BridgeControllerPakContentOperation.Save,
                    BridgeControllerPakContentPhase.Downloading);
                byte[] image = await DownloadAsync(
                    BridgeControllerPakContentOperation.Save, progress, token)
                    .ConfigureAwait(false);
                ValidateImage(image);
                Report(progress, BridgeControllerPakContentOperation.Save,
                    BridgeControllerPakContentPhase.WritingFile);
                await WriteFileAsync(normalizedPath, image, token).ConfigureAwait(false);
                Adopt(BridgeControllerPakContentOperation.Save, progress);
                await ReplacePersistenceAsync(normalizedPath,
                    BridgeControllerPakContentOperation.Save, progress,
                    restorePreviousAssociation: true, token: token)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);

            return Commit(BridgeControllerPakContentOperation.Save, normalizedPath);
        }, cancellationToken);

    private async Task<BridgeControllerPakContentResult> ExecuteAsync(
        BridgeControllerPakContentOperation operation,
        Func<CancellationToken, Task<BridgeControllerPakContentResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return Fail(operation, BridgeControllerPakContentFailure.Busy,
                    "Another Controller Pak content operation is already in progress.");
        }
        catch (OperationCanceledException)
        {
            return Fail(operation, BridgeControllerPakContentFailure.Cancelled,
                "The Controller Pak content operation was cancelled.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_bulk.IsConnected)
                return Fail(operation, BridgeControllerPakContentFailure.NotConnected,
                    "The bridge control connection is not available.");
            return await action(cancellationToken).ConfigureAwait(false);
        }
        catch (ControllerPakContentException ex)
        {
            return Fail(operation, ex.Failure, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail(operation, BridgeControllerPakContentFailure.Cancelled,
                "The Controller Pak content operation was cancelled.");
        }
        catch (Exception ex)
        {
            return Fail(operation, BridgeControllerPakContentFailure.UnexpectedFailure,
                ex.Message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : NormalizeRequiredPath(path);

    private string NormalizeRequiredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ControllerPakContentException(
                BridgeControllerPakContentFailure.InvalidPath,
                "A Controller Pak file path is required.");
        try { return _files.NormalizePath(path); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ControllerPakContentException(
                BridgeControllerPakContentFailure.InvalidPath, ex.Message, ex);
        }
    }

    private async Task<byte[]> ReadFileAsync(string path, CancellationToken token)
    {
        try { return await _files.ReadAllBytesAsync(path, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ControllerPakContentException(
                BridgeControllerPakContentFailure.FileReadFailure, ex.Message, ex);
        }
    }

    private async Task WriteFileAsync(string path, byte[] image, CancellationToken token)
    {
        try
        {
            await _files.WriteVerifiedAsync(path, image, ControllerPakImageSize, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ControllerPakContentException(
                BridgeControllerPakContentFailure.FileWriteFailure, ex.Message, ex);
        }
    }

    private static void ValidateImage(byte[] image)
    {
        if (image.Length != ControllerPakImageSize)
            throw new ControllerPakContentException(
                BridgeControllerPakContentFailure.InvalidImage,
                $"Expected a {ControllerPakImageSize} byte Controller Pak image, got {image.Length} bytes.");
    }

    private async Task FlushPersistenceAsync(CancellationToken token)
    {
        AccessoryPersistenceResult result;
        try { result = await _persistence.FlushAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ControllerPakContentException(
                BridgeControllerPakContentFailure.PersistenceFailure, ex.Message, ex);
        }
        if (!result.Success)
            throw new ControllerPakContentException(
                BridgeControllerPakContentFailure.PersistenceFailure, result.Error);
    }

    private async Task UploadAsync(byte[] image,
        BridgeControllerPakContentOperation operation,
        IProgress<BridgeControllerPakContentProgress>? progress,
        CancellationToken token)
    {
        try
        {
            await _bulk.UploadAsync(image,
                item => progress?.Report(new(operation,
                    BridgeControllerPakContentPhase.Uploading,
                    item.CompletedBytes, item.TotalBytes, item.ItemIndex, item.ItemCount)),
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ControllerPakContentException(
                BridgeControllerPakContentFailure.TransferFailure, ex.Message, ex);
        }
    }

    private async Task<byte[]> DownloadAsync(
        BridgeControllerPakContentOperation operation,
        IProgress<BridgeControllerPakContentProgress>? progress,
        CancellationToken token)
    {
        try
        {
            return await _bulk.DownloadAsync(
                item => progress?.Report(new(operation,
                    BridgeControllerPakContentPhase.Downloading,
                    item.CompletedBytes, item.TotalBytes, item.ItemIndex, item.ItemCount)),
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ControllerPakContentException(
                BridgeControllerPakContentFailure.TransferFailure, ex.Message, ex);
        }
    }

    private void Adopt(BridgeControllerPakContentOperation operation,
        IProgress<BridgeControllerPakContentProgress>? progress)
    {
        Report(progress, operation, BridgeControllerPakContentPhase.AdoptingAccessory);
        try { _accessory.AdoptVerifiedControllerPak(); }
        catch (Exception ex)
        {
            throw new ControllerPakContentException(
                BridgeControllerPakContentFailure.AccessoryAdoptionFailure, ex.Message, ex);
        }
    }

    private async Task ReplacePersistenceAsync(string? path,
        BridgeControllerPakContentOperation operation,
        IProgress<BridgeControllerPakContentProgress>? progress,
        bool restorePreviousAssociation,
        CancellationToken token)
    {
        Report(progress, operation, BridgeControllerPakContentPhase.ActivatingPersistence);
        AccessoryPersistenceSnapshot previousPersistence = _persistence.Current;
        BridgeControllerPakContentSnapshot previousContent = Current;
        uint baseline = 0;
        bool baselineKnown = false;
        if (path is not null)
        {
            try
            {
                uint? value = await _bulk.QueryAccessoryWriteCounterAsync(token)
                    .ConfigureAwait(false);
                if (value.HasValue) { baseline = value.Value; baselineKnown = true; }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        if (!_persistence.TryDeactivateClean())
            throw new ControllerPakContentException(
                BridgeControllerPakContentFailure.PersistenceFailure,
                "The previous accessory persistence lifecycle is not clean.");
        if (path is null) return;

        try
        {
            _persistence.Activate(AccessoryPersistenceKind.ControllerPak, path,
                ControllerPakImageSize, ReadCurrentForPersistenceAsync, baseline);
            if (!baselineKnown) _persistence.MarkDirty();
        }
        catch (Exception ex)
        {
            bool restored = restorePreviousAssociation &&
                TryRestorePreviousPersistence(previousContent, previousPersistence,
                    baseline, baselineKnown);
            if (!restored)
            {
                // Create/Load have already replaced device content, and an unsafe or
                // unavailable rollback must never leave the old path authoritative.
                _persistence.TryDeactivateClean();
                RecordUnassociatedContent();
            }
            throw new ControllerPakContentException(
                BridgeControllerPakContentFailure.PersistenceFailure, ex.Message, ex);
        }
    }

    private bool TryRestorePreviousPersistence(
        BridgeControllerPakContentSnapshot previousContent,
        AccessoryPersistenceSnapshot previousPersistence,
        uint baseline,
        bool baselineKnown)
    {
        if (!previousContent.ContentPresent ||
            !previousContent.HasAssociatedFile ||
            string.IsNullOrWhiteSpace(previousContent.Path) ||
            !previousPersistence.Active ||
            previousPersistence.Kind != AccessoryPersistenceKind.ControllerPak ||
            previousPersistence.ExpectedLength != ControllerPakImageSize ||
            !string.Equals(previousContent.Path, previousPersistence.Path,
                StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            _persistence.Activate(AccessoryPersistenceKind.ControllerPak,
                previousPersistence.Path, ControllerPakImageSize,
                ReadCurrentForPersistenceAsync, baseline);
            if (!baselineKnown) _persistence.MarkDirty();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<byte[]> ReadCurrentForPersistenceAsync(CancellationToken token)
    {
        byte[]? image = null;
        await WithInputSuspendedAsync(async () =>
        {
            image = await DownloadAsync(BridgeControllerPakContentOperation.Save,
                progress: null, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        ValidateImage(image!);
        return image!;
    }

    private async Task WithInputSuspendedAsync(Func<Task> action)
    {
        _input.Suspend();
        try { await action().ConfigureAwait(false); }
        finally { await _input.ResumeAsync(CancellationToken.None).ConfigureAwait(false); }
    }

    private BridgeControllerPakContentResult Commit(
        BridgeControllerPakContentOperation operation, string? path)
    {
        BridgeControllerPakContentSnapshot snapshot;
        lock (_stateLock)
        {
            _current = new(path, path is not null, true, ControllerPakImageSize,
                operation, string.Empty);
            snapshot = _current;
        }
        return new(true, BridgeControllerPakContentFailure.None, operation,
            snapshot, string.Empty);
    }

    private void RecordUnassociatedContent()
    {
        lock (_stateLock)
            _current = _current with
            {
                Path = null,
                HasAssociatedFile = false,
                ContentPresent = true
            };
    }

    private BridgeControllerPakContentResult Fail(
        BridgeControllerPakContentOperation operation,
        BridgeControllerPakContentFailure failure, string error)
    {
        BridgeControllerPakContentSnapshot snapshot;
        lock (_stateLock)
        {
            _current = _current with { LastError = error };
            snapshot = _current;
        }
        return new(false, failure, operation, snapshot, error);
    }

    private static void Report(IProgress<BridgeControllerPakContentProgress>? progress,
        BridgeControllerPakContentOperation operation,
        BridgeControllerPakContentPhase phase) =>
        progress?.Report(new(operation, phase, 0, ControllerPakImageSize, 0, 0));

    internal static byte[] CreateBlankImage()
    {
        byte[] image = new byte[ControllerPakImageSize];
        byte[] idBlock = new byte[BlockSize];
        idBlock[1] = (byte)(Random.Shared.Next(256) & 0x3F);
        idBlock[5] = (byte)(Random.Shared.Next(256) & 0x07);
        idBlock[6] = (byte)Random.Shared.Next(256);
        idBlock[7] = (byte)Random.Shared.Next(256);
        idBlock[8] = (byte)(Random.Shared.Next(256) & 0x0F);
        idBlock[9] = (byte)Random.Shared.Next(256);
        idBlock[10] = (byte)Random.Shared.Next(256);
        idBlock[11] = (byte)Random.Shared.Next(256);
        idBlock[25] = 0x01;
        idBlock[26] = 0x01;

        int sumA = 0;
        for (int index = 0; index < 28; index += 2)
            sumA = (sumA + ((idBlock[index] << 8) | idBlock[index + 1])) & 0xFFFF;
        int sumB = (0xFFF2 - sumA) & 0xFFFF;
        idBlock[28] = (byte)(sumA >> 8);
        idBlock[29] = (byte)sumA;
        idBlock[30] = (byte)(sumB >> 8);
        idBlock[31] = (byte)sumB;

        foreach (int offset in new[] { 0x20, 0x60, 0x80, 0xC0 })
            Buffer.BlockCopy(idBlock, 0, image, offset, BlockSize);
        for (int page = 5; page < 128; page++)
        {
            image[0x100 + (page * 2) + 1] = 0x03;
            image[0x200 + (page * 2) + 1] = 0x03;
        }
        image[0x100] = 0;
        image[0x101] = CalculateIndexChecksum(image, 0x100);
        image[0x200] = 0;
        image[0x201] = CalculateIndexChecksum(image, 0x200);
        return image;
    }

    private static byte CalculateIndexChecksum(byte[] image, int tableOffset)
    {
        int start = tableOffset == 0x100 ? tableOffset + 10 : tableOffset + 2;
        int sum = 0;
        for (int offset = start; offset < tableOffset + 0x100; offset++)
            sum = (sum + image[offset]) & 0xFF;
        return (byte)sum;
    }

    private sealed class ControllerPakContentException : Exception
    {
        public ControllerPakContentException(BridgeControllerPakContentFailure failure,
            string message, Exception? inner = null) : base(message, inner) => Failure = failure;
        public BridgeControllerPakContentFailure Failure { get; }
    }
}
