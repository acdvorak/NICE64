namespace PicoSwitchController;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed record BridgeApiHostOptions
{
    public const int DefaultPort = 6470;
    public const string EnabledEnvironmentVariable = "NICE64_BRIDGE_API_ENABLED";
    public const string PortEnvironmentVariable = "NICE64_BRIDGE_API_PORT";

    public bool Enabled { get; init; }
    public int Port { get; init; } = DefaultPort;
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    internal IReadOnlyList<BridgeApiResolvedListener>? Listeners { get; init; }

    public static BridgeApiHostOptions FromEnvironment()
    {
        string? enabledValue = Environment.GetEnvironmentVariable(
            EnabledEnvironmentVariable);
        bool enabled = enabledValue?.Trim().ToLowerInvariant() is
            "1" or "true" or "yes" or "on";
        int port = int.TryParse(Environment.GetEnvironmentVariable(
                PortEnvironmentVariable), out int configuredPort)
            ? configuredPort
            : DefaultPort;
        return new() { Enabled = enabled, Port = port };
    }

    internal void Validate()
    {
        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port),
                "The local API port must be in the range 1..65535.");
        if (ShutdownTimeout <= TimeSpan.Zero || ShutdownTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout),
                "The local API shutdown timeout must be positive and bounded.");
        IReadOnlyList<BridgeApiResolvedListener> listeners = GetListeners();
        if (listeners.Count is < 1 or > 2 ||
            !listeners[0].Address.Equals(IPAddress.Loopback) ||
            listeners[0].Security != BridgeApiListenerSecurity.Http ||
            listeners.Any(listener => listener.Port != Port) ||
            listeners.Select(listener => listener.Address).Distinct().Count() !=
                listeners.Count ||
            (listeners.Count == 2 &&
                listeners[1].Address.Equals(IPAddress.Loopback)))
            throw new ArgumentException(
                "API listeners must be explicit IPv4 addresses beginning with loopback.",
                nameof(Listeners));
    }

    internal IReadOnlyList<BridgeApiResolvedListener> GetListeners() =>
        Listeners ?? Array.AsReadOnly(new[] {
            BridgeApiResolvedListener.Http(IPAddress.Loopback, Port) });
}

public sealed record BridgeApiV1Health(string ApiVersion, string Status);

internal sealed record BridgeApiV1HostFailure(string Code, string Detail);

