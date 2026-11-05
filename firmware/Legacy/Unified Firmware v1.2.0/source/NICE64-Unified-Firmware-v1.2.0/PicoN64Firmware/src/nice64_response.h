#ifndef NICE64_RESPONSE_H
#define NICE64_RESPONSE_H

#include <stddef.h>

typedef void (*nice64_response_write_fn)(void *context,
                                         const char *data,
                                         size_t length);
typedef void (*nice64_response_flush_fn)(void *context);

typedef struct {
    nice64_response_write_fn write;
    nice64_response_flush_fn flush;
    void *context;
} nice64_response_sink_t;

void nice64_response_write(const nice64_response_sink_t *sink,
                           const char *data,
                           size_t length);
void nice64_response_putc(const nice64_response_sink_t *sink, char value);
void nice64_response_puts(const nice64_response_sink_t *sink, const char *line);
void nice64_response_printf(const nice64_response_sink_t *sink,
                            const char *format,
                            ...);
void nice64_response_flush(const nice64_response_sink_t *sink);

#endif
