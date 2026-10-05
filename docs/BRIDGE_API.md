# NICE64 Bridge v1.8.0 — Local API v1 Reference

## Document status

> **Release status:** This API is part of NICE64 Bridge v1.8.0. Its explicit v1
> routes and authoritative-state model are frozen for this release.

This document describes the HTTP API implemented in NICE64 Bridge v1.8.0. It is derived from the current host, route, facade, projection, token-store, operation, aggregate-state, session, History, controller-Primary, and carrier contracts. It documents the API as it exists, not a proposed future server or multi-device architecture.

The Bridge remains a one-Pico-per-instance Windows desktop application. The API is optional and disabled by default. It supports HTTP on IPv4 loopback and, when explicitly configured, authenticated HTTPS on exactly one selected LAN IPv4. Both listeners use the same semantic services and operation coordinator as the desktop UI.

## 1. API architecture at a glance

```text
Local API client
    |
    |  Authorization: Bearer <token>
    v
Explicit loopback HTTP / selected-LAN HTTPS Kestrel listeners
    |
    v
Explicit /api/v1 routes
    |
    v
BridgeApiCommandFacade
    |
    +--> one global BridgeOperationCoordinator (F8)
    |
    +--> frozen semantic service
             firmware / accessory / module / carrier
    |
    v
authoritative Bridge state
    |
    +--> GET /api/v1/operation
    +--> GET /api/v1/state
    +--> desktop terminal-presentation convergence
```

The API does not create a second semantic state machine. A command submitted through HTTP uses the same operation ownership, semantic service, authoritative state, and desktop convergence paths as an equivalent desktop action.

## 2. Enabling and locating the API

The API is disabled by default. Use the desktop API access settings to choose
one of three exact modes:

- **Off** — no API listener.
- **LocalOnly** — HTTP on `127.0.0.1` only.
- **LocalNetwork** — HTTP loopback plus HTTPS on exactly one validated selected
  LAN IPv4.

The default port is `6470`. LocalNetwork never uses a wildcard listener and
never falls back to plaintext LAN HTTP. If the selected interface, TLS identity,
or HTTPS bind is unavailable, the desired LocalNetwork setting remains visible
while runtime access degrades safely to loopback-only.

The Bridge owns one persistent self-signed ECDSA P-256 server identity protected
with DPAPI `CurrentUser`. Local onboarding exposes its non-secret SPKI pin and
certificate fingerprint together with the current HTTPS endpoint and bearer
credential in a transient QR presentation. The Android companion uses that
profile-scoped identity; normal public-PKI trust is not implied.

### Host limits

The embedded host is deliberately bounded:

| Limit | Value |
|---|---:|
| Maximum request body | 16 KiB |
| Maximum total request headers | 16 KiB |
| Maximum header count | 64 |
| Request headers timeout | 10 seconds |
| Keep-alive timeout | 30 seconds |
| Kestrel `Server` response header | Disabled |

## 3. Authentication and bearer-token handling

Every route under `/api/v1`, including the health route, requires a bearer token:

```http
Authorization: Bearer <token>
```

The Bridge creates a cryptographically random 32-byte token and stores it using Windows Data Protection API (DPAPI), scoped to `CurrentUser`. The protected token file is:

```text
%LOCALAPPDATA%\NICE64\bridge-api-token.dat
```

The DPAPI entropy label is versioned as:

```text
NICE64.Bridge.LocalApi.Token.v1
```

The encrypted file is not the bearer token and must not be copied into the HTTP header. Authentication hashes the supplied token and the expected token with SHA-256 and compares those hashes in fixed time.

### Local onboarding

The desktop provides an explicit local onboarding presentation for pairing the
Android companion. It reveals the current endpoint, server pin/fingerprint, and
bearer credential only during that local user action. No unauthenticated token,
onboarding, certificate, or settings route was added.

Do not log the bearer token, place it in source control, pass it in a URL, or expose it to another Windows user.

### Authentication failure

