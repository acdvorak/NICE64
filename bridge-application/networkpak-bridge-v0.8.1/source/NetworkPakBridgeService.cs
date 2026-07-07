using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PicoSwitchController;

public sealed record NetworkPakBridgeSnapshot(
    bool PicoConnected,
    bool HandshakeComplete,
    long RequestCount,
    long ResponseCount,
    long ErrorCount,
    string LastRequest,
    string LastResponse,
    string LastError,
    string DefaultMessage,
    long StreamOpenCount,
    long StreamChunkCount,
    long StreamCloseCount,
    long StreamErrorCount,
    long BytesRequested,
    long BytesDelivered,
    byte LastStreamId,
    string LastStreamResource,
    uint LastStreamOffset,
    int LastStreamLength,
    string LastStreamError,
    int ActiveStreamCount);

public sealed class NetworkPakBridgeService
{
    private const int MaxPayloadLength = 23;
    private const int StreamChunkSize = 16;
    private const byte ResponseStreamOpened = 0xA0;
    private const byte ResponseStreamInfo = 0xA1;
    private const byte ResponseStreamChunk = 0xA2;
    private const byte ResponseStreamClosed = 0xA3;
    private const byte ResponseStreamWriteAck = 0xA4;
    private const byte ResponseError = 0xFF;
    private const string BridgeVersion = "0.8.1";
    private const string ItcorpUrl = "https://itcorp.com/";
    private const int MaxGeneratedPageBytes = 12000;
    private const int MaxHeadlineItems = 8;
    private static readonly TimeSpan FeedCacheTtl = TimeSpan.FromMinutes(5);

    private sealed record StreamSession(byte Id, string Resource, byte[] Data, uint Crc32);
    private sealed record StaticContentMapping(string Resource, string RelativePath);
    private sealed record FeedConfig(string Key, string Resource, string DisplayName, string Url, int MaxItems, TimeSpan CacheTtl);
    private sealed record FeedCacheEntry(byte[] Data, DateTimeOffset FetchedAt, int ItemCount);
    private sealed record PortalRuntimeConfig(
        string Title,
        string Version,
        string Timezone,
        IReadOnlyList<PortalMenuItem> Menu,
        IReadOnlyDictionary<string, PortalResource> Resources,
        OllamaConfig Ollama);
    private sealed record OllamaConfig(bool Enabled, string Url, string Model, int TimeoutSeconds, int MaxResponseChars);
    private sealed record PortalMenuItem(string Title, string Resource, string Icon);
    private sealed record PortalResource(
        string Type,
        string? Path = null,
        string? Provider = null,
        string? Url = null,
        string? Title = null,
        int? MaxItems = null,
        int? CacheSeconds = null);

    private sealed class PortalJson
    {
        public PortalInfoJson? Portal { get; set; }
        public OllamaJson? Ollama { get; set; }
        public List<PortalMenuItemJson>? Menu { get; set; }
        public Dictionary<string, PortalResourceJson>? Resources { get; set; }
    }

    private sealed class PortalInfoJson
    {
        public string? Title { get; set; }
        public string? Version { get; set; }
        public string? Timezone { get; set; }
    }

    private sealed class PortalMenuItemJson
    {
        public string? Title { get; set; }
        public string? Resource { get; set; }
        public string? Icon { get; set; }
    }

    private sealed class OllamaJson
    {
        public bool? Enabled { get; set; }
        public string? Url { get; set; }
        public string? Model { get; set; }
        public int? TimeoutSeconds { get; set; }
        public int? MaxResponseChars { get; set; }
    }

    private sealed class PortalResourceJson
    {
        public string? Type { get; set; }
        public string? Path { get; set; }
        public string? Provider { get; set; }
        public string? Url { get; set; }
        public string? Title { get; set; }
        public int? MaxItems { get; set; }
        public int? CacheSeconds { get; set; }
    }

    private static readonly StaticContentMapping[] StaticContentMappings =
    [
        new("bridge://menu", Path.Combine("content", "menu.txt")),
        new("bridge://welcome", Path.Combine("content", "welcome.txt")),
        new("bridge://longtext", Path.Combine("content", "menu.txt")),
        new("bridge://about", Path.Combine("content", "about.txt")),
        new("bridge://diagnostics", Path.Combine("content", "diagnostics.txt")),
        new("bridge://news", Path.Combine("content", "news.txt")),
        new("bridge://projects", Path.Combine("content", "projects.txt"))
    ];

    private static readonly FeedConfig[] DefaultFeedConfigs =
    [
        new("retro", "bridge://rssr", "Retro Gaming", "https://www.retrorgb.com/feed", MaxHeadlineItems, FeedCacheTtl),
        new("news", "bridge://rssn", "General News", "https://feeds.bbci.co.uk/news/rss.xml", MaxHeadlineItems, FeedCacheTtl)
    ];

