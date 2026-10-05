# Joybus Timing Comparison - GB Tower Capture, NICE64 1.2

> **Historical validation record:** This document records specific hardware
> timing comparisons performed during NICE64 development. It is retained as
> engineering evidence and is not the current firmware specification.

Comparison baseline:

- Real hardware: `Real PK GB Tower.csv`
- NICE64 1.2 firmware: `1.2 NICE64 PK GB Towner.csv`

Additional NICE64 1.2 captures were later taken from the NICE64 debug point and
with two disposable diagnostic firmware builds to investigate the longer
command-to-response turnaround.

All captures were analyzed on Channel 0. The CSVs are transition/event captures,
so the analysis uses edge-to-edge durations rather than sampled row counts.

## Summary

NICE64 1.2 continues to match real hardware very closely in the most important
Joybus timing characteristic: the overall bit-cell period.

| Bit type | Real median cell | NICE64 1.2 median cell | Difference |
|---|---:|---:|---:|
| `1` bit | 3.958 us | 4.000 us | +0.042 us |
| `0` bit | 3.958 us | 4.000 us | +0.042 us |

The internal low/high duty cycle differs from the captured first-party Transfer
Pak, but the complementary high interval preserves the total approximately 4 us
bit cell.

Initial 1.2 capture:

| Pulse | Real median | NICE64 1.2 median | Difference |
|---|---:|---:|---:|
| `1` low pulse | 1.083 us | 0.708 us | -0.375 us |
| `0` low pulse | 3.042 us | 2.833 us | -0.209 us |

Direct measurement from the NICE64 debug point produced slightly different,
and slightly closer, low-pulse measurements:

| Pulse | Real median | NICE64 1.2 debug-point median |
|---|---:|---:|
| `1` low pulse | 1.083 us | 0.750 us |
| `0` low pulse | 3.042 us | 2.875 us |

This indicates that the external cable/measurement path can influence the
observed duty cycle slightly. It did **not**, however, materially affect the
longer response turnaround.

The largest consistent difference from first-party hardware remains the
command-to-response / intra-transaction turnaround:

| Capture | Median turnaround |
|---|---:|
| Real Transfer Pak | 9.083 us |
| Previous NICE / legacy-era capture | 20.125 us |
| NICE64 1.2 initial capture | 31.125 us |
| NICE64 1.2 direct debug-point capture | 30.958 us |

The approximately 31 us turnaround is therefore a real NICE64 1.2 firmware
characteristic rather than a cable artifact.

Importantly, **no meaningful negative effect has been observed from this longer
turnaround in current hardware testing**. GB Tower and Transfer Pak operation
remain functional, and the Unified firmware has shown broad practical
improvements over the legacy firmware despite this one timing characteristic
moving farther away from the first-party waveform.

This should therefore be treated as a **known implementation difference**, not
a demonstrated defect.

## Capture Size

| Capture | Rows | Channel 0 edges | Low pulses |
|---|---:|---:|---:|
| Real hardware | 8,748,836 | 8,748,834 | 4,374,417 |
| NICE64 1.2 initial | 7,356,002 | 7,356,000 | 3,678,000 |

The captures differ in length and activity mix, so raw event counts should not
be compared directly. Timing distributions and recurring transaction shapes are
the useful metrics.

## Low Pulse Widths

Initial 1.2 capture:

| Class | Real count | Real median | NICE64 1.2 count | NICE64 1.2 median |
|---|---:|---:|---:|---:|
| short low, `1` bit | 1,779,141 | 1.083 us | 1,518,863 | 0.708 us |
| mid/transition, 2.0-2.6 us | 17,424 | 2.042 us | 0 | — |
| long low, `0` bit | 2,577,852 | 3.042 us | 2,159,137 | 2.833 us |

Observations:

- NICE64 1.2 still produces two clearly separated low-pulse populations
  corresponding to Joybus `1` and `0`.
- The `1` low pulse is shorter than the captured first-party hardware.
- The `0` low pulse is also somewhat shorter.
- The expected short-low `1` / long-low `0` relationship remains clear.
- Direct measurement at the NICE64 debug point shifted these medians to
  approximately 0.750 us and 2.875 us respectively, showing that the external
  cable/measurement path has a small effect on apparent duty cycle.
- These duty-cycle differences do not produce a meaningful bit-cell period
  error.

## High Pulse Widths Within Bit Cells

Initial 1.2 capture:

