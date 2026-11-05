using PicoSwitchController;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

DateTimeOffset now = DateTimeOffset.UtcNow;
string cpPath = @"D:\ResolverFixture\shared-name.mpk";
string tpPath = @"D:\ResolverFixture\shared-name.gb";
MutableHistory provider = new(new(1,
    [new(cpPath, now)], [new(tpPath, now)], string.Empty));
FakeAvailability availability = new([cpPath]);
BridgeApiV1HistoryItemResolver resolver = new(provider, availability);
string cpId = BridgeApiV1HistoryProjector.DeriveItemId(
    "controller-pak", cpPath);
string tpId = BridgeApiV1HistoryProjector.DeriveItemId(
    "transfer-pak", tpPath);

BridgeApiV1HistoryResolution cp = resolver.Resolve(cpId);
Check(cp.Status == BridgeApiV1HistoryResolutionStatus.Resolved &&
    cp.Item?.Category == BridgeHistoryCategory.ControllerPak &&
    cp.Item.Path == cpPath && cp.Item.Available,
    "valid Controller Pak opaque ID resolves current authority");
BridgeApiV1HistoryResolution tp = resolver.Resolve(tpId);
Check(tp.Status == BridgeApiV1HistoryResolutionStatus.Resolved &&
    tp.Item?.Category == BridgeHistoryCategory.TransferPak &&
    tp.Item.Path == tpPath && !tp.Item.Available,
    "retained unavailable Transfer Pak entry resolves as unavailable");
Check(cp.Item!.FileName == "shared-name.mpk" &&
    tp.Item!.FileName == "shared-name.gb",
    "filename is presentation metadata and not lookup identity");
Check(cpId != BridgeApiV1HistoryProjector.DeriveItemId(
        "transfer-pak", cpPath),
    "History category remains part of opaque identity");
Check(resolver.Resolve(new string('0', 64)).Status ==
    BridgeApiV1HistoryResolutionStatus.NotFound,
    "unknown well-formed opaque ID fails closed");
foreach (string malformed in new[] { "", "abc", new string('g', 64), cpId.ToUpperInvariant() })
    Check(resolver.Resolve(malformed).Status ==
        BridgeApiV1HistoryResolutionStatus.MalformedItemId,
        $"malformed or non-lowercase ID is rejected: length={malformed.Length}");
Check(resolver.Resolve(cpPath).Status ==
    BridgeApiV1HistoryResolutionStatus.MalformedItemId,
    "path input is not accepted as opaque identity");
Check(resolver.Resolve("shared-name.mpk").Status ==
    BridgeApiV1HistoryResolutionStatus.MalformedItemId,
    "filename lookup is not accepted");

provider.CurrentValue = new(1,
    [new(cpPath, now), new(cpPath, now.AddMinutes(-1))], [], string.Empty);
Check(resolver.Resolve(cpId).Status ==
    BridgeApiV1HistoryResolutionStatus.Ambiguous,
    "duplicate authority ambiguity fails closed");
provider.Throw = true;
Check(resolver.Resolve(cpId).Status ==
    BridgeApiV1HistoryResolutionStatus.UnsafeAuthority,
    "unreadable History authority fails closed");

Check(typeof(BridgeApiV1HistoryActivationResult).GetProperties()
        .All(property => !property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)),
    "API-safe activation result exposes no resolved path");
string source = File.ReadAllText(Path.Combine(
    FindSourceDirectory(), "BridgeApiV1HistoryItemResolver.cs"));
Check(!source.Contains("File.Write", StringComparison.Ordinal) &&
    !source.Contains("Dictionary<", StringComparison.Ordinal) &&
    !source.Contains("ConcurrentDictionary", StringComparison.Ordinal),
    "resolver persists no item ID index or cache");

Console.WriteLine($"BridgeApiV1HistoryResolverHeadlessSmoke: {passed} checks passed.");

static string FindSourceDirectory()
{
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
        directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName,
                "BridgeApiV1HistoryItemResolver.cs"))) return directory.FullName;
    }
    throw new DirectoryNotFoundException();
}

sealed class MutableHistory(BridgeHistorySnapshot current) :
    IBridgeApiV1HistoryProvider
{
    public BridgeHistorySnapshot CurrentValue { get; set; } = current;
    public bool Throw { get; set; }
    public BridgeHistorySnapshot Current => Throw
        ? throw new InvalidOperationException("private authority failure")
        : CurrentValue;
}

sealed class FakeAvailability(IEnumerable<string> available) :
    IBridgeApiV1HistoryFileAvailability
{
    private readonly HashSet<string> _available = new(
        available, StringComparer.OrdinalIgnoreCase);
    public bool Exists(string path) => _available.Contains(path);
}

namespace PicoSwitchController
{
    public enum BridgeApiV1HistoryActivationDisposition
    {
        Accepted
    }

    public sealed record BridgeApiV1HistoryActivationResult(
        BridgeApiV1HistoryActivationDisposition Disposition,
        bool Accepted,
        long? OperationId,
        string? OperationKind,
        long? CurrentOperationId,
        string? CurrentOperationKind,
        string? Code,
        string? SafeDetail,
        object? Confirmation);
}
