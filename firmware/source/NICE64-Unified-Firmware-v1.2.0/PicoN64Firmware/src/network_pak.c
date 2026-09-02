#include "network_pak.h"

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "bulk_stage.h"
#include "bridge_line_dispatch.h"
#include "hardware/sync.h"
#include "nice64_line_framer.h"
#include "nice64_network_rudp_adapter.h"
#include "nice64_realtime_memops.h"
#include "nice64_response.h"
#include "nice64_tcp_service.h"
#include "nice64_usb_transport.h"
#include "pico/stdlib.h"
#include "pico/time.h"

#define NETWORK_PAK_MAGIC_HI 0x4Eu
#define NETWORK_PAK_MAGIC_LO 0x50u
#define NETWORK_PAK_VERSION 0x01u
#define NETWORK_PAK_MAX_PAYLOAD 23u

#define NETWORK_PAK_CMD_PING 0x01u
#define NETWORK_PAK_CMD_GET_STATUS 0x02u
#define NETWORK_PAK_CMD_READ_CHUNK 0x03u
#define NETWORK_PAK_CMD_WRITE_CHUNK 0x04u
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
#define NETWORK_PAK_CMD_STREAM_CHUNK 0xA2u
#define NETWORK_PAK_CMD_BULK_READY 0xA5u

#define NETWORK_PAK_BULK_BEGIN_MARKER 0xB0u
#define NETWORK_PAK_BULK_READ_MARKER 0xB1u

#define NETWORK_PAK_CMD_PONG 0x81u
#define NETWORK_PAK_CMD_STATUS 0x82u
#define NETWORK_PAK_CMD_CHUNK 0x83u
#define NETWORK_PAK_CMD_ACK 0x05u
#define NETWORK_PAK_CMD_BRIDGE_RESPONSE 0x90u
#define NETWORK_PAK_CMD_ERROR 0xFFu

#define NETWORK_PAK_BRIDGE_TIMEOUT_US 1000000u

enum {
    NETWORK_PAK_RESPONSE_CORE0 = 0,
    NETWORK_PAK_RESPONSE_CORE1 = 1,
};

static uint8_t network_pak_response[2][NETWORK_PAK_BLOCK_SIZE];
static volatile uint32_t network_pak_response_active_index;
static volatile uint32_t network_pak_response_generation;
static network_pak_debug_t network_pak_debug;
static bool bridge_request_pending;
static bool bridge_request_sent;
static uint8_t bridge_command;
static uint8_t bridge_sequence;
static uint8_t bridge_payload_length;
static uint8_t bridge_payload[NETWORK_PAK_MAX_PAYLOAD + 1u];
static uint32_t bridge_sent_us;

typedef enum {
    NETWORK_PAK_OWNER_NONE = 0,
    NETWORK_PAK_OWNER_USB,
    NETWORK_PAK_OWNER_TCP,
    NETWORK_PAK_OWNER_RUDP,
} network_pak_owner_t;

static network_pak_owner_t bridge_owner;
static bool bulk_stage_core1_initialized;

