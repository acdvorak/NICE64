# Pico N64 Controller Bridge Firmware

Best-effort first firmware for making a Raspberry Pi Pico 1 emulate a basic N64
controller.

## Wiring

Pico is powered by USB. Do not connect N64 3.3V to Pico 3V3.

```text
N64 GND  -> Pico GND
N64 DATA -> 330 ohm to 1k resistor -> Pico GP2
N64 3.3V -> not connected
```

The firmware treats the data pin like open drain: it pulls low for zero/one pulse
timing and releases the line for high.

## Current Behavior

- Responds to N64 status/identify commands with a normal controller identity.
- Responds to controller poll commands with button/stick state.
- Defaults to neutral input.
- Stubs accessory reads/writes with zero bytes.
- Uses GP2 for the N64 data line.

## USB Serial Input

Send one line with four hex bytes:

```text
N64 00000000
```

The four bytes are:

```text
byte 0: A B Z Start DUp DDown DLeft DRight
byte 1: 0 0 L R CUp CDown CLeft CRight
byte 2: stick X, signed byte
byte 3: stick Y, signed byte
```

Example, press A:

```text
N64 80000000
```

Example, stick right:

```text
N64 00005000
```

This is a preliminary firmware. N64 timing is strict, so this may need logic
analyzer tuning on real hardware.
