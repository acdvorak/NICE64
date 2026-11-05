# NICE64 Bridge v1.8.0 release notes

Bridge v1.8.0 packages the accepted Bridge API and Android companion integration
while preserving the established single-device desktop application, accessory
protocols, controller behavior, and Network Pak application format.

## Highlights

- The Android companion uses the authenticated Bridge API for pairing, status,
  History, accessory management, and controller authority. Authority has exactly
  two values: Bridge Primary and NICE Mobile Primary.
- Under NICE Mobile Primary, Android physical or touch controller state travels
  directly to NICE64/Pico over one-way UDP `LATEST_STATE`; gameplay packets are
  not relayed through the Bridge.
- The companion includes controller profiles/mapping, embedded and full-screen
  touch controls, and Controller Blackout Mode. Blackout keeps the NICE64
  activity foregrounded on a true-black surface; it does not provide Android
  lock-screen or power-button-off controller operation.
- Reusable semantic services now own firmware, accessory, connection,
  controller, Controller Pak, Transfer Pak, Network Pak module, Wi-Fi, and
  Network Pak carrier behavior.
- `BridgeOperationCoordinator` provides one global long-operation owner and
  `BridgeStateAggregator` provides an authoritative read-only state capture.
- The optional API v1 host is disabled by default, binds only to IPv4 loopback,
  requires bearer authentication, and stores its token with Windows DPAPI for
  the current user.
- Public HTTP mutations cover firmware mode, accessory mode, Network Pak
  module, and Network Pak carrier selection. Remote carrier values are limited
  to TCP and Reliable UDP Quick; USB remains desktop/internal.
- Network Pak carrier selection is routed through a reusable semantic service
  shared by desktop and API paths.
- Wireless controller delivery uses one-way `LATEST_STATE` datagrams and
  continuously reports the current authoritative controller state. Session
  ownership hardening prevents a retired sender lifetime from regaining
  authority.
- Controller delivery guards distinguish line-command ownership from the
  independent UDP controller path, avoiding unnecessary suspension during Pak
  content operations.
- N64 DirectInput composition gives calibrated analog output authoritative
  stick ownership instead of combining it with conflicting pseudo-stick bits.
- Transfer Pak full-ROM upload verifies exact GBFP acknowledgement offsets and
  committed ROM CRC.
- Transfer Pak save synchronization is event-driven. Periodic `STAT` polling
  and background debounced `GBSDUMP` reads no longer run during gameplay; saves
  flush at accessory change, firmware change, ROM replacement, normal
  disconnect, and application close.

## Transfer Pak runtime correction

Fixed a Transfer Pak runtime issue where periodic Bridge-side save-persistence
polling and background save reads could interfere with active Transfer Pak
operation. Transfer Pak save synchronization is now event-driven and occurs at
explicit lifecycle boundaries, allowing the Pico to own live Transfer Pak
runtime operation without periodic Bridge traffic.

## Wireless controller correction

Wireless controller delivery now continuously reports the current authoritative
controller state using `LATEST_STATE` semantics instead of relying only on
one-shot input-change delivery. This prevents long held-state/dropout behavior
and substantially improves practical wireless controller responsiveness.

## Compatibility and limits

- The Bridge remains a one-Pico-per-instance Windows desktop application.
- TCP remains the Network Pak reference carrier; this release does not claim
  that every historical TCP or transport-performance issue is solved.
- The API is local-only and does not expose controller streaming, content
  upload/download, Wi-Fi provisioning, connect/disconnect, or transport tuning.
- Forced process termination or device power loss before a Transfer Pak
  lifecycle flush can prevent the newest Game Boy save from reaching the PC.

Bridge v1.8.0 remains compatible with NICE64 Universal Firmware v1.2.0; no
firmware, Joybus, wire-protocol, or transport-parameter change is included.

See the repository `docs/BRIDGE_v1.8.0_CHANGES.md`, `docs/BRIDGE_USER_GUIDE.md`,
`docs/BRIDGE_API.md`, and `android-application/README.md` for the complete public
release documentation.