uint8_t __no_inline_not_in_flash_func(network_pak_data_crc)(const uint8_t data[NETWORK_PAK_BLOCK_SIZE]) {
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

uint16_t __no_inline_not_in_flash_func(network_pak_decode_address)(uint8_t hi, uint8_t lo) {
    return (uint16_t)((((uint16_t)hi << 8) | lo) & 0xFFE0u);
}

static uint16_t network_pak_checksum(const uint8_t packet[NETWORK_PAK_BLOCK_SIZE]) {
    uint16_t checksum = 0;
    const uint8_t payload_length = packet[6] <= NETWORK_PAK_MAX_PAYLOAD
        ? packet[6]
        : NETWORK_PAK_MAX_PAYLOAD;

    for (uint32_t i = 0; i < 7u; ++i) {
        checksum = (uint16_t)(checksum + packet[i]);
    }

    for (uint8_t i = 0; i < payload_length; ++i) {
        checksum = (uint16_t)(checksum + packet[7u + i]);
    }

    return (uint16_t)~checksum;
}

static void __no_inline_not_in_flash_func(network_pak_make_packet)(uint32_t writer_index,
                                                                   uint8_t command,
                                                                   uint8_t sequence,
                                                                   const void *payload,
                                                                   uint8_t payload_length) {
    if (payload_length > NETWORK_PAK_MAX_PAYLOAD) {
        payload_length = NETWORK_PAK_MAX_PAYLOAD;
    }

    uint8_t *response = network_pak_response[writer_index & 1u];
    nice64_realtime_fill(response, 0, NETWORK_PAK_BLOCK_SIZE);
    response[0] = NETWORK_PAK_MAGIC_HI;
    response[1] = NETWORK_PAK_MAGIC_LO;
    response[2] = NETWORK_PAK_VERSION;
    response[3] = command;
    response[4] = 0;
    response[5] = sequence;
    response[6] = payload_length;

    if (payload != NULL && payload_length > 0) {
        nice64_realtime_copy(&response[7], payload, payload_length);
    }

    const uint16_t checksum = network_pak_checksum(response);
    response[30] = (uint8_t)(checksum >> 8);
    response[31] = (uint8_t)(checksum & 0xFFu);

    __atomic_thread_fence(__ATOMIC_RELEASE);
    __atomic_store_n(&network_pak_response_active_index, writer_index & 1u, __ATOMIC_RELEASE);
    __atomic_store_n(&network_pak_response_generation,
                     __atomic_load_n(&network_pak_response_generation, __ATOMIC_RELAXED) + 1u,
                     __ATOMIC_RELEASE);
}

static bool network_pak_request_valid(const uint8_t data[NETWORK_PAK_BLOCK_SIZE]) {
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

static bool network_pak_route_local(const uint8_t data[NETWORK_PAK_BLOCK_SIZE]) {
    const uint8_t command = data[3];
    const uint8_t sequence = data[5];

    switch (command) {
    case NETWORK_PAK_CMD_PING:
        network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE0, NETWORK_PAK_CMD_PONG, sequence, "PONG", 4);
        return true;
    case NETWORK_PAK_CMD_GET_STATUS:
        network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE0, NETWORK_PAK_CMD_STATUS, sequence, "HOST_READY NIC=PICO", 19);
        return true;
    case NETWORK_PAK_CMD_READ_CHUNK:
        network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE0, NETWORK_PAK_CMD_CHUNK, sequence, "hello from pico", 15);
        return true;
    case NETWORK_PAK_CMD_WRITE_CHUNK:
        network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE0, NETWORK_PAK_CMD_ACK, sequence, "ACK", 3);
        return true;
    case NETWORK_PAK_CMD_RESET:
        network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE0, NETWORK_PAK_CMD_ACK, sequence, "RESET", 5);
        return true;
    default:
        return false;
    }
}

static bool network_pak_route_bridge(const uint8_t data[NETWORK_PAK_BLOCK_SIZE]) {
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
        network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE0, NETWORK_PAK_CMD_ERROR, sequence, "BRIDGE_OFFLINE", 14);
        return true;
    }

    if (__atomic_load_n(&bridge_request_pending, __ATOMIC_ACQUIRE)) {
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "BRIDGE_BUSY");
        network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE0, NETWORK_PAK_CMD_ERROR, sequence, "BRIDGE_BUSY", 11);
        return true;
    }

    bridge_command = command;
    bridge_sequence = sequence;
    bridge_payload_length = payload_length;
    nice64_realtime_fill(bridge_payload, 0, sizeof(bridge_payload));
    nice64_realtime_copy(bridge_payload, &data[7], payload_length);
    __atomic_store_n(&bridge_request_sent, false, __ATOMIC_RELEASE);
    network_pak_debug.bridge_pending = true;
    network_pak_debug.bridge_sent = false;
    network_pak_debug.current_sequence = sequence;
    snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "PENDING");
    __atomic_thread_fence(__ATOMIC_RELEASE);
    __atomic_store_n(&bridge_request_pending, true, __ATOMIC_RELEASE);
    network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE0, NETWORK_PAK_CMD_STATUS, sequence, "BRIDGE_PENDING", 14);
    return true;
}

static void network_pak_handle_request(const uint8_t data[NETWORK_PAK_BLOCK_SIZE]) {
    const uint8_t sequence = data[5];

    if (!network_pak_request_valid(data)) {
        network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE0, NETWORK_PAK_CMD_ERROR, sequence, "BAD_PACKET", 10);
        return;
    }

    if (network_pak_route_local(data) || network_pak_route_bridge(data)) {
        return;
    }

    network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE0, NETWORK_PAK_CMD_ERROR, sequence, "UNSUPPORTED", 11);
}

static void network_pak_init_response(void) {
    network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE0, NETWORK_PAK_CMD_STATUS, 0, "HOST_READY NIC=PICO", 19);
}

