# Build NICE64 Browser v1.4.1

Requirements: Docker or a native libdragon installation, GNU Make, and the included source and generated assets.

From this package's `source` directory in an environment where `N64_INST` points to an installed libdragon toolchain:

```sh
N64_INST=/n64_toolchain make clean
N64_INST=/n64_toolchain make
```

The Makefile produces `nice-browser-rom-v1.4.1-Release.z64`. The packaged release ROM is located at `rom/NICE-Browser-v1.4.1/nice-browser-rom-v1.4.1-Release.z64`.

The release build used libdragon's archived project revision with the GCC 16.2.0 N64 toolchain. See `RELEASE_MANIFEST.md` for provenance, the exact release command, tests, and hashes.
