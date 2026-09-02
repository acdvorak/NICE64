#include "logical_stream_reader.h"

#include "network_pak_transport.h"
#include "protocol.h"

#include <string.h>

#define BULK_CAPABILITY_VERSION 1u
#define BULK_CAPABILITY_FLAG_READ 0x01u
#define BULK_BEGIN_MARKER 0xB0u
#define BULK_READ_MARKER 0xB1u

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

static void record_exchange(
    LogicalStreamReader *reader,
    const nice_net_packet_t *request,
    const nice_net_packet_t *response)
{
    LogicalStreamExchangeTrace *trace =
        &reader->exchange_history[reader->exchange_history_next];
    trace->request_command = request->command;
    trace->request_sequence = request->sequence;
    trace->response_command = response->command;
    trace->response_sequence = response->sequence;
    trace->generation = reader->generation;
    trace->offset = reader->offset;
    trace->stage_base = reader->stage_base;
    reader->exchange_history_next = (uint8_t)(
        (reader->exchange_history_next + 1u) % LOGICAL_STREAM_EXCHANGE_HISTORY_COUNT);
    if (reader->exchange_history_count < LOGICAL_STREAM_EXCHANGE_HISTORY_COUNT)
    {
        reader->exchange_history_count++;
    }
}

static bool exchange(
    LogicalStreamReader *reader,
    nice_net_command_t command,
    const void *payload,
    size_t payload_length,
    nice_net_packet_t *response,
    bool record_error)
{
    nice_net_packet_t request;
    nice_net_error_t error = NICE_NET_ERR_NONE;

    nice_net_packet_init(&request, command, 0, ++reader->sequence, payload, payload_length);
    if (!network_pak_exchange(&request, response, &error))
    {
        if (record_error)
        {
            reader->error = LOGICAL_STREAM_ERROR_TRANSPORT;
        }
        return false;
    }
    record_exchange(reader, &request, response);
    if (response->sequence != request.sequence)
    {
        reader->mismatch_request_command = request.command;
        reader->mismatch_expected_sequence = request.sequence;
        reader->mismatch_response_command = response->command;
        reader->mismatch_actual_sequence = response->sequence;
        reader->mismatch_generation = reader->generation;
        reader->mismatch_offset = reader->offset;
        reader->mismatch_stage_base = reader->stage_base;
        if (record_error)
        {
            reader->error = LOGICAL_STREAM_ERROR_BAD_SEQUENCE;
        }
        return false;
    }
    return true;
}

static void negotiate_bulk(LogicalStreamReader *reader)
{
    static const uint8_t query[4] = { 'B', 'C', 'A', 'P' };
    nice_net_packet_t response;

    reader->bulk_capability = false;
    reader->mode = LOGICAL_STREAM_READER_LEGACY;
    reader->fallback = LOGICAL_STREAM_FALLBACK_CAPABILITY_UNAVAILABLE;

    if (!exchange(reader, NICE_NET_CMD_GET_STREAM_INFO, query, sizeof(query), &response, false))
    {
        return;
    }
    if (response.command == NICE_NET_CMD_ERROR)
    {
        reader->fallback = LOGICAL_STREAM_FALLBACK_CAPABILITY_UNSUPPORTED;
        return;
    }
    if (response.command != NICE_NET_CMD_STREAM_INFO || response.payload_length != 8u ||
        response.payload[0] != 'B' || response.payload[1] != 'C' ||
        response.payload[2] != BULK_CAPABILITY_VERSION ||
        (response.payload[3] & BULK_CAPABILITY_FLAG_READ) == 0u ||
        read_be16(&response.payload[4]) != LOGICAL_STREAM_BULK_MAX_BYTES ||
        response.payload[6] != NICE_NET_STREAM_CHUNK_SIZE)
    {
        reader->fallback = LOGICAL_STREAM_FALLBACK_CAPABILITY_MALFORMED;
        return;
    }

    /* Byte 7 is Bridge/Pico internal segmentation metadata. The Browser does
       not consume or depend on that physical framing detail. */
    reader->bulk_capability = true;
    reader->mode = LOGICAL_STREAM_READER_BULK;
    reader->fallback = LOGICAL_STREAM_FALLBACK_NONE;
}

void LogicalStreamReader_Init(LogicalStreamReader *reader)
{
    if (reader == NULL)
    {
        return;
    }
    memset(reader, 0, sizeof(*reader));
    reader->delivery_size = NICE_NET_STREAM_CHUNK_SIZE;
    reader->mode = LOGICAL_STREAM_READER_LEGACY;
    reader->fallback = LOGICAL_STREAM_FALLBACK_CAPABILITY_UNAVAILABLE;
}

