#ifndef KEYBOARD_MOUSE_SERVICE_H
#define KEYBOARD_MOUSE_SERVICE_H

#include <stdbool.h>

#include "nice64_response.h"

bool keyboard_mouse_service_handle_line(
    const char *line, const nice64_response_sink_t *response);

#endif
