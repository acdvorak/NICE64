namespace PicoSwitchController;

internal interface IBridgeApiHostFactory
{
    IBridgeApiHost Create(BridgeApiResolvedHostOptions options);
}

public interface IBridgeApiAccessLifecycle : IAsyncDisposable
{
    BridgeApiAccessRuntimeState Current { get; }
    Task<BridgeApiAccessRuntimeState> ApplyAsync(
        BridgeApiAccessResolution resolution,
        CancellationToken cancellationToken = default);
}

public sealed class BridgeApiAccessLifecycle : IBridgeApiAccessLifecycle
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IBridgeApiLanAddressValidator _validator;
    private readonly IBridgeApiHostFactory _hosts;
    private readonly IBridgeApiTlsIdentityProvider? _tlsIdentity;
    private readonly bool _allowDevelopmentPlaintextLan;
    private readonly Action<string>? _log;
    private IBridgeApiHost? _host;
    private bool _disposed;

    internal BridgeApiAccessLifecycle(
        IBridgeApiLanAddressValidator validator,
        IBridgeApiHostFactory hosts,
        IBridgeApiTlsIdentityProvider? tlsIdentity = null,
        bool allowDevelopmentPlaintextLan = false,
        Action<string>? log = null)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
        _tlsIdentity = tlsIdentity;
        _allowDevelopmentPlaintextLan = allowDevelopmentPlaintextLan;
        _log = log;
    }

    public BridgeApiAccessRuntimeState Current { get; private set; } =
        BridgeApiAccessRuntimeState.NotStarted(
            BridgeApiAccessConfiguration.Default);

    public async Task<BridgeApiAccessRuntimeState> ApplyAsync(
        BridgeApiAccessResolution resolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            BridgeApiAccessConfiguration desired = resolution.Desired;
            BridgeApiAccessConfiguration effective = resolution.Effective;

            if (effective.AccessMode == BridgeApiAccessMode.Off)
            {
                await RetireCurrentAsync(cancellationToken).ConfigureAwait(false);
                return Current = new(desired,
                    BridgeApiLoopbackRuntimeStatus.NotRequested,
                    BridgeApiLanRuntimeStatus.NotRequested,
                    BridgeApiLanValidationFailure.None, null, null);
            }

            if (effective.AccessMode == BridgeApiAccessMode.LocalOnly)
                return Current = await StartLocalOnlyAsync(desired, effective.Port,
                    BridgeApiLanRuntimeStatus.NotRequested,
                    BridgeApiLanValidationFailure.None,
                    cancellationToken).ConfigureAwait(false);

            BridgeApiLanValidationResult validation =
                _validator.Validate(effective);
            if (!validation.Success)
            {
                BridgeApiLanRuntimeStatus status = MapValidationFailure(
                    validation.Failure);
                return Current = await StartLocalOnlyAsync(desired, effective.Port,
                    status, validation.Failure, cancellationToken)
                    .ConfigureAwait(false);
            }

            BridgeApiResolvedHostOptions? dualOptions = null;
            bool secureLan = false;
            if (_tlsIdentity is not null)
            {
                BridgeApiTlsIdentityResult tls;
                try
                {
                    tls = _tlsIdentity.GetOrCreate();
                }
                catch
                {
                    tls = BridgeApiTlsIdentityResult.Unavailable;
                }
                if (tls.Success && tls.Identity is not null)
                {
                    dualOptions = BridgeApiResolvedHostOptions.LocalNetworkHttps(
                        effective.Port, validation.Selection!, tls.Identity);
                    secureLan = true;
                }
            }

            if (dualOptions is null && _allowDevelopmentPlaintextLan)
            {
                dualOptions = BridgeApiResolvedHostOptions.LocalNetwork(
                    effective.Port, validation.Selection!);
            }

            if (dualOptions is null)
            {
                return Current = await StartLocalOnlyAsync(desired, effective.Port,
                    BridgeApiLanRuntimeStatus.UnavailableCertificate,
                    BridgeApiLanValidationFailure.None,
                    cancellationToken).ConfigureAwait(false);
            }

            IBridgeApiHost dualHost = _hosts.Create(dualOptions);
            BridgeApiHostStartResult dualStart = await ReplaceAndStartAsync(
                dualHost, cancellationToken).ConfigureAwait(false);
            if (dualStart.Success && dualStart.Running)
            {
                return Current = new(desired,
                    BridgeApiLoopbackRuntimeStatus.Listening,
                    BridgeApiLanRuntimeStatus.Listening,
                    BridgeApiLanValidationFailure.None,
                    LoopbackEndpoint(effective.Port),
                    LanEndpoint(validation.Selection!.Ipv4Address,
                        effective.Port, secureLan));
            }

            Log("API dual-listener startup failed; recovering loopback-only.");
            return Current = await StartLocalOnlyAsync(desired, effective.Port,
                BridgeApiLanRuntimeStatus.BindFailed,
                BridgeApiLanValidationFailure.None,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BridgeApiAccessRuntimeState> StartLocalOnlyAsync(
        BridgeApiAccessConfiguration desired,
        int port,
        BridgeApiLanRuntimeStatus lanStatus,
        BridgeApiLanValidationFailure lanFailure,
        CancellationToken cancellationToken)
    {
        if (BridgeApiLanAddressValidator.ValidatePort(port) !=
            BridgeApiLanValidationFailure.None)
        {
            await RetireCurrentAsync(cancellationToken).ConfigureAwait(false);
            return new(desired, BridgeApiLoopbackRuntimeStatus.BindFailed,
                lanStatus == BridgeApiLanRuntimeStatus.NotRequested
                    ? BridgeApiLanRuntimeStatus.NotRequested : lanStatus,
                lanFailure, null, null);
        }

        IBridgeApiHost host = _hosts.Create(
            BridgeApiResolvedHostOptions.LocalOnly(port));
        BridgeApiHostStartResult started = await ReplaceAndStartAsync(
            host, cancellationToken).ConfigureAwait(false);
        if (!started.Success || !started.Running)
        {
            return new(desired, BridgeApiLoopbackRuntimeStatus.BindFailed,
                lanStatus, lanFailure, null, null);
        }

        return new(desired, BridgeApiLoopbackRuntimeStatus.Listening,
            lanStatus, lanFailure, LoopbackEndpoint(port), null);
    }

    private async Task<BridgeApiHostStartResult> ReplaceAndStartAsync(
        IBridgeApiHost replacement,
        CancellationToken cancellationToken)
    {
        await RetireCurrentAsync(cancellationToken).ConfigureAwait(false);
        BridgeApiHostStartResult result;
        try
        {
            result = await replacement.StartAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await replacement.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (result.Success && result.Running)
        {
            _host = replacement;
            return result;
        }
        await replacement.DisposeAsync().ConfigureAwait(false);
        return result;
    }

    private async Task RetireCurrentAsync(CancellationToken cancellationToken)
    {
        IBridgeApiHost? current = _host;
        _host = null;
        if (current is null) return;
        try
        {
            await current.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await current.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static BridgeApiLanRuntimeStatus MapValidationFailure(
        BridgeApiLanValidationFailure failure) => failure switch
        {
            BridgeApiLanValidationFailure.MissingInterfaceId or
            BridgeApiLanValidationFailure.InterfaceNotFound or
            BridgeApiLanValidationFailure.InterfaceIdentityAmbiguous or
            BridgeApiLanValidationFailure.InterfaceNotOperational or
            BridgeApiLanValidationFailure.LoopbackInterface or
            BridgeApiLanValidationFailure.InterfaceCatalogUnavailable =>
                BridgeApiLanRuntimeStatus.UnavailableSelectedInterface,
            BridgeApiLanValidationFailure.InvalidPort or
            BridgeApiLanValidationFailure.ReservedPort =>
                BridgeApiLanRuntimeStatus.BindFailed,
            _ => BridgeApiLanRuntimeStatus.UnavailableSelectedAddress
        };

    private static string LoopbackEndpoint(int port) =>
        $"http://127.0.0.1:{port}";

    private static string LanEndpoint(
        string address, int port, bool secure) =>
        $"{(secure ? "https" : "http")}://{address}:{port}";

    private void Log(string message)
    {
        try { _log?.Invoke(message); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await RetireCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            _tlsIdentity?.Dispose();
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
        _gate.Dispose();
    }
}
