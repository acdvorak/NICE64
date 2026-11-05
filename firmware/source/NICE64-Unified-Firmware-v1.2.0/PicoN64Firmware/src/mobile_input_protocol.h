#ifndef NICE64_MOBILE_INPUT_PROTOCOL_H
#define NICE64_MOBILE_INPUT_PROTOCOL_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "randnet_keymap.h"

#define NICE64_MOBILE_INPUT_VERSION 1u
#define NICE64_MOBILE_INPUT_KEYBOARD 1u
#define NICE64_MOBILE_INPUT_POINTER 2u
#define NICE64_MOBILE_KEYBOARD_PREFIX_BYTES 3u
#define NICE64_MOBILE_POINTER_BYTES 12u

typedef struct {
    uint8_t buttons;
    int32_t total_x;
    int32_t total_y;
} nice64_mobile_pointer_t;

bool nice64_mobile_input_decode_keyboard(
    const uint8_t *payload, size_t length,
    nice64_logical_key_t *keys, size_t capacity, size_t *count);

bool nice64_mobile_input_decode_pointer(
    const uint8_t *payload, size_t length, nice64_mobile_pointer_t *pointer);

#endif
