#include "nice64_usb_transport.h"

#include "pico/stdlib.h"
#include "pico/stdio.h"

static void usb_write(void *context, const char *data, size_t length) {
    (void)context;
    stdio_put_string(data, (int)length, false, true);
}

static void usb_flush(void *context) {
    (void)context;
    stdio_flush();
}

static const nice64_response_sink_t usb_sink = {
    .write = usb_write,
    .flush = usb_flush,
    .context = NULL,
};

const nice64_response_sink_t *nice64_usb_response_sink(void) {
    return &usb_sink;
}

void nice64_usb_poll_lines(nice64_line_framer_t *framer,
                           nice64_line_ready_fn on_line,
                           void *context) {
    int value;
    while ((value = getchar_timeout_us(0)) != PICO_ERROR_TIMEOUT) {
        nice64_line_framer_feed(framer, (char)value, on_line, context);
    }
}
