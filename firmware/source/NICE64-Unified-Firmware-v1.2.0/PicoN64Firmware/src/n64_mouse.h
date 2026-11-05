#ifndef N64_MOUSE_H
#define N64_MOUSE_H

#include <stdint.h>

#define N64_MOUSE_REPLY_SIZE 4u

void n64_mouse_init(void);
void n64_mouse_identity(uint8_t out[3]);
void n64_mouse_read(uint8_t out[N64_MOUSE_REPLY_SIZE]);

#endif
