#include "nice64_udp_service.h"

#include <limits.h>
#include <string.h>

#include "lwip/err.h"
#include "lwip/ip_addr.h"
#include "lwip/pbuf.h"
#include "lwip/udp.h"
#include "nice64_rudp_codec.h"
#include "nice64_rudp_engine.h"
#include "nice64_network_rudp_adapter.h"
#include "pico/cyw43_arch.h"
#include "pico/time.h"
#include "role_manager.h"
#include "wifi_service.h"
#include "wireless_transport_contract.h"

#define RUDPB_CONTROL_BYTES 1u
#define RUDPB_MAX_CONTROL_PAYLOAD \
    (RUDPB_CONTROL_BYTES + NICE64_RUDPB_TEST_PAYLOAD_MAX)
#define RUDPB_MAX_DATAGRAM \
    (NICE64_WIRELESS_HEADER_LENGTH + RUDPB_MAX_CONTROL_PAYLOAD)
#define RUDPB_STATS_FIELD_COUNT 45u
#define RUDPB_STATS_PAYLOAD_LENGTH \
    (RUDPB_CONTROL_BYTES + (RUDPB_STATS_FIELD_COUNT * 4u))
#define RUDPC_STATS_FIELD_COUNT 36u
#define RUDPC_STATS_PAYLOAD_LENGTH \
    (RUDPB_CONTROL_BYTES + (RUDPC_STATS_FIELD_COUNT * 4u))
#define RUDPC_STREAM_BEGIN_LENGTH 16u
#define RUDPC_STREAM_END_LENGTH 9u
#define RUDPC_MAX_STREAM_BYTES (2u * 1024u * 1024u)
#define NETWORK_RUDP_BEGIN_LENGTH 12u

enum {
    RUDPB_HELLO = 0x01u,
    RUDPB_PING = 0x03u,
    RUDPB_ECHO = 0x04u,
    RUDPB_GOODBYE = 0x05u,
    RUDPB_STATS = 0x06u,
    RUDPC_STREAM_BEGIN = 0x10u,
    RUDPC_STREAM_END = 0x11u,
    RUDPC_STREAM_PAUSE = 0x12u,
    RUDPC_STREAM_RESUME = 0x13u,
    RUDPC_STATS = 0x16u,
    NETWORK_RUDP_BEGIN = 0x20u,
    RUDPB_HELLO_ACK = 0x81u,
    RUDPB_BUSY = 0x82u,
    RUDPB_PONG = 0x83u,
    RUDPB_ECHO_REPLY = 0x84u,
    RUDPB_RELEASED = 0x85u,
    RUDPB_STATS_REPLY = 0x86u,
    RUDPC_STREAM_BEGIN_ACK = 0x90u,
    RUDPC_STREAM_RESULT = 0x91u,
    RUDPC_STREAM_STATE_ACK = 0x92u,
    RUDPC_STATS_REPLY = 0x96u,
    NETWORK_RUDP_BEGIN_ACK = 0xA0u,
    RUDPB_UNSUPPORTED_CLASS = 0xE1u,
    RUDPB_INVALID_CLASS = 0xE2u,
};

typedef struct {
    uint8_t wire[RUDPB_MAX_DATAGRAM];
    uint16_t length;
    ip_addr_t source;
    uint16_t source_port;
    uint32_t received_at_us;
    uint32_t callback_us;
    bool pending;
} rudpb_rx_slot_t;

typedef struct {
    uint32_t udp_rx;
    uint32_t udp_tx;
    uint32_t bytes_rx;
    uint32_t bytes_tx;
    uint32_t valid_headers;
    uint32_t invalid_headers;
    uint32_t unsupported_class;
    uint32_t invalid_class;
    uint32_t session_mismatch;
    uint32_t hello_received;
    uint32_t hello_accepted;
    uint32_t busy_responses;
    uint32_t ping_received;
    uint32_t pong_sent;
    uint32_t ownership_releases;
    uint32_t ownership_timeouts;
    uint32_t staging_full;
    uint32_t tx_failures;
    uint32_t pbuf_chains;
    uint32_t maximum_pbuf_length;
    uint32_t pbuf_type_internal_last;
    uint32_t pbuf_type_internal_or;
    uint32_t copy_count;
    uint32_t copy_total_us;
    uint32_t copy_min_us;
    uint32_t copy_max_us;
    uint32_t callback_count;
    uint32_t callback_total_us;
    uint32_t callback_min_us;
    uint32_t callback_max_us;
    uint32_t validation_count;
    uint32_t validation_total_us;
    uint32_t validation_min_us;
    uint32_t validation_max_us;
    uint32_t service_count;
    uint32_t service_total_us;
    uint32_t service_min_us;
    uint32_t service_max_us;
    uint32_t maximum_pending_us;
    uint32_t send_count;
    uint32_t send_total_us;
    uint32_t send_min_us;
    uint32_t send_max_us;
    uint32_t codec_self_test_pass;
} rudpb_diagnostics_t;

typedef enum {
    RUDPC_DIRECTION_UPLOAD = 0u,
    RUDPC_DIRECTION_DOWNLOAD = 1u,
} rudpc_direction_t;

