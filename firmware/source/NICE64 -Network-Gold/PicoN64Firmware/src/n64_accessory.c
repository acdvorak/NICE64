#include "n64_accessory.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "hardware/flash.h"
#include "hardware/regs/addressmap.h"
#include "hardware/sync.h"
#include "n64_protocol.h"
#include "pico/time.h"

#define PAK_SIZE 32768u
#define PAK_BLOCK_SIZE 32u
#define TRANSFER_ROM_CACHE_SIZE 65536u
#define TRANSFER_ROM_FLASH_OFFSET (1024u * 1024u)
#if PICO_RP2350
#define TRANSFER_ROM_FLASH_SIZE (2u * 1024u * 1024u)
#else
#define TRANSFER_ROM_FLASH_SIZE (1024u * 1024u)
#endif
#define TRANSFER_ROM_FLASH_PAGE_SIZE 256u
#define TRANSFER_ROM_FLASH_SECTOR_SIZE 4096u
#define GB_CART_SRAM_SIZE 32768u
#define NETWORK_PAK_ADDR_COMMAND 0x8000u
#define NETWORK_PAK_ADDR_RESPONSE 0x8020u
#define NETWORK_PAK_MAGIC_HI 0x4Eu
#define NETWORK_PAK_MAGIC_LO 0x50u
#define NETWORK_PAK_VERSION 0x01u
#define NETWORK_PAK_MAX_PAYLOAD 23u
#define NETWORK_PAK_CMD_PING 0x01u
#define NETWORK_PAK_CMD_GET_STATUS 0x02u
#define NETWORK_PAK_CMD_READ_CHUNK 0x03u
#define NETWORK_PAK_CMD_WRITE_CHUNK 0x04u
#define NETWORK_PAK_CMD_ACK 0x05u
#define NETWORK_PAK_CMD_RESET 0x06u
#define NETWORK_PAK_CMD_PING_BRIDGE 0x10u
#define NETWORK_PAK_CMD_GET_BRIDGE_STATUS 0x11u
#define NETWORK_PAK_CMD_GET_BRIDGE_TIME 0x12u
#define NETWORK_PAK_CMD_GET_BRIDGE_MESSAGE 0x13u
#define NETWORK_PAK_CMD_SEND_BRIDGE_MESSAGE 0x14u
#define NETWORK_PAK_CMD_OPEN_STREAM 0x20u
#define NETWORK_PAK_CMD_GET_STREAM_INFO 0x21u
#define NETWORK_PAK_CMD_READ_STREAM_CHUNK 0x22u
#define NETWORK_PAK_CMD_WRITE_STREAM_CHUNK 0x23u
#define NETWORK_PAK_CMD_CLOSE_STREAM 0x24u
#define NETWORK_PAK_CMD_PONG 0x81u
#define NETWORK_PAK_CMD_STATUS 0x82u
#define NETWORK_PAK_CMD_CHUNK 0x83u
#define NETWORK_PAK_CMD_BRIDGE_RESPONSE 0x90u
#define NETWORK_PAK_CMD_STREAM_OPENED 0xA0u
#define NETWORK_PAK_CMD_STREAM_INFO 0xA1u
#define NETWORK_PAK_CMD_STREAM_CHUNK 0xA2u
#define NETWORK_PAK_CMD_STREAM_CLOSED 0xA3u
#define NETWORK_PAK_CMD_STREAM_WRITE_ACK 0xA4u
#define NETWORK_PAK_CMD_ERROR 0xFFu
#define NETWORK_PAK_BRIDGE_TIMEOUT_US 1000000u

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
static uint8_t network_pak_response[PAK_BLOCK_SIZE];
static n64_network_pak_debug_t network_pak_debug;
static bool network_pak_bridge_request_pending;
static bool network_pak_bridge_request_sent;
static uint8_t network_pak_bridge_command;
static uint8_t network_pak_bridge_sequence;
static uint8_t network_pak_bridge_payload_length;
static uint8_t network_pak_bridge_payload[NETWORK_PAK_MAX_PAYLOAD + 1u];
static uint32_t network_pak_bridge_sent_us;

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

