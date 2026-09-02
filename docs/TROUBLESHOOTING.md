# Troubleshooting

For current lifecycle and application-throughput limitations, see
[Known Issues and Deferred Work](KNOWN_ISSUES.md).

**USB connection fails**: verify the Pico 2 W is running Universal Firmware,
refresh the COM list, choose the port that identifies as NICE64, and close other
programs that may own the port.

**Wi-Fi will not associate**: reconnect by USB, verify the SSID/password, clear
and reprovision credentials, and check the reported association/DHCP status.
The release has no static IPv4 configuration or UDP discovery.

**TCP connection fails**: use the DHCP IPv4 address reported during
provisioning and TCP port 6464. Confirm the PC and NICE64 can reach each other
on the trusted LAN/VPN and that no other TCP owner is connected.

**Quick Mode fails**: first verify the same Network Pak workload over TCP, then
select Quick Mode explicitly. UDP port 6465 must be reachable. Failure is
reported and is not hidden by TCP fallback. Reconnect to create a fresh RUDP
session. Normal Quick Mode operation is real-N64 validated, but repeated
reconnect/session renewal and TCP↔Quick handoff remain less exhaustively tested.
If the failure persists, reconnect or restart the Bridge/device, or temporarily
use TCP. TCP remains the supported reference transport.

**Firmware mode switch over TCP is unavailable or disconnects**: use USB for
mode recovery and provisioning. Mode changes reboot NICE64 and close sessions.

**Controller Pak or Transfer Pak save**: use normal Bridge lifecycle actions so
the active persistence service can finish its bounded flush. During normal
Transfer Pak gameplay, Bridge v1.7.0 performs no periodic Transfer Pak `STAT`
polling, no background dirty-counter polling, and no background debounced
`GBSDUMP` autosave. Game Boy save synchronization is event-driven at explicit
lifecycle boundaries: accessory change, firmware-mode change, GB/GBC ROM
replacement, normal Bridge disconnect, and normal application close. Forced
process termination, abrupt NICE64 power loss, or unplugging before that flush
can prevent the newest Game Boy save from reaching the PC.

**Connection Failed**: verify
`bridge-application/nice64-bridge-v1.7.0/application/PicoSwitchController.exe`
is running, the selected transport is correct, and the Network Pak hardware is
connected. Enable and inspect the Bridge log only when diagnosing a problem.

**403 or no website content**: this is normally a remote-site policy or dynamic-site limitation, not a protocol failure. Try another site.

**Search returns no results**: verify `SearchProviderUrl` contains `{query}` and inspect the log for provider fetch diagnostics. Providers can change their HTML at any time.

**Image unavailable**: the page remains valid. The selected image may be unsupported, oversized, blocked, or unavailable.

**Module does not load**: confirm its manifest, target framework, dependencies, and logs. Built-in module IDs take precedence over external duplicates.

**ROM does not boot or checksum differs**: verify the flash-cartridge deployment and compare against `CHECKSUMS_SHA256.txt`.
