# NICE64 Legacy Android Controller Lease Ownership

## Status

This document records the historical renewable Android controller-ownership architecture as it
existed immediately before the explicit Primary migration on 2026-09-22. It is retired from the
active design, retained for possible future use, and is not the intended controller-authority
architecture after that migration.

## Why It Existed

The lease provided bounded external ownership and automatic failback to Bridge-local controller
delivery. Android was permitted to send controller state directly to the Pico only after the
Bridge authenticated the request, validated the active Bridge session and controller endpoint,
and granted a short-lived capability. If Android stopped renewing, the Bridge automatically
restored its local controller path. The Bridge remained the management and semantic authority;
Android was only the temporarily selected controller-input source.

## Architecture

The control plane was:

```text
Android
  -> authenticated Bridge HTTPS API
  -> controller ownership acquire
  -> Bridge session and controller endpoint validation
  -> lease ID/capability returned
  -> Android starts direct UDP LATEST_STATE
  -> Pico controller receiver
```

The maintenance path was:

```text
Android controller GET
  -> ownership renew POST
  -> BridgeControllerOwnershipService.RenewAsync
  -> BridgeControllerEndpointProvider.ResolveCurrentAsync
  -> USB fresh Wi-Fi status / WIFI_STATUS? when applicable
  -> lease deadline extended
```

The Bridge suspended its own controller delivery after a successful acquire. Release, revocation,
session invalidation, or expiry restored Bridge-local delivery.

## Timing

- Bridge lease lifetime: exactly `5000` milliseconds
  (`BridgeControllerOwnershipService.LeaseDurationMilliseconds`).
- Android renewal delay: exactly `2000` milliseconds
  (`BridgeControllerOwnershipCoordinator.RENEW_CADENCE_MS`).
- Android performed the controller GET and renewal POST serially after each delay, so the observed
  cycle also included HTTPS, endpoint-resolution, serial-query, and presentation latency.
- Physical presentation captures settled near 2.13-2.18 seconds per cycle; the accepted diagnostic
  reported a 2.218-second median when early capture disturbance was included.

## Android Ownership State Machine

The active source states were:

- `UNOWNED`
- `ACQUIRING`
- `OWNED`
- `RENEWING`
- `RELEASING`
- `LOST`
- `UNCERTAIN`

Only `OWNED` and `RENEWING` with a non-empty lease ID and an unexpired local monotonic deadline
were usable authority. Each two-second cycle published `RENEWING` and then `OWNED`, even though the
data plane could remain active. Rejected or ambiguous renewal removed locally usable authority;
Android did not fabricate continued ownership.

## Bridge Ownership State

The Bridge bound ownership to the exact active Bridge session ID and a generated lease
ID/capability. Its in-memory snapshot included the owner, lease identity, UTC expiration,
five-second duration, local-delivery suspension, and last typed failure. Acquire validated the
session and endpoint before suspending local delivery. Renew revalidated both before extending the
deadline. Release revoked the lease explicitly. A monotonic expiry task revoked an unrenewed lease
and resumed Bridge-local delivery with the current state resent when the Bridge remained eligible.
Bridge restart did not preserve external ownership.

## UDP Data Plane

After acquire, Android sent one-way 32-byte `LATEST_STATE` datagrams directly to the Pico at a
target of approximately 120 reports per second. Delivery was newest-state-only: there was no ACK,
retry, reconstruction, or reliable window. Each sender lifetime used a fresh nonzero session ID
and increasing 32-bit sequence. The Pico accepted newer state, handled sequence wrap, and retained
a bounded retired-session set so a delayed old sender lifetime could not regain authority.

The Android sender used generation ownership to prevent an old socket/loop from reviving. It sent
neutral at generation start and, during a clean and still-authorized stop, sent a bounded neutral
tail before closing. Ambiguous authority loss stopped without claiming a safe neutral delivery.

## Endpoint Validation

For Wi-Fi TCP control, the Bridge used the verified control-session IPv4 authority. For USB
control, acquire and renew could perform a fresh Pico Wi-Fi status read and issue `WIFI_STATUS?`
over the serial management channel. This existed to detect silent Pico Wi-Fi loss or address drift
and to preserve fail-closed endpoint authority before extending Android controller authority.

