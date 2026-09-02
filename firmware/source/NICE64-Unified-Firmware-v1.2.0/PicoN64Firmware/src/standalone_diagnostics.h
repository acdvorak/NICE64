#ifndef STANDALONE_DIAGNOSTICS_H
#define STANDALONE_DIAGNOSTICS_H

#include <stdbool.h>

#include "nice64_response.h"

void standalone_diagnostics_init(void);
void standalone_diagnostics_write_banner(void);
void standalone_diagnostics_core0_service(void);
void standalone_diagnostics_core1_service(void);
bool standalone_diagnostics_handle_line(const char *line,
                                        const nice64_response_sink_t *response);

#endif
