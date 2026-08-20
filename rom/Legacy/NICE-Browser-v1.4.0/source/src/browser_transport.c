#include "browser_transport.h"

#include "document_loader.h"
#include "network_pak_transport.h"
#include "nice_document.h"

#include <stdio.h>
#include <string.h>

#define NICE_WIRE_MAGIC 0x4E494345u
#define NICE_WIRE_VERSION 1u
#define NICE_WIRE_HEADER_SIZE 16u
#define NICE_WIRE_BLOCK_HEADER_SIZE 12u
#define NICE_WIRE_CANONICAL_URL 10u

typedef struct browser_stream_s
{
    bool open;
    uint8_t id;
    uint32_t total_length;
    uint8_t chunk_size;
    uint32_t expected_crc32;
} browser_stream_t;

typedef enum document_transfer_state_e
{
    DOCUMENT_TRANSFER_IDLE,
    DOCUMENT_TRANSFER_OPENING,
    DOCUMENT_TRANSFER_READING,
    DOCUMENT_TRANSFER_VALIDATING,
    DOCUMENT_TRANSFER_CLOSING,
    DOCUMENT_TRANSFER_DECODING,
    DOCUMENT_TRANSFER_READY,
    DOCUMENT_TRANSFER_FAILED,
    DOCUMENT_TRANSFER_CANCELLED
} document_transfer_state_t;

static BrowserTransportStatus g_status;
static browser_stream_t g_stream;
static document_transfer_state_t g_document_transfer;
static char g_document_resource[129];
static uint8_t g_sequence;
static uint8_t g_document_buffer[BROWSER_TRANSPORT_MAX_DOCUMENT_SIZE];
static NICE_Block g_decoded_blocks[BROWSER_TRANSPORT_MAX_BLOCKS + 1u];
static char g_text_pool[BROWSER_TRANSPORT_TEXT_POOL_SIZE];
static NICE_Page g_decoded_page;

static void set_transport_progress(const char *message)
{
    snprintf(g_status.message, sizeof(g_status.message), "%s", message);
}

static void set_status(BrowserTransportState state, BrowserTransportResult result, const char *message)
{
    g_status.state = state;
    g_status.result = result;
    snprintf(g_status.message, sizeof(g_status.message), "%s", message);
}

const char *BrowserTransport_ResultName(BrowserTransportResult result)
{
    switch (result)
    {
    case BROWSER_TRANSPORT_OK: return "OK";
    case BROWSER_ERROR_NETWORK_PAK_INIT: return "NETWORK PAK INIT FAILED";
    case BROWSER_ERROR_NETWORK_PAK_NOT_FOUND: return "NETWORK PAK NOT FOUND";
    case BROWSER_ERROR_STREAM_OPEN: return "STREAM OPEN FAILED";
    case BROWSER_ERROR_RESOURCE_REQUEST: return "RESOURCE REQUEST FAILED";
    case BROWSER_ERROR_STREAM_READ: return "STREAM READ FAILED";
    case BROWSER_ERROR_STREAM_CLOSE: return "STREAM CLOSE FAILED";
    case BROWSER_ERROR_SHORT_PAYLOAD: return "SHORT PAYLOAD";
    case BROWSER_ERROR_BAD_MAGIC: return "BAD NICE MAGIC";
    case BROWSER_ERROR_UNSUPPORTED_VERSION: return "UNSUPPORTED NICE VERSION";
    case BROWSER_ERROR_BAD_PAYLOAD_LENGTH: return "BAD PAYLOAD LENGTH";
    case BROWSER_ERROR_BLOCK_LIMIT: return "BLOCK LIMIT EXCEEDED";
    case BROWSER_ERROR_TEXT_POOL_LIMIT: return "TEXT POOL LIMIT";
    case BROWSER_ERROR_BLOCK_DECODE: return "BLOCK DECODE FAILED";
    case BROWSER_ERROR_DOCUMENT_LOAD: return "DOCUMENT LOAD FAILED";
    default: return "UNKNOWN ERROR";
    }
}

static void set_transport_error(BrowserTransportResult result, const char *message)
{
    network_pak_status_t status = network_pak_get_status();
    g_status.last_write_status = status.last_write_status;
    g_status.last_read_status = status.last_read_status;
    set_status(BROWSER_TRANSPORT_ERROR, result, message);
}

static uint16_t read_be16(const uint8_t *data)
{
    return (uint16_t)(((uint16_t)data[0] << 8) | (uint16_t)data[1]);
}

