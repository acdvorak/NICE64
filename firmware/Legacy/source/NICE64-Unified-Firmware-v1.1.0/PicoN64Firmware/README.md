# NICE64 Universal Firmware v1.1.0

This Pico 2 W source builds the unified Controller, Standalone Diagnostics, and
Standalone Network personalities. The current configuration uses PIO Joybus RX,
selective SRAM placement for the deterministic Core0 hot path, flash/XIP for
ordinary code, and Core1-owned USB/CYW43/lwIP services.

Set `PICO_SDK_PATH` and run `build-clean-pico2w.ps1` for a clean Release build.
The RUDP candidate helper adds the configuration guard used by the packaged
Product Quick Mode candidate.

Wiring and hardware safety:

```text
N64 GND  -> Pico GND
N64 DATA -> 330 ohm series resistor -> Pico GP2
N64 3.3V -> not connected
```

Power the Pico by USB. Never connect N64 3.3 V to Pico VBUS, VSYS, or Pico 3V3.

Architecture and transport documentation is maintained in the repository
`docs/UNIFIED_FIRMWARE.md` and `docs/WIFI_IMPLEMENTATION.md` files.
