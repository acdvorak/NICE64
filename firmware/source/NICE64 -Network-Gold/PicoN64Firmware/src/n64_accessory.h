#pragma once

#include <stdbool.h>
#include <stdint.h>

typedef enum {
    N64_ACCESSORY_NONE = 0,
    N64_ACCESSORY_MEMORY_PAK,
    N64_ACCESSORY_RUMBLE_PAK,
    N64_ACCESSORY_TRANSFER_PAK,
    N64_ACCESSORY_NETWORK_PAK,
} n64_accessory_mode_t;

typedef struct {
    uint32_t read_count;
    uint32_t write_count;
    uint16_t last_read_address;
    uint16_t last_write_address;
    uint8_t last_request[8];
    uint8_t last_response[8];
    uint8_t served_read[8];
    bool bridge_connected;
    uint32_t bridge_req_count;
    uint32_t bridge_rsp_count;
    uint32_t bridge_timeout_count;
    uint8_t last_bridge_cmd;
    char last_bridge_rsp[24];
    char last_bridge_error[24];
} n64_network_pak_debug_t;

void n64_accessory_init(void);
n64_accessory_mode_t n64_accessory_mode(void);
void n64_accessory_set_mode(n64_accessory_mode_t mode);
uint8_t n64_accessory_identity_byte(void);
uint8_t n64_accessory_identity_byte_irq(void);
uint16_t n64_accessory_decode_address(uint8_t hi, uint8_t lo);
void n64_accessory_clear_memory(void);
void n64_accessory_write_block(uint16_t offset, const uint8_t data[32]);
void n64_accessory_read_block(uint16_t offset, uint8_t out[32]);
void n64_accessory_clear_transfer_rom(void);
void n64_accessory_set_transfer_profile(uint8_t cart_type, uint32_t rom_size, uint32_t ram_size, uint8_t flags);
void n64_accessory_write_transfer_rom_block(uint16_t offset, const uint8_t data[32]);
void n64_accessory_begin_transfer_flash(uint32_t rom_size);
void n64_accessory_write_transfer_flash_block(uint32_t offset, const uint8_t data[32]);
void n64_accessory_write_transfer_flash_page(uint32_t offset, const uint8_t data[256]);
void n64_accessory_commit_transfer_flash(void);
uint32_t n64_accessory_transfer_flash_capacity(void);
void n64_accessory_write_transfer_save_block(uint16_t offset, const uint8_t data[32]);
void n64_accessory_read_transfer_save_block(uint16_t offset, uint8_t out[32]);
bool n64_accessory_handle_read(uint16_t address, uint8_t out[33]);
bool n64_accessory_handle_write(uint16_t address, const uint8_t data[32], uint8_t *crc_out);
bool n64_accessory_rumble_active(void);
void n64_accessory_get_network_debug(n64_network_pak_debug_t *debug);
bool n64_accessory_handle_network_bridge_serial_line(const char *line);
void n64_accessory_poll_network_bridge(void);