void network_pak_init(void) {
    memset(&network_pak_debug, 0, sizeof(network_pak_debug));
    __atomic_store_n(&bridge_request_pending, false, __ATOMIC_RELEASE);
    __atomic_store_n(&bridge_request_sent, false, __ATOMIC_RELEASE);
    bridge_command = 0;
    bridge_sequence = 0;
    bridge_payload_length = 0;
    bridge_sent_us = 0;
    bridge_owner = NETWORK_PAK_OWNER_NONE;
    snprintf(network_pak_debug.last_bridge_rsp, sizeof(network_pak_debug.last_bridge_rsp), "NONE");
    snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "OFFLINE");
    __atomic_store_n(&network_pak_response_active_index, NETWORK_PAK_RESPONSE_CORE0, __ATOMIC_RELAXED);
    __atomic_store_n(&network_pak_response_generation, 0u, __ATOMIC_RELAXED);
    network_pak_init_response();
}

bool __no_inline_not_in_flash_func(network_pak_handle_read)(uint16_t address, uint8_t out[NETWORK_PAK_REPLY_SIZE]) {
    network_pak_debug.read_count++;
    network_pak_debug.last_read_address = address;

    const uint32_t generation_before =
        __atomic_load_n(&network_pak_response_generation, __ATOMIC_ACQUIRE);
    uint32_t index = __atomic_load_n(&network_pak_response_active_index, __ATOMIC_ACQUIRE);
    if (index >= 2u) {
        index = NETWORK_PAK_RESPONSE_CORE0;
    }
    nice64_realtime_copy(out, network_pak_response[index], NETWORK_PAK_BLOCK_SIZE);
    const uint32_t generation_after =
        __atomic_load_n(&network_pak_response_generation, __ATOMIC_ACQUIRE);
    if (generation_before != generation_after) {
        index = __atomic_load_n(&network_pak_response_active_index, __ATOMIC_ACQUIRE);
        if (index >= 2u) {
            index = NETWORK_PAK_RESPONSE_CORE0;
        }
        nice64_realtime_copy(out, network_pak_response[index], NETWORK_PAK_BLOCK_SIZE);
    }

    nice64_realtime_copy(network_pak_debug.last_response, out, sizeof(network_pak_debug.last_response));
    nice64_realtime_copy(network_pak_debug.served_read, out, sizeof(network_pak_debug.served_read));

    out[32] = network_pak_data_crc(out);
    return true;
}

bool __no_inline_not_in_flash_func(network_pak_handle_write)(uint16_t address,
                                                             const uint8_t data[NETWORK_PAK_BLOCK_SIZE],
                                                             uint8_t *crc_out) {
    network_pak_debug.write_count++;
    network_pak_debug.last_write_address = address;
    nice64_realtime_copy(network_pak_debug.last_request, data, sizeof(network_pak_debug.last_request));
    network_pak_handle_request(data);

    if (crc_out) {
        *crc_out = network_pak_data_crc(data);
    }

    return true;
}

void network_pak_get_debug(network_pak_debug_t *debug) {
    if (!debug) {
        return;
    }

    *debug = network_pak_debug;
    debug->bridge_pending = __atomic_load_n(&bridge_request_pending, __ATOMIC_ACQUIRE);
    debug->bridge_sent = __atomic_load_n(&bridge_request_sent, __ATOMIC_ACQUIRE);
    debug->current_sequence = bridge_sequence;
}

static void network_pak_bulk_clear_pending(void) {
    __atomic_store_n(&bridge_request_pending, false, __ATOMIC_RELEASE);
    __atomic_store_n(&bridge_request_sent, false, __ATOMIC_RELEASE);
    network_pak_debug.bridge_pending = false;
    network_pak_debug.bridge_sent = false;
}

static void network_pak_bulk_fail(const char *error) {
    bulk_stage_abort();
    network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE1, NETWORK_PAK_CMD_ERROR,
                            bridge_sequence, error, (uint8_t)strlen(error));
    snprintf(network_pak_debug.last_bridge_error,
             sizeof(network_pak_debug.last_bridge_error), "%s", error);
    network_pak_bulk_clear_pending();
}

static bool network_pak_parse_hex_field(const char *text,
                                        size_t digits,
                                        uint32_t *value) {
    if (text == NULL || value == NULL || strlen(text) != digits) return false;
    uint32_t parsed = 0u;
    for (size_t index = 0u; index < digits; ++index) {
        const int nibble = network_pak_hex_nibble(text[index]);
        if (nibble < 0) return false;
        parsed = (parsed << 4u) | (uint32_t)nibble;
    }
    *value = parsed;
    return true;
}

