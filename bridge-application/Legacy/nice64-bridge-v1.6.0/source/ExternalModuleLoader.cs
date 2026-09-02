using System.Reflection;

namespace PicoSwitchController;

public static class ExternalModuleLoader
{
    public static IReadOnlyList<INetworkPakModule> LoadExternalModules(
        string modulesRoot,
        IReadOnlySet<string> reservedModuleIds,
        Action<string> log)
    {
        List<INetworkPakModule> modules = [];
        if (!Directory.Exists(modulesRoot))
        {
            return modules;
        }

        foreach (string packageRoot in Directory.EnumerateDirectories(modulesRoot))
        {
            string manifestPath = Path.Combine(packageRoot, "module.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            ModulePackage package;
            try
            {
                package = ModulePackage.Load(packageRoot);
            }
            catch (Exception ex)
            {
                log($"External module skipped: invalid manifest package={Path.GetFileName(packageRoot)} {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (reservedModuleIds.Contains(package.Manifest.Id))
            {
                log($"External module skipped: id reserved or duplicate id={package.Manifest.Id}");
                continue;
            }

            string? assemblyPath = ResolveAssemblyPath(package);
            if (assemblyPath is null)
            {
                continue;
            }

            if (package.Manifest.ProtocolVersion is not null &&
                package.Manifest.ProtocolVersion != BridgeConstants.ProtocolVersion)
            {
                log($"External module skipped: unsupported protocol id={package.Manifest.Id} protocol={package.Manifest.ProtocolVersion}");
                continue;
            }

            try
            {
                INetworkPakModule? module = LoadModule(package, assemblyPath);
                if (module is null)
                {
                    log($"External module skipped: no INetworkPakModule implementation id={package.Manifest.Id} assembly={assemblyPath}");
                    continue;
                }

                if (!string.Equals(module.Id, package.Manifest.Id, StringComparison.OrdinalIgnoreCase))
                {
                    log($"External module skipped: module id mismatch manifest={package.Manifest.Id} module={module.Id}");
                    continue;
                }

                modules.Add(module);
                log($"External module loaded: {module.DisplayName} ({module.Id})");
            }
            catch (Exception ex)
            {
                log($"External module skipped: load failed id={package.Manifest.Id} {ex.GetType().Name}: {ex.Message}");
            }
        }

        return modules;
    }

    private static string? ResolveAssemblyPath(ModulePackage package)
    {
        if (!string.IsNullOrWhiteSpace(package.Manifest.AssemblyPath))
        {
            string fullPath = Path.GetFullPath(Path.Combine(package.PackageRoot, package.Manifest.AssemblyPath));
            return File.Exists(fullPath) ? fullPath : fullPath;
        }

        string binRoot = Path.Combine(package.PackageRoot, "bin");
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : null;
    }

    private static INetworkPakModule? LoadModule(ModulePackage package, string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException("External module assembly was not found.", assemblyPath);
        }

        ExternalModuleLoadContext loadContext = new(assemblyPath);
        Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        Type? moduleType = ResolveModuleType(assembly, package.Manifest.ModuleType);
        if (moduleType is null)
        {
            return null;
        }

        ConstructorInfo? packageConstructor = moduleType.GetConstructor([typeof(ModulePackage)]);
        if (packageConstructor is not null)
        {
            return (INetworkPakModule)packageConstructor.Invoke([package]);
        }

        ConstructorInfo? emptyConstructor = moduleType.GetConstructor(Type.EmptyTypes);
        return emptyConstructor is null ? null : (INetworkPakModule)emptyConstructor.Invoke(null);
    }

    private static Type? ResolveModuleType(Assembly assembly, string? preferredTypeName)
    {
        if (!string.IsNullOrWhiteSpace(preferredTypeName))
        {
            Type? preferred = assembly.GetType(preferredTypeName, throwOnError: false, ignoreCase: false);
            if (preferred is not null && IsModuleType(preferred))
            {
                return preferred;
            }
            return null;
        }

        return assembly.GetTypes().FirstOrDefault(IsModuleType);
    }

    private static bool IsModuleType(Type type)
    {
        if (!typeof(INetworkPakModule).IsAssignableFrom(type) ||
            type is not { IsAbstract: false, IsInterface: false })
        {
            return false;
        }

        return type.GetConstructor([typeof(ModulePackage)]) is not null ||
            type.GetConstructor(Type.EmptyTypes) is not null;
    }
}
