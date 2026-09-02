namespace PicoSwitchController;

/// <summary>
/// Represents a module package root plus its manifest and content directory.
/// Modules should read content through this object instead of assuming app or repository layout.
/// </summary>
public sealed class ModulePackage
{
    /// <summary>
    /// Creates a package wrapper from a package root and loaded manifest.
    /// </summary>
    public ModulePackage(string packageRoot, ModuleManifest manifest)
    {
        PackageRoot = Path.GetFullPath(packageRoot);
        Manifest = manifest;
        ContentRoot = Path.GetFullPath(Path.Combine(PackageRoot, manifest.ContentRoot));
    }

    /// <summary>
    /// Absolute path to the package root folder.
    /// </summary>
    public string PackageRoot { get; }

    /// <summary>
    /// Manifest metadata loaded from module.json.
    /// </summary>
    public ModuleManifest Manifest { get; }

    /// <summary>
    /// Absolute path to the package content root.
    /// </summary>
    public string ContentRoot { get; }

    /// <summary>
    /// Loads module.json from a package root and returns a package wrapper.
    /// </summary>
    public static ModulePackage Load(string packageRoot)
    {
        string fullRoot = Path.GetFullPath(packageRoot);
        string manifestPath = Path.Combine(fullRoot, "module.json");
        return new ModulePackage(fullRoot, ModuleManifest.Load(manifestPath));
    }

    /// <summary>
    /// Resolves a package-relative content path and rejects paths that escape the package content root.
    /// </summary>
    public string GetContentPath(string relativePath)
    {
        string cleanRelativePath = relativePath.Trim();
        if (cleanRelativePath.StartsWith("content/", StringComparison.OrdinalIgnoreCase) ||
            cleanRelativePath.StartsWith("content\\", StringComparison.OrdinalIgnoreCase))
        {
            cleanRelativePath = cleanRelativePath["content/".Length..];
        }

        string fullPath = Path.GetFullPath(Path.Combine(ContentRoot, cleanRelativePath));
        string contentRoot = ContentRoot.EndsWith(Path.DirectorySeparatorChar)
            ? ContentRoot
            : ContentRoot + Path.DirectorySeparatorChar;
        if (!fullPath.Equals(ContentRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Module content path escapes content root.");
        }

        return fullPath;
    }

    /// <summary>
    /// Reads bytes from the package content root. Returns null when the path is invalid or missing.
    /// </summary>
    public byte[]? TryReadContentBytes(string relativePath)
    {
        string fullPath;
        try
        {
            fullPath = GetContentPath(relativePath);
        }
        catch
        {
            return null;
        }

        return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
    }

    /// <summary>
    /// Reads text from the package content root. Returns null when the path is invalid or missing.
    /// </summary>
    public string? TryReadContentText(string relativePath)
    {
        string fullPath;
        try
        {
            fullPath = GetContentPath(relativePath);
        }
        catch
        {
            return null;
        }

        return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
    }
}
