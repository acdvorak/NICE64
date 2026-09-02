#include "nice64_rudp_engine.h"

#include <limits.h>
#include <string.h>

#define RUDPC_HALF_RANGE 0x80000000u

typedef struct {
    uint8_t payload[NICE64_RUDPC_PAYLOAD_MAX];
    uint32_t sequence;
    uint32_t first_sent_ms;
    uint32_t last_sent_ms;
    uint16_t length;
    uint8_t retransmissions;
    bool active;
    bool sent;
    bool selectively_acknowledged;
} rudpc_tx_slot_t;

typedef struct {
    uint8_t payload[NICE64_RUDPC_PAYLOAD_MAX];
    uint32_t sequence;
    uint16_t length;
    bool active;
} rudpc_rx_slot_t;

typedef struct {
    rudpc_tx_slot_t tx[NICE64_RUDPC_WINDOW_SIZE];
    rudpc_rx_slot_t rx[NICE64_RUDPC_WINDOW_SIZE];
    uint32_t session_id;
    uint32_t tx_base;
    uint32_t tx_next;
    uint32_t rx_next_expected;
    uint32_t retransmission_timeout_ms;
    bool tx_failed;
    bool failure_reported;
    bool consumer_paused;
    nice64_rudpc_diagnostics_t diagnostics;
} rudpc_engine_state_t;

static rudpc_engine_state_t engine;

static uint32_t saturating_add(uint32_t left, uint32_t right) {
    return UINT32_MAX - left < right ? UINT32_MAX : left + right;
}

static uint32_t forward_distance(uint32_t from, uint32_t to) {
    return to - from;
}

static rudpc_tx_slot_t *tx_slot_for(uint32_t sequence) {
    return &engine.tx[sequence % NICE64_RUDPC_WINDOW_SIZE];
}

static rudpc_rx_slot_t *rx_slot_for(uint32_t sequence) {
    return &engine.rx[sequence % NICE64_RUDPC_WINDOW_SIZE];
}

static void clear_tx_slot(uint32_t sequence, uint32_t now_ms) {
    rudpc_tx_slot_t *slot = tx_slot_for(sequence);
    if (!slot->active || slot->sequence != sequence) return;
    if (slot->sent && slot->retransmissions == 0u) {
        const uint32_t sample = now_ms - slot->first_sent_ms;
        engine.diagnostics.rtt_sample_count = saturating_add(
            engine.diagnostics.rtt_sample_count, 1u);
        engine.diagnostics.rtt_total_ms = saturating_add(
            engine.diagnostics.rtt_total_ms, sample);
        if (sample > engine.diagnostics.rtt_max_ms) {
            engine.diagnostics.rtt_max_ms = sample;
        }
    }
    memset(slot, 0, sizeof(*slot));
}

void nice64_rudpc_engine_init(void) {
    memset(&engine, 0, sizeof(engine));
    engine.retransmission_timeout_ms = 20u;
}

void nice64_rudpc_engine_reset(uint32_t session_id,
                               uint32_t tx_initial_sequence,
                               uint32_t rx_initial_sequence,
                               uint32_t retransmission_timeout_ms) {
    memset(engine.tx, 0, sizeof(engine.tx));
    memset(engine.rx, 0, sizeof(engine.rx));
    engine.session_id = session_id;
    engine.tx_base = tx_initial_sequence;
    engine.tx_next = tx_initial_sequence;
    engine.rx_next_expected = rx_initial_sequence;
    engine.retransmission_timeout_ms =
        retransmission_timeout_ms == 0u ? 20u : retransmission_timeout_ms;
    engine.tx_failed = false;
    engine.failure_reported = false;
    engine.consumer_paused = false;
    engine.diagnostics.session_resets = saturating_add(
        engine.diagnostics.session_resets, 1u);
}

bool nice64_rudpc_tx_full(void) {
    return forward_distance(engine.tx_base, engine.tx_next) >=
           NICE64_RUDPC_WINDOW_SIZE;
}

bool nice64_rudpc_tx_empty(void) {
    return engine.tx_base == engine.tx_next;
}

bool nice64_rudpc_tx_failed(void) {
    return engine.tx_failed;
}

