# Known Issues and Deferred Work

This document records current limitations of the NICE64 firmware v1.2.0 and
PicoSwitchController Bridge v1.8.0 release. It does not override the supported
workflows described in the [Bridge user guide](BRIDGE_USER_GUIDE.md).

## RC review dispositions

### BR-001 — BrowserLive local-network URL policy

**Status:** Accepted / deferred module hardening

The optional BrowserLive module can request HTTP/HTTPS resources reachable
from the host, including local/private-network destinations. BrowserLive and
the NICE Browser ROM are demonstration/application-layer functionality rather
than a hardened general-purpose browser. Browser-specific URL trust policy,
redirect filtering, and local-network filtering are outside the current
Bridge/Firmware release scope. This is not a core Bridge, API, transport,
firmware, Joybus, or Pak-wire release blocker.

### FW-001 — Core1 mailbox wait without timeout

**Status:** Accepted RC risk / no change without reproduction

Static review identified that `core_transport_core1_call_core0()` has no
timeout while waiting for the matching Core0 response generation. No occurrence
has been observed during current hardware development or validation, and the
review did not establish a supported lifecycle that leaves the mailbox
permanently unserviced. Because this is part of the established Core0/Core1
synchronization boundary, speculative timeout or recovery behavior could add
greater lifecycle risk. Preserve current v1.2.0 behavior and revisit only if a
reproducible mailbox-liveness failure is observed.

### BR-003 — LATEST_STATE diagnostic log amplification

**Status:** Resolved

Sustained UDP send failures no longer generate one forced diagnostic at the
controller reporting cadence. The Bridge reports the first failure, coalesces
continued failures into bounded periodic summaries, and reports recovery once
sending resumes. Healthy `LATEST_STATE` transport semantics are unchanged.

The RC static review has no remaining release blocker. BR-001 remains accepted
module-specific hardening debt, and FW-001 remains an accepted RC risk requiring
reproduction before any synchronization change.

## Network Pak application throughput

The existing Browser and Speed Test ROM/application path performs many small
16-byte request/response transactions. It therefore does not use the full
capacity of either the TCP or Reliable UDP Quick carrier.

Consequently:

- current ROM measurements are not direct carrier-throughput measurements;
- Browser image/resource streaming may be limited by application-level polling
  and request granularity;
- similar behavior can occur on TCP and Quick, so it is not evidence by itself
  of RUDP corruption;
- end-to-end Network Pak and ROM optimization remains deferred work.

The frozen Quick transport parameters are UDP port 6465, window 8, physical
flight cap 2, payload 512 bytes, RTO 40 ms, and retries 4.

## Hardware-specific USB reliability

Sustained Transfer Pak upload testing previously exposed Windows COM semaphore
timeouts on one Pico device. A second Pico using the same firmware and current
Bridge completed the workflow, and Wi-Fi completed the same 2 MiB upload and
produced a working game image. The issue followed the individual Pico across
Bridge versions and firmware reflash, so it is not classified as a v1.8.0
Bridge release blocker.

If a device exhibits repeated USB semaphore failures, try another known-good
Pico or use the supported Wi-Fi content path. A cable change alone did not
resolve the observed device-specific case.

## Android screen-off controller limitation

True Android lock-screen or power-button-off physical-controller continuation
is not supported. **Controller Blackout Mode** is the supported workaround: it
keeps the NICE64 Activity foregrounded on a true-black surface and prevents the
normal inactivity timeout while preserving physical-controller operation.
Blackout exits when the accepted Android `ON_STOP` lifecycle takes ownership,
including the lock-screen path. Restore display or Android Back exits Blackout
normally.

## API scope and token provisioning

The authenticated API is optional and disabled by default. Its supported
mutation surface includes firmware, accessory, Network Pak module, Network Pak
TCP/Quick carrier selection, session-scoped controller Primary selection, and
session-scoped History activation. USB carrier selection, Connect/Disconnect,
content transfer, Wi-Fi provisioning, controller streaming, and transport
tuning are not HTTP routes.

Token storage is protected for the current Windows user. Deployment and token
provisioning remain local administrator/user responsibilities; the Bridge does
not expose an unauthenticated token-creation endpoint. See
[Bridge API v1](BRIDGE_API.md).

## Observation model

External clients use the bounded polling contract:

1. submit a mutation and receive `202 Accepted` with an operation ID;
2. read `/api/v1/operation` until the operation is terminal;
3. read `/api/v1/state` for authoritative settled state.

There is no SSE, WebSocket, webhook, long-polling, event-bus, ETag, or state
revision mechanism in v1.8.0.

## Deferred product work

The following are outside this release:

- Network Pak application/ROM throughput optimization;
- broader real-hardware soak coverage across more Pico, router, and N64
  combinations;
- Wi-Fi provisioning mutations through HTTP;
- remote controller streaming through HTTP;
- content upload/download APIs;
- a separate multi-device/headless NICE64 Server application;
- broader Android controller compatibility beyond validated devices.

The desktop Bridge remains a one-Pico-per-instance application. A future server,
if developed, will be a separate composition that may reuse the extracted
semantic services rather than turning the desktop Bridge into a multi-device
server.