typedef struct {
    bool active;
    rudpc_direction_t direction;
    uint32_t initial_sequence;
    uint32_t expected_bytes;
    uint32_t expected_crc32;
    uint32_t transferred_bytes;
    uint32_t crc32_state;
    uint32_t upper_deliveries;
    uint32_t duplicate_upper_deliveries;
    uint32_t generated_bytes;
    uint32_t started_ms;
    uint32_t completed_transfers;
    uint32_t failed_transfers;
    uint32_t last_result;
    uint32_t last_staging_drop_sequence;
    uint32_t staging_drop_first32_bitmap;
    uint32_t last_ack_base;
    uint32_t last_ack_bitmap;
} rudpc_synthetic_state_t;

typedef struct {
    struct udp_pcb *pcb;
    rudpb_rx_slot_t rx[NICE64_RUDPB_RX_SLOT_COUNT];
    uint8_t tx[RUDPB_MAX_DATAGRAM];
    uint8_t next_rx;
    bool owner_active;
    uint32_t owner_session;
    ip_addr_t owner_address;
    uint16_t owner_port;
    uint32_t owner_last_activity_ms;
    rudpb_diagnostics_t diagnostics;
    rudpc_synthetic_state_t synthetic;
    uint8_t synthetic_source[NICE64_RUDPC_PAYLOAD_MAX];
} rudpb_state_t;

static rudpb_state_t state;

static uint32_t saturating_add(uint32_t left, uint32_t right) {
    return UINT32_MAX - left < right ? UINT32_MAX : left + right;
}

static uint32_t now_ms(void) {
    return to_ms_since_boot(get_absolute_time());
}

static void observe_timing(uint32_t value,
                           uint32_t *count,
                           uint32_t *total,
                           uint32_t *minimum,
                           uint32_t *maximum) {
    *count = saturating_add(*count, 1u);
    *total = saturating_add(*total, value);
    if (value < *minimum) *minimum = value;
    if (value > *maximum) *maximum = value;
}

static void write_u32_be(uint8_t *bytes, uint32_t value) {
    bytes[0] = (uint8_t)(value >> 24u);
    bytes[1] = (uint8_t)(value >> 16u);
    bytes[2] = (uint8_t)(value >> 8u);
    bytes[3] = (uint8_t)value;
}

static uint16_t read_u16_be(const uint8_t *bytes) {
    return (uint16_t)(((uint16_t)bytes[0] << 8u) | bytes[1]);
}

static uint32_t read_u32_be(const uint8_t *bytes) {
    return ((uint32_t)bytes[0] << 24u) |
           ((uint32_t)bytes[1] << 16u) |
           ((uint32_t)bytes[2] << 8u) |
           (uint32_t)bytes[3];
}

static uint32_t crc32_update(uint32_t crc, const uint8_t *bytes, uint16_t length) {
    for (uint16_t index = 0u; index < length; ++index) {
        crc ^= bytes[index];
        for (uint32_t bit = 0u; bit < 8u; ++bit) {
            crc = (crc >> 1u) ^ (0xEDB88320u & (0u - (crc & 1u)));
        }
    }
    return crc;
}

static uint32_t count_set_bits(uint32_t value) {
    uint32_t count = 0u;
    while (value != 0u) {
        value &= value - 1u;
        count++;
    }
    return count;
}

static bool codec_self_test(void) {
    static const uint8_t expected[] = {
        0x4Eu, 0x36u, 0x34u, 0x55u, 0x01u, 0x01u, 0x00u, 0x1Cu,
        0x10u, 0x20u, 0x30u, 0x40u, 0x50u, 0x60u, 0x70u, 0x80u,
        0x90u, 0xA0u, 0xB0u, 0xC0u, 0xD0u, 0xE0u, 0xF0u, 0x01u,
        0x00u, 0x03u, 0x00u, 0x00u, 0xAAu, 0xBBu, 0xCCu,
    };
    static const uint8_t payload[] = {0xAAu, 0xBBu, 0xCCu};
    nice64_rudp_header_t header = {
        .magic = NICE64_WIRELESS_MAGIC,
        .version = NICE64_WIRELESS_TRANSPORT_VERSION,
        .flags = NICE64_RUDP_FLAG_DATA,
        .delivery_class = NICE64_DELIVERY_RELIABLE_ORDERED,
        .header_length = NICE64_WIRELESS_HEADER_LENGTH,
        .session_id = 0x10203040u,
        .sequence = 0x50607080u,
        .ack_base = 0x90A0B0C0u,
        .ack_bitmap = 0xD0E0F001u,
        .payload_length = sizeof(payload),
        .header_crc_or_reserved = 0u,
    };
    uint8_t wire[sizeof(expected)];
    size_t wire_length = 0u;
    if (nice64_rudp_encode(&header, payload, RUDPB_MAX_CONTROL_PAYLOAD,
                           wire, sizeof(wire), &wire_length) != NICE64_RUDP_VALID ||
        wire_length != sizeof(expected) ||
        memcmp(wire, expected, sizeof(expected)) != 0) {
        return false;
    }
    memset(&header, 0, sizeof(header));
    return nice64_rudp_decode(expected, sizeof(expected),
                             RUDPB_MAX_CONTROL_PAYLOAD, true, 0x10203040u,
                             &header) == NICE64_RUDP_VALID &&
           header.sequence == 0x50607080u &&
           header.ack_base == 0x90A0B0C0u &&
           header.ack_bitmap == 0xD0E0F001u &&
           header.payload_length == 3u;
}

