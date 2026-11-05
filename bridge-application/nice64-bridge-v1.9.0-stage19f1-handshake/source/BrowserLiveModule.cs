using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nice64.BrowserLive.Readability;

namespace PicoSwitchController;

public sealed class BrowserLiveModule : INetworkPakModule
{
    public const string LiveResource = "browser://live";
    public const string HomeResource = "nice://home";
    private const string LinkRoutePrefix = "browser://g/";
    private const string UrlRoutePrefix = "browser://u/";
    private const string SearchRoutePrefix = "browser://search";
    private const string PollRoutePrefix = "browser://p/";
    private const string ImageRoutePrefix = "browser://i/";
    private const int StreamChunkSize = 16;
    // The ROM polls for roughly 20 seconds. Leave a small shutdown margin and
    // keep optional image conversion inside the same Bridge-side job deadline.
    private const int BrowserGenerationDeadlineMs = 18000;
    private const int GeneratorProcessTimeoutMs = 15000;
    private const int GeneratorFetchTimeoutSeconds = 7;
    private const int GeneratorTotalBudgetSeconds = 15;
    private const int ImageConversionMaximumMs = 6000;
    private const string ClientKey = "pico-local";
    private const string SessionKey = "session-default";
    private const string DefaultSearchProviderName = "Google";
    private const string DefaultSearchProviderUrl = "https://www.google.com/search?q={query}";
    private static readonly Regex SafeComponent = new("[^A-Za-z0-9._-]+", RegexOptions.Compiled);
    private static readonly HttpClient OriginalPageClient = CreateOriginalPageClient();

    private readonly ModulePackage _package;
    private readonly BrowserSessionStore _sessions;
    private readonly BrowserImageStore _images;
    private readonly BrowserReadableSourceMode _readableSourceMode;
    private readonly string _searchProviderName;
    private readonly string _searchProviderUrl;
    private readonly Action<string> _log;
    private readonly StringBuilder _urlRegistrationBuffer = new();
    private readonly object _jobsLock = new();
    private readonly Dictionary<string, BrowserGenerationJob> _jobsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BrowserGenerationJob> _jobsByUrl = new(StringComparer.OrdinalIgnoreCase);
    private ushort _nextJobId;
    private const int MaxJobs = 32;
    private const int MaxPendingJobs = 4;
    private static readonly TimeSpan ReadyRetention = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FailedRetention = TimeSpan.FromMinutes(5);

    public BrowserLiveModule(
        ModulePackage package,
        BrowserSessionStore sessions,
        BrowserImageStore images,
        string? readableSourceMode = null,
        Action<string>? log = null,
        string? searchProviderName = null,
        string? searchProviderUrl = null)
    {
        _package = package;
        _sessions = sessions;
        _images = images;
        _readableSourceMode = BrowserReadableSourceModeParser.Parse(readableSourceMode);
        _searchProviderName = string.IsNullOrWhiteSpace(searchProviderName) ? DefaultSearchProviderName : searchProviderName.Trim();
        _searchProviderUrl = NormalizeSearchProviderUrl(searchProviderUrl);
        _log = log ?? (_ => { });
        _log($"Browser readable source mode: {_readableSourceMode}");
        _log($"Browser search provider: {_searchProviderName} ({_searchProviderUrl})");
    }

    public string Id => _package.Manifest.Id;
    public string DisplayName => _package.Manifest.DisplayName;

