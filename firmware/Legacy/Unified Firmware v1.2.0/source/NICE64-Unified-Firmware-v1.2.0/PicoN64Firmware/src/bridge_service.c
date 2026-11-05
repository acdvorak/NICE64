#include "bridge_service.h"

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "bridge_line_dispatch.h"
#include "controller_state.h"
#include "core_transport.h"
#include "joybus.h"
#include "n64_accessory.h"
#include "n64_controller.h"
#include "nice64_line_framer.h"
#include "nice64_response.h"
#include "nice64_tcp_service.h"
#include "nice64_usb_transport.h"

#define CONTROLLER_PAK_BLOCK_SIZE 32

static int hex_value(int c) {
    if (c >= '0' && c <= '9') {
        return c - '0';
    }

    if (c >= 'a' && c <= 'f') {
        return c - 'a' + 10;
    }

    if (c >= 'A' && c <= 'F') {
        return c - 'A' + 10;
    }

    return -1;
}

static bool parse_hex_bytes(const char *text, uint8_t *out, int count) {
    for (int i = 0; i < count; ++i) {
        const int hi = hex_value((unsigned char)text[i * 2]);
        const int lo = hex_value((unsigned char)text[i * 2 + 1]);
        if (hi < 0 || lo < 0) {
            return false;
        }

        out[i] = (uint8_t)((hi << 4) | lo);
    }

    return true;
}

static bool parse_hex_address4(const char *text, uint16_t *address) {
    const int a0 = hex_value((unsigned char)text[0]);
    const int a1 = hex_value((unsigned char)text[1]);
    const int a2 = hex_value((unsigned char)text[2]);
    const int a3 = hex_value((unsigned char)text[3]);
    if (a0 < 0 || a1 < 0 || a2 < 0 || a3 < 0) {
        return false;
    }

    *address = (uint16_t)((a0 << 12) | (a1 << 8) | (a2 << 4) | a3);
    return true;
}

static bool parse_hex_uint32(const char *text, int digits, uint32_t *value) {
    uint32_t parsed = 0;
    for (int i = 0; i < digits; ++i) {
        const int nibble = hex_value((unsigned char)text[i]);
        if (nibble < 0) {
            return false;
        }
        parsed = (parsed << 4) | (uint32_t)nibble;
    }

    *value = parsed;
    return true;
}

static void phase3b_core1_transport_wait_service(void) {
    controller_state_service();
}

static bool call_core0_simple(core_transport_request_command_t command,
                              uint32_t arg0,
                              uint32_t arg1,
                              uint32_t arg2,
                              uint32_t length,
                              const uint8_t *data,
                              core_transport_response_t *response) {
    return core_transport_core1_simple_request(command,
                                               arg0,
                                               arg1,
                                               arg2,
                                               length,
                                               data,
                                               response,
                                               phase3b_core1_transport_wait_service);
}

