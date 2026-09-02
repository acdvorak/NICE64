#ifndef NICE64_WIFI_SERVICE_H
#define NICE64_WIFI_SERVICE_H

#include <stdbool.h>

typedef enum {
    WIFI_SERVICE_UNINITIALIZED = 0,
    WIFI_SERVICE_READY,
    WIFI_SERVICE_INIT_FAILED,
} wifi_service_state_t;

/* Core1-only API. Neither function may be called from Core0 or an IRQ. */
void wifi_service_init(void);
void wifi_service_poll(void);
wifi_service_state_t wifi_service_state(void);
bool wifi_service_connected(void);

#endif
