#include "bulk_stage.h"

#include <string.h>

typedef struct {
    uint8_t buffer[BULK_STAGE_CAPACITY];
    bulk_stage_snapshot_t metadata;
} bulk_stage_storage_t;

/* Core1-owned. Core0 never reads, writes, or waits on this object. */
static bulk_stage_storage_t stage;

uint32_t bulk_stage_crc32(const uint8_t *data, size_t length) {
    uint32_t crc = 0xFFFFFFFFu;
    if (data == NULL && length != 0u) return 0u;
    for (size_t index = 0u; index < length; ++index) {
        crc ^= data[index];
        for (uint8_t bit = 0u; bit < 8u; ++bit) {
            crc = (crc >> 1u) ^ (0xEDB88320u & (uint32_t)-(int32_t)(crc & 1u));
        }
    }
    return ~crc;
}

void bulk_stage_reset(void) {
    memset(&stage, 0, sizeof(stage));
    stage.metadata.state = BULK_STAGE_EMPTY;
}

bool bulk_stage_begin(uint8_t stream_id,
                      uint16_t generation,
                      uint32_t base_offset,
                      uint16_t total_length,
                      uint32_t expected_crc32,
                      bool stream_end) {
    if (stream_id == 0u || generation == 0u || total_length == 0u ||
        total_length > BULK_STAGE_CAPACITY) {
        bulk_stage_abort();
        return false;
    }
    memset(stage.buffer, 0, sizeof(stage.buffer));
    stage.metadata.state = BULK_STAGE_FILLING;
    stage.metadata.stream_id = stream_id;
    stage.metadata.generation = generation;
    stage.metadata.base_offset = base_offset;
    stage.metadata.total_length = total_length;
    stage.metadata.valid_length = 0u;
    stage.metadata.expected_crc32 = expected_crc32;
    stage.metadata.stream_end = stream_end;
    return true;
}

static bool metadata_matches(uint8_t stream_id,
                             uint16_t generation,
                             uint32_t base_offset,
                             uint16_t total_length,
                             uint32_t expected_crc32,
                             bool stream_end) {
    return stage.metadata.stream_id == stream_id &&
           stage.metadata.generation == generation &&
           stage.metadata.base_offset == base_offset &&
           stage.metadata.total_length == total_length &&
           stage.metadata.expected_crc32 == expected_crc32 &&
           stage.metadata.stream_end == stream_end;
}

bool bulk_stage_accept_segment(uint8_t stream_id,
                               uint16_t generation,
                               uint32_t base_offset,
                               uint16_t total_length,
                               uint16_t segment_offset,
                               const uint8_t *data,
                               uint16_t data_length,
                               uint32_t expected_crc32,
                               bool stream_end) {
    if (data == NULL || data_length == 0u ||
        data_length > BULK_STAGE_INTERNAL_SEGMENT_MAX ||
        total_length == 0u || total_length > BULK_STAGE_CAPACITY ||
        segment_offset > total_length ||
        data_length > (uint16_t)(total_length - segment_offset)) {
        bulk_stage_abort();
        return false;
    }
    if (stage.metadata.state == BULK_STAGE_EMPTY ||
        stage.metadata.state == BULK_STAGE_ABORTED ||
        stage.metadata.state == BULK_STAGE_COMPLETE) {
        if (!bulk_stage_begin(stream_id, generation, base_offset, total_length,
                              expected_crc32, stream_end)) return false;
    }
    if (stage.metadata.state != BULK_STAGE_FILLING ||
        !metadata_matches(stream_id, generation, base_offset, total_length,
                          expected_crc32, stream_end)) {
        bulk_stage_abort();
        return false;
    }
    if (segment_offset < stage.metadata.valid_length) {
        const uint16_t end = (uint16_t)(segment_offset + data_length);
        if (end <= stage.metadata.valid_length &&
            memcmp(&stage.buffer[segment_offset], data, data_length) == 0) {
            return true;
        }
        bulk_stage_abort();
        return false;
    }
    if (segment_offset != stage.metadata.valid_length) {
        bulk_stage_abort();
        return false;
    }
    memcpy(&stage.buffer[segment_offset], data, data_length);
    stage.metadata.valid_length = (uint16_t)(stage.metadata.valid_length + data_length);
    return true;
}

bool bulk_stage_commit(void) {
    if (stage.metadata.state != BULK_STAGE_FILLING ||
        stage.metadata.valid_length != stage.metadata.total_length ||
        bulk_stage_crc32(stage.buffer, stage.metadata.total_length) !=
            stage.metadata.expected_crc32) {
        bulk_stage_abort();
        return false;
    }
    stage.metadata.state = BULK_STAGE_READY;
    return true;
}

bool bulk_stage_read(uint8_t stream_id,
                     uint16_t generation,
                     uint32_t absolute_offset,
                     uint8_t *output,
                     uint8_t *output_length,
                     bool *stream_end) {
    if (output == NULL || output_length == NULL || stream_end == NULL ||
        (stage.metadata.state != BULK_STAGE_READY &&
         stage.metadata.state != BULK_STAGE_CONSUMING &&
         stage.metadata.state != BULK_STAGE_COMPLETE) ||
        stream_id != stage.metadata.stream_id ||
        generation != stage.metadata.generation ||
        absolute_offset < stage.metadata.base_offset) return false;

    const uint32_t relative32 = absolute_offset - stage.metadata.base_offset;
    if (relative32 >= stage.metadata.total_length) return false;
    const uint16_t relative = (uint16_t)relative32;
    const uint16_t remaining = (uint16_t)(stage.metadata.total_length - relative);
    const uint8_t count = remaining > BULK_STAGE_DELIVERY_MAX
        ? BULK_STAGE_DELIVERY_MAX : (uint8_t)remaining;
    memcpy(output, &stage.buffer[relative], count);
    *output_length = count;
    *stream_end = stage.metadata.stream_end && count == remaining;
    stage.metadata.state = count == remaining ? BULK_STAGE_COMPLETE : BULK_STAGE_CONSUMING;
    return true;
}

void bulk_stage_abort(void) {
    stage.metadata.state = BULK_STAGE_ABORTED;
    stage.metadata.valid_length = 0u;
}

bulk_stage_snapshot_t bulk_stage_snapshot(void) {
    return stage.metadata;
}
