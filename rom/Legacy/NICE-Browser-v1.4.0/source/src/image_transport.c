#include "image_transport.h"

#include "network_pak_transport.h"
#include "protocol.h"

#include <libdragon.h>
#include <stdio.h>
#include <string.h>

static uint8_t g_image_buffer[IMAGE_TRANSPORT_MAX_BYTES] __attribute__((aligned(16)));
static ImageTransportStatus g_status;
static char g_route[24];
static uint8_t g_sequence;
static surface_t g_image_surface;
static bool g_image_surface_valid;

static void invalidate_graphics_wrapper(void)
{
    if (g_image_surface_valid)
    {
        debugf("Browser: image graphics wrapper invalidated\n");
    }
    memset(&g_image_surface, 0, sizeof(g_image_surface));
    g_image_surface_valid = false;
}

static void create_graphics_wrapper(void)
{
    g_image_surface = surface_make_linear((void *)g_status.nimg.pixel_data, FMT_RGBA16,
        g_status.nimg.width, g_status.nimg.height);
    g_image_surface_valid = true;
    debugf("Browser: image complete route=%s dimensions=%ux%u format=RGB5A1 stride=%u payload=%lu rdpByteSwap=no firstPixel=%02X%02X\n",
        g_status.route, g_status.nimg.width, g_status.nimg.height, g_image_surface.stride,
        (unsigned long)g_status.nimg.payload_length,
        g_status.nimg.pixel_data[0], g_status.nimg.pixel_data[1]);
}

