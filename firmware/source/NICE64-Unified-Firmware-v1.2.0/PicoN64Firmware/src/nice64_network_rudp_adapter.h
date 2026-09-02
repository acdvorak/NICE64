#ifndef NICE64_NETWORK_RUDP_ADAPTER_H
#define NICE64_NETWORK_RUDP_ADAPTER_H

#include <stdbool.h>
#include <stdint.h>

#include "nice64_response.h"
#include "nice64_rudp_engine.h"

#define NICE64_NETWORK_RUDP_RTO_MS 40u
#define NICE64_NETWORK_RUDP_PHYSICAL_CAP 2u
#define NICE64_NETWORK_RUDP_MESSAGE_MAX 600u

typedef struct {
    uint32_t network_rudp_sessions;
    uint32_t network_rudp_requests_rx;
    uint32_t network_rudp_responses_tx;
    uint32_t network_rudp_request_bytes;
    uint32_t network_rudp_response_bytes;
    uint32_t network_rudp_application_deliveries;
    uint32_t network_rudp_duplicate_application_deliveries;
    uint32_t network_rudp_invalid_messages;
    uint32_t network_rudp_session_resets;
} nice64_network_rudp_diagnostics_t;

void nice64_network_rudp_adapter_init(void);
bool nice64_network_rudp_begin(uint32_t session_id,
                               uint32_t host_tx_initial_sequence,
                               uint32_t firmware_tx_initial_sequence);
void nice64_network_rudp_end(void);
bool nice64_network_rudp_active(void);
nice64_rudpc_rx_result_t nice64_network_rudp_receive(
    uint32_t session_id,
    uint32_t sequence,
    const uint8_t *payload,
    uint16_t length);
void nice64_network_rudp_poll(uint32_t now_ms,
                              nice64_rudpc_send_fn send,
                              void *context);
const nice64_response_sink_t *nice64_network_rudp_response_sink(void);
const nice64_network_rudp_diagnostics_t *nice64_network_rudp_diagnostics(void);

#endif
