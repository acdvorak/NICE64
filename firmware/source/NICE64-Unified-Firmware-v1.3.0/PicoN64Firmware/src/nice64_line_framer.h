#ifndef NICE64_LINE_FRAMER_H
#define NICE64_LINE_FRAMER_H

#include <stdbool.h>
#include <stddef.h>

typedef void (*nice64_line_ready_fn)(void *context, const char *line);

typedef struct {
    char *buffer;
    size_t capacity;
    size_t length;
    bool previous_was_cr;
    bool discarding_overlong;
} nice64_line_framer_t;

void nice64_line_framer_init(nice64_line_framer_t *framer,
                             char *buffer,
                             size_t capacity);
void nice64_line_framer_feed(nice64_line_framer_t *framer,
                             char value,
                             nice64_line_ready_fn on_line,
                             void *context);

#endif