## Protected Operation Interaction

Transfer Pak and History content work can protect line traffic while allowing direct controller
UDP. During such an operation, fresh USB endpoint resolution may return `Protected` without
issuing `WIFI_STATUS?`. The Bridge can retain the existing bounded lease until its prior deadline,
while Android can treat the rejected renewal as loss of locally usable authority and stop its
sender. This is a known lifecycle edge. It is **not proven** to cause the intermittent Transfer Pak
controller error, whose accepted discovery classification remains `I — NOT PROVEN`.

## Lifecycle Behavior

The original D8K policy treated Activity `ON_STOP` as genuine background/loss: it cleared semantic
input, cancelled renewal, performed a clean sender stop where authority remained certain, and
attempted lease release. Foreground return did not reacquire automatically. This correctly handled
screen lock/background but also ran during configuration recreation, so rotation unnecessarily
released otherwise valid authority.

The later `nice64-android-rotation1` candidate separated configuration recreation from genuine
backgrounding. Rotation cleared transient touch contribution while retaining the ViewModel-owned
lease, sender, session, and physical-controller state. Genuine background/screen lock continued to
use the original release behavior in that candidate.

## Advantages

- Automatic bounded failback to Bridge-local controller input.
- No indefinite stale external ownership after Android disappears.
- Exact session/capability binding and fail-closed ambiguous outcomes.
- Fresh endpoint validation capable of detecting silent Pico Wi-Fi loss/address drift.
- Direct low-latency Android-to-Pico data plane without routing controller reports through HTTP.

## Disadvantages / Complexity

- Recurring HTTPS ownership traffic and USB endpoint revalidation.
- Possible recurring `WIFI_STATUS?` management traffic.
- Visible ownership state churn during normal renewal.
- Lease timer, renewal job, deadline, capability, revocation, and expiry races.
- Activity/display lifecycle coupling in the original policy.
- Protected-operation interaction during long History/Transfer Pak work.
- More difficult correlation and debugging across Android, Bridge, serial management, and UDP.
- Poor fit for screen-off physical USB/Bluetooth controller-hub use without a separate long-lived
  Android runtime.

## Why Retired

The intended product usage no longer requires automatic five-second failback. Android touch is
primarily a debug/setup/fallback input, while physical USB/Bluetooth controllers connected to
Android are the intended main mobile use. Screen state should not define controller authority, and
manual recovery at the Bridge desktop is acceptable if the mobile controller hub disappears.

The replacement is an explicit Bridge-authoritative choice between **Bridge Primary** and
**NICE Mobile Primary**, with no lease, renewal heartbeat, automatic failback, or liveness
watchdog. This also better matches a future headless Bridge/server composition where an external
application is the normal control surface.

## Possible Future Use

The archived lease design may still be appropriate if NICE64 later requires unattended automatic
failback, remote multi-client arbitration, temporary controller grants, or autonomous recovery in
deployments where manual desktop access is unavailable.

## Restoration Reference

The complete pre-migration source, binaries, APKs, and manifest are preserved at:

`D:\NICE64\history-v1\preserved-baselines\PRE_EXPLICIT_PRIMARY_LEASE_SYSTEM_2026-09-22`

Bridge restoration source:

`...\bridge\nice64-bridge-v1.7.0`

Android baseline restoration source:

`...\android\nice64-android-baseline`

Rotation-candidate restoration source:

`...\android\nice64-android-rotation1`

## Known Reports

- `PHONE_OWNERSHIP_TRANSFER_PAK_DIAGNOSTIC_REPORT.md`
- `PHONE_OWNED_TRANSFER_PAK_INTERMITTENT_DISCOVERY_REPORT.md`
- `ANDROID_OWNERSHIP_LIFECYCLE_DISCOVERY_REPORT.md`
- `ANDROID_OWNERSHIP_ROTATION_STABILIZATION_REPORT.md` when present

These reports document measured renewal presentation, USB endpoint revalidation, lifecycle
behavior, rotation findings, and the still-unproven Transfer Pak correlation. They must not be read
as proof that the lease architecture caused the Transfer Pak failure.