bool nice64_rudpc_tx_try_enqueue(const uint8_t *payload,
                                 uint16_t length,
                                 uint32_t *sequence) {
    if (engine.tx_failed || payload == NULL || length > NICE64_RUDPC_PAYLOAD_MAX) {
        return false;
    }
    if (nice64_rudpc_tx_full()) {
        engine.diagnostics.tx_window_full = saturating_add(
            engine.diagnostics.tx_window_full, 1u);
        return false;
    }
    rudpc_tx_slot_t *slot = tx_slot_for(engine.tx_next);
    if (slot->active) {
        engine.diagnostics.tx_window_full = saturating_add(
            engine.diagnostics.tx_window_full, 1u);
        return false;
    }
    memset(slot, 0, sizeof(*slot));
    slot->active = true;
    slot->sequence = engine.tx_next;
    slot->length = length;
    if (length != 0u) memcpy(slot->payload, payload, length);
    if (sequence != NULL) *sequence = engine.tx_next;
    engine.tx_next++;
    return true;
}

void nice64_rudpc_tx_poll(uint32_t now_ms,
                          nice64_rudpc_send_fn send,
                          void *context) {
    if (engine.tx_failed || send == NULL) return;
    const uint32_t outstanding = forward_distance(engine.tx_base, engine.tx_next);
    for (uint32_t distance = 0u; distance < outstanding; ++distance) {
        const uint32_t sequence = engine.tx_base + distance;
        rudpc_tx_slot_t *slot = tx_slot_for(sequence);
        if (!slot->active || slot->sequence != sequence ||
            slot->selectively_acknowledged) {
            continue;
        }
        const bool retransmission = slot->sent;
        if (retransmission &&
            (uint32_t)(now_ms - slot->last_sent_ms) <
                engine.retransmission_timeout_ms) {
            continue;
        }
        if (retransmission &&
            slot->retransmissions >= NICE64_RUDPC_MAX_RETRANSMISSIONS) {
            engine.tx_failed = true;
            if (!engine.failure_reported) {
                engine.diagnostics.retry_exhaustion = saturating_add(
                    engine.diagnostics.retry_exhaustion, 1u);
                engine.failure_reported = true;
            }
            return;
        }
        if (retransmission) {
            slot->retransmissions++;
            engine.diagnostics.retransmissions = saturating_add(
                engine.diagnostics.retransmissions, 1u);
            if (slot->retransmissions > engine.diagnostics.maximum_retry_count) {
                engine.diagnostics.maximum_retry_count = slot->retransmissions;
            }
        }
        if (!slot->sent) slot->first_sent_ms = now_ms;
        slot->sent = true;
        slot->last_sent_ms = now_ms;
        engine.diagnostics.tx_datagrams = saturating_add(
            engine.diagnostics.tx_datagrams, 1u);
        engine.diagnostics.tx_payload_bytes = saturating_add(
            engine.diagnostics.tx_payload_bytes, slot->length);
        (void)send(context, sequence, slot->payload, slot->length,
                   retransmission, slot->retransmissions);
    }
}

void nice64_rudpc_tx_apply_ack(uint32_t ack_base,
                               uint32_t ack_bitmap,
                               uint32_t now_ms) {
    engine.diagnostics.acks_received = saturating_add(
        engine.diagnostics.acks_received, 1u);
    const uint32_t outstanding = forward_distance(engine.tx_base, engine.tx_next);
    if (outstanding == 0u) return;

    const uint32_t sequence_before_base = engine.tx_base - 1u;
    const uint32_t cumulative_count = forward_distance(sequence_before_base, ack_base);
    if (cumulative_count <= outstanding) {
        for (uint32_t distance = 0u; distance < cumulative_count; ++distance) {
            clear_tx_slot(engine.tx_base + distance, now_ms);
        }
        engine.tx_base += cumulative_count;
    } else if (ack_base != sequence_before_base) {
        return;
    }

    for (uint32_t bit = 0u; bit < 32u; ++bit) {
        if ((ack_bitmap & (1u << bit)) == 0u) continue;
        const uint32_t sequence = ack_base + 1u + bit;
        const uint32_t distance = forward_distance(engine.tx_base, sequence);
        const uint32_t remaining = forward_distance(engine.tx_base, engine.tx_next);
        if (distance >= remaining) continue;
        rudpc_tx_slot_t *slot = tx_slot_for(sequence);
        if (slot->active && slot->sequence == sequence) {
            slot->selectively_acknowledged = true;
        }
    }
}

uint32_t nice64_rudpc_rx_drain(nice64_rudpc_deliver_fn deliver,
                               void *context) {
    if (engine.consumer_paused || deliver == NULL) return 0u;
    uint32_t delivered = 0u;
    while (true) {
        rudpc_rx_slot_t *slot = rx_slot_for(engine.rx_next_expected);
        if (!slot->active || slot->sequence != engine.rx_next_expected) break;
        if (!deliver(context, slot->sequence, slot->payload, slot->length)) break;
        slot->active = false;
        slot->length = 0u;
        engine.rx_next_expected++;
        delivered++;
    }
    return delivered;
}

