#ifndef NICE64_WIFI_PROVISIONING_H
#define NICE64_WIFI_PROVISIONING_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "nice64_response.h"

#define NICE64_WIFI_SSID_MAX_LENGTH 32u
#define NICE64_WIFI_PASSWORD_MAX_LENGTH 64u

typedef enum {
    WIFI_ADDRESS_DHCP = 0,
} wifi_address_mode_t;

typedef struct {
    char ssid[NICE64_WIFI_SSID_MAX_LENGTH + 1u];
    char password[NICE64_WIFI_PASSWORD_MAX_LENGTH + 1u];
    uint8_t ssid_length;
    uint8_t password_length;
    wifi_address_mode_t address_mode;
    bool ssid_set;
    bool password_set;
    bool address_mode_set;
} wifi_config_t;

typedef enum {
    WIFI_CONNECTION_UNINITIALIZED = 0,
    WIFI_CONNECTION_READY,
    WIFI_CONNECTION_CONFIGURED,
    WIFI_CONNECTION_ASSOCIATING,
    WIFI_CONNECTION_WAITING_FOR_ADDRESS,
    WIFI_CONNECTION_CONNECTED,
    WIFI_CONNECTION_FAILED,
    WIFI_CONNECTION_DISCONNECTED,
} wifi_connection_state_t;

typedef enum {
    WIFI_FAILURE_NONE = 0,
    WIFI_FAILURE_NO_CONFIG,
    WIFI_FAILURE_INVALID_CONFIG,
    WIFI_FAILURE_ASSOCIATION_FAILED,
    WIFI_FAILURE_AUTH_FAILED,
    WIFI_FAILURE_NETWORK_NOT_FOUND,
    WIFI_FAILURE_ASSOCIATION_TIMEOUT,
    WIFI_FAILURE_DHCP_TIMEOUT,
    WIFI_FAILURE_LINK_LOST,
    WIFI_FAILURE_CYW43_INIT_FAILED,
    WIFI_FAILURE_PERSISTENCE_FAILED,
} wifi_failure_t;

typedef enum {
    WIFI_ACTION_NONE = 0,
    WIFI_ACTION_CONNECT,
    WIFI_ACTION_DISCONNECT,
    WIFI_ACTION_CLEAR,
    WIFI_ACTION_FORGET,
} wifi_action_t;

void wifi_provisioning_init(void);
void wifi_provisioning_set_runtime_ready(bool ready);
bool wifi_provisioning_try_handle_line(const char *line,
                                       const nice64_response_sink_t *response);
bool wifi_provisioning_take_action(wifi_action_t *action);
const wifi_config_t *wifi_provisioning_config(void);
bool wifi_provisioning_config_valid(void);
bool wifi_provisioning_restore_config(const wifi_config_t *config);
bool wifi_provisioning_queue_auto_connect(void);
void wifi_provisioning_clear_config(void);
void wifi_provisioning_set_stored(bool stored);
bool wifi_provisioning_stored(void);

void wifi_provisioning_set_state(wifi_connection_state_t state,
                                 wifi_failure_t failure);
wifi_connection_state_t wifi_provisioning_state(void);
wifi_failure_t wifi_provisioning_failure(void);
void wifi_provisioning_set_network(const char *ip,
                                   const char *netmask,
                                   const char *gateway,
                                   const char *dns);
void wifi_provisioning_clear_network(void);

/* Exposed for fixed-buffer host fixtures. */
bool wifi_base64_decode(const char *encoded,
                        uint8_t *decoded,
                        size_t decoded_capacity,
                        size_t *decoded_length);
bool wifi_base64_encode(const uint8_t *data,
                        size_t data_length,
                        char *encoded,
                        size_t encoded_capacity);

const char *wifi_connection_state_name(wifi_connection_state_t state);
const char *wifi_failure_name(wifi_failure_t failure);

#endif
