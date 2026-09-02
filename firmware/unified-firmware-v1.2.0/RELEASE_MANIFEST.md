# NICE64 Universal Firmware v1.2.0 release manifest

- Target: Pico 2 W / RP2350 Arm secure
- Build type: Release
- Product: `UNIVERSAL_FIRMWARE`
- Reported firmware version: `1.2.0`
- Joybus receive backend: PIO
- Network personality: enabled
- Reliable UDP hardware candidate configuration: enabled and accepted
- Execution: flash/XIP (`PICO_COPY_TO_RAM=OFF`)
- Firmware image boundary: 378,872 / 1,048,576 bytes — PASS
- Transfer Pak partition: `0x100000` through `0x300000`

Release artifact:

```text
NICE64-Universal-Firmware-v1.2.0.uf2
```

SHA-256:

```text
81A7266CF008ECC3EC665FC4D916EFBAF4BF8DE742930508E8905DE057FF7732
```

The version change does not alter Joybus timing, USB identity, accessory
protocols, Network Pak application framing, RUDP wire version, `LATEST_STATE`
packet semantics, or flash partition addresses.
