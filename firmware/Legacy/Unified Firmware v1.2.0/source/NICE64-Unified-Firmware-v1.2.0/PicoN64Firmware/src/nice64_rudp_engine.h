#ifndef NICE64_RUDP_ENGINE_H
#define NICE64_RUDP_ENGINE_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define NICE64_RUDPC_WINDOW_SIZE 8u
#define NICE64_RUDPC_PAYLOAD_MAX 512u
#define NICE64_RUDPC_MAX_RETRANSMISSIONS 4u

typedef struct {
    uint32_t tx_datagrams;
    uint32_t rx_datagrams;
    uint32_t tx_payload_bytes;
    uint32_t rx_payload_bytes;
    uint32_t acks_sent;
    uint32_t acks_received;
    uint32_t retransmissions;
    uint32_t duplicate_rx;
    uint32_t out_of_order_rx;
    uint32_t old_or_stale_rx;
    uint32_t invalid_header;
    uint32_t unsupported_class;
    uint32_t invalid_class;
    uint32_t session_mismatch;
    uint32_t tx_window_full;
    uint32_t rx_window_full;
    uint32_t retry_exhaustion;
    uint32_t session_resets;
    uint32_t maximum_retry_count;
    uint32_t rtt_sample_count;
    uint32_t rtt_total_ms;
    uint32_t rtt_max_ms;
} nice64_rudpc_diagnostics_t;

typedef enum {
    NICE64_RUDPC_RX_ACCEPTED = 0,
    NICE64_RUDPC_RX_DUPLICATE,
    NICE64_RUDPC_RX_ALREADY_DELIVERED,
    NICE64_RUDPC_RX_OUTSIDE_WINDOW,
    NICE64_RUDPC_RX_SESSION_MISMATCH,
    NICE64_RUDPC_RX_INVALID,
} nice64_rudpc_rx_result_t;

typedef bool (*nice64_rudpc_deliver_fn)(
    void *context, uint32_t sequence, const uint8_t *payload, uint16_t length);
typedef bool (*nice64_rudpc_send_fn)(
    void *context, uint32_t sequence, const uint8_t *payload, uint16_t length,
    bool retransmission, uint8_t retransmission_count);

void nice64_rudpc_engine_init(void);
void nice64_rudpc_engine_reset(uint32_t session_id,
                               uint32_t tx_initial_sequence,
                               uint32_t rx_initial_sequence,
                               uint32_t retransmission_timeout_ms);

bool nice64_rudpc_tx_try_enqueue(const uint8_t *payload,
                                 uint16_t length,
                                 uint32_t *sequence);
void nice64_rudpc_tx_poll(uint32_t now_ms,
                          nice64_rudpc_send_fn send,
                          void *context);
void nice64_rudpc_tx_apply_ack(uint32_t ack_base,
                               uint32_t ack_bitmap,
                               uint32_t now_ms);
bool nice64_rudpc_tx_empty(void);
bool nice64_rudpc_tx_full(void);
bool nice64_rudpc_tx_failed(void);
uint32_t nice64_rudpc_tx_base_sequence(void);
uint32_t nice64_rudpc_tx_next_sequence(void);

nice64_rudpc_rx_result_t nice64_rudpc_rx_receive(
    uint32_t session_id,
    uint32_t sequence,
    const uint8_t *payload,
    uint16_t length,
    nice64_rudpc_deliver_fn deliver,
    void *context);
uint32_t nice64_rudpc_rx_drain(nice64_rudpc_deliver_fn deliver,
                               void *context);
void nice64_rudpc_rx_set_paused(bool paused);
bool nice64_rudpc_rx_paused(void);
void nice64_rudpc_rx_ack(uint32_t *ack_base, uint32_t *ack_bitmap);
uint32_t nice64_rudpc_rx_next_expected(void);

uint32_t nice64_rudpc_session_id(void);
uint32_t nice64_rudpc_retransmission_timeout_ms(void);
const nice64_rudpc_diagnostics_t *nice64_rudpc_diagnostics(void);
size_t nice64_rudpc_fixed_state_bytes(void);

#endif
