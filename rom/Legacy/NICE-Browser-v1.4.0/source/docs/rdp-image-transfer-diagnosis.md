# RDP Image Transfer Diagnosis

## Observed failure

The Fox image stream `browser://i/0006` stopped at offset 18,256 of 28,816
bytes with libdragon's `Unexpected RDP interrupt` assertion. The Bridge log
shows normal 16-byte chunks up to that point and no transport, CRC, ACK, or
generator failure.

## ROM lifecycle

1. `ImageTransport_Start` resets the 64 KB, 16-byte-aligned static receive
   buffer and enters `OPENING`.
2. `ImageTransport_Update` opens the stream, then writes up to two 16-byte
   chunks into that buffer during `Browser_Update`.
3. The buffer is not drawable while the state is `OPENING`, `READING`,
   `VERIFYING`, or `VALIDATING_NIMG`.
4. After all bytes arrive, the ROM computes the stream CRC, closes the stream,
   validates the 16-byte NIMG header and payload bounds, then builds the
   `surface_t` wrapper and enters `COMPLETE`.
5. Only `COMPLETE` with a valid NIMG permits `ImageTransport_GetValidatedImage`.
   `NICE_DrawValidatedImage` additionally checks page ID, route, dimensions,
   and pixel/surface pointers before submitting `rdpq_tex_blit`.
6. The pixel payload is write-backed before RDP use. `rdpq_detach_wait` runs
   before the frame can retire or a future navigation resets the buffer.

The 160x90 Fox payload is 28,800 bytes, with a 16-byte NIMG header for the
confirmed 28,816-byte total. The receive buffer and the header-adjusted pixel
pointer are both 16-byte aligned. Chunk bounds prevent writes beyond the
declared or allocated length.

## Finding

There is one reusable receive buffer, but it is not shared concurrently by the
CPU transfer and RDP: transfer work occurs in `Browser_Update`; drawing occurs
later in `Browser_Draw`; and the image was still non-drawable at the recorded
failure offset. There is no partial image submission, stale page publication,
or missing final cache writeback indicated by this failure.

`graphics_make_color` is a pure CPU color packing helper in libdragon. Its
appearance in the interrupt backtrace is incidental: the asynchronous RDP
interrupt arrived while the browser was next executing legacy graphics code.

The browser nevertheless created an RDPQ attach/detach/sync-full cycle every
frame, even when no image could be submitted. During an active image transfer,
that was an empty RDPQ frame and the only RDPQ work in the frame. This was an
unnecessary RDP interrupt source during the period in which the assertion was
observed.

## Correction

`Browser_Draw` now calls RDPQ only when `NICE_HasValidatedImage` confirms a
complete, validated image surface. Loading placeholders remain CPU-rendered.
The existing RDP pixel path, NIMG format, cache writeback, stream sequence,
and transfer chunking are unchanged. A bounded state-transition log records
whether RDP image work is enabled or skipped.

## Remaining hardware validation

1. Load the Fox page that resolves to `browser://i/0006`; wait for image close
   and display.
2. Leave the page open for 15 minutes and repeat navigation during a transfer.
3. Test Milk and Nintendo images, Back/Forward, and navigating away mid-load.
4. Confirm logs show RDP image work `skipped` while receiving and `enabled`
   only after image completion.

If an RDP assertion remains after this correction, capture the ROM debug log.
The next investigation should add a ROM-only offline NIMG fixture and an RDPQ
trace around an actual completed texture submission; it should not change the
Bridge or Network Pak protocol.
