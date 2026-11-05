#include "nice64_tcp_service.h"

#include <string.h>

#include "lwip/err.h"
#include "lwip/pbuf.h"
#include "lwip/tcp.h"
#include "nice64_line_framer.h"
#include "pico/cyw43_arch.h"
#include "wifi_service.h"

#define NICE64_TCP_LINE_CAPACITY 600u
#define NICE64_TCP_TX_CAPACITY 2048u
#define NICE64_TCP_POLL_INTERVAL 2u

typedef struct {
    struct tcp_pcb *listener;
    struct tcp_pcb *client;
    nice64_line_framer_t framer;
    char line[NICE64_TCP_LINE_CAPACITY];
    char tx[NICE64_TCP_TX_CAPACITY];
    size_t tx_length;
    bool close_requested;
    bridge_personality_line_handler_fn personality_handler;
} nice64_tcp_state_t;

static nice64_tcp_state_t tcp_state;
static void tcp_try_send(void);

static void tcp_sink_write(void *context, const char *data, size_t length) {
    nice64_tcp_state_t *state = context;
    if (state == NULL || state->client == NULL || data == NULL || length == 0u) return;
    if (length > sizeof(state->tx) - state->tx_length) {
        state->close_requested = true;
        return;
    }
    memcpy(&state->tx[state->tx_length], data, length);
    state->tx_length += length;
}

static void tcp_sink_flush(void *context) {
    (void)context;
    tcp_try_send();
}

static nice64_response_sink_t tcp_sink = {
    .write = tcp_sink_write,
    .flush = tcp_sink_flush,
    .context = &tcp_state,
};

static void reset_client_state(void) {
    tcp_state.client = NULL;
    tcp_state.tx_length = 0u;
    tcp_state.close_requested = false;
    nice64_line_framer_init(&tcp_state.framer, tcp_state.line, sizeof(tcp_state.line));
}

static void tcp_try_send(void) {
    if (tcp_state.client == NULL || tcp_state.tx_length == 0u) return;
    const u16_t available = tcp_sndbuf(tcp_state.client);
    const size_t length = tcp_state.tx_length < available ? tcp_state.tx_length : (size_t)available;
    if (length == 0u) return;
    const err_t result = tcp_write(tcp_state.client, tcp_state.tx, (u16_t)length, TCP_WRITE_FLAG_COPY);
    if (result != ERR_OK) {
        if (result != ERR_MEM) tcp_state.close_requested = true;
        return;
    }
    if (length < tcp_state.tx_length) {
        memmove(tcp_state.tx, &tcp_state.tx[length], tcp_state.tx_length - length);
    }
    tcp_state.tx_length -= length;
    tcp_output(tcp_state.client);
}

static void dispatch_tcp_line(void *context, const char *line) {
    (void)context;
    bridge_line_dispatch(line, &tcp_sink, tcp_state.personality_handler);
}

static err_t tcp_sent_callback(void *arg, struct tcp_pcb *pcb, u16_t length) {
    (void)arg; (void)pcb; (void)length;
    tcp_try_send();
    return ERR_OK;
}

static void tcp_error_callback(void *arg, err_t error) {
    (void)arg; (void)error;
    reset_client_state();
}

static err_t tcp_receive_callback(void *arg, struct tcp_pcb *pcb, struct pbuf *packet, err_t error) {
    (void)arg;
    if (packet == NULL) {
        tcp_state.client = NULL;
        tcp_close(pcb);
        reset_client_state();
        return ERR_OK;
    }
    if (error != ERR_OK) {
        pbuf_free(packet);
        tcp_state.close_requested = true;
        return error;
    }
    for (struct pbuf *part = packet; part != NULL; part = part->next) {
        const char *bytes = part->payload;
        for (u16_t i = 0u; i < part->len; ++i) {
            nice64_line_framer_feed(&tcp_state.framer, bytes[i], dispatch_tcp_line, NULL);
        }
    }
    tcp_recved(pcb, packet->tot_len);
    pbuf_free(packet);
    tcp_try_send();
    return ERR_OK;
}

