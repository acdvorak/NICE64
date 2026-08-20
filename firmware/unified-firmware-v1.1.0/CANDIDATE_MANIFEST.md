# Candidate manifest

Candidate: `networkpak-rudp-quickmode-hardware-pending`

Prepared: 2026-08-16 America/Phoenix

Release validation update: normal Product Quick Mode operation subsequently
passed available real-N64 protected Network Pak validation with exact CRC and
zero reported errors/timeouts. The candidate name remains unchanged for
provenance; the validated UF2 bytes are published as
`NICE64-Universal-Firmware-v1.1.0.uf2`. Repeated reconnect/session renewal and TCP↔Quick Mode
handoff remain scheduled for additional lifecycle validation; an intermittent
development-time establishment timeout was not confirmed as a persistent
defect. RUDP-C remains physically accepted and frozen.

## Frozen carrier baseline

RUDP-C is physically accepted, closed, and frozen. Pre-integration hashes:

- UF2: `46C4538B6FF5E93D597D9A2A0A9F13A036A807D1D26B61E05612D378631867D2`
- ELF: `1409F2D0D9FCECE9B1757C35C91529BCF96041AFFAD6A43C092F5D38434F8BE6`
- MAP: `2D065CC6309B7499067070C8B827037BF99A992C116196906015146B5764F35F`

These frozen baseline hashes are historical validation provenance and do not
identify artifacts distributed in the current public release.

Frozen tuple: wire 1; RELIABLE_ORDERED/class 0; window 8; physical cap 2;
payload 512; RTO 40 ms; four retransmissions after original; UDP 6465;
two-slot staging; unchanged ACK and session semantics.

## Integration artifacts

- UF2 SHA256:
  `1B2B57CA8E79F40612F350F08F779714F34D04DB19C7EB6BEB165DC3C6836CB8`
- Bridge EXE SHA256:
  `8DCFE9E6B1308540AD17680CBFF25F12672835CD1A5F8BEB807EDE7E0DB3C72E`
- Bridge DLL SHA256:
  `869C16477CC522A47E110F1513DB1E92AA6E1C0E7921421FFE8F781F9D8D71CC`

The UF2 was reproduced in both the workspace and an independent short-path
clean build with the same hash.

## Build configuration

- Pico board: `pico2_w` / RP2350 Arm secure
- build type: Release
- universal Network personality: enabled
- Joybus RX: PIO (`NICE_RX_BACKEND_PIO=1`)
- execution: flash-XIP (`PICO_COPY_TO_RAM=0`)
- firmware image: 371224 bytes
- persistence boundary: 371224 / 4186112 bytes, PASS
- Bridge: .NET 9 Windows Release, 0 warnings / 0 errors

## Protection disposition

- Frozen carrier core: 8/8 byte-identical.
- UDP service `.c/.h`: authorized adapter/service routing deltas only.
- Protected firmware: 16/18 byte-identical; authorized changes are only
  `network_pak.c/.h` at the existing owner/sink/parser boundary.
- RUDP-0: 4/4 byte-identical.
- RUDP-A host algorithms: 2/2 byte-identical.
- Joybus, Core0 controller state, PIO, Normal Controller, Controller Pak,
  Transfer Pak, and normal accessories: untouched by this integration.
- Network Pak application format, response format, one-byte application
  sequence, ordering, timeout, and Core0 snapshot semantics: unchanged.
- TCP: preserved as explicit reference; no silent fallback.
- Normal Controller: remains TCP/PIO.
- Fast/LATEST_STATE: reserved, unsupported, unadvertised, unselectable.

## Verification

Bridge Release and firmware builds pass. Adapter parity, multi-payload exact
reassembly, duplicate/reorder/wrap/stale-session isolation, reconnect session
freshness, TCP isolation, RUDP simulation, RUDP-0, Wi-Fi provisioning, bulk
transfer, Network Pak Layer 2, and browser fixtures pass.

## Disposition

```text
RUDP-C — PHYSICALLY ACCEPTED / FROZEN
PROTECTED NETWORK PAK RUDP — IMPLEMENTED / NORMAL HARDWARE OPERATION PASS
PRODUCT QUICK MODE — IMPLEMENTED / NORMAL HARDWARE OPERATION PASS
NETWORK PAK TCP — PRESERVED
NORMAL CONTROLLER TCP/PIO — UNCHANGED
LATEST_STATE / FAST — RESERVED / UNSUPPORTED
GOLD — NOT YET DECLARED
```
