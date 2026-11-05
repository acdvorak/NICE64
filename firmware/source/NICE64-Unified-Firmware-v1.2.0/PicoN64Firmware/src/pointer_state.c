#include "pointer_state.h"

#include <string.h>

#include "pico/platform/compiler.h"
#include "pico/platform/sections.h"

#define POINTER_RAW_PATTERN 0x50545257u
#define POINTER_SNAPSHOT_PATTERN 0x5054534Eu
#define POINTER_RETIRED_SESSIONS 4u

typedef struct {
    uint32_t generation;
    uint32_t total_x;
    uint32_t total_y;
    uint8_t buttons;
    uint8_t reserved[3];
    uint32_t validation_pattern;
} pointer_value_t;

static pointer_value_t raw_input[2];
static pointer_value_t snapshots[2];
static volatile uint32_t raw_active_index;
static volatile uint32_t raw_generation;
static volatile uint32_t snapshot_active_index;
static volatile uint32_t snapshot_generation;
static uint32_t provider_total_x;
static uint32_t provider_total_y;
static uint32_t accepted_session;
static uint32_t accepted_sequence;
static int32_t accepted_host_x;
static int32_t accepted_host_y;
static uint32_t retired_sessions[POINTER_RETIRED_SESSIONS];
static uint32_t retired_session_index;
static uint32_t consumed_x;
static uint32_t consumed_y;
static volatile uint32_t validation_matches;
static volatile uint32_t validation_mismatches;

static bool sequence_newer(uint32_t candidate, uint32_t current)
{
    const uint32_t distance = candidate - current;
    return distance != 0u && distance < 0x80000000u;
}

static bool session_is_retired(uint32_t session_id)
{
    for (unsigned i = 0; i < POINTER_RETIRED_SESSIONS; ++i) {
        if (retired_sessions[i] == session_id)
            return true;
    }
    return false;
}

static void retire_session(uint32_t session_id)
{
    if (session_id == 0u)
        return;
    retired_sessions[retired_session_index] = session_id;
    retired_session_index =
        (retired_session_index + 1u) % POINTER_RETIRED_SESSIONS;
}

static bool read_value(const pointer_value_t values[2],
                       const volatile uint32_t *active_index,
                       const volatile uint32_t *generation,
                       uint32_t pattern, pointer_value_t *out)
{
    const uint32_t before = __atomic_load_n(generation, __ATOMIC_ACQUIRE);
    const uint32_t index = __atomic_load_n(active_index, __ATOMIC_ACQUIRE);
    if (index >= 2u)
        return false;
    const pointer_value_t value = values[index];
    const uint32_t after = __atomic_load_n(generation, __ATOMIC_ACQUIRE);
    if (before != after || value.generation == 0u || value.generation > after ||
        value.validation_pattern != (value.generation ^ pattern))
        return false;
    *out = value;
    return true;
}

static void publish_raw(uint8_t buttons)
{
    const uint32_t current =
        __atomic_load_n(&raw_active_index, __ATOMIC_RELAXED);
    const uint32_t next = current ^ 1u;
    const uint32_t generation =
        __atomic_load_n(&raw_generation, __ATOMIC_RELAXED) + 1u;
    const pointer_value_t value = {
        .generation = generation,
        .total_x = provider_total_x,
        .total_y = provider_total_y,
        .buttons = (uint8_t)(buttons & (POINTER_BUTTON_LEFT | POINTER_BUTTON_RIGHT)),
        .validation_pattern = generation ^ POINTER_RAW_PATTERN,
    };
    raw_input[next] = value;
    __atomic_thread_fence(__ATOMIC_RELEASE);
    __atomic_store_n(&raw_active_index, next, __ATOMIC_RELEASE);
    __atomic_store_n(&raw_generation, generation, __ATOMIC_RELEASE);
}

void pointer_state_init(void)
{
    memset(raw_input, 0, sizeof(raw_input));
    memset(snapshots, 0, sizeof(snapshots));
    provider_total_x = provider_total_y = 0u;
    consumed_x = consumed_y = 0u;
    accepted_session = accepted_sequence = 0u;
    accepted_host_x = accepted_host_y = 0;
    memset(retired_sessions, 0, sizeof(retired_sessions));
    retired_session_index = 0u;
    for (unsigned i = 0; i < 2u; ++i) {
        raw_input[i].generation = 1u;
        raw_input[i].validation_pattern = 1u ^ POINTER_RAW_PATTERN;
        snapshots[i].generation = 1u;
        snapshots[i].validation_pattern = 1u ^ POINTER_SNAPSHOT_PATTERN;
    }
    __atomic_thread_fence(__ATOMIC_RELEASE);
    __atomic_store_n(&raw_active_index, 0u, __ATOMIC_RELEASE);
    __atomic_store_n(&raw_generation, 1u, __ATOMIC_RELEASE);
    __atomic_store_n(&snapshot_active_index, 0u, __ATOMIC_RELEASE);
    __atomic_store_n(&snapshot_generation, 1u, __ATOMIC_RELEASE);
}

