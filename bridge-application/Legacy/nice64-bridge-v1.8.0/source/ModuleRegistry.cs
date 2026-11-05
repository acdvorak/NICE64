namespace PicoSwitchController;

public sealed class ModuleRegistry
{
    public const string DefaultModuleId = "network-portal";

    private readonly List<INetworkPakModule> _modules;

    public ModuleRegistry(
        string? activeModuleId = null,
        Action<string>? log = null,
        string? readableSourceMode = null,
        string? searchProviderName = null,
        string? searchProviderUrl = null)
    {
        log ??= _ => { };
        string modulesRoot = Path.Combine(AppContext.BaseDirectory, "modules");
        ModulePackage networkPortalPackage = ModulePackage.Load(Path.Combine(modulesRoot, "network-portal"));
        ModulePackage diagnosticsPackage = ModulePackage.Load(Path.Combine(modulesRoot, "diagnostics"));
        ModulePackage speedTestPackage = ModulePackage.Load(Path.Combine(modulesRoot, "speed-test"));
        ModulePackage browserTestPackage = ModulePackage.Load(Path.Combine(modulesRoot, "browser-test"));
        ModulePackage browserLivePackage = ModulePackage.Load(Path.Combine(modulesRoot, "browser-live"));
        BrowserSessionStore browserSessionStore = new(Path.Combine(browserLivePackage.PackageRoot, "browser-data"));
        BrowserImageStore browserImageStore = new(Path.Combine(browserLivePackage.PackageRoot, "browser-data", "pico-local", "session-default"));
        log("Browser route registry loaded");
        log($"Routes file: {browserSessionStore.RoutesPath}");
        log($"Calls file: {browserSessionStore.CallsPath}");
        log($"Routes active: {browserSessionStore.ActiveRouteCount}");
        log("Document max bytes: 16384");
        log("ROM wait expected: 20000 ms");
        log("Browser Test module loaded");
        log("Route registered: browser://test");
        log("Browser Live module loaded");
        log("Route registered: browser://live");
        log("Route registered: browser://g/");
        log("Route registered: browser://u/");
        log("Route registered: browser://search");
        log("Route registered: browser://i/");
        _modules =
        [
            new NetworkPortalModule(networkPortalPackage),
            new DiagnosticsModule(diagnosticsPackage),
            new SpeedTestModule(speedTestPackage),
            new BrowserTestModule(browserTestPackage),
            new BrowserLiveModule(browserLivePackage, browserSessionStore, browserImageStore,
                readableSourceMode, log, searchProviderName, searchProviderUrl),
            new SerialTunnelModule()
        ];
        HashSet<string> registeredIds = new(_modules.Select(module => module.Id), StringComparer.OrdinalIgnoreCase);
        foreach (INetworkPakModule module in ExternalModuleLoader.LoadExternalModules(modulesRoot, registeredIds, log))
        {
            if (registeredIds.Contains(module.Id))
            {
                log($"External module skipped: duplicate id={module.Id}");
                continue;
            }

            _modules.Add(module);
            registeredIds.Add(module.Id);
        }

        ActiveModule = _modules[0];
        SetActiveModule(activeModuleId);
    }

    public INetworkPakModule ActiveModule { get; private set; }

    public IReadOnlyList<INetworkPakModule> ListModules() => _modules;

    public INetworkPakModule? FindModuleForResource(string resource)
    {
        foreach (INetworkPakModule module in _modules)
        {
            if (ReferenceEquals(module, ActiveModule))
            {
                continue;
            }

            if (module is null)
            {
                continue;
            }

            if (ModuleMatchesResource(module, resource))
            {
                return module;
            }
        }

        return _modules.FirstOrDefault(module => !ReferenceEquals(module, ActiveModule));
    }

    private static bool ModuleMatchesResource(INetworkPakModule module, string resource)
    {
        return module switch
        {
            NetworkPortalModule => resource.StartsWith("bridge://", StringComparison.OrdinalIgnoreCase),
            DiagnosticsModule => resource.StartsWith("diag://", StringComparison.OrdinalIgnoreCase),
            SpeedTestModule => resource.StartsWith("speed://", StringComparison.OrdinalIgnoreCase),
            BrowserTestModule => BrowserTestModule.IsTestResource(resource),
            BrowserLiveModule => BrowserLiveModule.IsLiveResource(resource),
            SerialTunnelModule => SerialTunnelModule.IsTunnelResource(resource),
            _ => false
        };
    }

    public bool SetActiveModule(string? id)
    {
        INetworkPakModule? module = _modules.FirstOrDefault(
            item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (module is null)
        {
            ActiveModule = _modules.First(item => string.Equals(item.Id, DefaultModuleId, StringComparison.Ordinal));
            return false;
        }

        ActiveModule = module;
        return true;
    }
}
