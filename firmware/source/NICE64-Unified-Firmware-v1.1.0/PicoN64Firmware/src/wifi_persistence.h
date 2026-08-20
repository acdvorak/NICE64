#ifndef NICE64_WIFI_PERSISTENCE_H
#define NICE64_WIFI_PERSISTENCE_H

#include <stdbool.h>

#include "wifi_provisioning.h"

#define NICE64_WIFI_PERSIST_RESERVED_BYTES 8192u

typedef enum {
    WIFI_PERSISTENCE_OK = 0,
    WIFI_PERSISTENCE_UNCHANGED,
    WIFI_PERSISTENCE_NOT_AVAILABLE,
    WIFI_PERSISTENCE_UNSAFE_CONTEXT,
    WIFI_PERSISTENCE_IMAGE_OVERLAP,
    WIFI_PERSISTENCE_FLASH_ERROR,
    WIFI_PERSISTENCE_VERIFY_ERROR,
} wifi_persistence_result_t;

/* Core0 setup only; no erase/program is performed here. */
bool wifi_persistence_prepare_core0_lockout(void);

/* All remaining functions are Core1-owned. */
void wifi_persistence_init(void);
bool wifi_persistence_restore(wifi_config_t *config);
bool wifi_persistence_has_stored_config(void);
wifi_persistence_result_t wifi_persistence_commit_validated(
    const wifi_config_t *config);
wifi_persistence_result_t wifi_persistence_forget(void);
const char *wifi_persistence_result_name(wifi_persistence_result_t result);

#endif
