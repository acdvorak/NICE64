# Changelog

## Universal Firmware v1.3.0 RC / Bridge v1.9.0 / Android Companion v1.2.0

- Built and locally packaged Android Companion v1.2.0 (`versionCode` 2) as a
  debug-signed APK after 338/338 JVM tests and lint passed.
- Built and locally packaged Bridge v1.9.0 with unchanged runtime behavior and
  API v1 contracts.
- Built Universal Firmware v1.3.0 for Pico 2 W from the accepted G2 functional
  source with version-only declarations. Build, flash-boundary,
  embedded-version, and source-lineage checks passed.
- Firmware v1.3.0 is a **hardware-validated release candidate**. Extended
  real-hardware use includes N64 Mouse with Mario Studio on 64DD and RandNET
  Keyboard with a Windows CE port/side project. The preserved v1.2.0 G2 UF2
  remains the prior hardware-validated reference.
- NICE Browser remains v1.4.1.

## Universal Firmware v1.2.0 / Bridge v1.8.0 / NICE Browser v1.4.1 / Android companion

- Added hardware-validated standalone RandNET Keyboard and N64 Mouse
  personalities and their libdragon diagnostic ROM/source.
- Documented the existing unified-firmware recovery behavior: personality
  requests are volatile one-shot watchdog-reboot selections, and a cold boot
  after power removal defaults to Controller. This behavior is unchanged from
  the retained v1.1.0 unified firmware onward.
- Added typed direct Android-to-Pico Keyboard and cumulative Mouse input while
  retaining Bridge-mediated management, verification, endpoint authority, and
  keyboard LED reporting. Network Pak remains Bridge-mediated.
- Added explicit foreground Windows Raw Input capture with Any/Specific source
  selection, `Alt+Left` release, and fail-neutral lifecycle handling.
- Added persistent client-side Invert X/Invert Y settings. Android normalizes
  its down-positive pointer convention before inversion; Windows retains its
  physically validated native convention. Firmware performs no preference
  inversion.
- Added the accepted Android companion export with pinned HTTPS pairing,
  Bridge/History/Controllers navigation, authoritative operation state, and
  exactly two controller Primary values: Bridge Primary and NICE Mobile Primary.
- Added Android USB/Bluetooth controller profiles and mapping, embedded and
  full-screen touch controls, Accessories and Modes, and Advanced diagnostics.
- Added Controller Blackout Mode, which keeps the NICE64 activity foregrounded
  on a true-black OLED-friendly surface without claiming lock-screen or
  power-button-off controller operation.
- Preserved direct Android-to-Pico UDP `LATEST_STATE` controller delivery;
  gameplay packets are not relayed through the Bridge HTTP API.
- Included accepted Home/return sender reconciliation and mobile UI,
  accessibility, lifecycle, and failure-recovery corrections.
- Updated Bridge release identity and package metadata to v1.8.0 with no
  firmware, Joybus, accessory protocol, Network Pak protocol, or transport-
  parameter change.
- Promoted NICE Browser v1.4.1 from the confirmed working Summercart v1.4.0
  staged-bulk/carrier-response-gate lineage. The promotion changes Browser
  release identity only and introduces no new protocol or functional behavior.
- Universal Firmware remains v1.2.0. The current artifact is the
  hardware-validated G2 build; the later mouse-orientation correction changed
  Android and Windows clients only.

See [Bridge v1.8.0 changes](BRIDGE_v1.8.0_CHANGES.md) and
[Android companion](ANDROID_COMPANION.md).

## Universal Firmware v1.2.0 / Bridge v1.7.0 — Final Release

- Promoted the hardened unified Pico 2 W firmware with `LATEST_STATE`
  controller receive, Transfer Pak flash safety, exact previous-page replay
  idempotency, GBFCRC verification, and the 1 MiB partition guard.
- Completed the Bridge single-device service/state/operation architecture and
  optional authenticated loopback API v1.
- Added reusable Network Pak carrier ownership and public TCP/Reliable UDP
  Quick mutation while keeping USB desktop/internal.
- Added continuous authoritative current-state wireless controller reporting,
  session ownership hardening, controller guard separation, and calibrated
  analog stick ownership.
- Fixed BR-003: sustained `LATEST_STATE` UDP send failures now use bounded
  diagnostic coalescing instead of generating one forced log entry per report.
- Fixed Transfer Pak runtime interference by replacing periodic `STAT` polling
  and background `GBSDUMP` autosave with explicit lifecycle save flushes.
- Preserved Joybus/Core0 timing, accessory protocols, Network Pak application
  framing, and frozen RUDP wire/parameter compatibility.

See [Firmware v1.2.0 changes](FIRMWARE_v1.2.0_CHANGES.md) and
[Bridge v1.7.0 changes](BRIDGE_v1.7.0_CHANGES.md).

## Universal Firmware v1.1.0 / Bridge v1.6.0 release candidate

- Added the unified Controller, Diagnostics, and Network firmware personalities.
- Added Pico 2 W Wi-Fi provisioning, persistent credentials, DHCP reporting,
  manual-IP TCP transport, and USB fallback/recovery.
- Preserved the PIO Joybus receive backend and frozen Core0/accessory behavior.
- Added full GB/GBC ROM and save transfer plus Controller Pak `.mpk` load,
  create, save, and best-effort disconnect/application-close save behavior over
  the selected USB or TCP line transport.
- Froze the physically accepted RUDP-C reliable carrier at wire 1, window 8,
  flight cap 2, payload 512, RTO 40 ms, four retransmissions, UDP port 6465.
- Added explicit Network Pak Product Quick Mode without silent TCP fallback.
- Retained TCP as the Network Pak reference path and kept LATEST_STATE/Fast
  reserved and unsupported.

Normal Product Quick Mode operation passed available real-N64 validation with
exact protected Network Pak transfer CRC and zero reported errors/timeouts.
Repeated reconnect/session-renewal and TCP↔Quick handoff remain scheduled for
additional lifecycle validation. This is not a confirmed persistent defect and
does not reopen the physically accepted, frozen RUDP-C carrier. Gold status is
not changed solely by this validation note.

## NICE64 Browser v1.4.0

Changes in this release:

- B now returns directly to the current page from Favorites, History, Recent Addresses, and Options.
- The Start menu remains an explicit Start-button overlay.
- Local NICE Home with Enter URL, Search Web, Favorites, History, and Options.
- Configurable search provider and Brave search-result support.
- Local readability extraction with direct-converter fallback.
- DuckDuckGo redirect normalization when applicable.
- Persistent Favorites, Add to Home, History, and recent addresses.
- Clear History, Clear Recent Addresses, and Clear Cache options.
- Incremental image transfer, RGB5A1 rendering, and image failure handling.
- Page-loading indicator, browser banner, renderer stability work, and Bridge module loading.
