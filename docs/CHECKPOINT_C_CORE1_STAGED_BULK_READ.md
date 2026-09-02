# Checkpoint C — Core1 Staged Bulk Read

> **HISTORICAL DEVELOPMENT RECORD:** This document records the point-in-time
> Checkpoint C staged-bulk implementation and its original verification state.
> It is retained as engineering evidence and does not describe the current
> firmware v1.2.0 or Bridge v1.7.0 release status.

Original checkpoint status: implementation and automated verification pass;
physical benchmark pending.

Checkpoint C adds the explicitly negotiated `BULK_READ_V1` application path.
It does not modify the legacy `OPEN_STREAM (0x20) -> READ_STREAM_CHUNK
(0x22) -> CLOSE_STREAM (0x24)` request or response shapes.

## Layering

```text
Bridge stream source
  -> one logical read of at most 512 bytes
  -> 224 + 224 + up-to-64-byte bounded application frames
  -> fixed 512-byte Pico Core1 SRAM stage
  -> explicit staged 16-byte requests
  -> unchanged 32-byte Network Pak/Joybus replies
```

The Bridge performs one logical source fetch per block. Because the current
text envelope is retained, each full block uses three bounded internal
Bridge/Pico exchanges rather than 32 independent legacy READ16 exchanges.
Each generated frame is below the existing 600-byte TCP/RUDP application
limit. The RUDP-C payload, window, flight cap, RTO, retry budget, and port are
unchanged.

## Explicit capability negotiation

The new client sends the existing routed `GET_STREAM_INFO (0x21)` command with
the exact `BCAP` application query. New firmware rewrites only this opt-in
shape to the internal Bridge command `GET_APPLICATION_CAPABILITIES`. The new
Bridge replies with `STREAM_INFO (0xA1)` and this eight-byte payload:

```text
42 43 01 01 02 00 10 E0
 B  C  v1 flags max=512 delivery=16 internal-segment=224
```

This two-layer handshake prevents inference from TCP, Wi-Fi, RUDP, Quick Mode,
firmware version, or packet size:

- old firmware + new Bridge: the old `GET_STREAM_INFO BCAP` shape is rejected;
- new firmware + old/no-bulk Bridge: the internal capability command is rejected;
- new firmware + new Bridge: `BULK_READ_V1` is advertised;
- missing, malformed, rejected, or timed-out capability responses cause the
  new proof client to use legacy READ16.

The authoritative Bridge state model marks BulkRead as Bridge-implemented but
effective only after the application capability request is observed. Transport
selection alone never enables it.

## Stage ownership and protocol

The stage is a fixed 532-byte `.bss` object: 512 data bytes plus 20 bytes of
metadata/alignment. It uses no heap allocation, queue, linked list, double
buffer, or speculative prefetch. Its states are EMPTY, FILLING, READY,
CONSUMING, COMPLETE, and ABORTED.

The opt-in bulk-begin request is an extended `0x22` payload carrying marker
`B0`, stream ID, nonzero generation, absolute offset, and length. Length must
be 1–512. Bridge segment frames repeat the generation, stream, base offset,
logical length, segment offset/length, complete-block CRC32, and end-of-stream
flag. The stage accepts in-order segments, treats an exact already-accepted
duplicate idempotently, and rejects malformed overlap, gaps, wrong identity,
out-of-range data, truncation, or CRC mismatch.

After commit, each explicit `B1` staged-read request is resolved on Core1 and
published in the existing `STREAM_CHUNK (0xA2)` response shape. An accessory
read does not advance the stage. Core0 never traverses or waits on the stage.

## Memory and protected-path gate

```text
                              baseline       Checkpoint C       delta
flash/text                    375,320        378,712            +3,392
.bss                          191,044        191,576              +532
__bss_end__                   0x2003141C     0x20031630          +0x214
bytes to __StackLimit         322,532        322,000              -532
__StackLimit                  0x20080000     0x20080000              0
```

`network_pak_handle_read()` and `network_pak_handle_write()` are source-
identical to the baseline and retain the same SRAM addresses and sizes. The
read function is also machine-byte-identical. The write function's instruction
stream is unchanged; only linker-generated literal addresses moved because
new flash strings and `.bss` symbols were added.

Joybus, Normal Controller PIO, controller state, normal accessory code, and
RUDP-C protected sources remain hash-identical to the baseline.

## Verification disposition

Native stage boundary/error tests, Bridge capability/segment tests, legacy
Network Pak tests, B/B.5 lifecycle/state tests, TCP/provisioning tests, RUDP
adapter/reliability tests, Wireless Bulk Transfer tests, Browser readability
fixtures, firmware build, Bridge Release build, and proof ROM build pass.

Physical Wi-Fi/TCP comparison at 4, 16, and 64 KiB remains required. Browser
integration and Checkpoint D have not started.