| Class | Real median | NICE64 1.2 median |
|---|---:|---:|
| `1` high interval | 2.875 us | 3.333 us |
| `0` high interval | 0.917 us | 1.167 us |

Direct debug-point capture:

| Class | NICE64 1.2 debug-point median |
|---|---:|
| `1` high interval | 3.250 us |
| `0` high interval | 1.125 us |

The longer high intervals compensate for the shorter low periods.

The exact medians of independently measured low and high populations do not
necessarily sum to the exact full-cell median, but the distributions show the
same compensating behavior.

## Full Bit Cells

| Class | Real mean | Real median | NICE64 1.2 mean | NICE64 1.2 median |
|---|---:|---:|---:|---:|
| `1` bit cell | 4.013 us | 3.958 us | 4.246 us | 4.000 us |
| `0` bit cell | 3.966 us | 3.958 us | 4.007 us | 4.000 us |

The median remains the more representative measurement for normal Joybus bits.

The elevated NICE64 1.2 `1`-cell mean is primarily caused by longer high
intervals associated with transaction/staging gaps being included in the same
short-low population. The median normal bit cell remains almost exactly 4 us.

This remains the strongest timing result:

**NICE64 1.2 preserves the nominal Joybus 4 us bit-cell period extremely
closely.**

Direct debug-point and diagnostic captures continued to show 4.000 us median
cells for both `1` and `0`, demonstrating that the core bit timing remained
stable throughout the investigation.

## Transaction / Frame Shape

The dominant long transaction shape remains identical:

| Pulse count per group | Real occurrences | NICE64 1.2 occurrences | Likely meaning |
|---:|---:|---:|---|
| 290 | 14,731 | 12,617 | Long accessory transactions |
| 42 | 1,358 | 229 | Medium transactions |
| 34 | 1,335 | 277 | Medium transactions |

The exact frequency of each group differs because the captures contain
different amounts and mixtures of GB Tower activity.

The important structural result is that NICE64 1.2 still produces the same
characteristic 290-, 42-, and 34-pulse transaction shapes. The 290-pulse
accessory transaction remains clearly present and dominant.

This supports the conclusion that NICE64 continues to reproduce the same broad
command/reply transaction structures as real hardware during Transfer Pak /
GB Tower operation.

## Intra-Transaction / Command-to-Response Turnaround

High intervals between 4 us and 50 us were treated separately from normal
bit-cell highs and long frame separators.

Initial comparison:

| Metric | Real | NICE64 1.2 |
|---|---:|---:|
| occurrence count | 14,731 | 12,887 |
| median | 9.083 us | 31.125 us |
| mean | 9.040 us | 29.979 us |
| p95 | 9.292 us | 34.083 us |

Real hardware has a highly concentrated turnaround interval around 9 us.
NICE64 1.2 instead has a large population centered near 30-31 us.

A second capture taken directly from the NICE64 debug point measured:

- median: **30.958 us**
- p95: **34.083 us**

Removing the extra external cable path therefore changed the turnaround by only
about 0.17 us. This strongly indicates that the approximately 31 us turnaround
originates in the NICE64 implementation and is not a propagation/cabling
artifact.

### Functional significance

Although the NICE64 turnaround is measurably longer than both the first-party
Transfer Pak and the previous NICE capture, **no meaningful functional effect
has been observed**.

Current real-hardware testing shows:

- GB Tower remains operational.
- Transfer Pak reads and writes remain functional.
- No communication failure has been correlated with the approximately 31 us
  turnaround.
- The Unified firmware operates better overall than the legacy firmware in
  practical testing, despite adding roughly 10-11 us to this particular timing
  interval.

The result therefore appears to represent available Joybus timing tolerance
rather than an incompatibility by itself.

This does **not** prove that every possible game, accessory sequence, or
third-party implementation will tolerate the same timing. It does show that the
tested N64 / GB Tower / Transfer Pak workloads accept it reliably enough that
the difference should not presently be classified as a defect.

## Investigation of the Longer Turnaround

Because the Unified firmware introduced substantial architectural and
memory-layout changes relative to the legacy implementation, two disposable
diagnostic builds were created to determine whether the additional turnaround
was caused by flash/XIP behavior.

Neither experiment was integrated into production firmware.

### Diagnostic 1 - Explicit SRAM / No-Inline Transfer Pak Helpers

The first investigation examined the live Transfer Pak ROM-read chain:

