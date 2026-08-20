# Changelog

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
