#include "n64_accessory.h"

#include <string.h>

#include "hardware/flash.h"
#include "hardware/regs/addressmap.h"
#include "hardware/sync.h"
#include "n64_protocol.h"
#include "nice64_realtime_memops.h"
#include "pico/flash.h"
#include "pico/multicore.h"
#include "pico/time.h"
#include "transfer_pak_flash_layout.h"

#define PAK_SIZE 32768u
#define PAK_BLOCK_SIZE 32u
#define TRANSFER_ROM_CACHE_SIZE 65536u
#define GB_CART_SRAM_SIZE 32768u
#define TRANSFER_FLASH_SAFE_TIMEOUT_MS 1000u

typedef enum {
    GB_MAPPER_ROM = 0,
    GB_MAPPER_MBC1 = 1,
    GB_MAPPER_MBC3 = 3,
    GB_MAPPER_MBC5 = 5,
} gb_mapper_t;

static volatile n64_accessory_mode_t accessory_mode = N64_ACCESSORY_NONE;
static uint8_t pak_memory[PAK_SIZE];
static uint8_t transfer_rom_cache[TRANSFER_ROM_CACHE_SIZE];
static uint8_t gb_cart_sram[GB_CART_SRAM_SIZE];
static uint8_t transfer_flash_page[TRANSFER_ROM_FLASH_PAGE_SIZE];
static bool transfer_flash_page_dirty = false;
static uint32_t transfer_flash_page_base = 0xFFFFFFFFu;
static bool transfer_flash_loaded = false;
static uint32_t transfer_flash_rom_size = 0;
static transfer_flash_transaction_t transfer_flash_transaction;
static volatile bool transfer_flash_core1_lockout_ready = false;
static bool transfer_rom_loaded = false;
static bool transfer_enabled = false;
static uint8_t transfer_bank = 0;
static uint8_t transfer_access_mode = 0x80;
static uint8_t transfer_access_changed = 0x44;
static gb_mapper_t gb_cart_mapper = GB_MAPPER_MBC5;
static uint8_t gb_cart_type = 0x1B;
static uint32_t gb_cart_profile_rom_size = TRANSFER_ROM_CACHE_SIZE;
static uint32_t gb_cart_profile_ram_size = GB_CART_SRAM_SIZE;
static bool gb_cart_has_rtc = false;
static bool gb_cart_ram_enabled = false;
static uint16_t gb_cart_rom_bank = 1;
static uint8_t gb_cart_ram_bank = 0;
static uint8_t gb_cart_mbc1_bank_hi = 0;
static uint8_t gb_cart_mbc1_mode = 0;
static uint8_t gb_cart_mbc3_rtc_select = 0;
static uint8_t gb_cart_mbc3_rtc_regs[5] = {0};
static uint8_t gb_cart_mbc3_latch_pending = 0;
static bool rumble_active = false;
static uint32_t rumble_report_until_us = 0;

static gb_mapper_t gb_mapper_from_cart_type(uint8_t cart_type) {
    if (cart_type >= 0x01u && cart_type <= 0x03u) {
        return GB_MAPPER_MBC1;
    }

    if (cart_type >= 0x0Fu && cart_type <= 0x13u) {
        return GB_MAPPER_MBC3;
    }

    if (cart_type >= 0x19u && cart_type <= 0x1Eu) {
        return GB_MAPPER_MBC5;
    }

    return GB_MAPPER_ROM;
}

static void gb_cart_reset_mapper_state(void) {
    gb_cart_ram_enabled = false;
    gb_cart_rom_bank = 1;
    gb_cart_ram_bank = 0;
    gb_cart_mbc1_bank_hi = 0;
    gb_cart_mbc1_mode = 0;
    gb_cart_mbc3_rtc_select = 0;
    gb_cart_mbc3_latch_pending = 0;
}

static uint32_t gb_cart_sram_mask(void) {
    uint32_t size = gb_cart_profile_ram_size;
    if (size == 0 || size > GB_CART_SRAM_SIZE) {
        size = GB_CART_SRAM_SIZE;
    }

    return size - 1u;
}

