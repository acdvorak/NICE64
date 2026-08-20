# NICE64

NICE64 connects a real Nintendo 64 controller port to a Windows Bridge through
Pico 2 W firmware. The current release-candidate stack combines normal
controller/accessory operation, Diagnostics, Network Pak, USB, Wi-Fi/TCP, and
Product Quick Mode over reliable ordered UDP.

## Current versions and status

- **Universal Firmware:** v1.1.0 release candidate
- **NICE64 Bridge:** v1.6.0 release candidate
- **RUDP-C carrier:** physically accepted and frozen
- **Network Pak TCP:** supported reference transport
- **Product Quick Mode:** validated on real N64 hardware with exact protected
  Network Pak transfer CRC and zero reported errors/timeouts
- **Gold:** not yet declared

The earlier v1.4 Bridge and ROM releases remain under their existing `Legacy`
convention. Historical Network Pak Gold and Regular/Normal Gold firmware remain
unchanged.

## Repository layout

```text
NICE64/
|-- bridge-application/
|   |-- nice64-bridge-v1.6.0/
|   `-- Legacy/
|-- configuration/
|-- docs/
|-- examples/
|-- firmware/
|   |-- unified-firmware-v1.1.0/
|   |-- source/
|   |   `-- NICE64-Unified-Firmware-v1.1.0/
|   `-- Legacy/
|       |-- network-pak-gold/
|       `-- regular-gold-normal/
|-- hardware/
|-- rom/
|   `-- Legacy/
|-- CHECKSUMS_SHA256.txt
|-- LICENSE.md
`-- README.md
```

The established top-level structure and historical releases are intentionally
preserved.

## Quick start

1. Flash the v1.1.0 UF2 from `firmware/unified-firmware-v1.1.0/` to a Pico 2 W.
2. Start
   `bridge-application/nice64-bridge-v1.6.0/application/PicoSwitchController.exe`.
3. Connect over USB and select the NICE64 COM port.
4. Provision Wi-Fi over USB and note the DHCP address.
5. Use Controller personality for normal controller, Controller Pak, or
   Transfer Pak operation.
6. Use Network personality for Network Pak. Connect by manual IPv4 over TCP
   port 6464, then explicitly select TCP or Quick Mode (Reliable UDP, port
   6465).

USB remains the provisioning and recovery path. Quick Mode does not silently
fall back to TCP.

## Current architecture

Core0 exclusively owns deterministic Joybus work. The validated firmware uses
PIO receive, the proven Controller CPU-bitbang transmit path, and the existing
Network PIO/strict-bitbang policy. Core1 owns USB, controller-state publication,
CYW43/lwIP, Wi-Fi, TCP, reliable UDP, persistence, and Bridge command service.
Joybus, Core0 timing, PIO programs, and accessory semantics are frozen.

Universal Firmware supports Controller, Standalone Diagnostics, and Standalone
Network personalities in one image. The Bridge verifies and controls the active
personality, manages controller mapping/calibration, transfers GB/GBC ROM and
save data, manages Controller Pak images, and hosts Network Pak modules.

The frozen reliable UDP tuple is wire version 1, RELIABLE_ORDERED class 0,
window 8, physical cap 2, 512-byte payload, 40 ms RTO, four retransmissions, and
UDP port 6465. LATEST_STATE/Fast remains reserved and unsupported.

RUDP carrier capability is not Network Pak or ROM end-to-end throughput.

## Known Issues / Validation Notes

Normal Product Quick Mode operation is validated on real N64 hardware, and
RUDP-C remains physically accepted and frozen. See [Known Issues and Pending
Limitations](docs/KNOWN_ISSUES.md) for lifecycle validation notes, current ROM
throughput limitations, and planned persistence work.

## Documentation

- [Unified Firmware](docs/UNIFIED_FIRMWARE.md)
- [Wi-Fi implementation](docs/WIFI_IMPLEMENTATION.md)
- [Bridge architecture and user guide](docs/BRIDGE_APPLICATION.md)
- [Build the Bridge](docs/BUILD_BRIDGE.md)
- [Build manifest](docs/BUILD_MANIFEST.md)
- [Configuration](docs/CONFIGURATION.md)
- [Network Pak protocol](docs/NETWORK_PAK_PROTOCOL.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Known Issues and Pending Limitations](docs/KNOWN_ISSUES.md)
- [Changelog](docs/CHANGELOG.md)
- [Licenses and notices](docs/LICENSES.md)

Historical ROM documentation remains with each ROM beneath `rom/Legacy/`.

## Build overview

Bridge requires the .NET 9 SDK on Windows:

```powershell
dotnet build bridge-application/nice64-bridge-v1.6.0/source/PicoSwitchController.csproj -c Release
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
- Do not advertise reserved LATEST_STATE/Fast behavior.
- Do not compare synthetic carrier peak rates directly with real Network Pak
  application throughput.

## Support and license

NICE64 is released under the MIT License. See [LICENSE.md](LICENSE.md) and
[third-party notices](docs/LICENSES.md).

NICE64 is a free community project. Donations are never expected but help with
hardware and test costs: <https://paypal.me/melfegor>.