Missing or invalid authentication returns HTTP `401`, adds `WWW-Authenticate: Bearer`, and returns:

```json
{
  "code": "unauthorized",
  "detail": "A valid bearer token is required."
}
```

## 4. Media type and JSON conventions

Mutation requests require JSON:

```http
Content-Type: application/json
Authorization: Bearer <token>
```

Responses use compact, camel-case JSON. Unsupported request media types return HTTP `415`. Missing, malformed, or unreadable JSON returns HTTP `400` with an `invalid-request` error.

All documented command values are exact protocol strings. Values are trimmed, but command keywords are otherwise case-sensitive. For example, `controller` is valid while `Controller` is not.

## 5. Public endpoint summary

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/v1/health` | Authenticated host liveness check |
| `GET` | `/api/v1/operation` | Read the one current or most recently completed global operation |
| `GET` | `/api/v1/state` | Capture authoritative aggregate Bridge state |
| `GET` | `/api/v1/history` | Read API-safe Controller Pak/Transfer Pak History |
| `GET` | `/api/v1/sessions` | Read the current Bridge session collection |
| `GET` | `/api/v1/sessions/{sessionId}` | Read one current Bridge session |
| `GET` | `/api/v1/sessions/{sessionId}/controller/primary` | Read controller Primary and endpoint state |
| `PUT` | `/api/v1/sessions/{sessionId}/controller/primary` | Select Bridge Primary or NICE Mobile Primary |
| `POST` | `/api/v1/sessions/{sessionId}/history/{itemId}/activate` | Activate one authoritative History item |
| `POST` | `/api/v1/firmware` | Select firmware personality |
| `POST` | `/api/v1/accessory` | Select N64 accessory semantics |
| `POST` | `/api/v1/network-pak/module` | Select a registered Network Pak module |
| `POST` | `/api/v1/network-pak/carrier` | Select public TCP or Reliable UDP Quick carrier intent |

The public mutation surface is intentionally narrow. Section 12 lists operations that are not exposed.

## 6. Common asynchronous command contract

Every mutation is asynchronous and participates in the single global F8 operation coordinator.

### 6.1 Accepted response

An accepted command returns HTTP `202 Accepted` immediately:

```json
{
  "accepted": true,
  "operationId": 42,
  "operationKind": "firmware-mode-switch"
}
```

`accepted: true` means only that the operation was admitted and scheduled. It does not mean that the requested semantic transition succeeded.

### 6.2 Busy response

Only one global semantic operation may run at a time. If another operation is already running, the API returns HTTP `409 Conflict`:

```json
{
  "code": "operation-busy",
  "currentOperationId": 41,
  "currentOperationKind": "accessory-selection"
}
```

The rejected request does not queue behind the current operation. The client may wait for the reported operation to become terminal and explicitly submit its request again.

### 6.3 Required client workflow

```text
POST command
    |
    +--> 202 + operationId
             |
             v
       GET /api/v1/operation
       until the matching operation is terminal
             |
             v
       GET /api/v1/state
       and trust authoritative settled state
