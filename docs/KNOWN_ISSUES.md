# Known Issues and Pending Limitations

This document separates current limitations and incompletely characterized
behavior from features that are planned but not yet implemented. Planned work
is not a statement that the corresponding existing manual workflow is broken.

## Known Issues / Known Limitations

### Quick Mode reconnect and transport switching

Normal Product Quick Mode operation over Reliable UDP has been validated on
real N64 hardware, including protected Network Pak transfers with exact CRC and
zero reported errors/timeouts. RUDP-C is physically accepted and frozen; it is
not considered unstable.

The following lifecycle transitions have not received the same level of
repeated hardware validation:

- Quick Mode reconnect and session renewal;
- TCP to Quick Mode handoff;
- Quick Mode to TCP handoff;
- repeated TCP and Quick Mode switching.

An intermittent connection or session-establishment timeout was observed during
development, but it has not been established as a persistent or reliably
reproducible defect. If reconnection or transport switching fails, reconnect or
restart the Bridge/device, or temporarily return to TCP. TCP remains the
reference Network Pak transport.

### Browser and Speed Test ROM throughput

The current Browser and Speed Test ROM/application data path was originally
designed around Serial/USB-style Network Pak transfers. It performs many small
16-byte request/response transactions and does not efficiently use the available
Wi-Fi carrier capacity.

Consequently:

- current ROM performance does not represent available Wi-Fi TCP or RUDP
  carrier throughput;
- the Speed Test ROM can report approximately 1 KiB/s even when direct RUDP
  carrier testing is substantially faster;
- Browser image or resource streaming can fail or terminate early;
- similar Browser/image failures have occurred over TCP and with earlier
  pre-RUDP firmware, so they are not established as RUDP-specific corruption;
- the Browser/Speed Test ROMs and associated Bridge modules need a future
  Wi-Fi-aware bulk-transfer optimization.

Serial/USB compatibility must remain a first-class supported option when that
optimization is implemented.

**RUDP carrier capability is not Network Pak or ROM end-to-end throughput.** No
exact future application-throughput target has been selected.

## Planned / Not Yet Implemented

### Seamless firmware mode changes over Wi-Fi

Firmware roles cannot yet be switched seamlessly through the normal Wi-Fi
control path. USB mode-changing and setup capability remains available as
currently implemented.

The planned Bridge lifecycle is to request a firmware mode change over Wi-Fi,
receive acknowledgement, handle the Pico reboot/disconnection, rediscover and
reconnect, and verify the requested mode. This is planned post-release work,
not a current confirmed bug.

### Automatic Game Boy save persistence lifecycle

Automatic ongoing `.sav` synchronization is planned but is not yet the active
final persistence lifecycle. While the relevant Normal/Transfer Pak mode is
active, the intended design is to track Game Boy save changes automatically.
Before leaving that mode, it should flush pending state, verify the Bridge-side
`.sav` update, mark the state clean, and deactivate or clear stale working
state. This prevents save state from carrying across unrelated firmware or
accessory modes.

Current manual and existing save operations are not known to be broken. Real
Pokémon Stadium and Pokémon Crystal testing has successfully written the Game
Boy save back to `.sav` over Wi-Fi.

### Automatic Controller Pak persistence lifecycle

The corresponding planned Controller Pak lifecycle will track dirty Pak state,
automatically synchronize the backing `.mpk`, flush and verify before a mode or
accessory transition, mark the state clean, deactivate or clear stale working
state, and reload the authoritative image when activated again.

This is planned functionality, not a current confirmed defect. Existing manual
Controller Pak load/save operations remain supported.

### Other planned Bridge work

Other future concepts include GB ROM History/Favorites, Controller Pak `.mpk`
History/Favorites, a documented Bridge API, an Android remote/controller
client, and a headless standalone Bridge Server. These are summarized in the
[Bridge application documentation](BRIDGE_APPLICATION.md#future-direction)
and are not implemented in the current release.