`PIO IRQ`
→ accessory command dispatch
→ `n64_accessory_decode_address`
→ `n64_accessory_handle_read`
→ `n64_transfer_read`
→ `gb_cart_read`
→ `gb_cart_rom_size`
→ `gb_cart_raw_rom_read`
→ ROM-data load
→ `n64_data_crc`
→ TX

Source and ELF inspection showed that the untouched NICE64 1.2 compiler had
already inlined the Transfer Pak read, mapper, ROM-read, and CRC bodies into the
SRAM-resident `n64_accessory_handle_read`.

There was also:

- no Core0-to-Core1 round trip;
- no mailbox wait;
- no mutex;
- no synchronous flash lockout;
- no inter-core wait in the live ROM-read response path.

A disposable test build explicitly changed several helpers to
`__no_inline_not_in_flash_func` so they remained SRAM-resident but were forced
out-of-line.

Measured result:

| Capture | Turnaround median | p95 |
|---|---:|---:|
| Normal 1.2 debug-point | 30.958 us | 34.083 us |
| Explicit SRAM/no-inline test | 37.896 us | 41.208 us |

The experiment made turnaround roughly **7 us slower**.

This disproved the hypothesis that the normal 1.2 delay was caused by these
helper functions accidentally executing from flash/XIP. Instead, the original
compiler inlining was beneficial to the hot path, while forcing out-of-line
function calls added deterministic overhead.

The diagnostic change was therefore rejected and not integrated.

### Diagnostic 2 - SRAM-Backed GB ROM Payload

The second investigation tested the remaining XIP-related possibility: the
actual GB ROM payload data itself.

A fresh disposable fork from untouched 1.2 preloaded the first 32 KiB of the
uploaded GB ROM into SRAM during ROM commit.

For controlled cache-hit Transfer Pak reads:

- the response payload came entirely from SRAM;
- normal mapper calculations remained active;
- CRC remained unchanged;
- Joybus timing remained unchanged;
- PIO and TX remained unchanged;
- the original 1.2 inlining arrangement was preserved;
- higher ROM offsets still used the normal XIP fallback.

This isolated the cost of the Core0 GB-ROM payload load while preserving the
rest of the normal response path as closely as practical.

Measured result:

| Capture | Turnaround median | p95 |
|---|---:|---:|
| Normal 1.2 debug-point | 30.958 us | 34.083 us |
| SRAM-backed ROM-data test | 32.667 us | 34.875 us |

The SRAM-backed response did **not** reduce turnaround. The median was about
1.7 us higher.

The diagnostic required an SRAM-window bounds check in the timed path, so a
small amount of test overhead was expected. Regardless, the experiment clearly
did not reveal a large XIP ROM-data penalty hidden inside the approximately
31 us turnaround.

This substantially weakens the hypothesis that direct GB-ROM XIP data access is
the main source of the Unified firmware's additional response latency.

The diagnostic consumed approximately 32 KiB of additional SRAM and provided no
timing advantage, so it was not suitable as a production optimization and was
not integrated.

### Investigation conclusion

The experiments eliminated several initially plausible explanations.

The approximately 31 us NICE64 1.2 turnaround is **not primarily explained by**:

- the external cable/logic-analyzer connection;
- a synchronous Core0/Core1 mailbox exchange;
- mutex/inter-core waiting;
- accidental XIP execution of the Transfer Pak helper code;
- GB-ROM payload reads from XIP flash.

The remaining difference appears to be a deterministic characteristic of the
broader Unified Joybus/accessory response path.

Further stage-by-stage instrumentation could identify exactly where the
additional roughly 10 us versus the previous NICE implementation is spent, but
there is currently no demonstrated functional requirement to do so.

## Inter-Frame Gaps

High gaps greater than 50 us were treated as transaction/frame separators.

Initial 1.2 capture:

| Metric | Real | NICE64 1.2 |
|---|---:|---:|
| separator count | 17,425 | 13,124 |
| median gap | 391.667 us | 382.083 us |
| p95 gap | 12.536 ms | 0.569 ms |

Direct debug-point capture measured a median of approximately **382.708 us**,
which is similarly close to the real-hardware median.

The median separator timing is therefore much closer to first-party hardware
than in the previous NICE capture, whose median was approximately 435.875 us.

The p95 values should **not** be interpreted as evidence that NICE64 1.2
eliminated a firmware delay. The captures contain different activity
distributions, and the real capture contains long approximately 12.5 ms pauses
that are much less prominent in the particular NICE64 traces.

