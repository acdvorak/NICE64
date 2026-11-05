#include "nice64_line_framer.h"

void nice64_line_framer_init(nice64_line_framer_t *framer,
                             char *buffer,
                             size_t capacity) {
    if (framer == NULL) {
        return;
    }

    framer->buffer = buffer;
    framer->capacity = capacity;
    framer->length = 0u;
    framer->previous_was_cr = false;
    framer->discarding_overlong = false;
    if (buffer != NULL && capacity > 0u) {
        buffer[0] = '\0';
    }
}

void nice64_line_framer_feed(nice64_line_framer_t *framer,
                             char value,
                             nice64_line_ready_fn on_line,
                             void *context) {
    if (framer == NULL || framer->buffer == NULL || framer->capacity == 0u) {
        return;
    }

    if (value == '\r') {
        framer->previous_was_cr = true;
        value = '\n';
    } else if (value == '\n' && framer->previous_was_cr) {
        framer->previous_was_cr = false;
        return;
    } else {
        framer->previous_was_cr = false;
    }

    if (value == '\n') {
        if (!framer->discarding_overlong) {
            framer->buffer[framer->length] = '\0';
            if (on_line != NULL) {
                on_line(context, framer->buffer);
            }
        }
        framer->length = 0u;
        framer->discarding_overlong = false;
        return;
    }

    if (framer->discarding_overlong) {
        return;
    }

    if (framer->length < framer->capacity - 1u) {
        framer->buffer[framer->length++] = value;
    } else {
        framer->length = 0u;
        framer->discarding_overlong = true;
    }
}