static void clear_owner(void) {
    const bool reset_engine = state.owner_active || state.synthetic.active ||
                              nice64_rudpc_session_id() != 0u;
    state.owner_active = false;
    state.owner_session = 0u;
    ip_addr_set_zero(&state.owner_address);
    state.owner_port = 0u;
    state.owner_last_activity_ms = 0u;
    state.synthetic.active = false;
    if (nice64_network_rudp_active()) {
        nice64_network_rudp_end();
    }
    if (reset_engine) {
        nice64_rudpc_engine_reset(0u, 0u, 0u, 20u);
    }
}

static bool owner_matches(const rudpb_rx_slot_t *slot,
                          const nice64_rudp_header_t *header) {
    return state.owner_active &&
           state.owner_session == header->session_id &&
           state.owner_port == slot->source_port &&
           ip_addr_cmp(&state.owner_address, &slot->source);
}

static bool send_packet(const ip_addr_t *address,
                        uint16_t port,
                        uint32_t session,
                        uint32_t sequence,
                        uint8_t flags,
                        uint32_t ack_base,
                        uint32_t ack_bitmap,
                        const uint8_t *payload,
                        uint16_t payload_length) {
    nice64_rudp_header_t header = {
        .magic = NICE64_WIRELESS_MAGIC,
        .version = NICE64_WIRELESS_TRANSPORT_VERSION,
        .flags = flags,
        .delivery_class = NICE64_DELIVERY_RELIABLE_ORDERED,
        .header_length = NICE64_WIRELESS_HEADER_LENGTH,
        .session_id = session,
        .sequence = sequence,
        .ack_base = ack_base,
        .ack_bitmap = ack_bitmap,
        .payload_length = payload_length,
        .header_crc_or_reserved = 0u,
    };
    size_t wire_length = 0u;
    if (nice64_rudp_encode(&header, payload, RUDPB_MAX_CONTROL_PAYLOAD,
                           state.tx, sizeof(state.tx), &wire_length) !=
        NICE64_RUDP_VALID) {
        state.diagnostics.tx_failures =
            saturating_add(state.diagnostics.tx_failures, 1u);
        return false;
    }

    struct pbuf *packet = pbuf_alloc(PBUF_TRANSPORT, (u16_t)wire_length, PBUF_RAM);
    if (packet == NULL || pbuf_take(packet, state.tx, wire_length) != ERR_OK) {
        if (packet != NULL) pbuf_free(packet);
        state.diagnostics.tx_failures =
            saturating_add(state.diagnostics.tx_failures, 1u);
        return false;
    }
    const uint32_t send_started_us = time_us_32();
    const err_t result = udp_sendto(state.pcb, packet, address, port);
    const uint32_t send_us = time_us_32() - send_started_us;
    observe_timing(send_us,
                   &state.diagnostics.send_count,
                   &state.diagnostics.send_total_us,
                   &state.diagnostics.send_min_us,
                   &state.diagnostics.send_max_us);
    pbuf_free(packet);
    if (result != ERR_OK) {
        state.diagnostics.tx_failures =
            saturating_add(state.diagnostics.tx_failures, 1u);
        return false;
    }
    state.diagnostics.udp_tx = saturating_add(state.diagnostics.udp_tx, 1u);
    state.diagnostics.bytes_tx = saturating_add(
        state.diagnostics.bytes_tx, (uint32_t)wire_length);
    return true;
}

static bool send_payload(const ip_addr_t *address,
                         uint16_t port,
                         uint32_t session,
                         uint32_t sequence,
                         const uint8_t *payload,
                         uint16_t payload_length) {
    return send_packet(address, port, session, sequence,
                       NICE64_RUDP_FLAG_DATA, 0u, 0u,
                       payload, payload_length);
}

static void send_control(const rudpb_rx_slot_t *slot,
                         const nice64_rudp_header_t *header,
                         uint8_t operation) {
    send_payload(&slot->source, slot->source_port, header->session_id,
                 header->sequence, &operation, 1u);
}

static uint16_t write_stats_payload(uint8_t *payload) {
    const rudpb_diagnostics_t *d = &state.diagnostics;
    const uint32_t fields[RUDPB_STATS_FIELD_COUNT] = {
        d->udp_rx, d->udp_tx, d->bytes_rx, d->bytes_tx,
        d->valid_headers, d->invalid_headers, d->unsupported_class,
        d->invalid_class, d->session_mismatch, d->hello_received,
        d->hello_accepted, d->busy_responses, d->ping_received,
        d->pong_sent, d->ownership_releases, d->ownership_timeouts,
        d->staging_full, d->tx_failures, d->pbuf_chains,
        d->maximum_pbuf_length, d->pbuf_type_internal_last,
        d->pbuf_type_internal_or, d->copy_count, d->copy_total_us,
        d->copy_min_us == UINT32_MAX ? 0u : d->copy_min_us,
        d->copy_max_us, d->callback_count, d->callback_total_us,
        d->callback_min_us == UINT32_MAX ? 0u : d->callback_min_us,
        d->callback_max_us, d->validation_count, d->validation_total_us,
        d->validation_min_us == UINT32_MAX ? 0u : d->validation_min_us,
        d->validation_max_us, d->service_count, d->service_total_us,
        d->service_min_us == UINT32_MAX ? 0u : d->service_min_us,
        d->service_max_us, d->maximum_pending_us, d->send_count,
        d->send_total_us, d->send_min_us == UINT32_MAX ? 0u : d->send_min_us,
        d->send_max_us, d->codec_self_test_pass,
        state.owner_active ? 1u : 0u,
    };
    payload[0] = RUDPB_STATS_REPLY;
    for (uint32_t i = 0u; i < RUDPB_STATS_FIELD_COUNT; ++i) {
        write_u32_be(&payload[1u + (i * 4u)], fields[i]);
    }
    return RUDPB_STATS_PAYLOAD_LENGTH;
}

