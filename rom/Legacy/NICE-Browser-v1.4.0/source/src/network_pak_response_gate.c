#include "network_pak_response_gate.h"

#include <string.h>

network_pak_response_disposition_t network_pak_classify_response(
    const nice_net_packet_t *request,
    const nice_net_packet_t *response)
{
    static const char pending[] = "BRIDGE_PENDING";
    nice_net_error_t error = NICE_NET_ERR_NONE;

    if (request == NULL || response == NULL ||
        !nice_net_packet_validate(response, &error))
    {
        return NETWORK_PAK_RESPONSE_INVALID;
    }

    if (response->sequence != request->sequence)
    {
        return NETWORK_PAK_RESPONSE_STALE;
    }

    if (response->command == NICE_NET_CMD_STATUS &&
        response->payload_length == sizeof(pending) - 1u &&
        memcmp(response->payload, pending, sizeof(pending) - 1u) == 0)
    {
        return NETWORK_PAK_RESPONSE_PENDING;
    }

    return NETWORK_PAK_RESPONSE_CURRENT;
}
