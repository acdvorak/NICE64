#include "network_pak_transport.h"
#include "protocol.h"

#include <joybus_accessory.h>
#include <joypad.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define RESPONSE_QUEUE_CAPACITY 8u

static nice_net_packet_t g_response_queue[RESPONSE_QUEUE_CAPACITY];
static size_t g_response_count;
static size_t g_response_index;
static uint32_t g_read_count;
static uint32_t g_waited_ms;

static void require(bool condition, const char *message)
{
    if (!condition)
    {
        fprintf(stderr, "FAIL %s\n", message);
        exit(1);
    }
}

static void reset_transport(void)
{
    memset(g_response_queue, 0, sizeof(g_response_queue));
    g_response_count = 0u;
    g_response_index = 0u;
    g_read_count = 0u;
    g_waited_ms = 0u;
    network_pak_init();
    network_pak_set_stub_fallback_enabled(false);
}

static void queue_raw_zero(void)
{
    require(g_response_count < RESPONSE_QUEUE_CAPACITY, "response queue capacity");
    memset(&g_response_queue[g_response_count++], 0, sizeof(nice_net_packet_t));
}

static void queue_packet(uint8_t command, uint8_t sequence, const void *payload, size_t length)
{
    require(g_response_count < RESPONSE_QUEUE_CAPACITY, "response queue capacity");
    nice_net_packet_init(&g_response_queue[g_response_count++],
        (nice_net_command_t)command, 0u, sequence, payload, length);
}

static nice_net_packet_t make_request(uint8_t command, uint8_t sequence)
{
    nice_net_packet_t request;
    uint8_t payload[5] = { 7u, 0u, 0u, 0u, 0u };
    const size_t length = command == NICE_NET_CMD_READ_STREAM_CHUNK ? sizeof(payload) : 0u;
    nice_net_packet_init(&request, (nice_net_command_t)command, 0u, sequence, payload, length);
    return request;
}

int joybus_accessory_write(int port, uint16_t addr, const uint8_t *data)
{
    (void)port;
    (void)addr;
    (void)data;
    return JOYBUS_ACCESSORY_IO_STATUS_OK;
}

int joybus_accessory_read(int port, uint16_t addr, uint8_t *data)
{
    (void)port;
    (void)addr;
    g_read_count++;
    if (g_response_index < g_response_count)
    {
        memcpy(data, &g_response_queue[g_response_index++], NICE_NET_WIRE_BLOCK_SIZE);
    }
    else
    {
        memset(data, 0, NICE_NET_WIRE_BLOCK_SIZE);
    }
    return JOYBUS_ACCESSORY_IO_STATUS_OK;
}

uint16_t joypad_get_identifier(joypad_port_t port)
{
    (void)port;
    return 0u;
}

bool joypad_is_connected(joypad_port_t port)
{
    (void)port;
    return true;
}

void wait_ms(unsigned long milliseconds)
{
    g_waited_ms += (uint32_t)milliseconds;
}

static void delayed_current_response_is_accepted(void)
{
    nice_net_packet_t request = make_request(NICE_NET_CMD_READ_STREAM_CHUNK, 77u);
    nice_net_packet_t response;
    nice_net_error_t error = NICE_NET_ERR_NONE;
    network_pak_status_t status;

    reset_transport();
    queue_raw_zero();
    queue_packet(NICE_NET_CMD_STREAM_CHUNK, 76u, "OLD", 3u);
    queue_packet(NICE_NET_CMD_STATUS, 77u, "BRIDGE_PENDING", 14u);
    queue_packet(NICE_NET_CMD_STREAM_CHUNK, 77u, "NOW", 3u);

    require(network_pak_exchange(&request, &response, &error),
        "delayed current response succeeds");
    require(response.command == NICE_NET_CMD_STREAM_CHUNK && response.sequence == 77u,
        "only the current response completes exchange");
    require(g_read_count == 4u, "invalid stale and pending responses were polled past");
    status = network_pak_get_status();
    require(status.invalid_response_count == 1u, "invalid response counted");
    require(status.stale_response_count == 1u, "stale response counted");
    require(status.pending_response_count == 1u, "pending response counted");
}