static uint8_t n64_data_crc(const uint8_t *data) {
    uint8_t crc = 0;

    for (int i = 0; i < 32; ++i) {
        for (int bit = 7; bit >= 0; --bit) {
            const bool input_bit = (data[i] & (1u << bit)) != 0;
            const bool feedback = ((crc & 0x80u) != 0) ^ input_bit;
            crc <<= 1;
            if (feedback) {
                crc ^= 0x85u;
            }
        }
    }

    return crc;
}

void n64_accessory_init(void) {
    accessory_mode = N64_ACCESSORY_NONE;
    rumble_active = false;
    rumble_report_until_us = 0;
    transfer_rom_loaded = false;
    transfer_flash_loaded = false;
    transfer_flash_rom_size = 0;
    transfer_flash_transaction_reset(&transfer_flash_transaction);
    transfer_flash_page_dirty = false;
    transfer_flash_page_base = 0xFFFFFFFFu;
    transfer_enabled = false;
    transfer_bank = 0;
    transfer_access_mode = 0x80;
    transfer_access_changed = 0x44;
    gb_cart_mapper = GB_MAPPER_MBC5;
    gb_cart_type = 0x1B;
    gb_cart_profile_rom_size = TRANSFER_ROM_CACHE_SIZE;
    gb_cart_profile_ram_size = GB_CART_SRAM_SIZE;
    gb_cart_has_rtc = false;
    gb_cart_reset_mapper_state();
    memset(pak_memory, 0, sizeof(pak_memory));
    memset(transfer_rom_cache, 0xFF, sizeof(transfer_rom_cache));
    memset(gb_cart_sram, 0xFF, sizeof(gb_cart_sram));
    memset(transfer_flash_page, 0xFF, sizeof(transfer_flash_page));
}

n64_accessory_mode_t n64_accessory_mode(void) {
    const uint32_t save = save_and_disable_interrupts();
    const n64_accessory_mode_t mode = accessory_mode;
    restore_interrupts(save);
    return mode;
}

void n64_accessory_set_mode(n64_accessory_mode_t mode) {
    const uint32_t save = save_and_disable_interrupts();
    accessory_mode = mode;
    if (mode != N64_ACCESSORY_RUMBLE_PAK) {
        rumble_active = false;
        rumble_report_until_us = 0;
    }
    if (mode == N64_ACCESSORY_TRANSFER_PAK) {
        transfer_enabled = false;
        transfer_bank = 0;
        transfer_access_mode = transfer_rom_loaded ? 0x80 : 0x40;
        transfer_access_changed = transfer_rom_loaded ? 0x44 : 0x00;
    }
    restore_interrupts(save);
}

uint8_t __no_inline_not_in_flash_func(n64_accessory_identity_byte)(void) {
    return n64_accessory_mode() == N64_ACCESSORY_NONE
        ? N64_ID_NO_ACCESSORY_REAL_CONTROLLER
        : 0x01;
}

uint8_t __no_inline_not_in_flash_func(n64_accessory_identity_byte_irq)(void) {
    return accessory_mode == N64_ACCESSORY_NONE
        ? N64_ID_NO_ACCESSORY_REAL_CONTROLLER
        : 0x01;
}

uint16_t __no_inline_not_in_flash_func(n64_accessory_decode_address)(uint8_t hi, uint8_t lo) {
    return (uint16_t)((((uint16_t)hi << 8) | lo) & 0xFFE0u);
}

void n64_accessory_clear_memory(void) {
    const uint32_t save = save_and_disable_interrupts();
    memset(pak_memory, 0, sizeof(pak_memory));
    restore_interrupts(save);
}

void n64_accessory_write_block(uint16_t offset, const uint8_t data[32]) {
    offset = (uint16_t)(offset & (PAK_SIZE - 1u));
    const uint32_t save = save_and_disable_interrupts();
    for (uint32_t i = 0; i < PAK_BLOCK_SIZE; ++i) {
        pak_memory[(offset + i) & (PAK_SIZE - 1u)] = data[i];
    }
    restore_interrupts(save);
}

