# NICE64 Bridge Application

## Current release candidate

Bridge v1.9.0 is the one-Pico-per-instance Windows desktop host for controller
input, firmware and accessory control, Pak content, Wi-Fi provisioning, Network
Pak services/modules, standalone RandNET Keyboard and N64 Mouse operation, and
the optional authenticated local API.

Run:

```text
bridge-application/nice64-bridge-v1.9.0/application/PicoSwitchController.exe
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

Keyboard and Mouse use the same verified firmware-mode service as the desktop
and API. Windows input is captured explicitly through foreground Raw Input,
with device identity retained for Any/Specific source selection. `Alt+Left`
releases capture and is not forwarded. Focus, device, source, mode, connection,
or application loss publishes neutral state. Invert X and Invert Y are
client-side pointer preferences; firmware does not implement user-preference
inversion.

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

The synchronized v1.9.0 Release build and applicable Stage F/G host regressions pass. Real
hardware testing covers RandNET Keyboard and N64 Mouse diagnostics, Android
direct N64 Mouse input, Mario Artist: Paint Studio, Pokémon Stadium 2 GB Tower
through Transfer Pak, Star Fox 64 controller input, and Network Pak workflows.
TCP remains the Network Pak reference carrier; the release does not claim every
historical transport-performance issue is solved.

## Android companion

Bridge v1.9.0 provides the authoritative HTTPS API, onboarding identity,
History, and controller-Primary boundaries used by the Android companion.
Management traffic uses the authenticated Bridge API. Under NICE Mobile
Primary, gameplay controller state travels directly from Android to NICE64/Pico
over UDP `LATEST_STATE`; it is not relayed by the Bridge HTTP host.

The accepted hybrid data plane also sends Android Keyboard and Mouse human
input directly to Pico using typed packets, while mode management, endpoint
authority, status, and keyboard LED reporting remain Bridge-mediated. Network
Pak remains a Bridge service/provider path.

See [Android companion](ANDROID_COMPANION.md) for installation, pairing,
controller profiles, touch controls, and Controller Blackout Mode.