static uint16_t write_rudpc_stats_payload(uint8_t *payload) {
    const nice64_rudpc_diagnostics_t *d = nice64_rudpc_diagnostics();
    const uint32_t fields[RUDPC_STATS_FIELD_COUNT] = {
        d->tx_datagrams, d->rx_datagrams, d->tx_payload_bytes,
        d->rx_payload_bytes, d->acks_sent, d->acks_received,
        d->retransmissions, d->duplicate_rx, d->out_of_order_rx,
        d->old_or_stale_rx, state.diagnostics.invalid_headers,
        state.diagnostics.unsupported_class, state.diagnostics.invalid_class,
        saturating_add(d->session_mismatch, state.diagnostics.session_mismatch),
        d->tx_window_full,
        d->rx_window_full, state.diagnostics.staging_full,
        d->retry_exhaustion, d->session_resets, d->maximum_retry_count,
        d->rtt_sample_count, d->rtt_total_ms, d->rtt_max_ms,
        (uint32_t)nice64_rudpc_fixed_state_bytes(),
        state.synthetic.active ? 1u : 0u,
        (uint32_t)state.synthetic.direction,
        state.synthetic.expected_bytes, state.synthetic.transferred_bytes,
        state.synthetic.upper_deliveries,
        state.synthetic.duplicate_upper_deliveries,
        state.synthetic.completed_transfers,
        count_set_bits(state.synthetic.last_ack_bitmap),
        state.synthetic.last_staging_drop_sequence,
        state.synthetic.staging_drop_first32_bitmap,
        state.synthetic.last_ack_base,
        state.synthetic.last_ack_bitmap,
    };
    payload[0] = RUDPC_STATS_REPLY;
    for (uint32_t i = 0u; i < RUDPC_STATS_FIELD_COUNT; ++i) {
        write_u32_be(&payload[1u + (i * 4u)], fields[i]);
    }
    return RUDPC_STATS_PAYLOAD_LENGTH;
}

static bool synthetic_deliver(void *context,
                              uint32_t sequence,
                              const uint8_t *payload,
                              uint16_t length) {
    (void)context;
    (void)sequence;
    if (!state.synthetic.active ||
        state.synthetic.direction != RUDPC_DIRECTION_UPLOAD) {
        return false;
    }
    if (state.synthetic.transferred_bytes > state.synthetic.expected_bytes ||
        length > state.synthetic.expected_bytes -
                 state.synthetic.transferred_bytes) {
        state.synthetic.last_result = 1u;
        state.synthetic.failed_transfers = saturating_add(
            state.synthetic.failed_transfers, 1u);
        return false;
    }
    state.synthetic.crc32_state = crc32_update(
        state.synthetic.crc32_state, payload, length);
    state.synthetic.transferred_bytes += length;
    state.synthetic.upper_deliveries = saturating_add(
        state.synthetic.upper_deliveries, 1u);
    return true;
}

static void send_reliable_ack(void) {
    if (!state.owner_active || state.pcb == NULL) return;
    uint32_t ack_base = 0u;
    uint32_t ack_bitmap = 0u;
    nice64_rudpc_rx_ack(&ack_base, &ack_bitmap);
    state.synthetic.last_ack_base = ack_base;
    state.synthetic.last_ack_bitmap = ack_bitmap;
    (void)send_packet(&state.owner_address, state.owner_port,
                      state.owner_session, 0u, NICE64_RUDP_FLAG_ACK,
                      ack_base, ack_bitmap, NULL, 0u);
}

static bool send_reliable_data(void *context,
                               uint32_t sequence,
                               const uint8_t *payload,
                               uint16_t length,
                               bool retransmission,
                               uint8_t retransmission_count) {
    (void)context;
    (void)retransmission;
    (void)retransmission_count;
    uint32_t ack_base = 0u;
    uint32_t ack_bitmap = 0u;
    nice64_rudpc_rx_ack(&ack_base, &ack_bitmap);
    return send_packet(&state.owner_address, state.owner_port,
                       state.owner_session, sequence,
                       NICE64_RUDP_FLAG_DATA | NICE64_RUDP_FLAG_ACK,
                       ack_base, ack_bitmap, payload, length);
}

