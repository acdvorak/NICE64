# NICE64

NICE64 connects a real Nintendo 64 controller port to a Windows Bridge through
Pico 2 W firmware. The current release stack combines normal
controller/accessory operation, Diagnostics, Network Pak, USB, Wi-Fi/TCP, and
Product Quick Mode over reliable ordered UDP. Bridge v1.9.0 also provides
one-way `LATEST_STATE` wireless Controller delivery, an optional authenticated
API, RandNET Keyboard and N64 Mouse standalone personalities, and integration
with the NICE64 Android companion.

## Current versions and status

- **Universal Firmware:** v1.3.0 release candidate — hardware validated —
  `firmware/unified-firmware-v1.3.0/`
- **Hardware-validated firmware reference:** v1.2.0 G2 —
  `firmware/unified-firmware-v1.2.0/`
- **NICE64 Bridge:** v1.9.0 — `bridge-application/nice64-bridge-v1.9.0/`
- **NICE64 Android companion:** v1.2.0 (`versionCode` 2), debug-signed —
  `android-application/`
- **NICE Browser:** v1.4.1 — `rom/NICE-Browser-v1.4.1/`
- **RUDP-C carrier:** physically accepted and frozen
- **Network Pak TCP:** supported reference transport
- **Product Quick Mode:** validated on real N64 hardware with exact protected
  Network Pak transfer CRC and zero reported errors/timeouts

The earlier v1.4 Bridge and Browser v1.4.0 layout remains under the existing
`Legacy` convention. NICE Browser v1.4.1 is the current release at
`rom/NICE-Browser-v1.4.1/`. Historical Network Pak Gold and Regular/Normal Gold
firmware remain unchanged.

The v1.3.0 UF2 was compiled from the accepted G2 functional source with the
approved version-only declarations. Build, static validation, and extensive
real-hardware use passed. The preserved v1.2.0 G2 UF2 remains the prior
known-good physical reference.

## Repository layout

```text
NICE64/
|-- android-application/
|-- bridge-application/
|   |-- nice64-bridge-v1.9.0/
|   |-- nice64-bridge-v1.8.0/
|   `-- Legacy/
|-- configuration/
|-- docs/
|-- examples/
|-- firmware/
|   |-- unified-firmware-v1.3.0/
|   |-- unified-firmware-v1.2.0/
|   |-- source/
|   |   |-- NICE64-Unified-Firmware-v1.3.0/
|   |   `-- NICE64-Unified-Firmware-v1.2.0/
|   `-- Legacy/
|       |-- network-pak-gold/
|       `-- regular-gold-normal/
|-- hardware/
|-- rom/
|   |-- keyboard-mouse-test/
|   |-- NICE-Browser-v1.4.1/
|   `-- Legacy/
|-- CHECKSUMS_SHA256.txt
|-- LICENSE.md
`-- README.md
```

The established top-level structure and historical releases are intentionally
preserved.

## Quick start

1. For release-candidate testing, flash the v1.3.0 UF2 from
   `firmware/unified-firmware-v1.3.0/` to a Pico 2 W. Use the preserved v1.2.0
   G2 artifact when the hardware-validated baseline is required.
2. Start
   `bridge-application/nice64-bridge-v1.9.0/application/PicoSwitchController.exe`.
3. Connect over USB and select the NICE64 COM port.
4. Provision Wi-Fi over USB and note the DHCP address.
5. Use Controller personality for normal controller, Controller Pak, or
   Transfer Pak operation.
6. Use Standalone Keyboard / Mouse for RandNET Keyboard or N64 Mouse. The
   Bridge verifies the selected firmware mode before forwarding input.
7. Use Network personality for Network Pak. Connect by manual IPv4 over TCP
   port 6464, then explicitly select TCP or Quick Mode (Reliable UDP, port
   6465).

USB remains the provisioning and recovery path. Quick Mode does not silently
fall back to TCP.

Personality requests are volatile one-shot reboot selections rather than flash
preferences. Removing Pico power and starting again returns Universal Firmware
to Controller personality, providing a straightforward recovery path from a
stuck standalone device.

### Android companion

Install `android-application/NICE64-Android-Companion.apk`, then use the
Bridge's local onboarding workflow to pair on a trusted LAN. Android management
traffic uses the authenticated, pinned HTTPS Bridge API. Gameplay controller
state does not pass through HTTP or the Bridge: while **NICE Mobile Primary** is
authoritative it travels directly from Android to NICE64/Pico over disposable
UDP `LATEST_STATE` packets. RandNET Keyboard held state and N64 Mouse cumulative
relative movement also use the direct Android-to-Pico mobile input data plane
after the Bridge verifies the requested standalone mode. Keyboard LED state
returns through Pico and the Bridge API.

Controller authority has exactly two values: **Bridge Primary** and **NICE
Mobile Primary**. The companion supports Android USB/Bluetooth controllers,
saved controller mappings, embedded and full-screen touch controls, History,
Accessories and Modes, and Advanced diagnostics. Controller Blackout Mode keeps
the activity foregrounded on a true-black OLED-friendly surface and prevents
normal inactivity timeout; it does not provide controller operation while the
Android lock screen owns focus or the display is turned off with the power
button.

