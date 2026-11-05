#ifndef NICE64_RUDP_CODEC_H
#define NICE64_RUDP_CODEC_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define NICE64_RUDP_FLAG_DATA 0x01u
#define NICE64_RUDP_FLAG_ACK 0x02u
#define NICE64_RUDP_KNOWN_FLAGS \
    (NICE64_RUDP_FLAG_DATA | NICE64_RUDP_FLAG_ACK)
#define NICE64_RUDP_UNKNOWN_FLAGS_MASK 0xFCu

typedef struct {
    uint32_t magic;
    uint8_t version;
    uint8_t flags;
    uint8_t delivery_class;
    uint8_t header_length;
    uint32_t session_id;
    uint32_t sequence;
    uint32_t ack_base;
    uint32_t ack_bitmap;
    uint16_t payload_length;
    uint16_t header_crc_or_reserved;
} nice64_rudp_header_t;

typedef enum {
    NICE64_RUDP_VALID = 0,
    NICE64_RUDP_TRUNCATED_HEADER,
    NICE64_RUDP_BAD_MAGIC,
    NICE64_RUDP_UNSUPPORTED_VERSION,
    NICE64_RUDP_INVALID_HEADER_LENGTH,
    NICE64_RUDP_INVALID_FLAGS,
    NICE64_RUDP_UNSUPPORTED_DELIVERY_CLASS,
    NICE64_RUDP_INVALID_DELIVERY_CLASS,
    NICE64_RUDP_PAYLOAD_TOO_LARGE,
    NICE64_RUDP_TRUNCATED_PAYLOAD,
    NICE64_RUDP_PAYLOAD_LENGTH_MISMATCH,
    NICE64_RUDP_SESSION_MISMATCH,
    NICE64_RUDP_RESERVED_FIELD_NONZERO,
} nice64_rudp_validation_t;

nice64_rudp_validation_t nice64_rudp_decode(
    const uint8_t *wire,
    size_t wire_length,
    uint16_t maximum_payload_length,
    bool check_session,
    uint32_t expected_session_id,
    nice64_rudp_header_t *header);

nice64_rudp_validation_t nice64_rudp_encode(
    const nice64_rudp_header_t *header,
    const uint8_t *payload,
    uint16_t maximum_payload_length,
    uint8_t *wire,
    size_t wire_capacity,
    size_t *wire_length);

#endif