bool bridge_service_handle_line(const char *line,
                                const nice64_response_sink_t *sink) {
            uint8_t parsed[4];
            if (n64_controller_parse_serial_state(line, parsed)) {
                n64_controller_set_state(parsed);
                nice64_response_puts(sink, "OK");
            } else if (strcmp(line, "MODE NEUTRAL") == 0 || strcmp(line, "mode neutral") == 0) {
                const uint8_t neutral[4] = {0x00, 0x00, 0x00, 0x00};
                n64_controller_set_state(neutral);
                n64_controller_set_ignore_serial(true);
                nice64_response_puts(sink, "OK MODE NEUTRAL");
            } else if (strcmp(line, "MODE SERIAL") == 0 || strcmp(line, "mode serial") == 0) {
                n64_controller_set_ignore_serial(false);
                nice64_response_puts(sink, "OK MODE SERIAL");
            } else if (strcmp(line, "MODE A") == 0 || strcmp(line, "mode a") == 0) {
                const uint8_t a_held[4] = {0x80, 0x00, 0x00, 0x00};
                n64_controller_set_state(a_held);
                n64_controller_set_ignore_serial(true);
                nice64_response_puts(sink, "OK MODE A");
            } else if (strcmp(line, "PAKMODE NONE") == 0 || strcmp(line, "pakmode none") == 0) {
                core_transport_response_t response;
                if (call_core0_simple(CORE_TRANSPORT_REQUEST_SET_ACCESSORY_MODE,
                                                        N64_ACCESSORY_NONE, 0u, 0u, 0u, NULL, &response)) {
                    nice64_response_puts(sink, "OK PAKMODE NONE");
                } else {
                    nice64_response_puts(sink, "ERR CORE0");
                }
            } else if (strcmp(line, "PAKMODE MEM") == 0 || strcmp(line, "pakmode mem") == 0) {
                core_transport_response_t response;
                if (call_core0_simple(CORE_TRANSPORT_REQUEST_SET_ACCESSORY_MODE,
                                                        N64_ACCESSORY_MEMORY_PAK, 0u, 0u, 0u, NULL, &response)) {
                    nice64_response_puts(sink, "OK PAKMODE MEM");
                } else {
                    nice64_response_puts(sink, "ERR CORE0");
                }
            } else if (strcmp(line, "PAKMODE RUMBLE") == 0 || strcmp(line, "pakmode rumble") == 0) {
                core_transport_response_t response;
                if (call_core0_simple(CORE_TRANSPORT_REQUEST_SET_ACCESSORY_MODE,
                                                        N64_ACCESSORY_RUMBLE_PAK, 0u, 0u, 0u, NULL, &response)) {
                    nice64_response_puts(sink, "OK PAKMODE RUMBLE");
                } else {
                    nice64_response_puts(sink, "ERR CORE0");
                }
            } else if (strcmp(line, "PAKMODE TRANSFER") == 0 || strcmp(line, "pakmode transfer") == 0) {
                core_transport_response_t response;
                if (call_core0_simple(CORE_TRANSPORT_REQUEST_SET_ACCESSORY_MODE,
                                                        N64_ACCESSORY_TRANSFER_PAK, 0u, 0u, 0u, NULL, &response)) {
                    nice64_response_puts(sink, "OK PAKMODE TRANSFER");
                } else {
                    nice64_response_puts(sink, "ERR CORE0");
                }
            } else if (strcmp(line, "PAKCLR") == 0 || strcmp(line, "pakclr") == 0) {
                core_transport_response_t response;
                if (call_core0_simple(CORE_TRANSPORT_REQUEST_CLEAR_MEMORY,
                                                        0u, 0u, 0u, 0u, NULL, &response)) {
                    nice64_response_puts(sink, "OK PAKCLR");
                } else {
                    nice64_response_puts(sink, "ERR CORE0");
                }
            } else if (strcmp(line, "GBCLR") == 0 || strcmp(line, "gbclr") == 0) {
                core_transport_response_t response;
                if (call_core0_simple(CORE_TRANSPORT_REQUEST_CLEAR_TRANSFER_ROM,
                                                        0u, 0u, 0u, 0u, NULL, &response)) {
                    nice64_response_puts(sink, "OK GBCLR");
                } else {
                    nice64_response_puts(sink, "ERR CORE0");
                }
            } else if (strncmp(line, "GBP ", 4) == 0 || strncmp(line, "gbp ", 4) == 0) {
                uint32_t cart_type;
                uint32_t rom_size;
                uint32_t ram_size;
                uint32_t flags;
                if (strlen(line) < 27
                    || !parse_hex_uint32(&line[4], 2, &cart_type)
                    || line[6] != ' '
                    || !parse_hex_uint32(&line[7], 8, &rom_size)
                    || line[15] != ' '
                    || !parse_hex_uint32(&line[16], 8, &ram_size)
                    || line[24] != ' '
                    || !parse_hex_uint32(&line[25], 2, &flags)) {
                    nice64_response_puts(sink, "ERR GBP expected: GBP TT RRRRRRRR SSSSSSSS FF");
                } else {
                    core_transport_response_t response;
                    if (call_core0_simple(CORE_TRANSPORT_REQUEST_SET_TRANSFER_PROFILE,
                                                            cart_type, rom_size, ram_size, flags, NULL, &response)) {
                        nice64_response_printf(sink, "OK GBP %02lX %08lX %08lX %02lX\n", cart_type, rom_size, ram_size, flags);
                    } else {
                        nice64_response_puts(sink, "ERR CORE0");
                    }
                }
            } else if (strcmp(line, "RUMBLE?") == 0 || strcmp(line, "rumble?") == 0) {
                core_transport_response_t response;
                if (call_core0_simple(CORE_TRANSPORT_REQUEST_RUMBLE_QUERY,
                                                        0u, 0u, 0u, 0u, NULL, &response)) {
                    nice64_response_printf(sink, "RUMBLE %lu\n", response.value0);
                } else {
                    nice64_response_puts(sink, "ERR CORE0");
                }
            } else if (strcmp(line, "GBFCAP?") == 0 || strcmp(line, "gbfcap?") == 0) {
                core_transport_response_t response;
                if (call_core0_simple(CORE_TRANSPORT_REQUEST_TRANSFER_FLASH_CAPACITY_QUERY,
                                                        0u, 0u, 0u, 0u, NULL, &response)) {
                    nice64_response_printf(sink, "GBFCAP %08lX\n", response.value0);
                } else {
                    nice64_response_puts(sink, "ERR CORE0");
                }
            } else if (strncmp(line, "GBS ", 4) == 0 || strncmp(line, "gbs ", 4) == 0) {
                uint16_t address;
                uint8_t block[CONTROLLER_PAK_BLOCK_SIZE];
                if (strlen(line) < 73
                    || !parse_hex_address4(&line[4], &address)
                    || line[8] != ' '
                    || !parse_hex_bytes(&line[9], block, CONTROLLER_PAK_BLOCK_SIZE)) {
                    nice64_response_puts(sink, "ERR GBS expected: GBS 0000 64HEXCHARS");
                } else {
                    core_transport_response_t response;
                    if (call_core0_simple(CORE_TRANSPORT_REQUEST_WRITE_TRANSFER_SAVE_BLOCK,
                                                            address, 0u, 0u, CONTROLLER_PAK_BLOCK_SIZE,
                                                            block, &response)) {
                        nice64_response_printf(sink, "OK GBS %04X\n", address);
                    } else {
                        nice64_response_puts(sink, "ERR CORE0");
                    }
                }
            } else if (strncmp(line, "GBSDUMP ", 8) == 0 || strncmp(line, "gbsdump ", 8) == 0) {
                uint16_t address;
                if (strlen(line) < 12 || !parse_hex_address4(&line[8], &address)) {
                    nice64_response_puts(sink, "ERR GBSDUMP expected: GBSDUMP 0000");
                } else {
                    core_transport_response_t response;
                    if (call_core0_simple(CORE_TRANSPORT_REQUEST_READ_TRANSFER_SAVE_BLOCK,
                                                            address, 0u, 0u, 0u, NULL, &response)) {
                        nice64_response_printf(sink, "GBS %04X ", address);
                        for (int i = 0; i < CONTROLLER_PAK_BLOCK_SIZE; ++i) {
                            nice64_response_printf(sink, "%02X", response.data[i]);
                        }
                        nice64_response_putc(sink, '\n');
                    } else {
                        nice64_response_puts(sink, "ERR CORE0");
                    }
                }
            } else if (strncmp(line, "GB ", 3) == 0 || strncmp(line, "gb ", 3) == 0) {
                uint16_t address;
                uint8_t block[CONTROLLER_PAK_BLOCK_SIZE];
                if (strlen(line) < 72
                    || !parse_hex_address4(&line[3], &address)
                    || line[7] != ' '
                    || !parse_hex_bytes(&line[8], block, CONTROLLER_PAK_BLOCK_SIZE)) {
                    nice64_response_puts(sink, "ERR GB expected: GB 0000 64HEXCHARS");
                } else {
                    core_transport_response_t response;
                    if (call_core0_simple(CORE_TRANSPORT_REQUEST_WRITE_TRANSFER_ROM_BLOCK,
                                                            address, 0u, 0u, CONTROLLER_PAK_BLOCK_SIZE,
                                                            block, &response)) {
                        nice64_response_printf(sink, "OK GB %04X\n", address);
                    } else {
                        nice64_response_puts(sink, "ERR CORE0");
                    }
                }
            } else if (strncmp(line, "GBFBEGIN ", 9) == 0 || strncmp(line, "gbfbegin ", 9) == 0) {
                uint32_t size;
                if (strlen(line) < 17 || !parse_hex_uint32(&line[9], 8, &size)) {
                    nice64_response_puts(sink, "ERR GBFBEGIN expected: GBFBEGIN 00000000");
                } else {
                    core_transport_response_t response;
                    if (call_core0_simple(CORE_TRANSPORT_REQUEST_BEGIN_TRANSFER_FLASH,
                                                            size, 0u, 0u, 0u, NULL, &response)) {
                        if (response.value0 == TRANSFER_FLASH_OK) {
                            nice64_response_printf(sink, "OK GBFBEGIN %08lX\n", size);
                        } else {
                            nice64_response_printf(sink, "ERR GBFBEGIN %s\n",
                                transfer_flash_result_name((transfer_flash_result_t)response.value0));
                        }
                    } else {
                        nice64_response_puts(sink, "ERR CORE0");
                    }
                }
            } else if (strncmp(line, "GBF ", 4) == 0 || strncmp(line, "gbf ", 4) == 0) {
                uint32_t offset;
                uint8_t block[CONTROLLER_PAK_BLOCK_SIZE];
                if (strlen(line) < 75
                    || !parse_hex_uint32(&line[4], 6, &offset)
                    || line[10] != ' '
                    || !parse_hex_bytes(&line[11], block, CONTROLLER_PAK_BLOCK_SIZE)) {
                    nice64_response_puts(sink, "ERR GBF expected: GBF 000000 64HEXCHARS");
                } else {
                    core_transport_response_t response;
                    if (call_core0_simple(CORE_TRANSPORT_REQUEST_WRITE_TRANSFER_FLASH_BLOCK,
                                                            offset, 0u, 0u, CONTROLLER_PAK_BLOCK_SIZE,
                                                            block, &response)) {
                        if (response.value0 == TRANSFER_FLASH_OK) {
                            nice64_response_printf(sink, "OK GBF %06lX\n", offset);
                        } else {
                            nice64_response_printf(sink, "ERR GBF %s\n",
                                transfer_flash_result_name((transfer_flash_result_t)response.value0));
                        }
                    } else {
                        nice64_response_puts(sink, "ERR CORE0");
                    }
                }
            } else if (strncmp(line, "GBFP ", 5) == 0 || strncmp(line, "gbfp ", 5) == 0) {
                uint32_t offset;
                uint8_t page[256];
                if (strlen(line) < 524
                    || !parse_hex_uint32(&line[5], 6, &offset)
                    || line[11] != ' '
                    || !parse_hex_bytes(&line[12], page, 256)) {
                    nice64_response_puts(sink, "ERR GBFP expected: GBFP 000000 512HEXCHARS");
                } else {
                    core_transport_response_t response;
                    if (call_core0_simple(CORE_TRANSPORT_REQUEST_WRITE_TRANSFER_FLASH_PAGE,
                                                            offset, 0u, 0u, 256u, page, &response)) {
                        if (response.value0 == TRANSFER_FLASH_OK) {
                            nice64_response_printf(sink, "OK GBFP %06lX\n", offset);
                        } else {
                            nice64_response_printf(sink, "ERR GBFP %s\n",
                                transfer_flash_result_name((transfer_flash_result_t)response.value0));
                        }
                    } else {
                        nice64_response_puts(sink, "ERR CORE0");
                    }
                }
            } else if (strcmp(line, "GBFCOMMIT") == 0 || strcmp(line, "gbfcommit") == 0) {
                core_transport_response_t response;
                if (call_core0_simple(CORE_TRANSPORT_REQUEST_COMMIT_TRANSFER_FLASH,
                                                        0u, 0u, 0u, 0u, NULL, &response)) {
                    if (response.value0 == TRANSFER_FLASH_OK) {
                        nice64_response_puts(sink, "OK GBFCOMMIT");
                    } else {
                        nice64_response_printf(sink, "ERR GBFCOMMIT %s\n",
                            transfer_flash_result_name((transfer_flash_result_t)response.value0));
                    }
                } else {
                    nice64_response_puts(sink, "ERR CORE0");
                }
            } else if (strcmp(line, "GBFCRC?") == 0 || strcmp(line, "gbfcrc?") == 0) {
                uint32_t length = 0u;
                uint32_t crc32 = 0u;
                const transfer_flash_result_t result =
                    n64_accessory_transfer_flash_crc(&length, &crc32);
                if (result == TRANSFER_FLASH_OK) {
                    nice64_response_printf(sink, "GBFCRC %08lX %08lX\n",
                                           length, crc32);
                } else {
                    nice64_response_printf(sink, "ERR GBFCRC %s\n",
                                           transfer_flash_result_name(result));
                }
            } else if (strncmp(line, "PAK ", 4) == 0 || strncmp(line, "pak ", 4) == 0) {
                uint16_t address;
                uint8_t block[CONTROLLER_PAK_BLOCK_SIZE];
                if (strlen(line) < 73
                    || !parse_hex_address4(&line[4], &address)
                    || line[8] != ' '
                    || !parse_hex_bytes(&line[9], block, CONTROLLER_PAK_BLOCK_SIZE)) {
                    nice64_response_puts(sink, "ERR PAK expected: PAK 0000 64HEXCHARS");
                } else {
                    core_transport_response_t response;
                    if (call_core0_simple(CORE_TRANSPORT_REQUEST_WRITE_MEMORY_BLOCK,
                                                            address, 0u, 0u, CONTROLLER_PAK_BLOCK_SIZE,
                                                            block, &response)) {
                        nice64_response_printf(sink, "OK PAK %04X\n", address);
                    } else {
                        nice64_response_puts(sink, "ERR CORE0");
                    }
                }
            } else if (strncmp(line, "PAKDUMP ", 8) == 0 || strncmp(line, "pakdump ", 8) == 0) {
                uint16_t address;
                if (strlen(line) < 12 || !parse_hex_address4(&line[8], &address)) {
                    nice64_response_puts(sink, "ERR PAKDUMP expected: PAKDUMP 0000");
                } else {
                    core_transport_response_t response;
                    if (call_core0_simple(CORE_TRANSPORT_REQUEST_READ_MEMORY_BLOCK,
                                                            address, 0u, 0u, 0u, NULL, &response)) {
                        nice64_response_printf(sink, "PAK %04X ", address);
                        for (int i = 0; i < CONTROLLER_PAK_BLOCK_SIZE; ++i) {
                            nice64_response_printf(sink, "%02X", response.data[i]);
                        }
                        nice64_response_putc(sink, '\n');
                    } else {
                        nice64_response_puts(sink, "ERR CORE0");
                    }
                }
            } else if (strcmp(line, "STAT") == 0 || strcmp(line, "stat") == 0) {
                core_transport_response_t response;
                if (call_core0_simple(CORE_TRANSPORT_REQUEST_STAT_SNAPSHOT,
                                                        0u, 0u, 0u, 0u, NULL, &response)) {
                    const joybus_debug_t *debug = &response.debug;
                    nice64_response_printf(sink, "STAT edges=%lu streams=%lu commands=%lu identify=%lu status=%lu reset=%lu aread=%lu awrite=%lu other=%lu gcn40=%lu gcn41=%lu gcn43=%lu gcn_no=%lu rx_done=%lu rx_stop=%lu rx_hot=%lu rx_timeout=%lu rx_new=%lu rx_noreply=%lu tx_start=%lu tx_done=%lu tx_ident=%lu tx_status=%lu tx_block=%lu tx_last=0x%02X tx_len=%u gcn_len=%u gcn=%02X%02X%02X%02X%02X%02X%02X%02X%02X%02X last=0x%02X pak=0x%04lX mode=%lu rumble=%lu line=%lu serial=%lu ignored=%lu serial_a=%lu ignore=%u\n",
                           debug->edges,
                           debug->streams,
                           debug->commands,
                           debug->identify,
                           debug->status,
                           debug->reset,
                           debug->accessory_read,
                           debug->accessory_write,
                           debug->other,
                           debug->gcn_read_count,
                           debug->gcn_origin_count,
                           debug->gcn_long_count,
                           debug->gcn_no_reply_count,
                           debug->rx_command_complete_count,
                           debug->rx_stop_dispatch_count,
                           debug->rx_hot_reply_count,
                           debug->rx_stop_timeout_count,
                           debug->rx_waiting_new_stream_count,
                           debug->rx_no_reply_count,
                           debug->tx_start_count,
                           debug->tx_done_count,
                           debug->tx_hot_ident_count,
                           debug->tx_hot_status_count,
                           debug->tx_busy_or_blocked_count,
                           debug->last_tx_command,
                           (unsigned)debug->last_tx_reply_len,
                           (unsigned)debug->last_gcn_reply_len,
                           debug->last_gcn_reply[0],
                           debug->last_gcn_reply[1],
                           debug->last_gcn_reply[2],
                           debug->last_gcn_reply[3],
                           debug->last_gcn_reply[4],
                           debug->last_gcn_reply[5],
                           debug->last_gcn_reply[6],
                           debug->last_gcn_reply[7],
                           debug->last_gcn_reply[8],
                           debug->last_gcn_reply[9],
                           debug->last_command,
                           debug->last_accessory_address,
                           response.value0,
                           response.value1,
                           debug->line,
                           n64_controller_serial_state_count(),
                           n64_controller_serial_ignored_count(),
                           n64_controller_serial_a_on_count(),
                           n64_controller_ignore_serial() ? 1u : 0u);
                } else {
                    nice64_response_puts(sink, "ERR CORE0");
                }
            } else if (strcmp(line, "CLR") == 0 || strcmp(line, "clr") == 0) {
                core_transport_response_t response;
                if (call_core0_simple(CORE_TRANSPORT_REQUEST_CLEAR_JOYBUS_DEBUG,
                                                        0u, 0u, 0u, 0u, NULL, &response)) {
                    n64_controller_clear_debug();
                    nice64_response_puts(sink, "OK CLR");
                } else {
                    nice64_response_puts(sink, "ERR CORE0");
                }
            } else if (line[0] != '\0') {
                nice64_response_puts(sink, "ERR expected: N64 00000000, PAKMODE, PAK, PAKDUMP, PAKCLR, GB, GBS, GBSDUMP, GBF, GBCLR, GBFCAP?, RUMBLE?, STAT, or CLR");
            }

    return line[0] != '\0';
}

static void bridge_service_dispatch_usb_line(void *context, const char *line) {
    const nice64_response_sink_t *response = context;
    if (!nice64_tcp_usb_command_allowed(line)) {
        nice64_response_puts(response, "NICE64|BUSY|OWNER=TCP");
        return;
    }
    bridge_line_dispatch(line, response, bridge_service_handle_line);
}

void bridge_service_poll(void) {
    static char line[600];
    static nice64_line_framer_t framer;
    static bool initialized = false;

    if (!initialized) {
        nice64_line_framer_init(&framer, line, sizeof(line));
        initialized = true;
    }

    const nice64_response_sink_t *response = nice64_usb_response_sink();
    nice64_usb_poll_lines(&framer, bridge_service_dispatch_usb_line, (void *)response);
}

