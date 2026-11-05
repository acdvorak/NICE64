namespace PicoSwitchController;

using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public enum BridgeHistoryCategory
{
    ControllerPak,
    TransferPak
}

public enum BridgeHistoryOperation
{
    Load,
    RecordControllerPak,
    RecordTransferPak,
    Remove,
    ClearCategory,
    ClearAll
}

public enum BridgeHistoryFailure
{
    None,
    InvalidCategory,
    InvalidPath,
    PersistenceFailure,
    CorruptData,
    UnsupportedSchemaVersion
}

public sealed record BridgeControllerPakHistoryEntry(
    string Path,
    DateTimeOffset LastUsedUtc);

public sealed record BridgeTransferPakHistoryEntry(
    string RomPath,
    DateTimeOffset LastUsedUtc);

public sealed record BridgeHistorySnapshot(
    int SchemaVersion,
    IReadOnlyList<BridgeControllerPakHistoryEntry> ControllerPak,
    IReadOnlyList<BridgeTransferPakHistoryEntry> TransferPak,
    string LastError);

public sealed record BridgeHistoryResult(
    bool Success,
    BridgeHistoryFailure Failure,
    BridgeHistoryOperation Operation,
    BridgeHistorySnapshot Snapshot,
    string Error)
{
    public string? PreservedCorruptPath { get; init; }
}

public sealed record BridgeHistoryOptions(
    int ControllerPakLimit = 20,
    int TransferPakLimit = 20)
{
    internal void Validate()
    {
        if (ControllerPakLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(ControllerPakLimit));
        if (TransferPakLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(TransferPakLimit));
    }
}

public interface IBridgeHistoryService
{
    BridgeHistorySnapshot Current { get; }
    BridgeHistoryResult InitialLoad { get; }

    Task<BridgeHistoryResult> RecordControllerPakAsync(string path,
        CancellationToken cancellationToken = default);

    Task<BridgeHistoryResult> RecordTransferPakAsync(string romPath,
        CancellationToken cancellationToken = default);

    Task<BridgeHistoryResult> RemoveAsync(BridgeHistoryCategory category,
        string path, CancellationToken cancellationToken = default);

    Task<BridgeHistoryResult> ClearAsync(BridgeHistoryCategory category,
        CancellationToken cancellationToken = default);

    Task<BridgeHistoryResult> ClearAllAsync(
        CancellationToken cancellationToken = default);
}

public interface IBridgeHistoryStore
{
    string Path { get; }
    bool Exists { get; }
    string ReadAllText();
    Task WriteAllTextAtomicallyAsync(string content, CancellationToken cancellationToken);
    string PreserveCorruptFile(DateTimeOffset timestampUtc);
}

public sealed class BridgeHistoryJsonStore(string path) : IBridgeHistoryStore
{
    private readonly string _path = System.IO.Path.GetFullPath(
        string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A History storage path is required.", nameof(path))
            : path);

    public string Path => _path;
    public bool Exists => File.Exists(_path);
    public string ReadAllText() => File.ReadAllText(_path, Encoding.UTF8);