static void begin_synthetic_stream(const rudpb_rx_slot_t *slot,
                                   const nice64_rudp_header_t *header,
                                   const uint8_t *payload) {
    const uint8_t direction = payload[1];
    const uint32_t rto_ms = read_u16_be(&payload[2]);
    const uint32_t initial_sequence = read_u32_be(&payload[4]);
    const uint32_t total_bytes = read_u32_be(&payload[8]);
    const uint32_t expected_crc = read_u32_be(&payload[12]);
    uint8_t response[12] = {RUDPC_STREAM_BEGIN_ACK, 1u, direction, 0u};

    if (direction <= RUDPC_DIRECTION_DOWNLOAD && total_bytes != 0u &&
        total_bytes <= RUDPC_MAX_STREAM_BYTES && rto_ms != 0u &&
        rto_ms <= 1000u) {
        const uint32_t completed = state.synthetic.completed_transfers;
        const uint32_t failed = state.synthetic.failed_transfers;
        memset(&state.synthetic, 0, sizeof(state.synthetic));
        state.synthetic.completed_transfers = completed;
        state.synthetic.failed_transfers = failed;
        state.synthetic.active = true;
        state.synthetic.direction = (rudpc_direction_t)direction;
        state.synthetic.initial_sequence = initial_sequence;
        state.synthetic.expected_bytes = total_bytes;
        state.synthetic.expected_crc32 = expected_crc;
        state.synthetic.crc32_state = 0xFFFFFFFFu;
        state.synthetic.last_staging_drop_sequence = UINT32_MAX;
        state.synthetic.last_ack_base = initial_sequence - 1u;
        state.synthetic.started_ms = now_ms();
        nice64_rudpc_engine_reset(header->session_id, initial_sequence,
                                  initial_sequence, rto_ms);
        response[1] = 0u;
    }
    write_u32_be(&response[4], initial_sequence);
    write_u32_be(&response[8], (uint32_t)nice64_rudpc_fixed_state_bytes());
    (void)send_payload(&slot->source, slot->source_port, header->session_id,
                       header->sequence, response, sizeof(response));
}

static void begin_network_rudp(const rudpb_rx_slot_t *slot,
                               const nice64_rudp_header_t *header,
                               const uint8_t *payload) {
    const uint8_t version = payload[1];
    const uint16_t rto_ms = read_u16_be(&payload[2]);
    const uint32_t host_tx_initial = read_u32_be(&payload[4]);
    const uint32_t firmware_tx_initial = read_u32_be(&payload[8]);
    uint8_t response[NETWORK_RUDP_BEGIN_LENGTH] = {
        NETWORK_RUDP_BEGIN_ACK, 1u, version, 0u
    };
    if (!state.synthetic.active && version == 1u &&
        rto_ms == NICE64_NETWORK_RUDP_RTO_MS &&
        nice64_network_rudp_begin(header->session_id,
                                  host_tx_initial,
                                  firmware_tx_initial)) {
        response[1] = 0u;
    }
    write_u32_be(&response[4], host_tx_initial);
    write_u32_be(&response[8], firmware_tx_initial);
    (void)send_payload(&slot->source, slot->source_port,
                       header->session_id, header->sequence,
                       response, sizeof(response));
}

static void end_synthetic_stream(const rudpb_rx_slot_t *slot,
                                 const nice64_rudp_header_t *header,
                                 const uint8_t *payload) {
    if (state.synthetic.active &&
        state.synthetic.direction == RUDPC_DIRECTION_DOWNLOAD) {
        state.synthetic.transferred_bytes = read_u32_be(&payload[1]);
        state.synthetic.crc32_state = read_u32_be(&payload[5]) ^ 0xFFFFFFFFu;
    }
    const uint32_t actual_crc = state.synthetic.crc32_state ^ 0xFFFFFFFFu;
    uint8_t status = 0u;
    if (!state.synthetic.active) {
        status = 1u;
    } else if (nice64_rudpc_tx_failed()) {
        status = 3u;
    } else if (state.synthetic.transferred_bytes !=
               state.synthetic.expected_bytes) {
        status = 1u;
    } else if (actual_crc != state.synthetic.expected_crc32) {
        status = 2u;
    }
    state.synthetic.last_result = status;
    if (status == 0u) {
        state.synthetic.completed_transfers = saturating_add(
            state.synthetic.completed_transfers, 1u);
    } else {
        state.synthetic.failed_transfers = saturating_add(
            state.synthetic.failed_transfers, 1u);
    }

    uint8_t response[32] = {RUDPC_STREAM_RESULT, status,
                            (uint8_t)state.synthetic.direction, 0u};
    write_u32_be(&response[4], state.synthetic.expected_bytes);
    write_u32_be(&response[8], state.synthetic.transferred_bytes);
    write_u32_be(&response[12], state.synthetic.expected_crc32);
    write_u32_be(&response[16], actual_crc);
    write_u32_be(&response[20], state.synthetic.upper_deliveries);
    write_u32_be(&response[24], now_ms() - state.synthetic.started_ms);
    write_u32_be(&response[28], state.synthetic.last_result);
    (void)send_payload(&slot->source, slot->source_port, header->session_id,
                       header->sequence, response, sizeof(response));
    state.synthetic.active = false;
}

static void service_synthetic_download(void) {
    if (!state.synthetic.active ||
        state.synthetic.direction != RUDPC_DIRECTION_DOWNLOAD) {
        return;
    }
    while (state.synthetic.generated_bytes < state.synthetic.expected_bytes &&
           !nice64_rudpc_tx_full() && !nice64_rudpc_tx_failed()) {
        uint32_t remaining = state.synthetic.expected_bytes -
                             state.synthetic.generated_bytes;
        const uint16_t length = (uint16_t)(remaining > NICE64_RUDPC_PAYLOAD_MAX
            ? NICE64_RUDPC_PAYLOAD_MAX : remaining);
        for (uint16_t index = 0u; index < length; ++index) {
            const uint32_t offset = state.synthetic.generated_bytes + index;
            state.synthetic_source[index] = (uint8_t)((offset * 31u + 7u) & 0xFFu);
        }
        if (!nice64_rudpc_tx_try_enqueue(state.synthetic_source, length, NULL)) break;
        state.synthetic.generated_bytes += length;
    }
    nice64_rudpc_tx_poll(now_ms(), send_reliable_data, NULL);
}

