using System.Globalization;
using System.Text;

namespace PicoSwitchController;

public sealed class BrowserSessionStore
{
    public const int DefaultMaxTokens = 4096;
    public static readonly TimeSpan DefaultTokenTtl = TimeSpan.FromHours(24);

    private const string LegacyRoutesHeader = "Token,Route,NormalizedUrl,OriginalUrl,SourcePageId,ClientId,SessionId,CreatedUtc,LastUsedUtc,ExpiresUtc,HitCount,Status";
    private const string RoutesHeader = "Revision,Token,Route,NormalizedUrl,OriginalUrl,SourcePageId,ClientId,SessionId,CreatedUtc,LastUsedUtc,ExpiresUtc,HitCount,Status";
    private const string CallsHeader = "TimestampUtc,ClientId,SessionId,Route,Token,ResolvedUrl,Result,HttpStatus,ElapsedMs,RequestId,Detail";
    private const string LinkRoutePrefix = "browser://g/";
    private const string UrlRoutePrefix = "browser://u/";

    private readonly object _lock = new();
    private readonly Dictionary<string, BrowserToken> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BrowserToken> _tokensByUrl = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _clientKey;
    private readonly string _sessionKey;
    private readonly string _sessionRoot;
    private readonly string _routesPath;
    private readonly string _callsPath;
    private int _nextTokenId;
    private long _revision;
    private int _duplicateCount;
    private int _prunedCount;
    private string? _integrityError;

    public BrowserSessionStore(string dataRoot, string clientKey = "pico-local", string sessionKey = "session-default")
    {
        _clientKey = clientKey;
        _sessionKey = sessionKey;
        string root = Path.GetFullPath(dataRoot);
        _sessionRoot = Path.GetFullPath(Path.Combine(root, SanitizePath(clientKey), SanitizePath(sessionKey)));
        if (!_sessionRoot.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ROUTE REGISTRY PATH FAILED");
        }

        Directory.CreateDirectory(_sessionRoot);
        Directory.CreateDirectory(RequestsRoot);
        _routesPath = Path.Combine(_sessionRoot, "routes.csv");
        _callsPath = Path.Combine(_sessionRoot, "calls.csv");
        Load();
    }

    public int MaxTokens { get; init; } = DefaultMaxTokens;
    public TimeSpan TokenTtl { get; init; } = DefaultTokenTtl;
    public string RoutesPath => _routesPath;
    public string CallsPath => _callsPath;
    public string RequestsRoot => Path.Combine(_sessionRoot, "requests");
    public int ActiveRouteCount { get { lock (_lock) return _tokens.Count(token => token.Value.Status == "Active"); } }
    public long Revision { get { lock (_lock) return _revision; } }
    public string? IntegrityError { get { lock (_lock) return _integrityError; } }
    public int DuplicateCount { get { lock (_lock) return _duplicateCount; } }
    public int PrunedCount { get { lock (_lock) return _prunedCount; } }