static bool network_pak_decode_bulk_hex(const char *text,
                                        uint8_t *output,
                                        uint16_t expected_length) {
    if (text == NULL || output == NULL || expected_length == 0u ||
        expected_length > BULK_STAGE_INTERNAL_SEGMENT_MAX ||
        strlen(text) != (size_t)expected_length * 2u) return false;
    for (uint16_t index = 0u; index < expected_length; ++index) {
        const int hi = network_pak_hex_nibble(text[index * 2u]);
        const int lo = network_pak_hex_nibble(text[index * 2u + 1u]);
        if (hi < 0 || lo < 0) return false;
        output[index] = (uint8_t)((hi << 4) | lo);
    }
    return true;
}

static void network_pak_emit_bulk_segment_request(
    const nice64_response_sink_t *response,
    const bulk_stage_snapshot_t *snapshot) {
    nice64_response_printf(
        response,
        "NETPAK_REQ_HEX|%u|BULK_READ_SEGMENT|B0%02X%04X%08lX%04X%04X\n",
        bridge_sequence,
        snapshot->stream_id,
        snapshot->generation,
        (unsigned long)snapshot->base_offset,
        snapshot->total_length,
        snapshot->valid_length);
    bridge_sent_us = time_us_32();
    network_pak_debug.bridge_req_count++;
}

static bool network_pak_handle_bulk_segment_line(
    const char *line,
    const nice64_response_sink_t *response) {
    static const char prefix[] = "NETPAK_BULK_SEG|";
    if (strncmp(line, prefix, sizeof(prefix) - 1u) != 0) return false;

    if (!__atomic_load_n(&bridge_request_pending, __ATOMIC_ACQUIRE) ||
        bridge_command != NETWORK_PAK_CMD_READ_STREAM_CHUNK ||
        bridge_payload_length != 10u ||
        bridge_payload[0] != NETWORK_PAK_BULK_BEGIN_MARKER) {
        bulk_stage_abort();
        snprintf(network_pak_debug.last_bridge_error,
                 sizeof(network_pak_debug.last_bridge_error), "BULK_UNEXPECTED");
        return true;
    }

    char copy[NICE64_NETWORK_RUDP_MESSAGE_MAX + 1u];
    const size_t line_length = strlen(line);
    if (line_length >= sizeof(copy)) {
        network_pak_bulk_fail("BULK_FRAME_LONG");
        return true;
    }
    memcpy(copy, line + sizeof(prefix) - 1u,
           line_length - (sizeof(prefix) - 1u) + 1u);

    char *fields[10];
    char *cursor = copy;
    for (size_t index = 0u; index < 9u; ++index) {
        fields[index] = cursor;
        cursor = strchr(cursor, '|');
        if (cursor == NULL) {
            network_pak_bulk_fail("BULK_BAD_FRAME");
            return true;
        }
        *cursor++ = '\0';
    }
    fields[9] = cursor;
    if (strchr(fields[9], '|') != NULL) {
        network_pak_bulk_fail("BULK_BAD_FRAME");
        return true;
    }

    char *sequence_end = NULL;
    const unsigned long sequence = strtoul(fields[0], &sequence_end, 10);
    uint32_t generation = 0u, stream = 0u, base_offset = 0u;
    uint32_t total_length = 0u, segment_offset = 0u, data_length = 0u;
    uint32_t crc32 = 0u, flags = 0u;
    if (sequence_end == fields[0] || *sequence_end != '\0' || sequence > 255u ||
        !network_pak_parse_hex_field(fields[1], 4u, &generation) ||
        !network_pak_parse_hex_field(fields[2], 2u, &stream) ||
        !network_pak_parse_hex_field(fields[3], 8u, &base_offset) ||
        !network_pak_parse_hex_field(fields[4], 4u, &total_length) ||
        !network_pak_parse_hex_field(fields[5], 4u, &segment_offset) ||
        !network_pak_parse_hex_field(fields[6], 2u, &data_length) ||
        !network_pak_parse_hex_field(fields[7], 8u, &crc32) ||
        !network_pak_parse_hex_field(fields[8], 2u, &flags) ||
        sequence != bridge_sequence || data_length == 0u ||
        data_length > BULK_STAGE_INTERNAL_SEGMENT_MAX) {
        network_pak_bulk_fail("BULK_BAD_META");
        return true;
    }

    uint8_t segment[BULK_STAGE_INTERNAL_SEGMENT_MAX];
    if (!network_pak_decode_bulk_hex(fields[9], segment, (uint16_t)data_length) ||
        !bulk_stage_accept_segment((uint8_t)stream, (uint16_t)generation,
                                   base_offset, (uint16_t)total_length,
                                   (uint16_t)segment_offset, segment,
                                   (uint16_t)data_length, crc32,
                                   (flags & 1u) != 0u)) {
        network_pak_bulk_fail("BULK_STAGE_ABORT");
        return true;
    }

    bulk_stage_snapshot_t snapshot = bulk_stage_snapshot();
    if (snapshot.valid_length < snapshot.total_length) {
        network_pak_emit_bulk_segment_request(response, &snapshot);
        return true;
    }
    if (!bulk_stage_commit()) {
        network_pak_bulk_fail("BULK_CRC_FAIL");
        return true;
    }

    snapshot = bulk_stage_snapshot();
    uint8_t ready[14];
    ready[0] = snapshot.stream_id;
    ready[1] = (uint8_t)(snapshot.generation >> 8u);
    ready[2] = (uint8_t)snapshot.generation;
    ready[3] = (uint8_t)(snapshot.base_offset >> 24u);
    ready[4] = (uint8_t)(snapshot.base_offset >> 16u);
    ready[5] = (uint8_t)(snapshot.base_offset >> 8u);
    ready[6] = (uint8_t)snapshot.base_offset;
    ready[7] = (uint8_t)(snapshot.total_length >> 8u);
    ready[8] = (uint8_t)snapshot.total_length;
    ready[9] = (uint8_t)(snapshot.expected_crc32 >> 24u);
    ready[10] = (uint8_t)(snapshot.expected_crc32 >> 16u);
    ready[11] = (uint8_t)(snapshot.expected_crc32 >> 8u);
    ready[12] = (uint8_t)snapshot.expected_crc32;
    ready[13] = snapshot.stream_end ? 1u : 0u;
    network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE1,
                            NETWORK_PAK_CMD_BULK_READY,
                            bridge_sequence, ready, sizeof(ready));
    snprintf(network_pak_debug.last_bridge_error,
             sizeof(network_pak_debug.last_bridge_error), "NONE");
    network_pak_debug.bridge_rsp_count++;
    network_pak_bulk_clear_pending();
    return true;
}