void n64_accessory_read_block(uint16_t offset, uint8_t out[32]) {
    offset = (uint16_t)(offset & (PAK_SIZE - 1u));
    const uint32_t save = save_and_disable_interrupts();
    for (uint32_t i = 0; i < PAK_BLOCK_SIZE; ++i) {
        out[i] = pak_memory[(offset + i) & (PAK_SIZE - 1u)];
    }
    restore_interrupts(save);
}

void n64_accessory_clear_transfer_rom(void) {
    const uint32_t save = save_and_disable_interrupts();
    memset(transfer_rom_cache, 0xFF, sizeof(transfer_rom_cache));
    transfer_rom_loaded = false;
    transfer_flash_loaded = false;
    transfer_flash_rom_size = 0;
    transfer_flash_transaction_reset(&transfer_flash_transaction);
    transfer_flash_page_dirty = false;
    transfer_flash_page_base = 0xFFFFFFFFu;
    transfer_enabled = false;
    transfer_bank = 0;
    transfer_access_mode = 0x40;
    transfer_access_changed = 0;
    gb_cart_reset_mapper_state();
    restore_interrupts(save);
}

void n64_accessory_set_transfer_profile(uint8_t cart_type, uint32_t rom_size, uint32_t ram_size, uint8_t flags) {
    const uint32_t save = save_and_disable_interrupts();
    gb_cart_type = cart_type;
    gb_cart_mapper = gb_mapper_from_cart_type(cart_type);
    gb_cart_profile_rom_size = rom_size > 0 ? rom_size : TRANSFER_ROM_CACHE_SIZE;
    gb_cart_profile_ram_size = ram_size > 0 ? ram_size : GB_CART_SRAM_SIZE;
    if (gb_cart_profile_ram_size > GB_CART_SRAM_SIZE) {
        gb_cart_profile_ram_size = GB_CART_SRAM_SIZE;
    }
    gb_cart_has_rtc = (flags & 0x01u) != 0;
    gb_cart_reset_mapper_state();
    memset(gb_cart_mbc3_rtc_regs, 0, sizeof(gb_cart_mbc3_rtc_regs));
    restore_interrupts(save);
}

bool n64_accessory_prepare_core1_flash_lockout(void) {
    if (get_core_num() != 1u) return false;
    transfer_flash_core1_lockout_ready = flash_safe_execute_core_init();
    return transfer_flash_core1_lockout_ready;
}

typedef struct {
    uint32_t offset;
    const uint8_t *page;
    bool erase_sector;
} transfer_flash_operation_t;

static void __not_in_flash_func(run_transfer_flash_operation)(void *opaque) {
    const transfer_flash_operation_t *operation = opaque;
    if (operation->erase_sector) {
        flash_range_erase(operation->offset, TRANSFER_ROM_FLASH_SECTOR_SIZE);
    }
    flash_range_program(operation->offset, operation->page,
                        TRANSFER_ROM_FLASH_PAGE_SIZE);
}

static transfer_flash_result_t perform_transfer_flash_operation(
    uint32_t logical_offset,
    const uint8_t page[TRANSFER_ROM_FLASH_PAGE_SIZE]) {
    if (!transfer_flash_core1_lockout_ready)
        return TRANSFER_FLASH_EXECUTION_FAILED;
    if (logical_offset > TRANSFER_ROM_FLASH_SIZE ||
        TRANSFER_ROM_FLASH_PAGE_SIZE > TRANSFER_ROM_FLASH_SIZE - logical_offset)
        return TRANSFER_FLASH_OUT_OF_PARTITION;
    transfer_flash_operation_t operation = {
        .offset = TRANSFER_ROM_FLASH_OFFSET + logical_offset,
        .page = page,
        .erase_sector =
            (logical_offset & (TRANSFER_ROM_FLASH_SECTOR_SIZE - 1u)) == 0u,
    };
    const int result = flash_safe_execute(run_transfer_flash_operation, &operation,
                                          TRANSFER_FLASH_SAFE_TIMEOUT_MS);
    return result == PICO_OK ? TRANSFER_FLASH_OK
                             : TRANSFER_FLASH_EXECUTION_FAILED;
}

