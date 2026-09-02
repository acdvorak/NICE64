#include "wifi_provisioning.h"

#include <string.h>

#define WIFI_IPV4_TEXT_LENGTH 16u
#define WIFI_SSID_BASE64_LENGTH 45u

typedef struct {
    wifi_config_t config;
    wifi_connection_state_t state;
    wifi_failure_t failure;
    wifi_action_t pending_action;
    bool runtime_ready;
    bool stored;
    char ip[WIFI_IPV4_TEXT_LENGTH];
    char netmask[WIFI_IPV4_TEXT_LENGTH];
    char gateway[WIFI_IPV4_TEXT_LENGTH];
    char dns[WIFI_IPV4_TEXT_LENGTH];
} wifi_provisioning_context_t;

static wifi_provisioning_context_t context;

static void secure_zero(void *data, size_t length) {
    volatile uint8_t *cursor = data;
    while (length-- > 0u) {
        *cursor++ = 0u;
    }
}

static int base64_value(char value) {
    if (value >= 'A' && value <= 'Z') return value - 'A';
    if (value >= 'a' && value <= 'z') return value - 'a' + 26;
    if (value >= '0' && value <= '9') return value - '0' + 52;
    if (value == '+') return 62;
    if (value == '/') return 63;
    return -1;
}

bool wifi_base64_decode(const char *encoded,
                        uint8_t *decoded,
                        size_t decoded_capacity,
                        size_t *decoded_length) {
    if (encoded == NULL || decoded == NULL || decoded_length == NULL) {
        return false;
    }

    const size_t length = strlen(encoded);
    if ((length % 4u) != 0u) {
        return false;
    }

    size_t output = 0u;
    for (size_t index = 0u; index < length; index += 4u) {
        const bool final_group = index + 4u == length;
        const int a = base64_value(encoded[index]);
        const int b = base64_value(encoded[index + 1u]);
        const bool pad_c = encoded[index + 2u] == '=';
        const bool pad_d = encoded[index + 3u] == '=';
        const int c = pad_c ? 0 : base64_value(encoded[index + 2u]);
        const int d = pad_d ? 0 : base64_value(encoded[index + 3u]);

        if (a < 0 || b < 0 || c < 0 || d < 0 ||
            (!final_group && (pad_c || pad_d)) ||
            (pad_c && !pad_d) ||
            (pad_c && (b & 0x0f) != 0) ||
            (!pad_c && pad_d && (c & 0x03) != 0)) {
            return false;
        }

        const size_t bytes = pad_c ? 1u : (pad_d ? 2u : 3u);
        if (output + bytes > decoded_capacity) {
            return false;
        }

        decoded[output++] = (uint8_t)((a << 2) | (b >> 4));
        if (!pad_c) {
            decoded[output++] = (uint8_t)((b << 4) | (c >> 2));
        }
        if (!pad_d) {
            decoded[output++] = (uint8_t)((c << 6) | d);
        }
    }

    *decoded_length = output;
    return true;
}

bool wifi_base64_encode(const uint8_t *data,
                        size_t data_length,
                        char *encoded,
                        size_t encoded_capacity) {
    static const char alphabet[] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    if ((data == NULL && data_length != 0u) || encoded == NULL) {
        return false;
    }

    const size_t required = ((data_length + 2u) / 3u) * 4u;
    if (required + 1u > encoded_capacity) {
        return false;
    }

    size_t input = 0u;
    size_t output = 0u;
    while (input < data_length) {
        const size_t remaining = data_length - input;
        const uint32_t a = data[input++];
        const uint32_t b = remaining > 1u ? data[input++] : 0u;
        const uint32_t c = remaining > 2u ? data[input++] : 0u;
        encoded[output++] = alphabet[a >> 2];
        encoded[output++] = alphabet[((a & 0x03u) << 4) | (b >> 4)];
        encoded[output++] = remaining > 1u
            ? alphabet[((b & 0x0fu) << 2) | (c >> 6)] : '=';
        encoded[output++] = remaining > 2u ? alphabet[c & 0x3fu] : '=';
    }
    encoded[output] = '\0';
    return true;
}

