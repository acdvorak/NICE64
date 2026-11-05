#include "keyboard_state.h"

#include <string.h>

#include "pico/platform/compiler.h"
#include "pico/platform/sections.h"

#define KEYBOARD_STATE_WORDS (((unsigned)NICE64_KEY_COUNT + 31u) / 32u)
#define KEYBOARD_RAW_PATTERN 0x4B425257u
#define KEYBOARD_SNAPSHOT_PATTERN 0x4B42534Eu
#define KEYBOARD_RETIRED_SESSIONS 4u

typedef struct {
    uint32_t generation;
    uint32_t held[KEYBOARD_STATE_WORDS];
    uint32_t validation_pattern;
} keyboard_raw_t;

typedef struct {
    uint32_t generation;
    uint8_t report[KEYBOARD_STATE_REPORT_SIZE];
    uint8_t reserved;
    uint32_t validation_pattern;
} keyboard_snapshot_t;

static keyboard_raw_t raw_input[2];
static keyboard_snapshot_t snapshots[2];
static volatile uint32_t raw_active_index;
static volatile uint32_t raw_generation;
static volatile uint32_t snapshot_active_index;
static volatile uint32_t snapshot_generation;
static uint32_t accepted_session;
static uint32_t accepted_sequence;
static uint32_t retired_sessions[KEYBOARD_RETIRED_SESSIONS];
static uint32_t retired_session_index;
static volatile uint32_t validation_matches;
static volatile uint32_t validation_mismatches;

static void set_held(uint32_t held[KEYBOARD_STATE_WORDS],
                     nice64_logical_key_t key)
{
    const unsigned value = (unsigned)key;
    held[value / 32u] |= 1u << (value % 32u);
}

static bool is_held(const uint32_t held[KEYBOARD_STATE_WORDS],
                    nice64_logical_key_t key)
{
    const unsigned value = (unsigned)key;
    return (held[value / 32u] & (1u << (value % 32u))) != 0u;
}

static bool sequence_newer(uint32_t candidate, uint32_t current)
{
    const uint32_t distance = candidate - current;
    return distance != 0u && distance < 0x80000000u;
}