static uint16_t network_pak_checksum(const uint8_t packet[32]) {
    uint16_t checksum = 0;
    const uint8_t payload_length = packet[6] <= NETWORK_PAK_MAX_PAYLOAD ? packet[6] : NETWORK_PAK_MAX_PAYLOAD;

    for (uint32_t i = 0; i < 7u; ++i) {
        checksum = (uint16_t)(checksum + packet[i]);
    }

    for (uint8_t i = 0; i < payload_length; ++i) {
        checksum = (uint16_t)(checksum + packet[7u + i]);
    }

    return (uint16_t)~checksum;
}

static void network_pak_make_packet(uint8_t command, uint8_t sequence, const void *payload, uint8_t payload_length) {
    if (payload_length > NETWORK_PAK_MAX_PAYLOAD) {
        payload_length = NETWORK_PAK_MAX_PAYLOAD;
    }

    memset(network_pak_response, 0, sizeof(network_pak_response));
    network_pak_response[0] = NETWORK_PAK_MAGIC_HI;
    network_pak_response[1] = NETWORK_PAK_MAGIC_LO;
    network_pak_response[2] = NETWORK_PAK_VERSION;
    network_pak_response[3] = command;
    network_pak_response[4] = 0;
    network_pak_response[5] = sequence;
    network_pak_response[6] = payload_length;

    if (payload != NULL && payload_length > 0) {
        memcpy(&network_pak_response[7], payload, payload_length);
    }

    const uint16_t checksum = network_pak_checksum(network_pak_response);
    network_pak_response[30] = (uint8_t)(checksum >> 8);
    network_pak_response[31] = (uint8_t)(checksum & 0xFFu);
}

static bool network_pak_request_valid(const uint8_t data[32]) {
    if (data[0] != NETWORK_PAK_MAGIC_HI || data[1] != NETWORK_PAK_MAGIC_LO) {
        return false;
    }

    if (data[2] != NETWORK_PAK_VERSION || data[6] > NETWORK_PAK_MAX_PAYLOAD) {
        return false;
    }

    const uint16_t expected = network_pak_checksum(data);
    const uint16_t actual = (uint16_t)(((uint16_t)data[30] << 8) | data[31]);
    return expected == actual;
}

static const char *network_pak_bridge_command_name(uint8_t command) {
    switch (command) {
    case NETWORK_PAK_CMD_PING_BRIDGE: return "PING_BRIDGE";
    case NETWORK_PAK_CMD_GET_BRIDGE_STATUS: return "GET_BRIDGE_STATUS";
    case NETWORK_PAK_CMD_GET_BRIDGE_TIME: return "GET_BRIDGE_TIME";
    case NETWORK_PAK_CMD_GET_BRIDGE_MESSAGE: return "GET_BRIDGE_MESSAGE";
    case NETWORK_PAK_CMD_SEND_BRIDGE_MESSAGE: return "SEND_BRIDGE_MESSAGE";
    case NETWORK_PAK_CMD_OPEN_STREAM: return "OPEN_STREAM";
    case NETWORK_PAK_CMD_GET_STREAM_INFO: return "GET_STREAM_INFO";
    case NETWORK_PAK_CMD_READ_STREAM_CHUNK: return "READ_STREAM_CHUNK";
    case NETWORK_PAK_CMD_WRITE_STREAM_CHUNK: return "WRITE_STREAM_CHUNK";
    case NETWORK_PAK_CMD_CLOSE_STREAM: return "CLOSE_STREAM";
    default: return "UNKNOWN";
    }
}

static bool network_pak_is_stream_command(uint8_t command) {
    return command >= NETWORK_PAK_CMD_OPEN_STREAM && command <= NETWORK_PAK_CMD_CLOSE_STREAM;
}

static int network_pak_hex_nibble(char value) {
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'a' && value <= 'f') return value - 'a' + 10;
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    return -1;
}