static void copy_ipv4_text(char destination[WIFI_IPV4_TEXT_LENGTH],
                           const char *source) {
    const char *value = source != NULL ? source : "0.0.0.0";
    strncpy(destination, value, WIFI_IPV4_TEXT_LENGTH - 1u);
    destination[WIFI_IPV4_TEXT_LENGTH - 1u] = '\0';
}

void wifi_provisioning_clear_network(void) {
    copy_ipv4_text(context.ip, NULL);
    copy_ipv4_text(context.netmask, NULL);
    copy_ipv4_text(context.gateway, NULL);
    copy_ipv4_text(context.dns, NULL);
}

void wifi_provisioning_init(void) {
    secure_zero(&context, sizeof(context));
    context.config.address_mode = WIFI_ADDRESS_DHCP;
    context.state = WIFI_CONNECTION_UNINITIALIZED;
    wifi_provisioning_clear_network();
}

void wifi_provisioning_set_runtime_ready(bool ready) {
    context.runtime_ready = ready;
    if (ready) {
        context.state = wifi_provisioning_config_valid()
            ? WIFI_CONNECTION_CONFIGURED : WIFI_CONNECTION_READY;
        context.failure = WIFI_FAILURE_NONE;
    } else {
        context.state = WIFI_CONNECTION_FAILED;
        context.failure = WIFI_FAILURE_CYW43_INIT_FAILED;
    }
}

bool wifi_provisioning_config_valid(void) {
    return context.config.ssid_set && context.config.ssid_length > 0u &&
           context.config.password_set && context.config.address_mode_set &&
           context.config.address_mode == WIFI_ADDRESS_DHCP;
}

const wifi_config_t *wifi_provisioning_config(void) {
    return &context.config;
}

bool wifi_provisioning_restore_config(const wifi_config_t *config) {
    if (config == NULL || config->ssid_length == 0u ||
        config->ssid_length > NICE64_WIFI_SSID_MAX_LENGTH ||
        config->password_length > NICE64_WIFI_PASSWORD_MAX_LENGTH ||
        config->address_mode != WIFI_ADDRESS_DHCP || !config->ssid_set ||
        !config->password_set || !config->address_mode_set) {
        return false;
    }
    context.config = *config;
    context.config.ssid[context.config.ssid_length] = '\0';
    context.config.password[context.config.password_length] = '\0';
    context.stored = true;
    return true;
}

bool wifi_provisioning_queue_auto_connect(void) {
    if (!context.runtime_ready || !wifi_provisioning_config_valid() ||
        context.pending_action != WIFI_ACTION_NONE) {
        return false;
    }
    context.pending_action = WIFI_ACTION_CONNECT;
    context.state = WIFI_CONNECTION_ASSOCIATING;
    context.failure = WIFI_FAILURE_NONE;
    wifi_provisioning_clear_network();
    return true;
}

void wifi_provisioning_clear_config(void) {
    secure_zero(&context.config, sizeof(context.config));
    context.config.address_mode = WIFI_ADDRESS_DHCP;
}

void wifi_provisioning_set_stored(bool stored) {
    context.stored = stored;
}

bool wifi_provisioning_stored(void) {
    return context.stored;
}

bool wifi_provisioning_take_action(wifi_action_t *action) {
    if (action == NULL || context.pending_action == WIFI_ACTION_NONE) {
        return false;
    }
    *action = context.pending_action;
    context.pending_action = WIFI_ACTION_NONE;
    return true;
}

void wifi_provisioning_set_state(wifi_connection_state_t state,
                                 wifi_failure_t failure) {
    context.state = state;
    context.failure = failure;
}

wifi_connection_state_t wifi_provisioning_state(void) {
    return context.state;
}

wifi_failure_t wifi_provisioning_failure(void) {
    return context.failure;
}

