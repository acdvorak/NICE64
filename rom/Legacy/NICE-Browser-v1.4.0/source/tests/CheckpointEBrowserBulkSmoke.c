#include "image_transport.h"
#include "logical_stream_reader.h"
#include "network_pak_transport.h"
#include "protocol.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef enum mock_capability_e
{
    MOCK_CAPABILITY_BULK,
    MOCK_CAPABILITY_UNSUPPORTED,
    MOCK_CAPABILITY_MALFORMED,
    MOCK_CAPABILITY_UNAVAILABLE
} mock_capability_t;

static uint8_t g_fixture[65536];
static uint32_t g_fixture_length;
static uint32_t g_fixture_crc;
static mock_capability_t g_capability;
static bool g_fail_bulk_begin;
static uint8_t g_stream_id = 7u;
static uint16_t g_stage_generation;
static uint32_t g_stage_base;
static uint16_t g_stage_length;
static uint32_t g_exchange_count;
static uint32_t g_legacy_requests;
static uint32_t g_bulk_begins;
static uint32_t g_bulk_reads;
static uint32_t g_close_requests;
static uint32_t g_fail_bulk_read_offset = UINT32_MAX;
static bool g_fail_close;
static uint64_t g_ticks;
static const char *g_last_transport_error;
static uint32_t g_bad_sequence_exchange;
static uint32_t g_sequence_wraps;

surface_t surface_make_linear(void *buffer, int format, uint16_t width, uint16_t height)
{
    (void)format;
    surface_t surface = { buffer, width, height, width };
    return surface;
}

uint64_t get_ticks(void)
{
    return ++g_ticks;
}

void debugf(const char *format, ...)
{
    (void)format;
}

static void require(bool condition, const char *message)
{
    if (!condition)
    {
        fprintf(stderr, "FAIL %s\n", message);
        exit(1);
    }
}

static uint16_t read_be16(const uint8_t *data)
{
    return (uint16_t)(((uint16_t)data[0] << 8) | data[1]);
}

static uint32_t read_be32(const uint8_t *data)
{
    return ((uint32_t)data[0] << 24) | ((uint32_t)data[1] << 16) |
        ((uint32_t)data[2] << 8) | data[3];
}

static void write_be16(uint8_t *data, uint16_t value)
{
    data[0] = (uint8_t)(value >> 8);
    data[1] = (uint8_t)value;
}

static void write_be32(uint8_t *data, uint32_t value)
{
    data[0] = (uint8_t)(value >> 24);
    data[1] = (uint8_t)(value >> 16);
    data[2] = (uint8_t)(value >> 8);
    data[3] = (uint8_t)value;
}

static uint32_t crc32(const uint8_t *data, uint32_t length)
{
    uint32_t crc = 0xFFFFFFFFu;
    for (uint32_t index = 0; index < length; index++)
    {
        crc ^= data[index];
        for (uint8_t bit = 0; bit < 8u; bit++)
        {
            crc = (crc >> 1u) ^ (0xEDB88320u & (uint32_t)-(int32_t)(crc & 1u));
        }
    }
    return ~crc;
}

static void respond(
    nice_net_packet_t *response,
    const nice_net_packet_t *request,
    nice_net_command_t command,
    const void *payload,
    size_t payload_length)
{
    uint8_t sequence = request->sequence;
    if (g_bad_sequence_exchange != 0u && g_exchange_count == g_bad_sequence_exchange)
    {
        sequence = (uint8_t)(sequence - 1u);
    }
    nice_net_packet_init(response, command, 0, sequence, payload, payload_length);
}

