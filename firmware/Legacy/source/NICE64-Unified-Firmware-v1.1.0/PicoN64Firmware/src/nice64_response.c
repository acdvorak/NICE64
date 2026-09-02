#include "nice64_response.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#define NICE64_RESPONSE_FORMAT_BUFFER_SIZE 1024u

/* All command dispatch currently runs serially on Core1. */
static char format_buffer[NICE64_RESPONSE_FORMAT_BUFFER_SIZE];

void nice64_response_write(const nice64_response_sink_t *sink,
                           const char *data,
                           size_t length) {
    if (sink != NULL && sink->write != NULL && data != NULL && length > 0u) {
        sink->write(sink->context, data, length);
    }
}

void nice64_response_putc(const nice64_response_sink_t *sink, char value) {
    nice64_response_write(sink, &value, 1u);
}

void nice64_response_puts(const nice64_response_sink_t *sink, const char *line) {
    if (line != NULL) {
        nice64_response_write(sink, line, strlen(line));
    }
    nice64_response_putc(sink, '\n');
    nice64_response_flush(sink);
}

void nice64_response_printf(const nice64_response_sink_t *sink,
                            const char *format,
                            ...) {
    if (sink == NULL || sink->write == NULL || format == NULL) {
        return;
    }

    va_list args;
    va_start(args, format);
    const int result = vsnprintf(format_buffer, sizeof(format_buffer), format, args);
    va_end(args);

    if (result <= 0) {
        return;
    }

    size_t length = (size_t)result;
    if (length >= sizeof(format_buffer)) {
        length = sizeof(format_buffer) - 1u;
    }
    nice64_response_write(sink, format_buffer, length);
}

void nice64_response_flush(const nice64_response_sink_t *sink) {
    if (sink != NULL && sink->flush != NULL) {
        sink->flush(sink->context);
    }
}
