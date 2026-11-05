#include "wifi_service.h"

#include <stdint.h>

#include "cyw43.h"
#include "lwip/dns.h"
#include "lwip/ip4_addr.h"
#include "lwip/netif.h"
#include "pico/cyw43_arch.h"
#include "pico/time.h"
#include "wifi_persistence.h"
#include "wifi_provisioning.h"
#include "wireless_transport_contract.h"

#define WIFI_ASSOCIATION_TIMEOUT_MS 20000u
#define WIFI_DHCP_TIMEOUT_MS 15000u
#define WIFI_IPV4_TEXT_LENGTH 16u

static wifi_service_state_t service_state = WIFI_SERVICE_UNINITIALIZED;
static bool sta_enabled;
static uint32_t association_started_ms;
static uint32_t address_wait_started_ms;

static uint32_t now_ms(void) {
    return to_ms_since_boot(get_absolute_time());
}

static bool timeout_reached(uint32_t start_ms, uint32_t timeout_ms) {
    return (uint32_t)(now_ms() - start_ms) >= timeout_ms;
}

static void disable_station(void) {
    if (sta_enabled) {
        cyw43_arch_disable_sta_mode();
        sta_enabled = false;
    }
    wifi_provisioning_clear_network();
}

static void fail_attempt(wifi_failure_t failure) {
    disable_station();
    wifi_provisioning_set_state(WIFI_CONNECTION_FAILED, failure);
}

static void begin_connect(void) {
    const wifi_config_t *config = wifi_provisioning_config();
    if (!wifi_provisioning_config_valid()) {
        wifi_provisioning_set_state(WIFI_CONNECTION_FAILED,
                                    WIFI_FAILURE_INVALID_CONFIG);
        return;
    }

    disable_station();
    cyw43_arch_enable_sta_mode();
    sta_enabled = true;

    const uint32_t auth = config->password_length == 0u
        ? CYW43_AUTH_OPEN : CYW43_AUTH_WPA2_AES_PSK;
    const int result = cyw43_arch_wifi_connect_async(
        config->ssid,
        config->password_length == 0u ? NULL : config->password,
        auth);
    if (result != 0) {
        fail_attempt(WIFI_FAILURE_ASSOCIATION_FAILED);
        return;
    }

    association_started_ms = now_ms();
    address_wait_started_ms = 0u;
    wifi_provisioning_set_state(WIFI_CONNECTION_ASSOCIATING,
                                WIFI_FAILURE_NONE);
}

static void service_action(void) {
    wifi_action_t action;
    if (!wifi_provisioning_take_action(&action)) {
        return;
    }

    switch (action) {
    case WIFI_ACTION_CONNECT:
        begin_connect();
        break;
    case WIFI_ACTION_DISCONNECT:
        disable_station();
        wifi_provisioning_set_state(WIFI_CONNECTION_DISCONNECTED,
                                    WIFI_FAILURE_NONE);
        break;
    case WIFI_ACTION_CLEAR:
        disable_station();
        wifi_provisioning_set_state(WIFI_CONNECTION_READY,
                                    WIFI_FAILURE_NONE);
        break;
    case WIFI_ACTION_FORGET: {
        disable_station();
        const wifi_persistence_result_t result = wifi_persistence_forget();
        wifi_provisioning_clear_config();
        if (result == WIFI_PERSISTENCE_OK ||
            result == WIFI_PERSISTENCE_UNCHANGED) {
            wifi_provisioning_set_stored(false);
            wifi_provisioning_set_state(
                service_state == WIFI_SERVICE_READY
                    ? WIFI_CONNECTION_READY : WIFI_CONNECTION_FAILED,
                service_state == WIFI_SERVICE_READY
                    ? WIFI_FAILURE_NONE : WIFI_FAILURE_CYW43_INIT_FAILED);
        } else {
            wifi_provisioning_set_state(WIFI_CONNECTION_FAILED,
                                        WIFI_FAILURE_PERSISTENCE_FAILED);
        }
        break;
    }
    default:
        break;
    }
}