static void set_error(ImageTransportError code, const char *message)
{
    g_status.error_code = code;
    g_status.error = message;
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

static void write_be32(uint8_t *data, uint32_t value)
{
    data[0] = (uint8_t)(value >> 24);
    data[1] = (uint8_t)(value >> 16);
    data[2] = (uint8_t)(value >> 8);
    data[3] = (uint8_t)value;
}

static bool image_exchange(nice_net_command_t command, const void *payload, size_t length, nice_net_packet_t *response)
{
    nice_net_packet_t request;
    nice_net_error_t error = NICE_NET_ERR_NONE;

    nice_net_packet_init(&request, command, 0, ++g_sequence, payload, length);
    if (!network_pak_exchange(&request, response, &error))
    {
        set_error(IMAGE_TRANSPORT_ERROR_TRANSPORT, nice_net_error_name(error));
        return false;
    }
    if (response->sequence != request.sequence)
    {
        set_error(IMAGE_TRANSPORT_ERROR_BAD_SEQUENCE, "BAD_SEQUENCE");
        return false;
    }
    return true;
}

static bool image_stream_open_once(void)
{
    nice_net_packet_t response;
    size_t route_length = strlen(g_route);

    if (route_length == 0 || route_length > NICE_NET_MAX_PAYLOAD)
    {
        set_error(IMAGE_TRANSPORT_ERROR_ROUTE_TOO_LONG, "ROUTE_TOO_LONG");
        return false;
    }
    if (!image_exchange(NICE_NET_CMD_OPEN_STREAM, g_route, route_length, &response))
    {
        return false;
    }
    if (response.command != NICE_NET_CMD_STREAM_OPENED || response.payload_length != 10)
    {
        set_error(IMAGE_TRANSPORT_ERROR_BAD_OPEN_RESPONSE, "BAD_OPEN_RESPONSE");
        return false;
    }

    g_status.stream_id = response.payload[0];
    g_status.stream_open = g_status.stream_id != 0;
    g_status.expected_bytes = read_be32(&response.payload[1]);
    g_status.expected_crc32 = read_be32(&response.payload[6]);
    if (g_status.stream_id == 0 || g_status.expected_bytes == 0 ||
        g_status.expected_bytes > IMAGE_TRANSPORT_MAX_BYTES ||
        response.payload[5] != NICE_NET_STREAM_CHUNK_SIZE)
    {
        set_error(IMAGE_TRANSPORT_ERROR_BAD_STREAM_METADATA, "BAD_STREAM_METADATA");
        return false;
    }
    return true;
}

static bool image_stream_read_chunk_once(uint8_t *count)
{
    nice_net_packet_t response;
    uint8_t request[5];

    request[0] = g_status.stream_id;
    write_be32(&request[1], g_status.offset);
    if (!image_exchange(NICE_NET_CMD_READ_STREAM_CHUNK, request, sizeof(request), &response))
    {
        return false;
    }
    if (response.command != NICE_NET_CMD_STREAM_CHUNK || response.payload_length < 7 ||
        response.payload[0] != g_status.stream_id || read_be32(&response.payload[1]) != g_status.offset)
    {
        set_error(IMAGE_TRANSPORT_ERROR_BAD_CHUNK_RESPONSE, "BAD_CHUNK_RESPONSE");
        return false;
    }

    *count = response.payload[5];
    if (*count == 0 || *count > NICE_NET_STREAM_CHUNK_SIZE ||
        response.payload_length != (uint8_t)(7u + *count) ||
        *count > g_status.expected_bytes - g_status.offset ||
        *count > IMAGE_TRANSPORT_MAX_BYTES - g_status.offset)
    {
        set_error(IMAGE_TRANSPORT_ERROR_BAD_CHUNK_LENGTH, "BAD_CHUNK_LENGTH");
        return false;
    }

    memcpy(&g_image_buffer[g_status.offset], &response.payload[7], *count);
    return true;
}

static bool image_stream_close_once(void)
{
    nice_net_packet_t response;

    if (g_status.stream_id == 0)
    {
        return false;
    }
    if (!image_exchange(NICE_NET_CMD_CLOSE_STREAM, &g_status.stream_id, 1, &response) ||
        response.command != NICE_NET_CMD_STREAM_CLOSED)
    {
        set_error(IMAGE_TRANSPORT_ERROR_CLOSE_FAILED, "CLOSE_FAILED");
        return false;
    }
    return true;
}

static uint32_t stream_crc32(const uint8_t *data, uint32_t length)
{
    uint32_t crc = 0xFFFFFFFFu;

    for (uint32_t i = 0; i < length; i++)
    {
        crc ^= data[i];
        for (uint8_t bit = 0; bit < 8; bit++)
        {
            crc = (crc >> 1) ^ (0xEDB88320u & (uint32_t)-(int32_t)(crc & 1u));
        }
    }
    return ~crc;
}

static void close_active(void)
{
    if (g_status.stream_open && !g_status.close_attempted)
    {
        g_status.close_attempted = true;
        g_status.close_succeeded = image_stream_close_once();
        g_status.stream_open = false;
    }
}

static void update_elapsed(void)
{
    if (g_status.start_ticks != 0)
    {
        g_status.elapsed_ticks = (uint32_t)get_ticks() - g_status.start_ticks;
    }
}

static bool validate_nimg(void)
{
    ImageNimgMetadata *nimg = &g_status.nimg;
    uint32_t pixel_count;
    uint32_t expected_payload;

    memset(nimg, 0, sizeof(*nimg));
    nimg->total_resource_length = g_status.bytes_received;
    nimg->header_length = NIMG_HEADER_SIZE;

    if (g_status.nimg.validation_state != IMAGE_NIMG_STREAM_CRC_PASSED &&
        g_status.actual_crc32 != g_status.expected_crc32)
    {
        nimg->validation_state = IMAGE_NIMG_INVALID;
        set_error(IMAGE_TRANSPORT_ERROR_STREAM_CRC_FAILED, "STREAM_CRC_FAILED");
        return false;
    }
    nimg->validation_state = IMAGE_NIMG_STREAM_CRC_PASSED;

    if (g_status.bytes_received < NIMG_HEADER_SIZE)
    {
        set_error(IMAGE_TRANSPORT_ERROR_NIMG_TOO_SHORT, "NIMG_TOO_SHORT");
        goto invalid;
    }
    if (memcmp(g_image_buffer, "NIMG", 4) != 0)
    {
        set_error(IMAGE_TRANSPORT_ERROR_BAD_NIMG_MAGIC, "BAD_NIMG_MAGIC");
        goto invalid;
    }
    if (read_be16(&g_image_buffer[4]) != NIMG_VERSION)
    {
        set_error(IMAGE_TRANSPORT_ERROR_UNSUPPORTED_NIMG_VERSION, "UNSUPPORTED_NIMG_VERSION");
        goto invalid;
    }

    nimg->width = read_be16(&g_image_buffer[6]);
    nimg->height = read_be16(&g_image_buffer[8]);
    nimg->pixel_format = read_be16(&g_image_buffer[10]);
    nimg->payload_length = read_be32(&g_image_buffer[12]);
    nimg->pixel_data_offset = NIMG_HEADER_SIZE;

    if (nimg->width == 0 || nimg->width > IMAGE_MAX_WIDTH)
    {
        set_error(IMAGE_TRANSPORT_ERROR_BAD_IMAGE_WIDTH, "BAD_IMAGE_WIDTH");
        goto invalid;
    }
    if (nimg->height == 0 || nimg->height > IMAGE_MAX_HEIGHT)
    {
        set_error(IMAGE_TRANSPORT_ERROR_BAD_IMAGE_HEIGHT, "BAD_IMAGE_HEIGHT");
        goto invalid;
    }
    if (nimg->pixel_format != NIMG_PIXEL_FORMAT_RGB5A1)
    {
        set_error(IMAGE_TRANSPORT_ERROR_UNSUPPORTED_PIXEL_FORMAT, "UNSUPPORTED_PIXEL_FORMAT");
        goto invalid;
    }

    pixel_count = (uint32_t)nimg->width * nimg->height;
    if (pixel_count / nimg->width != nimg->height ||
        pixel_count > UINT32_MAX / NIMG_BYTES_PER_PIXEL)
    {
        set_error(IMAGE_TRANSPORT_ERROR_IMAGE_DIMENSION_OVERFLOW, "IMAGE_DIMENSION_OVERFLOW");
        goto invalid;
    }
    expected_payload = pixel_count * NIMG_BYTES_PER_PIXEL;
    if (nimg->payload_length == 0 || nimg->payload_length != expected_payload)
    {
        set_error(IMAGE_TRANSPORT_ERROR_PAYLOAD_SIZE_MISMATCH, "PAYLOAD_SIZE_MISMATCH");
        goto invalid;
    }
    if (nimg->payload_length > UINT32_MAX - nimg->header_length ||
        nimg->header_length + nimg->payload_length != nimg->total_resource_length)
    {
        set_error(IMAGE_TRANSPORT_ERROR_TOTAL_LENGTH_MISMATCH, "TOTAL_LENGTH_MISMATCH");
        goto invalid;
    }
    if (nimg->pixel_data_offset > IMAGE_TRANSPORT_MAX_BYTES ||
        nimg->payload_length > IMAGE_TRANSPORT_MAX_BYTES - nimg->pixel_data_offset)
    {
        set_error(IMAGE_TRANSPORT_ERROR_PIXEL_DATA_OUT_OF_BOUNDS, "PIXEL_DATA_OUT_OF_BOUNDS");
        goto invalid;
    }
    if (nimg->width != g_status.expected_width || nimg->height != g_status.expected_height)
    {
        set_error(IMAGE_TRANSPORT_ERROR_IMAGE_DIMENSION_MISMATCH, "IMAGE_DIMENSION_MISMATCH");
        goto invalid;
    }

    nimg->pixel_data = &g_image_buffer[nimg->pixel_data_offset];
    nimg->validation_state = IMAGE_NIMG_VALID;
    create_graphics_wrapper();
    return true;

invalid:
    nimg->pixel_data = NULL;
    nimg->validation_state = IMAGE_NIMG_INVALID;
    return false;
}

void ImageTransport_Init(void)
{
    ImageTransport_Reset();
}

void ImageTransport_Reset(void)
{
    invalidate_graphics_wrapper();
    memset(g_image_buffer, 0, sizeof(g_image_buffer));
    memset(&g_status, 0, sizeof(g_status));
    g_status.state = IMAGE_TRANSPORT_IDLE;
    g_status.route = g_route;
    g_route[0] = '\0';
}

bool ImageTransport_Start(const char *route, uint32_t page_id, uint16_t expected_width, uint16_t expected_height)
{
    size_t route_length;

    if (route == NULL || strncmp(route, "browser://i/", 12) != 0 ||
        g_status.state == IMAGE_TRANSPORT_OPENING ||
        g_status.state == IMAGE_TRANSPORT_READING ||
        g_status.state == IMAGE_TRANSPORT_VERIFYING ||
        g_status.state == IMAGE_TRANSPORT_VALIDATING_NIMG)
    {
        return false;
    }

    route_length = strlen(route);
    ImageTransport_Reset();
    if (route_length == 0 || route_length >= sizeof(g_route))
    {
        g_status.state = IMAGE_TRANSPORT_FAILED;
        set_error(IMAGE_TRANSPORT_ERROR_ROUTE_TOO_LONG, "ROUTE_TOO_LONG");
        return false;
    }

    memcpy(g_route, route, route_length + 1u);
    g_status.page_id = page_id;
    g_status.expected_width = expected_width;
    g_status.expected_height = expected_height;
    g_status.state = IMAGE_TRANSPORT_OPENING;
    return true;
}

void ImageTransport_Update(void)
{
    if (g_status.state == IMAGE_TRANSPORT_OPENING)
    {
        if (!image_stream_open_once())
        {
            close_active();
            g_status.state = IMAGE_TRANSPORT_FAILED;
            return;
        }
        g_status.start_ticks = (uint32_t)get_ticks();
        g_status.state = IMAGE_TRANSPORT_READING;
        return;
    }

    if (g_status.state == IMAGE_TRANSPORT_READING)
    {
        update_elapsed();
        for (uint8_t i = 0; i < IMAGE_CHUNKS_PER_UPDATE && g_status.offset < g_status.expected_bytes; i++)
        {
            uint8_t count = 0;
            if (!image_stream_read_chunk_once(&count))
            {
                close_active();
                update_elapsed();
                g_status.state = IMAGE_TRANSPORT_FAILED;
                return;
            }
            g_status.offset += count;
            g_status.bytes_received += count;
            g_status.chunk_count++;
        }
        if (g_status.bytes_received == g_status.expected_bytes)
        {
            g_status.state = IMAGE_TRANSPORT_VERIFYING;
        }
        return;
    }

    if (g_status.state == IMAGE_TRANSPORT_VERIFYING)
    {
        g_status.actual_crc32 = stream_crc32(g_image_buffer, g_status.expected_bytes);
        if (g_status.actual_crc32 != g_status.expected_crc32)
        {
            close_active();
            update_elapsed();
            g_status.nimg.validation_state = IMAGE_NIMG_INVALID;
            set_error(IMAGE_TRANSPORT_ERROR_STREAM_CRC_FAILED, "STREAM_CRC_FAILED");
            g_status.state = IMAGE_TRANSPORT_FAILED;
            return;
        }

        g_status.nimg.validation_state = IMAGE_NIMG_STREAM_CRC_PASSED;
        close_active();
        update_elapsed();
        if (g_status.error != NULL)
        {
            g_status.state = IMAGE_TRANSPORT_FAILED;
            return;
        }
        g_status.state = IMAGE_TRANSPORT_VALIDATING_NIMG;
        return;
    }

    if (g_status.state == IMAGE_TRANSPORT_VALIDATING_NIMG)
    {
        if (validate_nimg())
        {
            g_status.state = IMAGE_TRANSPORT_COMPLETE;
        }
        else
        {
            g_status.state = IMAGE_TRANSPORT_FAILED;
        }
    }
}

void ImageTransport_Cancel(void)
{
    close_active();
    invalidate_graphics_wrapper();
    memset(g_image_buffer, 0, sizeof(g_image_buffer));
    g_status.nimg.pixel_data = NULL;
    g_status.nimg.validation_state = IMAGE_NIMG_INVALID;
    g_status.state = IMAGE_TRANSPORT_CANCELLED;
    if (g_status.error == NULL)
    {
        set_error(IMAGE_TRANSPORT_ERROR_CANCELLED, "CANCELLED");
    }
}

void ImageTransport_InvalidatePageRoute(void)
{
    close_active();
    invalidate_graphics_wrapper();
    memset(g_image_buffer, 0, sizeof(g_image_buffer));
    g_status.nimg.pixel_data = NULL;
    g_status.nimg.validation_state = IMAGE_NIMG_INVALID;
    g_status.state = IMAGE_TRANSPORT_FAILED;
    set_error(IMAGE_TRANSPORT_ERROR_PAGE_ROUTE_CHANGED, "PAGE_ROUTE_CHANGED");
}

ImageTransportStatus ImageTransport_GetStatus(void)
{
    return g_status;
}

const uint8_t *ImageTransport_GetBuffer(void)
{
    return g_image_buffer;
}

const uint8_t *ImageTransport_GetPixelData(void)
{
    return g_status.state == IMAGE_TRANSPORT_COMPLETE &&
        g_status.nimg.validation_state == IMAGE_NIMG_VALID ? g_status.nimg.pixel_data : NULL;
}

bool ImageTransport_GetValidatedImage(ImageTransportImage *image)
{
    if (image == NULL || g_status.state != IMAGE_TRANSPORT_COMPLETE ||
        g_status.nimg.validation_state != IMAGE_NIMG_VALID || !g_image_surface_valid)
    {
        return false;
    }

    image->pixels = g_status.nimg.pixel_data;
    image->surface = &g_image_surface;
    image->route = g_status.route;
    image->page_id = g_status.page_id;
    image->width = g_status.nimg.width;
    image->height = g_status.nimg.height;
    image->stride = g_image_surface.stride;
    image->pixel_format = g_status.nimg.pixel_format;
    image->payload_length = g_status.nimg.payload_length;
    return true;
}