static err_t tcp_poll_callback(void *arg, struct tcp_pcb *pcb) {
    (void)arg;
    if (tcp_state.close_requested) {
        tcp_abort(pcb);
        reset_client_state();
        return ERR_ABRT;
    }
    tcp_try_send();
    return ERR_OK;
}

static err_t tcp_accept_callback(void *arg, struct tcp_pcb *client, err_t error) {
    (void)arg;
    if (error != ERR_OK || client == NULL) return ERR_VAL;
    if (tcp_state.client != NULL) {
        static const char busy[] = "NICE64|BUSY\n";
        tcp_write(client, busy, sizeof(busy) - 1u, TCP_WRITE_FLAG_COPY);
        tcp_output(client);
        if (tcp_close(client) != ERR_OK) {
            tcp_abort(client);
        }
        return ERR_OK;
    }
    tcp_state.client = client;
    tcp_state.tx_length = 0u;
    tcp_state.close_requested = false;
    nice64_line_framer_init(&tcp_state.framer, tcp_state.line, sizeof(tcp_state.line));
    tcp_arg(client, &tcp_state);
    tcp_recv(client, tcp_receive_callback);
    tcp_sent(client, tcp_sent_callback);
    tcp_err(client, tcp_error_callback);
    tcp_poll(client, tcp_poll_callback, NICE64_TCP_POLL_INTERVAL);
    return ERR_OK;
}

static void create_listener(void) {
    struct tcp_pcb *listener = tcp_new_ip_type(IPADDR_TYPE_V4);
    if (listener == NULL) return;
    if (tcp_bind(listener, IP_ANY_TYPE, NICE64_TCP_PORT) != ERR_OK) {
        tcp_abort(listener);
        return;
    }
    listener = tcp_listen_with_backlog(listener, 1u);
    if (listener == NULL) return;
    tcp_state.listener = listener;
    tcp_accept(listener, tcp_accept_callback);
}

static void stop_service(void) {
    if (tcp_state.client != NULL) {
        struct tcp_pcb *client = tcp_state.client;
        reset_client_state();
        tcp_abort(client);
    }
    if (tcp_state.listener != NULL) {
        struct tcp_pcb *listener = tcp_state.listener;
        tcp_state.listener = NULL;
        tcp_close(listener);
    }
}

void nice64_tcp_service_init(void) {
    memset(&tcp_state, 0, sizeof(tcp_state));
    nice64_line_framer_init(&tcp_state.framer, tcp_state.line, sizeof(tcp_state.line));
}

void nice64_tcp_service_poll(bridge_personality_line_handler_fn personality_handler) {
    tcp_state.personality_handler = personality_handler;
    cyw43_arch_lwip_begin();
    if (!wifi_service_connected()) {
        stop_service();
    } else {
        if (tcp_state.listener == NULL) create_listener();
        if (tcp_state.close_requested && tcp_state.client != NULL) {
            struct tcp_pcb *client = tcp_state.client;
            reset_client_state();
            tcp_abort(client);
        } else {
            tcp_try_send();
        }
    }
    cyw43_arch_lwip_end();
}

bool nice64_tcp_session_active(void) { return tcp_state.client != NULL; }
bool nice64_tcp_is_response_sink(const nice64_response_sink_t *sink) { return sink == &tcp_sink; }
const nice64_response_sink_t *nice64_tcp_response_sink(void) { return tcp_state.client == NULL ? NULL : &tcp_sink; }

bool nice64_tcp_usb_command_allowed(const char *line) {
    if (!nice64_tcp_session_active() || line == NULL) return true;
    return strncmp(line, "WIFI", 4u) == 0 || strcmp(line, "WHO") == 0 ||
           strcmp(line, "MODE?") == 0 || strcmp(line, "CAPS?") == 0 ||
           strcmp(line, "BOOTREQ?") == 0;
}
