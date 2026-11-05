using PicoSwitchController;
using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

DateTimeOffset newest = new(2026, 9, 3, 1, 25, 0, TimeSpan.Zero);
DateTimeOffset older = newest.AddMinutes(-5);
string controllerNewest = @"D:\PrivateHistoryFixture\Controller\stadium.mpk";
string controllerOlder = @"D:\PrivateHistoryFixture\Controller\older.mpk";
string transferNewest = @"D:\PrivateHistoryFixture\Transfer\Pokemon Gold.gb";
string transferMissing = @"D:\PrivateHistoryFixture\Transfer\missing.gbc";

BridgeControllerPakHistoryEntry[] controllerAuthority =
[
    new(controllerNewest, newest),
    new(controllerOlder, older)
];
BridgeTransferPakHistoryEntry[] transferAuthority =
[
    new(transferNewest, newest),
    new(transferMissing, older)
];
BridgeHistorySnapshot source = new(1, controllerAuthority, transferAuthority,
    "private internal diagnostic");
FakeAvailability availability = new([controllerNewest, transferNewest]);

BridgeApiV1History projected = BridgeApiV1HistoryProjector.Project(source, availability);
Require(projected.ApiVersion == "v1" && projected.SchemaVersion == 1,
    "History API versions were not projected truthfully");
Require(projected.ControllerPak.Count == 2 && projected.TransferPak.Count == 2,
    "History categories were not projected independently");
Require(projected.ControllerPak[0].FileName == "stadium.mpk" &&
    projected.ControllerPak[1].FileName == "older.mpk" &&
    projected.TransferPak[0].FileName == "Pokemon Gold.gb",
    "Filename-only values or source ordering were not preserved");
Require(projected.ControllerPak[0].LastUsedUtc == newest &&
    projected.ControllerPak[1].LastUsedUtc == older,
    "Most-recent-first ordering changed during projection");
Require(projected.ControllerPak[0].Available &&
    projected.TransferPak[0].Available &&
    !projected.TransferPak[1].Available &&
    projected.TransferPak[1].FileName == "missing.gbc",
    "Availability or missing-entry retention was incorrect");

string sameCaseId = BridgeApiV1HistoryProjector.DeriveItemId(
    "controller-pak", controllerNewest);
string changedCaseId = BridgeApiV1HistoryProjector.DeriveItemId(
    "controller-pak", controllerNewest.ToUpperInvariant());
string differentPathId = BridgeApiV1HistoryProjector.DeriveItemId(
    "controller-pak", controllerOlder);
string differentCategoryId = BridgeApiV1HistoryProjector.DeriveItemId(
    "transfer-pak", controllerNewest);
Require(sameCaseId == changedCaseId,
    "Case-insensitive Windows path identity was not preserved");
Require(sameCaseId != differentPathId && sameCaseId != differentCategoryId,
    "Path or category was absent from opaque item identity");
Require(Regex.IsMatch(sameCaseId, "^[0-9a-f]{64}$"),
    "Item ID is not a full stable lowercase SHA-256 digest");
Require(sameCaseId ==
    "6f559501b7d8718ea45fa62ac9ccaf4062d1d91fffb8b508d37b40f1532c283b",
    "Canonical item ID test vector changed");
Require(projected.ControllerPak[0].ItemId == sameCaseId,
    "Projected item ID did not use the frozen derivation");

BridgeHistorySnapshot sameTextAcrossCategories = new(1,
    [new(@"D:\PrivateHistoryFixture\same.value", newest)],
    [new(@"D:\PrivateHistoryFixture\same.value", newest)], string.Empty);
BridgeApiV1History crossCategory = BridgeApiV1HistoryProjector.Project(
    sameTextAcrossCategories, availability);
Require(crossCategory.ControllerPak[0].ItemId !=
    crossCategory.TransferPak[0].ItemId,
    "Identical textual paths collided across categories");

string json = JsonSerializer.Serialize(projected,
    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
string[] forbidden =
[
    @"D:\", @"D:\PrivateHistoryFixture", "PrivateHistoryFixture",
    "Controller\\stadium.mpk", "Transfer\\Pokemon Gold.gb",
    "history.json", "LOCALAPPDATA", "private internal diagnostic"
];
foreach (string value in forbidden)
    Require(!json.Contains(value, StringComparison.OrdinalIgnoreCase),
        $"History projection leaked path/internal value: {value}");

Require(controllerAuthority[0].Path == controllerNewest &&
    controllerAuthority[1].Path == controllerOlder &&
    transferAuthority[0].RomPath == transferNewest &&
    ReferenceEquals(source.ControllerPak, controllerAuthority) &&
    ReferenceEquals(source.TransferPak, transferAuthority),
    "Projection mutated or replaced authoritative History collections");
Require(projected.ControllerPak is IList controllerList && controllerList.IsReadOnly &&
    projected.TransferPak is IList transferList && transferList.IsReadOnly,
    "Projected collections are not read-only wrappers");

BridgeApiV1History empty = BridgeApiV1HistoryProjector.Project(
    new(1, Array.Empty<BridgeControllerPakHistoryEntry>(),
        Array.Empty<BridgeTransferPakHistoryEntry>(), string.Empty), availability);
Require(empty.ApiVersion == "v1" && empty.SchemaVersion == 1 &&
    empty.ControllerPak.Count == 0 && empty.TransferPak.Count == 0,
    "Empty History did not project a valid v1 response");

Console.WriteLine("PASS History API explicit projection, opaque IDs, privacy, availability, and immutability");

sealed class FakeAvailability(IEnumerable<string> existing) :
    IBridgeApiV1HistoryFileAvailability
{
    private readonly HashSet<string> _existing = new(
        existing, StringComparer.OrdinalIgnoreCase);

    public bool Exists(string path) => _existing.Contains(path);
}