Median transaction spacing is therefore the safer comparison here.

## Comparison to Previous NICE / Legacy-Era Capture

| Metric | Previous NICE | NICE64 1.2 | Change |
|---|---:|---:|---:|
| `1` low median | 0.917 us | 0.708 us initial / 0.750 us direct | shorter |
| `0` low median | 3.042 us | 2.833 us initial / 2.875 us direct | shorter |
| `1` bit median | 4.000 us | 4.000 us | unchanged |
| `0` bit median | 4.000 us | 4.000 us | unchanged |
| intra-transaction turnaround | 20.125 us | about 31 us | about +11 us |
| inter-frame median | 435.875 us | about 382-383 us | about -53 us |

The Unified 1.2 implementation therefore changed waveform duty cycle and added
roughly 10-11 us of command-to-response turnaround while preserving the
fundamental 4 us bit rate.

At the same time, the median inter-frame spacing moved substantially closer to
the first-party Transfer Pak capture.

More importantly, the Unified architecture has shown **across-the-board
practical improvements in current testing** compared with the legacy firmware.
The longer turnaround is the notable measured characteristic that moved in the
opposite direction, but no corresponding functional regression has been
observed.

It should therefore be described as a **measurable timing tradeoff / known
implementation difference**, not as a demonstrated regression in compatibility.

## Practical Conclusion

NICE64 1.2 remains functionally very close to the N64 Joybus protocol where it
matters most:

- total bit-cell periods remain essentially 4 us;
- `1` and `0` pulses remain clearly distinguishable;
- the expected short-low `1` and long-low `0` encoding is preserved;
- dominant transaction pulse-count shapes match real hardware;
- the 290-pulse accessory transaction remains clearly present;
- GB Tower / Transfer Pak activity is accepted by real N64 hardware;
- repeated direct and diagnostic captures show stable, deterministic timing;
- no meaningful functional problem has been linked to the approximately 31 us
  response turnaround.

The waveform is **not an electrical timing clone of the original hardware**.

Compared with the captured first-party Transfer Pak:

- NICE64's `1` low pulse is shorter;
- NICE64's `0` low pulse is somewhat shorter;
- the corresponding high intervals are longer;
- transaction turnaround is approximately 31 us rather than approximately
  9 us.

However, these differences do **not** currently translate into bit-period error
or an observed compatibility failure.

For a formal emulator, implementation should preserve protocol behavior rather
than duplicate the Pico-specific waveform exactly:

- 4 us bit cells
- short-low `1`
- long-low `0`
- correct command lengths
- correct response byte counts
- correct accessory CRC
- correct Transfer Pak register behavior
- valid command-to-response timing within demonstrated tolerances

For NICE64 hardware, there is currently no evidence that reducing the
approximately 31 us turnaround would improve compatibility or user-visible
behavior. Optimization solely to reproduce the first-party approximately 9 us
turnaround is therefore not presently justified.

If a future title, accessory sequence, or hardware implementation demonstrates
sensitivity to this interval, the Unified response path can be instrumented
stage-by-stage and revisited.

## Overall Assessment

The NICE64 1.2 captures demonstrate that the Unified implementation continues
to satisfy the fundamental Joybus timing requirements despite measurable
differences from the captured first-party Transfer Pak.

The strongest result remains:

> **Real hardware median bit cell: 3.958 us**  
> **NICE64 1.2 median bit cell: 4.000 us**

That difference is approximately **42 ns**, or about **1.1% of one Joybus bit
period**.

The approximately 31 us response turnaround is a repeatable Unified-firmware
characteristic and is roughly 10-11 us longer than the previous NICE capture.
Dedicated experiments showed that it is not explained by the external cable,
accidental flash-resident Transfer Pak helper code, Core1 synchronization, or
the direct XIP GB-ROM payload reads.

Despite this difference, current real-hardware testing has shown **no
noticeable adverse effect**, while the Unified firmware has produced broad
overall improvements relative to the legacy implementation.

The appropriate engineering conclusion at this stage is therefore:

> **NICE64 1.2 is behaviorally compatible with the tested Joybus / Transfer Pak
> workloads while not attempting cycle-exact reproduction of first-party
> electrical timing. The measured waveform and turnaround differences appear
> to remain within the tolerances accepted by the tested N64 hardware and
> software.**

This conclusion is based on the hardware and software combinations tested to
date and should not be interpreted as proof that every possible title,
accessory, console revision, or third-party implementation will have identical
tolerance.
