# Troubleshooting — NICE64 Browser v1.4.0

**Connection Failed**: verify `bridge-application/nice64-bridge-v1.4.0/application/PicoSwitchController.exe` is running, the serial device is selected, and the Network Pak hardware is connected. Check the Bridge log beside the executable.

**403 or no website content**: this is normally a remote-site policy or dynamic-site limitation, not a protocol failure. Try another site.

**Search returns no results**: verify `SearchProviderUrl` contains `{query}` and inspect the log for provider fetch diagnostics. Providers can change their HTML at any time.

**Image unavailable**: the page remains valid. The selected image may be unsupported, oversized, blocked, or unavailable.

**Module does not load**: confirm its manifest, target framework, dependencies, and logs. Built-in module IDs take precedence over external duplicates.

**ROM does not boot or checksum differs**: verify the flash-cartridge deployment and compare against `CHECKSUMS_SHA256.txt`.