bool network_pak_exchange(
    const nice_net_packet_t *request,
    nice_net_packet_t *response,
    nice_net_error_t *error)
{
    uint8_t payload[NICE_NET_MAX_PAYLOAD];
    g_exchange_count++;
    if (request->sequence == 0u) g_sequence_wraps++;
    if (error != NULL) *error = NICE_NET_ERR_NONE;

    if (request->command == NICE_NET_CMD_GET_STREAM_INFO &&
        request->payload_length == 4u && memcmp(request->payload, "BCAP", 4u) == 0)
    {
        if (g_capability == MOCK_CAPABILITY_UNAVAILABLE)
        {
            if (error != NULL) *error = NICE_NET_ERR_TRANSPORT_TIMEOUT;
            return false;
        }
        if (g_capability == MOCK_CAPABILITY_UNSUPPORTED)
        {
            respond(response, request, NICE_NET_CMD_ERROR, "UNSUPPORTED", 11u);
            return true;
        }
        if (g_capability == MOCK_CAPABILITY_MALFORMED)
        {
            static const uint8_t malformed[8] = { 'B', 'C', 2u, 1u, 2u, 0u, 16u, 224u };
            respond(response, request, NICE_NET_CMD_STREAM_INFO, malformed, sizeof(malformed));
            return true;
        }
        static const uint8_t capabilities[8] = { 'B', 'C', 1u, 1u, 2u, 0u, 16u, 224u };
        respond(response, request, NICE_NET_CMD_STREAM_INFO, capabilities, sizeof(capabilities));
        return true;
    }

    if (request->command == NICE_NET_CMD_OPEN_STREAM)
    {
        payload[0] = g_stream_id;
        write_be32(&payload[1], g_fixture_length);
        payload[5] = NICE_NET_STREAM_CHUNK_SIZE;
        write_be32(&payload[6], g_fixture_crc);
        respond(response, request, NICE_NET_CMD_STREAM_OPENED, payload, 10u);
        return true;
    }

    if (request->command == NICE_NET_CMD_READ_STREAM_CHUNK && request->payload_length == 5u)
    {
        uint32_t offset = read_be32(&request->payload[1]);
        uint8_t count = g_fixture_length - offset > NICE_NET_STREAM_CHUNK_SIZE
            ? NICE_NET_STREAM_CHUNK_SIZE : (uint8_t)(g_fixture_length - offset);
        require(request->payload[0] == g_stream_id && offset < g_fixture_length,
            "legacy request shape/range");
        g_legacy_requests++;
        payload[0] = g_stream_id;
        write_be32(&payload[1], offset);
        payload[5] = count;
        payload[6] = offset + count == g_fixture_length ? 1u : 0u;
        memcpy(&payload[7], &g_fixture[offset], count);
        respond(response, request, NICE_NET_CMD_STREAM_CHUNK, payload, 7u + count);
        return true;
    }

    if (request->command == NICE_NET_CMD_READ_STREAM_CHUNK &&
        request->payload_length == 10u && request->payload[0] == 0xB0u)
    {
        g_bulk_begins++;
        if (g_fail_bulk_begin)
        {
            respond(response, request, NICE_NET_CMD_ERROR, "BULK_FAIL", 9u);
            return true;
        }
        g_stage_generation = read_be16(&request->payload[2]);
        g_stage_base = read_be32(&request->payload[4]);
        g_stage_length = read_be16(&request->payload[8]);
        require(request->payload[1] == g_stream_id && g_stage_generation != 0u &&
            g_stage_length > 0u && g_stage_length <= LOGICAL_STREAM_BULK_MAX_BYTES &&
            g_stage_base + g_stage_length <= g_fixture_length,
            "bulk begin shape/range");
        payload[0] = g_stream_id;
        write_be16(&payload[1], g_stage_generation);
        write_be32(&payload[3], g_stage_base);
        write_be16(&payload[7], g_stage_length);
        write_be32(&payload[9], crc32(&g_fixture[g_stage_base], g_stage_length));
        payload[13] = g_stage_base + g_stage_length == g_fixture_length ? 1u : 0u;
        respond(response, request, NICE_NET_CMD_BULK_READY, payload, 14u);
        return true;
    }

    if (request->command == NICE_NET_CMD_READ_STREAM_CHUNK &&
        request->payload_length == 8u && request->payload[0] == 0xB1u)
    {
        uint32_t offset = read_be32(&request->payload[4]);
        uint8_t count = g_fixture_length - offset > NICE_NET_STREAM_CHUNK_SIZE
            ? NICE_NET_STREAM_CHUNK_SIZE : (uint8_t)(g_fixture_length - offset);
        require(request->payload[1] == g_stream_id &&
            read_be16(&request->payload[2]) == g_stage_generation &&
            offset >= g_stage_base && offset < g_stage_base + g_stage_length &&
            offset + count <= g_stage_base + g_stage_length,
            "bulk read shape/range");
        if (offset == g_fail_bulk_read_offset)
        {
            g_last_transport_error = "READ_TIMEOUT";
            if (error != NULL) *error = NICE_NET_ERR_TRANSPORT_TIMEOUT;
            return false;
        }
        g_bulk_reads++;
        payload[0] = g_stream_id;
        write_be32(&payload[1], offset);
        payload[5] = count;
        payload[6] = offset + count == g_fixture_length ? 1u : 0u;
        memcpy(&payload[7], &g_fixture[offset], count);
        respond(response, request, NICE_NET_CMD_STREAM_CHUNK, payload, 7u + count);
        return true;
    }

    if (request->command == NICE_NET_CMD_CLOSE_STREAM &&
        request->payload_length == 1u && request->payload[0] == g_stream_id)
    {
        g_close_requests++;
        if (g_fail_close)
        {
            g_last_transport_error = "CLOSE_TIMEOUT";
            if (error != NULL) *error = NICE_NET_ERR_TRANSPORT_TIMEOUT;
            return false;
        }
        respond(response, request, NICE_NET_CMD_STREAM_CLOSED, &g_stream_id, 1u);
        return true;
    }

    if (error != NULL) *error = NICE_NET_ERR_UNSUPPORTED_COMMAND;
    return false;
}