public sealed class BridgeApiHost : IBridgeApiHost
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly IBridgeStateProvider _state;
    private readonly IBridgeApiTokenProvider _tokens;
    private readonly BridgeApiHostOptions _options;
    private readonly IBridgeApiCommandFacade? _commands;
    private readonly IBridgeApiV1HistoryProvider? _history;
    private readonly IBridgeSessionProvider? _sessions;
    private readonly IBridgeApiV1ControllerFacade? _controller;
    private readonly IBridgeApiV1HistoryActivationFacade? _historyActivation;
    private readonly Action<string>? _log;
    private WebApplication? _application;
    private bool _startAttempted;
    private bool _disposed;
    private BridgeApiHostStartResult? _startResult;

    public BridgeApiHost(
        IBridgeStateProvider state,
        IBridgeApiTokenProvider tokens,
        BridgeApiHostOptions options,
        Action<string>? log = null)
        : this(state, tokens, options, null, log)
    {
    }

    public BridgeApiHost(
        IBridgeStateProvider state,
        IBridgeApiTokenProvider tokens,
        BridgeApiHostOptions options,
        IBridgeApiCommandFacade? commands,
        Action<string>? log = null,
        IBridgeApiV1HistoryProvider? history = null,
        IBridgeSessionProvider? sessions = null,
        IBridgeApiV1ControllerFacade? controller = null)
        : this(state, tokens, options, commands, log, history, sessions,
            controller, null)
    {
    }

    internal BridgeApiHost(
        IBridgeStateProvider state,
        IBridgeApiTokenProvider tokens,
        BridgeApiHostOptions options,
        IBridgeApiCommandFacade? commands,
        Action<string>? log,
        IBridgeApiV1HistoryProvider? history,
        IBridgeSessionProvider? sessions,
        IBridgeApiV1ControllerFacade? controller,
        IBridgeApiV1HistoryActivationFacade? historyActivation)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _commands = commands;
        _history = history;
        _sessions = sessions;
        _controller = controller;
        _historyActivation = historyActivation;
        _log = log;
    }

    public bool IsRunning { get; private set; }

    public async Task<BridgeApiHostStartResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning)
            {
                return new(BridgeApiHostStartDisposition.AlreadyRunning, true, null, null);
            }
            if (_startAttempted)
                return _startResult!;

            _startAttempted = true;
            if (!_options.Enabled)
            {
                return _startResult = new(BridgeApiHostStartDisposition.Disabled,
                    false, "host-disabled", "The optional local API host is disabled.");
            }

            WebApplication? application = null;
            try
            {
                _options.Validate();
                string token = _tokens.GetToken();
                if (token.Length < 32)
                    throw new InvalidOperationException(
                        "The local API token source returned an invalid token.");

                application = CreateApplication(
                    _state, token, _options, _log, commands: _commands,
                    history: _history, sessions: _sessions,
                    controller: _controller,
                    historyActivation: _historyActivation);
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
                _application = application;
                IsRunning = true;
                Log($"Local API started on {_options.GetListeners().Count} explicit listener(s) at port {_options.Port}.");
                return _startResult = new(BridgeApiHostStartDisposition.Started,
                    true, null, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (application is not null)
                    await application.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                if (application is not null)
                    await application.DisposeAsync().ConfigureAwait(false);
                Log($"Local API unavailable: {ex.Message}");
                return _startResult = new(BridgeApiHostStartDisposition.Failed,
                    false, "host-start-failed",
                    "The optional local API host could not be started.");
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WebApplication? application = _application;
            _application = null;
            IsRunning = false;
            if (application is null) return;

            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ShutdownTimeout);
            try
            {
                await application.StopAsync(timeout.Token).ConfigureAwait(false);
                Log("Local API stopped.");
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                Log("Local API graceful shutdown reached its time limit.");
            }
            catch (Exception ex)
            {
                Log($"Local API shutdown failure: {ex.Message}");
            }
            finally
            {
                await application.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _lifecycle.Dispose();
    }

    internal static WebApplication CreateApplication(
        IBridgeStateProvider state,
        string token,
        BridgeApiHostOptions options,
        Action<string>? log = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        IBridgeApiCommandFacade? commands = null,
        IBridgeApiV1HistoryProvider? history = null,
        IBridgeSessionProvider? sessions = null,
        IBridgeApiV1ControllerFacade? controller = null,
        IBridgeApiV1HistoryActivationFacade? historyActivation = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.WriteIndented = false;
        });
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            foreach (BridgeApiResolvedListener listener in options.GetListeners())
            {
                if (listener.Security == BridgeApiListenerSecurity.Http)
                {
                    kestrel.Listen(listener.Address, listener.Port);
                }
                else
                {
                    kestrel.Listen(listener.Address, listener.Port, endpoint =>
                        endpoint.UseHttps(listener.Certificate!));
                }
            }
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 16 * 1024;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Limits.MaxRequestHeaderCount = 64;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        });
        configureBuilder?.Invoke(builder);

        WebApplication application = builder.Build();
        ConfigurePipeline(application, state, token, log, commands, history,
            sessions, controller, historyActivation);
        return application;
    }

    private static void ConfigurePipeline(
        WebApplication application,
        IBridgeStateProvider state,
        string token,
        Action<string>? log,
        IBridgeApiCommandFacade? commands,
        IBridgeApiV1HistoryProvider? history,
        IBridgeSessionProvider? sessions,
        IBridgeApiV1ControllerFacade? controller,
        IBridgeApiV1HistoryActivationFacade? historyActivation)
    {
        application.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { log?.Invoke($"Local API request failed: {ex.Message}"); } catch { }
                if (context.Response.HasStarted) return;
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new BridgeApiV1HostFailure(
                    "internal-error", "The local API request could not be completed."))
                    .ConfigureAwait(false);
            }
        });

        application.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v1") &&
                !Authorized(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await context.Response.WriteAsJsonAsync(new BridgeApiV1HostFailure(
                    "unauthorized", "A valid bearer token is required."))
                    .ConfigureAwait(false);
                return;
            }
            await next(context).ConfigureAwait(false);
        });

        application.MapGet("/api/v1/state", () =>
            Results.Json(BridgeApiV1StateProjector.Project(state.GetSnapshot())));
        application.MapGet("/api/v1/operation", () => Results.Json(
            BridgeApiV1StateProjector.Project(state.GetSnapshot()).Operation));
        application.MapGet("/api/v1/health", () =>
            Results.Json(new BridgeApiV1Health(BridgeApiV1StateProjector.ApiVersion, "alive")));
        if (history is not null)
        {
            application.MapGet("/api/v1/history", () => Results.Json(
                BridgeApiV1HistoryProjector.Project(history.Current)));
        }
        if (sessions is not null)
        {
            application.MapGet("/api/v1/sessions", () => Results.Json(
                BridgeApiV1SessionProjector.ProjectList(sessions)));
            application.MapGet("/api/v1/sessions/{sessionId}", (string sessionId) =>
                BridgeApiV1SessionProjector.TryProjectCurrent(
                    sessions, sessionId, out BridgeApiV1Session? session)
                    ? Results.Json(session)
                    : Results.NotFound(new BridgeApiV1SessionFailure(
                        "session-not-found", "The requested Bridge session is not active.")));
        }
        if (commands is not null)
            BridgeApiV1CommandRoutes.Map(application, commands);
        if (controller is not null)
            BridgeApiV1ControllerRoutes.Map(application, controller);
        if (historyActivation is not null)
            BridgeApiV1HistoryActivationRoutes.Map(application, historyActivation);
    }

    private static bool Authorized(HttpContext context, string expectedToken)
    {
        string header = context.Request.Headers.Authorization.ToString();
        if (!AuthenticationHeaderValue.TryParse(header, out AuthenticationHeaderValue? value) ||
            !string.Equals(value.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(value.Parameter))
            return false;

        byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken));
        byte[] suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Parameter));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    private void Log(string message)
    {
        try { _log?.Invoke(message); } catch { }
    }
}
