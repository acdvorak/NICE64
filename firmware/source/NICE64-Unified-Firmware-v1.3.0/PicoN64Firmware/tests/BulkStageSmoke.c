#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "bulk_stage.h"

static uint8_t source[BULK_STAGE_CAPACITY];

static void prepare(void) {
    for (uint16_t i = 0; i < BULK_STAGE_CAPACITY; ++i) {
        source[i] = (uint8_t)((i * 37u + 11u) & 0xFFu);
    }
}

static void fill_and_verify(uint16_t length) {
    const uint8_t stream = 7u;
    const uint16_t generation = (uint16_t)(0x1200u + length);
    const uint32_t base = 0x1000u;
    const uint32_t crc = bulk_stage_crc32(source, length);
    bulk_stage_reset();
    uint16_t offset = 0u;
    while (offset < length) {
        uint16_t count = (uint16_t)(length - offset);
        if (count > BULK_STAGE_INTERNAL_SEGMENT_MAX) count = BULK_STAGE_INTERNAL_SEGMENT_MAX;
        assert(bulk_stage_accept_segment(stream, generation, base, length,
                                         offset, &source[offset], count, crc, true));
        if (offset == 0u) {
            assert(bulk_stage_accept_segment(stream, generation, base, length,
                                             offset, &source[offset], count, crc, true));
        }
        offset = (uint16_t)(offset + count);
    }
    assert(bulk_stage_commit());
    for (offset = 0u; offset < length; offset = (uint16_t)(offset + 16u)) {
        uint8_t output[16];
        uint8_t count = 0u;
        bool end = false;
        assert(bulk_stage_read(stream, generation, base + offset,
                               output, &count, &end));
        const uint8_t expected = length - offset > 16u ? 16u : (uint8_t)(length - offset);
        assert(count == expected);
        assert(memcmp(output, &source[offset], count) == 0);
        assert(end == (offset + count == length));
    }
}

int main(void) {
    prepare();
    fill_and_verify(1u);
    fill_and_verify(16u);
    fill_and_verify(511u);
    fill_and_verify(512u);

    bulk_stage_reset();
    assert(!bulk_stage_begin(1u, 1u, 0u, 513u, 0u, false));
    assert(bulk_stage_snapshot().state == BULK_STAGE_ABORTED);

    const uint32_t crc = bulk_stage_crc32(source, 512u);
    bulk_stage_reset();
    assert(bulk_stage_accept_segment(1u, 2u, 0u, 512u, 0u, source, 224u, crc, false));
    assert(!bulk_stage_accept_segment(1u, 2u, 0u, 512u, 225u, &source[225], 16u, crc, false));
    assert(bulk_stage_snapshot().state == BULK_STAGE_ABORTED);

    bulk_stage_reset();
    assert(bulk_stage_accept_segment(1u, 3u, 0u, 16u, 0u, source, 16u,
                                     bulk_stage_crc32(source, 16u), false));
    assert(!bulk_stage_accept_segment(1u, 4u, 0u, 16u, 0u, source, 16u,
                                      bulk_stage_crc32(source, 16u), false));

    bulk_stage_reset();
    assert(!bulk_stage_accept_segment(1u, 4u, 0u, 512u, 0u, source, 225u,
                                      crc, false));
    bulk_stage_reset();
    assert(!bulk_stage_accept_segment(1u, 4u, 0u, 16u, 15u, source, 2u,
                                      bulk_stage_crc32(source, 16u), false));

    bulk_stage_reset();
    assert(bulk_stage_accept_segment(1u, 5u, 0u, 16u, 0u, source, 16u,
                                     bulk_stage_crc32(source, 16u) ^ 1u, false));
    assert(!bulk_stage_commit());

    bulk_stage_reset();
    const uint32_t crc16 = bulk_stage_crc32(source, 16u);
    assert(bulk_stage_accept_segment(1u, 6u, 0u, 16u, 0u, source, 16u,
                                     crc16, false));
    assert(bulk_stage_commit());
    uint8_t output[16], output_length = 0u;
    bool end = false;
    assert(!bulk_stage_read(2u, 6u, 0u, output, &output_length, &end));
    assert(!bulk_stage_read(1u, 7u, 0u, output, &output_length, &end));
    assert(bulk_stage_read(1u, 6u, 0u, output, &output_length, &end));
    assert(output_length == 16u && memcmp(output, source, 16u) == 0);

    bulk_stage_reset();
    assert(bulk_stage_snapshot().state == BULK_STAGE_EMPTY);
    puts("BulkStageSmoke PASS");
    return 0;
}