network_pak_status_t network_pak_get_status(void)
{
    network_pak_status_t status;
    memset(&status, 0, sizeof(status));
    status.last_transport_error = g_last_transport_error;
    return status;
}

static void reset_mock(mock_capability_t capability, bool fail_bulk_begin)
{
    g_capability = capability;
    g_fail_bulk_begin = fail_bulk_begin;
    g_stage_generation = 0u;
    g_stage_base = 0u;
    g_stage_length = 0u;
    g_exchange_count = 0u;
    g_legacy_requests = 0u;
    g_bulk_begins = 0u;
    g_bulk_reads = 0u;
    g_close_requests = 0u;
    g_fail_bulk_read_offset = UINT32_MAX;
    g_fail_close = false;
    g_ticks = 0u;
    g_last_transport_error = NULL;
    g_bad_sequence_exchange = 0u;
    g_sequence_wraps = 0u;
}

static void fill_text_fixture(uint32_t length)
{
    require(length <= sizeof(g_fixture), "text fixture capacity");
    g_fixture_length = length;
    for (uint32_t index = 0; index < length; index++)
    {
        g_fixture[index] = (uint8_t)("NICE64 staged Browser text fixture\n"[index % 35u]);
    }
    g_fixture_crc = crc32(g_fixture, g_fixture_length);
}

static void fill_nimg_fixture(void)
{
    const uint16_t width = 160u;
    const uint16_t height = 90u;
    const uint32_t payload_length = (uint32_t)width * height * 2u;
    g_fixture_length = 16u + payload_length;
    memcpy(g_fixture, "NIMG", 4u);
    write_be16(&g_fixture[4], 1u);
    write_be16(&g_fixture[6], width);
    write_be16(&g_fixture[8], height);
    write_be16(&g_fixture[10], 1u);
    write_be32(&g_fixture[12], payload_length);
    for (uint32_t index = 16u; index < g_fixture_length; index++)
    {
        g_fixture[index] = (uint8_t)(index * 37u + 11u);
    }
    g_fixture_crc = crc32(g_fixture, g_fixture_length);
}