```

The correct interpretation rule is:

> The requested target and the terminal operation status do not, by themselves, prove the final device state. After the operation becomes terminal, read `/api/v1/state` and use its authoritative fields.

This matters because an operation can fail or be cancelled after part of a transition committed, or a service can reconcile to a truthful fallback different from the requested target.

### 6.4 Operation status values

The projected status is one of:

- `idle`
- `running`
- `succeeded`
- `failed`
- `cancelled`

All currently public mutation commands report cancellation capability as `before-commit-only`. No public HTTP cancellation endpoint currently exists, and API commands are dispatched without a caller cancellation token. The value documents the underlying semantic boundary rather than advertising a remote cancellation command.

## 7. Command reference

### 7.1 Select firmware personality

```http
POST /api/v1/firmware
```

Request:

```json
{
  "personality": "network"
}
```

Supported values:

| Value | Meaning |
|---|---|
| `controller` | Controller firmware personality |
| `diagnostics` | Diagnostics firmware personality |
| `network` | Network Pak firmware personality |

Operation kind:

```text
firmware-mode-switch
```

Semantic owner:

```text
IBridgeFirmwareService / BridgeFirmwareService
```

Invalid input returns HTTP `400`:

```json
{
  "code": "invalid-firmware",
  "detail": "A supported firmware personality is required."
}
```

After terminal completion, inspect at least:

```text
device.connected
device.firmwarePersonality
device.firmwareTransition
device.activeAccessory
device.accessoryTransition
```

A failed firmware operation may still have changed the connected control session or the device's observed personality. Do not assume the requested personality, prior UI state, or operation success flag is the final truth.

### 7.2 Select accessory

```http
POST /api/v1/accessory
```

Request:

```json
{
  "accessory": "network-pak"
}
```

Supported values:

| Value | Meaning |
|---|---|
| `none` | No active emulated accessory |
| `controller-pak` | Controller Pak |
| `rumble-pak` | Rumble Pak |
| `transfer-pak` | Transfer Pak |
| `network-pak` | Network Pak |

Operation kind:

```text
accessory-selection
```

Semantic owner:

```text
IBridgeAccessoryService / BridgeAccessoryService
```

Invalid input returns HTTP `400` with code `invalid-accessory`.

An accessory transition may require a firmware transition and control-session replacement. After terminal completion, read both firmware and accessory authority:

```text
device.firmwarePersonality
device.firmwareTransition
device.activeAccessory
device.accessoryTransition
```

If Network Pak becomes active, carrier selected-versus-active state may also be relevant.

### 7.3 Select Network Pak module

```http
POST /api/v1/network-pak/module
```

Request:

```json
{
  "moduleId": "diagnostics"
}
```

`moduleId` is a registered module identifier. It is trimmed, must not be empty, and must be no longer than 256 characters. Available module IDs should be discovered from:

```text
networkPakModule.availableModules
```

Operation kind:

```text
network-pak-module-selection
```

Semantic owner:

```text
IBridgeNetworkPakModuleService / BridgeNetworkPakModuleService
```

Invalid input returns HTTP `400` with code `invalid-network-pak-module`.

After terminal completion, inspect:

```text
networkPakModule.activeModuleId
networkPakModule.activeModuleName
networkPakModule.persistenceState
networkPakModule.lastSelectionOutcome
```

The active module can differ from the requested module if the service reconciles to a fallback. The authoritative `activeModuleId`, not the request or operation domain result, drives both API truth and terminal desktop presentation.

### 7.4 Select Network Pak carrier

```http
POST /api/v1/network-pak/carrier
```

TCP request:

```json
{
  "carrier": "tcp"
}
```

Reliable UDP Quick request:

```json
{
  "carrier": "reliable-udp-quick"
}
```

Publicly supported values:

| Value | Meaning |
|---|---|
| `tcp` | Existing Network Pak TCP line carrier |
| `reliable-udp-quick` | Existing Reliable UDP Quick carrier |

The API intentionally rejects aliases such as `quick`, `udp`, or `rudp`. It also rejects `usb` and `none` because neither is part of the approved public carrier-mutation surface.

Operation kind:

```text
network-pak-carrier-selection
```

Semantic owner and mandatory mutation path:

```text
IBridgeNetworkPakCarrierService.SelectCarrierAsync
```

Invalid input returns HTTP `400` with code `invalid-network-pak-carrier`.

After terminal completion, inspect:

```text
device.selectedNetworkPakTransport
device.activeNetworkPakTransport
device.connectionState
device.activeAccessory
```

Selected and active have different meanings:

- `selectedNetworkPakTransport` is the user's retained carrier intent.
- `activeNetworkPakTransport` is the carrier that is physically active now.

They are deliberately allowed to differ. For example, a failed Quick activation can truthfully leave selected intent as Reliable UDP Quick while active transport is none. The service does not silently fall back to TCP.

TCP activation can enter an accepted `ActivationPendingHandshake` lifecycle while the existing `NETPAK_HELLO_ACK|1` handshake is still converging. A successful operation therefore does not authorize a client to overwrite or infer the active field; `/state` remains authoritative.

USB carrier selection remains a desktop/internal function and is not accepted through HTTP.

## 8. Read endpoints

### 8.1 Health

```http
GET /api/v1/health
```

Successful authenticated response:

```json
{
  "apiVersion": "v1",
  "status": "alive"
}
```

This proves that the optional local host is running and authentication succeeded. It does not prove that a Pico is connected or ready.

### 8.2 Operation

```http
GET /api/v1/operation
```

Representative response:

```json
{
  "operationId": 42,
  "kind": "network-pak-carrier-selection",
  "status": "running",
  "startedUtc": "2026-08-28T18:30:00Z",
  "completedUtc": null,
  "cancellationCapability": "before-commit-only",
  "cancellationRequested": false,
  "domain": "network-pak-carrier",
  "failureCode": null,
  "safeDetail": null,
  "progress": {
    "phase": "activating",
    "completedUnits": 0,
    "totalUnits": 1,
    "itemIndex": null,
    "itemCount": null,
    "unit": "carrier",
    "timestampUtc": "2026-08-28T18:30:00Z"
  }
}
```

The route exposes the single current operation, or the most recently completed operation when idle. It is not an operation history and does not accept an operation ID in the URL. Operation IDs increase within a process, but they are not a durable cross-restart job ledger.

Always confirm that the returned `operationId` equals the ID accepted for your request. Another client or the desktop can begin a later operation after yours becomes terminal, causing the global projection to advance.

Failure information is deliberately safe and bounded. Internal exception messages, stack traces, service objects, and raw domain results are not serialized.

### 8.3 Aggregate state

```http
GET /api/v1/state
```

The response is an API-safe projection of the F9 aggregate capture:

```text
apiVersion
captureSequence
capturedUtc
device
controller
controllerPak
transferPak
persistence
networkPakModule
wifi
operation
```

Important device fields include:

```text
device.connected
device.connectionState
device.connectionKind
device.firmwarePersonality
device.deviceRole
device.deviceService
device.firmwareTransition
device.activeAccessory
device.accessoryTransition
device.selectedNetworkPakTransport
device.activeNetworkPakTransport
device.effectiveCapabilities
```

The aggregate also projects controller delivery state, Controller Pak and Transfer Pak content state, safe associated filenames, persistence status, Network Pak module state, Wi-Fi state, and the current operation.

Security-sensitive data is intentionally reduced:

- Full filesystem paths are projected as filenames only.
- Only explicit supported capability fields are exposed.
- Text is bounded and sanitized.
- Internal exceptions and developer diagnostics are mapped to safe failure codes/details.
- Service instances and internal domain result objects are never serialized.

`captureSequence` orders aggregate captures. It is not an entity revision, ETag, optimistic-concurrency token, or durable event sequence.

## 9. PowerShell usage examples

The following examples assume an authorized provisioning path has supplied the plaintext token:

```powershell
$baseUri = "http://127.0.0.1:6470"
$token = "<provisioned bearer token>"
$headers = @{ Authorization = "Bearer $token" }
```

Health check:

```powershell
Invoke-RestMethod `
    -Method Get `
    -Uri "$baseUri/api/v1/health" `
    -Headers $headers
```