static uint32_t read_be32(const uint8_t *data)
{
    return ((uint32_t)data[0] << 24) |
        ((uint32_t)data[1] << 16) |
        ((uint32_t)data[2] << 8) |
        (uint32_t)data[3];
}

static void write_be32(uint8_t *data, uint32_t value)
{
    data[0] = (uint8_t)(value >> 24);
    data[1] = (uint8_t)(value >> 16);
    data[2] = (uint8_t)(value >> 8);
    data[3] = (uint8_t)value;
}

static uint32_t crc32_update_byte(uint32_t crc, uint8_t value)
{
    crc ^= value;
    for (uint8_t bit = 0; bit < 8; bit++)
    {
        crc = (crc >> 1) ^ (0xEDB88320u & (uint32_t)-(int32_t)(crc & 1u));
    }
    return crc;
}

static uint32_t stream_crc32(const uint8_t *data, uint32_t length)
{
    uint32_t crc = 0xFFFFFFFFu;
    for (uint32_t i = 0; i < length; i++)
    {
        crc = crc32_update_byte(crc, data[i]);
    }
    return ~crc;
}

static bool expected_response(nice_net_command_t request, uint8_t response)
{
    switch (request)
    {
    case NICE_NET_CMD_OPEN_STREAM: return response == NICE_NET_CMD_STREAM_OPENED;
    case NICE_NET_CMD_READ_STREAM_CHUNK: return response == NICE_NET_CMD_STREAM_CHUNK;
    case NICE_NET_CMD_CLOSE_STREAM: return response == NICE_NET_CMD_STREAM_CLOSED;
    case NICE_NET_CMD_WRITE_STREAM_CHUNK: return response == NICE_NET_CMD_STREAM_WRITE_ACK;
    default: return response == NICE_NET_CMD_ERROR;
    }
}

static bool run_packet_command(
    nice_net_command_t command,
    const void *payload,
    size_t payload_length,
    nice_net_packet_t *response)
{
    nice_net_packet_t request;
    nice_net_error_t error = NICE_NET_ERR_NONE;

    g_sequence++;
    if (command == NICE_NET_CMD_OPEN_STREAM)
    {
        set_transport_progress("OPEN_STREAM request sent");
    }
    else if (command == NICE_NET_CMD_READ_STREAM_CHUNK)
    {
        set_transport_progress("READ_STREAM_CHUNK request sent");
    }
    nice_net_packet_init(&request, command, 0, g_sequence, payload, payload_length);
    if (command == NICE_NET_CMD_OPEN_STREAM)
    {
        set_transport_progress("OPEN_STREAM waiting for response");
    }
    if (!network_pak_exchange(&request, response, &error))
    {
        network_pak_status_t status = network_pak_get_status();
        snprintf(g_status.message, sizeof(g_status.message), "%s", nice_net_error_name(error));
        g_status.state = BROWSER_TRANSPORT_ERROR;
        g_status.last_write_status = status.last_write_status;
        g_status.last_read_status = status.last_read_status;
        if (status.last_transport_error != NULL &&
            (strcmp(status.last_transport_error, "NO_PAK") == 0 ||
             strcmp(status.last_transport_error, "NO_DEVICE") == 0))
        {
            g_status.result = BROWSER_ERROR_NETWORK_PAK_NOT_FOUND;
            snprintf(g_status.message, sizeof(g_status.message), "%s", status.last_transport_error);
        }
        else
        {
            g_status.result = command == NICE_NET_CMD_OPEN_STREAM ? BROWSER_ERROR_STREAM_OPEN : BROWSER_ERROR_STREAM_READ;
        }
        return false;
    }

    if (response->sequence != request.sequence)
    {
        set_transport_error(BROWSER_ERROR_RESOURCE_REQUEST, "BAD_SEQUENCE");
        return false;
    }

    if (!expected_response(command, response->command))
    {
        if (response->command == NICE_NET_CMD_ERROR && response->payload_length > 0)
        {
            snprintf(g_status.message, sizeof(g_status.message), "%.*s", response->payload_length, response->payload);
        }
        else
        {
            snprintf(g_status.message, sizeof(g_status.message), "BAD_RESPONSE_%02X", response->command);
        }
        g_status.state = BROWSER_TRANSPORT_ERROR;
        g_status.result = command == NICE_NET_CMD_OPEN_STREAM ? BROWSER_ERROR_RESOURCE_REQUEST : BROWSER_ERROR_STREAM_READ;
        return false;
    }

    if (command == NICE_NET_CMD_OPEN_STREAM)
    {
        set_transport_progress("OPEN_STREAM response received");
    }
    return true;
}