nice64_rudpc_rx_result_t nice64_rudpc_rx_receive(
    uint32_t session_id,
    uint32_t sequence,
    const uint8_t *payload,
    uint16_t length,
    nice64_rudpc_deliver_fn deliver,
    void *context) {
    engine.diagnostics.rx_datagrams = saturating_add(
        engine.diagnostics.rx_datagrams, 1u);
    if (session_id != engine.session_id) {
        engine.diagnostics.session_mismatch = saturating_add(
            engine.diagnostics.session_mismatch, 1u);
        return NICE64_RUDPC_RX_SESSION_MISMATCH;
    }
    if ((length != 0u && payload == NULL) || length > NICE64_RUDPC_PAYLOAD_MAX) {
        engine.diagnostics.invalid_header = saturating_add(
            engine.diagnostics.invalid_header, 1u);
        return NICE64_RUDPC_RX_INVALID;
    }
    const uint32_t distance = forward_distance(engine.rx_next_expected, sequence);
    if (distance >= RUDPC_HALF_RANGE) {
        engine.diagnostics.old_or_stale_rx = saturating_add(
            engine.diagnostics.old_or_stale_rx, 1u);
        return NICE64_RUDPC_RX_ALREADY_DELIVERED;
    }
    if (distance >= NICE64_RUDPC_WINDOW_SIZE) {
        engine.diagnostics.rx_window_full = saturating_add(
            engine.diagnostics.rx_window_full, 1u);
        return NICE64_RUDPC_RX_OUTSIDE_WINDOW;
    }
    rudpc_rx_slot_t *slot = rx_slot_for(sequence);
    if (slot->active) {
        if (slot->sequence == sequence) {
            engine.diagnostics.duplicate_rx = saturating_add(
                engine.diagnostics.duplicate_rx, 1u);
            return NICE64_RUDPC_RX_DUPLICATE;
        }
        engine.diagnostics.rx_window_full = saturating_add(
            engine.diagnostics.rx_window_full, 1u);
        return NICE64_RUDPC_RX_OUTSIDE_WINDOW;
    }
    slot->active = true;
    slot->sequence = sequence;
    slot->length = length;
    if (length != 0u) memcpy(slot->payload, payload, length);
    engine.diagnostics.rx_payload_bytes = saturating_add(
        engine.diagnostics.rx_payload_bytes, length);
    if (distance != 0u) {
        engine.diagnostics.out_of_order_rx = saturating_add(
            engine.diagnostics.out_of_order_rx, 1u);
    }
    uint32_t buffered = 0u;
    for (uint32_t index = 0u; index < NICE64_RUDPC_WINDOW_SIZE; ++index) {
        if (engine.rx[index].active) buffered++;
    }
    if (buffered == NICE64_RUDPC_WINDOW_SIZE) {
        engine.diagnostics.rx_window_full = saturating_add(
            engine.diagnostics.rx_window_full, 1u);
    }
    (void)nice64_rudpc_rx_drain(deliver, context);
    return NICE64_RUDPC_RX_ACCEPTED;
}

void nice64_rudpc_rx_set_paused(bool paused) {
    engine.consumer_paused = paused;
}

bool nice64_rudpc_rx_paused(void) {
    return engine.consumer_paused;
}

void nice64_rudpc_rx_ack(uint32_t *ack_base, uint32_t *ack_bitmap) {
    if (ack_base == NULL || ack_bitmap == NULL) return;
    *ack_base = engine.rx_next_expected - 1u;
    *ack_bitmap = 0u;
    for (uint32_t bit = 0u; bit < 32u; ++bit) {
        const uint32_t sequence = engine.rx_next_expected + bit;
        rudpc_rx_slot_t *slot = rx_slot_for(sequence);
        if (slot->active && slot->sequence == sequence) {
            *ack_bitmap |= 1u << bit;
        }
    }
    engine.diagnostics.acks_sent = saturating_add(
        engine.diagnostics.acks_sent, 1u);
}

uint32_t nice64_rudpc_tx_base_sequence(void) { return engine.tx_base; }
uint32_t nice64_rudpc_tx_next_sequence(void) { return engine.tx_next; }
uint32_t nice64_rudpc_rx_next_expected(void) { return engine.rx_next_expected; }
uint32_t nice64_rudpc_session_id(void) { return engine.session_id; }
uint32_t nice64_rudpc_retransmission_timeout_ms(void) {
    return engine.retransmission_timeout_ms;
}
const nice64_rudpc_diagnostics_t *nice64_rudpc_diagnostics(void) {
    return &engine.diagnostics;
}
size_t nice64_rudpc_fixed_state_bytes(void) { return sizeof(engine); }
