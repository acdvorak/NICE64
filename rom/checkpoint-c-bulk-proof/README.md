# NICE64 Checkpoint C Bulk Proof ROM

This dedicated diagnostic ROM compares the byte-compatible legacy READ16 path
with the explicitly negotiated `BULK_READ_V1` Core1 staging path. It does not
change or integrate the NICE Browser.

Use the D-pad left/right to select 4, 16, or 64 KiB. Use up/down to select
LEGACY, BULK, or AUTO. Press A to run. AUTO falls back to legacy when the
application capability is unavailable or when staged acquisition fails.

Run the six required Wi-Fi/TCP cases and record the displayed byte counts,
CRC32 values, elapsed time, rate, staged fetches, N64 chunks, Bridge round
trips, timeouts, protocol errors, staging aborts, and fallback state.