void wifi_provisioning_set_network(const char *ip,
                                   const char *netmask,
                                   const char *gateway,
                                   const char *dns) {
    copy_ipv4_text(context.ip, ip);
    copy_ipv4_text(context.netmask, netmask);
    copy_ipv4_text(context.gateway, gateway);
    copy_ipv4_text(context.dns, dns);
}

const char *wifi_connection_state_name(wifi_connection_state_t state) {
    switch (state) {
    case WIFI_CONNECTION_READY: return "READY";
    case WIFI_CONNECTION_CONFIGURED: return "CONFIGURED";
    case WIFI_CONNECTION_ASSOCIATING: return "ASSOCIATING";
    case WIFI_CONNECTION_WAITING_FOR_ADDRESS: return "WAITING_FOR_ADDRESS";
    case WIFI_CONNECTION_CONNECTED: return "CONNECTED";
    case WIFI_CONNECTION_FAILED: return "FAILED";
    case WIFI_CONNECTION_DISCONNECTED: return "DISCONNECTED";
    default: return "UNINITIALIZED";
    }
}

const char *wifi_failure_name(wifi_failure_t failure) {
    switch (failure) {
    case WIFI_FAILURE_NO_CONFIG: return "NO_CONFIG";
    case WIFI_FAILURE_INVALID_CONFIG: return "INVALID_CONFIG";
    case WIFI_FAILURE_ASSOCIATION_FAILED: return "ASSOCIATION_FAILED";
    case WIFI_FAILURE_AUTH_FAILED: return "AUTH_FAILED";
    case WIFI_FAILURE_NETWORK_NOT_FOUND: return "NETWORK_NOT_FOUND";
    case WIFI_FAILURE_ASSOCIATION_TIMEOUT: return "ASSOCIATION_TIMEOUT";
    case WIFI_FAILURE_DHCP_TIMEOUT: return "DHCP_TIMEOUT";
    case WIFI_FAILURE_LINK_LOST: return "LINK_LOST";
    case WIFI_FAILURE_CYW43_INIT_FAILED: return "CYW43_INIT_FAILED";
    case WIFI_FAILURE_PERSISTENCE_FAILED: return "PERSISTENCE_FAILED";
    default: return "NONE";
    }
}

static bool config_mutation_allowed(void) {
    return context.pending_action == WIFI_ACTION_NONE &&
           context.state != WIFI_CONNECTION_ASSOCIATING &&
           context.state != WIFI_CONNECTION_WAITING_FOR_ADDRESS &&
           context.state != WIFI_CONNECTION_CONNECTED;
}

static bool contains_nul(const uint8_t *data, size_t length) {
    for (size_t index = 0u; index < length; ++index) {
        if (data[index] == 0u) return true;
    }
    return false;
}

static void refresh_configured_state(void) {
    if (!context.runtime_ready) {
        context.state = WIFI_CONNECTION_FAILED;
        context.failure = WIFI_FAILURE_CYW43_INIT_FAILED;
        return;
    }
    context.failure = WIFI_FAILURE_NONE;
    context.state = wifi_provisioning_config_valid()
        ? WIFI_CONNECTION_CONFIGURED : WIFI_CONNECTION_READY;
}