static void record_validation(nice64_rudp_validation_t validation) {
    if (validation == NICE64_RUDP_VALID) {
        state.diagnostics.valid_headers =
            saturating_add(state.diagnostics.valid_headers, 1u);
        return;
    }
    state.diagnostics.invalid_headers =
        saturating_add(state.diagnostics.invalid_headers, 1u);
    if (validation == NICE64_RUDP_UNSUPPORTED_DELIVERY_CLASS) {
        state.diagnostics.unsupported_class =
            saturating_add(state.diagnostics.unsupported_class, 1u);
    } else if (validation == NICE64_RUDP_INVALID_DELIVERY_CLASS) {
        state.diagnostics.invalid_class =
            saturating_add(state.diagnostics.invalid_class, 1u);
    } else if (validation == NICE64_RUDP_SESSION_MISMATCH) {
        state.diagnostics.session_mismatch =
            saturating_add(state.diagnostics.session_mismatch, 1u);
    }
}

static void process_slot(rudpb_rx_slot_t *slot) {
    const uint32_t started_us = time_us_32();
    const uint32_t pending_us = started_us - slot->received_at_us;
    if (pending_us > state.diagnostics.maximum_pending_us) {
        state.diagnostics.maximum_pending_us = pending_us;
    }

    nice64_rudp_header_t header;
    memset(&header, 0, sizeof(header));
    const uint32_t validation_started_us = time_us_32();
    const nice64_rudp_validation_t validation = nice64_rudp_decode(
        slot->wire, slot->length, RUDPB_MAX_CONTROL_PAYLOAD,
        false, 0u, &header);
    const uint32_t validation_us = time_us_32() - validation_started_us;
    observe_timing(validation_us,
                   &state.diagnostics.validation_count,
                   &state.diagnostics.validation_total_us,
                   &state.diagnostics.validation_min_us,
                   &state.diagnostics.validation_max_us);
    record_validation(validation);
    if (validation == NICE64_RUDP_UNSUPPORTED_DELIVERY_CLASS) {
        send_control(slot, &header, RUDPB_UNSUPPORTED_CLASS);
    } else if (validation == NICE64_RUDP_INVALID_DELIVERY_CLASS) {
        send_control(slot, &header, RUDPB_INVALID_CLASS);
    } else if (validation == NICE64_RUDP_VALID) {
        uint8_t *payload = &slot->wire[NICE64_WIRELESS_HEADER_LENGTH];
        const bool has_payload = header.payload_length != 0u;
        const uint8_t operation = has_payload ? payload[0] : 0u;
        if (header.flags == NICE64_RUDP_FLAG_DATA && has_payload &&
            operation == RUDPB_HELLO) {
            state.diagnostics.hello_received =
                saturating_add(state.diagnostics.hello_received, 1u);
            if (header.session_id == 0u) {
                state.diagnostics.invalid_headers =
                    saturating_add(state.diagnostics.invalid_headers, 1u);
            } else if (!state.owner_active || owner_matches(slot, &header)) {
                state.owner_active = true;
                state.owner_session = header.session_id;
                ip_addr_copy(state.owner_address, slot->source);
                state.owner_port = slot->source_port;
                state.owner_last_activity_ms = now_ms();
                state.diagnostics.hello_accepted =
                    saturating_add(state.diagnostics.hello_accepted, 1u);
                send_control(slot, &header, RUDPB_HELLO_ACK);
            } else {
                state.diagnostics.busy_responses =
                    saturating_add(state.diagnostics.busy_responses, 1u);
                send_control(slot, &header, RUDPB_BUSY);
            }
        } else if (!owner_matches(slot, &header)) {
            state.diagnostics.session_mismatch =
                saturating_add(state.diagnostics.session_mismatch, 1u);
        } else {
            state.owner_last_activity_ms = now_ms();
            if ((header.flags & NICE64_RUDP_FLAG_ACK) != 0u &&
                (state.synthetic.active || nice64_network_rudp_active())) {
                nice64_rudpc_tx_apply_ack(header.ack_base, header.ack_bitmap,
                                          now_ms());
            }
            if (header.flags == NICE64_RUDP_KNOWN_FLAGS &&
                nice64_network_rudp_active()) {
                (void)nice64_network_rudp_receive(
                    header.session_id, header.sequence, payload,
                    header.payload_length);
                send_reliable_ack();
            } else if (header.flags == NICE64_RUDP_KNOWN_FLAGS &&
                state.synthetic.active &&
                state.synthetic.direction == RUDPC_DIRECTION_UPLOAD) {
                (void)nice64_rudpc_rx_receive(
                    header.session_id, header.sequence, payload,
                    header.payload_length, synthetic_deliver, NULL);
                send_reliable_ack();
            } else if (header.flags == NICE64_RUDP_FLAG_DATA && has_payload &&
                       operation == RUDPB_PING) {
                state.diagnostics.ping_received =
                    saturating_add(state.diagnostics.ping_received, 1u);
                payload[0] = RUDPB_PONG;
                if (send_payload(&slot->source, slot->source_port,
                                 header.session_id, header.sequence,
                                 payload, header.payload_length)) {
                    state.diagnostics.pong_sent =
                        saturating_add(state.diagnostics.pong_sent, 1u);
                }
            } else if (header.flags == NICE64_RUDP_FLAG_DATA && has_payload &&
                       operation == RUDPB_ECHO) {
                payload[0] = RUDPB_ECHO_REPLY;
                send_payload(&slot->source, slot->source_port,
                             header.session_id, header.sequence,
                             payload, header.payload_length);
            } else if (header.flags == NICE64_RUDP_FLAG_DATA && has_payload &&
                       operation == RUDPB_STATS) {
                const uint16_t length = write_stats_payload(payload);
                send_payload(&slot->source, slot->source_port,
                             header.session_id, header.sequence,
                             payload, length);
            } else if (header.flags == NICE64_RUDP_FLAG_DATA &&
                       header.payload_length == RUDPC_STREAM_BEGIN_LENGTH &&
                       operation == RUDPC_STREAM_BEGIN) {
                begin_synthetic_stream(slot, &header, payload);
            } else if (header.flags == NICE64_RUDP_FLAG_DATA &&
                       header.payload_length == NETWORK_RUDP_BEGIN_LENGTH &&
                       operation == NETWORK_RUDP_BEGIN) {
                begin_network_rudp(slot, &header, payload);
            } else if (header.flags == NICE64_RUDP_FLAG_DATA &&
                       header.payload_length == RUDPC_STREAM_END_LENGTH &&
                       operation == RUDPC_STREAM_END) {
                end_synthetic_stream(slot, &header, payload);
            } else if (header.flags == NICE64_RUDP_FLAG_DATA &&
                       header.payload_length == 1u &&
                       operation == RUDPC_STREAM_PAUSE &&
                       state.synthetic.active) {
                nice64_rudpc_rx_set_paused(true);
                send_control(slot, &header, RUDPC_STREAM_STATE_ACK);
            } else if (header.flags == NICE64_RUDP_FLAG_DATA &&
                       header.payload_length == 1u &&
                       operation == RUDPC_STREAM_RESUME &&
                       state.synthetic.active) {
                nice64_rudpc_rx_set_paused(false);
                (void)nice64_rudpc_rx_drain(synthetic_deliver, NULL);
                send_reliable_ack();
                send_control(slot, &header, RUDPC_STREAM_STATE_ACK);
            } else if (header.flags == NICE64_RUDP_FLAG_DATA &&
                       header.payload_length == 1u &&
                       operation == RUDPC_STATS) {
                const uint16_t length = write_rudpc_stats_payload(payload);
                send_payload(&slot->source, slot->source_port,
                             header.session_id, header.sequence,
                             payload, length);
            } else if (header.flags == NICE64_RUDP_FLAG_DATA && has_payload &&
                       operation == RUDPB_GOODBYE) {
                send_control(slot, &header, RUDPB_RELEASED);
                state.diagnostics.ownership_releases =
                    saturating_add(state.diagnostics.ownership_releases, 1u);
                clear_owner();
            }
        }
    }

    const uint32_t service_us = time_us_32() - started_us;
    observe_timing(service_us,
                   &state.diagnostics.service_count,
                   &state.diagnostics.service_total_us,
                   &state.diagnostics.service_min_us,
                   &state.diagnostics.service_max_us);
    slot->pending = false;
}

