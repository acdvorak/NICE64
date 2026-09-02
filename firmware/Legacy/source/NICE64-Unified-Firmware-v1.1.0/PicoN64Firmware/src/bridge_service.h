#ifndef BRIDGE_SERVICE_H
#define BRIDGE_SERVICE_H

#include <stdbool.h>

#include "nice64_response.h"

void bridge_service_poll(void);
bool bridge_service_handle_line(const char *line,
                                const nice64_response_sink_t *sink);

#endif