static transfer_flash_result_t n64_accessory_flush_transfer_flash_page(void) {
    if (!transfer_flash_page_dirty || transfer_flash_page_base >= TRANSFER_ROM_FLASH_SIZE) {
        return transfer_flash_page_dirty ? TRANSFER_FLASH_OUT_OF_PARTITION
                                         : TRANSFER_FLASH_OK;
    }
    const transfer_flash_result_t result = perform_transfer_flash_operation(
        transfer_flash_page_base, transfer_flash_page);
    if (result != TRANSFER_FLASH_OK) {
        transfer_flash_transaction_fail(&transfer_flash_transaction);
        return result;
    }
    transfer_flash_page_dirty = false;
    transfer_flash_transaction_record_staged_page(&transfer_flash_transaction);
    return TRANSFER_FLASH_OK;
}

static uint32_t transfer_flash_crc32(const uint8_t *data, uint32_t length) {
    uint32_t crc = 0xFFFFFFFFu;
    for (uint32_t index = 0; index < length; ++index) {
        crc ^= data[index];
        for (unsigned bit = 0; bit < 8u; ++bit)
            crc = (crc >> 1) ^ (0xEDB88320u &
                (uint32_t)-(int32_t)(crc & 1u));
    }
    return ~crc;
}

transfer_flash_result_t n64_accessory_begin_transfer_flash(uint32_t rom_size) {
    const transfer_flash_result_t result = transfer_flash_transaction_begin(
        &transfer_flash_transaction, rom_size, TRANSFER_ROM_FLASH_SIZE);
    transfer_flash_loaded = false;
    transfer_rom_loaded = false;
    transfer_flash_rom_size = 0u;
    transfer_flash_page_dirty = false;
    transfer_flash_page_base = 0xFFFFFFFFu;
    memset(transfer_flash_page, 0xFF, sizeof(transfer_flash_page));
    if (result != TRANSFER_FLASH_OK) {
        return result;
    }
    transfer_flash_rom_size = rom_size;
    return TRANSFER_FLASH_OK;
}

transfer_flash_result_t n64_accessory_write_transfer_flash_block(uint32_t offset, const uint8_t data[32]) {
    transfer_flash_result_t result = transfer_flash_transaction_validate_block(
        &transfer_flash_transaction, offset, TRANSFER_ROM_FLASH_SIZE,
        TRANSFER_ROM_FLASH_PAGE_SIZE, PAK_BLOCK_SIZE);
    if (result != TRANSFER_FLASH_OK) return result;

    const uint32_t page_base = offset & ~(TRANSFER_ROM_FLASH_PAGE_SIZE - 1u);
    if (transfer_flash_page_base != page_base) {
        result = n64_accessory_flush_transfer_flash_page();
        if (result != TRANSFER_FLASH_OK) return result;
        transfer_flash_page_base = page_base;
        memset(transfer_flash_page, 0xFF, sizeof(transfer_flash_page));
    }

    const uint32_t page_offset = offset & (TRANSFER_ROM_FLASH_PAGE_SIZE - 1u);
    memcpy(&transfer_flash_page[page_offset], data, PAK_BLOCK_SIZE);
    transfer_flash_page_dirty = true;
    transfer_flash_transaction_record_block(&transfer_flash_transaction,
                                             offset, PAK_BLOCK_SIZE);
    return TRANSFER_FLASH_OK;
}