static void udp_receive_callback(void *argument,
                                 struct udp_pcb *pcb,
                                 struct pbuf *packet,
                                 const ip_addr_t *address,
                                 u16_t port) {
    (void)argument;
    (void)pcb;
    const uint32_t started_us = time_us_32();
    if (packet == NULL || address == NULL) return;

    state.diagnostics.udp_rx = saturating_add(state.diagnostics.udp_rx, 1u);
    state.diagnostics.bytes_rx = saturating_add(
        state.diagnostics.bytes_rx, packet->tot_len);
    if (packet->next != NULL) {
        state.diagnostics.pbuf_chains =
            saturating_add(state.diagnostics.pbuf_chains, 1u);
    }
    if (packet->tot_len > state.diagnostics.maximum_pbuf_length) {
        state.diagnostics.maximum_pbuf_length = packet->tot_len;
    }
    state.diagnostics.pbuf_type_internal_last = packet->type_internal;
    state.diagnostics.pbuf_type_internal_or |= packet->type_internal;

    rudpb_rx_slot_t *slot = NULL;
    for (uint32_t offset = 0u; offset < NICE64_RUDPB_RX_SLOT_COUNT; ++offset) {
        const uint32_t index = (state.next_rx + offset) % NICE64_RUDPB_RX_SLOT_COUNT;
        if (!state.rx[index].pending) {
            slot = &state.rx[index];
            state.next_rx = (uint8_t)((index + 1u) % NICE64_RUDPB_RX_SLOT_COUNT);
            break;
        }
    }
    if (slot == NULL || packet->tot_len > sizeof(slot->wire)) {
        state.diagnostics.staging_full =
            saturating_add(state.diagnostics.staging_full, 1u);
        if (slot == NULL && state.synthetic.active &&
            packet->tot_len >= NICE64_WIRELESS_HEADER_LENGTH) {
            uint8_t sequence_bytes[4];
            uint8_t flags = 0u;
            if (pbuf_copy_partial(packet, &flags, sizeof(flags), 5u) ==
                    sizeof(flags) &&
                flags == NICE64_RUDP_KNOWN_FLAGS &&
                pbuf_copy_partial(packet, sequence_bytes,
                                  sizeof(sequence_bytes), 12u) ==
                sizeof(sequence_bytes)) {
                const uint32_t sequence = read_u32_be(sequence_bytes);
                const uint32_t relative = sequence -
                                          state.synthetic.initial_sequence;
                state.synthetic.last_staging_drop_sequence = sequence;
                if (relative < 32u) {
                    state.synthetic.staging_drop_first32_bitmap |=
                        1u << relative;
                }
            }
        }
    } else {
        const uint32_t copy_started_us = time_us_32();
        const u16_t copied = pbuf_copy_partial(
            packet, slot->wire, packet->tot_len, 0u);
        const uint32_t copy_us = time_us_32() - copy_started_us;
        observe_timing(copy_us,
                       &state.diagnostics.copy_count,
                       &state.diagnostics.copy_total_us,
                       &state.diagnostics.copy_min_us,
                       &state.diagnostics.copy_max_us);
        if (copied != packet->tot_len) {
            state.diagnostics.staging_full =
                saturating_add(state.diagnostics.staging_full, 1u);
        } else {
            slot->length = packet->tot_len;
            ip_addr_copy(slot->source, *address);
            slot->source_port = port;
            slot->received_at_us = time_us_32();
            slot->pending = true;
        }
    }
    pbuf_free(packet);

    const uint32_t callback_us = time_us_32() - started_us;
    if (slot != NULL && slot->pending) slot->callback_us = callback_us;
    observe_timing(callback_us,
                   &state.diagnostics.callback_count,
                   &state.diagnostics.callback_total_us,
                   &state.diagnostics.callback_min_us,
                   &state.diagnostics.callback_max_us);
}

