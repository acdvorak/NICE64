#ifndef IMAGE_TRANSPORT_H
#define IMAGE_TRANSPORT_H

#include "logical_stream_reader.h"

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <libdragon.h>

#define IMAGE_TRANSPORT_MAX_BYTES 65536u
#define IMAGE_CHUNKS_PER_UPDATE 2u

#define NIMG_HEADER_SIZE 16u
#define NIMG_VERSION 1u
#define NIMG_PIXEL_FORMAT_RGB5A1 1u
#define NIMG_BYTES_PER_PIXEL 2u
#define IMAGE_MAX_WIDTH 160u
#define IMAGE_MAX_HEIGHT 96u

typedef enum image_transport_state_e
{
    IMAGE_TRANSPORT_IDLE,
    IMAGE_TRANSPORT_OPENING,
    IMAGE_TRANSPORT_READING,
    IMAGE_TRANSPORT_VERIFYING,
    IMAGE_TRANSPORT_VALIDATING_NIMG,
    IMAGE_TRANSPORT_COMPLETE,
    IMAGE_TRANSPORT_FAILED,
    IMAGE_TRANSPORT_CANCELLED
} ImageTransportState;

typedef enum image_transport_error_e
{
    IMAGE_TRANSPORT_ERROR_NONE,
    IMAGE_TRANSPORT_ERROR_CANCELLED,
    IMAGE_TRANSPORT_ERROR_TRANSPORT,
    IMAGE_TRANSPORT_ERROR_ROUTE_TOO_LONG,
    IMAGE_TRANSPORT_ERROR_BAD_SEQUENCE,
    IMAGE_TRANSPORT_ERROR_BAD_OPEN_RESPONSE,
    IMAGE_TRANSPORT_ERROR_BAD_STREAM_METADATA,
    IMAGE_TRANSPORT_ERROR_BAD_CHUNK_RESPONSE,
    IMAGE_TRANSPORT_ERROR_BAD_CHUNK_LENGTH,
    IMAGE_TRANSPORT_ERROR_CLOSE_FAILED,
    IMAGE_TRANSPORT_ERROR_STREAM_CRC_FAILED,
    IMAGE_TRANSPORT_ERROR_NIMG_TOO_SHORT,
    IMAGE_TRANSPORT_ERROR_BAD_NIMG_MAGIC,
    IMAGE_TRANSPORT_ERROR_UNSUPPORTED_NIMG_VERSION,
    IMAGE_TRANSPORT_ERROR_BAD_IMAGE_WIDTH,
    IMAGE_TRANSPORT_ERROR_BAD_IMAGE_HEIGHT,
    IMAGE_TRANSPORT_ERROR_UNSUPPORTED_PIXEL_FORMAT,
    IMAGE_TRANSPORT_ERROR_IMAGE_DIMENSION_OVERFLOW,
    IMAGE_TRANSPORT_ERROR_IMAGE_DIMENSION_MISMATCH,
    IMAGE_TRANSPORT_ERROR_PAYLOAD_SIZE_MISMATCH,
    IMAGE_TRANSPORT_ERROR_TOTAL_LENGTH_MISMATCH,
    IMAGE_TRANSPORT_ERROR_PIXEL_DATA_OUT_OF_BOUNDS,
    IMAGE_TRANSPORT_ERROR_PAGE_ROUTE_CHANGED
} ImageTransportError;

typedef enum image_nimg_validation_state_e
{
    IMAGE_NIMG_NOT_VALIDATED,
    IMAGE_NIMG_STREAM_CRC_PASSED,
    IMAGE_NIMG_VALID,
    IMAGE_NIMG_INVALID
} ImageNimgValidationState;

typedef struct image_nimg_metadata_s
{
    ImageNimgValidationState validation_state;
    uint16_t width;
    uint16_t height;
    uint16_t pixel_format;
    uint32_t payload_length;
    uint32_t header_length;
    uint32_t total_resource_length;
    uint32_t pixel_data_offset;
    const uint8_t *pixel_data;
} ImageNimgMetadata;

typedef struct image_transport_status_s
{
    ImageTransportState state;
    const char *route;
    uint32_t page_id;
    uint16_t expected_width;
    uint16_t expected_height;
    uint8_t stream_id;
    uint32_t offset;
    uint32_t expected_bytes;
    uint32_t expected_crc32;
    uint32_t actual_crc32;
    uint32_t bytes_received;
    uint32_t chunk_count;
    uint32_t start_ticks;
    uint32_t elapsed_ticks;
    bool bulk_capability;
    LogicalStreamReaderMode reader_mode;
    LogicalStreamFallback fallback;
    uint32_t staged_blocks;
    uint32_t legacy_read16_count;
    bool eof;
    bool stream_open;
    bool close_attempted;
    bool close_succeeded;
    ImageTransportError error_code;
    const char *error;
    ImageNimgMetadata nimg;
} ImageTransportStatus;

typedef struct image_transport_image_s
{
    const uint8_t *pixels;
    const surface_t *surface;
    const char *route;
    uint32_t page_id;
    uint16_t width;
    uint16_t height;
    uint16_t stride;
    uint16_t pixel_format;
    uint32_t payload_length;
} ImageTransportImage;

void ImageTransport_Init(void);
void ImageTransport_Reset(void);
bool ImageTransport_Start(const char *route, uint32_t page_id, uint16_t expected_width, uint16_t expected_height);
void ImageTransport_Update(void);
void ImageTransport_Cancel(void);
void ImageTransport_InvalidatePageRoute(void);
ImageTransportStatus ImageTransport_GetStatus(void);
const uint8_t *ImageTransport_GetBuffer(void);
const uint8_t *ImageTransport_GetPixelData(void);
bool ImageTransport_GetValidatedImage(ImageTransportImage *image);

#endif