static bool stream_open(const char *resource)
{
    nice_net_packet_t response;
    size_t resource_length = strlen(resource);

    if (resource_length == 0 || resource_length > NICE_NET_MAX_PAYLOAD)
    {
        set_transport_error(BROWSER_ERROR_RESOURCE_REQUEST, "RESOURCE_TOO_LONG");
        return false;
    }

    if (!run_packet_command(NICE_NET_CMD_OPEN_STREAM, resource, resource_length, &response))
    {
        return false;
    }

    if (response.payload_length != 10)
    {
        set_transport_error(BROWSER_ERROR_STREAM_OPEN, "BAD_OPEN_METADATA");
        return false;
    }

    memset(&g_stream, 0, sizeof(g_stream));
    g_stream.open = true;
    g_stream.id = response.payload[0];
    g_stream.total_length = read_be32(&response.payload[1]);
    g_stream.chunk_size = response.payload[5];
    g_stream.expected_crc32 = read_be32(&response.payload[6]);
    g_status.stream_opened = true;
    g_status.expected_size = g_stream.total_length;
    snprintf(g_status.message, sizeof(g_status.message),
        "OPEN ok id=%u len=%lu chunk=%u",
        g_stream.id,
        (unsigned long)g_stream.total_length,
        g_stream.chunk_size);

    if (g_stream.id == 0 ||
        g_stream.chunk_size == 0 ||
        g_stream.chunk_size > NICE_NET_STREAM_CHUNK_SIZE ||
        g_stream.total_length == 0 ||
        g_stream.total_length > BROWSER_TRANSPORT_MAX_DOCUMENT_SIZE)
    {
        snprintf(g_status.message, sizeof(g_status.message),
            "BAD_STREAM_METADATA id=%u len=%lu chunk=%u max=%u",
            g_stream.id,
            (unsigned long)g_stream.total_length,
            g_stream.chunk_size,
            BROWSER_TRANSPORT_MAX_DOCUMENT_SIZE);
        g_status.state = BROWSER_TRANSPORT_ERROR;
        g_status.result = BROWSER_ERROR_STREAM_OPEN;
        return false;
    }

    set_transport_progress("begin chunk reads");
    return true;
}

static bool stream_read_chunk(void)
{
    nice_net_packet_t response;
    uint8_t request[5];
    uint8_t count;
    uint32_t chunk_offset;
    uint32_t offset = g_status.bytes_received;

    if (offset >= g_stream.total_length)
    {
        set_transport_error(BROWSER_ERROR_STREAM_READ, "READ_PAST_STREAM_END");
        return false;
    }

    request[0] = g_stream.id;
    write_be32(&request[1], offset);
    if (!run_packet_command(NICE_NET_CMD_READ_STREAM_CHUNK, request, sizeof(request), &response))
    {
        return false;
    }

    if (response.payload_length < 7 || response.payload[0] != g_stream.id)
    {
        set_transport_error(BROWSER_ERROR_STREAM_READ, "BAD_CHUNK_HEADER");
        return false;
    }

    chunk_offset = read_be32(&response.payload[1]);
    count = response.payload[5];
    if (chunk_offset != offset ||
        count == 0 ||
        count > g_stream.chunk_size ||
        response.payload_length != (uint8_t)(7u + count) ||
        count > g_stream.total_length - offset ||
        offset > BROWSER_TRANSPORT_MAX_DOCUMENT_SIZE ||
        count > BROWSER_TRANSPORT_MAX_DOCUMENT_SIZE - offset)
    {
        set_transport_error(BROWSER_ERROR_STREAM_READ, "BAD_CHUNK_DATA");
        return false;
    }

    memcpy(&g_document_buffer[offset], &response.payload[7], count);
    g_status.bytes_received = offset + count;
    return true;
}

static void stream_close(void)
{
    nice_net_packet_t response;
    if (g_stream.open)
    {
        if (!run_packet_command(NICE_NET_CMD_CLOSE_STREAM, &g_stream.id, 1, &response) &&
            g_status.result == BROWSER_TRANSPORT_OK)
        {
            set_transport_error(BROWSER_ERROR_STREAM_CLOSE, "STREAM_CLOSE_FAILED");
        }
    }
    memset(&g_stream, 0, sizeof(g_stream));
}