The frozen Android project snapshot is available at
`android-application/source/`; build details are in the component README.

## Current architecture

Core0 exclusively owns deterministic Joybus work. The validated firmware uses
PIO receive, the proven Controller CPU-bitbang transmit path, and the existing
Network PIO/strict-bitbang policy. Core1 owns USB, controller-state publication,
CYW43/lwIP, Wi-Fi, TCP, reliable UDP, persistence, and Bridge command service.
Joybus, Core0 timing, PIO programs, and accessory semantics are frozen.

Universal Firmware supports Controller, Standalone Diagnostics, Standalone
Network, and Standalone Keyboard / Mouse personalities in one image. Keyboard /
Mouse selects either RandNET Keyboard or N64 Mouse. The Bridge verifies and
controls the active personality, manages controller mapping/calibration,
transfers GB/GBC ROM and save data, manages Controller Pak images, and hosts
Network Pak modules.

The frozen reliable UDP tuple is wire version 1, RELIABLE_ORDERED class 0,
window 8, physical cap 2, 512-byte payload, 40 ms RTO, four retransmissions, and
UDP port 6465. Delivery class 1, `LATEST_STATE`, carries disposable complete
direct human-input state with session/sequence protection. Controller and
Keyboard use disposable complete state; Mouse uses cumulative movement totals
so replacement packets do not lose relative motion. `LATEST_STATE` is not a
reliable event stream and does not change Network Pak's reliable-ordered
carrier.

RUDP carrier capability is not Network Pak or ROM end-to-end throughput.

## Known Issues / Validation Notes

Normal Product Quick Mode operation is validated on real N64 hardware, and
RUDP-C remains physically accepted and frozen. Transfer Pak save persistence is
event-driven in Bridge v1.8.0, eliminating periodic Bridge save polling during
gameplay. RandNET Keyboard and N64 Mouse passed diagnostic-ROM hardware tests
and extensive real use, including Keyboard operation with a Windows CE
port/side project and Mouse operation with Mario Studio on 64DD. Android direct
Mouse input was also validated with Mario Artist, while Transfer Pak and
standard Controller regressions passed with Pokémon Stadium 2 GB Tower and Star
Fox 64. See [Known Issues and Deferred Work](docs/KNOWN_ISSUES.md) for remaining
lifecycle and throughput limitations.

## Documentation

- [Unified Firmware](docs/UNIFIED_FIRMWARE.md)
- [Bridge User's Guide](docs/BRIDGE_USER_GUIDE.md)
- [Bridge API v1](docs/BRIDGE_API.md)
- [Android companion](docs/ANDROID_COMPANION.md)
- [Firmware v1.2.0 changes](docs/FIRMWARE_v1.2.0_CHANGES.md)
- [Bridge v1.8.0 changes](docs/BRIDGE_v1.8.0_CHANGES.md)
- [Wi-Fi implementation](docs/WIFI_IMPLEMENTATION.md)
- [Bridge architecture and user guide](docs/BRIDGE_APPLICATION.md)
- [Build the Bridge](docs/BUILD_BRIDGE.md)
- [Build manifest](docs/BUILD_MANIFEST.md)
- [Configuration](docs/CONFIGURATION.md)
- [Network Pak protocol](docs/NETWORK_PAK_PROTOCOL.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Known Issues and Deferred Work](docs/KNOWN_ISSUES.md)
- [Changelog](docs/CHANGELOG.md)
- [Licenses and notices](docs/LICENSES.md)

ROM documentation remains with each packaged ROM. The current Browser is at
`rom/NICE-Browser-v1.4.1/`; Browser v1.4.0 remains preserved under
`rom/Legacy/` as a historical release.

## Build overview

Bridge requires the .NET 9 SDK on Windows:

```powershell
dotnet build bridge-application/nice64-bridge-v1.9.0/source/PicoSwitchController.csproj -c Release
```

Firmware requires CMake, Ninja, the Arm GNU embedded toolchain, and a compatible
Pico SDK. Set `PICO_SDK_PATH`, then run the included clean Pico 2 W build helper
from the Unified Firmware source directory. Generated build output is excluded
from Git.

## Hardware safety

The current hardware uses Pico 2 W, USB-only Pico power, a permanent 4.7 kOhm
N64-side pull-up, and a 330 ohm series resistor on the data path. Never connect
N64 3.3 V to Pico VBUS, VSYS, or Pico 3V3. Hardware files remain under
`hardware/`.

## Release guardrails

- Do not change Joybus timing, Core0 ownership, or PIO/bitbang policy as part of
  transport or release maintenance.
- Preserve historical firmware and ROM releases.
- Keep TCP as the explicit reference path during Quick Mode validation.
- Keep `LATEST_STATE` scoped to direct human-input state; do not represent it
  as a reliable event stream or Network Pak carrier. Preserve cumulative Mouse
  totals and complete Controller/Keyboard state semantics.
- Do not compare synthetic carrier peak rates directly with real Network Pak
  application throughput.

## Support and license

NICE64 is released under the MIT License. See [LICENSE.md](LICENSE.md) and
[third-party notices](docs/LICENSES.md).

NICE64 is a free community project. Donations are never expected but help with
hardware and test costs: <https://paypal.me/melfegor>.