    private readonly object _sync = new();
    private readonly object _feedSync = new();
    private readonly object _ollamaSync = new();
    private readonly Dictionary<byte, StreamSession> _streams = new();
    private readonly Dictionary<string, FeedCacheEntry> _feedCache = new(StringComparer.Ordinal);
    private readonly StringBuilder _ollamaUploadBuffer = new();
    private readonly PortalRuntimeConfig _portalConfig;
    private readonly Dictionary<string, FeedConfig> _feedConfigs;
    private readonly Task<byte[]> _itcorpFetchTask;
    private bool _picoConnected;
    private bool _handshakeComplete;
    private long _requestCount;
    private long _responseCount;
    private long _errorCount;
    private string _lastRequest = "None";
    private string _lastResponse = "None";
    private string _lastError = "None";
    private string _defaultMessage = "hello from bridge";
    private byte _nextStreamId = 1;
    private long _streamOpenCount;
    private long _streamChunkCount;
    private long _streamCloseCount;
    private long _streamErrorCount;
    private long _bytesRequested;
    private long _bytesDelivered;
    private byte _lastStreamId;
    private string _lastStreamResource = "None";
    private uint _lastStreamOffset;
    private int _lastStreamLength;
    private string _lastStreamError = "None";
    private string _lastOllamaQuestion = string.Empty;
    private byte[] _lastOllamaPage = Encoding.UTF8.GetBytes("""
        ASK NICE64

        No question has been submitted yet.

        Select Ask NICE64, press Z, type a question,
        then choose SEND.

        Retrieved by NICE64 Bridge
        """);

    public NetworkPakBridgeService()
    {
        _portalConfig = LoadPortalConfig();
        _feedConfigs = BuildFeedConfigs(_portalConfig);
        _itcorpFetchTask = Task.Run(FetchItcorpPage);
        foreach (FeedConfig feed in _feedConfigs.Values)
        {
            _ = Task.Run(() => WarmFeedCache(feed));
        }
    }

    public void SetPicoConnected(bool connected)
    {
        lock (_sync)
        {
            _picoConnected = connected;
            if (!connected)
            {
                _handshakeComplete = false;
                _streams.Clear();
            }
        }
    }

    public void SetHandshakeComplete(bool complete)
    {
        lock (_sync)
        {
            _handshakeComplete = complete;
            _lastError = complete ? "None" : _lastError;
        }
    }

    public void SetDefaultMessage(string message)
    {
        lock (_sync)
        {
            _defaultMessage = SanitizePayload(message);
        }
    }

    public bool TryHandleRequest(string line, out string responseLine)
    {
        if (line.StartsWith("NETPAK_REQ_HEX|", StringComparison.Ordinal))
        {
            return TryHandleStreamRequest(line, out responseLine);
        }

        responseLine = string.Empty;
        if (!line.StartsWith("NETPAK_REQ|", StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = line.Split('|', 4);
        if (parts.Length < 3 || !byte.TryParse(parts[1], out byte sequence))
        {
            RecordError("Malformed NETPAK_REQ");
            return true;
        }

        string command = parts[2];
        string payload = parts.Length == 4 ? parts[3] : string.Empty;
        string responsePayload;
        bool ok = true;

        lock (_sync)
        {
            _requestCount++;
            _lastRequest = $"{command} #{sequence}: {payload}";

            responsePayload = command switch
            {
                "PING_BRIDGE" => "BRIDGE_PONG",
                "GET_BRIDGE_STATUS" => "BRIDGE_READY",
                "GET_BRIDGE_TIME" => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "GET_BRIDGE_MESSAGE" => _defaultMessage,
                "SEND_BRIDGE_MESSAGE" => "MESSAGE_RECEIVED",
                _ => "UNSUPPORTED_BRIDGE_CMD"
            };

            if (command == "SEND_BRIDGE_MESSAGE")
            {
                _lastRequest = $"{command} #{sequence}: {SanitizePayload(payload)}";
            }
            else if (command is not ("PING_BRIDGE" or "GET_BRIDGE_STATUS" or "GET_BRIDGE_TIME" or "GET_BRIDGE_MESSAGE"))
            {
                ok = false;
                _errorCount++;
                _lastError = responsePayload;
            }

            responsePayload = SanitizePayload(responsePayload);
            responseLine = $"NETPAK_RSP|{sequence}|{(ok ? "OK" : "ERR")}|{responsePayload}";
            _responseCount++;
            _lastResponse = $"#{sequence}: {responsePayload}";
            if (ok) _lastError = "None";
        }

        return true;
    }

    private bool TryHandleStreamRequest(string line, out string responseLine)
    {
        responseLine = string.Empty;
        string[] parts = line.Split('|', 4);
        if (parts.Length < 2 || !byte.TryParse(parts[1], out byte sequence))
        {
            RecordError("Malformed NETPAK_REQ_HEX");
            return true;
        }
        if (parts.Length != 4)
        {
            RecordError("Malformed NETPAK_REQ_HEX");
            responseLine = MakeHexError(sequence, "BAD_STREAM_REQUEST");
            return true;
        }

        byte[] requestPayload;
        try
        {
            requestPayload = Convert.FromHexString(parts[3]);
        }
        catch (FormatException)
        {
            RecordError("Malformed stream hex");
            responseLine = MakeHexError(sequence, "BRIDGE_BAD_HEX");
            return true;
        }

        lock (_sync)
        {
            _requestCount++;
            _lastRequest = $"{parts[2]} #{sequence}: {Convert.ToHexString(requestPayload)}";

            (bool ok, byte responseCommand, byte[] payload) result = parts[2] switch
            {
                "OPEN_STREAM" => OpenStream(requestPayload),
                "GET_STREAM_INFO" => GetStreamInfo(requestPayload),
                "READ_STREAM_CHUNK" => ReadStreamChunk(requestPayload),
                "WRITE_STREAM_CHUNK" => WriteStreamChunk(requestPayload),
                "CLOSE_STREAM" => CloseStream(requestPayload),
                _ => StreamError("UNSUPPORTED_STREAM_CMD")
            };

            responseLine = $"NETPAK_RSP_HEX|{sequence}|{(result.ok ? "OK" : "ERR")}|{result.responseCommand:X2}|{Convert.ToHexString(result.payload)}";
            _responseCount++;
            _lastResponse = $"#{sequence}: CMD={result.responseCommand:X2} LEN={result.payload.Length}";
            if (result.ok)
            {
                _lastError = "None";
            }
            else
            {
                _errorCount++;
                _lastError = Encoding.ASCII.GetString(result.payload);
            }
        }

        return true;
    }

    private static string MakeHexError(byte sequence, string error)
    {
        return $"NETPAK_RSP_HEX|{sequence}|ERR|FF|{Convert.ToHexString(Encoding.ASCII.GetBytes(error))}";
    }

    private (bool, byte, byte[]) OpenStream(byte[] request)
    {
        string resource = Encoding.ASCII.GetString(request).TrimEnd('\0');
        LogStreamEvent($"OPEN_STREAM {resource}");
        byte[]? data = ResolveResource(resource);
        if (data is null)
        {
            LogStreamEvent($"ERROR {resource} STREAM_NOT_FOUND");
            return StreamError("STREAM_NOT_FOUND");
        }
        if (_streams.Count >= 16) return StreamError("STREAM_LIMIT");

        byte id = AllocateStreamId();
        StreamSession session = new(id, resource, data, ComputeCrc32(data));
        _streams.Add(id, session);
        _streamOpenCount++;
        UpdateLastStream(session, 0, "None");
        LogStreamEvent($"StreamId: {id}");
        return (true, ResponseStreamOpened, MakeStreamMetadata(session));
    }

    private (bool, byte, byte[]) GetStreamInfo(byte[] request)
    {
        if (request.Length != 1 || !_streams.TryGetValue(request[0], out StreamSession? session))
            return StreamError("BAD_STREAM_ID");
        UpdateLastStream(session, 0, "None");
        return (true, ResponseStreamInfo, MakeStreamMetadata(session));
    }

    private (bool, byte, byte[]) ReadStreamChunk(byte[] request)
    {
        if (request.Length != 5 || !_streams.TryGetValue(request[0], out StreamSession? session))
            return StreamError("BAD_STREAM_ID");

        uint offset = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(1, 4));
        if (offset > session.Data.Length) return StreamError("BAD_STREAM_OFFSET");

        int count = Math.Min(StreamChunkSize, session.Data.Length - (int)offset);
        byte[] payload = new byte[7 + count];
        payload[0] = session.Id;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), offset);
        payload[5] = (byte)count;
        payload[6] = (byte)(offset + count >= session.Data.Length ? 1 : 0);
        session.Data.AsSpan((int)offset, count).CopyTo(payload.AsSpan(7));

