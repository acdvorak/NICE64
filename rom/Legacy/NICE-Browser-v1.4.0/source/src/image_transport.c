#include "image_transport.h"

#include "logical_stream_reader.h"
#include "network_pak_transport.h"
#include "protocol.h"

#include <libdragon.h>
#include <stdio.h>
#include <string.h>

static uint8_t g_image_buffer[IMAGE_TRANSPORT_MAX_BYTES] __attribute__((aligned(16)));
static ImageTransportStatus g_status;
static char g_route[24];
static LogicalStreamReader g_reader;
static char g_reader_error_detail[96];
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

static const char *reader_error_detail(void)
{
    if (g_reader.error == LOGICAL_STREAM_ERROR_BAD_SEQUENCE)
    {
        snprintf(g_reader_error_detail, sizeof(g_reader_error_detail),
            "seq %u/%u q%02X/r%02X o%lu g%u b%lu",
            (unsigned)g_reader.mismatch_expected_sequence,
            (unsigned)g_reader.mismatch_actual_sequence,
            (unsigned)g_reader.mismatch_request_command,
            (unsigned)g_reader.mismatch_response_command,
            (unsigned long)g_reader.mismatch_offset,
            (unsigned)g_reader.mismatch_generation,
            (unsigned long)g_reader.mismatch_stage_base);
        debugf("Browser: image sequence mismatch expected=%u actual=%u request=0x%02X response=0x%02X offset=%lu generation=%u stageBase=%lu\n",
            (unsigned)g_reader.mismatch_expected_sequence,
            (unsigned)g_reader.mismatch_actual_sequence,
            (unsigned)g_reader.mismatch_request_command,
            (unsigned)g_reader.mismatch_response_command,
            (unsigned long)g_reader.mismatch_offset,
            (unsigned)g_reader.mismatch_generation,
            (unsigned long)g_reader.mismatch_stage_base);
        for (uint8_t i = 0u; i < g_reader.exchange_history_count; i++)
        {
            uint8_t index = (uint8_t)((g_reader.exchange_history_next +
                LOGICAL_STREAM_EXCHANGE_HISTORY_COUNT -
                g_reader.exchange_history_count + i) %
                LOGICAL_STREAM_EXCHANGE_HISTORY_COUNT);
            const LogicalStreamExchangeTrace *trace = &g_reader.exchange_history[index];
            debugf("Browser: image exchange history rq=0x%02X/%u rp=0x%02X/%u offset=%lu generation=%u stageBase=%lu\n",
                (unsigned)trace->request_command,
                (unsigned)trace->request_sequence,
                (unsigned)trace->response_command,
                (unsigned)trace->response_sequence,
                (unsigned long)trace->offset,
                (unsigned)trace->generation,
                (unsigned long)trace->stage_base);
        }
        return g_reader_error_detail;
    }
    if (g_reader.error == LOGICAL_STREAM_ERROR_TRANSPORT)
    {
        network_pak_status_t status = network_pak_get_status();
        if (status.last_transport_error != NULL && status.last_transport_error[0] != '\0')
        {
            return status.last_transport_error;
        }
    }
    return LogicalStreamReader_ErrorName(g_reader.error);
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

static bool image_stream_open_once(void)
{
    size_t route_length = strlen(g_route);

    if (route_length == 0 || route_length > NICE_NET_MAX_PAYLOAD)
    {
        set_error(IMAGE_TRANSPORT_ERROR_ROUTE_TOO_LONG, "ROUTE_TOO_LONG");
        return false;
    }
    if (!LogicalStreamReader_Open(&g_reader, g_route))
    {
        set_error(IMAGE_TRANSPORT_ERROR_TRANSPORT, reader_error_detail());
        return false;
    }

    g_status.stream_id = g_reader.stream_id;
    g_status.stream_open = g_reader.open;
    g_status.expected_bytes = g_reader.total_length;
    g_status.expected_crc32 = g_reader.expected_crc32;
    g_status.bulk_capability = g_reader.bulk_capability;
    g_status.reader_mode = g_reader.mode;
    g_status.fallback = g_reader.fallback;
    debugf("Browser: image reader capability=%s selected=%s fallback=%s bytes=%lu route=%s\n",
        g_reader.bulk_capability ? "yes" : "no",
        LogicalStreamReader_ModeName(g_reader.mode),
        LogicalStreamReader_FallbackName(g_reader.fallback),
        (unsigned long)g_reader.total_length,
        g_route);
    if (g_status.stream_id == 0 || g_status.expected_bytes == 0 ||
        g_status.expected_bytes > IMAGE_TRANSPORT_MAX_BYTES ||
        g_reader.delivery_size != NICE_NET_STREAM_CHUNK_SIZE)
    {
        set_error(IMAGE_TRANSPORT_ERROR_BAD_STREAM_METADATA, "BAD_STREAM_METADATA");
        return false;
    }
    return true;
}

static bool image_stream_read_chunk_once(uint8_t *count)
{
    LogicalStreamReadResult result;

    result = LogicalStreamReader_ReadNext(
        &g_reader,
        &g_image_buffer[g_status.offset],
        IMAGE_TRANSPORT_MAX_BYTES - g_status.offset,
        count);
    if (result != LOGICAL_STREAM_READ_OK)
    {
        set_error(IMAGE_TRANSPORT_ERROR_BAD_CHUNK_RESPONSE,
            result == LOGICAL_STREAM_READ_EOF ? "UNEXPECTED_EOF" :
            reader_error_detail());
        return false;
    }

    g_status.staged_blocks = g_reader.staged_blocks;
    g_status.legacy_read16_count = g_reader.legacy_read16_count;
    g_status.eof = g_reader.eof;
    return true;
}

static bool image_stream_close_once(void)
{
    if (g_status.stream_id == 0)
    {
        return false;
    }
    if (!LogicalStreamReader_Close(&g_reader))
    {
        set_error(IMAGE_TRANSPORT_ERROR_CLOSE_FAILED, reader_error_detail());
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
        ImageTransportError primary_error_code = g_status.error_code;
        const char *primary_error = g_status.error;

        g_status.close_attempted = true;
        g_status.close_succeeded = image_stream_close_once();

        /* Closing after a read failure is best-effort cleanup. If cleanup
         * also fails, retain the failure that caused cleanup so the visible
         * diagnostic reports the actionable root condition. */
        if (!g_status.close_succeeded && primary_error != NULL)
        {
            g_status.error_code = primary_error_code;
            g_status.error = primary_error;
        }

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
    LogicalStreamReader_Init(&g_reader);
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
