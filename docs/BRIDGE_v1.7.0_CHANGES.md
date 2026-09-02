# NICE64 Bridge v1.7.0

**Release status:** Release candidate. Current automated and available
real-hardware validation has passed for the documented workflows; final
comprehensive source-code review remains pending.

## Release summary

Bridge v1.7.0 completes the reusable single-device service/API boundary and the
accepted wireless-controller improvements while retaining the established
one-Pico-per-instance Windows desktop composition.

Release hashes:

```text
PicoSwitchController.exe
528FDFF7B5C2B124FA3C2AAD83B29C6B61507428A83E25A3CA946557573CB85E

PicoSwitchController.dll
A5F070D1E8E54CFCF3F529C8930050855FB4ECB5A4499C4E9242BF6DAABF403E
```

## Service and state architecture

- Reusable services own firmware, accessory, connection, controller,
  Controller Pak content, Transfer Pak content, Network Pak module, Wi-Fi
  provisioning, and Network Pak carrier semantics.
- `BridgeOperationCoordinator` provides one global owner for observable
  long-running semantic operations.
- `BridgeStateAggregator` captures authoritative device, controller, content,
  persistence, module, Wi-Fi, and operation state.
- API-safe v1 DTOs/projectors expose bounded values, safe errors, and filenames
  rather than internal objects or full paths.
- Desktop presentation converges from authoritative settled state after
  terminal API/background operations; observation does not reapply semantics.

## Local API v1

The optional Kestrel host is disabled by default, binds only to IPv4 loopback,
and requires bearer authentication for every `/api/v1` route. Its random token
is protected with Windows DPAPI for `CurrentUser`. Request body/header counts,
sizes, and timeouts are bounded; there is no wildcard CORS, Swagger, or
developer-exception leakage.

Public mutations are exactly:

```text
POST /api/v1/firmware
POST /api/v1/accessory
POST /api/v1/network-pak/module
POST /api/v1/network-pak/carrier
```

Remote carrier selection accepts only `tcp` and `reliable-udp-quick`. USB
carrier selection remains desktop/internal. The external observation contract
is `POST -> 202 + operationId -> GET /operation -> GET /state`.

See [Bridge API v1](BRIDGE_API.md).

## Wireless controller improvements

- Added the one-way `LATEST_STATE` UDP controller sender.
- Wireless delivery continuously reports the current authoritative controller
  state rather than relying only on one-shot input-change delivery. This avoids
  long held-state/dropout behavior and substantially improves practical
  responsiveness without claiming a fixed latency.
- Sender sessions use bounded ownership; delayed packets from a retired session
  cannot take authority back from the current session.
- Current-state packets remain complete and disposable: no ACK, retry window,
  event reconstruction, or backlog is added.
- Controller delivery guards distinguish line operations from the independent
  UDP state path so Pak content work does not unnecessarily stop UDP reporting.
- DirectInput calibrated analog composition no longer combines the same axes
  with conflicting N64 digital pseudo-stick bits. Buttons and D-pad remain
  independent.

### BR-003 — LATEST_STATE diagnostic log amplification

**Resolved.** Sustained UDP send failures no longer generate one forced
debug-log entry at the controller reporting cadence. Repeated failures are
coalesced into bounded periodic diagnostics, the first failure remains
immediately visible, and one recovery message is emitted when sending resumes.

Healthy `LATEST_STATE` transport behavior is unchanged. No wire-format,
firmware, sequence, session, cadence, retry, ACK, or current-state semantics
were changed.

## Network Pak

- Added a reusable carrier service with truthful selected-versus-active state.
- Integrated desktop USB/TCP/Quick physical adapters without changing frozen
  transport protocol parameters.
- API carrier selection uses the same semantic service as the desktop.
- Failed Quick activation remains observable as selected Quick with no active
  carrier; there is no silent TCP fallback.
- Network Pak browser and module selection remain supported.

## Transfer Pak fixes

- Full-ROM upload correlates `OK GBFP` acknowledgements with the exact expected
  page offset.
- The Bridge verifies the committed ROM with `GBFCRC` before completing the
  install.
- Fixed a Transfer Pak runtime issue where periodic Bridge-side
  save-persistence polling and background save reads could interfere with
  active Transfer Pak operation. Transfer Pak save synchronization is now
  event-driven and occurs at explicit lifecycle boundaries, allowing the Pico
  to own live Transfer Pak runtime operation without periodic Bridge traffic.
- Periodic Transfer Pak `STAT` polling, write-counter dirty detection, debounced
  autosave, and background `GBSDUMP` are disabled during gameplay.
- The current Game Boy save is flushed before accessory change, firmware change,
  ROM replacement, normal disconnect, and application close.
- Host save writes remain verified and atomic. A failed flush blocks destructive
  transitions where applicable and retains truthful association state.

Controller Pak persistence behavior remains unchanged.

## Validation observations

- Deterministic non-hardware regression: 31/31 PASS.
- Desktop Release build: 0 warnings / 0 errors.
- Hardware observations include Pokémon Gold and Pokémon Crystal Load Max
  success; Wario Land 2 running through patched Pokémon Stadium for about 30
  minutes over Wi-Fi; Pokémon Crystal loading with its retained save; continued
  Network Pak browser operation; and substantially improved wireless controller
  behavior.

These observations are not protocol or performance guarantees.

## Compatibility and known limitations

- Recommended pairing: Universal Firmware v1.2.0.
- The Bridge remains a Windows desktop application controlling one Pico.
- TCP remains the Network Pak reference carrier. This release does not claim
  every historical TCP reliability/performance issue is globally solved.
- The API has no Wi-Fi provisioning, controller streaming, Pak content,
  Connect/Disconnect, firmware upload, tuning, SSE, WebSocket, or long-poll
  endpoints.
- The current application can generate and load its DPAPI-protected API token,
  but has no public UI/CLI workflow to reveal or rotate the plaintext token.
- Forced process termination or device power loss before an event-driven save
  flush can prevent the newest Game Boy save from reaching the PC.
