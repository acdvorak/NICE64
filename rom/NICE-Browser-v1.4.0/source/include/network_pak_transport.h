#ifndef NICE_NETWORK_PAK_TRANSPORT_H
#define NICE_NETWORK_PAK_TRANSPORT_H

#include "protocol.h"

#include <stdbool.h>
#include <stdint.h>

typedef struct network_pak_status_s
{
    bool host_ready;
    bool connected;
    bool using_stub;
    const char *nic_name;
    const char *last_transport_error;
    uint16_t port_identifier;
    uint8_t port_status;
    int last_write_status;
    int last_read_status;
    int first_read_status;
    uint8_t read_attempts;
    uint8_t read_retry_successes;
    uint8_t read_bad_crc_count;
    uint8_t read_transport_error_count;
    uint8_t last_read_crc_wire;
    uint8_t last_read_crc_calc;
    uint8_t last_raw_response[NICE_NET_WIRE_BLOCK_SIZE];
    uint32_t packets_tx;
    uint32_t packets_rx;
    uint32_t errors;
    uint32_t bridge_poll_count;
    uint32_t bridge_wait_timeout_count;
} network_pak_status_t;

void network_pak_init(void);
bool network_pak_exchange(const nice_net_packet_t *request, nice_net_packet_t *response, nice_net_error_t *error);
void network_pak_set_max_read_retries(uint8_t max_read_retries);
void network_pak_set_stub_fallback_enabled(bool enabled);
void network_pak_detect_port(void);
network_pak_status_t network_pak_get_status(void);

#endif