Submit a carrier selection:

```powershell
$body = @{ carrier = "reliable-udp-quick" } | ConvertTo-Json -Compress

$accepted = Invoke-RestMethod `
    -Method Post `
    -Uri "$baseUri/api/v1/network-pak/carrier" `
    -Headers $headers `
    -ContentType "application/json" `
    -Body $body

$accepted
```

Poll the matching operation and then capture authoritative state:

```powershell
do {
    Start-Sleep -Milliseconds 500
    $operation = Invoke-RestMethod `
        -Method Get `
        -Uri "$baseUri/api/v1/operation" `
        -Headers $headers

    if ($operation.operationId -ne $accepted.operationId) {
        throw "The global operation view advanced to a different operation."
    }
} while ($operation.status -eq "running")

$state = Invoke-RestMethod `
    -Method Get `
    -Uri "$baseUri/api/v1/state" `
    -Headers $headers

[pscustomobject]@{
    OperationStatus = $operation.status
    SelectedCarrier = $state.device.selectedNetworkPakTransport
    ActiveCarrier   = $state.device.activeNetworkPakTransport
    Accessory       = $state.device.activeAccessory
}
```

The 500 ms polling interval above is an example client policy, not a protocol requirement. The API provides no event stream or long-poll contract.

## 10. curl examples

```bash
curl -H "Authorization: Bearer $TOKEN" \
  http://127.0.0.1:6470/api/v1/health
```

```bash
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"personality":"network"}' \
  http://127.0.0.1:6470/api/v1/firmware