bool network_pak_handle_serial_line(const char *line,
                                    const nice64_response_sink_t *response) {
    if (line == NULL) {
        return false;
    }

    if (network_pak_handle_bulk_segment_line(line, response)) return true;

    if (strcmp(line, "NETPAK_HELLO|1") == 0) {
        const network_pak_owner_t sender = nice64_tcp_is_response_sink(response)
            ? NETWORK_PAK_OWNER_TCP : NETWORK_PAK_OWNER_USB;
        if (bridge_owner != NETWORK_PAK_OWNER_NONE && bridge_owner != sender) {
            nice64_response_puts(response, "NICE64|BUSY");
            return true;
        }
        bridge_owner = sender;
        network_pak_debug.bridge_connected = true;
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "NONE");
        nice64_response_puts(response, "NETPAK_HELLO_ACK|1");
        return true;
    }

    if (strcmp(line, "NETPAK_BYE") == 0) {
        const network_pak_owner_t sender = nice64_tcp_is_response_sink(response)
            ? NETWORK_PAK_OWNER_TCP : NETWORK_PAK_OWNER_USB;
        if (bridge_owner != NETWORK_PAK_OWNER_NONE && bridge_owner != sender) {
            nice64_response_puts(response, "NICE64|BUSY");
            return true;
        }
        bridge_owner = NETWORK_PAK_OWNER_NONE;
        network_pak_debug.bridge_connected = false;
        __atomic_store_n(&bridge_request_pending, false, __ATOMIC_RELEASE);
        __atomic_store_n(&bridge_request_sent, false, __ATOMIC_RELEASE);
        network_pak_debug.bridge_pending = false;
        network_pak_debug.bridge_sent = false;
        bulk_stage_reset();
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "OFFLINE");
        nice64_response_puts(response, "NETPAK_BYE_ACK");
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

        if (!__atomic_load_n(&bridge_request_pending, __ATOMIC_ACQUIRE) || parsed_sequence != bridge_sequence) {
            snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "SEQ_MISMATCH");
            return true;
        }

        if (!payload_valid || parsed_command > 0xFFu) {
            network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE1, NETWORK_PAK_CMD_ERROR, bridge_sequence, "BRIDGE_BAD_HEX", 14);
            snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "BRIDGE_BAD_HEX");
        } else if (strcmp(status_text, "OK") == 0) {
            network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE1, (uint8_t)parsed_command, bridge_sequence, payload, payload_length);
            snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "NONE");
        } else {
            network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE1, NETWORK_PAK_CMD_ERROR, bridge_sequence, payload, payload_length);
            snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "%.*s",
                     payload_length, (const char *)payload);
        }

        snprintf(network_pak_debug.last_bridge_rsp,
                 sizeof(network_pak_debug.last_bridge_rsp),
                 "CMD=%02lX LEN=%u",
                 parsed_command,
                 payload_length);
        network_pak_debug.bridge_rsp_count++;
        __atomic_store_n(&bridge_request_pending, false, __ATOMIC_RELEASE);
        __atomic_store_n(&bridge_request_sent, false, __ATOMIC_RELEASE);
        network_pak_debug.bridge_pending = false;
        network_pak_debug.bridge_sent = false;
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
    if (!__atomic_load_n(&bridge_request_pending, __ATOMIC_ACQUIRE) || parsed_sequence != bridge_sequence) {
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "SEQ_MISMATCH");
        return true;
    }

    const uint8_t payload_length = (uint8_t)strnlen(payload, NETWORK_PAK_MAX_PAYLOAD);
    if (strcmp(status_text, "OK") == 0) {
        network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE1, NETWORK_PAK_CMD_BRIDGE_RESPONSE, bridge_sequence, payload, payload_length);
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "NONE");
    } else {
        network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE1, NETWORK_PAK_CMD_ERROR, bridge_sequence, payload, payload_length);
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "%.*s",
                 NETWORK_PAK_MAX_PAYLOAD, payload);
    }

    snprintf(network_pak_debug.last_bridge_rsp, sizeof(network_pak_debug.last_bridge_rsp), "%.*s",
             NETWORK_PAK_MAX_PAYLOAD, payload);
    network_pak_debug.bridge_rsp_count++;
    __atomic_store_n(&bridge_request_pending, false, __ATOMIC_RELEASE);
    __atomic_store_n(&bridge_request_sent, false, __ATOMIC_RELEASE);
    network_pak_debug.bridge_pending = false;
    network_pak_debug.bridge_sent = false;
    return true;
}