void pointer_state_publish_delta(uint8_t buttons, int32_t dx, int32_t dy)
{
    provider_total_x += (uint32_t)dx;
    provider_total_y += (uint32_t)dy;
    publish_raw(buttons);
}

bool pointer_state_accept_cumulative(uint32_t session_id, uint32_t sequence,
                                     uint8_t buttons, int32_t total_x,
                                     int32_t total_y)
{
    if (session_id == 0u || session_is_retired(session_id))
        return false;
    if (session_id != accepted_session) {
        retire_session(accepted_session);
        accepted_session = session_id;
        accepted_sequence = sequence;
        accepted_host_x = 0;
        accepted_host_y = 0;
    } else if (!sequence_newer(sequence, accepted_sequence)) {
        return false;
    }

    provider_total_x += (uint32_t)total_x - (uint32_t)accepted_host_x;
    provider_total_y += (uint32_t)total_y - (uint32_t)accepted_host_y;
    accepted_host_x = total_x;
    accepted_host_y = total_y;
    accepted_sequence = sequence;
    publish_raw(buttons);
    return true;
}

void pointer_state_service(void)
{
    static uint32_t last_raw_generation;
    const uint32_t generation =
        __atomic_load_n(&raw_generation, __ATOMIC_ACQUIRE);
    if (generation == last_raw_generation)
        return;
    pointer_value_t raw;
    if (!read_value(raw_input, &raw_active_index, &raw_generation,
                    POINTER_RAW_PATTERN, &raw))
        return;

    const uint32_t current =
        __atomic_load_n(&snapshot_active_index, __ATOMIC_RELAXED);
    const uint32_t next = current ^ 1u;
    const uint32_t published =
        __atomic_load_n(&snapshot_generation, __ATOMIC_RELAXED) + 1u;
    pointer_value_t value = raw;
    value.generation = published;
    value.validation_pattern = published ^ POINTER_SNAPSHOT_PATTERN;
    snapshots[next] = value;
    __atomic_thread_fence(__ATOMIC_RELEASE);
    __atomic_store_n(&snapshot_active_index, next, __ATOMIC_RELEASE);
    __atomic_store_n(&snapshot_generation, published, __ATOMIC_RELEASE);
    last_raw_generation = raw.generation;
}

static int8_t consume_axis(uint32_t total, uint32_t *consumed)
{
    const int32_t remaining = (int32_t)(total - *consumed);
    int32_t portion = remaining;
    if (portion > 127) portion = 127;
    if (portion < -128) portion = -128;
    *consumed += (uint32_t)portion;
    return (int8_t)portion;
}

void __no_inline_not_in_flash_func(pointer_state_consume_report)(uint8_t out[4])
{
    pointer_value_t value;
    if (!read_value(snapshots, &snapshot_active_index, &snapshot_generation,
                    POINTER_SNAPSHOT_PATTERN, &value)) {
        memset(out, 0, 4u);
        return;
    }
    out[0] = 0u;
    if ((value.buttons & POINTER_BUTTON_LEFT) != 0u) out[0] |= 0x80u;
    if ((value.buttons & POINTER_BUTTON_RIGHT) != 0u) out[0] |= 0x40u;
    out[1] = 0u;
    out[2] = (uint8_t)consume_axis(value.total_x, &consumed_x);
    out[3] = (uint8_t)consume_axis(value.total_y, &consumed_y);
}

void pointer_state_poll_validation(void)
{
    pointer_value_t raw;
    pointer_value_t snapshot;
    if (read_value(raw_input, &raw_active_index, &raw_generation,
                   POINTER_RAW_PATTERN, &raw) &&
        read_value(snapshots, &snapshot_active_index, &snapshot_generation,
                   POINTER_SNAPSHOT_PATTERN, &snapshot) &&
        raw.total_x == snapshot.total_x && raw.total_y == snapshot.total_y &&
        raw.buttons == snapshot.buttons)
        validation_matches++;
    else
        validation_mismatches++;
}
