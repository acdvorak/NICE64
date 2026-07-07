# NICE64 Joybus Implementation Notes

These notes document what the NICE64 Regular Gold firmware learned about acting like an N64 controller and accessory on the Joybus line.

This is not an official Nintendo specification. It is a practical description of the working Raspberry Pi Pico implementation shipped as Regular Gold / Normal firmware. It is intended to help future NICE64 work, emulator authors, FPGA projects, or other homebrew hardware projects understand the choices that made this bridge reliable enough for controller, Controller Pak, Transfer Pak, Pokemon Profile, and GB/GBC save handling work.

## Source References

Regular Gold source snapshot:

`firmware/source/NICE64-Regular-Gold-Pokemon-Profiles-20260612/PicoN64Firmware/`

Most relevant files:

| File | Purpose |
|---|---|
| `src/joybus.c` | Joybus receive/transmit timing, command parsing, reply dispatch |
| `src/joybus.h` | Debug counters exported over serial |
| `src/n64_protocol.h` | N64 command IDs and controller identity constants |
| `src/n64_accessory.c` | Controller Pak, Rumble Pak, Transfer Pak, GB/GBC ROM/SRAM behavior |
| `src/n64_controller.c` | 4-byte controller state parsing and storage |
| `src/n64_tx.pio` | PIO open-drain transmitter program |
| `src/n64_rx_pulse.pio` | Optional PIO pulse-width receiver |
| `src/main.c` | USB serial command interface and debug output |

## Electrical Model

The N64 Joybus data line behaves like an open-drain/open-collector single-wire bus.

The firmware does not drive high. It only:

```text
pulls low  -> configure Pico GPIO as output low
releases   -> configure Pico GPIO as input
```

Implementation:

```c
static inline void n64_release(void) {
    gpio_set_dir(N64_DATA_PIN, GPIO_IN);
}

static inline void n64_pull_low(void) {
    gpio_put(N64_DATA_PIN, 0);
    gpio_set_dir(N64_DATA_PIN, GPIO_OUT);
}
```

Hardware note from the firmware README:

```text
N64 GND  -> Pico GND
N64 DATA -> 330 ohm to 1k resistor -> Pico GP2
N64 3.3V -> not connected
```

The Pico is powered by USB. The N64 data line is treated carefully because the console owns the pull-up and the bus is timing-sensitive.

## Bit Encoding

Joybus bits are pulse-width encoded on a 4 us bit cell.

Working model used by the firmware:

| Bit | Low time | High/released time | Total |
|---|---:|---:|---:|
| `1` | about 1 us | about 3 us | about 4 us |
| `0` | about 3 us | about 1 us | about 4 us |
| stop | low pulse, then release | bus idle | n/a |

Comments in `joybus.c` record first-party capture targets:

```text
reply delay ~2.2 us
1-low ~1.07 us
0-low ~3.04 us
```

The source constants are:

```c
#define N64_BIT_US 4
#define N64_ONE_LOW_US 2
#define N64_ZERO_LOW_US 3
#define N64_FIRST_ZERO_LOW_US 4
#define N64_FIRST_BIT_US 5
#define N64_STOP_LOW_US 2
```

The PIO transmitter uses a 0.5 us state-machine tick and documents the target shape as:

```text
1 bit: low 1 us, high 3 us
0 bit: low 3 us, high 1 us
stop:  low 1 us, then release
```

In practice, the firmware includes some Pico-specific compensation. For example, RP2350 builds use cycle waits for one-bit lows:

```c
#define N64_FIRST_ONE_LOW_CYCLES 120u
#define N64_ONE_LOW_CYCLES 120u
```

## Receive Strategy

The default Regular Gold receive path uses GPIO falling-edge interrupts.

The idea:

1. Interrupt on each falling edge from the console.
2. Wait a short delay after the edge.
3. Sample the line.
4. If the line has already returned high, classify it as a `1`.
5. If the line is still low, classify it as a `0`.

Relevant constants:

```c
#define RX_SAMPLE_DELAY_US 1     // Pico 1
#define RX_SAMPLE_DELAY_US 2     // RP2350
#define RX_STOP_TIMEOUT_US 12
#define RX_NEW_STREAM_US 300
```

`RX_NEW_STREAM_US` separates one command transaction from the next. If the bus has been idle for more than this, the receive state is reset and a new command stream begins.

Once enough command bytes have been collected, the receiver marks `rx_waiting_stop = true`. The next falling edge is interpreted as the stop pulse / end of command, and the firmware dispatches a reply.

There is also an optional PIO receive backend behind `NICE_RX_BACKEND_PIO`. The PIO receiver measures pulse width and emits one FIFO word per bit:

```text
1 = short low pulse, about 1 us
0 = long low pulse, about 3 us
```

The GPIO receiver was the default in the frozen Regular Gold source.

## Transmit Strategy

The stable transmit path uses PIO by default.

Compile switch:

```c
#define NICE_TX_BACKEND_BITBANG 0
```

PIO is used because response timing is tight and consistent bit shapes matter. The PIO program controls only pin direction:

