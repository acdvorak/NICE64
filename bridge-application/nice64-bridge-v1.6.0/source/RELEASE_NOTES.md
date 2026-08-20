# NICE64 Bridge release notes

## RUDP-B development checkpoint — hardware pending

- Added an isolated manual-IP UDP diagnostic client for port 6465.
- Uses a fresh cryptographically generated nonzero session ID per client.
- Exercises HELLO/BUSY, PING/PONG, bounded echo, diagnostic counters, release,
  stale-session rejection, malformed packets, and delivery-class rejection.
- It is not connected to normal Bridge routing or the transport selector.
- Network Pak remains on the existing USB/TCP paths; Fast Mode remains hidden
  and unsupported.

## Development checkpoint — Network Pak RUDP-A

- Added an unconnected host-only selective-repeat reliability engine and
  deterministic loss/duplicate/reorder/delay/ACK-loss simulation.
- Full algorithmic and existing Bridge regressions pass.
- No firmware UDP or Network Pak routing is present; Bridge remains v1.6.0.

## Development checkpoint — Network Pak RUDP-0

- Reserved generic wireless delivery classes: `RELIABLE_ORDERED=0` and
  `LATEST_STATE=1`.
- Fast/latest-state mode is hidden and deterministically unsupported.
- No UDP carrier or reliability behavior is implemented, so Bridge remains
  v1.6.0 and existing USB/TCP behavior is unchanged.

## v1.6.0 — wireless bulk accessory transfers (hardware pending)

- Full GB/GBC ROM loading uses the selected USB or TCP Bridge line transport.
- Controller Pak `.mpk` load, create, save, and disconnect-save use the selected
  USB or TCP transport.
- Bulk operations retain single-reader ownership and pause ordinary controller
  command traffic while acknowledgements are in flight.
- The obsolete first-64-KiB GB loader is hidden from the normal UI; its
  diagnostic handler remains compiled.
- The firmware line protocol is unchanged. Universal Firmware remains v1.1.0.

Static and regression tests pass. Real USB and Wi-Fi/TCP hardware validation is
still required before acceptance.
# Product Quick Mode

- Activates explicit Network Pak **Quick Mode (Reliable UDP)** over the frozen
  RUDP-C carrier while retaining TCP as the reference selection.
- Routes TCP and RUDP through the same `NetworkPakBridgeService` application
  boundary with unchanged Network Pak requests and responses.
- Adds fresh nonzero RUDP sessions, bounded application framing, explicit
  failure reporting, single-owner handoff, and focused parity/session tests.
- Normal Controller remains TCP/PIO. Fast/LATEST_STATE remains unsupported.
- Normal real-N64 protected Network Pak operation passed with exact CRC and zero
  reported errors/timeouts. Repeated reconnect/session renewal and TCP↔Quick
  handoff remain less exhaustively validated. An intermittent establishment
  timeout observed during development has not been confirmed as a persistent
  defect; TCP remains the supported reference transport. This lifecycle note
  does not reopen the physically accepted and frozen RUDP-C carrier.
