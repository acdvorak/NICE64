# NICE64

NICE64 is a hardware, firmware, ROM, and Windows Bridge project for connecting a real Nintendo 64 console to external services through the controller port.

The repository contains the frozen Network Pak firmware, the current Windows Bridge, the NICE64 Browser ROM, the earlier Network Portal ROM, hardware designs, and technical documentation.

## License

NICE64 is released under the MIT License.

My goal is for this project to be useful to the retro gaming and homebrew community. If you build something with NICE64, I'd genuinely love to hear about it. Attribution is appreciated, and contributions are always welcome.

See the included `LICENSE.md` file for the complete license text.

## Current Public Versions

- **NICE64 Bridge:** v1.4.0
- **NICE64 Browser:** v1.4.0
  - ROM: `rom/NICE-Browser-v1.4.0/nice-browser-rom-v1.4.0-Release.z64`
- **Network Portal ROM:** v0.7
- **Network Pak firmware:** Gold / Layer 2 stream validated
- **Regular / Normal firmware:** Gold, maintained separately from Network Pak firmware

## Repository Layout

```text
NICE64/
|-- bridge-application/
|   `-- nice64-bridge-v1.4.0/
|       |-- application/
|       |-- modules/
|       `-- source/
|-- configuration/
|-- docs/
|-- examples/
|-- firmware/
|-- hardware/
|-- rom/
|   |-- NICE-Browser-v1.4.0/
|   `-- network-portal-v0.7/
|-- CHECKSUMS_SHA256.txt
|-- LICENSE.md
`-- README.md
```

## Bridge Application

The Windows Bridge owns host communication, resource handling, Browser Live processing, external module loading, and Network Pak stream handling.

Run:

```text
bridge-application/nice64-bridge-v1.4.0/application/PicoSwitchController.exe
```

The runtime folder includes the executable, required DLLs, configuration, assets, and module packages.

Build requirements and commands are documented in:

- [Build the Bridge](docs/BUILD_BRIDGE.md)
- [Bridge Configuration](docs/CONFIGURATION.md)
- [Bridge Modules](docs/BRIDGE_MODULES.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)

## NICE64 Browser

The NICE64 Browser is an N64 web browser client for the Network Pak. The Bridge fetches and simplifies web content, then streams a compact NICE document to the ROM.

Features include:

- Enter URL
- Search Web
- Brave search-result support
- Local readability extraction
- Favorites
- Add to Home
- Persistent History
- Recent Addresses
- Clear History
- Clear Recent Addresses
- Clear Cache
- Image transfer and rendering
- Back, Forward, and Reload
- Trusted Bridge modules

The public ROM is located under:

```text
rom/NICE-Browser-v1.4.0/
```

Browser-specific documentation is kept with the Browser ROM so future ROMs can carry their own build and control documentation without filename collisions:

- [Build the NICE64 Browser ROM](rom/NICE-Browser-v1.4.0/NICE64_BROWSER_BUILD.md)
- [NICE64 Browser Controls](rom/NICE-Browser-v1.4.0/NICE64_BROWSER_CONTROLS.md)
- [NICE64 Browser Quick Start](rom/NICE-Browser-v1.4.0/NICE64_BROWSER_QUICK_START.md)

## Network Portal ROM

The earlier Network Portal ROM remains available under:

```text
rom/network-portal-v0.7/
```

It is preserved as a separate ROM and is not replaced by the NICE64 Browser.

## Firmware

### Network Pak Gold

Flash the Network Pak UF2 from:

```text
firmware/network-pak-gold/
```

This firmware is frozen for the current release.

### Regular / Normal Gold

Regular / Normal firmware is maintained separately under:

```text
firmware/regular-gold-normal/
```

Do not mix Network Pak firmware with Regular / Normal firmware.

### Source

Firmware source and build scripts are stored under:

```text
firmware/source/
```

Build-output directories are intentionally excluded from source snapshots.

## Hardware

Hardware documentation is stored under:

```text
hardware/
```

The current carrier-board prototype is not yet published as production-ready KiCad hardware. It will be added after the physical prototype has been validated.

The current prototype design uses:

- Pico 2 W
- USB-only Pico power
- female right-angle DE-9 connector
- removable Pico socket headers
- permanent 4.7 kOhm N64-side pull-up
- 330 ohm series resistor on the data path

Never connect N64 3.3 V to Pico VBUS, VSYS, or Pico 3V3.

## Documentation

Shared technical documentation remains under `docs/`:

- [Network Pak Protocol](docs/NETWORK_PAK_PROTOCOL.md)
- [Joybus Implementation Notes](docs/JOYBUS_IMPLEMENTATION_NOTES.md)
- [GB Tower Timing Comparison](docs/JOYBUS_TIMING_COMPARISON_GB_TOWER.md)
- [Bridge Modules](docs/BRIDGE_MODULES.md)
- [Build the Bridge](docs/BUILD_BRIDGE.md)
- [Configuration](docs/CONFIGURATION.md)
- [Build Manifest](docs/BUILD_MANIFEST.md)
- [Changelog](docs/CHANGELOG.md)
- [Licenses and Notices](docs/LICENSES.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)

ROM-specific documentation stays with its ROM under `rom/<rom-name>/`.

For example:

```text
rom/NICE-Browser-v1.4.0/NICE64_BROWSER_BUILD.md
rom/NICE-Browser-v1.4.0/NICE64_BROWSER_CONTROLS.md
rom/network-portal-v0.7/README.md
```

This keeps shared project documentation under `docs/` while preventing ROM-specific build or control files from colliding as additional ROMs are added.

## Validation

The known-good Network Pak stack includes:

- Layer 0 PASS
- Layer 1 PASS
- Layer 2 PASS
- OPEN_STREAM PASS
- READ_STREAM_CHUNK PASS
- CLOSE_STREAM PASS
- 1 KB, 4 KB, and 16 KB stream tests PASS
- 100 stream sessions PASS

The current Browser ROM was rebuilt from the exported source and matched the validated ROM byte-for-byte.

The validated Bridge runtime remains the distributed runtime. Exact hashes are recorded in:

```text
CHECKSUMS_SHA256.txt
```

## Project Guardrails

- Keep Network Pak firmware separate from Regular / Normal firmware.
- Do not change Network Pak wire framing or ACK behavior without coordinated firmware, Bridge, and ROM changes.
- Do not merge experimental firmware work into frozen Gold artifacts.
- Treat external Bridge modules as trusted local code.
- Keep public release names separate from internal staging identifiers.
- Preserve ROM-specific documentation with the ROM it documents.

## Support the Project

NICE64 is a passion project and is released completely free and open source for the Nintendo 64 community.

If you found this project useful, built your own Network Pak, or simply enjoyed following its development, donations are never expected but are greatly appreciated. Contributions help offset the cost of hardware, replacement parts, flash cartridges, test equipment, and future development.

**Support NICE64 via PayPal:**

**https://paypal.me/melfegor**

Thank you for your support, and I hope you enjoy building and experimenting with NICE64!

## License

NICE64 is distributed under the MIT License.

See:

- [LICENSE.md](LICENSE.md)
- [Third-party licenses and notices](docs/LICENSES.md)
