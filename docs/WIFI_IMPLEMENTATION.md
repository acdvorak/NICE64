# NICE64 Wi-Fi Implementation

## Platform and ownership

Wi-Fi targets Raspberry Pi Pico 2 W and uses CYW43 with lwIP in polled mode.
Initialization, polling, DHCP, TCP, UDP/RUDP, and Wi-Fi state all belong to
Core1. Core0 never calls CYW43/lwIP and never waits on network activity.

## Provisioning and recovery

Provisioning is initiated from the Bridge over USB. The Bridge sends the SSID
and password to the firmware, association is attempted on Core1, and status
reports include association state and the DHCP address. Credentials are stored
by Universal Firmware v1.2.0 in its reserved flash persistence area. Clearing
configuration removes them. USB remains the setup, fallback, and recovery path
if Wi-Fi or a network connection is unavailable.

Credentials are not stored in this repository or its distributed Bridge
package.

## TCP transport

The firmware line service listens on TCP port **6464**. The Bridge connects by
manual IPv4 address and verifies the device and active personality before using
it. One TCP client owns the session at a time. Existing line-oriented control
and accessory commands follow the selected USB or TCP line transport. Current
wireless controller reports use the separate `LATEST_STATE` UDP delivery class
when that delivery path is active. Network Pak TCP is retained as the explicit
reference/fallback.

There is no UDP discovery or static IPv4 provisioning in this release.

## Reliable UDP and Product Quick Mode

The firmware UDP service listens on port **6465**. The frozen RUDP-C carrier is:

| Parameter | Value |
|---|---|
| Wire version | 1 |
| Delivery class | `RELIABLE_ORDERED` / 0 |
| Logical window | 8 |
| Physical flight cap | 2 |
| Payload | 512 bytes |
| RTO | 40 ms |
| Retry budget | 4 retransmissions after the original |
| UDP port | 6465 |

It provides session isolation, ordered delivery, cumulative/bitmap
acknowledgements, bounded retransmission, explicit exhaustion, duplicate and
stale rejection, and bounded receiver staging. Each Bridge connection creates a
fresh cryptographically random nonzero session.

Product Quick Mode explicitly selects protected Network Pak application traffic
over this reliable carrier. TCP and RUDP use the same Network Pak service
boundary. If Quick Mode cannot establish or fails, the error remains visible;
the Bridge does not silently switch the operation to TCP. Reconnect creates a
fresh session and does not reuse failed-session reliability state.

The UDP service has two deliberately separate delivery classes:

| Class | Name | Current use |
|---:|---|---|
| 0 | `RELIABLE_ORDERED` | Network Pak Quick reliable ordered carrier |
| 1 | `LATEST_STATE` | Current wireless controller-state delivery |

`LATEST_STATE` carries complete disposable controller reports with
newest/current-state semantics. Normal updates have no per-update ACK, no
retransmission queue, and no history or backlog reconstruction. Session and
sequence ownership rules prevent a retired sender lifetime from regaining
authority while allowing a legitimate new sender session. It is not a Network
Pak carrier and does not share the reliable class-0 window.

## Validation and performance interpretation

RUDP-C has passed physical synthetic closeout, including bounded terminal
exhaustion, fresh-session recovery, ordered acceptance, pause/resume flow
control, and a 2 MiB soak. The Product Quick Mode adapter and firmware routing
pass builds, simulations, application parity, reconnect/session isolation, and
protected-source audits. Real N64 Network Pak Quick Mode hardware acceptance is
complete for normal operation: protected Network Pak transfers completed with
exact CRC and zero reported errors/timeouts. RUDP-C remains physically accepted
and frozen.

### Known Issues / Validation Notes

Normal Product Quick Mode operation has been validated on real N64 hardware.
Repeated Quick Mode reconnect/session renewal, TCP to Quick Mode handoff, Quick
Mode to TCP handoff, and repeated TCP/Quick switching have not received the same
level of validation. An intermittent connection-establishment timeout was
observed during development, but it was not conclusively reproduced and is not
confirmed as a persistent defect. If reconnect or transport switching fails,
reconnect or restart the Bridge/device, or temporarily use TCP. TCP remains the
supported reference transport. Further lifecycle validation and polish are
planned after release. This lifecycle note does not reopen or qualify the
accepted RUDP-C carrier.

RUDP carrier capability is not Network Pak or ROM end-to-end throughput.
Synthetic rates measure carrier headroom. Real Network Pak performance includes
Joybus and application scheduling and must be compared with actual USB/TCP
workloads. USB Network Pak throughput investigation after Wi-Fi integration is
recorded as deferred; it does not change the frozen RUDP tuple.

## Limitations

Device traffic is plaintext and intended for a trusted LAN or VPN. The release
has no TLS, internet-facing device service, UDP discovery, static address UI,
or generic Fast transport UI. USB is still required for first-time
provisioning, credential recovery, and the safest firmware-mode recovery path.