static void handle_encoded_config(const char *encoded,
                                  bool password,
                                  const nice64_response_sink_t *response) {
    uint8_t decoded[NICE64_WIFI_PASSWORD_MAX_LENGTH + 1u];
    size_t decoded_length = 0u;
    const size_t maximum = password
        ? NICE64_WIFI_PASSWORD_MAX_LENGTH : NICE64_WIFI_SSID_MAX_LENGTH;

    if (!config_mutation_allowed()) {
        nice64_response_puts(response, "WIFI_CONFIG|ERR|BUSY");
        return;
    }
    if (!wifi_base64_decode(encoded, decoded, maximum, &decoded_length)) {
        nice64_response_puts(response, "WIFI_CONFIG|ERR|INVALID_BASE64_OR_LENGTH");
        return;
    }
    if (contains_nul(decoded, decoded_length)) {
        nice64_response_puts(response, "WIFI_CONFIG|ERR|EMBEDDED_NUL");
        secure_zero(decoded, sizeof(decoded));
        return;
    }

    if (password) {
        if (decoded_length > 0u && decoded_length < 8u) {
            nice64_response_puts(response, "WIFI_CONFIG|ERR|PASSWORD_LENGTH");
            secure_zero(decoded, sizeof(decoded));
            return;
        }
        secure_zero(context.config.password, sizeof(context.config.password));
        memcpy(context.config.password, decoded, decoded_length);
        context.config.password[decoded_length] = '\0';
        context.config.password_length = (uint8_t)decoded_length;
        context.config.password_set = true;
        secure_zero(decoded, sizeof(decoded));
        refresh_configured_state();
        nice64_response_puts(response, "WIFI_CONFIG|OK|PASSWORD");
        return;
    }

    if (decoded_length == 0u) {
        nice64_response_puts(response, "WIFI_CONFIG|ERR|SSID_LENGTH");
        return;
    }
    secure_zero(context.config.ssid, sizeof(context.config.ssid));
    memcpy(context.config.ssid, decoded, decoded_length);
    context.config.ssid[decoded_length] = '\0';
    context.config.ssid_length = (uint8_t)decoded_length;
    context.config.ssid_set = true;
    secure_zero(decoded, sizeof(decoded));
    refresh_configured_state();
    nice64_response_puts(response, "WIFI_CONFIG|OK|SSID");
}

static void write_status(const nice64_response_sink_t *response) {
    char encoded_ssid[WIFI_SSID_BASE64_LENGTH];
    encoded_ssid[0] = '\0';
    if (context.config.ssid_set) {
        wifi_base64_encode((const uint8_t *)context.config.ssid,
                           context.config.ssid_length,
                           encoded_ssid,
                           sizeof(encoded_ssid));
    }
    const char *link = "DOWN";
    if (context.state == WIFI_CONNECTION_ASSOCIATING) {
        link = "ASSOCIATING";
    } else if (context.state == WIFI_CONNECTION_WAITING_FOR_ADDRESS) {
        link = "UP_NO_ADDRESS";
    } else if (context.state == WIFI_CONNECTION_CONNECTED) {
        link = "UP";
    }
    nice64_response_printf(
        response,
        "WIFI|STATE=%s|MODE=DHCP|SSID_B64=%s|STORED=%u|LINK=%s|IP=%s|NETMASK=%s|GW=%s|DNS=%s|FAIL=%s\n",
        wifi_connection_state_name(context.state),
        encoded_ssid,
        context.stored ? 1u : 0u,
        link,
        context.ip,
        context.netmask,
        context.gateway,
        context.dns,
        wifi_failure_name(context.failure));
    nice64_response_flush(response);
}