transfer_flash_result_t n64_accessory_write_transfer_flash_page(uint32_t offset, const uint8_t data[256]) {
    const uint32_t page_crc32 = transfer_flash_crc32(
        data, TRANSFER_ROM_FLASH_PAGE_SIZE);
    bool is_replay = false;
    transfer_flash_result_t result = transfer_flash_transaction_validate_page(
        &transfer_flash_transaction, offset, TRANSFER_ROM_FLASH_SIZE,
        TRANSFER_ROM_FLASH_PAGE_SIZE, page_crc32, &is_replay);
    if (result != TRANSFER_FLASH_OK) return result;
    if (is_replay) return TRANSFER_FLASH_OK;
    result = n64_accessory_flush_transfer_flash_page();
    if (result != TRANSFER_FLASH_OK) return result;
    result = perform_transfer_flash_operation(offset, data);
    if (result != TRANSFER_FLASH_OK) {
        transfer_flash_transaction_fail(&transfer_flash_transaction);
        return result;
    }
    transfer_flash_transaction_record_page(&transfer_flash_transaction, offset,
                                            TRANSFER_ROM_FLASH_PAGE_SIZE,
                                            page_crc32);
    return TRANSFER_FLASH_OK;
}

transfer_flash_result_t n64_accessory_commit_transfer_flash(void) {
    if (!transfer_flash_transaction.active)
        return TRANSFER_FLASH_NO_TRANSACTION;
    if (transfer_flash_transaction.failed)
        return TRANSFER_FLASH_TRANSACTION_FAILED;
    transfer_flash_result_t result = n64_accessory_flush_transfer_flash_page();
    if (result != TRANSFER_FLASH_OK) return result;
    result = transfer_flash_transaction_validate_commit(
        &transfer_flash_transaction, TRANSFER_ROM_FLASH_PAGE_SIZE);
    if (result != TRANSFER_FLASH_OK) return result;
    transfer_flash_loaded = transfer_flash_rom_size > 0;
    transfer_rom_loaded = transfer_flash_loaded;
    if (accessory_mode == N64_ACCESSORY_TRANSFER_PAK) {
        transfer_access_mode = transfer_flash_loaded ? 0x80 : 0x40;
        transfer_access_changed = transfer_flash_loaded ? 0x44 : 0;
    }
    transfer_flash_transaction_commit(&transfer_flash_transaction);
    return TRANSFER_FLASH_OK;
}

uint32_t n64_accessory_transfer_flash_capacity(void) {
    return TRANSFER_ROM_FLASH_SIZE;
}

transfer_flash_result_t n64_accessory_transfer_flash_crc(uint32_t *length,
                                                         uint32_t *crc32) {
    if (length == NULL || crc32 == NULL || !transfer_flash_loaded ||
        transfer_flash_rom_size == 0u || transfer_flash_transaction.active)
        return TRANSFER_FLASH_NOT_COMMITTED;
    const uint8_t *rom =
        (const uint8_t *)(XIP_BASE + TRANSFER_ROM_FLASH_OFFSET);
    *length = transfer_flash_rom_size;
    *crc32 = transfer_flash_crc32(rom, transfer_flash_rom_size);
    return TRANSFER_FLASH_OK;
}

void n64_accessory_write_transfer_rom_block(uint16_t offset, const uint8_t data[32]) {
    offset = (uint16_t)(offset & (TRANSFER_ROM_CACHE_SIZE - 1u));
    const uint32_t save = save_and_disable_interrupts();
    for (uint32_t i = 0; i < PAK_BLOCK_SIZE; ++i) {
        transfer_rom_cache[(offset + i) & (TRANSFER_ROM_CACHE_SIZE - 1u)] = data[i];
    }
    transfer_rom_loaded = true;
    if (accessory_mode == N64_ACCESSORY_TRANSFER_PAK) {
        transfer_access_mode = 0x80;
        transfer_access_changed = 0x44;
    }
    restore_interrupts(save);
}

static uint32_t gb_cart_rom_size(void) {
    if (transfer_flash_loaded && transfer_flash_rom_size > 0) {
        return transfer_flash_rom_size;
    }

    return gb_cart_profile_rom_size > 0 ? gb_cart_profile_rom_size : TRANSFER_ROM_CACHE_SIZE;
}

static uint8_t gb_cart_raw_rom_read(uint32_t offset) {
    if (transfer_flash_loaded && offset < transfer_flash_rom_size) {
        const uint8_t *rom = (const uint8_t *)(XIP_BASE + TRANSFER_ROM_FLASH_OFFSET);
        return rom[offset];
    }

    if (offset < TRANSFER_ROM_CACHE_SIZE) {
        return transfer_rom_cache[offset];
    }

    return 0xFF;
}

