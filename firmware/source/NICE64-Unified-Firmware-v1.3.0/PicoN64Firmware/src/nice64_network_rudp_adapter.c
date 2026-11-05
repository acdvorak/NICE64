#include "nice64_network_rudp_adapter.h"

#include <limits.h>
#include <string.h>

#include "network_pak.h"

typedef struct {
    bool active;
    uint8_t length_bytes[2];
    uint8_t length_count;
    uint16_t expected_length;
    uint16_t received_length;
    char receive_message[NICE64_NETWORK_RUDP_MESSAGE_MAX + 1u];
    char response_line[NICE64_NETWORK_RUDP_MESSAGE_MAX + 1u];
    uint16_t response_length;
    uint8_t pending_frame[NICE64_NETWORK_RUDP_MESSAGE_MAX + 2u];
    uint16_t pending_length;
    uint16_t pending_offset;
    nice64_network_rudp_diagnostics_t diagnostics;
} nice64_network_rudp_state_t;

static nice64_network_rudp_state_t adapter;

static uint32_t saturating_add(uint32_t left, uint32_t right) {
    return UINT32_MAX - left < right ? UINT32_MAX : left + right;
}

static void reset_framer(void) {
    adapter.length_count = 0u;
    adapter.expected_length = 0u;
    adapter.received_length = 0u;
}

static void reset_session_buffers(void) {
    reset_framer();
    adapter.response_length = 0u;
    adapter.pending_length = 0u;
    adapter.pending_offset = 0u;
}

static bool queue_response_line(void) {
    if (adapter.response_length == 0u ||
        adapter.response_length > NICE64_NETWORK_RUDP_MESSAGE_MAX ||
        adapter.pending_length != 0u) {
        adapter.diagnostics.network_rudp_invalid_messages = saturating_add(
            adapter.diagnostics.network_rudp_invalid_messages, 1u);
        adapter.response_length = 0u;
        return false;
    }
    adapter.pending_frame[0] = (uint8_t)(adapter.response_length >> 8u);
    adapter.pending_frame[1] = (uint8_t)adapter.response_length;
    memcpy(&adapter.pending_frame[2], adapter.response_line,
           adapter.response_length);
    adapter.pending_length = (uint16_t)(adapter.response_length + 2u);
    adapter.pending_offset = 0u;
    adapter.diagnostics.network_rudp_responses_tx = saturating_add(
        adapter.diagnostics.network_rudp_responses_tx, 1u);
    adapter.diagnostics.network_rudp_response_bytes = saturating_add(
        adapter.diagnostics.network_rudp_response_bytes,
        adapter.response_length);
    adapter.response_length = 0u;
    return true;
}

static void response_write(void *context, const char *data, size_t length) {
    (void)context;
    if (!adapter.active || data == NULL || length == 0u) return;
    for (size_t index = 0u; index < length; ++index) {
        const char value = data[index];
        if (value == '\r') continue;
        if (value == '\n') {
            (void)queue_response_line();
            continue;
        }
        if (adapter.response_length >= NICE64_NETWORK_RUDP_MESSAGE_MAX) {
            adapter.diagnostics.network_rudp_invalid_messages = saturating_add(
                adapter.diagnostics.network_rudp_invalid_messages, 1u);
            adapter.response_length = 0u;
            continue;
        }
        adapter.response_line[adapter.response_length++] = value;
    }
}

static void response_flush(void *context) {
    (void)context;
}

static nice64_response_sink_t response_sink = {
    .write = response_write,
    .flush = response_flush,
    .context = &adapter,
};

static void deliver_complete_message(void) {
    adapter.receive_message[adapter.expected_length] = '\0';
    adapter.diagnostics.network_rudp_requests_rx = saturating_add(
        adapter.diagnostics.network_rudp_requests_rx, 1u);
    adapter.diagnostics.network_rudp_request_bytes = saturating_add(
        adapter.diagnostics.network_rudp_request_bytes,
        adapter.expected_length);
    adapter.diagnostics.network_rudp_application_deliveries = saturating_add(
        adapter.diagnostics.network_rudp_application_deliveries, 1u);
    if (!network_pak_handle_rudp_line(adapter.receive_message, &response_sink)) {
        adapter.diagnostics.network_rudp_invalid_messages = saturating_add(
            adapter.diagnostics.network_rudp_invalid_messages, 1u);
    }
}