    public NetworkPakResource? OpenResource(string resource, ModuleContext context)
    {
        if (!IsLiveResource(resource))
        {
            return null;
        }

        context.Log("Browser Live request received");
        context.Log($"Client: {ClientKey}");
        context.Log($"Session: {SessionKey}");
        context.Log($"Input route: {resource}");

        try
        {
            if (IsHomeResource(resource))
            {
                return OpenWire(resource, BuildHomeDocument(), context);
            }

            if (resource.StartsWith(ImageRoutePrefix, StringComparison.OrdinalIgnoreCase))
            {
                BrowserImageEntry? image = _images.Lookup(resource[ImageRoutePrefix.Length..], out string? imageReason);
                if (image is null)
                {
                    context.Log($"Browser image unavailable: {imageReason ?? "NOT_FOUND"}");
                    return null;
                }
                return new NetworkPakResource(resource, File.ReadAllBytes(image.FilePath));
            }

            if (resource.StartsWith(PollRoutePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return OpenPoll(resource, context);
            }

            RouteTarget target = ResolveRoute(resource, context);
            if (target.Wire is not null)
            {
                return OpenWire(resource, target.Wire, context);
            }

            string url = target.Token?.TargetUrl ?? target.Url!;
            BrowserGenerationJob job = GetOrCreateJob(url, resource, target.Mode, context);
            if (job.State == BrowserGenerationState.Ready && job.WirePayload is not null)
            {
                context.Log($"Browser job final delivery job={job.JobId} bytes={job.WirePayload.Length}");
                return OpenWire(resource, job.WirePayload, context);
            }
            if (job.State == BrowserGenerationState.Failed && job.ErrorPayload is not null)
            {
                return OpenWire(resource, job.ErrorPayload, context);
            }
            return OpenWire(resource, BuildLoadingDocument(job), context);
        }
        catch (Exception ex)
        {
            context.Log($"Browser Live failure: {ex.Message}");
            return OpenWire(resource, BuildErrorWire(ex), context);
        }
    }

    public NetworkPakWriteResult HandleWrite(NetworkPakWriteChunk chunk, ModuleContext context)
    {
        if (chunk.IsFirst)
        {
            _urlRegistrationBuffer.Clear();
        }

        _urlRegistrationBuffer.Append(chunk.GetUtf8Text());
        context.Log($"Browser URL registration received chunk flags={chunk.Flags:X2} bytes={chunk.Data.Length} total={_urlRegistrationBuffer.Length}");

        if (!chunk.IsLast)
        {
            return NetworkPakWriteResult.Ack(new byte[] { 1 });
        }

        string input = _urlRegistrationBuffer.ToString().Trim();
        _urlRegistrationBuffer.Clear();
        try
        {
            bool isSearch = input.StartsWith("search:", StringComparison.OrdinalIgnoreCase);
            string value = isSearch ? input["search:".Length..].Trim() : input;
            string url = isSearch ? ResolveSearchQueryToUrl(value) : ResolveTypedInputToUrl(value);
            BrowserToken token = _sessions.CreateToken(ClientKey, SessionKey, url, isSearch ? "keyboard-search" : "keyboard", BrowserTokenKind.Url);
            string route = token.Route;
            context.Log(isSearch ? "Browser search registration received" : "Browser URL registration received");
            context.Log($"Input length: {value.Length}");
            if (isSearch) context.Log($"Search provider: {_searchProviderName}");
            context.Log("URL validated");
            context.Log($"Token created: {token.Token}");
            return NetworkPakWriteResult.Ack(Encoding.ASCII.GetBytes(route));
        }
        catch (Exception ex)
        {
            context.Log($"URL REGISTRATION FAILED: {ex.Message}");
            return NetworkPakWriteResult.Ack(Encoding.ASCII.GetBytes("ERR"));
        }
    }

    public static bool IsLiveResource(string resource)
    {
        return IsHomeResource(resource) ||
               string.Equals(resource, LiveResource, StringComparison.OrdinalIgnoreCase) ||
               resource.StartsWith(LiveResource + "?", StringComparison.OrdinalIgnoreCase) ||
               resource.StartsWith(LinkRoutePrefix, StringComparison.OrdinalIgnoreCase) ||
               resource.StartsWith(UrlRoutePrefix, StringComparison.OrdinalIgnoreCase) ||
               resource.StartsWith(SearchRoutePrefix + "?", StringComparison.OrdinalIgnoreCase) ||
               resource.StartsWith(PollRoutePrefix, StringComparison.OrdinalIgnoreCase) ||
               resource.StartsWith(ImageRoutePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHomeResource(string resource)
    {
        return string.Equals(resource, HomeResource, StringComparison.OrdinalIgnoreCase);
    }

    private NetworkPakResource OpenWire(string resource, byte[] wire, ModuleContext context)
    {
        int chunks = (wire.Length + StreamChunkSize - 1) / StreamChunkSize;
        context.Log($"Request completed resource={resource} wireBytes={wire.Length} chunks={chunks}");
        return new NetworkPakResource(resource, wire);
    }

    private RouteTarget ResolveRoute(string resource, ModuleContext context)
    {
        if (resource.StartsWith(SearchRoutePrefix + "?", StringComparison.OrdinalIgnoreCase))
        {
            string? query = ReadQueryValue(resource, "q");
            if (string.IsNullOrWhiteSpace(query))
            {
                return RouteTarget.ForWire(NiceWireDocument.BuildErrorDocument("SEARCH QUERY REQUIRED", "Enter a search term and try again."));
            }

            string trimmedQuery = query.Trim();
            string encodedQuery = Uri.EscapeDataString(trimmedQuery);
            string url = _searchProviderUrl.Replace("{query}", encodedQuery, StringComparison.Ordinal);
            context.Log("Browser search request received");
            context.Log($"Search provider: {_searchProviderName}");
            context.Log($"Search query: {trimmedQuery}");
            context.Log($"Search resolved URL: {url}");
            return RouteTarget.ForUrl(ValidateUrl(url), "SEARCH");
        }

        if (resource.StartsWith(LinkRoutePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string tokenId = resource[LinkRoutePrefix.Length..];
            BrowserTokenLookup lookup = _sessions.Lookup(ClientKey, SessionKey, tokenId);
            context.Log($"Browser link request received");
            context.Log($"Token: {tokenId}");
            context.Log($"Token found: {lookup.Status == BrowserTokenStatus.Found}");
            return lookup.Status switch
            {
                BrowserTokenStatus.Found => RouteTarget.ForToken(lookup.Token!, IsKeyboardSearchToken(lookup.Token!) ? "SEARCH" : "AUTO"),
                BrowserTokenStatus.Expired => RouteTarget.ForWire(NiceWireDocument.BuildErrorDocument("ROUTE EXPIRED", "The saved link is no longer available.")),
                _ => RouteTarget.ForWire(NiceWireDocument.BuildErrorDocument("ROUTE NOT FOUND", "The Bridge route registry does not contain this link."))
            };
        }

        if (resource.StartsWith(UrlRoutePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string tokenId = resource[UrlRoutePrefix.Length..];
            BrowserTokenLookup lookup = _sessions.Lookup(ClientKey, SessionKey, tokenId);
            context.Log($"Browser URL token request received token={tokenId} found={lookup.Status == BrowserTokenStatus.Found}");
            return lookup.Status switch
            {
                BrowserTokenStatus.Found => RouteTarget.ForToken(lookup.Token!, IsKeyboardSearchToken(lookup.Token!) ? "SEARCH" : "AUTO"),
                BrowserTokenStatus.Expired => RouteTarget.ForWire(NiceWireDocument.BuildErrorDocument("ROUTE EXPIRED", "The saved URL is no longer available.")),
                _ => RouteTarget.ForWire(NiceWireDocument.BuildErrorDocument("ROUTE NOT FOUND", "The Bridge route registry does not contain this URL."))
            };
        }

        string alias = ReadQueryValue(resource, "u") ?? "cnn";
        return alias.ToLowerInvariant() switch
        {
            "cnn" => RouteTarget.ForUrl("https://lite.cnn.com", "INDEX"),
            "wiki" => RouteTarget.ForUrl("https://en.wikipedia.org/wiki/Nintendo_64", "ARTICLE"),
            _ => RouteTarget.ForWire(NiceWireDocument.BuildErrorDocument("ALIAS NOT FOUND", $"No Browser Live alias is configured for '{alias}'."))
        };
    }

    private NiceWireBuildResult GenerateWireForUrl(string url, string route, string mode, ModuleContext context, bool mapLinks)
    {
        url = ValidateUrl(url);
        Stopwatch elapsed = Stopwatch.StartNew();
        string requestId = SanitizeId($"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}", 48);
        string requestRoot = CreateRequestRoot(requestId);
        string outputPath = Path.Combine(requestRoot, "document.json");
        string logPath = Path.Combine(requestRoot, "generator.log");
        string scriptPath = Path.Combine(_package.PackageRoot, "scripts", "Generate-NiceDocument.ps1");
        string generatorMode = string.Equals(mode, "SEARCH", StringComparison.OrdinalIgnoreCase) ? "AUTO" : mode;

        WriteRequestJson(Path.Combine(requestRoot, "request.json"), route, url, mode, requestId, context);

        context.Log($"Request ID: {requestId}");
        context.Log($"Resolved URL: {url}");
        context.Log($"Mode: {mode}");
        if (!string.Equals(mode, generatorMode, StringComparison.Ordinal))
            context.Log($"Generator mode: {generatorMode}");
        context.Log($"Generator started");
        try
        {
            bool preferSearchFetchProfile = string.Equals(mode, "SEARCH", StringComparison.OrdinalIgnoreCase);
            bool generatedLocally = false;
            if (preferSearchFetchProfile)
            {
                // Search providers can give HttpClient a query-less landing page while
                // their HTTP/1.1 endpoint returns actual results. The generator's curl
                // profile preserves the configured provider URL for every provider.
                context.Log("LOCAL_READABILITY_SKIPPED reason=SEARCH_CURL_PROFILE");
            }
            else if (_readableSourceMode is BrowserReadableSourceMode.Auto or BrowserReadableSourceMode.Local)
            {
                try
                {
                    LocalReadableInput input = PrepareLocalReadableInput(url, requestRoot, context);
                    context.Log($"LOCAL_READABILITY_SELECTED mode={input.Result.SelectedMode} blocks={input.Result.Page.Blocks.Count} links={input.Result.Page.Links.Count}");
                RunGenerator(scriptPath, url, requestRoot, logPath, generatorMode, requestId, context, input.HtmlPath, input.ModelPath, enableReadableMerge: false, preferCurlFetch: false, searchResultMode: false);
                    generatedLocally = true;
                }
                catch (Exception ex)
                {
                    context.Log($"LOCAL_READABILITY_FALLBACK_DIRECT reason={Summarize(ex.Message)}");
                }
            }

            if (!generatedLocally)
            {
                bool enableFrogFind = _readableSourceMode == BrowserReadableSourceMode.FrogFind;
                context.Log(enableFrogFind ? "READABLE_SOURCE=FROGFIND_EXPLICIT" : "READABLE_SOURCE=DIRECT_FALLBACK");
                RunGenerator(scriptPath, url, requestRoot, logPath, generatorMode, requestId, context, null, null, enableFrogFind, preferSearchFetchProfile, searchResultMode: preferSearchFetchProfile);
            }
            context.Log("Generator completed");
            LogImageDiscoveryTrace(logPath, context);

            int linksCreated = 0;
            int linksReused = 0;
            int imageBudgetMs = Math.Max(0, BrowserGenerationDeadlineMs - (int)elapsed.ElapsedMilliseconds);
            ConvertFirstImage(outputPath, requestRoot, requestId, Math.Min(ImageConversionMaximumMs, imageBudgetMs), context);
            LogDocumentImageSummary(outputPath, context);
            NiceWireBuildResult result = NiceWireDocument.BuildFromJson(
                outputPath,
                context.MaxGeneratedPageBytes,
                mapLinks ? linkTarget => MapLinkTarget(linkTarget, requestId, context, ref linksCreated, ref linksReused) : null);

            elapsed.Stop();
            BrowserGeneratorError? generatorInfo = TryReadGeneratorInfo(logPath, Path.Combine(requestRoot, "error.json"));
            _sessions.RecordCall(new BrowserRouteCall(DateTime.UtcNow, ClientKey, SessionKey, route, RouteToken(route), url, "Success", generatorInfo?.HttpStatus ?? "200", (int)elapsed.ElapsedMilliseconds, requestId, string.Empty));
            context.Log("Page extraction completed");
            context.Log($"Canonical URL metadata: {result.CanonicalUrl ?? "(missing)"}");
            context.Log($"Links discovered: {linksCreated + linksReused}");
            context.Log($"Routes reused: {linksReused}");
            context.Log($"Routes created: {linksCreated}");
            context.Log($"Blocks extracted: {result.OriginalBlockCount}");
            context.Log($"Blocks retained: {result.RetainedBlockCount}");
            context.Log($"JSON bytes: {result.JsonBytes}");
            context.Log($"NICE wire bytes: {result.Wire.Length}");
            context.Log($"Truncated: {result.Truncated}");
            return result;
        }
        catch (BrowserPageException ex)
        {
            elapsed.Stop();
            _sessions.RecordCall(new BrowserRouteCall(DateTime.UtcNow, ClientKey, SessionKey, route, RouteToken(route), url, ex.Title, ex.HttpStatus, (int)elapsed.ElapsedMilliseconds, requestId, ex.Detail));
            throw;
        }
        catch (Exception ex)
        {
            elapsed.Stop();
            _sessions.RecordCall(new BrowserRouteCall(DateTime.UtcNow, ClientKey, SessionKey, route, RouteToken(route), url, "GeneratorFailed", string.Empty, (int)elapsed.ElapsedMilliseconds, requestId, ex.Message));
            throw;
        }
    }

    private string? MapLinkTarget(string target, string requestId, ModuleContext context, ref int linksCreated, ref int linksReused)
    {
        string originalTarget = target;
        if (TryUnwrapDuckDuckGoRedirect(target, out string unwrappedTarget, out string? unwrapFailure))
        {
            target = unwrappedTarget;
            context.Log("DDG_REDIRECT_UNWRAP");
            context.Log("wrapperHost=duckduckgo.com");
            context.Log($"targetHost={new Uri(target).Host}");
        }
        else if (unwrapFailure is not null)
        {
            context.Log("DDG_REDIRECT_UNWRAP_FAIL");
            context.Log($"reason={unwrapFailure}");
        }

        string url;
        try
        {
            url = ValidateUrl(target);
        }
        catch
        {
            return null;
        }

        bool existed = false;
        BrowserToken token = _sessions.CreateToken(ClientKey, SessionKey, url, requestId, BrowserTokenKind.Link);
        if (token.CreatedUtc < DateTime.UtcNow.AddSeconds(-1) || token.HitCount > 0)
        {
            existed = true;
        }

        if (existed) linksReused++; else linksCreated++;
        WriteRouteSnapshot(requestId, token, originalTarget, existed ? "reused" : "created");
        context.Log("Browser link token created");
        context.Log($"Client: {ClientKey}");
        context.Log($"Session: {SessionKey}");
        context.Log($"Page: {requestId}");
        context.Log($"Token: {token.Token}");
        context.Log($"Target: {token.TargetUrl}");
        context.Log($"Expires: {token.ExpiresUtc:O}");
        return token.Route;
    }

    /// <summary>
    /// Resolves the direct destination embedded in DuckDuckGo's HTML result redirect.
    /// The wrapper is retained by callers when this method returns <see langword="false"/>.
    /// </summary>
    public static bool TryUnwrapDuckDuckGoRedirect(string input, out string target, out string? failureReason)
    {
        target = input;
        failureReason = null;

        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? wrapper) ||
            !IsDuckDuckGoHost(wrapper.Host) ||
            !IsDuckDuckGoRedirectPath(wrapper.AbsolutePath))
        {
            return false;
        }

        // Uri.Query repairs malformed percent escapes. Read the original query to
        // reject malformed wrapper input before decoding the embedded target.
        string? encodedTarget = GetQueryValue(GetRawQuery(input), "uddg");
        if (encodedTarget is null)
        {
            failureReason = "missing_uddg";
            return false;
        }

        if (encodedTarget.Length == 0)
        {
            failureReason = "empty_uddg";
            return false;
        }

        if (HasInvalidPercentEncoding(encodedTarget))
        {
            failureReason = "decode_failed";
            return false;
        }

        string decodedTarget;
        try
        {
            decodedTarget = Uri.UnescapeDataString(encodedTarget);
        }
        catch (UriFormatException)
        {
            failureReason = "decode_failed";
            return false;
        }

        if (!Uri.TryCreate(decodedTarget, UriKind.Absolute, out Uri? destination))
        {
            failureReason = "invalid_uri";
            return false;
        }

        if ((destination.Scheme != Uri.UriSchemeHttp && destination.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(destination.Host))
        {
            failureReason = "unsupported_scheme";
            return false;
        }

        target = destination.AbsoluteUri;
        return true;
    }

    private static bool IsDuckDuckGoHost(string host) =>
        string.Equals(host, "duckduckgo.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "www.duckduckgo.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsDuckDuckGoRedirectPath(string path) =>
        string.Equals(path.TrimEnd('/'), "/l", StringComparison.OrdinalIgnoreCase);

    private static string? GetQueryValue(string query, string name)
    {
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.None))
        {
            int equals = pair.IndexOf('=');
            string key = equals < 0 ? pair : pair[..equals];
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return equals < 0 ? string.Empty : pair[(equals + 1)..];
            }
        }

        return null;
    }

    private static string GetRawQuery(string input)
    {
        int queryStart = input.IndexOf('?');
        if (queryStart < 0)
        {
            return string.Empty;
        }

        int fragmentStart = input.IndexOf('#', queryStart + 1);
        return fragmentStart < 0 ? input[(queryStart + 1)..] : input[(queryStart + 1)..fragmentStart];
    }

    private static bool HasInvalidPercentEncoding(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '%' &&
                (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2])))
            {
                return true;
            }
        }

        return false;
    }

    private void ConvertFirstImage(string documentPath, string requestRoot, string requestId, int timeoutMs, ModuleContext context)
    {
        int imageIndex = -1;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(documentPath));
            JsonElement image = default;
            int blockIndex = 0;
            foreach (JsonElement block in document.RootElement.GetProperty("blocks").EnumerateArray())
            {
                if (block.TryGetProperty("type", out JsonElement type) &&
                    string.Equals(type.GetString(), "IMAGE_PLACEHOLDER", StringComparison.OrdinalIgnoreCase) &&
                    block.TryGetProperty("target", out JsonElement target) && !string.IsNullOrWhiteSpace(target.GetString()))
                {
                    image = block;
                    imageIndex = blockIndex;
                    break;
                }
                blockIndex++;
            }
            if (imageIndex < 0)
            {
                context.Log("IMAGE PIPELINE no selected image block");
                return;
            }

            if (timeoutMs < 1000)
            {
                context.Log($"IMAGE BLOCK REMOVED reason=INSUFFICIENT_JOB_BUDGET remainingMs={timeoutMs}");
                RewriteImageBlock(documentPath, imageIndex, null);
                return;
            }

            string sourceUrl = image.GetProperty("target").GetString()!;
            string caption = image.TryGetProperty("text", out JsonElement text) ? text.GetString() ?? string.Empty : string.Empty;
            string outputPath = Path.Combine(requestRoot, "images", "lead.nimg");
            string script = Path.Combine(_package.PackageRoot, "scripts", "Convert-NiceImage.ps1");
            int pendingWidth = image.TryGetProperty("imageWidth", out JsonElement imageWidth) ? imageWidth.GetInt32() : 0;
            int pendingHeight = image.TryGetProperty("imageHeight", out JsonElement imageHeight) ? imageHeight.GetInt32() : 0;
            context.Log($"IMAGE BLOCK BEFORE REWRITE index={imageIndex} route={sourceUrl} size={pendingWidth}x{pendingHeight}");
            List<string> sources = GetImageConversionCandidates(document.RootElement, sourceUrl);
            for (int candidateIndex = 0; candidateIndex < sources.Count; candidateIndex++)
            {
                string candidateUrl = sources[candidateIndex];
                BrowserImageFitMode mode = SelectImageFitMode(candidateUrl, caption);
                context.Log($"Browser image source={candidateUrl}");
                context.Log($"Browser image mode={mode.ToString().ToUpperInvariant()} policy=metadata");
                if (File.Exists(outputPath)) File.Delete(outputPath);
                if (!RunImageConverter(script, candidateUrl, outputPath, mode, timeoutMs, context) || !File.Exists(outputPath))
                {
                    if (candidateIndex == 0 && sources.Count > 1) context.Log("IMAGE FALLBACK candidate=2 reason=FIRST_CONVERSION_FAILED");
                    continue;
                }
                if (!BrowserImageContract.TryReadNimg(outputPath, out ushort width, out ushort height, out string reason))
                {
                    context.Log($"IMAGE CONVERSION REJECTED reason=NIMG_INVALID detail={reason}");
                    File.Delete(outputPath);
                    if (candidateIndex == 0 && sources.Count > 1) context.Log("IMAGE FALLBACK candidate=2 reason=FIRST_NIMG_INVALID");
                    continue;
                }
                LogNimgValidation(outputPath, width, height, context);
                BrowserImageEntry entry = _images.Register(requestId, candidateUrl, outputPath, width, height);
                if (entry.Width != width || entry.Height != height)
                {
                    context.Log("IMAGE CONVERSION REJECTED reason=REGISTRY_MISMATCH");
                    if (candidateIndex == 0 && sources.Count > 1) context.Log("IMAGE FALLBACK candidate=2 reason=FIRST_REGISTRY_MISMATCH");
                    continue;
                }
                RewriteImageBlock(documentPath, imageIndex, entry);
                context.Log($"IMAGE BLOCK AFTER REWRITE route={entry.Route} size={entry.Width}x{entry.Height}");
                context.Log($"IMAGE REGISTRY route={entry.Route} file={entry.FilePath} page={entry.SourcePageId} size={entry.Width}x{entry.Height}");
                context.Log($"Browser image contract NIMG={width}x{height} registry={entry.Width}x{entry.Height} block={entry.Width}x{entry.Height}");
                context.Log($"Browser lead image registered token={entry.Token} bytes={entry.Bytes} size={entry.Width}x{entry.Height}");
                return;
            }
            context.Log("IMAGE BLOCK REMOVED reason=CONVERSION_FAILED");
            RewriteImageBlock(documentPath, imageIndex, null);
        }
        catch (Exception ex)
        {
            if (imageIndex >= 0)
            {
                try { RewriteImageBlock(documentPath, imageIndex, null); } catch { }
            }
            context.Log($"IMAGE BLOCK REMOVED reason=UNKNOWN detail={ex.Message}");
        }
    }

    private static List<string> GetImageConversionCandidates(JsonElement document, string selectedSource)
    {
        List<string> candidates = [selectedSource];
        if (!document.TryGetProperty("imageFallbacks", out JsonElement fallbacks) || fallbacks.ValueKind != JsonValueKind.Array) return candidates;
        foreach (JsonElement fallback in fallbacks.EnumerateArray())
        {
            string? value = fallback.GetString();
            if (string.IsNullOrWhiteSpace(value) || candidates.Contains(value, StringComparer.Ordinal)) continue;
            candidates.Add(value);
            if (candidates.Count == 2) break;
        }
        return candidates;
    }

    private static BrowserImageFitMode SelectImageFitMode(string sourceUrl, string caption)
    {
        string classification = (sourceUrl + " " + caption).ToLowerInvariant();
        if (classification.Contains("hero") || classification.Contains("photo") || classification.Contains("banner") || classification.Contains("cover"))
        {
            return BrowserImageFitMode.Fill;
        }
        return BrowserImageFitMode.Fit;
    }

    private static void RewriteImageBlock(string documentPath, int imageIndex, BrowserImageEntry? entry)
    {
        using JsonDocument source = JsonDocument.Parse(File.ReadAllText(documentPath));
        using FileStream stream = File.Create(documentPath);
        using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        foreach (JsonProperty property in source.RootElement.EnumerateObject())
        {
            if (property.Name != "blocks") { property.WriteTo(writer); continue; }
            writer.WritePropertyName("blocks");
            writer.WriteStartArray();
            int blockIndex = 0;
            foreach (JsonElement block in property.Value.EnumerateArray())
            {
                bool replace = blockIndex++ == imageIndex;
                if (replace && entry is null) continue;
                writer.WriteStartObject();
                foreach (JsonProperty field in block.EnumerateObject())
                {
                    if (replace && field.Name is "target" or "imageWidth" or "imageHeight") continue;
                    field.WriteTo(writer);
                }
                if (replace)
                {
                    writer.WriteString("target", entry!.Route);
                    writer.WriteNumber("imageWidth", entry.Width);
                    writer.WriteNumber("imageHeight", entry.Height);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static bool RunImageConverter(string script, string sourceUrl, string outputPath, BrowserImageFitMode mode, int timeoutMs, ModuleContext context)
    {
        if (!File.Exists(script)) return false;
        Stopwatch elapsed = Stopwatch.StartNew();
        context.Log($"IMAGE CONVERSION START source={sourceUrl} destination={outputPath} mode={mode.ToString().ToUpperInvariant()}");
        ProcessStartInfo start = new() { FileName = ResolvePowerShell(), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass"); start.ArgumentList.Add("-File"); start.ArgumentList.Add(script);
        start.ArgumentList.Add("-Url"); start.ArgumentList.Add(sourceUrl); start.ArgumentList.Add("-OutputPath"); start.ArgumentList.Add(outputPath);
        start.ArgumentList.Add("-Mode"); start.ArgumentList.Add(mode.ToString().ToUpperInvariant());
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("IMAGE CONVERTER START FAILED");
        if (!process.WaitForExit(timeoutMs)) { try { process.Kill(true); } catch { } elapsed.Stop(); context.Log($"IMAGE CONVERSION FAILED exit=TIMEOUT elapsedMs={elapsed.ElapsedMilliseconds}"); return false; }
        string output = Summarize(process.StandardOutput.ReadToEnd());
        string error = Summarize(process.StandardError.ReadToEnd());
        elapsed.Stop();
        context.Log($"IMAGE CONVERSION {(process.ExitCode == 0 ? "COMPLETE" : "FAILED")} exit={process.ExitCode} elapsedMs={elapsed.ElapsedMilliseconds} output={output} error={error}");
        return process.ExitCode == 0;
    }

    private static void LogImageDiscoveryTrace(string logPath, ModuleContext context)
    {
        if (!File.Exists(logPath)) return;
        foreach (string line in File.ReadLines(logPath).Where(line => line.Contains("IMAGE CANDIDATE", StringComparison.OrdinalIgnoreCase)))
        {
            context.Log(line);
        }
    }

    private static void LogDocumentImageSummary(string documentPath, ModuleContext context)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(documentPath));
        JsonElement[] images = document.RootElement.GetProperty("blocks").EnumerateArray().Where(block =>
            block.TryGetProperty("type", out JsonElement type) && string.Equals(type.GetString(), "IMAGE_PLACEHOLDER", StringComparison.OrdinalIgnoreCase)).ToArray();
        context.Log($"DOCUMENT SUMMARY imageBlocks={images.Length}");
        foreach (JsonElement image in images)
        {
            string route = image.TryGetProperty("target", out JsonElement target) ? target.GetString() ?? "(missing)" : "(missing)";
            int width = image.TryGetProperty("imageWidth", out JsonElement imageWidth) ? imageWidth.GetInt32() : 0;
            int height = image.TryGetProperty("imageHeight", out JsonElement imageHeight) ? imageHeight.GetInt32() : 0;
            context.Log($"DOCUMENT IMAGE route={route} size={width}x{height}");
        }
    }

    private static void LogNimgValidation(string path, ushort width, ushort height, ModuleContext context)
    {
        byte[] header = File.ReadAllBytes(path).Take(BrowserImageContract.NimgHeaderSize).ToArray();
        ushort version = (ushort)((header[4] << 8) | header[5]);
        ushort format = (ushort)((header[10] << 8) | header[11]);
        uint payload = ((uint)header[12] << 24) | ((uint)header[13] << 16) | ((uint)header[14] << 8) | header[15];
        uint expected = (uint)width * height * BrowserImageContract.BytesPerPixel;
        context.Log($"NIMG VALID magic={Encoding.ASCII.GetString(header, 0, 4)} version={version} format={format} size={width}x{height} payload={payload} expected={expected} file={path}");
    }

    private void WriteRouteSnapshot(string requestId, BrowserToken token, string originalHref, string disposition)
    {
        try
        {
            string root = Path.Combine(_sessions.RequestsRoot, requestId);
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, disposition == "created" ? "routes-created.csv" : "routes-reused.csv");
            bool header = !File.Exists(path) || new FileInfo(path).Length == 0;
            using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using StreamWriter writer = new(stream, new UTF8Encoding(false));
            if (header) writer.WriteLine("SourcePageId,Token,NormalizedUrl,OriginalHref,Disposition,RegistryRevision");
            writer.WriteLine(string.Join(",", [Csv(requestId), Csv(token.Token), Csv(token.TargetUrl), Csv(originalHref), disposition, _sessions.Revision.ToString()]));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            // Snapshots are diagnostics. Route persistence remains authoritative.
        }
    }

    private static string Csv(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

    private NetworkPakResource OpenPoll(string resource, ModuleContext context)
    {
        string jobId = resource[PollRoutePrefix.Length..];
        BrowserGenerationJob? job;
        lock (_jobsLock)
        {
            CleanupJobsLocked();
            _jobsById.TryGetValue(jobId, out job);
            if (job is not null) job.PollCount++;
        }
        if (job is null)
        {
            return OpenWire(resource, NiceWireDocument.BuildErrorDocument("JOB NOT FOUND", "The Bridge no longer has this page request."), context);
        }
        context.Log($"Browser job poll job={job.JobId} state={job.State} count={job.PollCount}");
        return job.State switch
        {
            BrowserGenerationState.Ready when job.WirePayload is not null => OpenWire(resource, job.WirePayload, context),
            BrowserGenerationState.Failed when job.ErrorPayload is not null => OpenWire(resource, job.ErrorPayload, context),
            BrowserGenerationState.Expired => OpenWire(resource, NiceWireDocument.BuildErrorDocument("JOB EXPIRED", "The Bridge discarded this page request."), context),
            BrowserGenerationState.Cancelled => OpenWire(resource, NiceWireDocument.BuildErrorDocument("JOB CANCELLED", "This page request was cancelled."), context),
            _ => OpenWire(resource, BuildLoadingDocument(job), context)
        };
    }

    private BrowserGenerationJob GetOrCreateJob(string url, string route, string mode, ModuleContext context)
    {
        url = ValidateUrl(url);
        BrowserGenerationJob? job;
        lock (_jobsLock)
        {
            CleanupJobsLocked();
            if (_jobsByUrl.TryGetValue(url, out job) && job.State == BrowserGenerationState.Failed)
            {
                _jobsById.Remove(job.JobId);
                _jobsByUrl.Remove(url);
                context.Log($"Browser job retry after failure previousJob={job.JobId} url={url}");
                job = null;
            }
            if (job is not null && job.State is not BrowserGenerationState.Expired and not BrowserGenerationState.Cancelled)
            {
                context.Log($"Browser job reused job={job.JobId} state={job.State} url={url}");
                return job;
            }
            if (_jobsById.Count >= MaxJobs || _jobsById.Values.Count(item => item.State is BrowserGenerationState.Pending or BrowserGenerationState.Fetching or BrowserGenerationState.Converting or BrowserGenerationState.Encoding) >= MaxPendingJobs)
            {
                throw new BrowserPageException("JOB LIMIT REACHED", "The Bridge is already preparing too many pages.", string.Empty);
            }
            job = new BrowserGenerationJob
            {
                JobId = AllocateJobIdLocked(), ClientId = ClientKey, SessionId = SessionKey, NormalizedUrl = url,
                RequestDirectory = string.Empty, CreatedUtc = DateTime.UtcNow, State = BrowserGenerationState.Pending
            };
            _jobsById.Add(job.JobId, job);
            _jobsByUrl[url] = job;
        }
        context.Log($"Browser job created job={job.JobId} client={ClientKey} session={SessionKey} url={url}");
        Task.Run(() => RunJob(job, route, mode, context));
        return job;
    }

    private void RunJob(BrowserGenerationJob job, string route, string mode, ModuleContext context)
    {
        try
        {
            lock (_jobsLock) job.State = BrowserGenerationState.Fetching;
            context.Log($"Browser generator start job={job.JobId}");
            NiceWireBuildResult result = GenerateWireForUrl(job.NormalizedUrl, route, mode, context, mapLinks: true);
            lock (_jobsLock)
            {
                if (!job.Cancelled) { job.WirePayload = result.Wire; job.State = BrowserGenerationState.Ready; job.CompletedUtc = DateTime.UtcNow; }
            }
            context.Log($"Browser job ready job={job.JobId} bytes={result.Wire.Length}");
        }
        catch (Exception ex)
        {
            lock (_jobsLock)
            {
                if (!job.Cancelled) { job.ErrorPayload = BuildErrorWire(ex); job.ErrorCode = ex.Message; job.State = BrowserGenerationState.Failed; job.CompletedUtc = DateTime.UtcNow; }
            }
            context.Log($"Browser job failed job={job.JobId} error={ex.Message}");
        }
    }

    private static byte[] BuildLoadingDocument(BrowserGenerationJob job)
    {
        string stage = job.State switch { BrowserGenerationState.Pending => "QUEUED", BrowserGenerationState.Fetching => "FETCHING", BrowserGenerationState.Converting => "CONVERTING", BrowserGenerationState.Encoding => "ENCODING", _ => "WAITING" };
        return NiceWireDocument.Build(0x00001002u,
        [new(0, "NICE64 Browser", null, 0, 0), new(1, "Loading Page", null, 0, 0), new(3, stage, null, 0, 0), new(3, "Waiting for Bridge...", null, 0, 0), new(6, "Poll", PollRoutePrefix + job.JobId, 0, 0), new(9, null, null, 0, 0)]);
    }

    private string AllocateJobIdLocked()
    {
        for (int attempts = 0; attempts < 0xFFFF; attempts++)
        {
            _nextJobId++; if (_nextJobId == 0) _nextJobId = 1;
            string id = _nextJobId.ToString("X4");
            if (!_jobsById.ContainsKey(id)) return id;
        }
        throw new InvalidOperationException("JOB TABLE FULL");
    }

    private void CleanupJobsLocked()
    {
        DateTime now = DateTime.UtcNow;
        foreach (BrowserGenerationJob job in _jobsById.Values.ToArray())
        {
            TimeSpan retention = job.State == BrowserGenerationState.Failed ? FailedRetention : ReadyRetention;
            if (job.CompletedUtc.HasValue && now - job.CompletedUtc.Value > retention)
            {
                job.State = BrowserGenerationState.Expired;
                _jobsById.Remove(job.JobId);
                _jobsByUrl.Remove(job.NormalizedUrl);
            }
        }
    }

    private void EnsurePrefetchStarted(BrowserToken token, ModuleContext context)
    {
        if (token.PrefetchStarted || token.CachedWire is not null)
        {
            return;
        }

        token.PrefetchStarted = true;
        Task.Run(() =>
        {
            try
            {
                NiceWireBuildResult result = GenerateWireForUrl(token.TargetUrl, token.Route, "AUTO", context, mapLinks: false);
                token.CachedWire = result.Wire;
                context.Log($"Browser link prefetch completed token={token.Token} bytes={result.Wire.Length}");
            }
            catch (Exception ex)
            {
                token.CachedWire = BuildErrorWire(ex);
                context.Log($"Browser link prefetch failed token={token.Token} error={ex.Message}");
            }
        });
    }

    private static byte[] BuildHomeDocument()
    {
        NiceWireBlock[] blocks =
        [
            new(0, "NICE64 Browser", null, 0, 0),
            new(1, "Welcome.", null, 0, 0),
            new(6, "Enter URL", "nice://enter", 0, 0),
            new(6, "CNN Lite", "browser://live?u=cnn", 0, 0),
            new(6, "Wikipedia N64", "browser://live?u=wiki", 0, 0),
            new(6, "Browser Test", "browser://test", 0, 0),
            new(8, null, null, 0, 0),
            new(2, "Navigation", null, 0, 0),
            new(3, "Links use short Bridge tokens. Full URLs stay in the Bridge session store.", null, 0, 0),
            new(9, null, null, 0, 0)
        ];

        return NiceWireDocument.Build(0x00001001u, blocks);
    }

    private static string ResolveTypedInputToUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidDataException("Empty URL");
        }

        if (input.Equals("cnn", StringComparison.OrdinalIgnoreCase))
        {
            return "https://lite.cnn.com";
        }

        if (input.Equals("wiki", StringComparison.OrdinalIgnoreCase))
        {
            return "https://en.wikipedia.org/wiki/Nintendo_64";
        }

        return ValidateUrl(input);
    }

    private string ResolveSearchQueryToUrl(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidDataException("Empty search query");
        }

        return ValidateUrl(_searchProviderUrl.Replace("{query}", Uri.EscapeDataString(query.Trim()), StringComparison.Ordinal));
    }

    private static bool IsKeyboardSearchToken(BrowserToken token)
    {
        return string.Equals(token.SourcePageId, "keyboard-search", StringComparison.Ordinal);
    }

    private static string NormalizeSearchProviderUrl(string? configuredTemplate)
    {
        string template = string.IsNullOrWhiteSpace(configuredTemplate) ? DefaultSearchProviderUrl : configuredTemplate.Trim();
        if (!template.Contains("{query}", StringComparison.Ordinal))
        {
            return DefaultSearchProviderUrl;
        }

        try
        {
            ValidateUrl(template.Replace("{query}", "nice64", StringComparison.Ordinal));
            return template;
        }
        catch
        {
            return DefaultSearchProviderUrl;
        }
    }

    private static string ValidateUrl(string input)
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

        return BrowserSessionStore.NormalizeUrl(uri.AbsoluteUri);
    }

    private string CreateRequestRoot(string requestId)
    {
        string root = Path.GetFullPath(_sessions.RequestsRoot);
        string requestRoot = Path.GetFullPath(Path.Combine(root, requestId));
        if (!requestRoot.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("REQUEST DIRECTORY FAILED");
        }

        Directory.CreateDirectory(requestRoot);
        Directory.CreateDirectory(Path.Combine(requestRoot, "images"));
        return requestRoot;
    }

    private static void WriteRequestJson(string path, string route, string url, string mode, string requestId, ModuleContext context)
    {
        var request = new
        {
            route,
            url,
            mode,
            requestId,
            picoId = ClientKey,
            sessionId = SessionKey,
            bridgeVersion = context.BridgeVersion,
            generatedAtUtc = DateTime.UtcNow.ToString("O")
        };

        File.WriteAllText(path, JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static LocalReadableInput PrepareLocalReadableInput(string url, string requestRoot, ModuleContext context)
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(GeneratorFetchTimeoutSeconds));
        context.Log("LOCAL_READABILITY_FETCH_STARTED");
        using HttpResponseMessage response = OriginalPageClient.GetAsync(url, cancellation.Token).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ORIGINAL_HTTP_{(int)response.StatusCode}");
        }

        string html = response.Content.ReadAsStringAsync(cancellation.Token).GetAwaiter().GetResult();
        if (html.Length < 80)
        {
            throw new InvalidOperationException("ORIGINAL_TINY_BODY");
        }

        Uri finalUrl = response.RequestMessage?.RequestUri ?? new Uri(url);
        ReadableExtractionResult result = new NiceReadableExtractor().Extract(html, finalUrl, cancellation.Token);
        context.Log($"LOCAL_READABILITY_RESULT mode={result.SelectedMode} quality={result.PassesQualityGate} blocks={result.Page.Blocks.Count} links={result.Page.Links.Count} parseMs={result.Page.Diagnostics.ParseMilliseconds} extractMs={result.Page.Diagnostics.ExtractionMilliseconds}");
        if (!result.PassesQualityGate)
        {
            throw new InvalidOperationException("LOCAL_QUALITY_GATE_FAILED:" + string.Join(',', result.FailureReasons));
        }

        string htmlPath = Path.Combine(requestRoot, "original-source.html");
        string modelPath = Path.Combine(requestRoot, "local-readable-model.json");
        File.WriteAllText(htmlPath, html, Encoding.UTF8);
        File.WriteAllText(modelPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return new LocalReadableInput(htmlPath, modelPath, result);
    }

    private static HttpClient CreateOriginalPageClient()
    {
        HttpClientHandler handler = new() { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        return client;
    }

    private sealed record LocalReadableInput(string HtmlPath, string ModelPath, ReadableExtractionResult Result);

    private static void RunGenerator(
        string scriptPath,
        string url,
        string outputDirectory,
        string logPath,
        string mode,
        string requestId,
        ModuleContext context,
        string? inputHtmlPath,
        string? localReadableModelPath,
        bool enableReadableMerge,
        bool preferCurlFetch,
        bool searchResultMode)
    {
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("POWERSHELL START FAILED", scriptPath);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = ResolvePowerShell(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-Url");
        startInfo.ArgumentList.Add(url);
        startInfo.ArgumentList.Add("-OutputDirectory");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("-PicoId");
        startInfo.ArgumentList.Add(ClientKey);
        startInfo.ArgumentList.Add("-SessionId");
        startInfo.ArgumentList.Add(SessionKey);
        startInfo.ArgumentList.Add("-RequestId");
        startInfo.ArgumentList.Add(requestId);
        startInfo.ArgumentList.Add("-Mode");
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add("-TimeoutSeconds");
        startInfo.ArgumentList.Add(GeneratorFetchTimeoutSeconds.ToString());
        startInfo.ArgumentList.Add("-TotalGenerationBudgetSeconds");
        startInfo.ArgumentList.Add(GeneratorTotalBudgetSeconds.ToString());
        startInfo.ArgumentList.Add("-EnableReadableMerge");
        startInfo.ArgumentList.Add(enableReadableMerge ? "true" : "false");
        startInfo.ArgumentList.Add("-PreferCurlFetch");
        startInfo.ArgumentList.Add(preferCurlFetch ? "1" : "0");
        startInfo.ArgumentList.Add("-SearchResultMode");
        startInfo.ArgumentList.Add(searchResultMode ? "1" : "0");
        if (!string.IsNullOrWhiteSpace(inputHtmlPath))
        {
            startInfo.ArgumentList.Add("-InputHtmlPath");
            startInfo.ArgumentList.Add(inputHtmlPath);
        }
        if (!string.IsNullOrWhiteSpace(localReadableModelPath))
        {
            startInfo.ArgumentList.Add("-LocalReadableModelPath");
            startInfo.ArgumentList.Add(localReadableModelPath);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("POWERSHELL START FAILED");
        if (!process.WaitForExit(GeneratorProcessTimeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cleanup only.
            }

            context.Log($"Generator timed out after {GeneratorProcessTimeoutMs}ms");
            throw new BrowserPageException("PAGE GENERATION TIMED OUT", "The Bridge took too long to generate this page.", string.Empty);
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        File.AppendAllText(logPath, stdout);
        File.AppendAllText(logPath, stderr);

        context.Log($"PowerShell exit code: {process.ExitCode}");
        if (process.ExitCode != 0)
        {
            BrowserGeneratorError generatorError = ParseGeneratorError(logPath);
            context.Log($"Generator stderr summary: {Summarize(stderr)}");
            context.Log($"Generator friendly error: {generatorError.Title} code={generatorError.Code} http={generatorError.HttpStatus} exception={generatorError.ExceptionType} elapsedMs={generatorError.ElapsedMs}");
            throw new BrowserPageException(generatorError.Title, generatorError.Detail, generatorError.HttpStatus);
        }

        if (!File.Exists(Path.Combine(outputDirectory, "document.json")))
        {
            throw new FileNotFoundException("DOCUMENT JSON MISSING");
        }
    }

    private static string? ReadQueryValue(string route, string key)
    {
        int question = route.IndexOf('?');
        if (question < 0 || question == route.Length - 1)
        {
            return null;
        }

        foreach (string part in route[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }

    private static string SanitizeId(string value, int maxLength)
    {
        string sanitized = SafeComponent.Replace(value, "-").Trim('.', '-', '_');
        if (sanitized.Length == 0)
        {
            sanitized = "id";
        }
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength];
    }

    private static string ResolvePowerShell()
    {
        return OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
    }

    private static byte[] BuildErrorWire(Exception ex)
    {
        if (ex is BrowserPageException pageException)
        {
            return NiceWireDocument.BuildErrorDocument(pageException.Title, pageException.Detail);
        }

        return NiceWireDocument.BuildErrorDocument("GENERATOR FAILED", ex.Message);
    }

    public static BrowserGeneratorError ParseGeneratorError(string logPath)
    {
        string errorJsonPath = Path.Combine(Path.GetDirectoryName(logPath) ?? string.Empty, "error.json");
        BrowserGeneratorError? jsonError = TryReadGeneratorInfo(logPath, errorJsonPath);
        if (jsonError is not null && jsonError.Code != "OK")
        {
            return jsonError;
        }

        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(logPath))
        {
            foreach (string line in File.ReadLines(logPath))
            {
                int start = line.IndexOf("] ", StringComparison.Ordinal);
                string text = start >= 0 ? line[(start + 2)..] : line;
                int equals = text.IndexOf('=');
                if (equals > 0)
                {
                    values[text[..equals].Trim()] = text[(equals + 1)..].Trim();
                }
            }
        }

        string title = values.GetValueOrDefault("ERROR_TITLE", "CONNECTION FAILED");
        string detail = values.GetValueOrDefault("ERROR_DETAIL", "The Bridge could not download this page.");
        string code = values.GetValueOrDefault("ERROR_CODE", "GENERATOR_EXIT_FAILED");
        string httpStatus = values.GetValueOrDefault("HTTP_STATUS", string.Empty);
        string exceptionType = values.GetValueOrDefault("EXCEPTION_TYPE", string.Empty);
        int.TryParse(values.GetValueOrDefault("FETCH_ELAPSED_MS", "0"), out int elapsedMs);
        return new BrowserGeneratorError(title, detail, code, httpStatus, exceptionType, elapsedMs);
    }

    private static BrowserGeneratorError? TryReadGeneratorInfo(string logPath, string errorJsonPath)
    {
        if (File.Exists(errorJsonPath))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(errorJsonPath));
                JsonElement root = document.RootElement;
                string title = ReadJsonString(root, "title") ?? "CONNECTION FAILED";
                string detail = ReadJsonString(root, "message") ?? "The Bridge could not download this page.";
                string category = ReadJsonString(root, "category") ?? "GENERATOR";
                string httpStatus = root.TryGetProperty("httpStatus", out JsonElement status) && status.ValueKind == JsonValueKind.Number
                    ? status.GetInt32().ToString()
                    : string.Empty;
                return new BrowserGeneratorError(title, detail, category, httpStatus, string.Empty, 0);
            }
            catch
            {
                // Fall back to generator.log parsing.
            }
        }

        if (!File.Exists(logPath))
        {
            return null;
        }

        string? statusLine = File.ReadLines(logPath).LastOrDefault(line => line.Contains("HTTP_STATUS=", StringComparison.Ordinal));
        if (statusLine is not null)
        {
            string status = statusLine[(statusLine.IndexOf("HTTP_STATUS=", StringComparison.Ordinal) + "HTTP_STATUS=".Length)..].Trim();
            return new BrowserGeneratorError("OK", string.Empty, "OK", status, string.Empty, 0);
        }

        return null;
    }

    private static string? ReadJsonString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string Summarize(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 220 ? value : value[..220];
    }

    private static string RouteToken(string route)
    {
        int slash = route.LastIndexOf('/');
        return slash >= 0 && slash + 1 < route.Length ? route[(slash + 1)..] : string.Empty;
    }

    private sealed record RouteTarget(string? Url, string Mode, byte[]? Wire, BrowserToken? Token)
    {
        public static RouteTarget ForUrl(string url, string mode) => new(url, mode, null, null);
        public static RouteTarget ForWire(byte[] wire) => new(null, "ARTICLE", wire, null);
        public static RouteTarget ForToken(BrowserToken token, string mode) => new(token.TargetUrl, mode, null, token);
    }

    public sealed record BrowserGeneratorError(string Title, string Detail, string Code, string HttpStatus, string ExceptionType, int ElapsedMs);

    private sealed class BrowserPageException : Exception
    {
        public BrowserPageException(string title, string detail, string httpStatus)
            : base(title)
        {
            Title = title;
            Detail = detail;
            HttpStatus = httpStatus;
        }

        public string Title { get; }
        public string Detail { get; }
        public string HttpStatus { get; }
    }
}