bool LogicalStreamReader_Open(LogicalStreamReader *reader, const char *resource)
{
    nice_net_packet_t response;
    uint8_t sequence;
    size_t resource_length;

    if (reader == NULL || resource == NULL)
    {
        return false;
    }
    resource_length = strlen(resource);
    if (resource_length == 0u || resource_length > NICE_NET_MAX_PAYLOAD)
    {
        reader->error = LOGICAL_STREAM_ERROR_BAD_OPEN_RESPONSE;
        return false;
    }

    sequence = reader->sequence;
    LogicalStreamReader_Init(reader);
    reader->sequence = sequence;
    negotiate_bulk(reader);

    if (!exchange(reader, NICE_NET_CMD_OPEN_STREAM, resource, resource_length, &response, true))
    {
        return false;
    }
    if (response.command != NICE_NET_CMD_STREAM_OPENED || response.payload_length != 10u)
    {
        reader->error = LOGICAL_STREAM_ERROR_BAD_OPEN_RESPONSE;
        return false;
    }

    reader->stream_id = response.payload[0];
    reader->total_length = read_be32(&response.payload[1]);
    reader->delivery_size = response.payload[5];
    reader->expected_crc32 = read_be32(&response.payload[6]);
    if (reader->stream_id == 0u || reader->delivery_size != NICE_NET_STREAM_CHUNK_SIZE)
    {
        reader->error = LOGICAL_STREAM_ERROR_BAD_STREAM_METADATA;
        return false;
    }

    reader->open = true;
    reader->eof = reader->total_length == 0u;
    reader->error = LOGICAL_STREAM_ERROR_NONE;
    return true;
}

static bool begin_bulk_block(LogicalStreamReader *reader)
{
    nice_net_packet_t response;
    uint8_t request[10];
    uint32_t remaining = reader->total_length - reader->offset;
    uint16_t length = remaining > LOGICAL_STREAM_BULK_MAX_BYTES
        ? LOGICAL_STREAM_BULK_MAX_BYTES : (uint16_t)remaining;
    uint16_t generation = (uint16_t)(reader->generation + 1u);

    if (generation == 0u)
    {
        generation = 1u;
    }
    reader->generation = generation;

    request[0] = BULK_BEGIN_MARKER;
    request[1] = reader->stream_id;
    write_be16(&request[2], generation);
    write_be32(&request[4], reader->offset);
    write_be16(&request[8], length);

    if (!exchange(reader, NICE_NET_CMD_READ_STREAM_CHUNK, request, sizeof(request), &response, true))
    {
        return false;
    }
    if (response.command != NICE_NET_CMD_BULK_READY || response.payload_length != 14u ||
        response.payload[0] != reader->stream_id ||
        read_be16(&response.payload[1]) != generation ||
        read_be32(&response.payload[3]) != reader->offset ||
        read_be16(&response.payload[7]) != length ||
        (response.payload[13] != 0u) != (reader->offset + length == reader->total_length))
    {
        reader->error = LOGICAL_STREAM_ERROR_BAD_BULK_READY;
        return false;
    }

    reader->stage_base = reader->offset;
    reader->stage_length = length;
    reader->stage_crc32 = read_be32(&response.payload[9]);
    reader->stage_ready = true;
    reader->staged_blocks++;
    return true;
}

static bool read_delivery(
    LogicalStreamReader *reader,
    uint8_t *destination,
    size_t destination_capacity,
    uint8_t *count)
{
    nice_net_packet_t response;
    uint8_t request[8];
    size_t request_length;
    uint32_t offset = reader->offset;
    uint8_t received;

    if (reader->mode == LOGICAL_STREAM_READER_BULK)
    {
        if (!reader->stage_ready ||
            offset < reader->stage_base ||
            offset >= reader->stage_base + reader->stage_length)
        {
            if (!begin_bulk_block(reader))
            {
                return false;
            }
        }
        request[0] = BULK_READ_MARKER;
        request[1] = reader->stream_id;
        write_be16(&request[2], reader->generation);
        write_be32(&request[4], offset);
        request_length = 8u;
    }
    else
    {
        request[0] = reader->stream_id;
        write_be32(&request[1], offset);
        request_length = 5u;
    }

    if (!exchange(reader, NICE_NET_CMD_READ_STREAM_CHUNK, request, request_length, &response, true))
    {
        return false;
    }
    if (response.command != NICE_NET_CMD_STREAM_CHUNK || response.payload_length < 7u ||
        response.payload[0] != reader->stream_id || read_be32(&response.payload[1]) != offset)
    {
        reader->error = LOGICAL_STREAM_ERROR_BAD_CHUNK_RESPONSE;
        return false;
    }

    received = response.payload[5];
    if (received == 0u || received > reader->delivery_size ||
        response.payload_length != (uint8_t)(7u + received) ||
        received > reader->total_length - offset ||
        destination_capacity < received ||
        (reader->mode == LOGICAL_STREAM_READER_BULK &&
         offset + received > reader->stage_base + reader->stage_length))
    {
        reader->error = LOGICAL_STREAM_ERROR_BAD_CHUNK_LENGTH;
        return false;
    }

    memcpy(destination, &response.payload[7], received);
    reader->offset += received;
    reader->physical_delivery_count++;
    if (reader->mode == LOGICAL_STREAM_READER_LEGACY)
    {
        reader->legacy_read16_count++;
    }
    if (reader->mode == LOGICAL_STREAM_READER_BULK &&
        reader->offset == reader->stage_base + reader->stage_length)
    {
        reader->stage_ready = false;
    }
    reader->eof = reader->offset == reader->total_length;
    *count = received;
    return true;
}

