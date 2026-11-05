namespace PicoSwitchController;

using System.Security.Cryptography;

public enum AccessoryPersistenceKind
{
    None,
    GameBoySave,
    ControllerPak
}

public enum AccessoryPersistenceFlushReason
{
    Unspecified,
    AccessoryChange,
    FirmwareChange,
    RomReplacement,
    Disconnect,
    ApplicationClose
}

public sealed record AccessoryPersistenceSnapshot(
    AccessoryPersistenceKind Kind,
    string Path,
    int ExpectedLength,
    bool Active,
    bool Dirty,
    bool FlushInProgress,
    long DirtyGeneration,
    DateTimeOffset? LastSavedUtc,
    string LastError);

public sealed record AccessoryPersistenceResult(bool Success, bool WasDirty, string Error)
{
    public static AccessoryPersistenceResult Clean { get; } = new(true, false, string.Empty);
}

public interface IAtomicPersistenceWriter
{
    Task WriteVerifiedAsync(string path, byte[] data, int expectedLength,
        CancellationToken cancellationToken);
}

public sealed class AtomicPersistenceWriter : IAtomicPersistenceWriter
{
    public async Task WriteVerifiedAsync(string path, byte[] data, int expectedLength,
        CancellationToken cancellationToken)
    {
        if (data.Length != expectedLength)
            throw new InvalidDataException(
                $"Refusing to persist {data.Length} bytes; expected exactly {expectedLength}.");
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Persistence path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            byte[] verification = await File.ReadAllBytesAsync(temporary, cancellationToken)
                .ConfigureAwait(false);
            if (verification.Length != expectedLength ||
                !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(verification), SHA256.HashData(data)))
                throw new IOException("Temporary persistence verification failed.");
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}

public interface IAccessoryPersistenceService : IAsyncDisposable
{
    AccessoryPersistenceSnapshot Current { get; }
    void Activate(AccessoryPersistenceKind kind, string path, int expectedLength,
        Func<CancellationToken, Task<byte[]>> readCurrent, uint initialWriteCounter);
    void ObserveAccessoryWriteCounter(uint counter);
    void MarkDirty();
    Task<AccessoryPersistenceResult> FlushAsync(CancellationToken cancellationToken = default);
    Task<AccessoryPersistenceResult> FlushAsync(
        AccessoryPersistenceFlushReason reason,
        CancellationToken cancellationToken = default) => FlushAsync(cancellationToken);
    bool TryDeactivateClean();
}

public sealed record BridgeDisconnectAuthorization(
    bool DisconnectAllowed,
    AccessoryPersistenceResult Persistence);

public static class BridgeDisconnectPersistencePolicy
{
    public static async Task<BridgeDisconnectAuthorization> AuthorizeAsync(
        IAccessoryPersistenceService persistence,
        bool formClosing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        AccessoryPersistenceResult result = await persistence.FlushAsync(
            formClosing
                ? AccessoryPersistenceFlushReason.ApplicationClose
                : AccessoryPersistenceFlushReason.Disconnect,
            cancellationToken).ConfigureAwait(false);
        return new(result.Success || formClosing, result);
    }
}

