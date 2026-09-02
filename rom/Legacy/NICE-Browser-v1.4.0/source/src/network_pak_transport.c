#include "network_pak_transport.h"
#include "network_pak_response_gate.h"

#include <joybus.h>
#include <joybus_accessory.h>
#include <joypad.h>
#include <n64sys.h>
#include <string.h>

#define NETWORK_PAK_PORT 1
#define NETWORK_PAK_ADDR_COMMAND 0x8000u
#define NETWORK_PAK_ADDR_RESPONSE 0x8020u
#define BROWSER_RESOURCE_WAIT_MS 20000u
#define BROWSER_RESOURCE_POLL_INTERVAL_MS 5u

static network_pak_status_t g_status;
static uint8_t g_max_read_retries;
static bool g_stub_fallback_enabled;

static bool network_pak_is_bridge_command(uint8_t command)
{
    return (command >= NICE_NET_CMD_PING_BRIDGE && command <= NICE_NET_CMD_SEND_BRIDGE_MESSAGE)
        || (command >= NICE_NET_CMD_OPEN_STREAM && command <= NICE_NET_CMD_CLOSE_STREAM);
}

static uint8_t network_pak_data_crc(const uint8_t *data)
{
    unsigned int crc = 0;

    for (size_t i = 0; i < NICE_NET_WIRE_BLOCK_SIZE; i++)
    {
        unsigned int x = crc ^ data[i];
        crc = 0;
        if (x & 0x80) { crc ^= 0x89; }
        if (x & 0x40) { crc ^= 0x86; }
        if (x & 0x20) { crc ^= 0x43; }
        if (x & 0x10) { crc ^= 0xE3; }
        if (x & 0x08) { crc ^= 0xB3; }
        if (x & 0x04) { crc ^= 0x9B; }
        if (x & 0x02) { crc ^= 0x8F; }
        if (x & 0x01) { crc ^= 0x85; }
    }

    return crc;
}

static int network_pak_read_raw(uint16_t addr, uint8_t *data, uint8_t *wire_crc, uint8_t *calc_crc)
{
    int status = joybus_accessory_read(NETWORK_PAK_PORT, addr, data);

    *calc_crc = network_pak_data_crc(data);
    *wire_crc = status == JOYBUS_ACCESSORY_IO_STATUS_OK ? *calc_crc : 0;

    return status;
}

static const char *accessory_io_status_name(int status)
{
    switch (status)
    {
    case JOYBUS_ACCESSORY_IO_STATUS_OK: return "OK";
    case JOYBUS_ACCESSORY_IO_STATUS_NO_DEVICE: return "NO_DEVICE";
    case JOYBUS_ACCESSORY_IO_STATUS_NO_PAK: return "NO_PAK";
    case JOYBUS_ACCESSORY_IO_STATUS_BAD_CRC: return "BAD_CRC";
    default: return "UNKNOWN_IO";
    }
}

static bool network_pak_stub_exchange(const nice_net_packet_t *request, nice_net_packet_t *response, nice_net_error_t *error)
{
    switch ((nice_net_command_t)request->command)
    {
    case NICE_NET_CMD_PING:
        nice_net_packet_init(response, NICE_NET_CMD_PONG, 0, request->sequence, "PONG", 4);
        break;

    case NICE_NET_CMD_GET_STATUS:
        nice_net_packet_init(response, NICE_NET_CMD_STATUS, 0, request->sequence, "HOST_READY NIC=AUTO", 19);
        break;

    case NICE_NET_CMD_READ_CHUNK:
        nice_net_packet_init(response, NICE_NET_CMD_CHUNK, 0, request->sequence, "hello from bridge", 17);
        break;

    case NICE_NET_CMD_WRITE_CHUNK:
        nice_net_packet_init(response, NICE_NET_CMD_ACK, 0, request->sequence, "ACK", 3);
        break;

    case NICE_NET_CMD_RESET:
        g_status.packets_tx = 0;
        g_status.packets_rx = 0;
        g_status.errors = 0;
        nice_net_packet_init(response, NICE_NET_CMD_ACK, 0, request->sequence, "RESET", 5);
        break;

    default:
        g_status.errors++;
        nice_net_packet_init(response, NICE_NET_CMD_ERROR, 0, request->sequence, "UNSUPPORTED", 11);
        if (error != NULL) *error = NICE_NET_ERR_UNSUPPORTED_COMMAND;
        return false;
    }

    if (error != NULL) *error = NICE_NET_ERR_NONE;
    return true;
}

