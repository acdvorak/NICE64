# NICE64 Unified Firmware

The current firmware is Universal Firmware v1.2.0 for Pico 2
W. It combines Controller, Standalone Diagnostics, and Standalone Network
personalities and supports USB, Wi-Fi/TCP, and the frozen reliable-ordered UDP
carrier used by Product Quick Mode. It also supports the session/sequence
protected, one-way `LATEST_STATE` receive path used for disposable complete
controller-state delivery.

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
