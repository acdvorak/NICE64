#include "mobile_input_protocol.h"

#include "pointer_state.h"

static uint32_t read_u32_be(const uint8_t *source)
{
    return ((uint32_t)source[0] << 24) |
           ((uint32_t)source[1] << 16) |
           ((uint32_t)source[2] << 8) |
           (uint32_t)source[3];
}

bool nice64_mobile_input_decode_keyboard(
    const uint8_t *payload, size_t length,
    nice64_logical_key_t *keys, size_t capacity, size_t *count)
{
    if (payload == NULL || keys == NULL || count == NULL ||
        length < NICE64_MOBILE_KEYBOARD_PREFIX_BYTES ||
        payload[0] != NICE64_MOBILE_INPUT_VERSION ||
        payload[1] != NICE64_MOBILE_INPUT_KEYBOARD)
        return false;

    const size_t key_count = payload[2];
    if (key_count > capacity ||
        length != NICE64_MOBILE_KEYBOARD_PREFIX_BYTES + key_count)
        return false;
    for (size_t i = 0; i < key_count; ++i) {
        const uint8_t value = payload[NICE64_MOBILE_KEYBOARD_PREFIX_BYTES + i];
        if (value <= NICE64_KEY_NONE || value >= NICE64_KEY_COUNT)
            return false;
        keys[i] = (nice64_logical_key_t)value;
    }
    *count = key_count;
    return true;
}

bool nice64_mobile_input_decode_pointer(
    const uint8_t *payload, size_t length, nice64_mobile_pointer_t *pointer)
{
    if (payload == NULL || pointer == NULL ||
        length != NICE64_MOBILE_POINTER_BYTES ||
        payload[0] != NICE64_MOBILE_INPUT_VERSION ||
        payload[1] != NICE64_MOBILE_INPUT_POINTER || payload[3] != 0u ||
        (payload[2] & ~(POINTER_BUTTON_LEFT | POINTER_BUTTON_RIGHT)) != 0u)
        return false;
    pointer->buttons = payload[2];
    pointer->total_x = (int32_t)read_u32_be(&payload[4]);
    pointer->total_y = (int32_t)read_u32_be(&payload[8]);
    return true;
}
