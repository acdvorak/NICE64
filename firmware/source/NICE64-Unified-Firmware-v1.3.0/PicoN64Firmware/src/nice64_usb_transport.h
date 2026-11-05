#ifndef NICE64_USB_TRANSPORT_H
#define NICE64_USB_TRANSPORT_H

#include "nice64_line_framer.h"
#include "nice64_response.h"

const nice64_response_sink_t *nice64_usb_response_sink(void);
void nice64_usb_poll_lines(nice64_line_framer_t *framer,
                           nice64_line_ready_fn on_line,
                           void *context);

#endif
