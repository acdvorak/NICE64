# NICE64 Universal Firmware v1.2.0

**Release status:** Final / accepted. Build, regression, source review, and
available real-hardware validation passed for the exported v1.2.0 artifact.

## Release summary

Firmware v1.2.0 matures the unified Pico 2 W image while preserving the
established Bridge, Joybus, accessory, Network Pak, RUDP wire, USB identity, and
flash-partition contracts. Controller, Standalone Diagnostics, and Standalone
Network personalities remain in one image.

Release UF2:

```text
firmware/unified-firmware-v1.2.0/NICE64-Universal-Firmware-v1.2.0.uf2
```

SHA-256:

```text
95C6F83D84FDC32C4E1ADCA638A37B1BB06F56E897B43658DF09D5153FEE2359
```

## New and matured capabilities

- Unified Pico 2 W controller, normal accessory, Diagnostics, and Network Pak
  firmware personalities.
- USB control plus Wi-Fi/TCP service on port 6464.
- Reliable ordered UDP infrastructure for Network Pak Quick on port 6465.
- One-way `LATEST_STATE` receive path for low-latency complete controller-state
  replacement without per-update ACKs, retries, or backlog.
- Standalone Keyboard / Mouse service with RandNET Keyboard and N64 Mouse
  selection while preserving the Controller/accessory role boundary.
- Typed direct-mobile Keyboard held-state and Mouse cumulative-pointer packets
  on Core1, preserving the existing Controller `LATEST_STATE` payload.
- A 250 ms fail-neutral timeout for direct Keyboard/Mouse state so a missing
  final datagram cannot leave a key or button held indefinitely.
- Bounded session ownership so a retired controller sender lifetime cannot
  regain authority after a legitimate replacement session.
- Core1 ownership of USB, line dispatch, controller publication, CYW43/lwIP,
  Wi-Fi, TCP/UDP service, and bounded persistence work, separate from Core0
  Joybus ownership.

## Transfer Pak flash safety and reliability

- External flash erase/program operations use `flash_safe_execute`.
- `GBFBEGIN` strictly validates supported ROM length and partition bounds.
- `GBFP` strictly validates 256-byte page bounds and ordering.
- The exact immediately previous successfully programmed page is idempotently
  replayable using its offset and CRC32. A valid retry returns the original
  success without a second physical erase/program, page-count increment, or
  transaction advance.
- Same-offset replay with changed contents is rejected, and older stale or
  skipped pages remain out-of-order errors.
- `GBFCOMMIT` requires a complete, valid transaction.
- `GBFCRC` reports the committed ROM CRC for Bridge verification.
- The legacy full-GB staging overflow is bounded.
- The build enforces a 1 MiB firmware-image boundary, protecting the Transfer
  Pak partition at flash offsets `0x100000` through `0x300000`.

## Compatibility

- Joybus timing, Core0 command/response ownership, PIO programs, and accessory
  semantics are unchanged by release versioning.
- Controller Pak and Transfer Pak command grammar remain compatible with the
  current Bridge.
- Network Pak application framing and RUDP wire version remain unchanged.
- USB VID/PID and the existing flash partition layout are unchanged.
- Bridge v1.8.0 is the recommended paired desktop release.

## Validation observations

- The accepted G2 Release build completed with the 1 MiB boundary guard passing
  at 387,184 bytes.
- The embedded `VERSION?` identity reports `1.2.0`.
- Available hardware validation includes Pokémon Gold and Pokémon Crystal Load
  Max success, Wario Land 2 running through patched Pokémon Stadium for about
  30 minutes over Wi-Fi, and Pokémon Crystal loading over Wi-Fi with its
  retained save.
- Network Pak browser operation remained functional during the accepted test
  cycle.
- RandNET Keyboard and N64 Mouse passed the dedicated diagnostic-ROM hardware
  suite. Android direct Mouse input was validated in Mario Artist. Pokémon
  Stadium 2 GB Tower / Transfer Pak and Star Fox 64 / standard Controller
  regressions passed on the same firmware.

These are validation observations, not universal performance guarantees.

## Review disposition

Static review identified that `core_transport_core1_call_core0()` waits for
the matching Core0 response generation without a timeout. This is accepted as
an accepted deferred risk and intentionally remains unchanged because no supported
reproducible mailbox-liveness failure has been demonstrated. Timeout, recovery,
mailbox-generation, Core0, and Joybus behavior were not changed speculatively
at this protected synchronization boundary. Revisit only with a reproducible
failure.

## Known limitations

- TCP remains the Network Pak reference transport; not every historical TCP or
  end-to-end performance issue is claimed solved.
- Wi-Fi quality, host scheduling, and N64 application polling can affect
  observed throughput and responsiveness.
- `LATEST_STATE` is intentionally disposable current-state delivery, not a
  reliable event stream.
- Forced power loss can interrupt operations despite transaction and partition
  protections; use normal Bridge lifecycle controls.
