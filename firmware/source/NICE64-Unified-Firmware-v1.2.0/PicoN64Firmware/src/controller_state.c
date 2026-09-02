#include "controller_state.h"

#include <stdbool.h>

#include "pico/platform/compiler.h"
#include "pico/platform/sections.h"

#define CONTROLLER_STATE_RAW_PATTERN 0x43305257u
#define CONTROLLER_STATE_SNAPSHOT_PATTERN 0x4332534Eu

typedef struct {
    uint32_t generation;
    union {
        uint8_t bytes[4];
        uint32_t word;
    } state;
    uint32_t validation_pattern;
} controller_raw_input_t;

typedef struct {
    uint32_t generation;
    union {
        uint8_t bytes[4];
        uint32_t word;
    } state;
    uint32_t validation_pattern;
} controller_snapshot_t;

_Static_assert(sizeof(controller_raw_input_t) == 12,
               "controller raw input must stay fixed-size and pointer-free");
_Static_assert(sizeof(controller_snapshot_t) == 12,
               "controller snapshot must stay fixed-size and pointer-free");

static controller_raw_input_t controller_raw_input[2];
static controller_snapshot_t controller_snapshot[2];
static volatile uint32_t controller_raw_active_index = 0;
static volatile uint32_t controller_raw_generation = 0;
static volatile uint32_t controller_snapshot_active_index = 0;
static volatile uint32_t controller_snapshot_generation = 0;
static volatile uint32_t controller_snapshot_match_count = 0;
static volatile uint32_t controller_snapshot_mismatch_count = 0;
static volatile uint32_t controller_snapshot_invalid_count = 0;

static uint32_t controller_state_pack(const uint8_t state[4]) {
    return (uint32_t)state[0]
        | ((uint32_t)state[1] << 8)
        | ((uint32_t)state[2] << 16)
        | ((uint32_t)state[3] << 24);
}

static void controller_state_unpack(uint32_t word, uint8_t out_state[4]) {
    out_state[0] = (uint8_t)word;
    out_state[1] = (uint8_t)(word >> 8);
    out_state[2] = (uint8_t)(word >> 16);
    out_state[3] = (uint8_t)(word >> 24);
}

void controller_state_publish_raw(const uint8_t state[4]) {
    const uint32_t current = __atomic_load_n(&controller_raw_active_index, __ATOMIC_RELAXED);
    const uint32_t next = current ^ 1u;
    const uint32_t generation =
        __atomic_load_n(&controller_raw_generation, __ATOMIC_RELAXED) + 1u;

    controller_raw_input[next].generation = generation;
    controller_raw_input[next].state.word = controller_state_pack(state);
    controller_raw_input[next].validation_pattern =
        generation ^ CONTROLLER_STATE_RAW_PATTERN;

    __atomic_thread_fence(__ATOMIC_RELEASE);
    __atomic_store_n(&controller_raw_active_index, next, __ATOMIC_RELEASE);
    __atomic_store_n(&controller_raw_generation, generation, __ATOMIC_RELEASE);
}

static bool controller_state_read_raw_latest(controller_raw_input_t *out) {
    const uint32_t generation_before =
        __atomic_load_n(&controller_raw_generation, __ATOMIC_ACQUIRE);
    const uint32_t index = __atomic_load_n(&controller_raw_active_index, __ATOMIC_ACQUIRE);
    if (index >= 2u) {
        return false;
    }

    const controller_raw_input_t raw = controller_raw_input[index];
    const uint32_t generation_after =
        __atomic_load_n(&controller_raw_generation, __ATOMIC_ACQUIRE);
    if (generation_before != generation_after) {
        return false;
    }

    if (raw.generation == 0u ||
        raw.generation > generation_after ||
        raw.validation_pattern != (raw.generation ^ CONTROLLER_STATE_RAW_PATTERN)) {
        return false;
    }

    *out = raw;
    return true;
}

static uint32_t __attribute__((noinline, used))
controller_state_prepare(uint32_t raw_state_word) {
    return raw_state_word;
}