static void update_network_snapshot(void) {
    struct netif *netif = &cyw43_state.netif[CYW43_ITF_STA];
    char ip[WIFI_IPV4_TEXT_LENGTH];
    char netmask[WIFI_IPV4_TEXT_LENGTH];
    char gateway[WIFI_IPV4_TEXT_LENGTH];
    char dns[WIFI_IPV4_TEXT_LENGTH];

    ip4addr_ntoa_r(netif_ip4_addr(netif), ip, sizeof(ip));
    ip4addr_ntoa_r(netif_ip4_netmask(netif), netmask, sizeof(netmask));
    ip4addr_ntoa_r(netif_ip4_gw(netif), gateway, sizeof(gateway));
    ipaddr_ntoa_r(dns_getserver(0), dns, sizeof(dns));
    wifi_provisioning_set_network(ip, netmask, gateway, dns);
}

static void service_connection_state(void) {
    const wifi_connection_state_t state = wifi_provisioning_state();
    if (!sta_enabled ||
        (state != WIFI_CONNECTION_ASSOCIATING &&
         state != WIFI_CONNECTION_WAITING_FOR_ADDRESS &&
         state != WIFI_CONNECTION_CONNECTED)) {
        return;
    }

    const int link = cyw43_tcpip_link_status(&cyw43_state, CYW43_ITF_STA);
    if (link == CYW43_LINK_UP) {
        update_network_snapshot();
        if (state != WIFI_CONNECTION_CONNECTED) {
            const wifi_persistence_result_t result =
                wifi_persistence_commit_validated(
                    wifi_provisioning_config());
            if (result == WIFI_PERSISTENCE_OK ||
                result == WIFI_PERSISTENCE_UNCHANGED) {
                wifi_provisioning_set_stored(true);
                wifi_provisioning_set_state(WIFI_CONNECTION_CONNECTED,
                                            WIFI_FAILURE_NONE);
            } else {
                wifi_provisioning_set_state(WIFI_CONNECTION_CONNECTED,
                                            WIFI_FAILURE_PERSISTENCE_FAILED);
            }
        }
        return;
    }
    if (link == CYW43_LINK_BADAUTH) {
        fail_attempt(WIFI_FAILURE_AUTH_FAILED);
        return;
    }
    if (link == CYW43_LINK_NONET) {
        fail_attempt(WIFI_FAILURE_NETWORK_NOT_FOUND);
        return;
    }
    if (link == CYW43_LINK_FAIL) {
        fail_attempt(WIFI_FAILURE_ASSOCIATION_FAILED);
        return;
    }

    if (state == WIFI_CONNECTION_CONNECTED) {
        fail_attempt(WIFI_FAILURE_LINK_LOST);
        return;
    }

    if (link == CYW43_LINK_NOIP) {
        if (state != WIFI_CONNECTION_WAITING_FOR_ADDRESS) {
            address_wait_started_ms = now_ms();
            wifi_provisioning_set_state(WIFI_CONNECTION_WAITING_FOR_ADDRESS,
                                        WIFI_FAILURE_NONE);
        } else if (timeout_reached(address_wait_started_ms,
                                   WIFI_DHCP_TIMEOUT_MS)) {
            fail_attempt(WIFI_FAILURE_DHCP_TIMEOUT);
        }
        return;
    }

    if (timeout_reached(association_started_ms,
                        WIFI_ASSOCIATION_TIMEOUT_MS)) {
        fail_attempt(WIFI_FAILURE_ASSOCIATION_TIMEOUT);
    }
}

void wifi_service_init(void) {
    if (service_state != WIFI_SERVICE_UNINITIALIZED) {
        return;
    }

    wifi_provisioning_init();
    wifi_persistence_init();
    wifi_config_t restored_config;
    const bool restored = wifi_persistence_restore(&restored_config) &&
                          wifi_provisioning_restore_config(&restored_config);
    if (cyw43_arch_init() == 0) {
        service_state = WIFI_SERVICE_READY;
        wifi_provisioning_set_runtime_ready(true);
        if (restored) {
            wifi_provisioning_queue_auto_connect();
        }
    } else {
        service_state = WIFI_SERVICE_INIT_FAILED;
        wifi_provisioning_set_runtime_ready(false);
    }
}

void wifi_service_poll(void) {
    if (service_state == WIFI_SERVICE_READY) {
        cyw43_arch_poll();
        service_action();
        service_connection_state();
    } else if (service_state == WIFI_SERVICE_INIT_FAILED) {
        /* WIFI_FORGET remains available over USB after CYW43 init failure. */
        service_action();
    }
}

wifi_service_state_t wifi_service_state(void) {
    return service_state;
}

bool wifi_service_connected(void) {
    return service_state == WIFI_SERVICE_READY &&
           wifi_provisioning_state() == WIFI_CONNECTION_CONNECTED;
}