static bool deliver_bytes(void *context,
                          uint32_t sequence,
                          const uint8_t *payload,
                          uint16_t length) {
    (void)context;
    (void)sequence;
    for (uint16_t index = 0u; index < length; ++index) {
        if (adapter.expected_length == 0u) {
            adapter.length_bytes[adapter.length_count++] = payload[index];
            if (adapter.length_count == 2u) {
                adapter.expected_length = (uint16_t)(
                    ((uint16_t)adapter.length_bytes[0] << 8u) |
                    adapter.length_bytes[1]);
                adapter.length_count = 0u;
                adapter.received_length = 0u;
                if (adapter.expected_length == 0u ||
                    adapter.expected_length > NICE64_NETWORK_RUDP_MESSAGE_MAX) {
                    adapter.diagnostics.network_rudp_invalid_messages =
                        saturating_add(
                            adapter.diagnostics.network_rudp_invalid_messages,
                            1u);
                    reset_framer();
                    return true;
                }
            }
            continue;
        }
        adapter.receive_message[adapter.received_length++] =
            (char)payload[index];
        if (adapter.received_length == adapter.expected_length) {
            deliver_complete_message();
            reset_framer();
        }
    }
    return true;
}

void nice64_network_rudp_adapter_init(void) {
    memset(&adapter, 0, sizeof(adapter));
}

bool nice64_network_rudp_begin(uint32_t session_id,
                               uint32_t host_tx_initial_sequence,
                               uint32_t firmware_tx_initial_sequence) {
    if (session_id == 0u || !network_pak_rudp_session_opened()) return false;
    if (adapter.active) {
        adapter.diagnostics.network_rudp_session_resets = saturating_add(
            adapter.diagnostics.network_rudp_session_resets, 1u);
    }
    reset_session_buffers();
    nice64_rudpc_engine_reset(session_id,
                              firmware_tx_initial_sequence,
                              host_tx_initial_sequence,
                              NICE64_NETWORK_RUDP_RTO_MS);
    adapter.active = true;
    adapter.diagnostics.network_rudp_sessions = saturating_add(
        adapter.diagnostics.network_rudp_sessions, 1u);
    return true;
}

void nice64_network_rudp_end(void) {
    if (adapter.active) {
        adapter.diagnostics.network_rudp_session_resets = saturating_add(
            adapter.diagnostics.network_rudp_session_resets, 1u);
    }
    adapter.active = false;
    reset_session_buffers();
    network_pak_rudp_session_closed();
}

bool nice64_network_rudp_active(void) {
    return adapter.active;
}

nice64_rudpc_rx_result_t nice64_network_rudp_receive(
    uint32_t session_id,
    uint32_t sequence,
    const uint8_t *payload,
    uint16_t length) {
    if (!adapter.active) return NICE64_RUDPC_RX_INVALID;
    nice64_rudpc_rx_result_t result = nice64_rudpc_rx_receive(
        session_id, sequence, payload, length, deliver_bytes, NULL);
    /* The frozen RX engine rejects duplicate/old DATA before deliver_bytes.
     * This counter intentionally measures duplicate upper delivery, not merely
     * duplicate transport reception, and therefore remains zero here. */
    return result;
}

void nice64_network_rudp_poll(uint32_t current_ms,
                              nice64_rudpc_send_fn send,
                              void *context) {
    if (!adapter.active) return;
    while (adapter.pending_length != 0u &&
           !nice64_rudpc_tx_full() &&
           !nice64_rudpc_tx_failed() &&
           (nice64_rudpc_tx_next_sequence() -
            nice64_rudpc_tx_base_sequence()) <
                NICE64_NETWORK_RUDP_PHYSICAL_CAP) {
        uint16_t remaining = (uint16_t)(adapter.pending_length -
                                        adapter.pending_offset);
        uint16_t count = remaining > NICE64_RUDPC_PAYLOAD_MAX
            ? NICE64_RUDPC_PAYLOAD_MAX : remaining;
        if (!nice64_rudpc_tx_try_enqueue(
                &adapter.pending_frame[adapter.pending_offset], count, NULL)) {
            break;
        }
        adapter.pending_offset = (uint16_t)(adapter.pending_offset + count);
        if (adapter.pending_offset == adapter.pending_length) {
            adapter.pending_length = 0u;
            adapter.pending_offset = 0u;
        }
    }
    nice64_rudpc_tx_poll(current_ms, send, context);
}

const nice64_response_sink_t *nice64_network_rudp_response_sink(void) {
    return adapter.active ? &response_sink : NULL;
}

const nice64_network_rudp_diagnostics_t *nice64_network_rudp_diagnostics(void) {
    return &adapter.diagnostics;
}