static void repeated_pending_is_bounded_by_current_response(void)
{
    nice_net_packet_t request = make_request(NICE_NET_CMD_OPEN_STREAM, 18u);
    nice_net_packet_t response;
    nice_net_error_t error = NICE_NET_ERR_NONE;

    reset_transport();
    queue_packet(NICE_NET_CMD_STATUS, 18u, "BRIDGE_PENDING", 14u);
    queue_packet(NICE_NET_CMD_STATUS, 18u, "BRIDGE_PENDING", 14u);
    queue_packet(NICE_NET_CMD_STREAM_OPENED, 18u, "OPEN", 4u);
    require(network_pak_exchange(&request, &response, &error),
        "repeated pending reaches current response");
    require(g_read_count == 3u && response.sequence == 18u,
        "pending snapshots cannot complete exchange");
}

static void sequence_wrap_zero_remains_valid(void)
{
    nice_net_packet_t request = make_request(NICE_NET_CMD_READ_STREAM_CHUNK, 0u);
    nice_net_packet_t response;
    nice_net_error_t error = NICE_NET_ERR_NONE;

    reset_transport();
    queue_packet(NICE_NET_CMD_STREAM_CHUNK, 0u, "WRAP", 4u);
    require(network_pak_exchange(&request, &response, &error),
        "valid sequence zero accepted at wrap");
    require(response.command == NICE_NET_CMD_STREAM_CHUNK && response.sequence == 0u,
        "sequence wrap is distinguished from an invalid all-zero packet");
}

static void invalid_non_bridge_response_fails(void)
{
    nice_net_packet_t request = make_request(NICE_NET_CMD_PING, 4u);
    nice_net_packet_t response;
    nice_net_error_t error = NICE_NET_ERR_NONE;
    network_pak_status_t status;

    reset_transport();
    queue_raw_zero();
    require(!network_pak_exchange(&request, &response, &error),
        "invalid non-bridge response does not escape as success");
    status = network_pak_get_status();
    require(strcmp(status.last_transport_error, "INVALID_RESPONSE") == 0,
        "invalid response has explicit failure classification");
}

static void timeout_then_new_open_starts_clean(void)
{
    nice_net_packet_t failed = make_request(NICE_NET_CMD_READ_STREAM_CHUNK, 33u);
    nice_net_packet_t next = make_request(NICE_NET_CMD_OPEN_STREAM, 34u);
    nice_net_packet_t response;
    nice_net_error_t error = NICE_NET_ERR_NONE;
    network_pak_status_t status;

    reset_transport();
    require(!network_pak_exchange(&failed, &response, &error),
        "permanent invalid snapshots reach bounded timeout");
    status = network_pak_get_status();
    require(status.bridge_wait_timeout_count == 1u,
        "bounded bridge timeout recorded");
    require(g_read_count == 4000u, "existing 20 second poll budget preserved");

    g_response_count = 0u;
    g_response_index = 0u;
    queue_packet(NICE_NET_CMD_STREAM_OPENED, 34u, "NEXT", 4u);
    require(network_pak_exchange(&next, &response, &error),
        "new OPEN succeeds after failed request");
    require(response.sequence == 34u && response.command == NICE_NET_CMD_STREAM_OPENED,
        "failed request cannot complete the next request");
}

int main(void)
{
    delayed_current_response_is_accepted();
    repeated_pending_is_bounded_by_current_response();
    sequence_wrap_zero_remains_valid();
    invalid_non_bridge_response_fails();
    timeout_then_new_open_starts_clean();
    puts("CheckpointECarrierResponseGateSmoke PASS");
    return 0;
}
