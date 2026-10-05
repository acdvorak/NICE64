# NICE64 Bridge v1.8.0 User's Guide

## 1. What the Bridge does

The NICE64 Bridge is the Windows desktop application between a NICE64 Pico 2 W
and your controller, accessory files, Wi-Fi connection, and Network Pak
modules. One Bridge instance controls one Pico. It supports USB and Wi-Fi
control, controller mapping and calibration, Controller Pak images, Transfer
Pak GB/GBC content, Network Pak, and an optional local API.

## 2. Requirements

- NICE64 hardware with a Pico 2 W running Universal Firmware v1.2.0.
- A Windows PC. The packaged application targets .NET 9 for Windows and includes
  its managed dependencies; install the .NET 9 Desktop Runtime if Windows asks
  for it.
- A data-capable USB cable for initial setup, recovery, and USB use.
- For Wi-Fi features, a reachable 2.4 GHz network supported by the Pico 2 W.
- Legally obtained Controller Pak images and GB/GBC ROM/save files.

Do not power the Pico from conflicting sources. Follow the repository hardware
and wiring documentation.

## 3. Starting the Bridge

Run:

```text
bridge-application\nice64-bridge-v1.8.0\application\PicoSwitchController.exe
```

The title should report `PicoSwitchController v1.8.0`. Keep the entire
`application` folder together; its DLLs, assets, modules, and runtime files are
required.

## 4. Connecting over USB

1. Connect the NICE64 Pico 2 W to the PC with USB.
2. Start the Bridge.
3. Choose the USB/serial connection option and select the NICE64 COM port.
4. Wait for device identity and firmware mode verification.

USB is the recommended provisioning and recovery path. A Windows COM-port
failure is separate from the Wi-Fi path; reconnect the Pico and application if
the serial driver becomes unavailable.

## 5. Connecting over Wi-Fi

After provisioning, choose the TCP/Wi-Fi connection option and enter the Pico's
IPv4 address. The firmware control service uses TCP port 6464. The Bridge
verifies NICE64 identity and capabilities before accepting the session.

The Network Pak carrier is separate from the TCP control session. Changing a
Network Pak carrier does not transfer ownership of the control connection.

## 6. Wi-Fi provisioning

Provision Wi-Fi while connected over USB:

1. Open the Bridge Wi-Fi configuration workflow.
2. Enter the network SSID and password.
3. Ask the Pico to connect and wait for its link/IP status.
4. Record the assigned IPv4 address for later TCP connections.

Credentials are retained by firmware using its protected persistence format.
Do not disconnect or power off during a provisioning write.

## 7. Firmware modes

The unified image provides three personalities:

- **Controller** for controller input, no accessory, Controller Pak, Rumble Pak,
  and Transfer Pak.
- **Diagnostics** for bounded device diagnostics.
- **Network** for Network Pak operation.

The Bridge automatically requests required firmware changes during accessory
selection. A mode change can reboot the Pico and replace the USB or TCP control
session; wait for reconnection and verification to finish.

## 8. Accessory modes

Choose no accessory, Controller Pak, Rumble Pak, Transfer Pak, or Network Pak
from the accessory controls. The Bridge owns the semantic transition and shows
the final verified state. Do not unplug or terminate the application during a
content or persistence transition.

## 9. Controller input and mapping

The Bridge accepts on-screen/manual input and supported physical gamepads. The
effective digital buttons are the union of manual and physical contributions.
Open the mapping editor to bind buttons and digital directions for the selected
N64 or SNES profile.

N64 D-pad and analog stick are separate controls. In calibrated analog mode,
the analog axes own the N64 stick; the same axes are not simultaneously emitted
as conflicting digital pseudo-stick directions.

## 10. Analog-stick calibration

Use the calibration workflow for the selected DirectInput device:

1. Leave the stick centered when prompted.
2. Move it through its useful range.
3. Save the calibration for that device/profile.

Calibration files are user-specific runtime data and are not included in the
release. Recalibrate if the device identity, driver, or physical range changes.

## 11. Wireless controller mode

When the Bridge and Pico have a valid Wi-Fi path, controller state can use the
one-way `LATEST_STATE` UDP path. Bridge v1.8.0 repeatedly reports the current
authoritative state instead of sending only input changes. Packets are
disposable: there is no per-update ACK, retry queue, or reconstruction backlog.
A new sender session takes ownership, and retired sessions cannot regain it.

Use the debug log to confirm the selected controller delivery path when
diagnosing input. Network conditions can still affect wireless behavior; no
fixed latency guarantee is made.

## 12. Controller Pak

Controller Pak operations support creating, loading, and saving 32 KiB `.mpk`
images. The Bridge suspends conflicting line delivery while a content operation
owns the transport. Controller Pak retains its accepted write-counter,
dirty-state, debounce, and autosave behavior. Use normal disconnect or shutdown
so dirty content can be persisted safely.

## 13. Transfer Pak and GB/GBC ROMs

To install a game:

1. Connect to the Pico and select Transfer Pak.
2. Choose the full GB/GBC ROM installation command.
3. Select a supported `.gb` or `.gbc` file.
4. The Bridge prepares a 32 KiB save sidecar, uploads the ROM/save, commits the
   transaction, verifies the committed ROM CRC, and activates Transfer Pak.
5. Wait for completion before starting the N64 game that uses Transfer Pak.

The firmware validates the ROM length, 256-byte page ordering, exact duplicate
page replay, transaction completeness, and committed CRC. A replay with changed
data or an out-of-order page is rejected.

## 14. Game Boy save files

The associated `.sav` file is normally beside the ROM and is normalized to the
supported 32 KiB size. Short files are padded and oversized files are
normalized by the content service. Host writes use a temporary file,
verification, and atomic replacement so a failed write does not silently
replace known-good data.