```text
set pindirs, 1 -> pull bus low
set pindirs, 0 -> release bus high
```

The transmit FIFO protocol is:

```text
word 0: bit_count - 1
word 1..N: response bits, MSB-first
```

The firmware expands response bytes into individual bit words before feeding PIO:

```c
for each byte:
    for bit 7 down to 0:
        push bit
```

Interrupts are disabled while a reply is transmitted. This keeps the waveform from being stretched by USB serial work or other firmware activity.

## Command IDs

Defined in `n64_protocol.h`:

| Command | Value | Notes |
|---|---:|---|
| `N64_CMD_IDENTIFY` | `0x00` | Controller/accessory identity |
| `N64_CMD_STATUS` | `0x01` | 4-byte controller state |
| `N64_CMD_ACCESSORY_READ` | `0x02` | Read 32-byte accessory block plus CRC |
| `N64_CMD_ACCESSORY_WRITE` | `0x03` | Write 32-byte accessory block and return CRC |
| `N64_CMD_GCN_CONTROLLER_READ` | `0x40` | Diagnostic / compatibility path |
| `N64_CMD_GCN_CONTROLLER_ORIGIN` | `0x41` | Diagnostic / compatibility path |
| `N64_CMD_GCN_CONTROLLER_RECALIBRATE` | `0x42` | Diagnostic / compatibility path |
| `N64_CMD_GCN_CONTROLLER_READ_LONG` | `0x43` | Diagnostic / compatibility path |
| `N64_CMD_GAME_ID` | `0x1D` | Treated as 11-byte incoming command |
| `N64_CMD_RESET` | `0xFF` | Identify-like response |

Identity constants:

```c
#define N64_ID_CONTROLLER 0x05
#define N64_ID_NO_ACCESSORY_REAL_CONTROLLER 0x02
```

The firmware reports a standard controller identity and uses the third identity byte to indicate accessory presence.

## Command Lengths

The receive layer decides how many command bytes to collect based on the first byte:

| Command | Expected incoming bytes |
|---|---:|
| `0x00` identify | 1 |
| `0x01` status | 1 |
| `0x02` accessory read | 3 |
| `0x03` accessory write | 35 |
| `0x1D` game ID | 11 |
| `0x40` GCN read | 3 |
| other single-byte commands | 1 |

Accessory write is 35 bytes because it carries:

```text
command byte + 2-byte address + 32-byte data
```

## Response Timing

Several reply delays exist because not all commands tolerated the same response timing during hardware testing.

Source constants:

```c
#define N64_REPLY_DELAY_US 0
#define N64_HOT_REPLY_DELAY_US 2
#define N64_ACCESSORY_READ_REPLY_DELAY_US 6
#define N64_ACCESSORY_WRITE_REPLY_DELAY_US 3
```

The firmware has a "hot reply" path for common one-byte commands:

| Command | Hot reply? | Response |
|---|---|---|
| identify `0x00` | yes | 3 bytes |
| status `0x01` | yes | 4 bytes |

This avoids unnecessary work between command completion and reply start.

Accessory replies are delayed slightly more:

| Command | Delay |
|---|---:|
| accessory read | 6 us |
| accessory write | 3 us |

This was part of getting reliable behavior with real games and hardware.

## Controller Status Bytes

The controller status reply is 4 bytes. The bridge application sends controller state over USB serial, and `n64_controller.c` stores the most recent state.

The firmware can also force neutral status for diagnostics:

```c
#define NICE_FORCE_NEUTRAL_STATUS 0
```

If enabled, status replies are hard-coded to:

```text
00 00 00 00
```

Otherwise the current serial-fed controller state is returned.

For emulator authors: model this as the N64 controller's normal 4-byte controller report. Button mapping is bridge-side policy; Joybus only transports the packed bytes.

## Accessory Identity

Accessory presence changes the third byte of the identify response.

If no accessory is active:

```text
05 00 02
```

If an accessory is active:

```text
05 00 01
```

`n64_accessory_identity_byte()` owns that third byte.

## Accessory Reads And Writes

Accessory read command:

```text
02 AA BB
```

The address is decoded as:

```c
(((hi << 8) | lo) & 0xFFE0)
```

That aligns all accessory access to 32-byte blocks.

Accessory read response:

```text
32 data bytes + 1 CRC byte
```

Accessory write command:

```text
03 AA BB + 32 data bytes
```

Accessory write response:

```text
1 CRC byte
```

## Accessory CRC

The N64 accessory data CRC covers 32 data bytes and uses polynomial `0x85`.

Implementation:

```c
uint8_t crc = 0;
for each of 32 bytes:
    for bit 7 down to 0:
        input_bit = data_bit;
        feedback = ((crc & 0x80) != 0) ^ input_bit;
        crc <<= 1;
        if (feedback) crc ^= 0x85;
```

This CRC byte is appended to accessory reads and returned after accessory writes.

## Controller Pak Model

Memory Pak / Controller Pak state is a 32 KB memory array:

```c
#define PAK_SIZE 32768
#define PAK_BLOCK_SIZE 32
```

