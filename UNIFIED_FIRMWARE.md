# NICE64 Unified Firmware

The current Pico 2 W release candidate is Universal Firmware v1.3.0. It has
passed clean build, flash-boundary, embedded-version, and source-lineage checks,
but physical hardware validation is pending. The existing hardware-validated
v1.2.0 G2 UF2 remains preserved as the known-good reference. The firmware combines
Controller, Standalone Diagnostics, and Standalone Network
personalities plus Standalone Keyboard / Mouse, and supports USB, Wi-Fi/TCP, and the frozen reliable-ordered UDP
carrier used by Product Quick Mode. It also supports the session/sequence
protected, one-way `LATEST_STATE` receive path used for disposable complete
Controller-state delivery and typed direct RandNET Keyboard/N64 Mouse input.

See [docs/UNIFIED_FIRMWARE.md](docs/UNIFIED_FIRMWARE.md) for architecture,
ownership, compatibility boundaries, implemented behavior, and deferred work.
See [docs/WIFI_IMPLEMENTATION.md](docs/WIFI_IMPLEMENTATION.md) for provisioning,
ports, session rules, the frozen RUDP tuple, and validation status.

Normal protected Network Pak Quick Mode operation has passed available real-N64
validation with exact CRC and zero reported errors/timeouts. Repeated reconnect,
session-renewal, and TCP↔Quick transport handoff remain less exhaustively tested;
RUDP-C itself remains physically accepted and frozen. Firmware v1.2.0 also
includes Transfer Pak flash transaction safety, exact previous-page retry
idempotency, committed-ROM CRC reporting, and the 1 MiB firmware partition
guard. See [Firmware v1.2.0 changes](docs/FIRMWARE_v1.2.0_CHANGES.md).

The hardware-validated G2 image adds RandNET Keyboard and N64 Mouse standalone
operation while retaining the established Controller/accessory and Network Pak
boundaries. Android sends latency-sensitive Controller, Keyboard, and Mouse
state directly to Pico; management and mode selection remain Bridge-mediated.