static bool network_pak_decode_hex(const char *text, uint8_t *output, uint8_t *output_length) {
    const size_t text_length = strlen(text);
    if ((text_length & 1u) != 0 || text_length > NETWORK_PAK_MAX_PAYLOAD * 2u) {
        return false;
    }

    for (size_t i = 0; i < text_length; i += 2u) {
        const int high = network_pak_hex_nibble(text[i]);
        const int low = network_pak_hex_nibble(text[i + 1u]);
        if (high < 0 || low < 0) {
            return false;
        }
        output[i / 2u] = (uint8_t)((high << 4) | low);
    }
    *output_length = (uint8_t)(text_length / 2u);
    return true;
}

static bool network_pak_route_local(const uint8_t data[32]) {
    const uint8_t command = data[3];
    const uint8_t sequence = data[5];

    switch (command) {
    case NETWORK_PAK_CMD_PING:
        network_pak_make_packet(NETWORK_PAK_CMD_PONG, sequence, "PONG", 4);
        return true;

    case NETWORK_PAK_CMD_GET_STATUS:
        network_pak_make_packet(NETWORK_PAK_CMD_STATUS, sequence, "HOST_READY NIC=PICO", 19);
        return true;

    case NETWORK_PAK_CMD_READ_CHUNK:
        network_pak_make_packet(NETWORK_PAK_CMD_CHUNK, sequence, "hello from pico", 15);
        return true;

    case NETWORK_PAK_CMD_WRITE_CHUNK:
        network_pak_make_packet(NETWORK_PAK_CMD_ACK, sequence, "ACK", 3);
        return true;

    case NETWORK_PAK_CMD_RESET:
        network_pak_make_packet(NETWORK_PAK_CMD_ACK, sequence, "RESET", 5);
        return true;

    default:
        return false;
    }
}

static bool network_pak_route_bridge(const uint8_t data[32]) {
    const uint8_t command = data[3];
    const uint8_t sequence = data[5];
    const uint8_t payload_length = data[6];

    const bool layer1_command =
        command >= NETWORK_PAK_CMD_PING_BRIDGE && command <= NETWORK_PAK_CMD_SEND_BRIDGE_MESSAGE;
    if (!layer1_command && !network_pak_is_stream_command(command)) {
        return false;
    }

    network_pak_debug.last_bridge_cmd = command;
    if (!network_pak_debug.bridge_connected) {
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "BRIDGE_OFFLINE");
        network_pak_make_packet(NETWORK_PAK_CMD_ERROR, sequence, "BRIDGE_OFFLINE", 14);
        return true;
    }

    if (network_pak_bridge_request_pending) {
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "BRIDGE_BUSY");
        network_pak_make_packet(NETWORK_PAK_CMD_ERROR, sequence, "BRIDGE_BUSY", 11);
        return true;
    }

    network_pak_bridge_command = command;
    network_pak_bridge_sequence = sequence;
    network_pak_bridge_payload_length = payload_length;
    memset(network_pak_bridge_payload, 0, sizeof(network_pak_bridge_payload));
    memcpy(network_pak_bridge_payload, &data[7], payload_length);
    network_pak_bridge_request_pending = true;
    network_pak_bridge_request_sent = false;
    snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "PENDING");
    network_pak_make_packet(NETWORK_PAK_CMD_STATUS, sequence, "BRIDGE_PENDING", 14);
    return true;
}

static void network_pak_handle_request(const uint8_t data[32]) {
    const uint8_t sequence = data[5];

    if (!network_pak_request_valid(data)) {
        network_pak_make_packet(NETWORK_PAK_CMD_ERROR, sequence, "BAD_PACKET", 10);
        return;
    }

    if (network_pak_route_local(data) || network_pak_route_bridge(data)) {
        return;
    }

    network_pak_make_packet(NETWORK_PAK_CMD_ERROR, sequence, "UNSUPPORTED", 11);
}

static void network_pak_init_response(void) {
    network_pak_make_packet(NETWORK_PAK_CMD_STATUS, 0, "HOST_READY NIC=PICO", 19);
}

