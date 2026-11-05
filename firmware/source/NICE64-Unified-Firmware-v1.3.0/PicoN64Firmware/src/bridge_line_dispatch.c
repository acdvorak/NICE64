#include "bridge_line_dispatch.h"

#include "control_plane.h"
#include "wifi_provisioning.h"

bool bridge_line_dispatch(const char *line,
                          const nice64_response_sink_t *response,
                          bridge_personality_line_handler_fn personality_handler) {
    if (line == NULL || response == NULL || response->write == NULL) {
        return false;
    }

    if (control_plane_try_handle_line(line, response)) {
        return true;
    }

    if (wifi_provisioning_try_handle_line(line, response)) {
        return true;
    }

    return personality_handler != NULL && personality_handler(line, response);
}
