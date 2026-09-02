#ifndef NICE_NETWORK_PAK_RESPONSE_GATE_H
#define NICE_NETWORK_PAK_RESPONSE_GATE_H

#include "protocol.h"

typedef enum network_pak_response_disposition_e
{
    NETWORK_PAK_RESPONSE_INVALID = 0,
    NETWORK_PAK_RESPONSE_PENDING,
    NETWORK_PAK_RESPONSE_STALE,
    NETWORK_PAK_RESPONSE_CURRENT
} network_pak_response_disposition_t;

network_pak_response_disposition_t network_pak_classify_response(
    const nice_net_packet_t *request,
    const nice_net_packet_t *response);

#endif