        _streamChunkCount++;
        _bytesRequested += StreamChunkSize;
        _bytesDelivered += count;
        UpdateLastStream(session, offset, "None");
        LogStreamEvent($"READ_STREAM_CHUNK id={session.Id} resource={session.Resource} offset={offset} bytes={count}");
        return (true, ResponseStreamChunk, payload);
    }

    private (bool, byte, byte[]) CloseStream(byte[] request)
    {
        if (request.Length != 1 || !_streams.Remove(request[0], out StreamSession? session))
            return StreamError("BAD_STREAM_ID");
        _streamCloseCount++;
        UpdateLastStream(session, (uint)session.Data.Length, "None");
        LogStreamEvent($"CLOSE_STREAM id={session.Id} resource={session.Resource}");
        return (true, ResponseStreamClosed, new[] { session.Id });
    }

    private (bool, byte, byte[]) WriteStreamChunk(byte[] request)
    {
        if (request.Length < 2)
        {
            return StreamError("BAD_WRITE_CHUNK");
        }

        byte flags = request[0];
        int count = request[1];
        if (count < 0 || count > request.Length - 2)
        {
            return StreamError("BAD_WRITE_LENGTH");
        }

        if ((flags & 0x01) != 0)
        {
            _ollamaUploadBuffer.Clear();
        }

        string text = Encoding.UTF8.GetString(request, 2, count);
        _ollamaUploadBuffer.Append(text);
        if (_ollamaUploadBuffer.Length > 512)
        {
            _ollamaUploadBuffer.Length = 512;
        }

        LogStreamEvent($"WRITE_STREAM_CHUNK flags={flags:X2} bytes={count} total={_ollamaUploadBuffer.Length}");

        if ((flags & 0x02) != 0)
        {
            string query = _ollamaUploadBuffer.ToString().Trim();
            StartOllamaRequest(query);
            _ollamaUploadBuffer.Clear();
        }

        return (true, ResponseStreamWriteAck, new byte[] { 1 });
    }

    private (bool, byte, byte[]) StreamError(string error)
    {
        string clean = SanitizePayload(error);
        _streamErrorCount++;
        _lastStreamError = clean;
        LogStreamEvent($"ERROR {clean}");
        return (false, ResponseError, Encoding.ASCII.GetBytes(clean));
    }

    private static PortalRuntimeConfig LoadPortalConfig()
    {
        PortalRuntimeConfig fallback = BuildDefaultPortalConfig();
        string path = Path.Combine(AppContext.BaseDirectory, "portal.json");
        if (!File.Exists(path))
        {
            LogStreamEvent("Config: portal.json missing, using built-in defaults");
            return fallback;
        }

        try
        {
            JsonSerializerOptions options = new()
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            PortalJson? json = JsonSerializer.Deserialize<PortalJson>(File.ReadAllText(path), options);
            PortalRuntimeConfig config = BuildRuntimeConfig(json, fallback);
            LogStreamEvent($"Config: portal.json loaded menu={config.Menu.Count} resources={config.Resources.Count}");
            return config;
        }
        catch (Exception ex)
        {
            LogStreamEvent($"Config: portal.json invalid, using defaults {ex.GetType().Name}: {SanitizeLogValue(ex.Message)}");
            return fallback;
        }
    }

    private static PortalRuntimeConfig BuildRuntimeConfig(PortalJson? json, PortalRuntimeConfig fallback)
    {
        if (json?.Menu is null || json.Resources is null)
        {
            throw new InvalidDataException("portal.json requires menu and resources");
        }

        List<PortalMenuItem> menu = [];
        foreach (PortalMenuItemJson item in json.Menu)
        {
            string title = (item.Title ?? string.Empty).Trim();
            string resource = (item.Resource ?? string.Empty).Trim();
            string icon = (item.Icon ?? string.Empty).Trim();
            if (title.Length == 0 || resource.Length == 0)
            {
                continue;
            }
            if (resource.Length > MaxPayloadLength)
            {
                LogStreamEvent($"Config WARNING menu resource too long: {resource}");
                continue;
            }
            menu.Add(new PortalMenuItem(title, resource, icon));
        }

        Dictionary<string, PortalResource> resources = new(StringComparer.Ordinal);
        foreach ((string key, PortalResourceJson value) in json.Resources)
        {
            string resource = key.Trim();
            string type = (value.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (resource.Length == 0 || type.Length == 0)
            {
                continue;
            }
            if (resource.Length > MaxPayloadLength)
            {
                LogStreamEvent($"Config WARNING resource id too long: {resource}");
                continue;
            }
            resources[resource] = new PortalResource(
                type,
                value.Path?.Trim(),
                value.Provider?.Trim(),
                value.Url?.Trim(),
                value.Title?.Trim(),
                value.MaxItems,
                value.CacheSeconds);
        }

        if (menu.Count == 0 || resources.Count == 0)
        {
            throw new InvalidDataException("portal.json produced no usable menu/resources");
        }

        return new PortalRuntimeConfig(
            string.IsNullOrWhiteSpace(json.Portal?.Title) ? fallback.Title : json.Portal!.Title!.Trim(),
            string.IsNullOrWhiteSpace(json.Portal?.Version) ? BridgeVersion : json.Portal!.Version!.Trim(),
            string.IsNullOrWhiteSpace(json.Portal?.Timezone) ? fallback.Timezone : json.Portal!.Timezone!.Trim(),
            menu,
            resources,
            BuildOllamaConfig(json.Ollama));
    }

    private static PortalRuntimeConfig BuildDefaultPortalConfig()
    {
        List<PortalMenuItem> menu =
        [
            new("Welcome", "bridge://welcome", "smile"),
            new("Headlines", "bridge://rssr", "news"),
            new("ITcorp Demo", "bridge://itcorp", "web"),
            new("About", "bridge://about", "info"),
            new("Diagnostics", "bridge://diagnostics", "diag"),
            new("News", "bridge://news", "news"),
            new("Ask NICE64", "bridge://ollama", "chat"),
            new("Current Time", "bridge://time", "clock")
        ];

        Dictionary<string, PortalResource> resources = new(StringComparer.Ordinal)
        {
            ["bridge://welcome"] = new("file", Path: Path.Combine("content", "welcome.txt")),
            ["bridge://about"] = new("file", Path: Path.Combine("content", "about.txt")),
            ["bridge://diagnostics"] = new("file", Path: Path.Combine("content", "diagnostics.txt")),
            ["bridge://projects"] = new("file", Path: Path.Combine("content", "projects.txt")),
            ["bridge://ollama"] = new("dynamic", Provider: "ollama"),
            ["bridge://aires"] = new("dynamic", Provider: "ollama-result"),
            ["bridge://news"] = new("file", Path: Path.Combine("content", "news.txt")),
            ["bridge://time"] = new("dynamic", Provider: "time"),
            ["bridge://now"] = new("dynamic", Provider: "now"),
            ["bridge://itcorp"] = new("webpage", Url: ItcorpUrl),
            ["bridge://rssr"] = new("rss", Url: "https://www.retrorgb.com/feed", Title: "Retro Gaming", MaxItems: MaxHeadlineItems, CacheSeconds: 300),
            ["bridge://rssn"] = new("rss", Url: "https://feeds.bbci.co.uk/news/rss.xml", Title: "General News", MaxItems: MaxHeadlineItems, CacheSeconds: 300)
        };

        return new PortalRuntimeConfig(
            "NICE64 Network Portal",
            BridgeVersion,
            "America/Phoenix",
            menu,
            resources,
            new OllamaConfig(false, "http://localhost:11434", "llama3", 45, 4000));
    }

    private static OllamaConfig BuildOllamaConfig(OllamaJson? json)
    {
        return new OllamaConfig(
            json?.Enabled ?? false,
            string.IsNullOrWhiteSpace(json?.Url) ? "http://localhost:11434" : json!.Url!.Trim(),
            string.IsNullOrWhiteSpace(json?.Model) ? "llama3" : json!.Model!.Trim(),
            Math.Clamp(json?.TimeoutSeconds ?? 45, 5, 180),
            Math.Clamp(json?.MaxResponseChars ?? 4000, 256, 12000));
    }

    private static Dictionary<string, FeedConfig> BuildFeedConfigs(PortalRuntimeConfig config)
    {
        Dictionary<string, FeedConfig> feeds = new(StringComparer.Ordinal);
        foreach ((string resource, PortalResource entry) in config.Resources)
        {
            if (!string.Equals(entry.Type, "rss", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(entry.Url))
            {
                continue;
            }

            string key = resource.Replace("bridge://", string.Empty, StringComparison.Ordinal);
            string title = string.IsNullOrWhiteSpace(entry.Title) ? key : entry.Title!;
            int maxItems = entry.MaxItems is > 0 ? entry.MaxItems.Value : MaxHeadlineItems;
            TimeSpan cacheTtl = TimeSpan.FromSeconds(entry.CacheSeconds is > 0 ? entry.CacheSeconds.Value : 300);
            feeds[resource] = new FeedConfig(key, resource, title, entry.Url!, maxItems, cacheTtl);
        }

        if (feeds.Count == 0)
        {
            foreach (FeedConfig feed in DefaultFeedConfigs)
            {
                feeds[feed.Resource] = feed;
            }
        }

        return feeds;
    }

    private byte[]? ResolveResource(string resource)
    {
        if (string.Equals(resource, "bridge://menu", StringComparison.Ordinal))
        {
            LogStreamEvent("Requested: bridge://menu");
            LogStreamEvent("Generated: menu from portal config");
            return GenerateMenuPage();
        }

        if (string.Equals(resource, "bridge://longtext", StringComparison.Ordinal))
        {
            LogStreamEvent("Requested: bridge://longtext");
            LogStreamEvent("Generated: menu compatibility page");
            return GenerateMenuPage();
        }

        if (_portalConfig.Resources.TryGetValue(resource, out PortalResource? configured))
        {
            return ResolveConfiguredResource(resource, configured);
        }

        if (string.Equals(resource, "bridge://headlines", StringComparison.Ordinal))
        {
            FeedConfig? defaultFeed = _feedConfigs.GetValueOrDefault("bridge://rssr") ?? _feedConfigs.Values.FirstOrDefault();
            return defaultFeed is null ? null : GetHeadlinesPage(defaultFeed);
        }

        byte[]? staticContent = TryReadMappedStaticContent(resource);
        if (staticContent is not null)
        {
            return staticContent;
        }

        return resource switch
        {
            "bridge://message" => Encoding.UTF8.GetBytes(_defaultMessage),
            "bridge://test1k" => MakePatternResource(1024),
            "bridge://test4k" => MakePatternResource(4096),
            "bridge://test16k" => MakePatternResource(16384),
            _ => null
        };
    }

    private byte[]? ResolveConfiguredResource(string resource, PortalResource configured)
    {
        switch (configured.Type.ToLowerInvariant())
        {
        case "file":
            return TryReadSafeContentFile(resource, configured.Path);
        case "dynamic":
            return ResolveDynamicResource(resource, configured.Provider);
        case "webpage":
            if (string.Equals(resource, "bridge://itcorp", StringComparison.Ordinal))
            {
                LogStreamEvent("Requested: bridge://itcorp");
                return GetItcorpPage();
            }
            return null;
        case "rss":
            return _feedConfigs.TryGetValue(resource, out FeedConfig? feed) ? GetHeadlinesPage(feed) : null;
        default:
            LogStreamEvent($"ERROR {resource} UNSUPPORTED_CONFIG_TYPE {configured.Type}");
            return null;
        }
    }

    private byte[]? ResolveDynamicResource(string resource, string? provider)
    {
        string cleanProvider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (string.Equals(resource, "bridge://time", StringComparison.Ordinal) || cleanProvider == "time")
        {
            LogStreamEvent("Requested: bridge://time");
            LogStreamEvent("Generated: dynamic page");
            return GenerateTimePage();
        }

        if (string.Equals(resource, "bridge://now", StringComparison.Ordinal) || cleanProvider == "now")
        {
            LogStreamEvent("Requested: bridge://now");
            LogStreamEvent("Generated: compact time page");
            return GenerateNowPage();
        }

        if (string.Equals(resource, "bridge://ollama", StringComparison.Ordinal) ||
            string.Equals(resource, "bridge://aires", StringComparison.Ordinal) ||
            cleanProvider is "ollama" or "ollama-result")
        {
            LogStreamEvent($"Requested: {resource}");
            LogStreamEvent("Generated: Ask NICE64 status/result page");
            return GetOllamaPage();
        }

        return null;
    }

    private byte[] GenerateMenuPage()
    {
        StringBuilder builder = new();
        foreach (PortalMenuItem item in _portalConfig.Menu)
        {
            builder.Append(SanitizeMenuField(item.Title));
            builder.Append('|');
            builder.Append(SanitizeMenuField(item.Resource));
            if (!string.IsNullOrWhiteSpace(item.Icon))
            {
                builder.Append('|');
                builder.Append(SanitizeMenuField(item.Icon));
            }
            builder.Append('\n');
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string SanitizeMenuField(string value)
    {
        string clean = value.Replace('|', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 60 ? clean : clean[..60];
    }

    private static byte[] GenerateTimePage()
    {
        TimeZoneInfo zone = GetArizonaTimeZone();
        DateTimeOffset now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        string page = $"""
            CURRENT TIME

            Date:
            {now:yyyy-MM-dd}

            Time:
            {now:HH:mm:ss}

            Timezone:
            America/Phoenix

            Bridge Version:
            {BridgeVersion}

            Portal:
            Online
            """;
        return Encoding.UTF8.GetBytes(page);
    }

    private static byte[] GenerateNowPage()
    {
        TimeZoneInfo zone = GetArizonaTimeZone();
        DateTimeOffset now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        return Encoding.UTF8.GetBytes($"{now:yyyy-MM-dd}|{now:HH:mm:ss}|America/Phoenix\n");
    }

    private byte[] GetOllamaPage()
    {
        lock (_ollamaSync)
        {
            return _lastOllamaPage;
        }
    }

    private void SetOllamaPage(byte[] page)
    {
        lock (_ollamaSync)
        {
            _lastOllamaPage = page;
        }
    }

    private void StartOllamaRequest(string query)
    {
        LogStreamEvent("Requested: bridge://ollama query");

        if (string.IsNullOrWhiteSpace(query))
        {
            SetOllamaPage(Encoding.UTF8.GetBytes("""
                ASK NICE64

                No question was entered.

                Press B to return to the menu.
                Select Ask NICE64 and try again.
                """));
            return;
        }

        _lastOllamaQuestion = query;
        SetOllamaPage(MakeOllamaStatusPage(query, "Thinking..."));
        LogStreamEvent($"Ollama: background start model={_portalConfig.Ollama.Model} timeout={_portalConfig.Ollama.TimeoutSeconds}s maxChars={_portalConfig.Ollama.MaxResponseChars}");

        _ = Task.Run(async () =>
        {
            byte[] page;
            if (!_portalConfig.Ollama.Enabled)
            {
                LogStreamEvent("Ollama: disabled");
                page = Encoding.UTF8.GetBytes($"""
                    ASK NICE64

                    Q:
                    {query}

                    --------------------------------

                    Ollama is disabled in portal.json.

                    Enable ollama.enabled and try again.

                    Retrieved by NICE64 Bridge
                    """);
                SetOllamaPage(page);
                return;
            }

            try
            {
                string answer = await QueryOllama(query).ConfigureAwait(false);
                string pageText = $"""
                    ASK NICE64

                    Q:
                    {query}

                    --------------------------------

                    {answer}

                    Retrieved by NICE64 Bridge
                    """;

                page = Encoding.UTF8.GetBytes(pageText);
                if (page.Length > MaxGeneratedPageBytes)
                {
                    pageText = Encoding.UTF8.GetString(page, 0, MaxGeneratedPageBytes);
                    pageText += "\n\n[Truncated for NICE64 portal]\n";
                    page = Encoding.UTF8.GetBytes(pageText);
                }
                LogStreamEvent($"Ollama: complete responseBytes={page.Length}");
            }
            catch (Exception ex)
            {
                LogStreamEvent($"Ollama ERROR {ex.GetType().Name}: {SanitizeLogValue(ex.Message)}");
                page = Encoding.UTF8.GetBytes($"""
                    ASK NICE64

                    Q:
                    {query}

                    --------------------------------

                    Ollama request failed.

                    Endpoint:
                    {_portalConfig.Ollama.Url}

                    Model:
                    {_portalConfig.Ollama.Model}

                    Error:
                    {SanitizeLogValue(ex.Message)}

                    Retrieved by NICE64 Bridge
                    """);
            }

            SetOllamaPage(page);
        });
    }

    private byte[] MakeOllamaStatusPage(string query, string status)
    {
        return Encoding.UTF8.GetBytes($"""
            ASK NICE64

            Q:
            {query}

            --------------------------------

            {status}

            Open Ask NICE64 again in a few seconds
            to refresh the response.

            Retrieved by NICE64 Bridge
            """);
    }

    private async Task<string> QueryOllama(string query)
    {
        string endpoint = BuildOllamaGenerateUrl(_portalConfig.Ollama.Url);
        using HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(_portalConfig.Ollama.TimeoutSeconds)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NICE64-NetworkPak-Bridge/0.8.1");

        var request = new
        {
            model = _portalConfig.Ollama.Model,
            prompt = $"Answer clearly for a Nintendo 64 portal user. Keep it concise and plain text.\n\nQuestion: {query}",
            stream = false
        };
        string json = JsonSerializer.Serialize(request);
        using StringContent content = new(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(endpoint, content).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("response", out JsonElement responseText))
        {
            return TruncateOllamaText(CleanOllamaText(responseText.GetString() ?? string.Empty));
        }
        if (document.RootElement.TryGetProperty("message", out JsonElement message) &&
            message.TryGetProperty("content", out JsonElement contentText))
        {
            return TruncateOllamaText(CleanOllamaText(contentText.GetString() ?? string.Empty));
        }

        return TruncateOllamaText(CleanOllamaText(body));
    }

    private static string BuildOllamaGenerateUrl(string baseUrl)
    {
        string clean = baseUrl.TrimEnd('/');
        if (clean.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase))
        {
            return clean;
        }
        return $"{clean}/api/generate";
    }

    private static string CleanOllamaText(string value)
    {
        string clean = value.Replace("\r", "\n");
        clean = Regex.Replace(clean, @"[ \t\f\v]+", " ");
        clean = Regex.Replace(clean, @"\n[ \t]+", "\n");
        clean = Regex.Replace(clean, @"\n{3,}", "\n\n").Trim();
        return string.IsNullOrWhiteSpace(clean) ? "No response text was returned." : clean;
    }

    private string TruncateOllamaText(string value)
    {
        return value.Length <= _portalConfig.Ollama.MaxResponseChars
            ? value
            : value[.._portalConfig.Ollama.MaxResponseChars] + "\n\n[Ollama response truncated]";
    }

    private byte[] GetHeadlinesPage(FeedConfig feed)
    {
        LogStreamEvent($"Requested: {feed.Resource}");
        LogStreamEvent($"Feed: {feed.DisplayName}");
        LogStreamEvent($"URL: {feed.Url}");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_feedSync)
        {
            if (_feedCache.TryGetValue(feed.Key, out FeedCacheEntry? cached) &&
                now - cached.FetchedAt < feed.CacheTtl)
            {
                LogStreamEvent($"Cache: HIT ageSeconds={(int)(now - cached.FetchedAt).TotalSeconds}");
                LogStreamEvent($"Items: {cached.ItemCount}");
                return cached.Data;
            }
        }

        LogStreamEvent("Cache: MISS");
        try
        {
            (byte[] data, int itemCount) = FetchFeedPage(feed).GetAwaiter().GetResult();
            lock (_feedSync)
            {
                _feedCache[feed.Key] = new FeedCacheEntry(data, now, itemCount);
            }
            LogStreamEvent($"Fetched: OK bytes={data.Length}");
            LogStreamEvent($"Items: {itemCount}");
            return data;
        }
        catch (Exception ex)
        {
            LogStreamEvent($"Fetched: ERROR {ex.GetType().Name}: {SanitizeLogValue(ex.Message)}");
            string page = $"""
                HEADLINES UNAVAILABLE

                Source: {feed.DisplayName}
                URL: {feed.Url}

                Could not retrieve feed.
                {SanitizeLogValue(ex.Message)}

                Bridge is still online.
                Retrieved by NICE64 Bridge
                """;
            return Encoding.UTF8.GetBytes(page);
        }
    }

    private void WarmFeedCache(FeedConfig feed)
    {
        try
        {
            (byte[] data, int itemCount) = FetchFeedPage(feed).GetAwaiter().GetResult();
            lock (_feedSync)
            {
                _feedCache[feed.Key] = new FeedCacheEntry(data, DateTimeOffset.UtcNow, itemCount);
            }
            LogStreamEvent($"Prefetched: {feed.DisplayName} bytes={data.Length} items={itemCount}");
        }
        catch (Exception ex)
        {
            LogStreamEvent($"Prefetch ERROR {feed.DisplayName}: {ex.GetType().Name}: {SanitizeLogValue(ex.Message)}");
        }
    }

    private static async Task<(byte[] Data, int ItemCount)> FetchFeedPage(FeedConfig feed)
    {
        using HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NICE64-NetworkPak-Bridge/0.4");

        string xml = await client.GetStringAsync(feed.Url).ConfigureAwait(false);
        LogStreamEvent($"Downloaded: {feed.Url}");
        LogStreamEvent($"Bytes: {Encoding.UTF8.GetByteCount(xml)}");

        string converted = ConvertFeedToTextPage(feed, xml, out int itemCount);
        byte[] data = Encoding.UTF8.GetBytes(converted);
        if (data.Length > MaxGeneratedPageBytes)
        {
            converted = Encoding.UTF8.GetString(data, 0, MaxGeneratedPageBytes);
            converted += "\n\n[Truncated for NICE64 portal]\n";
            data = Encoding.UTF8.GetBytes(converted);
        }

        return (data, itemCount);
    }

    private static string ConvertFeedToTextPage(FeedConfig feed, string xml, out int itemCount)
    {
        XDocument document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        XElement? channel = document.Descendants("channel").FirstOrDefault();
        string feedTitle = CleanFeedText(channel?.Element("title")?.Value) ?? feed.DisplayName;
        IEnumerable<XElement> items = channel?.Elements("item") ?? Enumerable.Empty<XElement>();

        List<string> lines = [];
        itemCount = 0;
        lines.Add("HEADLINES");
        lines.Add("");
        lines.Add($"Source: {feed.DisplayName}");
        lines.Add(feedTitle);
        lines.Add($"Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        lines.Add("");

        foreach (XElement item in items.Take(feed.MaxItems))
        {
            string? title = CleanFeedText(item.Element("title")?.Value);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            string? date = CleanFeedText(item.Element("pubDate")?.Value);
            itemCount++;
            lines.Add($"{itemCount}. {title}");
            if (!string.IsNullOrWhiteSpace(date))
            {
                lines.Add($"   {date}");
            }
            lines.Add("");
        }

        if (itemCount == 0)
        {
            lines.Add("No headlines were available.");
            lines.Add("");
        }

        lines.Add("Retrieved by NICE64 Bridge");
        return string.Join('\n', lines);
    }

    private static string? CleanFeedText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = WebUtility.HtmlDecode(value);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private byte[] GetItcorpPage()
    {
        try
        {
            return _itcorpFetchTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogStreamEvent($"ERROR bridge://itcorp {ex.GetType().Name}: {SanitizeLogValue(ex.Message)}");
            string url = GetConfiguredUrl("bridge://itcorp", ItcorpUrl);
            string page = $"""
                ITCORP

                Unable to retrieve {url}

                {SanitizeLogValue(ex.Message)}

                Retrieved by NICE64 Bridge
                """;
            return Encoding.UTF8.GetBytes(page);
        }
    }

    private async Task<byte[]> FetchItcorpPage()
    {
        string url = GetConfiguredUrl("bridge://itcorp", ItcorpUrl);
        using HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NICE64-NetworkPak-Bridge/0.4");

        string html = await client.GetStringAsync(url).ConfigureAwait(false);
        LogStreamEvent($"Downloaded: {url}");
        LogStreamEvent($"Bytes: {Encoding.UTF8.GetByteCount(html)}");

        string converted = ConvertHtmlToTextPage(html);
        byte[] data = Encoding.UTF8.GetBytes(converted);
        if (data.Length > MaxGeneratedPageBytes)
        {
            converted = Encoding.UTF8.GetString(data, 0, MaxGeneratedPageBytes);
            converted += "\n\n[Truncated for NICE64 portal]\n";
            data = Encoding.UTF8.GetBytes(converted);
        }

        LogStreamEvent($"Converted: {data.Length}");
        return data;
    }

    private string GetConfiguredUrl(string resource, string fallback)
    {
        return _portalConfig.Resources.TryGetValue(resource, out PortalResource? entry) &&
            !string.IsNullOrWhiteSpace(entry.Url)
            ? entry.Url!
            : fallback;
    }

    private static string ConvertHtmlToTextPage(string html)
    {
        string title = MatchValue(html, @"<title[^>]*>(.*?)</title>");
        string text = html;

        text = Regex.Replace(text, @"<script\b[^>]*>.*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<style\b[^>]*>.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"</?(p|div|section|article|header|footer|nav|main|aside|h[1-6]|li|ul|ol|br|tr|td|th)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t\f\v]+", " ");
        text = Regex.Replace(text, @"\r", "\n");
        text = Regex.Replace(text, @"\n[ \t]+", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "itcorp.com";
        }
        title = WebUtility.HtmlDecode(Regex.Replace(title, @"\s+", " ")).Trim();

        return $"""
            ITCORP

            {title}

            {text}

            Retrieved by NICE64 Bridge
            """;
    }

    private static string MatchValue(string value, string pattern)
    {
        Match match = Regex.Match(value, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static TimeZoneInfo GetArizonaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("US Mountain Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
        }
    }

    private static byte[]? TryReadMappedStaticContent(string resource)
    {
        StaticContentMapping? mapping = StaticContentMappings.FirstOrDefault(
            item => string.Equals(item.Resource, resource, StringComparison.Ordinal));
        if (mapping is null)
        {
            return null;
        }

        string baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        string fullPath = Path.GetFullPath(Path.Combine(baseDirectory, mapping.RelativePath));
        if (!fullPath.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            LogStreamEvent($"ERROR {resource} PATH_TRAVERSAL_BLOCKED");
            return null;
        }

        if (!File.Exists(fullPath))
        {
            LogStreamEvent($"ERROR {resource} MISSING_FILE {mapping.RelativePath}");
            return null;
        }

        byte[] data = File.ReadAllBytes(fullPath);
        LogStreamEvent($"Resolved: {mapping.RelativePath}");
        LogStreamEvent($"Bytes: {data.Length}");
        return data;
    }

    private static byte[]? TryReadSafeContentFile(string resource, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            LogStreamEvent($"ERROR {resource} MISSING_CONFIG_PATH");
            return null;
        }

        string baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        string fullPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        if (!fullPath.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            LogStreamEvent($"ERROR {resource} PATH_TRAVERSAL_BLOCKED");
            return null;
        }

        if (!File.Exists(fullPath))
        {
            LogStreamEvent($"ERROR {resource} MISSING_FILE {relativePath}");
            return null;
        }

        byte[] data = File.ReadAllBytes(fullPath);
        LogStreamEvent($"Resolved: {relativePath}");
        LogStreamEvent($"Bytes: {data.Length}");
        return data;
    }

    private static byte[] MakePatternResource(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 37 + 11) & 0xFF);
        return data;
    }

    private static byte[] MakeRepeatedResource(int length, string text)
    {
        byte[] source = Encoding.UTF8.GetBytes(text);
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++) data[i] = source[i % source.Length];
        return data;
    }

    private byte AllocateStreamId()
    {
        for (int i = 0; i < byte.MaxValue; i++)
        {
            byte candidate = _nextStreamId++;
            if (_nextStreamId == 0) _nextStreamId = 1;
            if (!_streams.ContainsKey(candidate)) return candidate;
        }
        throw new InvalidOperationException("No stream ID available.");
    }

    private static byte[] MakeStreamMetadata(StreamSession session)
    {
        byte[] payload = new byte[10];
        payload[0] = session.Id;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), (uint)session.Data.Length);
        payload[5] = StreamChunkSize;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(6, 4), session.Crc32);
        return payload;
    }

    private void UpdateLastStream(StreamSession session, uint offset, string error)
    {
        _lastStreamId = session.Id;
        _lastStreamResource = session.Resource;
        _lastStreamOffset = offset;
        _lastStreamLength = session.Data.Length;
        _lastStreamError = error;
    }

    public NetworkPakBridgeSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new NetworkPakBridgeSnapshot(
                _picoConnected, _handshakeComplete, _requestCount, _responseCount, _errorCount,
                _lastRequest, _lastResponse, _lastError, _defaultMessage,
                _streamOpenCount, _streamChunkCount, _streamCloseCount, _streamErrorCount,
                _bytesRequested, _bytesDelivered, _lastStreamId, _lastStreamResource,
                _lastStreamOffset, _lastStreamLength, _lastStreamError, _streams.Count);
        }
    }

    private void RecordError(string error)
    {
        lock (_sync)
        {
            _errorCount++;
            _lastError = error;
        }
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static string SanitizePayload(string value)
    {
        string clean = value.Replace('|', '_').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= MaxPayloadLength ? clean : clean[..MaxPayloadLength];
    }

    private static string SanitizeLogValue(string value)
    {
        string clean = value.Replace('|', '_').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 180 ? clean : clean[..180];
    }

    private static void LogStreamEvent(string message)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "networkpak-bridge.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never block serial request handling.
        }
    }
}
