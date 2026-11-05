#include "nice64_rudp_codec.h"

#include <string.h>

#include "wireless_transport_contract.h"

static uint16_t read_u16_be(const uint8_t *bytes) {
    return (uint16_t)(((uint16_t)bytes[0] << 8u) | bytes[1]);
}

static uint32_t read_u32_be(const uint8_t *bytes) {
    return ((uint32_t)bytes[0] << 24u) |
           ((uint32_t)bytes[1] << 16u) |
           ((uint32_t)bytes[2] << 8u) |
           (uint32_t)bytes[3];
}

static void write_u16_be(uint8_t *bytes, uint16_t value) {
    bytes[0] = (uint8_t)(value >> 8u);
    bytes[1] = (uint8_t)value;
}

static void write_u32_be(uint8_t *bytes, uint32_t value) {
    bytes[0] = (uint8_t)(value >> 24u);
    bytes[1] = (uint8_t)(value >> 16u);
    bytes[2] = (uint8_t)(value >> 8u);
    bytes[3] = (uint8_t)value;
}

static nice64_rudp_validation_t validate(
    const nice64_rudp_header_t *header,
    size_t actual_payload_length,
    uint16_t maximum_payload_length,
    bool check_session,
    uint32_t expected_session_id) {
    if (header->magic != NICE64_WIRELESS_MAGIC) {
        return NICE64_RUDP_BAD_MAGIC;
    }
    if (header->version != NICE64_WIRELESS_TRANSPORT_VERSION) {
        return NICE64_RUDP_UNSUPPORTED_VERSION;
    }
    if (header->header_length != NICE64_WIRELESS_HEADER_LENGTH) {
        return NICE64_RUDP_INVALID_HEADER_LENGTH;
    }
    if (header->flags == 0u ||
        (header->flags & NICE64_RUDP_UNKNOWN_FLAGS_MASK) != 0u ||
        ((header->flags & NICE64_RUDP_FLAG_DATA) == 0u &&
         header->payload_length != 0u)) {
        return NICE64_RUDP_INVALID_FLAGS;
    }
    const nice64_delivery_selection_result_t delivery =
        nice64_wireless_validate_delivery_class(header->delivery_class);
    if (delivery == NICE64_DELIVERY_SELECTION_UNSUPPORTED) {
        return NICE64_RUDP_UNSUPPORTED_DELIVERY_CLASS;
    }
    if (delivery == NICE64_DELIVERY_SELECTION_INVALID) {
        return NICE64_RUDP_INVALID_DELIVERY_CLASS;
    }
    if (header->payload_length > maximum_payload_length) {
        return NICE64_RUDP_PAYLOAD_TOO_LARGE;
    }
    if ((size_t)header->payload_length != actual_payload_length) {
        return actual_payload_length < (size_t)header->payload_length
            ? NICE64_RUDP_TRUNCATED_PAYLOAD
            : NICE64_RUDP_PAYLOAD_LENGTH_MISMATCH;
    }
    if (check_session && header->session_id != expected_session_id) {
        return NICE64_RUDP_SESSION_MISMATCH;
    }
    if (header->header_crc_or_reserved != 0u) {
        return NICE64_RUDP_RESERVED_FIELD_NONZERO;
    }
    return NICE64_RUDP_VALID;
}

nice64_rudp_validation_t nice64_rudp_decode(
    const uint8_t *wire,
    size_t wire_length,
    uint16_t maximum_payload_length,
    bool check_session,
    uint32_t expected_session_id,
    nice64_rudp_header_t *header) {
    if (wire == NULL || header == NULL ||
        wire_length < NICE64_WIRELESS_HEADER_LENGTH) {
        return NICE64_RUDP_TRUNCATED_HEADER;
    }

    header->magic = read_u32_be(&wire[NICE64_WIRELESS_OFFSET_MAGIC]);
    header->version = wire[NICE64_WIRELESS_OFFSET_VERSION];
    header->flags = wire[NICE64_WIRELESS_OFFSET_FLAGS];
    header->delivery_class = wire[NICE64_WIRELESS_OFFSET_DELIVERY_CLASS];
    header->header_length = wire[NICE64_WIRELESS_OFFSET_HEADER_LENGTH];
    header->session_id = read_u32_be(&wire[NICE64_WIRELESS_OFFSET_SESSION_ID]);
    header->sequence = read_u32_be(&wire[NICE64_WIRELESS_OFFSET_SEQUENCE]);
    header->ack_base = read_u32_be(&wire[NICE64_WIRELESS_OFFSET_ACK_BASE]);
    header->ack_bitmap = read_u32_be(&wire[NICE64_WIRELESS_OFFSET_ACK_BITMAP]);
    header->payload_length = read_u16_be(&wire[NICE64_WIRELESS_OFFSET_PAYLOAD_LENGTH]);
    header->header_crc_or_reserved =
        read_u16_be(&wire[NICE64_WIRELESS_OFFSET_HEADER_CRC_OR_RESERVED]);

    return validate(header,
                    wire_length - NICE64_WIRELESS_HEADER_LENGTH,
                    maximum_payload_length,
                    check_session,
                    expected_session_id);
}

nice64_rudp_validation_t nice64_rudp_encode(
    const nice64_rudp_header_t *header,
    const uint8_t *payload,
    uint16_t maximum_payload_length,
    uint8_t *wire,
    size_t wire_capacity,
    size_t *wire_length) {
    if (header == NULL || wire == NULL || wire_length == NULL ||
        (header->payload_length != 0u && payload == NULL)) {
        return NICE64_RUDP_PAYLOAD_LENGTH_MISMATCH;
    }
    const nice64_rudp_validation_t result =
        validate(header, header->payload_length, maximum_payload_length,
                 false, 0u);
    const size_t required = NICE64_WIRELESS_HEADER_LENGTH +
                            (size_t)header->payload_length;
    if (result != NICE64_RUDP_VALID) {
        return result;
    }
    if (wire_capacity < required) {
        return NICE64_RUDP_TRUNCATED_PAYLOAD;
    }

    write_u32_be(&wire[NICE64_WIRELESS_OFFSET_MAGIC], header->magic);
    wire[NICE64_WIRELESS_OFFSET_VERSION] = header->version;
    wire[NICE64_WIRELESS_OFFSET_FLAGS] = header->flags;
    wire[NICE64_WIRELESS_OFFSET_DELIVERY_CLASS] = header->delivery_class;
    wire[NICE64_WIRELESS_OFFSET_HEADER_LENGTH] = header->header_length;
    write_u32_be(&wire[NICE64_WIRELESS_OFFSET_SESSION_ID], header->session_id);
    write_u32_be(&wire[NICE64_WIRELESS_OFFSET_SEQUENCE], header->sequence);
    write_u32_be(&wire[NICE64_WIRELESS_OFFSET_ACK_BASE], header->ack_base);
    write_u32_be(&wire[NICE64_WIRELESS_OFFSET_ACK_BITMAP], header->ack_bitmap);
    write_u16_be(&wire[NICE64_WIRELESS_OFFSET_PAYLOAD_LENGTH], header->payload_length);
    write_u16_be(&wire[NICE64_WIRELESS_OFFSET_HEADER_CRC_OR_RESERVED],
                 header->header_crc_or_reserved);
    if (header->payload_length != 0u) {
        memcpy(&wire[NICE64_WIRELESS_HEADER_LENGTH], payload,
               header->payload_length);
    }
    *wire_length = required;
    return NICE64_RUDP_VALID;
}
