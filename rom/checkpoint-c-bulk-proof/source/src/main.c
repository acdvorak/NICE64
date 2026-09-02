#include <libdragon.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define NETWORK_PAK_PORT 1
#define NETWORK_PAK_ADDR_COMMAND 0x8000u
#define NETWORK_PAK_ADDR_RESPONSE 0x8020u
#define WIRE_SIZE 32u
#define MAX_PAYLOAD 23u
#define READ16_SIZE 16u
#define BULK_MAX 512u
#define BULK_MARKER_BEGIN 0xB0u
#define BULK_MARKER_READ 0xB1u
#define CMD_OPEN_STREAM 0x20u
#define CMD_GET_STREAM_INFO 0x21u
#define CMD_READ_STREAM_CHUNK 0x22u
#define CMD_CLOSE_STREAM 0x24u
#define RSP_STATUS 0x82u
#define RSP_STREAM_OPENED 0xA0u
#define RSP_STREAM_INFO 0xA1u
#define RSP_STREAM_CHUNK 0xA2u
#define RSP_STREAM_CLOSED 0xA3u
#define RSP_BULK_READY 0xA5u
#define RSP_ERROR 0xFFu
#define WAIT_POLLS 4000u
#define WAIT_MS 5u

typedef struct __attribute__((packed)) {
    uint16_t magic;
    uint8_t version;
    uint8_t command;
    uint8_t flags;
    uint8_t sequence;
    uint8_t payload_length;
    uint8_t payload[MAX_PAYLOAD];
    uint16_t checksum;
} packet_t;

typedef enum { MODE_LEGACY, MODE_BULK, MODE_AUTO } proof_mode_t;

typedef struct {
    bool pass;
    bool capability;
    bool fallback;
    uint32_t requested;
    uint32_t received;
    uint32_t n64_chunks;
    uint32_t staged_fetches;
    uint32_t bridge_round_trips;
    uint32_t elapsed_ms;
    uint32_t expected_crc;
    uint32_t actual_crc;
    uint32_t timeouts;
    uint32_t protocol_errors;
    uint32_t staging_aborts;
} proof_result_t;

static uint8_t sequence;
static uint16_t generation = 1u;
static uint8_t data_buffer[65536];
static proof_result_t result;
static proof_mode_t selected_mode = MODE_AUTO;
static int selected_size;
static const uint32_t sizes[] = { 4096u, 16384u, 65536u };
static const char *resources[] = { "speed://4k", "speed://16k", "speed://64k" };

static uint16_t read_be16(const uint8_t *p) { return (uint16_t)(((uint16_t)p[0] << 8) | p[1]); }
static uint32_t read_be32(const uint8_t *p) {
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) |
           ((uint32_t)p[2] << 8) | p[3];
}
static void write_be16(uint8_t *p, uint16_t value) { p[0] = value >> 8; p[1] = value; }
static void write_be32(uint8_t *p, uint32_t value) {
    p[0] = value >> 24; p[1] = value >> 16; p[2] = value >> 8; p[3] = value;
}

static uint16_t packet_checksum(const packet_t *packet) {
    const uint8_t *bytes = (const uint8_t *)packet;
    uint16_t checksum = 0u;
    for (int i = 0; i < 7; ++i) checksum += bytes[i];
    for (uint8_t i = 0u; i < packet->payload_length && i < MAX_PAYLOAD; ++i)
        checksum += packet->payload[i];
    return (uint16_t)~checksum;
}

static void packet_init(packet_t *packet, uint8_t command,
                        const void *payload, uint8_t payload_length) {
    memset(packet, 0, sizeof(*packet));
    packet->magic = 0x4E50u;
    packet->version = 1u;
    packet->command = command;
    packet->sequence = sequence++;
    packet->payload_length = payload_length > MAX_PAYLOAD ? MAX_PAYLOAD : payload_length;
    if (payload != NULL && packet->payload_length != 0u)
        memcpy(packet->payload, payload, packet->payload_length);
    packet->checksum = packet_checksum(packet);
}