bool network_pak_handle_bridge_line(
    const char *line,
    const nice64_response_sink_t *response) {
    if (network_pak_handle_serial_line(line, response)) {
        return true;
    }

    if (strcmp(line, "STAT") == 0 || strcmp(line, "stat") == 0) {
        network_pak_debug_t debug;
        const nice64_network_rudp_diagnostics_t *rudp =
            nice64_network_rudp_diagnostics();
        network_pak_get_debug(&debug);
        nice64_response_printf(
            response,
            "STAT SERVICE NETWORK bridge=%u pending=%u sent=%u seq=%u reads=%lu writes=%lu breq=%lu brsp=%lu bto=%lu cmd=0x%02X err=%s rsp=%s nrudp_sessions=%lu nrudp_req_rx=%lu nrudp_rsp_tx=%lu nrudp_req_bytes=%lu nrudp_rsp_bytes=%lu nrudp_deliveries=%lu nrudp_dup_deliveries=%lu nrudp_invalid=%lu nrudp_resets=%lu\n",
            debug.bridge_connected ? 1u : 0u,
            debug.bridge_pending ? 1u : 0u,
            debug.bridge_sent ? 1u : 0u,
            (unsigned)debug.current_sequence,
            debug.read_count,
            debug.write_count,
            debug.bridge_req_count,
            debug.bridge_rsp_count,
            debug.bridge_timeout_count,
            debug.last_bridge_cmd,
            debug.last_bridge_error,
            debug.last_bridge_rsp,
            rudp->network_rudp_sessions,
            rudp->network_rudp_requests_rx,
            rudp->network_rudp_responses_tx,
            rudp->network_rudp_request_bytes,
            rudp->network_rudp_response_bytes,
            rudp->network_rudp_application_deliveries,
            rudp->network_rudp_duplicate_application_deliveries,
            rudp->network_rudp_invalid_messages,
            rudp->network_rudp_session_resets);
        return true;
    }

    if (line[0] != '\0') {
        nice64_response_puts(response, "ERR NETWORK");
        return true;
    }

    return false;
}

