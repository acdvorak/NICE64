# Build Manifest

## Current release candidate

- Universal Firmware: v1.1.0
- Bridge: v1.6.0
- Status: build/static pass; normal Product Quick Mode real-N64 hardware pass
- Gold: not declared

Authoritative packaged artifacts:

- `firmware/unified-firmware-v1.1.0/NICE64-Universal-Firmware-v1.1.0.uf2`
- `bridge-application/nice64-bridge-v1.6.0/application/PicoSwitchController.exe`
- `bridge-application/nice64-bridge-v1.6.0/application/PicoSwitchController.dll`

Historical firmware releases are preserved beneath `firmware/Legacy`, alongside
the existing Bridge and ROM `Legacy` trees.

## Toolchains and configuration

- Bridge: .NET 9 Windows, Release
- Firmware: Pico SDK, CMake, Ninja, Arm GNU embedded toolchain
- Pico target: `pico2_w` / RP2350 Arm secure
- Firmware options: `NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK=1`,
  `NICE_RX_BACKEND_PIO=1`, `PICO_COPY_TO_RAM=OFF`
- RUDP guard: `NICE64_RUDPC_HARDWARE_CANDIDATE=1`

Set `PICO_SDK_PATH` before invoking the firmware helper. Optional tool overrides
use normal environment/PATH discovery; no machine-specific paths are required.

## Validation disposition

Bridge Release builds with zero warnings and zero errors. Host simulations,
transport contracts, Network Pak adapter parity, Wi-Fi provisioning, bulk
transfer, Layer 2, reconnect/session, and Browser fixtures pass. Protected
Joybus/Core0/PIO/accessory sources remain unchanged at the integration boundary.
RUDP-C physical synthetic closeout is accepted and frozen.

The packaged Bridge v1.6.0 and Legacy v1.4.0 first-party outputs were rebuilt
from isolated clean workspaces using .NET 9 Release, deterministic/continuous-
integration settings, path mapping, and no published debug symbols. Both clean
builds completed with zero warnings and zero errors; applicable host tests and
packaged startup smokes passed. Binary scans found no personal workspace paths
or development-tool provenance in either rebuilt EXE/DLL pair.

A clean repository-path firmware build on 2026-08-18 succeeded and passed the
371,224 / 4,186,112-byte persistence boundary. Its UF2 hash is
`99BCD833678C93A6AF6F4290DC4E5BE09746B571904115199AD0143154DBDE2C`.
The rebuilt binary has the same length and ELF section sizes as the packaged
candidate and differs at exactly one byte: Pico SDK binary-info embeds the
build date (`Aug 16 2026` in the packaged candidate versus `Aug 18 2026` in the
rebuild). The public release UF2 preserves the validated candidate bytes with SHA-256
`1B2B57CA8E79F40612F350F08F779714F34D04DB19C7EB6BEB165DC3C6836CB8`;
it was not replaced by the date-stamped verification rebuild.

Normal Product Quick Mode hardware validation completed with exact protected
Network Pak transfer CRC and zero reported errors/timeouts. Repeated reconnect,
session renewal, and TCP↔Quick handoff remain less exhaustively validated; an
intermittent development-time establishment timeout was not confirmed as a
persistent defect. This lifecycle note does not change RUDP-C's physically
accepted/frozen status and does not by itself alter the release/Gold status.
Critical artifact hashes are maintained in `CHECKSUMS_SHA256.txt`.