    public async Task WriteAllTextAtomicallyAsync(string content,
        CancellationToken cancellationToken)
    {
        string directory = System.IO.Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The History path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(directory,
            $".{System.IO.Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetBytes(content);
            await using (FileStream stream = new(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_path))
                File.Move(temporary, _path, overwrite: true);
            else
                File.Move(temporary, _path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public string PreserveCorruptFile(DateTimeOffset timestampUtc)
    {
        string timestamp = timestampUtc.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ");
        string preserved = $"{_path}.corrupt-{timestamp}";
        if (File.Exists(preserved))
            preserved += $"-{Guid.NewGuid():N}";
        File.Move(_path, preserved);
        return preserved;
    }
}

public sealed class BridgeHistoryService : IBridgeHistoryService
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly IBridgeHistoryStore _store;
    private readonly BridgeHistoryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _log;
    private BridgeHistorySnapshot _current = Empty();
    private string? _writeBlockReason;

    public BridgeHistoryService(IBridgeHistoryStore? store = null,
        BridgeHistoryOptions? options = null, TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        _store = store ?? new BridgeHistoryJsonStore(DefaultStoragePath());
        _options = options ?? new BridgeHistoryOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log;
        InitialLoad = Load();
    }

    public BridgeHistorySnapshot Current
    {
        get { lock (_stateLock) return Clone(_current); }
    }

    public BridgeHistoryResult InitialLoad { get; }

    public static string DefaultStoragePath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NICE64", "history.json");

    public Task<BridgeHistoryResult> RecordControllerPakAsync(string path,
        CancellationToken cancellationToken = default) =>
        MutateAsync(BridgeHistoryOperation.RecordControllerPak,
            current => RecordControllerPak(current, Normalize(path, ".mpk"),
                _timeProvider.GetUtcNow()), cancellationToken);

    public Task<BridgeHistoryResult> RecordTransferPakAsync(string romPath,
        CancellationToken cancellationToken = default) =>
        MutateAsync(BridgeHistoryOperation.RecordTransferPak,
            current => RecordTransferPak(current, Normalize(romPath, ".gb", ".gbc"),
                _timeProvider.GetUtcNow()), cancellationToken);

    public Task<BridgeHistoryResult> RemoveAsync(BridgeHistoryCategory category,
        string path, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(category))
            return Task.FromResult(Fail(BridgeHistoryOperation.Remove,
                BridgeHistoryFailure.InvalidCategory,
                "The requested History category is not recognized."));

        string normalized;
        try
        {
            normalized = category == BridgeHistoryCategory.ControllerPak
                ? Normalize(path, ".mpk")
                : Normalize(path, ".gb", ".gbc");
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return Task.FromResult(Fail(BridgeHistoryOperation.Remove,
                BridgeHistoryFailure.InvalidPath, ex.Message));
        }

        return MutateAsync(BridgeHistoryOperation.Remove, current => category switch
        {
            BridgeHistoryCategory.ControllerPak => current with
            {
                ControllerPak = ReadOnly(current.ControllerPak
                    .Where(entry => !PathEquals(entry.Path, normalized)))
            },
            BridgeHistoryCategory.TransferPak => current with
            {
                TransferPak = ReadOnly(current.TransferPak
                    .Where(entry => !PathEquals(entry.RomPath, normalized)))
            },
            _ => current
        }, cancellationToken);
    }

    public Task<BridgeHistoryResult> ClearAsync(BridgeHistoryCategory category,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(category))
            return Task.FromResult(Fail(BridgeHistoryOperation.ClearCategory,
                BridgeHistoryFailure.InvalidCategory,
                "The requested History category is not recognized."));

        return MutateAsync(BridgeHistoryOperation.ClearCategory,
            current => category switch
            {
                BridgeHistoryCategory.ControllerPak => current with
                {
                    ControllerPak = ReadOnly<BridgeControllerPakHistoryEntry>([])
                },
                BridgeHistoryCategory.TransferPak => current with
                {
                    TransferPak = ReadOnly<BridgeTransferPakHistoryEntry>([])
                },
                _ => current
            }, cancellationToken);
    }

    public Task<BridgeHistoryResult> ClearAllAsync(
        CancellationToken cancellationToken = default) =>
        MutateAsync(BridgeHistoryOperation.ClearAll,
            _ => Empty(), cancellationToken);

    private BridgeHistoryResult Load()
    {
        if (!_store.Exists)
        {
            _current = Empty();
            return Succeed(BridgeHistoryOperation.Load, _current);
        }

        try
        {
            HistoryDocument document = JsonSerializer.Deserialize<HistoryDocument>(
                _store.ReadAllText(), JsonOptions)
                ?? throw new JsonException("History JSON contained a null document.");
            if (document.SchemaVersion != SchemaVersion)
                return RecoverInvalidFile(BridgeHistoryFailure.UnsupportedSchemaVersion,
                    $"Unsupported History schema version {document.SchemaVersion}; expected {SchemaVersion}.");

            BridgeHistorySnapshot loaded = FromDocument(document);
            lock (_stateLock) _current = loaded;
            return Succeed(BridgeHistoryOperation.Load, loaded);
        }
        catch (JsonException ex)
        {
            return RecoverInvalidFile(BridgeHistoryFailure.CorruptData,
                $"History JSON is malformed or invalid: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            return RecoverInvalidFile(BridgeHistoryFailure.CorruptData, ex.Message);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return RecoverInvalidFile(BridgeHistoryFailure.CorruptData,
                $"History JSON contains an invalid path: {ex.Message}");
        }
        catch (Exception ex)
        {
            string error = $"History could not be loaded: {ex.Message}";
            lock (_stateLock) _current = WithError(Empty(), error);
            Log(error);
            return Fail(BridgeHistoryOperation.Load,
                BridgeHistoryFailure.PersistenceFailure, error);
        }
    }