void n64_accessory_write_transfer_save_block(uint16_t offset, const uint8_t data[32]) {
    offset = (uint16_t)(offset & (GB_CART_SRAM_SIZE - 1u));
    const uint32_t save = save_and_disable_interrupts();
    for (uint32_t i = 0; i < PAK_BLOCK_SIZE; ++i) {
        gb_cart_sram[(offset + i) & (GB_CART_SRAM_SIZE - 1u)] = data[i];
    }
    restore_interrupts(save);
}

void n64_accessory_read_transfer_save_block(uint16_t offset, uint8_t out[32]) {
    offset = (uint16_t)(offset & (GB_CART_SRAM_SIZE - 1u));
    const uint32_t save = save_and_disable_interrupts();
    for (uint32_t i = 0; i < PAK_BLOCK_SIZE; ++i) {
        out[i] = gb_cart_sram[(offset + i) & (GB_CART_SRAM_SIZE - 1u)];
    }
    restore_interrupts(save);
}

static uint8_t gb_cart_read(uint16_t address) {
    const uint32_t rom_size = gb_cart_rom_size();
    if (address < 0x4000u) {
        uint32_t offset = address;
        if (gb_cart_mapper == GB_MAPPER_MBC1 && gb_cart_mbc1_mode != 0) {
            offset = ((uint32_t)(gb_cart_mbc1_bank_hi & 0x03u) << 19) + address;
        }
        return rom_size > 0 ? gb_cart_raw_rom_read(offset % rom_size) : 0xFF;
    }

    if (address < 0x8000u) {
        uint32_t bank = gb_cart_rom_bank;
        if (gb_cart_mapper == GB_MAPPER_MBC1) {
            bank = (uint32_t)(gb_cart_rom_bank & 0x1Fu) | ((uint32_t)(gb_cart_mbc1_bank_hi & 0x03u) << 5);
            if ((bank & 0x1Fu) == 0) {
                bank |= 1u;
            }
        } else if (gb_cart_mapper == GB_MAPPER_MBC3) {
            bank &= 0x7Fu;
            if (bank == 0) {
                bank = 1;
            }
        } else if (gb_cart_mapper == GB_MAPPER_MBC5) {
            bank &= 0x01FFu;
        } else {
            bank = 1;
        }
        uint32_t offset = (bank * 0x4000u) + (uint32_t)(address - 0x4000u);
        return rom_size > 0 ? gb_cart_raw_rom_read(offset % rom_size) : 0xFF;
    }

    if (address >= 0xA000u && address < 0xC000u) {
        if (!gb_cart_ram_enabled) {
            return 0xFF;
        }

        if (gb_cart_mapper == GB_MAPPER_MBC3 && gb_cart_mbc3_rtc_select >= 0x08u && gb_cart_mbc3_rtc_select <= 0x0Cu) {
            return gb_cart_has_rtc ? gb_cart_mbc3_rtc_regs[gb_cart_mbc3_rtc_select - 0x08u] : 0xFF;
        }

        uint32_t bank = gb_cart_ram_bank;
        if (gb_cart_mapper == GB_MAPPER_MBC1 && gb_cart_mbc1_mode == 0) {
            bank = 0;
        }
        uint32_t offset = ((bank & 0x0Fu) * 0x2000u) + (uint32_t)(address - 0xA000u);
        return gb_cart_sram[offset & gb_cart_sram_mask()];
    }

    return 0xFF;
}

