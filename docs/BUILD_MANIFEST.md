# NICE64 Build Manifest

This manifest records the locally packaged NICE64 release-candidate artifacts.
No Git publication has been performed.

## Current release

- NICE64 unified firmware: **v1.3.0 release candidate — hardware validated**
- PicoSwitchController Bridge: **v1.9.0**
- NICE Browser: **v1.4.1**
- Android companion: **v1.2.0 (`versionCode` 2), debug-signed**
- Release status: **Local release candidates prepared; firmware physical validation passed**
- Checkpoint F: **COMPLETE / ACCEPTED / FROZEN**
- Phase G: **COMPLETE / ACCEPTED / FROZEN**

## Firmware artifact

Path:

`firmware/unified-firmware-v1.3.0/NICE64-Universal-Firmware-v1.3.0.uf2`

SHA-256:

`B7807310DA88D16F251A5349F9D67E04A082A1CCE651527ACD17484062CDC785`

Build target and configuration:

- Raspberry Pi Pico 2 W / RP2350
- Pico SDK CMake build
- `NICE64_RUDPC_HARDWARE_CANDIDATE=1`
- `NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK=1`
- `NICE_RX_BACKEND_PIO=1`
- typed direct mobile Controller/Keyboard/Mouse input on Core 1
- firmware version string verified in the built ELF as `1.3.0`
- firmware/Transfer Pak partition boundary guard passed
- firmware image end: 387,184 bytes
- protected boundary: 1,048,576 bytes (`0x100000`)

The v1.3.0 source matches the accepted G2 implementation across functional
files; only approved version declarations and documentation differ. The
compiled UF2 has passed extensive real-hardware use, including RandNET Keyboard
with a Windows CE port/side project and N64 Mouse with Mario Studio on 64DD. The
preserved v1.2.0 G2 UF2 remains the prior hardware-validated reference with
SHA-256
`95C6F83D84FDC32C4E1ADCA638A37B1BB06F56E897B43658DF09D5153FEE2359`.
The prior v1.1.0 binary and source release remain under `firmware/Legacy/`.

## Bridge artifacts

Application directory:

`bridge-application/nice64-bridge-v1.9.0/application/`

Executable:

`bridge-application/nice64-bridge-v1.9.0/application/PicoSwitchController.exe`

SHA-256:

`3CE9AF09E8D8BAF58858FCDDE1378FACC1706C06BDBA8718AA9E346577CD0AA4`

Primary assembly:

`bridge-application/nice64-bridge-v1.9.0/application/PicoSwitchController.dll`

SHA-256:

`3EE9F33F70AA5B0EAF952E3575AED375A7C711E123DCE0E0006FEAAC7BF85065`

Build configuration:

- .NET 9 Windows Desktop
- Release configuration
- application/product version: v1.9.0; assembly/file version: v1.9.0.0
- 10 selected Stage F/G smoke suites passed on the first run
- `KeyboardMouseWindowsInputSmoke` initially encountered its pre-existing
  asynchronous initial-neutral timing race, then passed five consecutive
  no-build reruns; no product or test source was changed
- Release build baseline: 0 warnings / 0 errors

The historical Bridge releases under `bridge-application/Legacy/` remain preserved and unchanged. The prior nonlegacy v1.6.0 package is not part of the current public release surface.

## Android artifact

Path:

`android-application/NICE64-Android-Companion.apk`

SHA-256:

`E40C976B9D5A5BDE26B77F834B4F0EE59719D9007DB58AD3A531E4BEF680260A`

Build identity and validation:

- version name `1.2.0`; version code `2`;
- minimum API 26; target API 36;
- JVM 338/338 PASS (294 app + 44 core);
- lint and debug APK build PASS;
- APK Signature Scheme v2 verification PASS with one Android Debug signer;
- prior connected instrumentation baseline: 81/81 PASS;
- G2 direct Mouse/Mario Artist hardware validation and Controller/Transfer Pak
  regressions PASS. The post-G2 orientation artifact is identified separately
  by its accepted hash; no additional physical result is inferred from its
  automated build alone.

## Keyboard/Mouse diagnostic ROM

Path:

`rom/keyboard-mouse-test/nice64-keyboard-mouse-test.z64`

SHA-256:

`86B747D967377FAA72AA9C6798999E06FF4FF32D24AE4EBD48C2B646E841D4ED`

The included source and ROM implement the real-N64 RandNET Keyboard and N64
Mouse diagnostic used for identity, command, rollover, LED, button, relative
movement, and consume-once validation.

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

The newly packaged applications exclude:

- build-intermediate `bin/` and `obj/` trees from packaged source;
- PDB and temporary files;
- runtime logs;
- machine-local INI state;
- analog-calibration state;
- generated firmware build trees.

The Bridge release assembly was produced with deterministic, path-mapped build
metadata so its public CodeView reference uses a neutral virtual source root.

The v1.3.0 release candidate retains the accepted G2 typed direct
Keyboard/Mouse input on Core 1 while
preserving Joybus timing, Controller/accessory behavior, Network Pak protocol,
and physical RUDP tuning. The post-G2 mouse-orientation correction is confined
to Android and Windows clients. NICE Browser v1.4.1 is a release-identity-only
promotion of its confirmed working source lineage. Frozen RUDP parameters
remain UDP port 6465, window 8, physical flight cap 2, payload 512 bytes, RTO
40 ms, and retries 4.

Firmware personality requests are volatile one-shot watchdog-reboot selections,
not flash-persisted preferences. The selector is consumed and cleared during
initialization, and a cold boot after power removal safely defaults to
Controller personality. This behavior is present throughout the retained
unified-firmware lineage beginning with v1.1.0.

## Documentation

- [Bridge user guide](BRIDGE_USER_GUIDE.md)
- [Bridge API v1 guide](BRIDGE_API.md)
- [Bridge v1.7.0 changes](BRIDGE_v1.7.0_CHANGES.md)
- [Bridge v1.8.0 changes](BRIDGE_v1.8.0_CHANGES.md)
- [Android companion](ANDROID_COMPANION.md)
- [NICE Browser v1.4.1](../rom/NICE-Browser-v1.4.1/README.md)
- [Firmware v1.2.0 changes](FIRMWARE_v1.2.0_CHANGES.md)

The authoritative artifact hashes are also listed in the repository-root `CHECKSUMS_SHA256.txt`.
