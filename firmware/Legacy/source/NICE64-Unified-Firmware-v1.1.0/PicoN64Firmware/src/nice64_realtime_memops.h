#ifndef NICE64_REALTIME_MEMOPS_H
#define NICE64_REALTIME_MEMOPS_H

#include <stddef.h>
#include <stdint.h>

// Bounded byte operations for the Core0 response path. These deliberately avoid
// a dependency on Flash/XIP libc memset/memcpy.
void nice64_realtime_fill(void *destination, uint8_t value, size_t length);
void nice64_realtime_copy(void *destination, const void *source, size_t length);

#endif