    private BridgeHistoryResult RecoverInvalidFile(BridgeHistoryFailure failure,
        string error)
    {
        try
        {
            string preserved = _store.PreserveCorruptFile(_timeProvider.GetUtcNow());
            BridgeHistorySnapshot recovered = WithError(Empty(), error);
            lock (_stateLock) _current = recovered;
            Log($"{error} Preserved as {preserved}.");
            return new(false, failure, BridgeHistoryOperation.Load,
                Clone(recovered), error)
            {
                PreservedCorruptPath = preserved
            };
        }
        catch (Exception ex)
        {
            string blocked = $"{error} The damaged History file could not be preserved: " +
                $"{ex.Message}. Writes are blocked to protect the original file.";
            _writeBlockReason = blocked;
            BridgeHistorySnapshot recovered = WithError(Empty(), blocked);
            lock (_stateLock) _current = recovered;
            Log(blocked);
            return new(false, BridgeHistoryFailure.PersistenceFailure,
                BridgeHistoryOperation.Load, Clone(recovered), blocked);
        }
    }

    private async Task<BridgeHistoryResult> MutateAsync(BridgeHistoryOperation operation,
        Func<BridgeHistorySnapshot, BridgeHistorySnapshot> mutation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Fail(operation, BridgeHistoryFailure.PersistenceFailure,
                "The History operation was cancelled.");
        }

