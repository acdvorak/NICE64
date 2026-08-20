#pragma once

#include <stdbool.h>
#include <stdint.h>

#include "nice64_response.h"

#define NETWORK_PAK_BLOCK_SIZE 32u
#define NETWORK_PAK_REPLY_SIZE 33u
#define NETWORK_PAK_ADDR_COMMAND 0x8000u
#define NETWORK_PAK_ADDR_RESPONSE 0x8020u
#define NETWORK_PAK_ACCESSORY_ID 0x01u

typedef struct {
    uint32_t read_count;
    uint32_t write_count;
    uint32_t bridge_req_count;
    uint32_t bridge_rsp_count;
    uint32_t bridge_timeout_count;
    uint32_t last_read_address;
    uint32_t last_write_address;
    uint8_t last_request[NETWORK_PAK_BLOCK_SIZE];
    uint8_t last_response[NETWORK_PAK_BLOCK_SIZE];
    uint8_t served_read[NETWORK_PAK_BLOCK_SIZE];
    bool bridge_connected;
    bool bridge_pending;
    bool bridge_sent;
    uint8_t last_bridge_cmd;
    uint8_t current_sequence;
    char last_bridge_rsp[24];
    char last_bridge_error[24];
} network_pak_debug_t;

void network_pak_init(void);
uint16_t network_pak_decode_address(uint8_t hi, uint8_t lo);
bool network_pak_handle_read(uint16_t address, uint8_t out[NETWORK_PAK_REPLY_SIZE]);
bool network_pak_handle_write(uint16_t address, const uint8_t data[NETWORK_PAK_BLOCK_SIZE], uint8_t *crc_out);
bool network_pak_handle_serial_line(const char *line,
                                    const nice64_response_sink_t *response);
bool network_pak_handle_bridge_line(const char *line,
                                    const nice64_response_sink_t *response);
bool network_pak_rudp_session_opened(void);
void network_pak_rudp_session_closed(void);
bool network_pak_handle_rudp_line(const char *line,
                                  const nice64_response_sink_t *response);
void network_pak_core1_service(void);
void network_pak_get_debug(network_pak_debug_t *debug);
uint8_t network_pak_data_crc(const uint8_t data[NETWORK_PAK_BLOCK_SIZE]);
