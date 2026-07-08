# NICE64 Network Pak Gold Export

This export contains the frozen NICE64 Network Pak firmware and the frozen NICE64 Network Pak Bridge application.

## Versions

- Network Pak firmware: Gold / Layer 2 stream validated
- Bridge application: NetworkPakBridge v0.8.1

## Layout

- `firmware/network-pak-gold/`
  - Ready-to-flash Network Pak firmware UF2.
  - ELF file for reference/debugging.
- `firmware/regular-gold-normal/`
  - Ready-to-flash Regular Gold / Normal firmware artifacts.
  - This is separate from the Network Pak firmware.
- `firmware/source/PicoN64Firmware/`
  - Firmware source and build scripts copied from the Network Pak Gold firmware package.
  - Build output directories are intentionally excluded.
- `bridge-application/networkpak-bridge-v0.8.1/application/`
  - Ready-to-run Windows bridge application and runtime dependencies.
- `bridge-application/networkpak-bridge-v0.8.1/source/`
  - Clean bridge application source tree for modification/building.
  - `bin` and `obj` are intentionally excluded.
- `archives/`
  - Clean source zip.
  - Ready-to-run Windows app zip.

## Running The Bridge

Run:

`bridge-application/networkpak-bridge-v0.8.1/application/PicoSwitchController.exe`

The application folder includes required DLLs, runtime files, assets, `portal.json`, content files, and controller mapping files.

## Building The Bridge

Requirements:

- Windows
- .NET 9 SDK

Build from:

`bridge-application/networkpak-bridge-v0.8.1/source/`

Command:

`dotnet build PicoSwitchController.sln -c Release`

NuGet dependencies are declared in `PicoSwitchController.csproj`.

## Firmware

Flash this UF2 to the Pico used as the Network Pak:

`firmware/network-pak-gold/pico_n64_controller_bridge.uf2`

This firmware is considered frozen for this release.

## Guardrails

Do not mix this with Regular Gold firmware.

Do not merge Network Pak firmware/application work back into Regular Gold.

Do not modify firmware, Layer 0, Layer 1, Layer 2, Joybus timing, ROM, or Portal ROM when working on the bridge application.

## Validation Notes

Known-good Network Pak stack includes:

- Layer 0 PASS
- Layer 1 PASS
- Layer 2 PASS
- OPEN_STREAM PASS
- READ_STREAM_CHUNK PASS
- CLOSE_STREAM PASS
- 1KB / 4KB / 16KB stream tests PASS
- 100 stream sessions PASS

Bridge v0.8.1 preserves:

- Network Pak bridge mode
- Portal/content/RSS/Ollama resources
- Transfer Pak full ROM install behavior
- 32KB `.sav` handling
- XInput mapping fixes
- Generic USB mapping
- 8BitDo / Parsec manual input source


## Network Portal ROM

- `rom/network-portal-v0.7/`
  - Latest frozen NICE64 Network Portal ROM build.
  - Matching source snapshot and source zip.

## Public Configuration Note

The exported `portal.json` uses public-safe Ollama defaults:

- `ollama.enabled`: `false`
- `ollama.url`: `http://localhost:11434`
- `ollama.model`: `llama3`

Set these locally after download if you want to use Ask NICE64 with a private Ollama endpoint.

## Documentation

- `docs/NETWORK_PAK_PROTOCOL.md`
  - Packet layout, command IDs, stream examples, bridge serial forwarding, resource examples, and protocol guardrails.
- `docs/JOYBUS_IMPLEMENTATION_NOTES.md`
  - Regular Gold Joybus timing, open-drain line behavior, command handling, accessory CRC, Transfer Pak notes, and emulator integration guidance.
- `docs/JOYBUS_TIMING_COMPARISON_GB_TOWER.md`
  - Logic analyzer timing comparison between a real Pokemon GB Tower capture and the NICE Transfer Pak capture.

## Support the Project

NICE64 is a passion project and is released completely free and open source for the Nintendo 64 community.

If you found this project useful, built your own Network Pak, or simply enjoyed following its development, donations are never expected but are greatly appreciated. Contributions help offset the cost of hardware, replacement parts, flash cartridges, test equipment, and future development.

**Support NICE64 via PayPal:**

**https://paypal.me/melfegor**

Thank you for your support, and I hope you enjoy building and experimenting with NICE64!
