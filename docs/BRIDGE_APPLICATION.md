# NICE64 Bridge Application

## Architecture

Bridge v1.6.0 is the Windows host for controller input, firmware control,
accessory file operations, Network Pak services, and optional Browser/Portal
modules. It supports a serial USB line transport and a manually addressed TCP
line transport. Wi-Fi provisioning is deliberately performed over USB.

The application verifies NICE64 identity, personality, and capabilities when
connecting. Controller polling writes state through the selected line
transport. Firmware personality and accessory UI are kept consistent, and mode
changes are verified after reboot/reconnect. Settings, selected Network Pak
module, input source, console profile, analog calibration, and Network Pak
transport choice are persisted locally.

Controller input supports the existing DirectInput/XInput profiles, mapping
files, 8BitDo/Parsec profile, and per-device analog calibration. Normal
accessory choices are no accessory, Controller Pak, and Transfer Pak.

Controller Pak operations create, load, and save 32 KiB `.mpk` images over the
selected USB or TCP line transport. The current Bridge performs a best-effort
save on disconnect/application close when a file is active. Transfer Pak can
load a full supported GB/GBC ROM and its adjacent `.sav`, and performs
best-effort save retrieval before ROM replacement or disconnect. These current
operations are distinct from a future event/dirty-state synchronization system.

Network mode hosts the Network Pak Bridge service and routes requests to the
selected module. Included modules cover Browser Live, Browser Test,
Diagnostics, Network Portal, Speed Test, and an example module. External
modules are trusted local code. TCP is the reference Network Pak transport;
Product Quick Mode explicitly selects the frozen reliable-ordered UDP carrier.
There is no silent fallback from Quick Mode to TCP.

Debug mode writes local diagnostic logs when enabled. Runtime logs, settings,
Browser sessions, ROMs, saves, and Pak images are intentionally excluded from
the repository release.

## User guide

1. Start `bridge-application/nice64-bridge-v1.6.0/application/PicoSwitchController.exe`.
2. Connect NICE64 by USB, select its COM port, and connect. If several serial
   devices exist, use the port that responds as NICE64.
3. To configure Wi-Fi, open the Wi-Fi provisioning dialog while connected over
   USB, enter the network credentials, provision, and note the reported DHCP
   address.
4. Choose Controller firmware for normal controller/accessory use. Select No
   Accessory, Controller Pak, or Transfer Pak as required.
5. For Network Pak, select the Network firmware personality, reconnect after
   its reboot, and select the desired Network Pak module.
6. To use Wi-Fi, choose the TCP connection type and enter the NICE64 IPv4
   address. TCP uses port 6464. Normal controller state also follows this
   selected connection.
7. For Network Pak transport, choose **TCP** for the reference path or **Quick
   Mode (Reliable UDP)** for RUDP on port 6465. A Quick Mode failure is reported
   instead of being hidden by a TCP fallback.
8. For Transfer Pak, use the full GB/GBC ROM load command and select the ROM.
   The Bridge uses an adjacent `.sav` file, creating one when appropriate.
9. For Controller Pak, create or load a 32 KiB `.mpk`, then use Save when an
   immediate PC-side copy is required.
10. Configure mapping/input source under controller settings and run analog
    calibration for the active device. Calibration is stored per device.
11. To return to ordinary play, select Controller firmware, reconnect after the
    reboot, and choose the desired normal accessory.

If a connection fails, return to USB, verify the COM port and firmware
personality, reprovision or clear Wi-Fi configuration, and then retry the
reported DHCP address. A reboot or mode switch closes network sessions. Only
one TCP/RUDP owner is allowed. USB remains necessary for provisioning and is the
recommended recovery path even when normal operation is untethered.

## Current release status

Bridge v1.6.0 and Product Quick Mode pass Release builds, host regressions, and
available real-N64 validation. Protected Network Pak transfers completed with
exact CRC and zero reported errors/timeouts. RUDP-C remains physically accepted
and frozen.

### Known Issues / Validation Notes

Normal Quick Mode operation is hardware-validated. Repeated reconnect/session
renewal and TCP↔Quick Mode handoff have not received equally exhaustive
validation. An intermittent connection-establishment timeout was observed once
during development but has not been confirmed as a persistent defect. If a
reconnection or transport switch fails, reconnect or restart the Bridge/device,
or temporarily select TCP. TCP remains the supported reference transport.
Further lifecycle validation and polish are planned for a future release.

## Future Direction

The following are planned concepts, not current features: a consolidated set of
Bridge runtime mode/transport flags; History/Favorites for GB ROM and `.mpk`
selection; a documented Bridge API; an Android remote/controller client; a
headless standalone Bridge Server; Wi-Fi-originated firmware mode changing; and
a stronger dirty-state/session-event GB-save and Controller Pak autosave and
persistence lifecycle. Current best-effort file saves do not imply that future
synchronization architecture already exists.