void controller_state_init(void) {
    const uint8_t neutral[4] = {0x00, 0x00, 0x00, 0x00};
    for (uint32_t i = 0; i < 2u; ++i) {
        controller_raw_input[i].generation = 1u;
        controller_raw_input[i].state.word = 0u;
        controller_raw_input[i].validation_pattern =
            1u ^ CONTROLLER_STATE_RAW_PATTERN;
    }

    for (uint32_t i = 0; i < 2u; ++i) {
        controller_snapshot[i].generation = 1u;
        controller_snapshot[i].state.word = 0u;
        controller_snapshot[i].validation_pattern =
            1u ^ CONTROLLER_STATE_SNAPSHOT_PATTERN;
    }

    __atomic_thread_fence(__ATOMIC_RELEASE);
    __atomic_store_n(&controller_raw_active_index, 0u, __ATOMIC_RELEASE);
    __atomic_store_n(&controller_raw_generation, 1u, __ATOMIC_RELEASE);
    __atomic_store_n(&controller_snapshot_active_index, 0u, __ATOMIC_RELEASE);
    __atomic_store_n(&controller_snapshot_generation, 1u, __ATOMIC_RELEASE);

    controller_state_publish_raw(neutral);
}

void __no_inline_not_in_flash_func(controller_state_read_snapshot)(uint8_t out_state[4]) {
    const uint32_t index = __atomic_load_n(&controller_snapshot_active_index,
                                           __ATOMIC_ACQUIRE);
    if (index >= 2u) {
        out_state[0] = 0x00;
        out_state[1] = 0x00;
        out_state[2] = 0x00;
        out_state[3] = 0x00;
        return;
    }

    const uint32_t word = __atomic_load_n(&controller_snapshot[index].state.word,
                                          __ATOMIC_ACQUIRE);
    controller_state_unpack(word, out_state);
}

void controller_state_service(void) {
    static uint32_t generation = 0;
    static uint32_t last_raw_generation = 0;
    static bool initialized = false;

    if (!initialized) {
        generation = __atomic_load_n(&controller_snapshot_generation, __ATOMIC_ACQUIRE);
        last_raw_generation = __atomic_load_n(&controller_raw_generation, __ATOMIC_ACQUIRE);
        initialized = true;
    }

    const uint32_t raw_generation =
        __atomic_load_n(&controller_raw_generation, __ATOMIC_ACQUIRE);
    if (raw_generation == last_raw_generation) {
        return;
    }

    controller_raw_input_t raw;
    if (!controller_state_read_raw_latest(&raw)) {
        return;
    }

    const uint32_t current =
        __atomic_load_n(&controller_snapshot_active_index, __ATOMIC_RELAXED);
    const uint32_t next = current ^ 1u;
    const uint32_t published_generation = generation + 1u;
    const uint32_t prepared_state = controller_state_prepare(raw.state.word);

    controller_snapshot[next].generation = published_generation;
    controller_snapshot[next].state.word = prepared_state;
    controller_snapshot[next].validation_pattern =
        published_generation ^ CONTROLLER_STATE_SNAPSHOT_PATTERN;

    __atomic_thread_fence(__ATOMIC_RELEASE);
    __atomic_store_n(&controller_snapshot_active_index, next, __ATOMIC_RELEASE);
    __atomic_store_n(&controller_snapshot_generation, published_generation, __ATOMIC_RELEASE);

    generation = published_generation;
    last_raw_generation = raw.generation;
}

void controller_state_poll_validation(void) {
    const uint32_t generation_before =
        __atomic_load_n(&controller_snapshot_generation, __ATOMIC_ACQUIRE);
    const uint32_t index =
        __atomic_load_n(&controller_snapshot_active_index, __ATOMIC_ACQUIRE);
    if (index >= 2u) {
        controller_snapshot_invalid_count++;
        return;
    }

    const controller_snapshot_t snapshot = controller_snapshot[index];
    const uint32_t generation_after =
        __atomic_load_n(&controller_snapshot_generation, __ATOMIC_ACQUIRE);
    if (generation_before != generation_after) {
        return;
    }

    if (snapshot.generation != 0 &&
        snapshot.generation <= generation_after &&
        snapshot.validation_pattern ==
            (snapshot.generation ^ CONTROLLER_STATE_SNAPSHOT_PATTERN)) {
        controller_raw_input_t raw;
        if (controller_state_read_raw_latest(&raw) &&
            snapshot.state.word == controller_state_prepare(raw.state.word)) {
            controller_snapshot_match_count++;
        } else {
            controller_snapshot_mismatch_count++;
        }
    } else {
        controller_snapshot_invalid_count++;
    }
}
