#ifndef CHECKPOINT_E_LIBDRAGON_STUB_H
#define CHECKPOINT_E_LIBDRAGON_STUB_H

#include <stdint.h>

typedef struct
{
    void *buffer;
    uint16_t width;
    uint16_t height;
    uint16_t stride;
} surface_t;

enum { FMT_RGBA16 = 0 };

surface_t surface_make_linear(void *buffer, int format, uint16_t width, uint16_t height);
uint64_t get_ticks(void);
void debugf(const char *format, ...);

#endif