static void document_transfer_fail(void)
{
    if (g_stream.open)
    {
        g_document_transfer = DOCUMENT_TRANSFER_CLOSING;
    }
    else
    {
        g_document_transfer = DOCUMENT_TRANSFER_FAILED;
    }
}

static bool register_keyboard_input(const char *input, const char *prefix, char *route_out, uint32_t route_out_size)
{
    /* 128 keyboard characters plus the internal "search:" marker and NUL. */
    char registration[136];
    const uint8_t *cursor;
    uint32_t remaining;
    size_t prefix_length;
    size_t input_length;
    bool first = true;

    if (input == NULL || prefix == NULL || route_out == NULL || route_out_size == 0)
    {
        set_transport_error(BROWSER_ERROR_RESOURCE_REQUEST, "BAD_URL_REGISTER");
        return false;
    }

    prefix_length = strlen(prefix);
    input_length = strlen(input);
    if (prefix_length + input_length >= sizeof(registration))
    {
        set_transport_error(BROWSER_ERROR_RESOURCE_REQUEST, "BAD_URL_REGISTER");
        return false;
    }

    memcpy(registration, prefix, prefix_length);
    memcpy(registration + prefix_length, input, input_length + 1u);
    cursor = (const uint8_t *)registration;
    remaining = (uint32_t)strlen(registration);
    route_out[0] = '\0';
    set_status(BROWSER_TRANSPORT_LOADING, BROWSER_TRANSPORT_OK, "Registering URL");

    while (remaining > 0 || first)
    {
        nice_net_packet_t response;
        uint8_t payload[NICE_NET_MAX_PAYLOAD];
        uint8_t count = remaining > 21u ? 21u : (uint8_t)remaining;
        bool last = remaining <= 21u;

        payload[0] = (uint8_t)((first ? 0x01u : 0u) | (last ? 0x02u : 0u));
        payload[1] = count;
        if (count > 0)
        {
            memcpy(&payload[2], cursor, count);
        }

        if (!run_packet_command(NICE_NET_CMD_WRITE_STREAM_CHUNK, payload, (size_t)(2u + count), &response))
        {
            set_transport_error(BROWSER_ERROR_RESOURCE_REQUEST, "URL_REGISTER_WRITE_FAILED");
            return false;
        }

        if (last)
        {
            if (response.payload_length == 0 || response.payload_length >= route_out_size)
            {
                set_transport_error(BROWSER_ERROR_RESOURCE_REQUEST, "BAD_URL_REGISTER_ACK");
                return false;
            }
            snprintf(route_out, route_out_size, "%.*s", response.payload_length, response.payload);
            if (strncmp(route_out, "browser://u/", 12) != 0 &&
                strncmp(route_out, "browser://live", 14) != 0)
            {
                set_transport_error(BROWSER_ERROR_RESOURCE_REQUEST, "URL_REGISTER_REJECTED");
                return false;
            }
            set_status(BROWSER_TRANSPORT_READY, BROWSER_TRANSPORT_OK, "URL registered");
            return true;
        }

        cursor += count;
        remaining -= count;
        first = false;
    }

    set_transport_error(BROWSER_ERROR_RESOURCE_REQUEST, "URL_REGISTER_FAILED");
    return false;
}

bool BrowserTransport_RegisterUrl(const char *input, char *route_out, uint32_t route_out_size)
{
    return register_keyboard_input(input, "", route_out, route_out_size);
}

bool BrowserTransport_RegisterSearchQuery(const char *query, char *route_out, uint32_t route_out_size)
{
    return register_keyboard_input(query, "search:", route_out, route_out_size);
}

bool BrowserTransport_GetPendingPollRoute(char *route_out, uint32_t route_out_size)
{
    const NICE_Page *page = NICE_CurrentDocument();
    if (route_out == NULL || route_out_size == 0 || page == NULL || page->blocks == NULL)
    {
        return false;
    }
    route_out[0] = '\0';
    for (uint32_t index = 0; index < BROWSER_TRANSPORT_MAX_BLOCKS; index++)
    {
        const NICE_Block *block = &page->blocks[index];
        if (block->type == NICE_END)
        {
            break;
        }
        if (block->type == NICE_LINK && block->target != NULL && strncmp(block->target, "browser://p/", 12) == 0)
        {
            snprintf(route_out, route_out_size, "%s", block->target);
            return true;
        }
    }
    return false;
}