    public BrowserToken CreateToken(string clientKey, string sessionKey, string targetUrl, string sourcePageId, BrowserTokenKind kind)
    {
        string normalizedUrl = NormalizeUrl(targetUrl);
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            ThrowIfIntegrityErrorLocked();
            CleanupLocked(now);
            if (_tokensByUrl.TryGetValue(MakeUrlKey(clientKey, sessionKey, normalizedUrl), out BrowserToken? existing) &&
                existing.ExpiresUtc > now &&
                existing.Status == "Active")
            {
                DateTime previousAccess = existing.LastAccessUtc;
                int previousHits = existing.HitCount;
                existing.LastAccessUtc = now;
                existing.HitCount++;
                try { SaveRoutesLocked(); }
                catch { existing.LastAccessUtc = previousAccess; existing.HitCount = previousHits; throw; }
                return existing;
            }

            while (_tokens.Count >= MaxTokens)
            {
                BrowserToken? oldest = _tokens.Values.OrderBy(token => token.LastAccessUtc).FirstOrDefault();
                if (oldest is null)
                {
                    break;
                }

                RemoveLocked(oldest);
            }

            string tokenId = AllocateTokenLocked();
            string routePrefix = kind == BrowserTokenKind.Url ? UrlRoutePrefix : LinkRoutePrefix;
            BrowserToken token = new()
            {
                ClientKey = clientKey,
                SessionKey = sessionKey,
                Token = tokenId,
                Route = routePrefix + tokenId,
                TargetUrl = normalizedUrl,
                OriginalUrl = targetUrl,
                SourcePageId = sourcePageId,
                Kind = kind,
                CreatedUtc = now,
                LastAccessUtc = now,
                ExpiresUtc = now.Add(TokenTtl),
                HitCount = 0,
                Status = "Active"
            };
            SaveRoutesLocked(token);
            AddLocked(token);
            return token;
        }
    }

    public BrowserTokenLookup Lookup(string clientKey, string sessionKey, string tokenId)
    {
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            ThrowIfIntegrityErrorLocked();
            CleanupLocked(now);
            if (!IsValidToken(tokenId) || !_tokens.TryGetValue(MakeKey(clientKey, sessionKey, tokenId), out BrowserToken? token))
            {
                return BrowserTokenLookup.NotFound;
            }

            if (now > token.ExpiresUtc || token.Status != "Active")
            {
                RemoveLocked(token);
                SaveRoutesLocked();
                return BrowserTokenLookup.Expired;
            }

            DateTime previousAccess = token.LastAccessUtc;
            int previousHits = token.HitCount;
            token.LastAccessUtc = now;
            token.HitCount++;
            try { SaveRoutesLocked(); }
            catch { token.LastAccessUtc = previousAccess; token.HitCount = previousHits; throw; }
            return BrowserTokenLookup.Found(token);
        }
    }

    public void RecordCall(BrowserRouteCall call)
    {
        try
        {
            lock (_lock)
            {
                bool writeHeader = !File.Exists(_callsPath) || new FileInfo(_callsPath).Length == 0;
                using FileStream stream = new(_callsPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                using StreamWriter writer = new(stream, new UTF8Encoding(false));
                if (writeHeader)
                {
                    writer.WriteLine(CallsHeader);
                }

                writer.WriteLine(JoinCsv(
                    call.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                    call.ClientId,
                    call.SessionId,
                    call.Route,
                    call.Token,
                    call.ResolvedUrl,
                    call.Result,
                    call.HttpStatus,
                    call.ElapsedMs.ToString(CultureInfo.InvariantCulture),
                    call.RequestId,
                    call.Detail));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // Diagnostic logging must never block browsing.
        }
    }

    public void Cleanup()
    {
        lock (_lock)
        {
            ThrowIfIntegrityErrorLocked();
            CleanupLocked(DateTime.UtcNow);
            SaveRoutesLocked();
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_routesPath))
            {
                SaveRoutesLocked();
                return;
            }

            string[] lines = File.ReadAllLines(_routesPath);
            bool legacy = lines.Length > 0 && string.Equals(lines[0], LegacyRoutesHeader, StringComparison.Ordinal);
            if (lines.Length == 0 || (!legacy && !string.Equals(lines[0], RoutesHeader, StringComparison.Ordinal)))
            {
                File.Move(_routesPath, _routesPath + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), overwrite: true);
                SaveRoutesLocked();
                return;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                string[] fields = ParseCsvLine(lines[i]).ToArray();
                if (fields.Length != (legacy ? 12 : 13))
                {
                    continue;
                }

                int start = legacy ? 0 : 1;
                if (!legacy && long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long rowRevision)) _revision = Math.Max(_revision, rowRevision);
                if (!DateTime.TryParse(fields[start + 7], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime created) ||
                    !DateTime.TryParse(fields[start + 8], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime lastUsed) ||
                    !DateTime.TryParse(fields[start + 9], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime expires) ||
                    !int.TryParse(fields[start + 10], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hitCount))
                {
                    continue;
                }

                string tokenId = fields[start];
                if (!IsValidToken(tokenId))
                {
                    continue;
                }

                BrowserToken token = new()
                {
                    Token = tokenId,
                    Route = fields[start + 1],
                    TargetUrl = fields[start + 2],
                    OriginalUrl = fields[start + 3],
                    SourcePageId = fields[start + 4],
                    ClientKey = fields[start + 5],
                    SessionKey = fields[start + 6],
                    CreatedUtc = created.ToUniversalTime(),
                    LastAccessUtc = lastUsed.ToUniversalTime(),
                    ExpiresUtc = expires.ToUniversalTime(),
                    HitCount = hitCount,
                    Status = fields[start + 11],
                    Kind = fields[start + 1].StartsWith(UrlRoutePrefix, StringComparison.OrdinalIgnoreCase) ? BrowserTokenKind.Url : BrowserTokenKind.Link
                };
                string tokenKey = MakeKey(token.ClientKey, token.SessionKey, token.Token);
                string urlKey = MakeUrlKey(token.ClientKey, token.SessionKey, token.TargetUrl);
                if (_tokens.ContainsKey(tokenKey) || _tokensByUrl.ContainsKey(urlKey))
                {
                    _duplicateCount++;
                    _integrityError = "ROUTE REGISTRY AMBIGUOUS";
                    continue;
                }
                AddLocked(token);
                if (int.TryParse(token.Token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int numeric) && numeric > _nextTokenId)
                {
                    _nextTokenId = numeric;
                }
            }

            CleanupLocked(DateTime.UtcNow);
            SaveRoutesLocked();
        }
    }

    private void CleanupLocked(DateTime now)
    {
        foreach (BrowserToken token in _tokens.Values.Where(token => token.ExpiresUtc <= now || token.Status != "Active").ToArray())
        {
            RemoveLocked(token);
            _prunedCount++;
        }
    }

    private void SaveRoutesLocked(BrowserToken? pending = null)
    {
        Directory.CreateDirectory(_sessionRoot);
        string tempPath = _routesPath + ".tmp";
        using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (StreamWriter writer = new(stream, new UTF8Encoding(false)))
        {
            writer.WriteLine(RoutesHeader);
            long revision = _revision + 1;
            foreach (BrowserToken token in _tokens.Values.Append(pending).Where(token => token is not null).Cast<BrowserToken>().OrderBy(token => token.Token, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteLine(JoinCsv(
                    revision.ToString(CultureInfo.InvariantCulture),
                    token.Token,
                    token.Route,
                    token.TargetUrl,
                    token.OriginalUrl,
                    token.SourcePageId,
                    token.ClientKey,
                    token.SessionKey,
                    token.CreatedUtc.ToString("O", CultureInfo.InvariantCulture),
                    token.LastAccessUtc.ToString("O", CultureInfo.InvariantCulture),
                    token.ExpiresUtc.ToString("O", CultureInfo.InvariantCulture),
                    token.HitCount.ToString(CultureInfo.InvariantCulture),
                    token.Status));
            }

            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, _routesPath, overwrite: true);
        _revision++;
    }

    private string AllocateTokenLocked()
    {
        for (int attempts = 0; attempts < 0x10000; attempts++)
        {
            _nextTokenId = (_nextTokenId + 1) & 0xFFFF;
            if (_nextTokenId == 0)
            {
                _nextTokenId = 1;
            }

            string token = _nextTokenId.ToString("X4", CultureInfo.InvariantCulture);
            if (!_tokens.ContainsKey(MakeKey(_clientKey, _sessionKey, token)))
            {
                return token;
            }
        }

        throw new InvalidOperationException("ROUTE REGISTRY FULL");
    }

    public static string NormalizeUrl(string input)
    {
        if (input.Any(char.IsControl) || input.Length > 2048)
        {
            throw new InvalidDataException("LINK TARGET INVALID");
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidDataException("LINK TARGET INVALID");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("UNSUPPORTED URL SCHEME");
        }

        UriBuilder builder = new(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Fragment = string.Empty
        };
        if ((builder.Scheme == Uri.UriSchemeHttp && builder.Port == 80) ||
            (builder.Scheme == Uri.UriSchemeHttps && builder.Port == 443))
        {
            builder.Port = -1;
        }

        return builder.Uri.AbsoluteUri;
    }

    private void AddLocked(BrowserToken token)
    {
        _tokens[MakeKey(token.ClientKey, token.SessionKey, token.Token)] = token;
        _tokensByUrl[MakeUrlKey(token.ClientKey, token.SessionKey, token.TargetUrl)] = token;
    }

    private void ThrowIfIntegrityErrorLocked()
    {
        if (!string.IsNullOrWhiteSpace(_integrityError))
        {
            throw new InvalidDataException(_integrityError);
        }
    }

    private void RemoveLocked(BrowserToken token)
    {
        _tokens.Remove(MakeKey(token.ClientKey, token.SessionKey, token.Token));
        _tokensByUrl.Remove(MakeUrlKey(token.ClientKey, token.SessionKey, token.TargetUrl));
    }

    private static string MakeKey(string clientKey, string sessionKey, string tokenId)
    {
        return $"{clientKey}/{sessionKey}/{tokenId}";
    }

    private static string MakeUrlKey(string clientKey, string sessionKey, string normalizedUrl)
    {
        return $"{clientKey}/{sessionKey}/{normalizedUrl}";
    }

    private static bool IsValidToken(string value)
    {
        return value.Length == 4 && value.All(Uri.IsHexDigit);
    }

    private static string SanitizePath(string value)
    {
        StringBuilder builder = new();
        foreach (char c in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }

        string sanitized = builder.ToString().Trim('.', '-', '_');
        return sanitized.Length == 0 ? "id" : sanitized;
    }

    private static string JoinCsv(params string[] fields)
    {
        return string.Join(",", fields.Select(EscapeCsv));
    }

    private static string EscapeCsv(string value)
    {
        value ??= string.Empty;
        if (value.Contains('"') || value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private static IEnumerable<string> ParseCsvLine(string line)
    {
        StringBuilder field = new();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == ',')
            {
                yield return field.ToString();
                field.Clear();
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else
            {
                field.Append(c);
            }
        }

        yield return field.ToString();
    }
}

public enum BrowserTokenKind
{
    Link,
    Url
}

public sealed class BrowserToken
{
    public required string ClientKey { get; init; }
    public required string SessionKey { get; init; }
    public required string Token { get; init; }
    public required string Route { get; init; }
    public required string TargetUrl { get; init; }
    public required string OriginalUrl { get; init; }
    public required string SourcePageId { get; init; }
    public required BrowserTokenKind Kind { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required DateTime LastAccessUtc { get; set; }
    public required DateTime ExpiresUtc { get; init; }
    public required int HitCount { get; set; }
    public required string Status { get; set; }
    public byte[]? CachedWire { get; set; }
    public bool PrefetchStarted { get; set; }
}

public sealed record BrowserTokenLookup(BrowserToken? Token, BrowserTokenStatus Status)
{
    public static BrowserTokenLookup NotFound { get; } = new(null, BrowserTokenStatus.NotFound);
    public static BrowserTokenLookup Expired { get; } = new(null, BrowserTokenStatus.Expired);
    public static BrowserTokenLookup Found(BrowserToken token) => new(token, BrowserTokenStatus.Found);
}

public enum BrowserTokenStatus
{
    Found,
    NotFound,
    Expired
}

public sealed record BrowserRouteCall(
    DateTime TimestampUtc,
    string ClientId,
    string SessionId,
    string Route,
    string Token,
    string ResolvedUrl,
    string Result,
    string HttpStatus,
    int ElapsedMs,
    string RequestId,
    string Detail);
