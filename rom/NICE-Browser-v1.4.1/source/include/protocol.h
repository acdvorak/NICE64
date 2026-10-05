#ifndef NICE_NETWORK_PAK_PROTOCOL_H
#define NICE_NETWORK_PAK_PROTOCOL_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define NICE_NET_MAGIC 0x4E50u /* "NP" */
#define NICE_NET_VERSION 0x01u
#define NICE_NET_WIRE_BLOCK_SIZE 32u
#define NICE_NET_MAX_PAYLOAD 23u
#define NICE_NET_STREAM_CHUNK_SIZE 16u

typedef enum nice_net_command_e
{
    NICE_NET_CMD_PING = 0x01,
    NICE_NET_CMD_GET_STATUS = 0x02,
    NICE_NET_CMD_READ_CHUNK = 0x03,
    NICE_NET_CMD_WRITE_CHUNK = 0x04,
    NICE_NET_CMD_ACK = 0x05,
    NICE_NET_CMD_RESET = 0x06,
    NICE_NET_CMD_PING_BRIDGE = 0x10,
    NICE_NET_CMD_GET_BRIDGE_STATUS = 0x11,
    NICE_NET_CMD_GET_BRIDGE_TIME = 0x12,
    NICE_NET_CMD_GET_BRIDGE_MESSAGE = 0x13,
    NICE_NET_CMD_SEND_BRIDGE_MESSAGE = 0x14,
    NICE_NET_CMD_OPEN_STREAM = 0x20,
    NICE_NET_CMD_GET_STREAM_INFO = 0x21,
    NICE_NET_CMD_READ_STREAM_CHUNK = 0x22,
    NICE_NET_CMD_WRITE_STREAM_CHUNK = 0x23,
    NICE_NET_CMD_CLOSE_STREAM = 0x24,
    NICE_NET_CMD_PONG = 0x81,
    NICE_NET_CMD_STATUS = 0x82,
    NICE_NET_CMD_CHUNK = 0x83,
    NICE_NET_CMD_BRIDGE_RESPONSE = 0x90,
    NICE_NET_CMD_STREAM_OPENED = 0xA0,
    NICE_NET_CMD_STREAM_INFO = 0xA1,
    NICE_NET_CMD_STREAM_CHUNK = 0xA2,
    NICE_NET_CMD_STREAM_CLOSED = 0xA3,
    NICE_NET_CMD_STREAM_WRITE_ACK = 0xA4,
    NICE_NET_CMD_BULK_READY = 0xA5,
    NICE_NET_CMD_ERROR = 0xFF
} nice_net_command_t;

typedef enum nice_net_error_e
{
    NICE_NET_ERR_NONE = 0,
    NICE_NET_ERR_BAD_MAGIC = 1,
    NICE_NET_ERR_BAD_VERSION = 2,
    NICE_NET_ERR_BAD_LENGTH = 3,
    NICE_NET_ERR_BAD_CHECKSUM = 4,
    NICE_NET_ERR_UNSUPPORTED_COMMAND = 5,
    NICE_NET_ERR_TRANSPORT_TIMEOUT = 6,
    NICE_NET_ERR_TRANSPORT_STUB = 7
} nice_net_error_t;

typedef struct __attribute__((packed)) nice_net_packet_s
{
    uint16_t magic;
    uint8_t version;
    uint8_t command;
    uint8_t flags;
    uint8_t sequence;
    uint8_t payload_length;
    uint8_t payload[NICE_NET_MAX_PAYLOAD];
    uint16_t checksum;
} nice_net_packet_t;

_Static_assert(sizeof(nice_net_packet_t) == NICE_NET_WIRE_BLOCK_SIZE, "NICE packet must fit one Joybus accessory block");

void nice_net_packet_init(
    nice_net_packet_t *packet,
    nice_net_command_t command,
    uint8_t flags,
    uint8_t sequence,
    const void *payload,
    size_t payload_length);

uint16_t nice_net_checksum(const nice_net_packet_t *packet);
bool nice_net_packet_validate(const nice_net_packet_t *packet, nice_net_error_t *error);
const char *nice_net_command_name(uint8_t command);
const char *nice_net_error_name(nice_net_error_t error);

#endif
