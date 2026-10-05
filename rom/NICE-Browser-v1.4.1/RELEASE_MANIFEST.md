# NICE Browser v1.4.1 Release Manifest

## Provenance

- Confirmed working base ROM: `D:\NICE64\nice-browser-rom-v1.4.0-Release.z64`
- Summercart v1.4.0 SHA-256: `8BA44E20AC9399CF47ECEA6C9E297AE786959452829F6F8C216D6B5C6719A9A8`
- Summercart v1.4.0 size: 458,752 bytes
- Authoritative source root: `D:\NICE64\checkpoint-e\carrier-response-gate-build\NICE-Browser-v1.4.0\source`
- Provenance status: **CONFIRMED**
- Exact v1.4.0 rebuild reproduction: **YES**; the clean rebuild produced the same SHA-256 as the Summercart ROM.

The authoritative lineage contains the staged logical stream reader and the Network Pak carrier response gate. The co-located original ROM and the hardware-pending candidate copy both match the Summercart provenance hash exactly.

## v1.4.1 release

- Release change: Browser release identity only (`1.4.0` to `1.4.1`)
- Functional Browser changes: none
- ROM filename: `nice-browser-rom-v1.4.1-Release.z64`
- ROM size: 458,752 bytes
- ROM SHA-256: `66D3D406DA5D116546EDA7A738AD3BEBE81C415DB5DA67C9472BD761118E2311`
- Source file count: 47
- Toolchain: archived libdragon source with N64 GCC 16.2.0

Release build sequence from the packaged `source` directory, with `N64_INST`
pointing to the installed archived libdragon revision:

```sh
N64_INST=/n64_toolchain make clean
N64_INST=/n64_toolchain make -j2
```

Build result: **PASS**. Browser project compilation produced no warnings or errors. The archived libdragon dependency build emitted only its existing deprecation warnings.

## Tests

- `CheckpointEBrowserBulkSmoke`: PASS
- `CheckpointECarrierResponseGateSmoke`: PASS

Both tests compiled the actual Browser lineage sources under `-std=c11 -Wall -Wextra -Werror`.
