#ifndef CONTROL_PLANE_H
#define CONTROL_PLANE_H

#include <stdbool.h>

#include "nice64_response.h"

bool control_plane_try_handle_line(const char *line,
                                   const nice64_response_sink_t *response);

#endif
