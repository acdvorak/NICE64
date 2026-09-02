#ifndef NICE64_LATEST_STATE_H
#define NICE64_LATEST_STATE_H

#include <stddef.h>
#include <stdint.h>

#define NICE64_LATEST_STATE_CONTROLLER_BYTES 4u
#define NICE64_LATEST_STATE_RETIRED_SESSIONS 4u

typedef enum {
    NICE64_LATEST_STATE_ACCEPTED = 0,
    NICE64_LATEST_STATE_STALE,
    NICE64_LATEST_STATE_INVALID,
} nice64_latest_state_result_t;

void nice64_latest_state_init(void);
void nice64_latest_state_reset(void);
nice64_latest_state_result_t nice64_latest_state_receive_controller(
    uint32_t session_id,
    uint32_t sequence,
    const uint8_t *payload,
    size_t payload_length);
uint32_t nice64_latest_state_accepted_count(void);
uint32_t nice64_latest_state_stale_count(void);
size_t nice64_latest_state_static_bytes(void);

#endif
