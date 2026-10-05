# NICE64 Browser Profile SRAM Format

Browser v1.3 stores a reusable NICE64 application profile in 32 KiB cartridge SRAM.
The browser accesses this only through `browser_profile.h`; the physical backend is
isolated in `browser_profile_storage_sram.c`.

## ROM Declaration

The Makefile declares `N64_ROM_SAVETYPE = sram256k`, which is the current
libdragon spelling for 256-kilobit (32 KiB) SRAM.

## Storage API

`browser_profile_storage.h` provides initialized, bounded, aligned read, write,
verify, and clear operations. Its current SRAM backend uses the cartridge SRAM PI
window. A future FlashRAM backend can implement the same API without changes to the
browser shell or profile record format.

All storage calls require four-byte alignment and may not exceed 32 KiB.

## On-Storage Image

The image begins at offset zero and is 31,312 bytes:

| Region | Bytes |
| --- | ---: |
| Header | 32 |
| Settings and counters | 16 |
| 64 favorite records | 1,024 |
| 16 folder records | 128 |
| 128 history records | 1,024 |
| 32 recent-address offsets | 64 |
| Future reserved payload | 1,024 |
| Shared UTF-8 string pool | 28,000 |
| Unused SRAM capacity | 1,456 |

The header contains the `NPF1` magic, format version, generation, payload length,
payload CRC32, and reserved words. Invalid magic, incompatible version, invalid
length, malformed string offsets, or CRC mismatch creates a fresh in-memory profile
without crashing the browser.

Records store 16-bit string-pool offsets rather than fixed URL buffers. Before a
save, the profile compacts referenced strings to reclaim space from replaced history,
recent-address, or setting values.

## Save Policy

Favorites and settings save immediately because they are explicit user changes.
History and typed addresses mark the profile dirty and save after eight accumulated
changes. This avoids an SRAM write on every frame or every browser operation.

## Current Browser Integration

The profile supplies the startup homepage. A typed HTTP(S) address is recorded as a
canonical recent address, and after its page opens successfully it is recorded in
persistent history with the rendered page title. Temporary `browser://` routes are
not written to the profile.

## Browser v1.3B UI

The Start menu exposes persistent History, Recent Addresses, Favorites, and Add
Favorite. List views use D-Pad selection, A to open through the normal typed-address
route, and B/Start to return. Favorites use Z to request removal and require A to
confirm the destructive action.

Favorites default to folder ID zero (`Unfiled`). Flat folder records and assignment
are present in the profile API; folder editing is intentionally deferred so this
release can keep its existing browser/menu flow.

Only pages loaded from a typed HTTP(S) address currently retain a canonical URL in
the ROM. Link-token pages without a canonical URL remain browsable but show
`FAVORITE UNAVAILABLE` rather than persisting a temporary `browser://` route.
