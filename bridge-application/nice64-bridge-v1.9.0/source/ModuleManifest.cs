using System.Text.Json;

namespace PicoSwitchController;

/// <summary>
/// Metadata loaded from a module package's module.json file.
/// The manifest is the stable package contract for built-in modules now and future DLL modules later.
/// </summary>
public sealed record ModuleManifest(
    string Id,
    string DisplayName,
    string Version,
    string Author,
    string Description,
    string ResourcePrefix,
    string DefaultResource,
    string ContentRoot,
    string? AssemblyPath = null,
    string? ModuleType = null,
    string? MinimumBridgeSdkVersion = null,
    int? ProtocolVersion = null,
    int? ModuleApiVersion = null,
    string[]? ResourcePrefixes = null)
{
    public IReadOnlyList<string> EffectiveResourcePrefixes =>
        ResourcePrefixes is { Length: > 0 } ? ResourcePrefixes : [ResourcePrefix];

    /// <summary>
    /// Loads and validates a module manifest from disk.
    /// </summary>
    public static ModuleManifest Load(string path)
    {
        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        ModuleManifest? manifest = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(path), options);
        if (manifest is null ||
            string.IsNullOrWhiteSpace(manifest.Id) ||
            string.IsNullOrWhiteSpace(manifest.DisplayName) ||
            string.IsNullOrWhiteSpace(manifest.ResourcePrefix) ||
            string.IsNullOrWhiteSpace(manifest.ContentRoot))
        {
            throw new InvalidDataException($"Invalid module manifest: {path}");
        }

        return manifest with
        {
            Id = manifest.Id.Trim(),
            DisplayName = manifest.DisplayName.Trim(),
            Version = manifest.Version.Trim(),
            Author = manifest.Author.Trim(),
            Description = manifest.Description.Trim(),
            ResourcePrefix = manifest.ResourcePrefix.Trim(),
            DefaultResource = manifest.DefaultResource.Trim(),
            ContentRoot = manifest.ContentRoot.Trim(),
            AssemblyPath = string.IsNullOrWhiteSpace(manifest.AssemblyPath) ? null : manifest.AssemblyPath.Trim(),
            ModuleType = string.IsNullOrWhiteSpace(manifest.ModuleType) ? null : manifest.ModuleType.Trim(),
            MinimumBridgeSdkVersion = string.IsNullOrWhiteSpace(manifest.MinimumBridgeSdkVersion) ? null : manifest.MinimumBridgeSdkVersion.Trim(),
            ProtocolVersion = manifest.ProtocolVersion,
            ModuleApiVersion = manifest.ModuleApiVersion,
            ResourcePrefixes = manifest.ResourcePrefixes?
                .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                .Select(prefix => prefix.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }
}
