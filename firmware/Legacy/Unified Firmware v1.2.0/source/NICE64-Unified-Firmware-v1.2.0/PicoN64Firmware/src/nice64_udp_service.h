#ifndef NICE64_UDP_SERVICE_H
#define NICE64_UDP_SERVICE_H

#include <stdbool.h>
#include <stdint.h>

#define NICE64_RUDPB_UDP_PORT 6465u
#define NICE64_RUDPB_TEST_PAYLOAD_MAX 512u
#define NICE64_RUDPB_RX_SLOT_COUNT 2u
#define NICE64_RUDPB_TX_SLOT_COUNT 1u
#define NICE64_RUDPB_OWNER_TIMEOUT_MS 30000u

/* Core1-only reliable carrier service. Network Pak integration terminates at
 * the existing protected application boundary; no Core0 network work occurs. */
void nice64_udp_service_init(void);
void nice64_udp_service_poll(void);
bool nice64_udp_service_listening(void);
bool nice64_udp_service_observability_active(void);
void nice64_udp_service_observe_wifi_poll(uint32_t duration_us);

#endif