        try
        {
            if (_writeBlockReason is not null)
                return Fail(operation, BridgeHistoryFailure.PersistenceFailure,
                    _writeBlockReason);

            BridgeHistorySnapshot before;
            lock (_stateLock) before = Clone(_current);

            BridgeHistorySnapshot proposed;
            try
            {
                proposed = mutation(before) with { LastError = string.Empty };
            }
            catch (Exception ex) when (IsPathException(ex))
            {
                return Fail(operation, BridgeHistoryFailure.InvalidPath, ex.Message);
            }

            try
            {
                string json = JsonSerializer.Serialize(ToDocument(proposed), JsonOptions) +
                    Environment.NewLine;
                await _store.WriteAllTextAtomicallyAsync(json, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Fail(operation, BridgeHistoryFailure.PersistenceFailure,
                    "The History persistence operation was cancelled.");
            }
            catch (Exception ex)
            {
                string error = $"History persistence failed: {ex.Message}";
                Log(error);
                return Fail(operation, BridgeHistoryFailure.PersistenceFailure, error);
            }

            lock (_stateLock) _current = proposed;
            return Succeed(operation, proposed);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private BridgeHistorySnapshot RecordControllerPak(BridgeHistorySnapshot current,
        string path, DateTimeOffset timestamp)
    {
        IEnumerable<BridgeControllerPakHistoryEntry> retained = current.ControllerPak
            .Where(entry => !PathEquals(entry.Path, path));
        return current with
        {
            ControllerPak = ReadOnly(
                new[] { new BridgeControllerPakHistoryEntry(path, timestamp.ToUniversalTime()) }
                    .Concat(retained).Take(_options.ControllerPakLimit))
        };
    }

    private BridgeHistorySnapshot RecordTransferPak(BridgeHistorySnapshot current,
        string path, DateTimeOffset timestamp)
    {
        IEnumerable<BridgeTransferPakHistoryEntry> retained = current.TransferPak
            .Where(entry => !PathEquals(entry.RomPath, path));
        return current with
        {
            TransferPak = ReadOnly(
                new[] { new BridgeTransferPakHistoryEntry(path, timestamp.ToUniversalTime()) }
                    .Concat(retained).Take(_options.TransferPakLimit))
        };
    }

    private BridgeHistorySnapshot FromDocument(HistoryDocument document)
    {
        if (document.ControllerPak is null || document.TransferPak is null)
            throw new InvalidDataException("History JSON category arrays are required.");

        IEnumerable<BridgeControllerPakHistoryEntry> controllerPak = document.ControllerPak
            .Select(entry => new BridgeControllerPakHistoryEntry(
                Normalize(entry.Path, ".mpk"), entry.LastUsedUtc.ToUniversalTime()))
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(entry => entry.LastUsedUtc).First())
            .OrderByDescending(entry => entry.LastUsedUtc)
            .Take(_options.ControllerPakLimit);

        IEnumerable<BridgeTransferPakHistoryEntry> transferPak = document.TransferPak
            .Select(entry => new BridgeTransferPakHistoryEntry(
                Normalize(entry.RomPath, ".gb", ".gbc"),
                entry.LastUsedUtc.ToUniversalTime()))
            .GroupBy(entry => entry.RomPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(entry => entry.LastUsedUtc).First())
            .OrderByDescending(entry => entry.LastUsedUtc)
            .Take(_options.TransferPakLimit);

        return new(SchemaVersion, ReadOnly(controllerPak), ReadOnly(transferPak), string.Empty);
    }

    private static HistoryDocument ToDocument(BridgeHistorySnapshot snapshot) => new()
    {
        SchemaVersion = SchemaVersion,
        ControllerPak = snapshot.ControllerPak.Select(entry => new ControllerPakDocument
        {
            Path = entry.Path,
            LastUsedUtc = entry.LastUsedUtc.ToUniversalTime()
        }).ToList(),
        TransferPak = snapshot.TransferPak.Select(entry => new TransferPakDocument
        {
            RomPath = entry.RomPath,
            LastUsedUtc = entry.LastUsedUtc.ToUniversalTime()
        }).ToList()
    };

    private static string Normalize(string path, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A History file path is required.", nameof(path));
        string normalized = System.IO.Path.GetFullPath(path);
        string extension = System.IO.Path.GetExtension(normalized);
        if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"History path must use one of: {string.Join(", ", extensions)}.", nameof(path));
        return normalized;
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsPathException(Exception exception) =>
        exception is ArgumentException or NotSupportedException or IOException;

    private BridgeHistoryResult Succeed(BridgeHistoryOperation operation,
        BridgeHistorySnapshot snapshot) =>
        new(true, BridgeHistoryFailure.None, operation, Clone(snapshot), string.Empty);

    private BridgeHistoryResult Fail(BridgeHistoryOperation operation,
        BridgeHistoryFailure failure, string error) =>
        new(false, failure, operation, Current, error);

    private static BridgeHistorySnapshot Empty() => new(SchemaVersion,
        ReadOnly<BridgeControllerPakHistoryEntry>([]),
        ReadOnly<BridgeTransferPakHistoryEntry>([]), string.Empty);

    private static BridgeHistorySnapshot WithError(BridgeHistorySnapshot snapshot,
        string error) => snapshot with { LastError = error };

    private static BridgeHistorySnapshot Clone(BridgeHistorySnapshot snapshot) =>
        new(snapshot.SchemaVersion, ReadOnly(snapshot.ControllerPak),
            ReadOnly(snapshot.TransferPak), snapshot.LastError);

    private static ReadOnlyCollection<T> ReadOnly<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values.ToArray());

    private void Log(string message)
    {
        try { _log?.Invoke(message); } catch { }
    }

    private sealed class HistoryDocument
    {
        public int SchemaVersion { get; set; }
        public List<ControllerPakDocument>? ControllerPak { get; set; }
        public List<TransferPakDocument>? TransferPak { get; set; }
    }

    private sealed class ControllerPakDocument
    {
        public string Path { get; set; } = string.Empty;
        public DateTimeOffset LastUsedUtc { get; set; }
    }

    private sealed class TransferPakDocument
    {
        public string RomPath { get; set; } = string.Empty;
        public DateTimeOffset LastUsedUtc { get; set; }
    }
}