bool network_pak_rudp_session_opened(void) {
    if (bridge_owner != NETWORK_PAK_OWNER_NONE &&
        bridge_owner != NETWORK_PAK_OWNER_RUDP) {
        return false;
    }
    bridge_owner = NETWORK_PAK_OWNER_RUDP;
    network_pak_debug.bridge_connected = true;
    snprintf(network_pak_debug.last_bridge_error,
             sizeof(network_pak_debug.last_bridge_error), "NONE");
    return true;
}

void network_pak_rudp_session_closed(void) {
    if (bridge_owner != NETWORK_PAK_OWNER_RUDP) return;
    bridge_owner = NETWORK_PAK_OWNER_NONE;
    network_pak_debug.bridge_connected = false;
    __atomic_store_n(&bridge_request_pending, false, __ATOMIC_RELEASE);
    __atomic_store_n(&bridge_request_sent, false, __ATOMIC_RELEASE);
    network_pak_debug.bridge_pending = false;
    network_pak_debug.bridge_sent = false;
    bulk_stage_reset();
    snprintf(network_pak_debug.last_bridge_error,
             sizeof(network_pak_debug.last_bridge_error), "OFFLINE");
}

bool network_pak_handle_rudp_line(
    const char *line,
    const nice64_response_sink_t *response) {
    return bridge_owner == NETWORK_PAK_OWNER_RUDP &&
           network_pak_handle_serial_line(line, response);
}

static void network_pak_dispatch_usb_line(void *context, const char *line) {
    const nice64_response_sink_t *response = context;
    if (!nice64_tcp_usb_command_allowed(line)) {
        nice64_response_puts(response, "NICE64|BUSY|OWNER=TCP");
        return;
    }
    bridge_line_dispatch(line, response, network_pak_handle_bridge_line);
}

static void network_pak_poll_serial(const nice64_response_sink_t *response) {
    static char line[NETWORK_PAK_USB_LINE_CAPACITY];
    static nice64_line_framer_t framer;
    static bool initialized = false;

    if (!initialized) {
        nice64_line_framer_init(&framer, line, sizeof(line));
        initialized = true;
    }

    nice64_usb_poll_lines(&framer, network_pak_dispatch_usb_line, (void *)response);
}

static bool network_pak_try_serve_staged(uint8_t sequence,
                                         const uint8_t *payload,
                                         uint8_t payload_length) {
    if (payload_length != 8u || payload[0] != NETWORK_PAK_BULK_READ_MARKER) {
        return false;
    }
    const uint8_t stream_id = payload[1];
    const uint16_t generation = (uint16_t)(((uint16_t)payload[2] << 8u) | payload[3]);
    const uint32_t offset = ((uint32_t)payload[4] << 24u) |
                            ((uint32_t)payload[5] << 16u) |
                            ((uint32_t)payload[6] << 8u) |
                            payload[7];
    uint8_t data[BULK_STAGE_DELIVERY_MAX];
    uint8_t data_length = 0u;
    bool stream_end = false;
    if (!bulk_stage_read(stream_id, generation, offset, data, &data_length,
                         &stream_end)) {
        network_pak_bulk_fail("BULK_STAGE_MISS");
        return true;
    }

    uint8_t response_payload[7u + BULK_STAGE_DELIVERY_MAX];
    response_payload[0] = stream_id;
    response_payload[1] = (uint8_t)(offset >> 24u);
    response_payload[2] = (uint8_t)(offset >> 16u);
    response_payload[3] = (uint8_t)(offset >> 8u);
    response_payload[4] = (uint8_t)offset;
    response_payload[5] = data_length;
    response_payload[6] = stream_end ? 1u : 0u;
    memcpy(&response_payload[7], data, data_length);
    network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE1,
                            NETWORK_PAK_CMD_STREAM_CHUNK,
                            sequence, response_payload,
                            (uint8_t)(7u + data_length));
    network_pak_bulk_clear_pending();
    return true;
}