static bool packet_valid(const packet_t *packet) {
    return packet->magic == 0x4E50u && packet->version == 1u &&
           packet->payload_length <= MAX_PAYLOAD &&
           packet->checksum == packet_checksum(packet);
}

static bool bridge_pending(const packet_t *packet) {
    static const char text[] = "BRIDGE_PENDING";
    return packet_valid(packet) && packet->command == RSP_STATUS &&
           packet->payload_length == sizeof(text) - 1u &&
           memcmp(packet->payload, text, sizeof(text) - 1u) == 0;
}

static bool exchange(const packet_t *request, packet_t *response) {
    int io = joybus_accessory_write(NETWORK_PAK_PORT, NETWORK_PAK_ADDR_COMMAND,
                                    (const uint8_t *)request);
    if (io != JOYBUS_ACCESSORY_IO_STATUS_OK && io != JOYBUS_ACCESSORY_IO_STATUS_BAD_CRC) {
        result.protocol_errors++;
        return false;
    }
    wait_ms(1);
    for (uint16_t poll = 0u; poll < WAIT_POLLS; ++poll) {
        io = joybus_accessory_read(NETWORK_PAK_PORT, NETWORK_PAK_ADDR_RESPONSE,
                                   (uint8_t *)response);
        if (io != JOYBUS_ACCESSORY_IO_STATUS_OK || !packet_valid(response)) {
            result.protocol_errors++;
            return false;
        }
        if (!bridge_pending(response)) return response->command != RSP_ERROR;
        wait_ms(WAIT_MS);
    }
    result.timeouts++;
    return false;
}

static uint32_t crc32(const uint8_t *data, uint32_t length) {
    uint32_t crc = 0xFFFFFFFFu;
    for (uint32_t i = 0; i < length; ++i) {
        crc ^= data[i];
        for (uint8_t bit = 0; bit < 8u; ++bit)
            crc = (crc >> 1u) ^ (0xEDB88320u & (uint32_t)-(int32_t)(crc & 1u));
    }
    return ~crc;
}

static bool query_capability(void) {
    packet_t request, response;
    static const uint8_t query[] = { 'B', 'C', 'A', 'P' };
    packet_init(&request, CMD_GET_STREAM_INFO, query, sizeof(query));
    if (!exchange(&request, &response) || response.command != RSP_STREAM_INFO ||
        response.payload_length != 8u) return false;
    return response.payload[0] == 'B' && response.payload[1] == 'C' &&
           response.payload[2] == 1u && (response.payload[3] & 1u) != 0u &&
           read_be16(&response.payload[4]) == BULK_MAX &&
           response.payload[6] == READ16_SIZE;
}

static bool open_stream(const char *resource, uint8_t *stream_id,
                        uint32_t *length, uint32_t *expected_crc) {
    packet_t request, response;
    packet_init(&request, CMD_OPEN_STREAM, resource, (uint8_t)strlen(resource));
    if (!exchange(&request, &response) || response.command != RSP_STREAM_OPENED ||
        response.payload_length != 10u) return false;
    *stream_id = response.payload[0];
    *length = read_be32(&response.payload[1]);
    *expected_crc = read_be32(&response.payload[6]);
    return *stream_id != 0u;
}

static void close_stream(uint8_t stream_id) {
    packet_t request, response;
    packet_init(&request, CMD_CLOSE_STREAM, &stream_id, 1u);
    (void)exchange(&request, &response);
}

static bool read_legacy(uint8_t stream_id, uint32_t length) {
    packet_t request, response;
    for (uint32_t offset = 0u; offset < length; offset += READ16_SIZE) {
        uint8_t payload[5];
        payload[0] = stream_id;
        write_be32(&payload[1], offset);
        packet_init(&request, CMD_READ_STREAM_CHUNK, payload, sizeof(payload));
        result.bridge_round_trips++;
        if (!exchange(&request, &response) || response.command != RSP_STREAM_CHUNK ||
            response.payload_length < 7u || response.payload[0] != stream_id ||
            read_be32(&response.payload[1]) != offset || response.payload[5] > READ16_SIZE ||
            response.payload_length != 7u + response.payload[5]) return false;
        memcpy(&data_buffer[offset], &response.payload[7], response.payload[5]);
        result.received += response.payload[5];
        result.n64_chunks++;
    }
    return result.received == length;
}

