#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define BULK_STAGE_CAPACITY 512u
#define BULK_STAGE_DELIVERY_MAX 16u
#define BULK_STAGE_INTERNAL_SEGMENT_MAX 224u

typedef enum {
    BULK_STAGE_EMPTY = 0,
    BULK_STAGE_FILLING,
    BULK_STAGE_READY,
    BULK_STAGE_CONSUMING,
    BULK_STAGE_COMPLETE,
    BULK_STAGE_ABORTED,
} bulk_stage_state_t;

typedef struct {
    bulk_stage_state_t state;
    uint8_t stream_id;
    uint16_t generation;
    uint32_t base_offset;
    uint16_t total_length;
    uint16_t valid_length;
    uint32_t expected_crc32;
    bool stream_end;
} bulk_stage_snapshot_t;

void bulk_stage_reset(void);
bool bulk_stage_begin(uint8_t stream_id,
                      uint16_t generation,
                      uint32_t base_offset,
                      uint16_t total_length,
                      uint32_t expected_crc32,
                      bool stream_end);
bool bulk_stage_accept_segment(uint8_t stream_id,
                               uint16_t generation,
                               uint32_t base_offset,
                               uint16_t total_length,
                               uint16_t segment_offset,
                               const uint8_t *data,
                               uint16_t data_length,
                               uint32_t expected_crc32,
                               bool stream_end);
bool bulk_stage_commit(void);
bool bulk_stage_read(uint8_t stream_id,
                     uint16_t generation,
                     uint32_t absolute_offset,
                     uint8_t *output,
                     uint8_t *output_length,
                     bool *stream_end);
void bulk_stage_abort(void);
bulk_stage_snapshot_t bulk_stage_snapshot(void);
uint32_t bulk_stage_crc32(const uint8_t *data, size_t length);
