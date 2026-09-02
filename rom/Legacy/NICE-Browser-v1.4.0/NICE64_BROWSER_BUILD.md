# Build the ROM

Requirements: Docker with a libdragon toolchain image, GNU Make, and the included ROM source and generated assets.

From `rom/source` inside an environment where `N64_INST` points at libdragon:

```sh
N64_INST=/n64_toolchain make clean
N64_INST=/n64_toolchain make
```

The Makefile produces `nice-browser-rom-v1.4.0-Release.z64`. This historical
release ROM is located at
`rom/Legacy/NICE-Browser-v1.4.0/nice-browser-rom-v1.4.0-Release.z64`. Copy it
to a supported flash cartridge.
