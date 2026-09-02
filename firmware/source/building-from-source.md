# Building Firmware from Source

The current source is under
`firmware/source/NICE64-Unified-Firmware-v1.2.0/PicoN64Firmware`.

Requirements:

- CMake and Ninja
- Arm GNU embedded toolchain
- a compatible Raspberry Pi Pico SDK
- PowerShell 5.1 or later for the helper scripts

Set `PICO_SDK_PATH` to the local SDK directory, ensure the Arm tools and Pico
build tools are discoverable, then run:

```powershell
cd firmware\source\NICE64-Unified-Firmware-v1.2.0\PicoN64Firmware
.\build-clean-pico2w.ps1
```

If `pioasm` or `picotool` is installed outside normal package discovery, set
`PIOASM_DIR` and/or `PICOTOOL_DIR` to the directories containing their CMake
package configuration files. These overrides are optional and machine-local.

The release configuration targets `pico2_w` and asserts the universal
Controller/Diagnostics/Network image, PIO Joybus RX, and flash-XIP placement.
`build-rudpc-hardware-candidate-pico2w.ps1` additionally enables the RUDP
candidate configuration guard.

Build output is generated beneath the source directory and is excluded from
Git. Do not commit SDK paths, generated CMake state, logs, or intermediate
objects.