static bool copy_pool_string(
    uint8_t **cursor,
    uint32_t *remaining,
    uint16_t length,
    char **pool_cursor,
    uint32_t *pool_remaining,
    const char **out)
{
    if (length == 0)
    {
        *out = NULL;
        return true;
    }
    if (length > *remaining || (uint32_t)length + 1u > *pool_remaining)
    {
        return false;
    }

    memcpy(*pool_cursor, *cursor, length);
    (*pool_cursor)[length] = '\0';
    *out = *pool_cursor;
    *cursor += length;
    *remaining -= length;
    *pool_cursor += length + 1u;
    *pool_remaining -= (uint32_t)length + 1u;
    return true;
}

static bool decode_wire_document(void)
{
    uint32_t magic;
    uint16_t version;
    uint16_t block_count;
    uint32_t payload_size;
    uint8_t *cursor;
    uint32_t remaining;
    char *pool_cursor = g_text_pool;
    uint32_t pool_remaining = BROWSER_TRANSPORT_TEXT_POOL_SIZE;
    bool saw_end = false;
    uint16_t decoded_count = 0;

    if (g_status.bytes_received < NICE_WIRE_HEADER_SIZE)
    {
        set_transport_error(BROWSER_ERROR_SHORT_PAYLOAD, "DOC_TOO_SMALL");
        return false;
    }

    magic = read_be32(&g_document_buffer[0]);
    version = read_be16(&g_document_buffer[4]);
    block_count = read_be16(&g_document_buffer[6]);
    g_status.page_id = read_be32(&g_document_buffer[8]);
    payload_size = read_be32(&g_document_buffer[12]);

    if (magic != NICE_WIRE_MAGIC)
    {
        set_transport_error(BROWSER_ERROR_BAD_MAGIC, "BAD_NICE_MAGIC");
        return false;
    }
    if (version != NICE_WIRE_VERSION)
    {
        set_transport_error(BROWSER_ERROR_UNSUPPORTED_VERSION, "BAD_NICE_VERSION");
        return false;
    }
    if (block_count == 0 || block_count > BROWSER_TRANSPORT_MAX_BLOCKS)
    {
        set_transport_error(BROWSER_ERROR_BLOCK_LIMIT, "BAD_BLOCK_COUNT");
        return false;
    }
    if (payload_size != g_status.bytes_received - NICE_WIRE_HEADER_SIZE)
    {
        set_transport_error(BROWSER_ERROR_BAD_PAYLOAD_LENGTH, "BAD_PAYLOAD_SIZE");
        return false;
    }

    memset(g_decoded_blocks, 0, sizeof(g_decoded_blocks));
    memset(g_text_pool, 0, sizeof(g_text_pool));
    cursor = &g_document_buffer[NICE_WIRE_HEADER_SIZE];
    remaining = payload_size;

    for (uint16_t i = 0; i < block_count; i++)
    {
        uint16_t type;
        uint16_t text_length;
        uint16_t target_length;
        uint16_t image_width;
        uint16_t image_height;
        const char *text = NULL;
        const char *target = NULL;

        if (remaining < NICE_WIRE_BLOCK_HEADER_SIZE)
        {
            snprintf(g_status.message, sizeof(g_status.message), "Block %u: unexpected end of document", i);
            g_status.state = BROWSER_TRANSPORT_ERROR;
            g_status.result = BROWSER_ERROR_BLOCK_DECODE;
            return false;
        }

        type = read_be16(&cursor[0]);
        text_length = read_be16(&cursor[4]);
        target_length = read_be16(&cursor[6]);
        image_width = read_be16(&cursor[8]);
        image_height = read_be16(&cursor[10]);
        cursor += NICE_WIRE_BLOCK_HEADER_SIZE;
        remaining -= NICE_WIRE_BLOCK_HEADER_SIZE;

        if (type > NICE_WIRE_CANONICAL_URL)
        {
            snprintf(g_status.message, sizeof(g_status.message), "Block %u: unexpected type %u", i, type);
            g_status.state = BROWSER_TRANSPORT_ERROR;
            g_status.result = BROWSER_ERROR_BLOCK_DECODE;
            return false;
        }

        if (text_length > BROWSER_TRANSPORT_MAX_TEXT_BYTES)
        {
            snprintf(g_status.message, sizeof(g_status.message), "Block %u: text %u > max %u", i, text_length, BROWSER_TRANSPORT_MAX_TEXT_BYTES);
            g_status.state = BROWSER_TRANSPORT_ERROR;
            g_status.result = BROWSER_ERROR_BLOCK_DECODE;
            return false;
        }

        if (target_length > BROWSER_TRANSPORT_MAX_TARGET_BYTES)
        {
            snprintf(g_status.message, sizeof(g_status.message), "Block %u: target %u > max %u", i, target_length, BROWSER_TRANSPORT_MAX_TARGET_BYTES);
            g_status.state = BROWSER_TRANSPORT_ERROR;
            g_status.result = BROWSER_ERROR_BLOCK_DECODE;
            return false;
        }

        if ((uint32_t)text_length + (uint32_t)target_length > remaining)
        {
            snprintf(g_status.message, sizeof(g_status.message), "Block %u: string lengths exceed remaining %lu", i, (unsigned long)remaining);
            g_status.state = BROWSER_TRANSPORT_ERROR;
            g_status.result = BROWSER_ERROR_BLOCK_DECODE;
            return false;
        }

        if (!copy_pool_string(&cursor, &remaining, text_length, &pool_cursor, &pool_remaining, &text) ||
            !copy_pool_string(&cursor, &remaining, target_length, &pool_cursor, &pool_remaining, &target))
        {
            snprintf(g_status.message, sizeof(g_status.message), "Block %u: text pool exhausted", i);
            g_status.state = BROWSER_TRANSPORT_ERROR;
            g_status.result = BROWSER_ERROR_TEXT_POOL_LIMIT;
            return false;
        }

        if (type == NICE_WIRE_CANONICAL_URL)
        {
            if (target != NULL || text == NULL ||
                (strncmp(text, "http://", 7) != 0 && strncmp(text, "https://", 8) != 0))
            {
                snprintf(g_status.message, sizeof(g_status.message), "Block %u: invalid canonical URL", i);
                g_status.state = BROWSER_TRANSPORT_ERROR;
                g_status.result = BROWSER_ERROR_BLOCK_DECODE;
                return false;
            }
            snprintf(g_status.canonical_url, sizeof(g_status.canonical_url), "%s", text);
            continue;
        }

        g_decoded_blocks[decoded_count].type = (NICE_BlockType)type;
        g_decoded_blocks[decoded_count].text = text;
        g_decoded_blocks[decoded_count].target = target;
        g_decoded_blocks[decoded_count].image_width = image_width;
        g_decoded_blocks[decoded_count].image_height = image_height;
        decoded_count++;

        if (type == NICE_END)
        {
            saw_end = true;
            g_status.block_count = decoded_count;
            break;
        }
    }

    if (!saw_end)
    {
        if (block_count >= BROWSER_TRANSPORT_MAX_BLOCKS)
        {
            set_transport_error(BROWSER_ERROR_BLOCK_LIMIT, "MISSING_END");
            return false;
        }
        g_decoded_blocks[decoded_count].type = NICE_END;
        g_status.block_count = decoded_count + 1u;
    }

    g_decoded_page.title = g_decoded_blocks[0].text != NULL ? g_decoded_blocks[0].text : "NICE Browser";
    g_decoded_page.blocks = g_decoded_blocks;
    g_status.decode_ok = true;
    return true;
}

