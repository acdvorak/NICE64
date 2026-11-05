# NICE64 Universal Firmware v1.3.0 release-candidate manifest

**v1.3.0 RELEASE CANDIDATE — HARDWARE VALIDATION PENDING**

- Target: Pico 2 W / RP2350 Arm secure
- Build type: Release
- Product: `UNIVERSAL_FIRMWARE`
- Reported firmware version: `1.3.0`
- Joybus receive backend: PIO
- Network personality: enabled
- Standalone Keyboard / Mouse personality: enabled (RandNET Keyboard and N64 Mouse)
- Reliable UDP hardware candidate configuration: enabled; wire/behavior
  inherited from the accepted G2 source
- Execution: flash/XIP (`PICO_COPY_TO_RAM=OFF`)
- Firmware image boundary: 387,184 / 1,048,576 bytes — PASS
- Transfer Pak partition: `0x100000` through `0x300000`

Release artifact:

```text
NICE64-Universal-Firmware-v1.3.0.uf2
```

SHA-256:

```text
B7807310DA88D16F251A5349F9D67E04A082A1CCE651527ACD17484062CDC785
```

The source matches the accepted G2 implementation across all functional files;
only the approved version declarations and documentation differ. The candidate
preserves Joybus timing, USB identity, Controller and accessory protocols,
Network Pak framing, RUDP wire version, Controller `LATEST_STATE`, direct
Keyboard/Mouse input, and flash partition addresses. It has not yet passed a
new physical hardware validation cycle. The prior v1.2.0 G2 UF2 remains the
hardware-validated reference with SHA-256
`95C6F83D84FDC32C4E1ADCA638A37B1BB06F56E897B43658DF09D5153FEE2359`.
