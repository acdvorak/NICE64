namespace PicoSwitchController;

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

public sealed record BridgeApiV1HistoryItem(
    string ItemId,
    string FileName,
    DateTimeOffset LastUsedUtc,
    bool Available);

public sealed record BridgeApiV1History(
    string ApiVersion,
    int SchemaVersion,
    IReadOnlyList<BridgeApiV1HistoryItem> ControllerPak,
    IReadOnlyList<BridgeApiV1HistoryItem> TransferPak);

public interface IBridgeApiV1HistoryProvider
{
    BridgeHistorySnapshot Current { get; }
}

public sealed class BridgeApiV1HistoryProvider(IBridgeHistoryService history) :
    IBridgeApiV1HistoryProvider
{
    private readonly IBridgeHistoryService _history = history ??
        throw new ArgumentNullException(nameof(history));

    public BridgeHistorySnapshot Current => _history.Current;
}

public interface IBridgeApiV1HistoryFileAvailability
{
    bool Exists(string path);
}

public sealed class BridgeApiV1HistoryFileAvailability :
    IBridgeApiV1HistoryFileAvailability
{
    public bool Exists(string path) => File.Exists(path);
}

public static class BridgeApiV1HistoryProjector
{
    public const string ApiVersion = "v1";
    private const string ControllerPakCategory = "controller-pak";
    private const string TransferPakCategory = "transfer-pak";

    public static BridgeApiV1History Project(
        BridgeHistorySnapshot source,
        IBridgeApiV1HistoryFileAvailability? availability = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        availability ??= new BridgeApiV1HistoryFileAvailability();

        ReadOnlyCollection<BridgeApiV1HistoryItem> controllerPak =
            Array.AsReadOnly(source.ControllerPak.Select(entry => ProjectItem(
                ControllerPakCategory, entry.Path, entry.LastUsedUtc,
                availability)).ToArray());
        ReadOnlyCollection<BridgeApiV1HistoryItem> transferPak =
            Array.AsReadOnly(source.TransferPak.Select(entry => ProjectItem(
                TransferPakCategory, entry.RomPath, entry.LastUsedUtc,
                availability)).ToArray());

        return new(ApiVersion, source.SchemaVersion,
            controllerPak, transferPak);
    }

    public static string DeriveItemId(string category, string settledNormalizedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(settledNormalizedPath);
        string canonical = category + '\0' +
            settledNormalizedPath.ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static BridgeApiV1HistoryItem ProjectItem(
        string category,
        string path,
        DateTimeOffset lastUsedUtc,
        IBridgeApiV1HistoryFileAvailability availability) =>
        new(DeriveItemId(category, path), SafeFileName(path),
            lastUsedUtc.ToUniversalTime(), availability.Exists(path));

    private static string SafeFileName(string path)
    {
        string name = Path.GetFileName(path);
        string safe = new string(name.Where(character => !char.IsControl(character)).ToArray())
            .Trim();
        return safe.Length <= 256 ? safe : safe[..256];
    }
}
