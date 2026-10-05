#ifndef BROWSER_TRANSPORT_H
#define BROWSER_TRANSPORT_H

#include "logical_stream_reader.h"

#include <stdbool.h>
#include <stdint.h>

#define BROWSER_TRANSPORT_TEST_RESOURCE "browser://test"
#define BROWSER_TRANSPORT_LIVE_RESOURCE "browser://live"
#define BROWSER_TRANSPORT_RESOURCE BROWSER_TRANSPORT_LIVE_RESOURCE
#define BROWSER_TRANSPORT_MAX_DOCUMENT_SIZE 16384u
#define BROWSER_TRANSPORT_MAX_BLOCKS 96u
#define BROWSER_TRANSPORT_TEXT_POOL_SIZE 12288u
#define BROWSER_TRANSPORT_MAX_TEXT_BYTES 240u
#define BROWSER_TRANSPORT_MAX_TARGET_BYTES 96u
#define BROWSER_TRANSPORT_DOCUMENT_CHUNKS_PER_UPDATE 2u

typedef enum browser_transport_state_e
{
    BROWSER_TRANSPORT_IDLE,
    BROWSER_TRANSPORT_LOADING,
    BROWSER_TRANSPORT_READY,
    BROWSER_TRANSPORT_ERROR
} BrowserTransportState;

typedef enum browser_transport_result_e
{
    BROWSER_TRANSPORT_OK = 0,
    BROWSER_ERROR_NETWORK_PAK_INIT,
    BROWSER_ERROR_NETWORK_PAK_NOT_FOUND,
    BROWSER_ERROR_STREAM_OPEN,
    BROWSER_ERROR_RESOURCE_REQUEST,
    BROWSER_ERROR_STREAM_READ,
    BROWSER_ERROR_STREAM_CLOSE,
    BROWSER_ERROR_SHORT_PAYLOAD,
    BROWSER_ERROR_BAD_MAGIC,
    BROWSER_ERROR_UNSUPPORTED_VERSION,
    BROWSER_ERROR_BAD_PAYLOAD_LENGTH,
    BROWSER_ERROR_BLOCK_LIMIT,
    BROWSER_ERROR_TEXT_POOL_LIMIT,
    BROWSER_ERROR_BLOCK_DECODE,
    BROWSER_ERROR_DOCUMENT_LOAD
} BrowserTransportResult;

typedef struct browser_transport_status_s
{
    BrowserTransportState state;
    BrowserTransportResult result;
    bool network_initialized;
    bool stream_opened;
    bool decode_ok;
    uint32_t expected_size;
    uint32_t bytes_received;
    uint32_t block_count;
    uint32_t page_id;
    bool bulk_capability;
    LogicalStreamReaderMode reader_mode;
    LogicalStreamFallback fallback;
    uint32_t staged_blocks;
    uint32_t legacy_read16_count;
    bool eof;
    char canonical_url[129];
    int last_write_status;
    int last_read_status;
    char message[96];
} BrowserTransportStatus;

void BrowserTransport_Init(void);
bool BrowserTransport_RegisterUrl(const char *input, char *route_out, uint32_t route_out_size);
bool BrowserTransport_RegisterSearchQuery(const char *query, char *route_out, uint32_t route_out_size);
bool BrowserTransport_StartResource(const char *resource);
void BrowserTransport_Update(void);
void BrowserTransport_Cancel(void);
bool BrowserTransport_IsLoading(void);
bool BrowserTransport_LoadResource(const char *resource);
bool BrowserTransport_LoadTestDocument(void);
bool BrowserTransport_GetPendingPollRoute(char *route_out, uint32_t route_out_size);
BrowserTransportStatus BrowserTransport_GetStatus(void);
const char *BrowserTransport_ResultName(BrowserTransportResult result);

#endif