void BrowserTransport_Init(void)
{
    memset(&g_status, 0, sizeof(g_status));
    memset(&g_stream, 0, sizeof(g_stream));
    g_document_transfer = DOCUMENT_TRANSFER_IDLE;
    g_sequence = 0;
    network_pak_init();
    network_pak_set_max_read_retries(1);
    network_pak_set_stub_fallback_enabled(false);
    network_pak_detect_port();
    g_status.network_initialized = true;
    set_status(BROWSER_TRANSPORT_IDLE, BROWSER_TRANSPORT_OK, "Network Pak client initialized");
}

bool BrowserTransport_StartResource(const char *resource)
{
    if (resource == NULL || resource[0] == '\0')
    {
        resource = BROWSER_TRANSPORT_RESOURCE;
    }

    snprintf(g_status.message, sizeof(g_status.message), "Requesting %.70s", resource);
    g_status.state = BROWSER_TRANSPORT_LOADING;
    g_status.result = BROWSER_TRANSPORT_OK;
    g_status.stream_opened = false;
    g_status.decode_ok = false;
    g_status.expected_size = 0;
    g_status.bytes_received = 0;
    g_status.block_count = 0;
    g_status.page_id = 0;
    g_status.canonical_url[0] = '\0';
    snprintf(g_document_resource, sizeof(g_document_resource), "%s", resource);

    g_document_transfer = DOCUMENT_TRANSFER_OPENING;
    return true;
}

