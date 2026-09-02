#include "nice64_realtime_memops.h"

#include "pico/platform/compiler.h"
#include "pico/platform/sections.h"

void __no_inline_not_in_flash_func(nice64_realtime_fill)(void *destination,
                                                          uint8_t value,
                                                          size_t length) {
    volatile uint8_t *out = (volatile uint8_t *)destination;
    for (size_t i = 0; i < length; ++i) {
        out[i] = value;
    }
}

void __no_inline_not_in_flash_func(nice64_realtime_copy)(void *destination,
                                                          const void *source,
                                                          size_t length) {
    volatile uint8_t *out = (volatile uint8_t *)destination;
    const volatile uint8_t *in = (const volatile uint8_t *)source;
    for (size_t i = 0; i < length; ++i) {
        out[i] = in[i];
    }
}
