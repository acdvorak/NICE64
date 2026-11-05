# NICE64 Universal Firmware v1.3.0 source

This is a source-version preparation only. The existing hardware-validated UF2
remains the separately packaged v1.2.0 artifact until a v1.3.0 firmware build
is explicitly authorized and validated.

This Pico 2 W source builds the unified Controller, Standalone Diagnostics,
Standalone Network, and Standalone Keyboard / Mouse personalities. Keyboard /
Mouse selects either RandNET Keyboard or N64 Mouse. The current configuration uses PIO Joybus RX,
selective SRAM placement for the deterministic Core0 hot path, flash/XIP for
ordinary code, and Core1-owned USB/CYW43/lwIP services.

Core1 accepts the typed direct-mobile Keyboard/Mouse packets and publishes them
through the same source-neutral state boundary consumed by Core0 Joybus.
Controller payload compatibility, Joybus timing, accessory behavior, and
Network Pak remain unchanged.

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
