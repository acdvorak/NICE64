# NICE64 Unified Firmware

## Status and purpose

Universal Firmware v1.2.0 combines the proven Controller, Standalone
Diagnostics, and Standalone Network personalities in one Pico 2 W image. The
selected personality is stored in flash and applied at boot. The current
Product Quick Mode image is build-, static-, and real-N64 validated for normal
protected Network Pak operation, including exact CRC and zero reported
errors/timeouts.

## Runtime personalities

- **Controller** provides normal N64 controller state and the selected normal
  accessory: no accessory, Controller Pak, or Transfer Pak.
- **Standalone Diagnostics** exposes the bounded diagnostics personality used
  for hardware and protocol checks.
- **Standalone Network** exposes Network Pak behavior. USB and TCP remain
  supported; Product Quick Mode explicitly routes Network Pak application
  traffic over reliable ordered UDP.

Mode queries and requests use the firmware control plane. A mode change writes
the boot selection and reboots. USB is the recovery and provisioning path.

## Core ownership

Core0 owns deterministic Joybus receive, command decoding, immediate response
preparation, and transmit. It never waits for USB, TCP, UDP, Wi-Fi, storage, or
Bridge activity. Timing-critical project code is explicitly retained in SRAM;
ordinary control and networking code executes from flash/XIP.

Core1 owns USB service, line framing and dispatch, controller-state publication,
CYW43/lwIP polling, Wi-Fi provisioning and association, TCP, RUDP, persistence,
and bounded background service work. Cross-core data is published through
bounded snapshots/shared state; network callbacks do not execute Joybus work.

## Frozen Joybus boundary

The validated receive backend is PIO (`NICE_RX_BACKEND_PIO=1`). Controller
normal replies use the proven CPU bit-bang transmit path. Network personality
uses PIO for normal replies and the CPU bit-bang override for strict Network Pak
replies. GPIO25 heartbeat use is disabled for Pico 2 W because that pin belongs
to CYW43. The onboard CYW43 LED is not used as a Core0 heartbeat.

Joybus timing, PIO programs, Core0 ownership, and accessory semantics are
frozen. They are not transport extension points.

## Accessories

Controller Pak uses the existing 32 KiB image boundary. Transfer Pak retains
the known-good mapper/accessory implementation and the full GB/GBC ROM upload
path. Firmware accepts ROM/save data and exposes bounded read/write operations;
the Bridge owns file selection and PC-side file safety. Network Pak retains its
existing application request/response format, one-byte application sequence,
ordering, timeout, and Core0 snapshot behavior.

## Host transports

The Core1 line dispatcher gives USB and TCP the same control-plane command
semantics. TCP listens on port 6464 and has one active owner. USB remains
available for provisioning and recovery, while ownership rules prevent two
hosts from concurrently driving the same state.

The UDP service listens on port 6465. Delivery class 0 is the frozen
`RELIABLE_ORDERED` carrier. Product Quick Mode routes only Network Pak traffic
through that carrier. Delivery class 1, `LATEST_STATE`, accepts disposable,
complete controller-state replacement with session and sequence protection. It
uses no per-update acknowledgement, retry window, reconstruction, or backlog,
and it is not a Network Pak carrier.

## Compatibility and validation

The architecture preserves analog calibration, automatic accessory/personality
selection, Controller Pak, Transfer Pak, GB Tower operation, Diagnostics,
Network Pak TCP, USB fallback, and the proven PIO Joybus receive backend.
RUDP-C synthetic hardware acceptance is complete. The protected Network Pak
Quick Mode integration has passed clean builds, simulations, parity tests,
protected-source audits, and available real-N64 validation. Repeated reconnect,
session-renewal, and TCP↔Quick transport handoff remain less exhaustively tested
and are documented as a lifecycle validation note rather than a confirmed
carrier defect. RUDP-C remains physically accepted and frozen.

## Deferred work

UDP discovery, static IPv4 provisioning, Android clients, and headless server
operation remain future work. `LATEST_STATE` controller delivery and the
documented local Bridge API are current v1.2.0/v1.7.0 behavior. Network Pak
throughput investigation remains separate and must not be conflated with
synthetic RUDP carrier throughput.
Joybus redesign, broad Core0 changes, RTC work, removable storage, and Rumble
are not part of this release.
