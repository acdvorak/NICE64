#pragma once

#include <stdint.h>

typedef struct {
    uint32_t edges;
    uint32_t streams;
    uint32_t commands;
    uint32_t identify;
    uint32_t status;
    uint32_t reset;
    uint32_t accessory_read;
    uint32_t accessory_write;
    uint32_t other;
    uint32_t gcn_read_count;
    uint32_t gcn_origin_count;
    uint32_t gcn_long_count;
    uint32_t gcn_no_reply_count;
    uint32_t rx_command_complete_count;
    uint32_t rx_stop_dispatch_count;
    uint32_t rx_hot_reply_count;
    uint32_t rx_stop_timeout_count;
    uint32_t rx_waiting_new_stream_count;
    uint32_t rx_no_reply_count;
    uint32_t tx_start_count;
    uint32_t tx_done_count;
    uint32_t tx_hot_ident_count;
    uint32_t tx_hot_status_count;
    uint32_t tx_busy_or_blocked_count;
    uint32_t last_accessory_address;
    uint8_t last_command;
    uint8_t last_tx_command;
    uint8_t last_tx_reply_len;
    uint8_t last_gcn_reply[10];
    uint8_t last_gcn_reply_len;
    uint32_t line;
} joybus_debug_t;

void joybus_init(void);
void joybus_get_debug(joybus_debug_t *out);
void joybus_clear_debug(void);