static void network_pak_poll_bridge(const nice64_response_sink_t *response) {
    bool emit_request = false;
    bool timed_out = false;
    uint8_t command = 0;
    uint8_t sequence = 0;
    uint8_t payload[NETWORK_PAK_MAX_PAYLOAD + 1u];
    uint8_t payload_length = 0;
    memset(payload, 0, sizeof(payload));

    const uint32_t now = time_us_32();
    if (__atomic_load_n(&bridge_request_pending, __ATOMIC_ACQUIRE) &&
        !__atomic_load_n(&bridge_request_sent, __ATOMIC_ACQUIRE)) {
        command = bridge_command;
        sequence = bridge_sequence;
        payload_length = bridge_payload_length;
        memcpy(payload, bridge_payload, payload_length);
        __atomic_store_n(&bridge_request_sent, true, __ATOMIC_RELEASE);
        bridge_sent_us = now;
        network_pak_debug.bridge_req_count++;
        network_pak_debug.bridge_sent = true;
        if (command == NETWORK_PAK_CMD_READ_STREAM_CHUNK &&
            network_pak_try_serve_staged(sequence, payload, payload_length)) {
            emit_request = false;
        } else {
            emit_request = true;
        }
    } else if (__atomic_load_n(&bridge_request_pending, __ATOMIC_ACQUIRE) &&
               __atomic_load_n(&bridge_request_sent, __ATOMIC_ACQUIRE)
               && (uint32_t)(now - bridge_sent_us) >= NETWORK_PAK_BRIDGE_TIMEOUT_US) {
        sequence = bridge_sequence;
        network_pak_make_packet(NETWORK_PAK_RESPONSE_CORE1, NETWORK_PAK_CMD_ERROR, sequence, "BRIDGE_TIMEOUT", 14);
        network_pak_debug.bridge_timeout_count++;
        snprintf(network_pak_debug.last_bridge_rsp, sizeof(network_pak_debug.last_bridge_rsp), "BRIDGE_TIMEOUT");
        snprintf(network_pak_debug.last_bridge_error, sizeof(network_pak_debug.last_bridge_error), "BRIDGE_TIMEOUT");
        __atomic_store_n(&bridge_request_pending, false, __ATOMIC_RELEASE);
        __atomic_store_n(&bridge_request_sent, false, __ATOMIC_RELEASE);
        network_pak_debug.bridge_pending = false;
        network_pak_debug.bridge_sent = false;
        bulk_stage_abort();
        timed_out = true;
    }

    if (emit_request) {
        if (command == NETWORK_PAK_CMD_GET_STREAM_INFO &&
            payload_length == 4u && memcmp(payload, "BCAP", 4u) == 0) {
            nice64_response_printf(response,
                "NETPAK_REQ_HEX|%u|GET_APPLICATION_CAPABILITIES|42434150\n",
                sequence);
        } else if (command == NETWORK_PAK_CMD_READ_STREAM_CHUNK &&
            payload_length == 10u && payload[0] == NETWORK_PAK_BULK_BEGIN_MARKER) {
            nice64_response_printf(response, "NETPAK_REQ_HEX|%u|BULK_READ|", sequence);
            for (uint8_t i = 0; i < payload_length; ++i) {
                nice64_response_printf(response, "%02X", payload[i]);
            }
            nice64_response_putc(response, '\n');
        } else if (network_pak_is_stream_command(command)) {
            nice64_response_printf(response, "NETPAK_REQ_HEX|%u|%s|", sequence, network_pak_bridge_command_name(command));
            for (uint8_t i = 0; i < payload_length; ++i) {
                nice64_response_printf(response, "%02X", payload[i]);
            }
            nice64_response_putc(response, '\n');
        } else {
            nice64_response_printf(response, "NETPAK_REQ|%u|%s|%s\n",
                   sequence,
                   network_pak_bridge_command_name(command),
                   (char *)payload);
        }
    }

    if (timed_out) {
        nice64_response_printf(response, "NETPAK_TIMEOUT|%u\n", sequence);
    }
}

void network_pak_core1_service(void) {
    if (!bulk_stage_core1_initialized) {
        bulk_stage_reset();
        bulk_stage_core1_initialized = true;
    }
    const nice64_response_sink_t *usb = nice64_usb_response_sink();
    network_pak_poll_serial(usb);

    if (bridge_owner == NETWORK_PAK_OWNER_TCP && !nice64_tcp_session_active()) {
        bridge_owner = NETWORK_PAK_OWNER_NONE;
        network_pak_debug.bridge_connected = false;
        __atomic_store_n(&bridge_request_pending, false, __ATOMIC_RELEASE);
        __atomic_store_n(&bridge_request_sent, false, __ATOMIC_RELEASE);
        bulk_stage_reset();
    }

    if (bridge_owner == NETWORK_PAK_OWNER_RUDP &&
        !nice64_network_rudp_active()) {
        network_pak_rudp_session_closed();
    }

    const nice64_response_sink_t *active = bridge_owner == NETWORK_PAK_OWNER_TCP
        ? nice64_tcp_response_sink()
        : bridge_owner == NETWORK_PAK_OWNER_RUDP
            ? nice64_network_rudp_response_sink() : usb;
    if (active != NULL) {
        network_pak_poll_bridge(active);
    }
}
