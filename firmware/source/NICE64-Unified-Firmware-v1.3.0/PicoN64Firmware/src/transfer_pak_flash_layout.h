#pragma once

#include <stdint.h>

#include "hardware/flash.h"
#include "pico/platform.h"

#ifndef NICE64_TRANSFER_ROM_FLASH_OFFSET_BYTES
#error "NICE64_TRANSFER_ROM_FLASH_OFFSET_BYTES must be supplied by the build"
#endif

#define TRANSFER_ROM_FLASH_OFFSET NICE64_TRANSFER_ROM_FLASH_OFFSET_BYTES
#if PICO_RP2350
#define TRANSFER_ROM_FLASH_SIZE (2u * 1024u * 1024u)
#else
#define TRANSFER_ROM_FLASH_SIZE (1024u * 1024u)
#endif
#define TRANSFER_ROM_FLASH_END \
    (TRANSFER_ROM_FLASH_OFFSET + TRANSFER_ROM_FLASH_SIZE)
#define TRANSFER_ROM_FLASH_PAGE_SIZE FLASH_PAGE_SIZE
#define TRANSFER_ROM_FLASH_SECTOR_SIZE FLASH_SECTOR_SIZE

_Static_assert(TRANSFER_ROM_FLASH_OFFSET <= UINT32_MAX - TRANSFER_ROM_FLASH_SIZE,
               "Transfer Pak flash partition arithmetic wraps");
_Static_assert((TRANSFER_ROM_FLASH_OFFSET % TRANSFER_ROM_FLASH_SECTOR_SIZE) == 0u,
               "Transfer Pak flash start is not sector aligned");
_Static_assert((TRANSFER_ROM_FLASH_SIZE % TRANSFER_ROM_FLASH_SECTOR_SIZE) == 0u,
               "Transfer Pak flash size is not sector aligned");
_Static_assert(TRANSFER_ROM_FLASH_END <= PICO_FLASH_SIZE_BYTES,
               "Transfer Pak flash partition exceeds physical flash");
