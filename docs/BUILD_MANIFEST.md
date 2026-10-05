# NICE64 Build Manifest

This manifest records the finalized release artifacts packaged in the public NICE64 repository.

## Current release

- NICE64 unified firmware: **v1.2.0**
- PicoSwitchController Bridge: **v1.8.0**
- NICE Browser: **v1.4.1**
- Android companion: **accepted frozen build (`0.1.0-dev` project identity)**
- Release status: **Final release export / accepted**
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

`bridge-application/nice64-bridge-v1.8.0/application/`

Executable:

`bridge-application/nice64-bridge-v1.8.0/application/PicoSwitchController.exe`

SHA-256:

`7B1A328662B759D95BBA9BC243925B828FBC5817E8B6E283DBF5BAE7C0C0945D`

Primary assembly:

`bridge-application/nice64-bridge-v1.8.0/application/PicoSwitchController.dll`

SHA-256:

`0ECA098485ED42ABA22F435D0780498E9759FB544B1B359F7092C7EF8482117D`

Build configuration:

- .NET 9 Windows Desktop
- Release configuration
- application and assembly version: v1.8.0
- deterministic non-hardware regression baseline: 60/60 PASS
- Release build baseline: 0 warnings / 0 errors

The historical Bridge releases under `bridge-application/Legacy/` remain preserved and unchanged. The prior nonlegacy v1.6.0 package is not part of the current public release surface.

## Android artifact

Path:

`android-application/NICE64-Android-Companion.apk`

SHA-256:

`28D9E16DBD94E4FC07B32033DE84C621DE50E2A60C7F8D0D2BD87A5E0BF897CC`

Build identity and validation:

- project version identity: `0.1.0-dev` (no new semantic Android version was invented);
- minimum API 26; target API 36;
- JVM 327/327 PASS;
- connected instrumentation 81/81 PASS;
- lint, build, and physical freeze PASS.

## Browser artifact

Path:

`rom/NICE-Browser-v1.4.1/nice-browser-rom-v1.4.1-Release.z64`

SHA-256:

`66D3D406DA5D116546EDA7A738AD3BEBE81C415DB5DA67C9472BD761118E2311`

Release identity and validation:

- current Browser version: v1.4.1;
- promoted from the confirmed working Summercart v1.4.0 source lineage;
- promotion changed release identity only, not Browser behavior or protocol semantics;
- exported source: 47/47 files verified;
- staged-bulk and carrier-response-gate smoke tests: 2/2 PASS.

The historical v1.4.0 Browser package remains preserved under `rom/Legacy/`.

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

Firmware, Joybus, Network Pak protocol, and physical RUDP tuning are unchanged except for the separately documented firmware v1.2.0 safety and retry-idempotency corrections. NICE Browser v1.4.1 is a release-identity-only promotion of its confirmed working source lineage. Frozen RUDP parameters remain UDP port 6465, window 8, physical flight cap 2, payload 512 bytes, RTO 40 ms, and retries 4.

## Documentation

- [Bridge user guide](BRIDGE_USER_GUIDE.md)
- [Bridge API v1 guide](BRIDGE_API.md)
- [Bridge v1.7.0 changes](BRIDGE_v1.7.0_CHANGES.md)
- [Bridge v1.8.0 changes](BRIDGE_v1.8.0_CHANGES.md)
- [Android companion](ANDROID_COMPANION.md)
- [NICE Browser v1.4.1](../rom/NICE-Browser-v1.4.1/README.md)
- [Firmware v1.2.0 changes](FIRMWARE_v1.2.0_CHANGES.md)

The authoritative artifact hashes are also listed in the repository-root `CHECKSUMS_SHA256.txt`.