void network_pak_init(void)
{
    memset(&g_status, 0, sizeof(g_status));
    g_max_read_retries = 0;
    g_stub_fallback_enabled = true;
    g_status.host_ready = true;
    g_status.connected = true;
    g_status.using_stub = false;
    g_status.nic_name = "PORT2/PAK";
    g_status.last_transport_error = "BOOT";
    g_status.last_write_status = 0;
    g_status.last_read_status = 0;
    g_status.first_read_status = 0;
    g_status.read_attempts = 0;
    g_status.read_retry_successes = 0;
    g_status.read_bad_crc_count = 0;
    g_status.read_transport_error_count = 0;
    g_status.last_read_crc_wire = 0;
    g_status.last_read_crc_calc = 0;
}

network_pak_status_t network_pak_get_status(void)
{
    return g_status;
}

void network_pak_set_max_read_retries(uint8_t max_read_retries)
{
    g_max_read_retries = max_read_retries > 2 ? 2 : max_read_retries;
}

void network_pak_set_stub_fallback_enabled(bool enabled)
{
    g_stub_fallback_enabled = enabled;
}

void network_pak_detect_port(void)
{
    g_status.port_identifier = joypad_get_identifier((joypad_port_t)NETWORK_PAK_PORT);
    g_status.port_status = joypad_is_connected((joypad_port_t)NETWORK_PAK_PORT) ? 1 : 0;
}