static bool begin_bulk(uint8_t stream_id, uint32_t offset, uint16_t length,
                       uint16_t operation) {
    packet_t request, response;
    uint8_t payload[10];
    payload[0] = BULK_MARKER_BEGIN;
    payload[1] = stream_id;
    write_be16(&payload[2], operation);
    write_be32(&payload[4], offset);
    write_be16(&payload[8], length);
    packet_init(&request, CMD_READ_STREAM_CHUNK, payload, sizeof(payload));
    result.staged_fetches++;
    result.bridge_round_trips += (length + 223u) / 224u;
    if (!exchange(&request, &response) || response.command != RSP_BULK_READY ||
        response.payload_length != 14u || response.payload[0] != stream_id ||
        read_be16(&response.payload[1]) != operation ||
        read_be32(&response.payload[3]) != offset ||
        read_be16(&response.payload[7]) != length) {
        result.staging_aborts++;
        return false;
    }
    return true;
}

static bool read_bulk(uint8_t stream_id, uint32_t length) {
    packet_t request, response;
    uint32_t block_base = 0u;
    while (block_base < length) {
        uint16_t block_length = length - block_base > BULK_MAX
            ? BULK_MAX : (uint16_t)(length - block_base);
        uint16_t operation = generation++;
        if (generation == 0u) generation = 1u;
        if (!begin_bulk(stream_id, block_base, block_length, operation)) return false;
        for (uint16_t relative = 0u; relative < block_length; relative += READ16_SIZE) {
            uint32_t offset = block_base + relative;
            uint8_t payload[8];
            payload[0] = BULK_MARKER_READ;
            payload[1] = stream_id;
            write_be16(&payload[2], operation);
            write_be32(&payload[4], offset);
            packet_init(&request, CMD_READ_STREAM_CHUNK, payload, sizeof(payload));
            if (!exchange(&request, &response) || response.command != RSP_STREAM_CHUNK ||
                response.payload_length < 7u || response.payload[0] != stream_id ||
                read_be32(&response.payload[1]) != offset || response.payload[5] > READ16_SIZE ||
                response.payload_length != 7u + response.payload[5]) {
                result.staging_aborts++;
                return false;
            }
            memcpy(&data_buffer[offset], &response.payload[7], response.payload[5]);
            result.received += response.payload[5];
            result.n64_chunks++;
        }
        block_base += block_length;
    }
    return result.received == length;
}

static void run_proof(void) {
    uint8_t stream_id = 0u;
    uint32_t stream_length = 0u;
    memset(&result, 0, sizeof(result));
    result.requested = sizes[selected_size];
    result.capability = query_capability();
    bool use_bulk = selected_mode != MODE_LEGACY && result.capability;
    if (selected_mode == MODE_AUTO && !result.capability) result.fallback = true;
    if (selected_mode == MODE_BULK && !result.capability) {
        result.protocol_errors++;
        return;
    }
    if (!open_stream(resources[selected_size], &stream_id, &stream_length,
                     &result.expected_crc) || stream_length != result.requested) return;

    uint64_t started = get_ticks();
    bool ok = use_bulk ? read_bulk(stream_id, stream_length)
                       : read_legacy(stream_id, stream_length);
    if (!ok && use_bulk && selected_mode == MODE_AUTO) {
        result.fallback = true;
        result.received = result.n64_chunks = result.staged_fetches = 0u;
        result.bridge_round_trips = 0u;
        ok = read_legacy(stream_id, stream_length);
    }
    uint64_t elapsed = get_ticks() - started;
    result.elapsed_ms = (uint32_t)(elapsed * 1000u / TICKS_PER_SECOND);
    result.actual_crc = crc32(data_buffer, result.received);
    result.pass = ok && result.received == result.requested &&
                  result.actual_crc == result.expected_crc &&
                  result.timeouts == 0u && result.protocol_errors == 0u;
    close_stream(stream_id);
}

