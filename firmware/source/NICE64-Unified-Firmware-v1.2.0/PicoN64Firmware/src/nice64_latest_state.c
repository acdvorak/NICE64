#include "nice64_latest_state.h"

#include <stdbool.h>
#include <string.h>

#include "controller_state.h"

typedef struct {
    bool initialized;
    uint32_t session_id;
    uint32_t sequence;
    uint32_t accepted;
    uint32_t stale;
    uint32_t retired_sessions[NICE64_LATEST_STATE_RETIRED_SESSIONS];
    uint8_t retired_count;
    uint8_t retired_next;
} nice64_latest_state_t;

static nice64_latest_state_t latest;

static bool sequence_is_after(uint32_t candidate, uint32_t reference) {
    const uint32_t distance = candidate - reference;
    return distance != 0u && distance < 0x80000000u;
}

static bool session_is_retired(uint32_t session_id) {
    for (uint8_t index = 0u; index < latest.retired_count; ++index) {
        if (latest.retired_sessions[index] == session_id) return true;
    }
    return false;
}

static void retire_active_session(void) {
    if (!latest.initialized) return;
    latest.retired_sessions[latest.retired_next] = latest.session_id;
    latest.retired_next = (uint8_t)(
        (latest.retired_next + 1u) % NICE64_LATEST_STATE_RETIRED_SESSIONS);
    if (latest.retired_count < NICE64_LATEST_STATE_RETIRED_SESSIONS) {
        latest.retired_count++;
    }
}

void nice64_latest_state_init(void) {
    memset(&latest, 0, sizeof(latest));
}

void nice64_latest_state_reset(void) {
    latest.initialized = false;
    latest.session_id = 0u;
    latest.sequence = 0u;
    memset(latest.retired_sessions, 0, sizeof(latest.retired_sessions));
    latest.retired_count = 0u;
    latest.retired_next = 0u;
}

nice64_latest_state_result_t nice64_latest_state_receive_controller(
    uint32_t session_id,
    uint32_t sequence,
    const uint8_t *payload,
    size_t payload_length) {
    if (session_id == 0u || payload == NULL ||
        payload_length != NICE64_LATEST_STATE_CONTROLLER_BYTES) {
        return NICE64_LATEST_STATE_INVALID;
    }

    if (latest.initialized) {
        if (latest.session_id == session_id) {
            if (!sequence_is_after(sequence, latest.sequence)) {
                latest.stale++;
                return NICE64_LATEST_STATE_STALE;
            }
        } else {
            if (session_is_retired(session_id)) {
                latest.stale++;
                return NICE64_LATEST_STATE_STALE;
            }
            retire_active_session();
        }
    }

    controller_state_publish_raw(payload);
    latest.initialized = true;
    latest.session_id = session_id;
    latest.sequence = sequence;
    latest.accepted++;
    return NICE64_LATEST_STATE_ACCEPTED;
}

uint32_t nice64_latest_state_accepted_count(void) {
    return latest.accepted;
}

uint32_t nice64_latest_state_stale_count(void) {
    return latest.stale;
}

size_t nice64_latest_state_static_bytes(void) {
    return sizeof(latest);
}