public sealed class AccessoryPersistenceService : IAccessoryPersistenceService
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(20);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly SemaphoreSlim _workerSignal = new(0, 1);
    private readonly CancellationTokenSource _workerStop = new();
    private readonly IAtomicPersistenceWriter _writer;
    private readonly TimeSpan _debounce;
    private readonly Action<string>? _log;
    private readonly Task _workerTask;
    private CancellationTokenSource _scheduleChanged = new();
    private DateTimeOffset? _autosaveDueUtc;
    private Func<CancellationToken, Task<byte[]>>? _readCurrent;
    private AccessoryPersistenceSnapshot _current = Empty();
    private uint? _lastWriteCounter;
    private bool _disposed;

    public AccessoryPersistenceService(IAtomicPersistenceWriter? writer = null,
        TimeSpan? debounce = null, Action<string>? log = null)
    {
        _writer = writer ?? new AtomicPersistenceWriter();
        _debounce = debounce ?? DefaultDebounce;
        _log = log;
        if (_debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
        _workerTask = Task.Run(AutosaveWorkerAsync);
    }

    public AccessoryPersistenceSnapshot Current { get { lock (_sync) return _current; } }

    public void Activate(AccessoryPersistenceKind kind, string path, int expectedLength,
        Func<CancellationToken, Task<byte[]>> readCurrent, uint initialWriteCounter)
    {
        ArgumentNullException.ThrowIfNull(readCurrent);
        if (kind == AccessoryPersistenceKind.None || expectedLength <= 0 ||
            string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Invalid persistence activation.");
        lock (_sync)
        {
            if (_current.Active && _current.Dirty)
                throw new InvalidOperationException("Cannot replace a dirty accessory lifecycle.");
            CancelPendingAutosaveLocked();
            _readCurrent = readCurrent;
            _lastWriteCounter = initialWriteCounter;
            _current = new(kind, Path.GetFullPath(path), expectedLength, true, false,
                false, 0, null, string.Empty);
        }
    }

    public void ObserveAccessoryWriteCounter(uint counter)
    {
        lock (_sync)
        {
            if (!_current.Active || _current.Kind == AccessoryPersistenceKind.GameBoySave)
                return;
            if (_lastWriteCounter is null) { _lastWriteCounter = counter; return; }
            uint delta = unchecked(counter - _lastWriteCounter.Value);
            _lastWriteCounter = counter;
            if (delta == 0 || delta >= 0x80000000u) return;
            MarkDirtyLocked();
        }
    }

    public void MarkDirty()
    {
        lock (_sync) { if (_current.Active) MarkDirtyLocked(); }
    }

    private void MarkDirtyLocked()
    {
        _current = _current with
        {
            Dirty = true,
            DirtyGeneration = _current.DirtyGeneration + 1,
            LastError = string.Empty
        };
        if (_current.Kind == AccessoryPersistenceKind.GameBoySave)
        {
            _autosaveDueUtc = null;
            ResetScheduleChangedLocked();
            return;
        }
        _autosaveDueUtc = DateTimeOffset.UtcNow + _debounce;
        ResetScheduleChangedLocked();
        SignalWorker();
    }

    private async Task AutosaveWorkerAsync()
    {
        try
        {
            CancellationToken stop = _workerStop.Token;
            while (true)
            {
                await _workerSignal.WaitAsync(stop).ConfigureAwait(false);
                while (true)
                {
                    TimeSpan delay;
                    CancellationToken changed;
                    lock (_sync)
                    {
                        if (_autosaveDueUtc is null || !_current.Active || !_current.Dirty)
                            break;
                        delay = _autosaveDueUtc.Value - DateTimeOffset.UtcNow;
                        changed = _scheduleChanged.Token;
                    }

                    if (delay > TimeSpan.Zero)
                    {
                        try { await Task.Delay(delay, changed).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (changed.IsCancellationRequested)
                        {
                            stop.ThrowIfCancellationRequested();
                            continue;
                        }
                    }

                    lock (_sync)
                    {
                        if (_autosaveDueUtc is null ||
                            _autosaveDueUtc.Value > DateTimeOffset.UtcNow)
                            continue;
                        _autosaveDueUtc = null;
                    }
                    await FlushCoreAsync(stop, forceAssociatedRead: false).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_workerStop.IsCancellationRequested) { }
    }

    public async Task<AccessoryPersistenceResult> FlushAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_sync) CancelPendingAutosaveLocked();
        SignalWorker();
        return await FlushCoreAsync(cancellationToken, forceAssociatedRead: false)
            .ConfigureAwait(false);
    }

    public async Task<AccessoryPersistenceResult> FlushAsync(
        AccessoryPersistenceFlushReason reason,
        CancellationToken cancellationToken = default)
    {
        lock (_sync) CancelPendingAutosaveLocked();
        SignalWorker();
        bool forceAssociatedRead;
        lock (_sync)
            forceAssociatedRead = _current.Active &&
                _current.Kind == AccessoryPersistenceKind.GameBoySave &&
                reason != AccessoryPersistenceFlushReason.Unspecified;
        if (!forceAssociatedRead)
            return await FlushCoreAsync(cancellationToken, forceAssociatedRead: false)
                .ConfigureAwait(false);

        string reasonText = FormatReason(reason);
        int bytes;
        lock (_sync) bytes = _current.ExpectedLength;
        Log($"TRANSFER SAVE FLUSH START reason={reasonText}");
        AccessoryPersistenceResult result;
        try
        {
            result = await FlushCoreAsync(cancellationToken, forceAssociatedRead: true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log($"TRANSFER SAVE FLUSH FAILED reason={reasonText} error=cancelled");
            throw;
        }
        if (result.Success)
        {
            Log($"TRANSFER SAVE FLUSH SUCCESS bytes={bytes} reason={reasonText}");
        }
        else
        {
            Log($"TRANSFER SAVE FLUSH FAILED reason={reasonText} error={result.Error}");
        }
        return result;
    }

    private async Task<AccessoryPersistenceResult> FlushCoreAsync(
        CancellationToken cancellationToken,
        bool forceAssociatedRead)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Func<CancellationToken, Task<byte[]>> reader;
            AccessoryPersistenceSnapshot started;
            lock (_sync)
            {
                if (!_current.Active || (!_current.Dirty && !forceAssociatedRead))
                    return AccessoryPersistenceResult.Clean;
                reader = _readCurrent ?? throw new InvalidOperationException("No persistence reader is active.");
                started = _current;
                _current = _current with { FlushInProgress = true, LastError = string.Empty };
            }
            try
            {
                byte[] data = await reader(cancellationToken).ConfigureAwait(false);
                await _writer.WriteVerifiedAsync(started.Path, data, started.ExpectedLength,
                    cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    bool unchanged = _current.Active &&
                        _current.DirtyGeneration == started.DirtyGeneration;
                    _current = _current with
                    {
                        Dirty = !unchanged,
                        FlushInProgress = false,
                        LastSavedUtc = DateTimeOffset.UtcNow,
                        LastError = string.Empty
                    };
                }
                return new(true, true, string.Empty);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_sync)
                    _current = _current with { Dirty = true, FlushInProgress = false, LastError = ex.Message };
                return new(false, true, ex.Message);
            }
            catch (OperationCanceledException)
            {
                lock (_sync)
                    _current = _current with { Dirty = true, FlushInProgress = false };
                throw;
            }
        }
        finally { _flushGate.Release(); }
    }

    private static string FormatReason(AccessoryPersistenceFlushReason reason) => reason switch
    {
        AccessoryPersistenceFlushReason.AccessoryChange => "accessory-change",
        AccessoryPersistenceFlushReason.FirmwareChange => "firmware-change",
        AccessoryPersistenceFlushReason.RomReplacement => "rom-replacement",
        AccessoryPersistenceFlushReason.Disconnect => "disconnect",
        AccessoryPersistenceFlushReason.ApplicationClose => "application-close",
        _ => "unspecified"
    };

    private void Log(string message)
    {
        try { _log?.Invoke(message); } catch { }
    }

    public bool TryDeactivateClean()
    {
        lock (_sync)
        {
            if (_current.Dirty || _current.FlushInProgress) return false;
            CancelPendingAutosaveLocked();
            _readCurrent = null;
            _lastWriteCounter = null;
            _current = Empty();
            return true;
        }
    }

    private void CancelPendingAutosaveLocked()
    {
        _autosaveDueUtc = null;
        ResetScheduleChangedLocked();
    }

    private void ResetScheduleChangedLocked()
    {
        _scheduleChanged.Cancel();
        _scheduleChanged.Dispose();
        _scheduleChanged = new CancellationTokenSource();
    }

    private void SignalWorker()
    {
        try { _workerSignal.Release(); } catch (SemaphoreFullException) { }
    }

    private static AccessoryPersistenceSnapshot Empty() =>
        new(AccessoryPersistenceKind.None, string.Empty, 0, false, false, false,
            0, null, string.Empty);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_sync)
        {
            _autosaveDueUtc = null;
            _workerStop.Cancel();
            _scheduleChanged.Cancel();
        }
        SignalWorker();
        await _workerTask.ConfigureAwait(false);
        await _flushGate.WaitAsync().ConfigureAwait(false);
        _flushGate.Release();
        _flushGate.Dispose();
        _workerSignal.Dispose();
        _workerStop.Dispose();
        _scheduleChanged.Dispose();
    }
}
