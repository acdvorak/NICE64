# NICE64 Build Manifest

This manifest records the Phase F/G release-candidate artifacts packaged in the public NICE64 repository.

## Current release

- NICE64 unified firmware: **v1.2.0**
- PicoSwitchController Bridge: **v1.7.0**
- Release status: **Release Candidate**
- Final comprehensive source-code review: **Pending**
- Checkpoint F: **COMPLETE / ACCEPTED / FROZEN**
- Phase G: **COMPLETE / ACCEPTED / FROZEN**

## Firmware artifact

Path:

`firmware/unified-firmware-v1.2.0/NICE64-Universal-Firmware-v1.2.0.uf2`

SHA-256:

`81A7266CF008ECC3EC665FC4D916EFBAF4BF8DE742930508E8905DE057FF7732`

Build target and configuration:

- Raspberry Pi Pico 2 W / RP2350
- Pico SDK CMake build
- `NICE64_ENABLE_RUDP_C=1`
- firmware version string verified in the built ELF as `1.2.0`
- firmware/Transfer Pak partition boundary guard passed
- firmware image end: 378,872 bytes
- protected boundary: 1,048,576 bytes (`0x100000`)

The prior v1.1.0 binary and source release are retained under `firmware/Legacy/`.

## Bridge artifacts

Application directory:

`bridge-application/nice64-bridge-v1.7.0/application/`

Executable:

`bridge-application/nice64-bridge-v1.7.0/application/PicoSwitchController.exe`

SHA-256:

`528FDFF7B5C2B124FA3C2AAD83B29C6B61507428A83E25A3CA946557573CB85E`

Primary assembly:

`bridge-application/nice64-bridge-v1.7.0/application/PicoSwitchController.dll`

SHA-256:

`A5F070D1E8E54CFCF3F529C8930050855FB4ECB5A4499C4E9242BF6DAABF403E`

Build configuration:

- .NET 9 Windows Desktop
- Release configuration
- application and assembly version: v1.7.0
- deterministic non-hardware regression baseline: 31/31 PASS
- Release build baseline: 0 warnings / 0 errors

The historical Bridge releases under `bridge-application/Legacy/` remain preserved and unchanged. The prior nonlegacy v1.6.0 package is not part of the current public release surface.

## Release hygiene

The promoted packages exclude:

- build-intermediate `bin/` and `obj/` trees from packaged source;
- PDB and temporary files;
- runtime logs;
- machine-local INI state;
- analog-calibration state;
- generated firmware build trees.

The Bridge release assembly was produced with deterministic, path-mapped build
metadata so its public CodeView reference uses a neutral virtual source root.

Firmware, ROM, Joybus, Network Pak protocol, and physical RUDP tuning are unchanged except for the separately documented firmware v1.2.0 safety and retry-idempotency corrections. Frozen RUDP parameters remain UDP port 6465, window 8, physical flight cap 2, payload 512 bytes, RTO 40 ms, and retries 4.

## Documentation

- [Bridge user guide](BRIDGE_USER_GUIDE.md)
- [Bridge API v1 guide](BRIDGE_API.md)
- [Bridge v1.7.0 changes](BRIDGE_v1.7.0_CHANGES.md)
- [Firmware v1.2.0 changes](FIRMWARE_v1.2.0_CHANGES.md)

The authoritative artifact hashes are also listed in the repository-root `CHECKSUMS_SHA256.txt`.