static bool session_is_retired(uint32_t session_id)
{
    for (unsigned i = 0; i < KEYBOARD_RETIRED_SESSIONS; ++i) {
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
        (retired_session_index + 1u) % KEYBOARD_RETIRED_SESSIONS;
}

static bool read_raw(keyboard_raw_t *out)
{
    const uint32_t generation_before =
        __atomic_load_n(&raw_generation, __ATOMIC_ACQUIRE);
    const uint32_t index = __atomic_load_n(&raw_active_index, __ATOMIC_ACQUIRE);
    if (index >= 2u)
        return false;
    const keyboard_raw_t value = raw_input[index];
    const uint32_t generation_after =
        __atomic_load_n(&raw_generation, __ATOMIC_ACQUIRE);
    if (generation_before != generation_after || value.generation == 0u ||
        value.generation > generation_after ||
        value.validation_pattern != (value.generation ^ KEYBOARD_RAW_PATTERN))
        return false;
    *out = value;
    return true;
}

void keyboard_state_init(void)
{
    memset(raw_input, 0, sizeof(raw_input));
    memset(snapshots, 0, sizeof(snapshots));
    memset(retired_sessions, 0, sizeof(retired_sessions));
    accepted_session = accepted_sequence = 0u;
    retired_session_index = 0u;
    for (unsigned i = 0; i < 2u; ++i) {
        raw_input[i].generation = 1u;
        raw_input[i].validation_pattern = 1u ^ KEYBOARD_RAW_PATTERN;
        snapshots[i].generation = 1u;
        snapshots[i].validation_pattern = 1u ^ KEYBOARD_SNAPSHOT_PATTERN;
    }
    __atomic_thread_fence(__ATOMIC_RELEASE);
    __atomic_store_n(&raw_active_index, 0u, __ATOMIC_RELEASE);
    __atomic_store_n(&raw_generation, 1u, __ATOMIC_RELEASE);
    __atomic_store_n(&snapshot_active_index, 0u, __ATOMIC_RELEASE);
    __atomic_store_n(&snapshot_generation, 1u, __ATOMIC_RELEASE);
}

bool keyboard_state_publish_held(const nice64_logical_key_t *keys, size_t count)
{
    if (count != 0u && keys == NULL)
        return false;

    const uint32_t current =
        __atomic_load_n(&raw_active_index, __ATOMIC_RELAXED);
    const uint32_t next = current ^ 1u;
    const uint32_t generation =
        __atomic_load_n(&raw_generation, __ATOMIC_RELAXED) + 1u;
    keyboard_raw_t value = { .generation = generation };

    for (size_t i = 0; i < count; ++i) {
        if (keys[i] <= NICE64_KEY_NONE || keys[i] >= NICE64_KEY_COUNT)
            return false;
        set_held(value.held, keys[i]);
    }
    value.validation_pattern = generation ^ KEYBOARD_RAW_PATTERN;
    raw_input[next] = value;
    __atomic_thread_fence(__ATOMIC_RELEASE);
    __atomic_store_n(&raw_active_index, next, __ATOMIC_RELEASE);
    __atomic_store_n(&raw_generation, generation, __ATOMIC_RELEASE);
    return true;
}

bool keyboard_state_accept_held(uint32_t session_id, uint32_t sequence,
                                const nice64_logical_key_t *keys, size_t count)
{
    if ((count != 0u && keys == NULL) || session_id == 0u ||
        session_is_retired(session_id))
        return false;
    for (size_t i = 0; i < count; ++i) {
        if (keys[i] <= NICE64_KEY_NONE || keys[i] >= NICE64_KEY_COUNT)
            return false;
    }
    if (session_id != accepted_session) {
        retire_session(accepted_session);
        accepted_session = session_id;
        accepted_sequence = sequence;
    } else if (!sequence_newer(sequence, accepted_sequence)) {
        return false;
    }
    if (!keyboard_state_publish_held(keys, count))
        return false;
    accepted_sequence = sequence;
    return true;
}

static void prepare_report(const keyboard_raw_t *raw, uint8_t report[7])
{
    uint16_t matrix[3] = {0};
    unsigned count = 0;
    uint8_t status = 0;

    memset(report, 0, 7u);
    for (unsigned key_value = 1u; key_value < (unsigned)NICE64_KEY_COUNT;
         ++key_value) {
        const nice64_logical_key_t key = (nice64_logical_key_t)key_value;
        if (!is_held(raw->held, key))
            continue;
        if (randnet_keymap_is_home(key)) {
            status |= KEYBOARD_STATE_STATUS_HOME;
            continue;
        }
        const uint16_t value = randnet_keymap_matrix_value(key);
        if (value == 0u)
            continue;
        if (count < 3u)
            matrix[count] = value;
        ++count;
    }

    if (count > 3u) {
        matrix[0] = matrix[1] = matrix[2] = 0u;
        status |= KEYBOARD_STATE_STATUS_TOO_MANY_KEYS;
    }
    for (unsigned i = 0; i < 3u; ++i) {
        report[i * 2u] = (uint8_t)(matrix[i] >> 8);
        report[i * 2u + 1u] = (uint8_t)matrix[i];
    }
    report[6] = status;
}

void keyboard_state_service(void)
{
    static uint32_t last_raw_generation;
    const uint32_t generation =
        __atomic_load_n(&raw_generation, __ATOMIC_ACQUIRE);
    if (generation == last_raw_generation)
        return;

    keyboard_raw_t raw;
    if (!read_raw(&raw))
        return;

    const uint32_t current =
        __atomic_load_n(&snapshot_active_index, __ATOMIC_RELAXED);
    const uint32_t next = current ^ 1u;
    const uint32_t published =
        __atomic_load_n(&snapshot_generation, __ATOMIC_RELAXED) + 1u;
    keyboard_snapshot_t value = { .generation = published };
    prepare_report(&raw, value.report);
    value.validation_pattern = published ^ KEYBOARD_SNAPSHOT_PATTERN;
    snapshots[next] = value;
    __atomic_thread_fence(__ATOMIC_RELEASE);
    __atomic_store_n(&snapshot_active_index, next, __ATOMIC_RELEASE);
    __atomic_store_n(&snapshot_generation, published, __ATOMIC_RELEASE);
    last_raw_generation = raw.generation;
}

void __no_inline_not_in_flash_func(keyboard_state_read_report)(uint8_t out[7])
{
    for (unsigned attempt = 0; attempt < 2u; ++attempt) {
        const uint32_t generation_before =
            __atomic_load_n(&snapshot_generation, __ATOMIC_ACQUIRE);
        const uint32_t index =
            __atomic_load_n(&snapshot_active_index, __ATOMIC_ACQUIRE);
        if (index >= 2u)
            break;
        const keyboard_snapshot_t value = snapshots[index];
        const uint32_t generation_after =
            __atomic_load_n(&snapshot_generation, __ATOMIC_ACQUIRE);
        if (generation_before == generation_after && value.generation != 0u &&
            value.generation <= generation_after &&
            value.validation_pattern ==
                (value.generation ^ KEYBOARD_SNAPSHOT_PATTERN)) {
            memcpy(out, value.report, 7u);
            return;
        }
    }
    memset(out, 0, 7u);
}

void keyboard_state_poll_validation(void)
{
    keyboard_raw_t raw;
    uint8_t expected[7];
    uint8_t actual[7];
    if (!read_raw(&raw)) {
        validation_mismatches++;
        return;
    }
    prepare_report(&raw, expected);
    keyboard_state_read_report(actual);
    if (memcmp(expected, actual, sizeof(expected)) == 0)
        validation_matches++;
    else
        validation_mismatches++;
}