Reads and writes wrap inside that 32 KB region. This was good enough for the bridge application's Controller Pak style workflows.

## Transfer Pak Model

The Transfer Pak implementation lives in `n64_accessory.c`.

It models:

- Transfer Pak enable/disable register behavior
- bank selection
- access mode / changed flag behavior
- GB/GBC ROM reads
- SRAM reads/writes
- MBC1, MBC3, and MBC5 bank switching
- optional MBC3 RTC register selection

Important constants:

```c
#define TRANSFER_ROM_CACHE_SIZE 65536
#define GB_CART_SRAM_SIZE 32768
#define TRANSFER_ROM_FLASH_OFFSET (1024 * 1024)
```

The bridge can upload ROM data into flash for full ROM persistence, while SRAM is held in RAM and can be uploaded/downloaded by the bridge.

Transfer Pak visible register ranges in this implementation:

| N64 accessory address range | Meaning |
|---|---|
| `0x8000-0x8FFF` | enable/disable |
| `0xA000-0xAFFF` | Transfer Pak bank |
| `0xB000-0xBFFF` | access mode/status |
| `0xC000+` | GB cartridge bus window |

The GB cartridge window maps through the selected Transfer Pak bank:

```c
base = transfer_bank * 0x4000 + (address & 0x3FFF)
```

GB writes in the `0x0000-0x7FFF` range affect mapper registers. GB reads/writes in `0xA000-0xBFFF` affect SRAM or selected RTC registers, depending on mapper state.

## Practical Timing Lessons

Things that mattered in the working firmware:

- Treat the line as open-drain. Never drive high.
- Keep TX timing out of normal C code where possible. PIO gives consistent pulses.
- Disable interrupts during transmit.
- Separate command streams after an idle gap. This avoids carrying half-parsed state into the next poll.
- Dispatch identify/status quickly. Games poll these constantly.
- Accessory read/write replies needed slightly different delays than plain controller status replies.
- Keep debug counters. The `STAT` output was useful for seeing command counts, accessory reads/writes, stop timeouts, no-reply counts, and last command values.
- Do not assume every game polls accessories the same way. Pokemon Stadium / Transfer Pak flows are stricter than basic controller polling.

## Notes For Emulator Authors

If applying this to a formal N64 emulator, the Pico-specific details are less important than the behavioral model.

Model these:

- Joybus command IDs and response lengths.
- 4-byte controller status response.
- identify response and accessory-present byte.
- accessory read/write block alignment to 32 bytes.
- accessory CRC polynomial `0x85`.
- Transfer Pak register ranges and GB bus mapping.
- MBC1/MBC3/MBC5 ROM/RAM banking behavior if emulating Transfer Pak.

You usually do not need to model these exactly in an emulator:

- Pico GPIO direction switching.
- PIO FIFO protocol.
- interrupt masking.
- microsecond busy waits.

But you should preserve ordering and response semantics:

```text
host command -> parse full command -> produce correct response bytes -> include CRC where required
```

For cycle-accurate or hardware-in-the-loop emulation, timing becomes relevant:

- bits are 4 us cells
- `1` is short-low/long-high
- `0` is long-low/short-high
- accessories return 32 data bytes plus CRC on read
- command boundaries are detected by the stop pulse and idle gap

## Serial Bridge Interface

The Regular Gold firmware receives controller/accessory content from the Windows bridge over USB serial.

Examples from `main.c`:

| Serial command | Purpose |
|---|---|
| `N64 00000000` | Set 4-byte controller state |
| `PAKMODE TRANSFER` | Enable Transfer Pak mode |
| `PAKCLR` | Clear Controller Pak memory |
| `GB <offset> <64 hex chars>` | Write ROM cache block |
| `GBFBEGIN <size>` | Begin flash-backed ROM upload |
| `GBFPAGE <offset> <512 hex chars>` | Write 256-byte flash page |
| `GBFCOMMIT` | Commit flash-backed ROM upload |
| `GBS <offset> <64 hex chars>` | Write 32-byte save/SRAM block |
| `GBSDUMP <offset>` | Read 32-byte save/SRAM block |
| `STAT` | Print Joybus and accessory debug counters |
| `CLR` | Clear debug counters |

This means the Pico handles strict Joybus timing, while the Windows app handles high-level controller mapping, ROM loading, and save file persistence.

## Debug Counters

`joybus_debug_t` tracks:

- edge count
- command stream count
- command counts by type
- accessory reads/writes
- GCN diagnostic command counts
- RX command complete count
- stop dispatch count
- stop timeout count
- no-reply count
- TX start/done count
- last accessory address
- last command
- last TX command and reply length
- current line state

This is worth copying into future projects. Hardware protocol work is much easier when the device can explain what it thinks it saw.

## Guardrails

- Keep Regular Gold firmware separate from Network Pak firmware.
- Treat this document as learned implementation behavior, not a replacement for independent protocol verification.
- Preserve the open-drain electrical model.
- Preserve accessory CRC behavior.
- Preserve 32-byte accessory block behavior.
- Avoid moving web, RSS, AI, or other high-level features into firmware. That belongs to bridge applications.

