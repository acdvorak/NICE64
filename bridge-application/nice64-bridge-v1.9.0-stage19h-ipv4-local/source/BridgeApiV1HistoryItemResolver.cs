namespace PicoSwitchController;

internal enum BridgeApiV1HistoryResolutionStatus
{
    Resolved,
    MalformedItemId,
    NotFound,
    Ambiguous,
    UnsafeAuthority
}

internal sealed record BridgeApiV1ResolvedHistoryItem(
    BridgeHistoryCategory Category,
    string Path,
    string FileName,
    bool Available);

internal sealed record BridgeApiV1HistoryResolution(
    BridgeApiV1HistoryResolutionStatus Status,
    BridgeApiV1ResolvedHistoryItem? Item);

internal interface IBridgeApiV1HistoryItemResolver
{
    BridgeApiV1HistoryResolution Resolve(string? itemId);
}

internal sealed class BridgeApiV1HistoryItemResolver(
    IBridgeApiV1HistoryProvider history,
    IBridgeApiV1HistoryFileAvailability? availability = null) :
    IBridgeApiV1HistoryItemResolver
{
    private const string ControllerPakCategory = "controller-pak";
    private const string TransferPakCategory = "transfer-pak";

    private readonly IBridgeApiV1HistoryProvider _history = history ??
        throw new ArgumentNullException(nameof(history));
    private readonly IBridgeApiV1HistoryFileAvailability _availability =
        availability ?? new BridgeApiV1HistoryFileAvailability();

    public BridgeApiV1HistoryResolution Resolve(string? itemId)
    {
        if (!IsFrozenItemId(itemId))
            return Failed(BridgeApiV1HistoryResolutionStatus.MalformedItemId);

        BridgeHistorySnapshot snapshot;
        try { snapshot = _history.Current; }
        catch { return Failed(BridgeApiV1HistoryResolutionStatus.UnsafeAuthority); }

        List<BridgeApiV1ResolvedHistoryItem> matches = [];
        try
        {
            foreach (BridgeControllerPakHistoryEntry entry in snapshot.ControllerPak)
            {
                AddIfMatch(matches, itemId!, ControllerPakCategory,
                    BridgeHistoryCategory.ControllerPak, entry.Path);
            }
            foreach (BridgeTransferPakHistoryEntry entry in snapshot.TransferPak)
            {
                AddIfMatch(matches, itemId!, TransferPakCategory,
                    BridgeHistoryCategory.TransferPak, entry.RomPath);
            }
        }
        catch
        {
            return Failed(BridgeApiV1HistoryResolutionStatus.UnsafeAuthority);
        }

        return matches.Count switch
        {
            0 => Failed(BridgeApiV1HistoryResolutionStatus.NotFound),
            1 => new(BridgeApiV1HistoryResolutionStatus.Resolved, matches[0]),
            _ => Failed(BridgeApiV1HistoryResolutionStatus.Ambiguous)
        };
    }

    private void AddIfMatch(
        List<BridgeApiV1ResolvedHistoryItem> matches,
        string requestedId,
        string categoryToken,
        BridgeHistoryCategory category,
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("History authority contains an invalid entry.");
        string derived = BridgeApiV1HistoryProjector.DeriveItemId(
            categoryToken, path);
        if (!string.Equals(derived, requestedId, StringComparison.Ordinal)) return;
        bool available = _availability.Exists(path);
        matches.Add(new(category, path, SafeFileName(path), available));
    }

    private static bool IsFrozenItemId(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string SafeFileName(string path)
    {
        string value = System.IO.Path.GetFileName(path);
        string safe = new(value.Where(character => !char.IsControl(character))
            .ToArray());
        safe = safe.Trim();
        return safe.Length <= 256 ? safe : safe[..256];
    }

    private static BridgeApiV1HistoryResolution Failed(
        BridgeApiV1HistoryResolutionStatus status) => new(status, null);
}
