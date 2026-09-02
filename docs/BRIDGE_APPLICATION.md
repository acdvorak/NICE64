# NICE64 Bridge Application

## Current release

Bridge v1.7.0 is the one-Pico-per-instance Windows desktop host for controller
input, firmware and accessory control, Pak content, Wi-Fi provisioning, Network
Pak services/modules, and the optional authenticated local API.

Run:

```text
bridge-application/nice64-bridge-v1.7.0/application/PicoSwitchController.exe
```

For practical setup and operation, see the [Bridge User's Guide](BRIDGE_USER_GUIDE.md).
For HTTP integration, see the [Bridge API v1 Reference](BRIDGE_API.md).

## Architecture summary

Semantic services own firmware, accessory, connection, controller, Controller
Pak, Transfer Pak, Network Pak module, Wi-Fi, and carrier behavior. One
`BridgeOperationCoordinator` serializes observable long operations and
`BridgeStateAggregator` captures authoritative read-only state. The desktop and
API share these boundaries; the Form remains responsible for physical adapter
composition and presentation rather than competing semantic state machines.

USB and TCP provide line/control transport. Network Pak supports explicit TCP
or Reliable UDP Quick carrier selection, with USB retained as a desktop/internal
option. There is no silent Quick-to-TCP fallback. Wireless controller delivery
can use one-way `LATEST_STATE` current-state reporting independently of the
Network Pak reliable-ordered carrier.

Controller Pak retains its accepted persistence lifecycle. Transfer Pak save
persistence is event-driven: no periodic `STAT` or background `GBSDUMP` runs
during gameplay, and the current save is flushed at explicit lifecycle
boundaries.

## Local data

Settings and user state are created at runtime and are intentionally excluded
from the release. `picoswitchcontroller.ini` and analog calibrations live near
the executable. The DPAPI-protected API token is stored beneath
`%LOCALAPPDATA%\NICE64`. Debug logs, browser sessions/cache, ROMs, saves, and Pak
images should not be committed or redistributed unintentionally.

## Status and limitations

The v1.7.0 Release build completes with zero warnings and errors and the
established non-hardware matrix passes 31/31. Available hardware testing covers
normal Transfer Pak, Network Pak browser, and wireless controller workflows.
TCP remains the Network Pak reference carrier; the release does not claim every
historical transport-performance issue is solved.