static ImageTransportStatus run_image_transport(void)
{
    ImageTransport_Init();
    require(ImageTransport_Start("browser://i/fixture", 1u, 160u, 90u),
        "start image transport");
    ImageTransportStatus status = ImageTransport_GetStatus();
    for (uint32_t update = 0u;
         update < 5000u && status.state != IMAGE_TRANSPORT_COMPLETE &&
             status.state != IMAGE_TRANSPORT_FAILED &&
             status.state != IMAGE_TRANSPORT_CANCELLED;
         update++)
    {
        ImageTransport_Update();
        status = ImageTransport_GetStatus();
    }
    require(status.state == IMAGE_TRANSPORT_COMPLETE ||
        status.state == IMAGE_TRANSPORT_FAILED || status.state == IMAGE_TRANSPORT_CANCELLED,
        "image transport bounded completion");
    return status;
}

static LogicalStreamReader read_fixture(
    mock_capability_t capability,
    uint8_t *output,
    uint32_t *output_length)
{
    LogicalStreamReader reader;
    LogicalStreamReadResult result;
    uint8_t count;

    reset_mock(capability, false);
    LogicalStreamReader_Init(&reader);
    require(LogicalStreamReader_Open(&reader, "browser://fixture"), "open fixture");
    *output_length = 0u;
    while ((result = LogicalStreamReader_ReadNext(
                &reader, &output[*output_length], sizeof(g_fixture) - *output_length, &count)) ==
           LOGICAL_STREAM_READ_OK)
    {
        *output_length += count;
    }
    require(result == LOGICAL_STREAM_READ_EOF, "stream EOF");
    uint32_t exchanges_at_eof = g_exchange_count;
    require(LogicalStreamReader_ReadNext(
                &reader, &output[*output_length], sizeof(g_fixture) - *output_length, &count) ==
            LOGICAL_STREAM_READ_EOF && g_exchange_count == exchanges_at_eof,
        "EOF caused over-read");
    require(LogicalStreamReader_Close(&reader), "close fixture");
    require(*output_length == g_fixture_length &&
        memcmp(output, g_fixture, g_fixture_length) == 0,
        "fixture bytes");
    return reader;
}

