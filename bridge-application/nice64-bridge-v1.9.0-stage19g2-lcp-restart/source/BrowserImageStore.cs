using System.Globalization;

namespace PicoSwitchController;

public sealed class BrowserImageStore
{
    private const string Header = "Token,Route,SourcePageId,SourceUrl,FilePath,Width,Height,Format,Bytes,CreatedUtc,ExpiresUtc,HitCount";
    private readonly object _lock = new();
    private readonly Dictionary<string, BrowserImageEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _invalidations = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly string _path;
    private ushort _nextToken;

    public BrowserImageStore(string sessionRoot)
    {
        _root = sessionRoot;
        _path = Path.Combine(_root, "images.csv");
        Directory.CreateDirectory(_root);
        Load();
    }

    public BrowserImageEntry Register(string sourcePageId, string sourceUrl, string filePath, ushort width, ushort height)
    {
        lock (_lock)
        {
            CleanupLocked();
            BrowserImageEntry? existing = _entries.Values.FirstOrDefault(item =>
                string.Equals(item.SourceUrl, sourceUrl, StringComparison.OrdinalIgnoreCase) &&
                BrowserImageContract.TryReadNimg(item.FilePath, out _, out _, out _));
            if (existing is not null) return existing;
            string token;
            do { _nextToken++; if (_nextToken == 0) _nextToken = 1; token = _nextToken.ToString("X4", CultureInfo.InvariantCulture); } while (_entries.ContainsKey(token));
            BrowserImageEntry entry = new(token, "browser://i/" + token, sourcePageId, sourceUrl, filePath, width, height, "RGB5A1", new FileInfo(filePath).Length, DateTime.UtcNow, DateTime.UtcNow.AddHours(2), 0);
            SaveLocked(entry);
            _entries.Add(token, entry);
            return entry;
        }
    }

    public BrowserImageEntry? Lookup(string token, out string? invalidationReason)
    {
        lock (_lock)
        {
            CleanupLocked();
            invalidationReason = null;
            if (!_entries.TryGetValue(token, out BrowserImageEntry? entry) || !File.Exists(entry.FilePath))
            {
                _invalidations.TryGetValue(token, out invalidationReason);
                return null;
            }
            if (!BrowserImageContract.TryReadNimg(entry.FilePath, out ushort width, out ushort height, out string reason) ||
                width != entry.Width || height != entry.Height)
            {
                _entries.Remove(token);
                invalidationReason = "OLD_IMAGE_CONTRACT_INVALIDATED:" + reason;
                _invalidations[token] = invalidationReason;
                TryDelete(entry.FilePath);
                SaveLocked();
                return null;
            }
            entry.HitCount++;
            SaveLocked();
            return entry;
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) { SaveLocked(); return; }
            string[] lines = File.ReadAllLines(_path);
            if (lines.Length == 0 || lines[0] != Header) return;
            foreach (string line in lines.Skip(1))
            {
                string[] p = line.Split(',');
                if (p.Length != 12 || !ushort.TryParse(p[5], out ushort width) || !ushort.TryParse(p[6], out ushort height) || !long.TryParse(p[8], out long bytes) || !DateTime.TryParse(p[9], null, DateTimeStyles.AdjustToUniversal, out DateTime created) || !DateTime.TryParse(p[10], null, DateTimeStyles.AdjustToUniversal, out DateTime expires) || !int.TryParse(p[11], out int hits)) continue;
                BrowserImageEntry entry = new(p[0], p[1], p[2], p[3], p[4], width, height, p[7], bytes, created, expires, hits);
                if (!BrowserImageContract.TryReadNimg(entry.FilePath, out ushort nimgWidth, out ushort nimgHeight, out _) ||
                    nimgWidth != entry.Width || nimgHeight != entry.Height)
                {
                    TryDelete(entry.FilePath);
                    _invalidations[p[0]] = "OLD_IMAGE_CONTRACT_INVALIDATED";
                    continue;
                }
                _entries[p[0]] = entry;
                if (ushort.TryParse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort value) && value > _nextToken) _nextToken = value;
            }
            CleanupLocked(); SaveLocked();
        }
    }

    private void CleanupLocked()
    {
        foreach (BrowserImageEntry entry in _entries.Values.Where(item => item.ExpiresUtc <= DateTime.UtcNow || !File.Exists(item.FilePath)).ToArray()) _entries.Remove(entry.Token);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void SaveLocked(BrowserImageEntry? pending = null)
    {
        string temp = _path + ".tmp";
        using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (StreamWriter writer = new(stream))
        {
            writer.WriteLine(Header);
            foreach (BrowserImageEntry item in _entries.Values.Append(pending).Where(item => item is not null).Cast<BrowserImageEntry>().OrderBy(item => item.Token)) writer.WriteLine(string.Join(',', item.Token, item.Route, item.SourcePageId, item.SourceUrl, item.FilePath, item.Width, item.Height, item.Format, item.Bytes, item.CreatedUtc.ToString("O"), item.ExpiresUtc.ToString("O"), item.HitCount));
            writer.Flush(); stream.Flush(true);
        }
        File.Move(temp, _path, true);
    }
}

public sealed class BrowserImageEntry
{
    public BrowserImageEntry(string token, string route, string sourcePageId, string sourceUrl, string filePath, ushort width, ushort height, string format, long bytes, DateTime createdUtc, DateTime expiresUtc, int hitCount) { Token = token; Route = route; SourcePageId = sourcePageId; SourceUrl = sourceUrl; FilePath = filePath; Width = width; Height = height; Format = format; Bytes = bytes; CreatedUtc = createdUtc; ExpiresUtc = expiresUtc; HitCount = hitCount; }
    public string Token { get; } public string Route { get; } public string SourcePageId { get; } public string SourceUrl { get; } public string FilePath { get; } public ushort Width { get; } public ushort Height { get; } public string Format { get; } public long Bytes { get; } public DateTime CreatedUtc { get; } public DateTime ExpiresUtc { get; } public int HitCount { get; set; }
}
