#ifndef BRIDGE_LINE_DISPATCH_H
#define BRIDGE_LINE_DISPATCH_H

#include <stdbool.h>

#include "nice64_response.h"

typedef bool (*bridge_personality_line_handler_fn)(
    const char *line,
    const nice64_response_sink_t *response);

bool bridge_line_dispatch(const char *line,
                          const nice64_response_sink_t *response,
                          bridge_personality_line_handler_fn personality_handler);

#endif
