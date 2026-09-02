#ifndef NICE64_WIRELESS_TRANSPORT_CONTRACT_H
#define NICE64_WIRELESS_TRANSPORT_CONTRACT_H

#include <stdbool.h>
#include <stdint.h>

/* RUDP-0 contract reservation only. There is no UDP PCB, packet parser,
 * reliability engine, or latest-state delivery in this checkpoint. */
#define NICE64_WIRELESS_MAGIC 0x4E363455u /* network-order bytes: "N64U" */
#define NICE64_WIRELESS_TRANSPORT_VERSION 1u
#define NICE64_WIRELESS_HEADER_LENGTH 28u
#define NICE64_WIRELESS_OFFSET_MAGIC 0u
#define NICE64_WIRELESS_OFFSET_VERSION 4u
#define NICE64_WIRELESS_OFFSET_FLAGS 5u
#define NICE64_WIRELESS_OFFSET_DELIVERY_CLASS 6u
#define NICE64_WIRELESS_OFFSET_HEADER_LENGTH 7u
#define NICE64_WIRELESS_OFFSET_SESSION_ID 8u
#define NICE64_WIRELESS_OFFSET_SEQUENCE 12u
#define NICE64_WIRELESS_OFFSET_ACK_BASE 16u
#define NICE64_WIRELESS_OFFSET_ACK_BITMAP 20u
#define NICE64_WIRELESS_OFFSET_PAYLOAD_LENGTH 24u
#define NICE64_WIRELESS_OFFSET_HEADER_CRC_OR_RESERVED 26u

typedef enum {
    NICE64_DELIVERY_RELIABLE_ORDERED = 0u,
    NICE64_DELIVERY_LATEST_STATE = 1u
} nice64_delivery_class_t;

#define NICE64_WIRELESS_CAP_RELIABLE_ORDERED (1u << 0)
#define NICE64_WIRELESS_CAP_LATEST_STATE (1u << 1)
#define NICE64_WIRELESS_RESERVED_DELIVERY_CAPABILITIES \
    (NICE64_WIRELESS_CAP_RELIABLE_ORDERED | NICE64_WIRELESS_CAP_LATEST_STATE)

/* RELIABLE_ORDERED is the only selectable class for the future carrier.
 * No RUDP service exists yet, so no wireless transport capability is
 * advertised at RUDP-0. */
#define NICE64_WIRELESS_SELECTABLE_DELIVERY_CAPABILITIES \
    NICE64_WIRELESS_CAP_RELIABLE_ORDERED
#define NICE64_WIRELESS_ADVERTISED_TRANSPORT_CAPABILITIES 0u
#define NICE64_WIRELESS_ACTIVE_DELIVERY_CLASS \
    NICE64_DELIVERY_RELIABLE_ORDERED

typedef enum {
    NICE64_DELIVERY_SELECTION_ACCEPTED = 0u,
    NICE64_DELIVERY_SELECTION_UNSUPPORTED = 1u,
    NICE64_DELIVERY_SELECTION_INVALID = 2u
} nice64_delivery_selection_result_t;

static inline nice64_delivery_selection_result_t
nice64_wireless_validate_delivery_class(uint8_t requested_channel) {
    if (requested_channel == (uint8_t)NICE64_DELIVERY_RELIABLE_ORDERED) {
        return NICE64_DELIVERY_SELECTION_ACCEPTED;
    }
    if (requested_channel == (uint8_t)NICE64_DELIVERY_LATEST_STATE) {
        return NICE64_DELIVERY_SELECTION_UNSUPPORTED;
    }
    return NICE64_DELIVERY_SELECTION_INVALID;
}

static inline bool
nice64_wireless_channel_may_enter_reliable_path(uint8_t channel) {
    return nice64_wireless_validate_delivery_class(channel) ==
           NICE64_DELIVERY_SELECTION_ACCEPTED;
}

_Static_assert(NICE64_DELIVERY_RELIABLE_ORDERED == 0u,
               "RELIABLE_ORDERED wire value changed");
_Static_assert(NICE64_DELIVERY_LATEST_STATE == 1u,
               "LATEST_STATE reserved wire value changed");
_Static_assert(NICE64_WIRELESS_HEADER_LENGTH == 28u,
               "reserved wireless header length changed");

#endif