static void create_listener(void) {
    state.pcb = udp_new_ip_type(IPADDR_TYPE_V4);
    if (state.pcb == NULL) {
        state.diagnostics.tx_failures =
            saturating_add(state.diagnostics.tx_failures, 1u);
        return;
    }
    if (udp_bind(state.pcb, IP_ANY_TYPE, NICE64_RUDPB_UDP_PORT) != ERR_OK) {
        udp_remove(state.pcb);
        state.pcb = NULL;
        state.diagnostics.tx_failures =
            saturating_add(state.diagnostics.tx_failures, 1u);
        return;
    }
    udp_recv(state.pcb, udp_receive_callback, &state);
}

static void stop_service(void) {
    if (state.pcb != NULL) {
        udp_remove(state.pcb);
        state.pcb = NULL;
    }
    for (uint32_t i = 0u; i < NICE64_RUDPB_RX_SLOT_COUNT; ++i) {
        state.rx[i].pending = false;
    }
    clear_owner();
}

void nice64_udp_service_init(void) {
    memset(&state, 0, sizeof(state));
    nice64_rudpc_engine_init();
    nice64_network_rudp_adapter_init();
    state.diagnostics.callback_min_us = UINT32_MAX;
    state.diagnostics.copy_min_us = UINT32_MAX;
    state.diagnostics.validation_min_us = UINT32_MAX;
    state.diagnostics.service_min_us = UINT32_MAX;
    state.diagnostics.send_min_us = UINT32_MAX;
    state.diagnostics.codec_self_test_pass = codec_self_test() ? 1u : 0u;
}

void nice64_udp_service_poll(void) {
    cyw43_arch_lwip_begin();
    const nice64_active_mode_t mode = role_manager_active_mode();
    const bool network_mode = mode.role == NICE64_ROLE_STANDALONE &&
                              mode.standalone_service == NICE64_SERVICE_NETWORK;
    if (!wifi_service_connected() || !network_mode) {
        stop_service();
    } else {
        if (state.pcb == NULL) create_listener();
        if (state.owner_active &&
            (uint32_t)(now_ms() - state.owner_last_activity_ms) >=
                NICE64_RUDPB_OWNER_TIMEOUT_MS) {
            state.diagnostics.ownership_timeouts =
                saturating_add(state.diagnostics.ownership_timeouts, 1u);
            clear_owner();
        }
        for (uint32_t i = 0u; i < NICE64_RUDPB_RX_SLOT_COUNT; ++i) {
            if (state.rx[i].pending) process_slot(&state.rx[i]);
        }
        if (state.synthetic.active &&
            state.synthetic.direction == RUDPC_DIRECTION_DOWNLOAD) {
            const uint32_t service_started_us = time_us_32();
            service_synthetic_download();
            observe_timing(time_us_32() - service_started_us,
                           &state.diagnostics.service_count,
                           &state.diagnostics.service_total_us,
                           &state.diagnostics.service_min_us,
                           &state.diagnostics.service_max_us);
        }
        if (nice64_network_rudp_active()) {
            const uint32_t service_started_us = time_us_32();
            nice64_network_rudp_poll(now_ms(), send_reliable_data, NULL);
            observe_timing(time_us_32() - service_started_us,
                           &state.diagnostics.service_count,
                           &state.diagnostics.service_total_us,
                           &state.diagnostics.service_min_us,
                           &state.diagnostics.service_max_us);
        }
    }
    cyw43_arch_lwip_end();
}

bool nice64_udp_service_listening(void) {
    return state.pcb != NULL;
}
