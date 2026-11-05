#ifndef N64_KEYBOARD_H
#define N64_KEYBOARD_H

#include <stdint.h>

#define N64_KEYBOARD_COMMAND_READ 0x13u
#define N64_KEYBOARD_REPLY_SIZE 7u

void n64_keyboard_init(void);
void n64_keyboard_identity(uint8_t out[3]);
void n64_keyboard_read(uint8_t led_request, uint8_t out[N64_KEYBOARD_REPLY_SIZE]);
uint8_t n64_keyboard_led_request(void);

#endif