static void gb_cart_write(uint16_t address, uint8_t value) {
    if (address < 0x2000u) {
        gb_cart_ram_enabled = (value & 0x0Fu) == 0x0Au;
    } else if (address < 0x4000u) {
        if (gb_cart_mapper == GB_MAPPER_MBC5 && address >= 0x3000u) {
            gb_cart_rom_bank = (uint16_t)((gb_cart_rom_bank & 0x00FFu) | ((uint16_t)(value & 0x01u) << 8));
        } else if (gb_cart_mapper == GB_MAPPER_MBC5) {
            gb_cart_rom_bank = (uint16_t)((gb_cart_rom_bank & 0x0100u) | value);
        } else if (gb_cart_mapper == GB_MAPPER_MBC3) {
            gb_cart_rom_bank = value & 0x7Fu;
            if (gb_cart_rom_bank == 0) {
                gb_cart_rom_bank = 1;
            }
        } else if (gb_cart_mapper == GB_MAPPER_MBC1) {
            gb_cart_rom_bank = value & 0x1Fu;
            if ((gb_cart_rom_bank & 0x1Fu) == 0) {
                gb_cart_rom_bank = 1;
            }
        }
    } else if (address < 0x6000u) {
        if (gb_cart_mapper == GB_MAPPER_MBC1) {
            gb_cart_mbc1_bank_hi = value & 0x03u;
            gb_cart_ram_bank = value & 0x03u;
        } else if (gb_cart_mapper == GB_MAPPER_MBC3) {
            if (value <= 0x03u) {
                gb_cart_ram_bank = value;
                gb_cart_mbc3_rtc_select = 0;
            } else if (value >= 0x08u && value <= 0x0Cu) {
                gb_cart_mbc3_rtc_select = value;
            }
        } else if (gb_cart_mapper == GB_MAPPER_MBC5) {
            gb_cart_ram_bank = (uint8_t)(value & 0x0Fu);
        }
    } else if (address < 0x8000u) {
        if (gb_cart_mapper == GB_MAPPER_MBC1) {
            gb_cart_mbc1_mode = value & 0x01u;
        } else if (gb_cart_mapper == GB_MAPPER_MBC3) {
            if (gb_cart_mbc3_latch_pending == 0 && value == 0) {
                gb_cart_mbc3_latch_pending = 1;
            } else if (gb_cart_mbc3_latch_pending == 1 && value == 1) {
                gb_cart_mbc3_latch_pending = 0;
            } else {
                gb_cart_mbc3_latch_pending = 0;
            }
        }
    } else if (address >= 0xA000u && address < 0xC000u && gb_cart_ram_enabled) {
        if (gb_cart_mapper == GB_MAPPER_MBC3 && gb_cart_mbc3_rtc_select >= 0x08u && gb_cart_mbc3_rtc_select <= 0x0Cu) {
            if (gb_cart_has_rtc) {
                gb_cart_mbc3_rtc_regs[gb_cart_mbc3_rtc_select - 0x08u] = value;
            }
        } else {
            uint32_t bank = gb_cart_ram_bank;
            if (gb_cart_mapper == GB_MAPPER_MBC1 && gb_cart_mbc1_mode == 0) {
                bank = 0;
            }
            uint32_t offset = ((bank & 0x0Fu) * 0x2000u) + (uint32_t)(address - 0xA000u);
            gb_cart_sram[offset & gb_cart_sram_mask()] = value;
        }
    }
}

static void n64_transfer_read(uint16_t address, uint8_t out[32]) {
    nice64_realtime_fill(out, 0, 32);

    if (address < 0x2000u) {
        nice64_realtime_fill(out, transfer_enabled ? 0x84 : 0x00, 32);
        return;
    }

    if (address >= 0x2000u && address <= 0x2FFFu) {
        nice64_realtime_fill(out, transfer_enabled ? (uint8_t)(transfer_bank <= 3u ? transfer_bank : 0u) : 0x00, 32);
        return;
    }

    if (address >= 0x3000u && address <= 0x3FFFu) {
        const uint8_t mode = transfer_rom_loaded ? transfer_access_mode : 0x40;
        nice64_realtime_fill(out, transfer_enabled ? (uint8_t)(mode | transfer_access_changed) : 0x00, 32);
        if (transfer_enabled) {
            transfer_access_changed = 0;
        }
        return;
    }

    if (address >= 0x8000u && address <= 0x8FFFu) {
        nice64_realtime_fill(out, transfer_enabled ? 0x84 : 0x00, 32);
        return;
    }

    if (address >= 0xA000u && address <= 0xAFFFu) {
        nice64_realtime_fill(out, transfer_enabled ? (uint8_t)(transfer_bank <= 3u ? transfer_bank : 0u) : 0x00, 32);
        return;
    }

    if (address >= 0xB000u && address <= 0xBFFFu) {
        const uint8_t mode = transfer_rom_loaded ? transfer_access_mode : 0x40;
        nice64_realtime_fill(out, transfer_enabled ? (uint8_t)(mode | transfer_access_changed) : 0x00, 32);
        if (transfer_enabled) {
            transfer_access_changed = 0;
        }
        return;
    }

    if (!transfer_enabled || !transfer_rom_loaded || address < 0xC000u) {
        return;
    }

    const uint32_t base = ((uint32_t)transfer_bank * 0x4000u) + (uint32_t)(address & 0x3FFFu);
    for (uint32_t i = 0; i < 32u; ++i) {
        out[i] = gb_cart_read((uint16_t)((base + i) & 0xFFFFu));
    }
}

