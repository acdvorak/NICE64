#ifndef LOGICAL_STREAM_READER_H
#define LOGICAL_STREAM_READER_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define LOGICAL_STREAM_BULK_MAX_BYTES 512u
#define LOGICAL_STREAM_EXCHANGE_HISTORY_COUNT 8u

typedef struct logical_stream_exchange_trace_s
{
    uint8_t request_command;
    uint8_t request_sequence;
    uint8_t response_command;
    uint8_t response_sequence;
    uint16_t generation;
    uint32_t offset;
    uint32_t stage_base;
} LogicalStreamExchangeTrace;

typedef enum logical_stream_reader_mode_e
{
    LOGICAL_STREAM_READER_LEGACY,
    LOGICAL_STREAM_READER_BULK
} LogicalStreamReaderMode;

typedef enum logical_stream_fallback_e
{
    LOGICAL_STREAM_FALLBACK_NONE,
    LOGICAL_STREAM_FALLBACK_CAPABILITY_UNAVAILABLE,
    LOGICAL_STREAM_FALLBACK_CAPABILITY_UNSUPPORTED,
    LOGICAL_STREAM_FALLBACK_CAPABILITY_MALFORMED
} LogicalStreamFallback;

typedef enum logical_stream_error_e
{
    LOGICAL_STREAM_ERROR_NONE,
    LOGICAL_STREAM_ERROR_TRANSPORT,
    LOGICAL_STREAM_ERROR_BAD_SEQUENCE,
    LOGICAL_STREAM_ERROR_BAD_OPEN_RESPONSE,
    LOGICAL_STREAM_ERROR_BAD_STREAM_METADATA,
    LOGICAL_STREAM_ERROR_BAD_BULK_READY,
    LOGICAL_STREAM_ERROR_BAD_CHUNK_RESPONSE,
    LOGICAL_STREAM_ERROR_BAD_CHUNK_LENGTH,
    LOGICAL_STREAM_ERROR_READ_PAST_END,
    LOGICAL_STREAM_ERROR_CLOSE_FAILED
} LogicalStreamError;

typedef enum logical_stream_read_result_e
{
    LOGICAL_STREAM_READ_ERROR = -1,
    LOGICAL_STREAM_READ_OK = 0,
    LOGICAL_STREAM_READ_EOF = 1
} LogicalStreamReadResult;

typedef struct logical_stream_reader_s
{
    bool open;
    bool eof;
    bool bulk_capability;
    bool stage_ready;
    uint8_t stream_id;
    uint8_t delivery_size;
    uint8_t sequence;
    uint16_t generation;
    uint32_t total_length;
    uint32_t expected_crc32;
    uint32_t offset;
    uint32_t stage_base;
    uint16_t stage_length;
    uint32_t stage_crc32;
    LogicalStreamReaderMode mode;
    LogicalStreamFallback fallback;
    LogicalStreamError error;
    uint32_t staged_blocks;
    uint32_t legacy_read16_count;
    uint32_t physical_delivery_count;
    LogicalStreamExchangeTrace exchange_history[LOGICAL_STREAM_EXCHANGE_HISTORY_COUNT];
    uint8_t exchange_history_next;
    uint8_t exchange_history_count;
    uint8_t mismatch_request_command;
    uint8_t mismatch_expected_sequence;
    uint8_t mismatch_response_command;
    uint8_t mismatch_actual_sequence;
    uint16_t mismatch_generation;
    uint32_t mismatch_offset;
    uint32_t mismatch_stage_base;
} LogicalStreamReader;

void LogicalStreamReader_Init(LogicalStreamReader *reader);
bool LogicalStreamReader_Open(LogicalStreamReader *reader, const char *resource);
LogicalStreamReadResult LogicalStreamReader_ReadNext(
    LogicalStreamReader *reader,
    uint8_t *destination,
    size_t destination_capacity,
    uint8_t *count);
bool LogicalStreamReader_Close(LogicalStreamReader *reader);
const char *LogicalStreamReader_ModeName(LogicalStreamReaderMode mode);
const char *LogicalStreamReader_FallbackName(LogicalStreamFallback fallback);
const char *LogicalStreamReader_ErrorName(LogicalStreamError error);

#endif
