# NICE64 Keyboard / Mouse Joybus Diagnostic

Permanent libdragon diagnostic for the hardware-validated NICE64 Standalone
Keyboard / Mouse personality.

The ROM probes all four controller ports, preserves each raw three-byte
identify response, issues RandNET command `0x13` with a selectable LED byte,
and polls an N64 Mouse with command `0x01`. Keyboard and mouse raw replies stay
visible beside decoded values. Mouse deltas move a bounded cursor and are also
accumulated on screen so repeated or lost deltas are obvious.

Connect a normal controller on another port to change the keyboard LED request:

- D-Up toggles Power (bit 2)
- D-Left toggles Caps Lock (bit 1)
- D-Right toggles Num Lock (bit 0)

The screen also shows each normal controller's raw 16-bit button word and the
exact two request bytes transmitted to every detected keyboard (`tx 13 xx`).
For an N64 controller, D-Up is `0800`, D-Left is `0200`, and D-Right is `0100`
in the displayed big-endian word.

Build with the repository's archived libdragon source installed in the normal
libdragon container, then run `make` in this directory.

The included `nice64-keyboard-mouse-test.z64` is the diagnostic used to validate
RandNET identity/command/matrix/rollover/LED behavior and N64 Mouse
buttons/relative movement/consume-once behavior on real N64 hardware. Its
SHA-256 is recorded in the repository root checksum file.