int main(void)
{
    uint8_t bulk_output[sizeof(g_fixture)];
    uint8_t legacy_output[sizeof(g_fixture)];
    uint32_t bulk_length;
    uint32_t legacy_length;

    fill_text_fixture(1031u); /* Two full blocks plus a partial final block. */
    LogicalStreamReader bulk = read_fixture(MOCK_CAPABILITY_BULK, bulk_output, &bulk_length);
    require(bulk.mode == LOGICAL_STREAM_READER_BULK && bulk.bulk_capability &&
        bulk.fallback == LOGICAL_STREAM_FALLBACK_NONE && bulk.staged_blocks == 3u &&
        bulk.legacy_read16_count == 0u,
        "bulk selection/partial block metrics");

    LogicalStreamReader legacy = read_fixture(
        MOCK_CAPABILITY_UNSUPPORTED, legacy_output, &legacy_length);
    require(legacy.mode == LOGICAL_STREAM_READER_LEGACY && !legacy.bulk_capability &&
        legacy.fallback == LOGICAL_STREAM_FALLBACK_CAPABILITY_UNSUPPORTED &&
        legacy.legacy_read16_count == (g_fixture_length + 15u) / 16u,
        "unsupported capability legacy fallback");
    require(bulk_length == legacy_length &&
        memcmp(bulk_output, legacy_output, bulk_length) == 0,
        "bulk/legacy text byte equivalence");

    LogicalStreamReader malformed = read_fixture(
        MOCK_CAPABILITY_MALFORMED, legacy_output, &legacy_length);
    require(malformed.mode == LOGICAL_STREAM_READER_LEGACY &&
        malformed.fallback == LOGICAL_STREAM_FALLBACK_CAPABILITY_MALFORMED,
        "malformed capability fallback");

    LogicalStreamReader unavailable = read_fixture(
        MOCK_CAPABILITY_UNAVAILABLE, legacy_output, &legacy_length);
    require(unavailable.mode == LOGICAL_STREAM_READER_LEGACY &&
        unavailable.fallback == LOGICAL_STREAM_FALLBACK_CAPABILITY_UNAVAILABLE,
        "unavailable capability fallback");

    fill_nimg_fixture();
    bulk = read_fixture(MOCK_CAPABILITY_BULK, bulk_output, &bulk_length);
    require(bulk.staged_blocks == 57u && bulk.eof && !bulk.stage_ready &&
        g_bulk_begins == 57u && g_bulk_reads == 1801u && g_close_requests == 1u,
        "production NIMG stages/final partial/EOF/close");
    legacy = read_fixture(MOCK_CAPABILITY_UNSUPPORTED, legacy_output, &legacy_length);
    require(bulk_length == legacy_length && bulk_length == g_fixture_length &&
        memcmp(bulk_output, legacy_output, bulk_length) == 0 &&
        memcmp(bulk_output, "NIMG", 4u) == 0 &&
        crc32(bulk_output, bulk_length) == g_fixture_crc,
        "NIMG byte/CRC equivalence");

    reset_mock(MOCK_CAPABILITY_BULK, false);
    ImageTransportStatus image = run_image_transport();
    require(image.state == IMAGE_TRANSPORT_COMPLETE && image.error == NULL &&
        image.bytes_received == g_fixture_length && image.actual_crc32 == g_fixture_crc &&
        image.staged_blocks == 57u && image.eof && image.close_attempted &&
        image.close_succeeded && g_bulk_begins == 57u && g_bulk_reads == 1801u &&
        g_close_requests == 1u &&
        g_sequence_wraps >= 7u &&
        memcmp(ImageTransport_GetBuffer(), g_fixture, g_fixture_length) == 0,
        "production NIMG lifecycle exact bytes/CRC/EOF/close");

    reset_mock(MOCK_CAPABILITY_BULK, false);
    g_bad_sequence_exchange = 70u;
    image = run_image_transport();
    require(image.state == IMAGE_TRANSPORT_FAILED && image.bytes_received == 1024u &&
        image.error_code == IMAGE_TRANSPORT_ERROR_BAD_CHUNK_RESPONSE &&
        image.error != NULL &&
        strcmp(image.error, "seq 70/69 q22/rA2 o1024 g3 b1024") == 0 &&
        g_close_requests == 1u,
        "stale previous response exposes exact sequence mismatch context");

    reset_mock(MOCK_CAPABILITY_BULK, false);
    g_fail_bulk_read_offset = 25088u;
    g_fail_close = true;
    image = run_image_transport();
    require(image.state == IMAGE_TRANSPORT_FAILED &&
        image.bytes_received == 25088u &&
        image.error_code == IMAGE_TRANSPORT_ERROR_BAD_CHUNK_RESPONSE &&
        image.error != NULL && strcmp(image.error, "READ_TIMEOUT") == 0 &&
        image.close_attempted && !image.close_succeeded && g_close_requests == 1u,
        "cleanup close failure preserves primary image read failure");

    fill_text_fixture(64u);
    reset_mock(MOCK_CAPABILITY_BULK, true);
    LogicalStreamReader failed;
    LogicalStreamReader_Init(&failed);
    require(LogicalStreamReader_Open(&failed, "browser://fixture"), "open failed-bulk fixture");
    uint8_t count = 0u;
    require(LogicalStreamReader_ReadNext(
                &failed, bulk_output, sizeof(bulk_output), &count) == LOGICAL_STREAM_READ_ERROR &&
        failed.offset == 0u && failed.mode == LOGICAL_STREAM_READER_BULK &&
        failed.error == LOGICAL_STREAM_ERROR_BAD_BULK_READY && g_legacy_requests == 0u,
        "bulk failure before consumption was not explicit");
    require(LogicalStreamReader_Close(&failed), "close after bulk failure");

    puts("CheckpointEBrowserBulkSmoke PASS");
    return 0;
}