void n64_accessory_init(void) {
    accessory_mode = N64_ACCESSORY_NONE;
    rumble_active = false;
    rumble_report_until_us = 0;
    transfer_rom_loaded = false;
    transfer_flash_loaded = false;
    transfer_flash_rom_size = 0;
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
    memset(&network_pak_debug, 0, sizeof(network_pak_debug));
    network_pak_bridge_request_pending = false;
    network_pak_bridge_request_sent = false;
    network_pak_bridge_command = 0;
    network_pak_bridge_sequence = 0;
    network_pak_bridge_payload_length = 0;
    network_pak_bridge_sent_us = 0;
    snprintf(network_pak_debug.last_bridge_rsp, sizeof(network_pak_debug.last_bridge_rsp), "NONE");
    snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "OFFLINE");
    network_pak_init_response();
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
    if (mode == N64_ACCESSORY_NETWORK_PAK) {
        network_pak_init_response();
    }
    restore_interrupts(save);
}

uint8_t n64_accessory_identity_byte(void) {
    return n64_accessory_mode() == N64_ACCESSORY_NONE
        ? N64_ID_NO_ACCESSORY_REAL_CONTROLLER
        : 0x01;
}

uint8_t n64_accessory_identity_byte_irq(void) {
    return accessory_mode == N64_ACCESSORY_NONE
        ? N64_ID_NO_ACCESSORY_REAL_CONTROLLER
        : 0x01;
}