bool BrowserTransport_IsLoading(void)
{
    return g_document_transfer == DOCUMENT_TRANSFER_OPENING ||
        g_document_transfer == DOCUMENT_TRANSFER_READING ||
        g_document_transfer == DOCUMENT_TRANSFER_VALIDATING ||
        g_document_transfer == DOCUMENT_TRANSFER_CLOSING ||
        g_document_transfer == DOCUMENT_TRANSFER_DECODING;
}

void BrowserTransport_Update(void)
{
    uint32_t chunk_index;

    switch (g_document_transfer)
    {
    case DOCUMENT_TRANSFER_OPENING:
        if (stream_open(g_document_resource))
        {
            g_document_transfer = DOCUMENT_TRANSFER_READING;
        }
        else
        {
            document_transfer_fail();
        }
        break;

    case DOCUMENT_TRANSFER_READING:
        for (chunk_index = 0; chunk_index < BROWSER_TRANSPORT_DOCUMENT_CHUNKS_PER_UPDATE &&
             g_status.bytes_received < g_stream.total_length; chunk_index++)
        {
            if (!stream_read_chunk())
            {
                document_transfer_fail();
                return;
            }
        }
        if (g_status.bytes_received == g_stream.total_length)
        {
            set_transport_progress("all chunks received");
            g_document_transfer = DOCUMENT_TRANSFER_VALIDATING;
        }
        break;

    case DOCUMENT_TRANSFER_VALIDATING:
        if (stream_crc32(g_document_buffer, g_stream.total_length) != g_stream.expected_crc32)
        {
            set_transport_error(BROWSER_ERROR_STREAM_READ, "STREAM_CRC_FAIL");
            document_transfer_fail();
        }
        else
        {
            g_document_transfer = DOCUMENT_TRANSFER_CLOSING;
        }
        break;

    case DOCUMENT_TRANSFER_CLOSING:
        stream_close();
        if (g_status.state == BROWSER_TRANSPORT_ERROR)
        {
            g_document_transfer = DOCUMENT_TRANSFER_FAILED;
        }
        else
        {
            g_document_transfer = DOCUMENT_TRANSFER_DECODING;
        }
        break;

    case DOCUMENT_TRANSFER_DECODING:
        if (!decode_wire_document())
        {
            g_document_transfer = DOCUMENT_TRANSFER_FAILED;
        }
        else if (!NICE_LoadDocument(g_status.page_id, &g_decoded_page))
        {
            set_transport_error(BROWSER_ERROR_DOCUMENT_LOAD, "DOCUMENT_LOAD_FAILED");
            g_document_transfer = DOCUMENT_TRANSFER_FAILED;
        }
        else
        {
            snprintf(g_status.message, sizeof(g_status.message), "Loaded %.70s", g_document_resource);
            g_status.state = BROWSER_TRANSPORT_READY;
            g_status.result = BROWSER_TRANSPORT_OK;
            g_document_transfer = DOCUMENT_TRANSFER_READY;
        }
        break;

    default:
        break;
    }
}

void BrowserTransport_Cancel(void)
{
    if (!BrowserTransport_IsLoading())
    {
        return;
    }

    if (g_stream.open)
    {
        stream_close();
    }
    g_document_resource[0] = '\0';
    g_document_transfer = DOCUMENT_TRANSFER_CANCELLED;
    set_status(BROWSER_TRANSPORT_IDLE, BROWSER_TRANSPORT_OK, "Document transfer cancelled");
}

bool BrowserTransport_LoadResource(const char *resource)
{
    if (!BrowserTransport_StartResource(resource))
    {
        return false;
    }

    while (BrowserTransport_IsLoading())
    {
        BrowserTransport_Update();
    }
    return g_document_transfer == DOCUMENT_TRANSFER_READY;
}

bool BrowserTransport_LoadTestDocument(void)
{
    return BrowserTransport_LoadResource(BROWSER_TRANSPORT_TEST_RESOURCE);
}

BrowserTransportStatus BrowserTransport_GetStatus(void)
{
    return g_status;
}