bool wifi_provisioning_try_handle_line(const char *line,
                                       const nice64_response_sink_t *response) {
    if (line == NULL || response == NULL || response->write == NULL) {
        return false;
    }

    if (strcmp(line, "WIFI?") == 0 || strcmp(line, "WIFI_STATUS?") == 0) {
        write_status(response);
        return true;
    }
    if (strncmp(line, "WIFI_CONFIG|SSID_B64|", 21u) == 0) {
        handle_encoded_config(&line[21], false, response);
        return true;
    }
    if (strncmp(line, "WIFI_CONFIG|PASSWORD_B64|", 25u) == 0) {
        handle_encoded_config(&line[25], true, response);
        return true;
    }
    if (strcmp(line, "WIFI_CONFIG|MODE|DHCP") == 0) {
        if (!config_mutation_allowed()) {
            nice64_response_puts(response, "WIFI_CONFIG|ERR|BUSY");
        } else {
            context.config.address_mode = WIFI_ADDRESS_DHCP;
            context.config.address_mode_set = true;
            refresh_configured_state();
            nice64_response_puts(response, "WIFI_CONFIG|OK|MODE=DHCP");
        }
        return true;
    }
    if (strcmp(line, "WIFI_CONFIG|MODE|STATIC") == 0 ||
        strncmp(line, "WIFI_CONFIG|IP|", 15u) == 0 ||
        strncmp(line, "WIFI_CONFIG|NETMASK|", 20u) == 0 ||
        strncmp(line, "WIFI_CONFIG|GATEWAY|", 20u) == 0 ||
        strncmp(line, "WIFI_CONFIG|DNS|", 16u) == 0) {
        nice64_response_puts(response, "WIFI_CONFIG|ERR|STATIC_DEFERRED");
        return true;
    }
    if (strcmp(line, "WIFI_CONNECT") == 0) {
        if (!context.runtime_ready) {
            nice64_response_puts(response, "WIFI|CONNECT|ERR|CYW43_INIT_FAILED");
        } else if (!wifi_provisioning_config_valid()) {
            context.failure = WIFI_FAILURE_NO_CONFIG;
            nice64_response_puts(response, "WIFI|CONNECT|ERR|NO_CONFIG");
        } else if (context.pending_action != WIFI_ACTION_NONE ||
                   context.state == WIFI_CONNECTION_ASSOCIATING ||
                   context.state == WIFI_CONNECTION_WAITING_FOR_ADDRESS ||
                   context.state == WIFI_CONNECTION_CONNECTED) {
            nice64_response_puts(response, "WIFI|CONNECT|ERR|BUSY");
        } else {
            context.pending_action = WIFI_ACTION_CONNECT;
            context.state = WIFI_CONNECTION_ASSOCIATING;
            context.failure = WIFI_FAILURE_NONE;
            wifi_provisioning_clear_network();
            nice64_response_puts(response, "WIFI|CONNECT|ACCEPTED");
        }
        return true;
    }
    if (strcmp(line, "WIFI_DISCONNECT") == 0) {
        if (context.pending_action != WIFI_ACTION_NONE) {
            nice64_response_puts(response, "WIFI|DISCONNECT|ERR|BUSY");
        } else if (!context.runtime_ready) {
            nice64_response_puts(response, "WIFI|DISCONNECT|OK|NO_LINK");
        } else {
            context.pending_action = WIFI_ACTION_DISCONNECT;
            context.state = WIFI_CONNECTION_DISCONNECTED;
            context.failure = WIFI_FAILURE_NONE;
            wifi_provisioning_clear_network();
            nice64_response_puts(response, "WIFI|DISCONNECT|ACCEPTED");
        }
        return true;
    }
    if (strcmp(line, "WIFI_CLEAR") == 0) {
        if (context.pending_action != WIFI_ACTION_NONE) {
            nice64_response_puts(response, "WIFI|CLEAR|ERR|BUSY");
        } else {
            context.pending_action = context.runtime_ready
                ? WIFI_ACTION_CLEAR : WIFI_ACTION_NONE;
            wifi_provisioning_clear_config();
            context.state = context.runtime_ready
                ? WIFI_CONNECTION_READY : WIFI_CONNECTION_FAILED;
            context.failure = context.runtime_ready
                ? WIFI_FAILURE_NONE : WIFI_FAILURE_CYW43_INIT_FAILED;
            wifi_provisioning_clear_network();
            nice64_response_puts(response, "WIFI|CLEAR|ACCEPTED");
        }
        return true;
    }
    if (strcmp(line, "WIFI_FORGET") == 0) {
        if (context.pending_action != WIFI_ACTION_NONE) {
            nice64_response_puts(response, "WIFI|FORGET|ERR|BUSY");
        } else {
            context.pending_action = WIFI_ACTION_FORGET;
            nice64_response_puts(response, "WIFI|FORGET|ACCEPTED");
        }
        return true;
    }
    if (strncmp(line, "WIFI_CONFIG|", 12u) == 0) {
        nice64_response_puts(response, "WIFI_CONFIG|ERR|SYNTAX");
        return true;
    }
    if (strncmp(line, "WIFI", 4u) == 0) {
        nice64_response_puts(response, "WIFI|ERR|UNKNOWN_COMMAND");
        return true;
    }
    return false;
}