uint16_t n64_accessory_decode_address(uint8_t hi, uint8_t lo) {
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

static void n64_accessory_flush_transfer_flash_page(void) {
    if (!transfer_flash_page_dirty || transfer_flash_page_base >= TRANSFER_ROM_FLASH_SIZE) {
        return;
    }

    const uint32_t save = save_and_disable_interrupts();
    if ((transfer_flash_page_base & (TRANSFER_ROM_FLASH_SECTOR_SIZE - 1u)) == 0) {
        flash_range_erase(TRANSFER_ROM_FLASH_OFFSET + transfer_flash_page_base,
                          TRANSFER_ROM_FLASH_SECTOR_SIZE);
    }
    flash_range_program(TRANSFER_ROM_FLASH_OFFSET + transfer_flash_page_base,
                        transfer_flash_page,
                        TRANSFER_ROM_FLASH_PAGE_SIZE);
    restore_interrupts(save);
    transfer_flash_page_dirty = false;
}

void n64_accessory_begin_transfer_flash(uint32_t rom_size) {
    if (rom_size > TRANSFER_ROM_FLASH_SIZE) {
        rom_size = TRANSFER_ROM_FLASH_SIZE;
    }

    transfer_flash_rom_size = rom_size;
    transfer_flash_loaded = false;
    transfer_flash_page_dirty = false;
    transfer_flash_page_base = 0xFFFFFFFFu;
    memset(transfer_flash_page, 0xFF, sizeof(transfer_flash_page));
}

void n64_accessory_write_transfer_flash_block(uint32_t offset, const uint8_t data[32]) {
    if (offset >= TRANSFER_ROM_FLASH_SIZE) {
        return;
    }

    const uint32_t page_base = offset & ~(TRANSFER_ROM_FLASH_PAGE_SIZE - 1u);
    if (transfer_flash_page_base != page_base) {
        n64_accessory_flush_transfer_flash_page();
        transfer_flash_page_base = page_base;
        memset(transfer_flash_page, 0xFF, sizeof(transfer_flash_page));
    }

    const uint32_t page_offset = offset & (TRANSFER_ROM_FLASH_PAGE_SIZE - 1u);
    memcpy(&transfer_flash_page[page_offset], data, PAK_BLOCK_SIZE);
    transfer_flash_page_dirty = true;
}

void n64_accessory_write_transfer_flash_page(uint32_t offset, const uint8_t data[256]) {
    if (offset >= TRANSFER_ROM_FLASH_SIZE || (offset & (TRANSFER_ROM_FLASH_PAGE_SIZE - 1u)) != 0) {
        return;
    }

    n64_accessory_flush_transfer_flash_page();
    const uint32_t save = save_and_disable_interrupts();
    if ((offset & (TRANSFER_ROM_FLASH_SECTOR_SIZE - 1u)) == 0) {
        flash_range_erase(TRANSFER_ROM_FLASH_OFFSET + offset, TRANSFER_ROM_FLASH_SECTOR_SIZE);
    }
    flash_range_program(TRANSFER_ROM_FLASH_OFFSET + offset,
                        data,
                        TRANSFER_ROM_FLASH_PAGE_SIZE);
    restore_interrupts(save);
}

void n64_accessory_commit_transfer_flash(void) {
    n64_accessory_flush_transfer_flash_page();
    transfer_flash_loaded = transfer_flash_rom_size > 0;
    transfer_rom_loaded = transfer_flash_loaded;
    if (accessory_mode == N64_ACCESSORY_TRANSFER_PAK) {
        transfer_access_mode = transfer_flash_loaded ? 0x80 : 0x40;
        transfer_access_changed = transfer_flash_loaded ? 0x44 : 0;
    }
}

uint32_t n64_accessory_transfer_flash_capacity(void) {
    return TRANSFER_ROM_FLASH_SIZE;
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
    memset(out, 0, 32);

    if (address < 0x2000u) {
        memset(out, transfer_enabled ? 0x84 : 0x00, 32);
        return;
    }

    if (address >= 0x2000u && address <= 0x2FFFu) {
        memset(out, transfer_enabled ? (uint8_t)(transfer_bank <= 3u ? transfer_bank : 0u) : 0x00, 32);
        return;
    }

    if (address >= 0x3000u && address <= 0x3FFFu) {
        const uint8_t mode = transfer_rom_loaded ? transfer_access_mode : 0x40;
        memset(out, transfer_enabled ? (uint8_t)(mode | transfer_access_changed) : 0x00, 32);
        if (transfer_enabled) {
            transfer_access_changed = 0;
        }
        return;
    }

    if (address >= 0x8000u && address <= 0x8FFFu) {
        memset(out, transfer_enabled ? 0x84 : 0x00, 32);
        return;
    }

    if (address >= 0xA000u && address <= 0xAFFFu) {
        memset(out, transfer_enabled ? (uint8_t)(transfer_bank <= 3u ? transfer_bank : 0u) : 0x00, 32);
        return;
    }

    if (address >= 0xB000u && address <= 0xBFFFu) {
        const uint8_t mode = transfer_rom_loaded ? transfer_access_mode : 0x40;
        memset(out, transfer_enabled ? (uint8_t)(mode | transfer_access_changed) : 0x00, 32);
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

bool n64_accessory_handle_read(uint16_t address, uint8_t out[33]) {
    const n64_accessory_mode_t mode = n64_accessory_mode();

    if (mode == N64_ACCESSORY_MEMORY_PAK) {
        n64_accessory_read_block((uint16_t)(address & (PAK_SIZE - 1u)), out);
    } else if (mode == N64_ACCESSORY_RUMBLE_PAK && address == 0x8000u) {
        memset(out, 0x80, 32);
    } else if (mode == N64_ACCESSORY_TRANSFER_PAK) {
        n64_transfer_read(address, out);
    } else if (mode == N64_ACCESSORY_NETWORK_PAK) {
        const uint32_t save = save_and_disable_interrupts();
        network_pak_debug.read_count++;
        network_pak_debug.last_read_address = address;
        memcpy(network_pak_debug.last_response, network_pak_response, sizeof(network_pak_debug.last_response));
        memcpy(out, network_pak_response, 32);
        memcpy(network_pak_debug.served_read, out, sizeof(network_pak_debug.served_read));
        restore_interrupts(save);
    } else {
        memset(out, 0, 32);
    }

    out[32] = n64_data_crc(out);
    return true;
}

bool n64_accessory_handle_write(uint16_t address, const uint8_t data[32], uint8_t *crc_out) {
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
    } else if (mode == N64_ACCESSORY_NETWORK_PAK) {
        const uint32_t save = save_and_disable_interrupts();
        network_pak_debug.write_count++;
        network_pak_debug.last_write_address = address;
        memcpy(network_pak_debug.last_request, data, sizeof(network_pak_debug.last_request));
        network_pak_handle_request(data);
        memcpy(network_pak_debug.last_response, network_pak_response, sizeof(network_pak_debug.last_response));
        restore_interrupts(save);
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

void n64_accessory_get_network_debug(n64_network_pak_debug_t *debug) {
    if (!debug) {
        return;
    }

    const uint32_t save = save_and_disable_interrupts();
    *debug = network_pak_debug;
    restore_interrupts(save);
}

bool n64_accessory_handle_network_bridge_serial_line(const char *line) {
    if (line == NULL) {
        return false;
    }

    if (strcmp(line, "NETPAK_HELLO|1") == 0) {
        const uint32_t save = save_and_disable_interrupts();
        network_pak_debug.bridge_connected = true;
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "NONE");
        restore_interrupts(save);
        puts("NETPAK_HELLO_ACK|1");
        return true;
    }

    if (strcmp(line, "NETPAK_BYE") == 0) {
        const uint32_t save = save_and_disable_interrupts();
        network_pak_debug.bridge_connected = false;
        network_pak_bridge_request_pending = false;
        network_pak_bridge_request_sent = false;
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "OFFLINE");
        restore_interrupts(save);
        puts("NETPAK_BYE_ACK");
        return true;
    }

    if (strncmp(line, "NETPAK_RSP_HEX|", 15) == 0) {
        char copy[128];
        snprintf(copy, sizeof(copy), "%s", line + 15);
        char *sequence_text = copy;
        char *status_text = strchr(sequence_text, '|');
        if (status_text == NULL) return true;
        *status_text++ = '\0';
        char *command_text = strchr(status_text, '|');
        if (command_text == NULL) return true;
        *command_text++ = '\0';
        char *payload_text = strchr(command_text, '|');
        if (payload_text == NULL) return true;
        *payload_text++ = '\0';

        const unsigned long parsed_sequence = strtoul(sequence_text, NULL, 10);
        const unsigned long parsed_command = strtoul(command_text, NULL, 16);
        uint8_t payload[NETWORK_PAK_MAX_PAYLOAD];
        uint8_t payload_length = 0;
        memset(payload, 0, sizeof(payload));
        const bool payload_valid = network_pak_decode_hex(payload_text, payload, &payload_length);

        const uint32_t save = save_and_disable_interrupts();
        if (!network_pak_bridge_request_pending || parsed_sequence != network_pak_bridge_sequence) {
            snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "SEQ_MISMATCH");
            restore_interrupts(save);
            return true;
        }

        if (!payload_valid || parsed_command > 0xFFu) {
            network_pak_make_packet(NETWORK_PAK_CMD_ERROR, network_pak_bridge_sequence, "BRIDGE_BAD_HEX", 14);
            snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "BRIDGE_BAD_HEX");
        } else if (strcmp(status_text, "OK") == 0) {
            network_pak_make_packet((uint8_t)parsed_command, network_pak_bridge_sequence, payload, payload_length);
            snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "NONE");
        } else {
            network_pak_make_packet(NETWORK_PAK_CMD_ERROR, network_pak_bridge_sequence, payload, payload_length);
            snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "%.*s",
                payload_length, (const char *)payload);
        }
        snprintf(network_pak_debug.last_bridge_rsp, sizeof(network_pak_debug.last_bridge_rsp),
            "CMD=%02lX LEN=%u", parsed_command, payload_length);
        network_pak_debug.bridge_rsp_count++;
        network_pak_bridge_request_pending = false;
        network_pak_bridge_request_sent = false;
        restore_interrupts(save);
        return true;
    }

    if (strncmp(line, "NETPAK_RSP|", 11) != 0) {
        return false;
    }

    char copy[96];
    snprintf(copy, sizeof(copy), "%s", line + 11);
    char *sequence_text = copy;
    char *status_text = strchr(sequence_text, '|');
    if (status_text == NULL) {
        return true;
    }
    *status_text++ = '\0';
    char *payload = strchr(status_text, '|');
    if (payload == NULL) {
        return true;
    }
    *payload++ = '\0';

    const unsigned long parsed_sequence = strtoul(sequence_text, NULL, 10);
    const uint32_t save = save_and_disable_interrupts();
    if (!network_pak_bridge_request_pending || parsed_sequence != network_pak_bridge_sequence) {
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "SEQ_MISMATCH");
        restore_interrupts(save);
        return true;
    }

    const uint8_t payload_length = (uint8_t)strnlen(payload, NETWORK_PAK_MAX_PAYLOAD);
    if (strcmp(status_text, "OK") == 0) {
        network_pak_make_packet(NETWORK_PAK_CMD_BRIDGE_RESPONSE, network_pak_bridge_sequence, payload, payload_length);
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "NONE");
    } else {
        network_pak_make_packet(NETWORK_PAK_CMD_ERROR, network_pak_bridge_sequence, payload, payload_length);
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "%.*s", NETWORK_PAK_MAX_PAYLOAD, payload);
    }
    snprintf(network_pak_debug.last_bridge_rsp, sizeof(network_pak_debug.last_bridge_rsp), "%.*s", NETWORK_PAK_MAX_PAYLOAD, payload);
    network_pak_debug.bridge_rsp_count++;
    network_pak_bridge_request_pending = false;
    network_pak_bridge_request_sent = false;
    restore_interrupts(save);
    return true;
}