LogicalStreamReadResult LogicalStreamReader_ReadNext(
    LogicalStreamReader *reader,
    uint8_t *destination,
    size_t destination_capacity,
    uint8_t *count)
{
    if (count != NULL)
    {
        *count = 0u;
    }
    if (reader == NULL || destination == NULL || count == NULL || !reader->open)
    {
        if (reader != NULL)
        {
            reader->error = LOGICAL_STREAM_ERROR_TRANSPORT;
        }
        return LOGICAL_STREAM_READ_ERROR;
    }
    if (reader->offset >= reader->total_length)
    {
        reader->eof = true;
        return LOGICAL_STREAM_READ_EOF;
    }
    size_t required_capacity = reader->total_length - reader->offset < reader->delivery_size
        ? (size_t)(reader->total_length - reader->offset)
        : reader->delivery_size;
    if (destination_capacity < required_capacity)
    {
        reader->error = LOGICAL_STREAM_ERROR_BAD_CHUNK_LENGTH;
        return LOGICAL_STREAM_READ_ERROR;
    }
    if (!read_delivery(reader, destination, destination_capacity, count))
    {
        return LOGICAL_STREAM_READ_ERROR;
    }
    return LOGICAL_STREAM_READ_OK;
}

bool LogicalStreamReader_Close(LogicalStreamReader *reader)
{
    nice_net_packet_t response;
    uint8_t stream_id;

    if (reader == NULL || !reader->open)
    {
        return false;
    }
    stream_id = reader->stream_id;
    reader->open = false;
    reader->stage_ready = false;
    if (!exchange(reader, NICE_NET_CMD_CLOSE_STREAM, &stream_id, 1u, &response, true) ||
        response.command != NICE_NET_CMD_STREAM_CLOSED)
    {
        reader->error = LOGICAL_STREAM_ERROR_CLOSE_FAILED;
        return false;
    }
    return true;
}

const char *LogicalStreamReader_ModeName(LogicalStreamReaderMode mode)
{
    return mode == LOGICAL_STREAM_READER_BULK ? "bulk" : "legacy";
}

const char *LogicalStreamReader_FallbackName(LogicalStreamFallback fallback)
{
    switch (fallback)
    {
    case LOGICAL_STREAM_FALLBACK_NONE: return "none";
    case LOGICAL_STREAM_FALLBACK_CAPABILITY_UNAVAILABLE: return "capability unavailable";
    case LOGICAL_STREAM_FALLBACK_CAPABILITY_UNSUPPORTED: return "capability unsupported";
    case LOGICAL_STREAM_FALLBACK_CAPABILITY_MALFORMED: return "capability malformed";
    default: return "unknown";
    }
}

const char *LogicalStreamReader_ErrorName(LogicalStreamError error)
{
    switch (error)
    {
    case LOGICAL_STREAM_ERROR_NONE: return "none";
    case LOGICAL_STREAM_ERROR_TRANSPORT: return "transport";
    case LOGICAL_STREAM_ERROR_BAD_SEQUENCE: return "bad sequence";
    case LOGICAL_STREAM_ERROR_BAD_OPEN_RESPONSE: return "bad open response";
    case LOGICAL_STREAM_ERROR_BAD_STREAM_METADATA: return "bad stream metadata";
    case LOGICAL_STREAM_ERROR_BAD_BULK_READY: return "bad bulk ready";
    case LOGICAL_STREAM_ERROR_BAD_CHUNK_RESPONSE: return "bad chunk response";
    case LOGICAL_STREAM_ERROR_BAD_CHUNK_LENGTH: return "bad chunk length";
    case LOGICAL_STREAM_ERROR_READ_PAST_END: return "read past end";
    case LOGICAL_STREAM_ERROR_CLOSE_FAILED: return "close failed";
    default: return "unknown";
    }
}