```

```bash
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"accessory":"network-pak"}' \
  http://127.0.0.1:6470/api/v1/accessory
```

```bash
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"moduleId":"diagnostics"}' \
  http://127.0.0.1:6470/api/v1/network-pak/module
```

```bash
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"carrier":"tcp"}' \
  http://127.0.0.1:6470/api/v1/network-pak/carrier
```

## 11. HTTP and semantic error model

| HTTP status | Meaning | Typical code |
|---:|---|---|
| `200` | Authenticated read completed | N/A |
| `202` | Mutation admitted; operation still must be observed | N/A |
| `400` | Invalid JSON or unsupported command value | `invalid-request`, `invalid-firmware`, `invalid-accessory`, `invalid-network-pak-module`, `invalid-network-pak-carrier` |
| `401` | Missing or invalid bearer token | `unauthorized` |
| `404` | Route does not exist | Host-generated/not-found response |
| `405` | Route exists for another method | Host-generated/method response |
| `409` | Another global operation is running | `operation-busy` |
| `415` | Mutation did not use an accepted JSON media type | `unsupported-media-type` |
| `500` | Request processing encountered an unexpected internal failure | `internal-error` |

An HTTP `202` can later lead to operation status `failed` or `cancelled`. Conversely, a failed operation can leave some authoritative state changed. HTTP status describes admission and request handling; the operation projection describes execution; the state projection describes final semantic truth.

Safe operation failures can be represented with bounded codes such as `cancelled`, `invalid-request`, `bridge-unavailable`, `persistence-failure`, `timeout`, or `operation-failed`, depending on the underlying semantic result. Clients should not parse human-readable `safeDetail` to determine behavior.

## 12. Deliberately unsupported HTTP commands

The following are not public `/api/v1` mutation routes:

- Connect or disconnect.
- USB Network Pak carrier selection.
- Wi-Fi scanning, provisioning, credential management, or connection commands.
- Controller input injection or streaming.
- Controller Pak creation, load, save, upload, or download.
- Transfer Pak ROM/save installation, upload, or download.
- Firmware image upload.
- Transport parameter or RUDP tuning.
- Operation cancellation.
- API token display, rotation, or provisioning.
- Arbitrary service invocation.

Some capabilities, such as connection and disconnection, have internal semantic-facade methods. Their presence in application code does not make them HTTP endpoints.

## 13. Observation model and client responsibilities

The external v1 observation contract remains simple polling:

```text
POST mutation
    -> 202 + operationId
GET /api/v1/operation
    -> wait for matching terminal status
GET /api/v1/state
    -> consume authoritative settled state