bool network_pak_exchange(const nice_net_packet_t *request, nice_net_packet_t *response, nice_net_error_t *error)
{
    nice_net_error_t validation_error = NICE_NET_ERR_NONE;
    uint8_t response_block[NICE_NET_WIRE_BLOCK_SIZE];
    uint8_t wire_crc = 0;
    uint8_t calc_crc = 0;
    int io_status;
    bool bridge_wait_timeout = false;
    bool response_accepted = false;
    network_pak_response_disposition_t response_disposition = NETWORK_PAK_RESPONSE_INVALID;
    g_status.packets_tx++;
    memset(response_block, 0xEE, sizeof(response_block));
    g_status.first_read_status = 0;
    g_status.read_attempts = 0;
    g_status.read_retry_successes = 0;
    g_status.read_bad_crc_count = 0;
    g_status.read_transport_error_count = 0;

    if (!nice_net_packet_validate(request, &validation_error))
    {
        g_status.errors++;
        if (error != NULL) *error = validation_error;
        return false;
    }

    io_status = joybus_accessory_write(NETWORK_PAK_PORT, NETWORK_PAK_ADDR_COMMAND, (const uint8_t *)request);
    g_status.last_write_status = io_status;
    if (io_status == JOYBUS_ACCESSORY_IO_STATUS_OK || io_status == JOYBUS_ACCESSORY_IO_STATUS_BAD_CRC)
    {
        wait_ms(1);
        const uint16_t max_polls = network_pak_is_bridge_command(request->command)
            ? (BROWSER_RESOURCE_WAIT_MS / BROWSER_RESOURCE_POLL_INTERVAL_MS)
            : 1u;
        for (uint16_t poll = 0; poll < max_polls; poll++)
        {
            for (uint8_t attempt = 0; attempt <= g_max_read_retries; attempt++)
            {
                if (attempt > 0)
                {
                    wait_ms(1);
                }

                io_status = network_pak_read_raw(NETWORK_PAK_ADDR_RESPONSE, response_block, &wire_crc, &calc_crc);
                if (poll == 0 && attempt == 0)
                {
                    g_status.first_read_status = io_status;
                }

                g_status.read_attempts = (uint8_t)(attempt + 1);
                g_status.last_read_status = io_status;
                g_status.last_read_crc_wire = wire_crc;
                g_status.last_read_crc_calc = calc_crc;
                memcpy(g_status.last_raw_response, response_block, sizeof(g_status.last_raw_response));
                memcpy(response, response_block, sizeof(*response));

                if (io_status == JOYBUS_ACCESSORY_IO_STATUS_OK)
                {
                    if (attempt > 0)
                    {
                        g_status.read_retry_successes++;
                    }
                    break;
                }

                if (io_status == JOYBUS_ACCESSORY_IO_STATUS_BAD_CRC)
                {
                    g_status.read_bad_crc_count++;
                }
                else
                {
                    g_status.read_transport_error_count++;
                }
            }

            if (io_status != JOYBUS_ACCESSORY_IO_STATUS_OK)
            {
                break;
            }

            response_disposition = network_pak_classify_response(request, response);
            if (response_disposition == NETWORK_PAK_RESPONSE_CURRENT)
            {
                response_accepted = true;
                break;
            }

            if (response_disposition == NETWORK_PAK_RESPONSE_INVALID)
            {
                g_status.invalid_response_count++;
            }
            else if (response_disposition == NETWORK_PAK_RESPONSE_STALE)
            {
                g_status.stale_response_count++;
            }
            else
            {
                g_status.pending_response_count++;
            }

            if (!network_pak_is_bridge_command(request->command))
            {
                break;
            }

            g_status.bridge_poll_count++;
            if (poll + 1u >= max_polls)
            {
                bridge_wait_timeout = true;
                g_status.bridge_wait_timeout_count++;
                break;
            }
            wait_ms(BROWSER_RESOURCE_POLL_INTERVAL_MS);
        }

        if (g_status.last_read_status == JOYBUS_ACCESSORY_IO_STATUS_OK &&
            response_accepted && !bridge_wait_timeout)
        {
            g_status.connected = true;
            g_status.using_stub = false;
            g_status.nic_name = "PORT2/PAK";
            g_status.last_transport_error = g_status.last_write_status == JOYBUS_ACCESSORY_IO_STATUS_OK
                ? "OK"
                : "WRITE_CRC_IGN";
            g_status.packets_rx++;
            if (error != NULL) *error = NICE_NET_ERR_NONE;
            return true;
        }

        if (bridge_wait_timeout)
        {
            g_status.last_transport_error = "BRIDGE_TIMEOUT";
        }
        else if (g_status.last_read_status != JOYBUS_ACCESSORY_IO_STATUS_OK)
        {
            g_status.last_transport_error = accessory_io_status_name(g_status.last_read_status);
        }
        else if (response_disposition == NETWORK_PAK_RESPONSE_STALE)
        {
            g_status.last_transport_error = "STALE_RESPONSE";
        }
        else
        {
            g_status.last_transport_error = "INVALID_RESPONSE";
        }
    }
    else
    {
        g_status.last_read_status = 0;
        g_status.read_transport_error_count++;
        g_status.last_transport_error = accessory_io_status_name(io_status);
    }

    g_status.errors++;
    g_status.connected = false;
    g_status.using_stub = false;
    g_status.nic_name = "PORT2/PAK";

    if (!g_stub_fallback_enabled)
    {
        if (error != NULL) *error = NICE_NET_ERR_TRANSPORT_TIMEOUT;
        return false;
    }

    g_status.using_stub = true;
    g_status.nic_name = "STUB/AUTO";
    if (!network_pak_stub_exchange(request, response, error))
    {
        return false;
    }

    g_status.packets_rx++;
    return true;
}
