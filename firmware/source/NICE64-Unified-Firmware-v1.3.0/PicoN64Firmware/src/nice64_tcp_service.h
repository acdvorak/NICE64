#ifndef NICE64_TCP_SERVICE_H
#define NICE64_TCP_SERVICE_H

#include <stdbool.h>

#include "bridge_line_dispatch.h"
#include "nice64_response.h"

#define NICE64_TCP_PORT 6464u

void nice64_tcp_service_init(void);
void nice64_tcp_service_poll(bridge_personality_line_handler_fn personality_handler);
bool nice64_tcp_session_active(void);
bool nice64_tcp_is_response_sink(const nice64_response_sink_t *sink);
const nice64_response_sink_t *nice64_tcp_response_sink(void);
bool nice64_tcp_usb_command_allowed(const char *line);

#endif
