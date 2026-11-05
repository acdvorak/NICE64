using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PicoSwitchController;

public sealed class NetworkPortalModule : INetworkPakModule
{
    private const int MaxPayloadLength = 23;
    private const string ItcorpUrl = "https://itcorp.com/";
    private const int MaxGeneratedPageBytes = 12000;
    private const int MaxHeadlineItems = 8;
    private static readonly TimeSpan FeedCacheTtl = TimeSpan.FromMinutes(5);

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
        new("bridge://menu", "menu.txt"),
        new("bridge://welcome", "welcome.txt"),
        new("bridge://longtext", "menu.txt"),
        new("bridge://about", "about.txt"),
        new("bridge://diagnostics", "diagnostics.txt"),
        new("bridge://news", "news.txt"),
        new("bridge://projects", "projects.txt")
    ];

    private static readonly FeedConfig[] DefaultFeedConfigs =
    [
        new("retro", "bridge://rssr", "Retro Gaming", "https://www.retrorgb.com/feed", MaxHeadlineItems, FeedCacheTtl),
        new("news", "bridge://rssn", "General News", "https://feeds.bbci.co.uk/news/rss.xml", MaxHeadlineItems, FeedCacheTtl)
    ];

    private readonly object _feedSync = new();
    private readonly object _ollamaSync = new();
    private readonly Dictionary<string, FeedCacheEntry> _feedCache = new(StringComparer.Ordinal);
    private readonly StringBuilder _ollamaUploadBuffer = new();
    private readonly ModulePackage _package;
    private readonly PortalRuntimeConfig _portalConfig;
    private readonly Dictionary<string, FeedConfig> _feedConfigs;
    private readonly Task<byte[]> _itcorpFetchTask;
    private string _lastOllamaQuestion = string.Empty;
    private byte[] _lastOllamaPage = Encoding.UTF8.GetBytes("""
        ASK NICE64

        No question has been submitted yet.

        Select Ask NICE64, press Z, type a question,
        then choose SEND.

        Retrieved by NICE64 Bridge
        """);

    public NetworkPortalModule(ModulePackage package)
    {
        _package = package;
        _portalConfig = LoadPortalConfig(package);
        _feedConfigs = BuildFeedConfigs(_portalConfig);
        _itcorpFetchTask = Task.Run(FetchItcorpPage);
        foreach (FeedConfig feed in _feedConfigs.Values)
        {
            _ = Task.Run(() => WarmFeedCache(feed));
        }
    }

    public string Id => _package.Manifest.Id;
    public string DisplayName => _package.Manifest.DisplayName;

    public NetworkPakResource? OpenResource(string resource, ModuleContext context)
    {
        byte[]? data = ResolveResource(resource, context);
        return data is null ? null : new NetworkPakResource(resource, data);
    }

    public NetworkPakWriteResult HandleWrite(NetworkPakWriteChunk chunk, ModuleContext context)
    {
        if (chunk.IsFirst)
        {
            _ollamaUploadBuffer.Clear();
        }

        _ollamaUploadBuffer.Append(chunk.GetUtf8Text());
        if (_ollamaUploadBuffer.Length > 512)
        {
            _ollamaUploadBuffer.Length = 512;
        }

        LogStreamEvent($"WRITE_STREAM_CHUNK module={DisplayName} flags={chunk.Flags:X2} bytes={chunk.Data.Length} total={_ollamaUploadBuffer.Length}");

        if (chunk.IsLast)
        {
            string query = _ollamaUploadBuffer.ToString().Trim();
            StartOllamaRequest(query);
            _ollamaUploadBuffer.Clear();
        }

        return NetworkPakWriteResult.Ack(new byte[] { 1 });
    }

    private static PortalRuntimeConfig LoadPortalConfig(ModulePackage package)
    {
        PortalRuntimeConfig fallback = BuildDefaultPortalConfig();
        string path = package.GetContentPath("portal.json");
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
            string.IsNullOrWhiteSpace(json.Portal?.Version) ? BridgeConstants.BridgeVersion : json.Portal!.Version!.Trim(),
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
            BridgeConstants.BridgeVersion,
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

    private byte[]? ResolveResource(string resource, ModuleContext context)
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
            "bridge://message" => Encoding.UTF8.GetBytes(context.DefaultMessage),
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
            {BridgeConstants.BridgeVersion}

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
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"NICE64-NetworkPak-Bridge/{BridgeConstants.BridgeVersion}");

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

    private byte[]? TryReadMappedStaticContent(string resource)
    {
        StaticContentMapping? mapping = StaticContentMappings.FirstOrDefault(
            item => string.Equals(item.Resource, resource, StringComparison.Ordinal));
        if (mapping is null)
        {
            return null;
        }

        byte[]? data = _package.TryReadContentBytes(mapping.RelativePath);
        if (data is null)
        {
            LogStreamEvent($"ERROR {resource} MISSING_FILE {mapping.RelativePath}");
            return null;
        }

        LogStreamEvent($"Resolved: {mapping.RelativePath}");
        LogStreamEvent($"Bytes: {data.Length}");
        return data;
    }

    private byte[]? TryReadSafeContentFile(string resource, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            LogStreamEvent($"ERROR {resource} MISSING_CONFIG_PATH");
            return null;
        }

        byte[]? data = _package.TryReadContentBytes(relativePath);
        if (data is null)
        {
            LogStreamEvent($"ERROR {resource} MISSING_FILE {relativePath}");
            return null;
        }

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