void n64_accessory_poll_network_bridge(void) {
    bool emit_request = false;
    bool timed_out = false;
    uint8_t command = 0;
    uint8_t sequence = 0;
    uint8_t payload[NETWORK_PAK_MAX_PAYLOAD + 1u];
    uint8_t payload_length = 0;
    memset(payload, 0, sizeof(payload));

    const uint32_t now = time_us_32();
    uint32_t save = save_and_disable_interrupts();
    if (network_pak_bridge_request_pending && !network_pak_bridge_request_sent) {
        command = network_pak_bridge_command;
        sequence = network_pak_bridge_sequence;
        payload_length = network_pak_bridge_payload_length;
        memcpy(payload, network_pak_bridge_payload, payload_length);
        network_pak_bridge_request_sent = true;
        network_pak_bridge_sent_us = now;
        network_pak_debug.bridge_req_count++;
        emit_request = true;
    } else if (network_pak_bridge_request_pending && network_pak_bridge_request_sent
        && (uint32_t)(now - network_pak_bridge_sent_us) >= NETWORK_PAK_BRIDGE_TIMEOUT_US) {
        sequence = network_pak_bridge_sequence;
        network_pak_make_packet(NETWORK_PAK_CMD_ERROR, sequence, "BRIDGE_TIMEOUT", 14);
        network_pak_debug.bridge_timeout_count++;
        snprintf(network_pak_debug.last_bridge_rsp, sizeof(network_pak_debug.last_bridge_rsp), "BRIDGE_TIMEOUT");
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "BRIDGE_TIMEOUT");
        network_pak_bridge_request_pending = false;
        network_pak_bridge_request_sent = false;
        timed_out = true;
    }
    restore_interrupts(save);

    if (emit_request) {
        if (network_pak_is_stream_command(command)) {
            printf("NETPAK_REQ_HEX|%u|%s|", sequence, network_pak_bridge_command_name(command));
            for (uint8_t i = 0; i < payload_length; ++i) {
                printf("%02X", payload[i]);
            }
            putchar('\n');
        } else {
            printf("NETPAK_REQ|%u|%s|%s\n", sequence, network_pak_bridge_command_name(command), (char *)payload);
        }
    }
    if (timed_out) {
        printf("NETPAK_TIMEOUT|%u\n", sequence);
    }
}