static void n64_transfer_write(uint16_t address, const uint8_t data[32]) {
    if (address >= 0x8000u && address <= 0x8FFFu) {
        if (data[0] == 0xFEu) {
            transfer_enabled = false;
        } else if (data[0] == 0x84u) {
            transfer_enabled = true;
        }
        return;
    }

    if (!transfer_enabled) {
        return;
    }

    if (address >= 0xA000u && address <= 0xAFFFu) {
        transfer_bank = data[0] <= 3u ? data[0] : 0u;
    } else if (address >= 0xB000u && address <= 0xBFFFu) {
        transfer_access_mode = (data[0] & 1u) == 0 ? 0x80 : 0x89;
        transfer_access_changed = 0x04;
    } else if (address >= 0xC000u) {
        const uint32_t base = ((uint32_t)transfer_bank * 0x4000u) + (uint32_t)(address & 0x3FFFu);
        for (uint32_t i = 0; i < 32u; ++i) {
            gb_cart_write((uint16_t)((base + i) & 0xFFFFu), data[i]);
        }
    }
}

bool __no_inline_not_in_flash_func(n64_accessory_handle_read)(uint16_t address, uint8_t out[33]) {
    const n64_accessory_mode_t mode = n64_accessory_mode();

    if (mode == N64_ACCESSORY_MEMORY_PAK) {
        n64_accessory_read_block((uint16_t)(address & (PAK_SIZE - 1u)), out);
    } else if (mode == N64_ACCESSORY_RUMBLE_PAK && address == 0x8000u) {
        nice64_realtime_fill(out, 0x80, 32);
    } else if (mode == N64_ACCESSORY_TRANSFER_PAK) {
        n64_transfer_read(address, out);
    } else {
        nice64_realtime_fill(out, 0, 32);
    }

    out[32] = n64_data_crc(out);
    return true;
}

bool __no_inline_not_in_flash_func(n64_accessory_handle_write)(uint16_t address, const uint8_t data[32], uint8_t *crc_out) {
    const n64_accessory_mode_t mode = n64_accessory_mode();

    if (mode == N64_ACCESSORY_MEMORY_PAK) {
        n64_accessory_write_block((uint16_t)(address & (PAK_SIZE - 1u)), data);
    } else if (mode == N64_ACCESSORY_RUMBLE_PAK && address == 0xC000u) {
        const bool active = data[0] != 0;
        const uint32_t report_until = active
            ? (uint32_t)(time_us_32() + 350000u)
            : rumble_report_until_us;
        const uint32_t save = save_and_disable_interrupts();
        rumble_active = active;
        rumble_report_until_us = report_until;
        restore_interrupts(save);
    } else if (mode == N64_ACCESSORY_TRANSFER_PAK) {
        n64_transfer_write(address, data);
    }

    if (crc_out) {
        *crc_out = n64_data_crc(data);
    }

    return true;
}

bool n64_accessory_rumble_active(void) {
    const uint32_t now = time_us_32();
    const uint32_t save = save_and_disable_interrupts();
    const bool active = rumble_active || (int32_t)(rumble_report_until_us - now) > 0;
    restore_interrupts(save);
    return active;
}
