#ifndef BRIDGE_SERVICE_H
#define BRIDGE_SERVICE_H

#include <stdbool.h>

#include "nice64_response.h"
#include "bridge_line_dispatch.h"

void bridge_service_poll(void);
void bridge_service_poll_with_handler(
    bridge_personality_line_handler_fn personality_handler);
bool bridge_service_handle_line(const char *line,
                                const nice64_response_sink_t *sink);

#endif