### Event-driven Transfer Pak saves

Bridge v1.8.0 does not periodically poll `STAT` or perform background
`GBSDUMP` reads during Transfer Pak gameplay. The Pico owns live runtime
operation. The Bridge reads and persists the current save at these boundaries:

- changing away from Transfer Pak;
- changing firmware away from Controller mode;
- installing a new or different GB/GBC ROM;
- normal Bridge disconnect;
- normal application close.

A failed flush blocks a destructive transition where safe to do so. Application
shutdown retains bounded cleanup and records a failure in the debug log.

> **Important:** Forced process termination, PC/Pico power loss, or unplugging
> before a lifecycle flush may prevent the newest Game Boy save from reaching
> the PC. Exit or disconnect normally after play.

## 15. Network Pak

Select Network Pak to enter the required Network firmware personality and
activate the selected carrier. Network Pak modules provide application content
such as the browser and diagnostics. Module selection and active module state
are persisted through the Bridge service boundary.

### Browser usage

Choose the browser module, then use the N64 Network Pak interface to request
pages. The Bridge fetches and transforms supported content through the browser
module. Browser sessions/cache are runtime data and are not part of the release.
Internet sites vary, so not every page can be converted successfully.

### Carrier selection

- **USB** is available only from the desktop/internal carrier controls.
- **TCP** is the reference Network Pak carrier.
- **Reliable UDP Quick** uses UDP port 6465 and the frozen reliable-ordered
  transport parameters.

Selected intent and physically active carrier are distinct. A failed Quick
activation does not silently fall back to TCP; select TCP explicitly or retry
Quick after correcting the problem.

## 16. Optional local API

The API host is disabled by default and binds only to IPv4 loopback when
enabled. It requires a bearer token protected for the current Windows user. The
public HTTP mutations cover firmware, accessory, Network Pak module, TCP or
Reliable UDP Quick carrier selection, session-scoped controller Primary, and
session-scoped History activation. See [Bridge API v1](BRIDGE_API.md).

The API does not expose remote controller streaming, Connect/Disconnect,
Wi-Fi provisioning, Pak content transfer, firmware upload, or transport tuning.

### Android companion and controller authority

Bridge v1.8.0 supports the NICE64 Android companion through LocalNetwork HTTPS
and local QR onboarding. Controller authority has exactly two values: **Bridge
Primary** and **NICE Mobile Primary**. The Bridge remains authoritative.

Android management uses the authenticated Bridge API. Under NICE Mobile
Primary, Android physical/touch controller state travels directly to the Pico
over UDP `LATEST_STATE`; it is not streamed through HTTP. See
[Android companion](ANDROID_COMPANION.md).

## 17. Settings and debug logs

The Bridge stores `picoswitchcontroller.ini` beside the executable. It contains
desktop settings such as console/input choices, module/carrier selection, and
debug mode. Analog calibration data is also stored as user-specific runtime
data near the application. The API token is stored separately at:

```text
%LOCALAPPDATA%\NICE64\bridge-api-token.dat
```

When debug logging is enabled, use the Bridge debug-log command to inspect
connection, controller-delivery, persistence, and operation diagnostics. Logs
may contain local device/file information; review them before sharing. The
release package intentionally contains no logs or personal settings.

## 18. Troubleshooting

- **Bridge cannot see COM port:** reconnect USB, close other serial programs,
  and restart the Bridge/Pico.
- **Wi-Fi connection fails:** provision again over USB, verify 2.4 GHz network
  access, and confirm the DHCP IPv4 address.
- **Wrong firmware mode:** wait for reboot/reconnect verification; retry only
  after the previous operation is terminal.
- **Network Pak Quick fails:** confirm UDP 6465 is not blocked, then retry the
  same selection or explicitly select TCP. There is no automatic fallback.
- **Controller feels conflicted:** select the intended input source and redo
  analog calibration; avoid mapping the same physical analog axis as a digital
  stick direction.
- **Transfer Pak save not updated:** exit or disconnect normally and check for a
  `TRANSFER SAVE FLUSH SUCCESS` line. Do not overwrite the `.sav` after a
  reported persistence failure.
- **Stadium rejects Transfer Pak:** verify full install, commit, CRC, and
  `PAKMODE TRANSFER` completion; then confirm no periodic Transfer Pak `STAT`
  activity appears during gameplay.

See [Troubleshooting](TROUBLESHOOTING.md) and [Known Issues](KNOWN_ISSUES.md) for
additional project-specific notes.

## 19. Compatibility and legacy releases

Bridge v1.8.0 is packaged with Universal Firmware v1.2.0 and preserves the
established Bridge/firmware/accessory protocol contract. Use the matched current
pair for testing and support. Historical Bridge versions remain under
`bridge-application/Legacy`; historical firmware remains under
`firmware/Legacy`. Do not overwrite Legacy releases with current files.

## 20. Glossary

- **Bridge:** the Windows desktop application controlling one NICE64 Pico.
- **Firmware:** software flashed onto the Pico 2 W.
- **Controller Mode:** firmware personality for controller and normal accessory
  operation.
- **Standalone Mode:** Diagnostics or Network firmware personality.
- **Controller Pak:** N64 memory accessory represented by a 32 KiB `.mpk` file.
- **Transfer Pak:** N64 accessory that exposes installed GB/GBC ROM/save content.
- **Network Pak:** NICE64 network accessory and application protocol.
- **RUDP:** the reliable ordered UDP carrier used by Network Pak Quick.
- **LATEST_STATE:** disposable, sequence/session-protected UDP delivery for the
  newest complete controller state.
