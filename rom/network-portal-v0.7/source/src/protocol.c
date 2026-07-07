#include "protocol.h"

#include <string.h>

void nice_net_packet_init(
    nice_net_packet_t *packet,
    nice_net_command_t command,
    uint8_t flags,
    uint8_t sequence,
    const void *payload,
    size_t payload_length)
{
    if (payload_length > NICE_NET_MAX_PAYLOAD)
    {
        payload_length = NICE_NET_MAX_PAYLOAD;
    }

    memset(packet, 0, sizeof(*packet));
    packet->magic = NICE_NET_MAGIC;
    packet->version = NICE_NET_VERSION;
    packet->command = (uint8_t)command;
    packet->flags = flags;
    packet->sequence = sequence;
    packet->payload_length = (uint8_t)payload_length;

    if (payload_length > 0 && payload != NULL)
    {
        memcpy(packet->payload, payload, payload_length);
    }

    packet->checksum = nice_net_checksum(packet);
}

uint16_t nice_net_checksum(const nice_net_packet_t *packet)
{
    uint16_t checksum = 0;

    checksum += (uint8_t)(packet->magic >> 8);
    checksum += (uint8_t)(packet->magic & 0xFF);
    checksum += packet->version;
    checksum += packet->command;
    checksum += packet->flags;
    checksum += packet->sequence;
    checksum += packet->payload_length;

    for (uint8_t index = 0; index < packet->payload_length && index < NICE_NET_MAX_PAYLOAD; index++)
    {
        checksum += packet->payload[index];
    }

    return (uint16_t)~checksum;
}

bool nice_net_packet_validate(const nice_net_packet_t *packet, nice_net_error_t *error)
{
    if (packet->magic != NICE_NET_MAGIC)
    {
        if (error != NULL) *error = NICE_NET_ERR_BAD_MAGIC;
        return false;
    }

    if (packet->version != NICE_NET_VERSION)
    {
        if (error != NULL) *error = NICE_NET_ERR_BAD_VERSION;
        return false;
    }

    if (packet->payload_length > NICE_NET_MAX_PAYLOAD)
    {
        if (error != NULL) *error = NICE_NET_ERR_BAD_LENGTH;
        return false;
    }

    if (packet->checksum != nice_net_checksum(packet))
    {
        if (error != NULL) *error = NICE_NET_ERR_BAD_CHECKSUM;
        return false;
    }

    if (error != NULL) *error = NICE_NET_ERR_NONE;
    return true;
}

const char *nice_net_command_name(uint8_t command)
{
    switch ((nice_net_command_t)command)
    {
    case NICE_NET_CMD_PING: return "PING";
    case NICE_NET_CMD_GET_STATUS: return "GET_STATUS";
    case NICE_NET_CMD_READ_CHUNK: return "READ_CHUNK";
    case NICE_NET_CMD_WRITE_CHUNK: return "WRITE_CHUNK";
    case NICE_NET_CMD_ACK: return "ACK";
    case NICE_NET_CMD_RESET: return "RESET";
    case NICE_NET_CMD_PING_BRIDGE: return "PING_BRIDGE";
    case NICE_NET_CMD_GET_BRIDGE_STATUS: return "GET_BRIDGE_STATUS";
    case NICE_NET_CMD_GET_BRIDGE_TIME: return "GET_BRIDGE_TIME";
    case NICE_NET_CMD_GET_BRIDGE_MESSAGE: return "GET_BRIDGE_MESSAGE";
    case NICE_NET_CMD_SEND_BRIDGE_MESSAGE: return "SEND_BRIDGE_MESSAGE";
    case NICE_NET_CMD_OPEN_STREAM: return "OPEN_STREAM";
    case NICE_NET_CMD_GET_STREAM_INFO: return "GET_STREAM_INFO";
    case NICE_NET_CMD_READ_STREAM_CHUNK: return "READ_STREAM_CHUNK";
    case NICE_NET_CMD_WRITE_STREAM_CHUNK: return "WRITE_STREAM_CHUNK";
    case NICE_NET_CMD_CLOSE_STREAM: return "CLOSE_STREAM";
    case NICE_NET_CMD_PONG: return "PONG";
    case NICE_NET_CMD_STATUS: return "STATUS";
    case NICE_NET_CMD_CHUNK: return "CHUNK";
    case NICE_NET_CMD_BRIDGE_RESPONSE: return "BRIDGE_RESPONSE";
    case NICE_NET_CMD_STREAM_OPENED: return "STREAM_OPENED";
    case NICE_NET_CMD_STREAM_INFO: return "STREAM_INFO";
    case NICE_NET_CMD_STREAM_CHUNK: return "STREAM_CHUNK";
    case NICE_NET_CMD_STREAM_CLOSED: return "STREAM_CLOSED";
    case NICE_NET_CMD_STREAM_WRITE_ACK: return "STREAM_WRITE_ACK";
    case NICE_NET_CMD_ERROR: return "ERROR";
    default: return "UNKNOWN";
    }
}

const char *nice_net_error_name(nice_net_error_t error)
{
    switch (error)
    {
    case NICE_NET_ERR_NONE: return "OK";
    case NICE_NET_ERR_BAD_MAGIC: return "BAD_MAGIC";
    case NICE_NET_ERR_BAD_VERSION: return "BAD_VERSION";
    case NICE_NET_ERR_BAD_LENGTH: return "BAD_LENGTH";
    case NICE_NET_ERR_BAD_CHECKSUM: return "BAD_CHECKSUM";
    case NICE_NET_ERR_UNSUPPORTED_COMMAND: return "UNSUPPORTED_COMMAND";
    case NICE_NET_ERR_TRANSPORT_TIMEOUT: return "TRANSPORT_TIMEOUT";
    case NICE_NET_ERR_TRANSPORT_STUB: return "TRANSPORT_STUB";
    default: return "UNKNOWN_ERROR";
    }
}
