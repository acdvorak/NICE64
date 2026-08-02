# Build Manifest

## Public release

- Public release: `NICE64 Browser v1.4.0`
- Public Bridge version: `v1.4.0`
- Public ROM filename: `nice-browser-rom-v1.4.0-Release.z64`
- Public ROM path: `rom/NICE-Browser-v1.4.0/nice-browser-rom-v1.4.0-Release.z64`

## Release artifacts

- Bridge runtime: `bridge-application/nice64-bridge-v1.4.0/application/PicoSwitchController.exe`
- ROM runtime: `rom/NICE-Browser-v1.4.0/nice-browser-rom-v1.4.0-Release.z64`
- Source snapshot/freeze date: `2026-07-31`

## Toolchains and dependencies

- Bridge target framework: `net9.0-windows`
- Bridge dependency: HtmlAgilityPack 1.11.62
- Bridge validation host: Windows with .NET SDK 9.0.313
- ROM toolchain: libdragon in Docker with `N64_INST=/n64_toolchain`
- ROM build command: `N64_INST=/n64_toolchain make clean && N64_INST=/n64_toolchain make`
- Bridge build command: `dotnet build PicoSwitchController.csproj -c Release`
- Network Pak dependency: compatible NICE64 Network Pak firmware

Critical hashes are recorded in the repository-root `CHECKSUMS_SHA256.txt`.

## Validation results

- The release ROM is rebuilt from the included source and recorded in `CHECKSUMS_SHA256.txt`.

## Known limitations

- Modern sites can block automated fetches or change their markup.
- Search providers and image hosts can change behavior independently of the ROM.
- External modules are trusted local code.