static const char *mode_name(void) {
    return selected_mode == MODE_LEGACY ? "LEGACY READ16" :
           selected_mode == MODE_BULK ? "BULK_READ_V1" : "AUTO";
}

static void draw(void) {
    surface_t *surface = display_get();
    graphics_fill_screen(surface, graphics_make_color(10, 18, 32, 255));
    char line[96];
    graphics_draw_text(surface, 10, 8, "NICE64 CHECKPOINT C BULK PROOF");
    snprintf(line, sizeof(line), "MODE: %s   SIZE: %lu KiB", mode_name(),
             (unsigned long)(sizes[selected_size] / 1024u));
    graphics_draw_text(surface, 10, 24, line);
    graphics_draw_text(surface, 10, 36, "D-PAD: mode/size   A: run");
    snprintf(line, sizeof(line), "RESULT: %s  CAP:%s FALLBACK:%s",
             result.pass ? "PASS" : "--", result.capability ? "YES" : "NO",
             result.fallback ? "YES" : "NO");
    graphics_draw_text(surface, 10, 56, line);
    snprintf(line, sizeof(line), "BYTES %lu/%lu  N64 CHUNKS %lu",
             (unsigned long)result.received, (unsigned long)result.requested,
             (unsigned long)result.n64_chunks);
    graphics_draw_text(surface, 10, 68, line);
    snprintf(line, sizeof(line), "STAGED FETCHES %lu  BRIDGE RT %lu",
             (unsigned long)result.staged_fetches,
             (unsigned long)result.bridge_round_trips);
    graphics_draw_text(surface, 10, 80, line);
    snprintf(line, sizeof(line), "TIME %lu ms  RATE %lu.%02lu KiB/s",
             (unsigned long)result.elapsed_ms,
             result.elapsed_ms ? (unsigned long)(result.received * 1000u / result.elapsed_ms / 1024u) : 0u,
             result.elapsed_ms ? (unsigned long)((result.received * 100000u / result.elapsed_ms / 1024u) % 100u) : 0u);
    graphics_draw_text(surface, 10, 92, line);
    snprintf(line, sizeof(line), "CRC EXP %08lX ACT %08lX",
             (unsigned long)result.expected_crc, (unsigned long)result.actual_crc);
    graphics_draw_text(surface, 10, 104, line);
    snprintf(line, sizeof(line), "TIMEOUT %lu PROTO %lu ABORT %lu",
             (unsigned long)result.timeouts, (unsigned long)result.protocol_errors,
             (unsigned long)result.staging_aborts);
    graphics_draw_text(surface, 10, 116, line);
    graphics_draw_text(surface, 10, 136, "CONTROL: Wi-Fi/TCP first");
    graphics_draw_text(surface, 10, 148, "CARRIER: selected in Bridge");
    display_show(surface);
}

int main(void) {
    timer_init();
    joypad_init();
    debug_init_emulog();
    display_init(RESOLUTION_320x240, DEPTH_16_BPP, 2, GAMMA_NONE, FILTERS_RESAMPLE);
    graphics_set_default_font();
    memset(&result, 0, sizeof(result));
    while (1) {
        joypad_poll();
        joypad_buttons_t pressed = joypad_get_buttons_pressed(JOYPAD_PORT_1);
        if (pressed.d_left && selected_size > 0) selected_size--;
        if (pressed.d_right && selected_size < 2) selected_size++;
        if (pressed.d_up && selected_mode > MODE_LEGACY) selected_mode--;
        if (pressed.d_down && selected_mode < MODE_AUTO) selected_mode++;
        if (pressed.a) run_proof();
        draw();
        wait_ms(16);
    }
}
