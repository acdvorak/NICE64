# NICE64 Unified Firmware

The current release-candidate firmware is Universal Firmware v1.1.0 for Pico 2
W. It combines Controller, Standalone Diagnostics, and Standalone Network
personalities and supports USB, Wi-Fi/TCP, and the frozen reliable-ordered UDP
carrier used by Product Quick Mode.

See [docs/UNIFIED_FIRMWARE.md](docs/UNIFIED_FIRMWARE.md) for architecture,
ownership, compatibility boundaries, implemented behavior, and deferred work.
See [docs/WIFI_IMPLEMENTATION.md](docs/WIFI_IMPLEMENTATION.md) for provisioning,
ports, session rules, the frozen RUDP tuple, and validation status.

Normal protected Network Pak Quick Mode operation has passed available real-N64
validation with exact CRC and zero reported errors/timeouts. Repeated reconnect,
session-renewal, and TCP↔Quick transport handoff remain scheduled for additional
post-release validation; RUDP-C itself remains physically accepted and frozen.