```

The API does not implement:

- Server-Sent Events (SSE).
- WebSockets or SignalR.
- WebHooks.
- An event bus exposed to clients.
- Long polling.
- State revision or ETag handling.
- `Last-Modified` or `If-None-Match` semantics.
- Per-operation history lookup.

A robust client should therefore:

1. Serialize its own mutations where practical.
2. Treat HTTP `409` as a prompt to observe and retry, not as an implicit queue.
3. Retain the accepted `operationId` and verify it while polling.
4. Treat `succeeded`, `failed`, and `cancelled` as terminal.
5. Fetch `/state` after every terminal operation, including failure and cancellation.
6. Use selected-versus-active carrier fields independently.
7. Discover available Network Pak module IDs from state rather than hard-coding an undocumented list.
8. Avoid using `captureSequence` as a revision or concurrency token.

## 14. Desktop convergence behavior

Commands initiated through the API are expected to converge the WinForms presentation after their F8 operation becomes terminal. The desktop observes terminal transitions for the approved mutation kinds, reads authoritative settled state, marshals a bounded refresh onto the UI thread, and updates presentation-only caches and controls.

This observation path is read-only. It does not reapply the requested semantic action and does not infer final state from the HTTP request, operation success, operation failure, or internal domain result.

For carrier selection specifically, terminal desktop checkmarks are derived from authoritative carrier state. The HTTP route does not bypass `IBridgeNetworkPakCarrierService.SelectCarrierAsync`, and there is no competing Form-owned semantic carrier state machine.

## 15. Security boundary

The frozen local API security posture is:

- Optional and disabled by default.
- HTTP on IPv4 loopback; optional HTTPS on exactly one selected LAN IPv4.
- Bearer authentication for every `/api/v1` route.
- Random token protected with DPAPI `CurrentUser`.
- Bounded Kestrel request and timeout limits.
- No wildcard CORS policy.
- No Swagger/OpenAPI UI.
- No developer exception details in responses.
- No full local paths in aggregate state.
- No wildcard listener, plaintext LAN fallback, or automatic interface migration.

TLS does not replace bearer authentication. Pair only on a trusted LAN and keep
the onboarding payload and bearer credential secret. The API does not bind to
all interfaces, install a trusted root, alter Windows Firewall, or expose a
public/internet listener.

## 16. Frozen transport behavior

The carrier mutation changes semantic carrier selection only. It does not expose or modify physical transport tuning. The frozen Reliable UDP Quick parameters remain:

| Parameter | Frozen value |
|---|---:|
| UDP port | 6465 |
| RUDP window | 8 |
| Physical flight cap | 2 |
| Payload | 512 bytes |
| Retransmission timeout | 40 ms |
| Retries | 4 |

The API does not imply that earlier TCP performance concerns are globally solved. Performance measurement and UDP/RUDP implementation or optimization work remain separate future scope.

## 17. Source map

The principal implementation files are located under:

`bridge-application/nice64-bridge-v1.8.0/source/` in the repository.

| Concern | Source file |
|---|---|
| HTTP host, authentication middleware, and limits | `BridgeApiHost.cs` |
| Explicit v1 routes and request validation | `BridgeApiV1CommandRoutes.cs` |
| Operation admission and semantic service dispatch | `BridgeApiCommandFacade.cs` |
| API-safe state and operation projection | `BridgeApiV1Projection.cs` |
| DPAPI token storage | `BridgeApiProtectedTokenStore.cs` |
| Global operation contracts/coordinator | `BridgeOperationState.cs` |
| Desktop host startup and presentation convergence | `Form1.cs` |

## 18. Compact command checklist

Before sending a command:

- Confirm the API was enabled before the desktop process started.
- Use `http://127.0.0.1:<port>`.
- Supply the provisioned bearer token.
- Send `Content-Type: application/json`.
- Use the exact lower-case protocol value.

After receiving `202`:

- Store `operationId`.
- Poll `/api/v1/operation` for that exact ID.
- Wait for `succeeded`, `failed`, or `cancelled`.
- Fetch `/api/v1/state` even when the operation failed or was cancelled.
- Treat authoritative settled state—not the requested target—as the result.

---

**Release context:** Bridge v1.8.0 packages the accepted service/API boundary,
explicit controller Primary, History/session routes, LocalNetwork HTTPS, and
Android companion integration. Controller gameplay remains direct Android-to-
Pico UDP `LATEST_STATE`; it is not an HTTP streaming route.
