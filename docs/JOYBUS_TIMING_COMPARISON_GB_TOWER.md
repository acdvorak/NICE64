# Joybus Timing Comparison - GB Tower Capture

> **Historical validation record:** This document records a specific hardware
> timing comparison performed during NICE64 development. It is retained as
> engineering evidence and is not the current firmware specification.

Comparison between:

- Real hardware: `Real PK GB Tower.csv`
- NICE firmware: `TP PaK GB tower NICE.csv`

Both captures were analyzed on Channel 0. The CSVs appear to be transition/event captures, so the analysis uses edge-to-edge durations rather than sampled row counts.

## Summary

NICE is very close to real hardware for the core Joybus bit timing.

The strongest match is the overall bit-cell period:

| Bit type | Real median cell | NICE median cell | Difference |
|---|---:|---:|---:|
| `1` bit | 3.958 us | 4.000 us | +0.042 us |
| `0` bit | 3.958 us | 4.000 us | +0.042 us |

The main visible difference is the short-low `1` pulse:

| Pulse | Real median | NICE median | Difference |
|---|---:|---:|---:|
| `1` low pulse | 1.083 us | 0.917 us | -0.166 us |
| `0` low pulse | 3.042 us | 3.042 us | ~0 us |

NICE compensates by leaving the `1` high interval longer, so the total bit cell still lands at about 4 us.

The largest behavioral timing difference found in this pass is the longer intra-transaction high gap:

| Gap class | Real median | NICE median | Difference |
|---|---:|---:|---:|
| high gaps 4-50 us | 9.083 us | 20.125 us | +11.042 us |

These gaps likely include command-to-response turnaround or response staging gaps. Real hardware is notably quicker here, while NICE still appears accepted by GB Tower based on the working behavior.

## Capture Size

| Capture | Rows | Channel 0 edges | Low pulses |
|---|---:|---:|---:|
| Real hardware | 8,748,836 | 8,748,834 | 4,374,417 |
| NICE | 19,039,068 | 18,949,096 | 9,474,548 |

The NICE capture is longer, so counts are not directly comparable. Timing distributions are the useful comparison.

## Low Pulse Widths

| Class | Real count | Real median | NICE count | NICE median |
|---|---:|---:|---:|---:|
| short low, `1` bit | 1,779,141 | 1.083 us | 3,272,849 | 0.917 us |
| mid/transition, 2.0-2.6 us | 17,424 | 2.042 us | 34,918 | 2.042 us |
| long low, `0` bit | 2,577,851 | 3.042 us | 6,125,212 | 3.042 us |

Observations:

- `0` bit low timing is essentially identical.
- `1` bit low timing is shorter on NICE by about 0.17 us.
- Both captures have a small mid-width population around 2.04 us.

## High Pulse Widths Within Bit Cells

| Class | Real median | NICE median |
|---|---:|---:|
| `1` high interval | 2.875 us | 3.083 us |
| `0` high interval | 0.917 us | 0.958 us |

NICE's short `1` low is balanced by a longer `1` high, preserving the total bit cell.

## Full Bit Cells

| Class | Real mean | Real median | NICE mean | NICE median |
|---|---:|---:|---:|---:|
| `1` bit cell | 4.013 us | 3.958 us | 4.015 us | 4.000 us |
| `0` bit cell | 3.966 us | 3.958 us | 4.007 us | 4.000 us |

This is the most important result: both captures stay very close to the expected 4 us Joybus cell.

## Transaction / Frame Shape

Dominant pulse-count groups are the same:

| Pulse count per group | Real occurrences | NICE occurrences | Likely meaning |
|---:|---:|---:|---|
| 290 | 14,731 | 30,736 | Long accessory transactions |
| 42 | 1,358 | 7,374 | Medium transactions |
| 34 | 1,335 | 7,359 | Medium transactions |

The exact counts differ because the captures are not the same length, but the presence of the same dominant group sizes is a good sign. It means NICE is producing the same broad command/reply shapes as the real hardware capture during GB Tower activity.

## Inter-Frame Gaps

High gaps greater than 50 us were treated as transaction/frame separators.

| Metric | Real | NICE |
|---|---:|---:|
| separator count | 17,425 | 45,478 |
| median gap | 391.667 us | 435.875 us |
| p95 gap | 12.536 ms | 12.504 ms |

NICE's median inter-frame gap is about 44 us longer, but the high-percentile pause behavior is very similar.

## Practical Conclusion

NICE is close enough in the core Joybus waveform timing:

- `0` low pulses match extremely well.
- total bit-cell periods are within roughly 0.04 us median.
- transaction pulse-count shapes match.

The main differences are:

- NICE `1` low pulses are shorter, around 0.92 us versus real hardware around 1.08 us.
- NICE compensates with a longer high period, so the full cell is still about 4 us.
- NICE has a longer intra-transaction pause, about 20 us median versus real hardware about 9 us.
- NICE inter-frame gaps are slightly longer at the median, but similar at the p95 level.

For a formal emulator, the most important behavior to preserve is not the Pico-specific low/high split. Preserve:

- 4 us bit cells
- short-low `1`
- long-low `0`
- correct command lengths
- correct response byte counts
- correct accessory CRC
- correct Transfer Pak register behavior

For a hardware implementation, the NICE timing is workable but could be made closer to real hardware by:

1. Lengthening `1` low pulses by about 0.15-0.17 us.
2. Shortening the command-to-response turnaround gap where possible.
3. Keeping `0` low timing as-is.
