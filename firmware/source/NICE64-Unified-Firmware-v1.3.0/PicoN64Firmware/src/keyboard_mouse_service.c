#include "keyboard_mouse_service.h"

#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include "keyboard_state.h"
#include "n64_keyboard.h"
#include "pointer_state.h"
#include "randnet_keymap.h"
#include "role_manager.h"

#define KEYBOARD_STATE_COMMAND "KEYBOARD_STATE_V1 "
#define POINTER_STATE_COMMAND "POINTER_STATE_V1 "

static bool parse_hex(const char **cursor, size_t digits, uint32_t *value)
{
    uint32_t parsed = 0u;
    for (size_t i = 0; i < digits; ++i) {
        const char c = (*cursor)[i];
        uint8_t nibble;
        if (c >= '0' && c <= '9') nibble = (uint8_t)(c - '0');
        else if (c >= 'A' && c <= 'F') nibble = (uint8_t)(c - 'A' + 10);
        else if (c >= 'a' && c <= 'f') nibble = (uint8_t)(c - 'a' + 10);
        else return false;
        parsed = (parsed << 4u) | nibble;
    }
    *cursor += digits;
    *value = parsed;
    return true;
}

static bool consume_space(const char **cursor)
{
    if (**cursor != ' ')
        return false;
    ++*cursor;
    return true;
}

static bool handle_keyboard(const char *payload,
                            const nice64_response_sink_t *response)
{
    uint32_t session;
    uint32_t sequence;
    uint32_t count;
    nice64_logical_key_t keys[NICE64_KEY_COUNT - 1u];
    const char *cursor = payload;

    if (!parse_hex(&cursor, 8u, &session) || !consume_space(&cursor) ||
        !parse_hex(&cursor, 8u, &sequence) || !consume_space(&cursor) ||
        !parse_hex(&cursor, 2u, &count) || count >= NICE64_KEY_COUNT)
        goto invalid;

    for (uint32_t i = 0; i < count; ++i) {
        uint32_t key;
        if (!parse_hex(&cursor, 2u, &key) ||
            key <= NICE64_KEY_NONE || key >= NICE64_KEY_COUNT)
            goto invalid;
        keys[i] = (nice64_logical_key_t)key;
    }
    if (*cursor != '\0')
        goto invalid;

    if (role_manager_keyboard_mouse_device() != NICE64_KEYBOARD_MOUSE_KEYBOARD) {
        nice64_response_puts(response, "KEYBOARD_STATE_V1|ERR|INACTIVE");
        return true;
    }
    nice64_response_puts(response,
        keyboard_state_accept_held(session, sequence, keys, count)
            ? "KEYBOARD_STATE_V1|ACK"
            : "KEYBOARD_STATE_V1|STALE");
    return true;

invalid:
    nice64_response_puts(response, "KEYBOARD_STATE_V1|ERR|FORMAT");
    return true;
}

static bool handle_pointer(const char *payload,
                           const nice64_response_sink_t *response)
{
    uint32_t session;
    uint32_t sequence;
    uint32_t buttons;
    uint32_t total_x;
    uint32_t total_y;
    const char *cursor = payload;

    if (!parse_hex(&cursor, 8u, &session) || !consume_space(&cursor) ||
        !parse_hex(&cursor, 8u, &sequence) || !consume_space(&cursor) ||
        !parse_hex(&cursor, 2u, &buttons) || !consume_space(&cursor) ||
        !parse_hex(&cursor, 8u, &total_x) || !consume_space(&cursor) ||
        !parse_hex(&cursor, 8u, &total_y) || *cursor != '\0' ||
        (buttons & ~(POINTER_BUTTON_LEFT | POINTER_BUTTON_RIGHT)) != 0u) {
        nice64_response_puts(response, "POINTER_STATE_V1|ERR|FORMAT");
        return true;
    }

    if (role_manager_keyboard_mouse_device() != NICE64_KEYBOARD_MOUSE_MOUSE) {
        nice64_response_puts(response, "POINTER_STATE_V1|ERR|INACTIVE");
        return true;
    }
    nice64_response_puts(response,
        pointer_state_accept_cumulative(session, sequence, (uint8_t)buttons,
                                        (int32_t)total_x, (int32_t)total_y)
            ? "POINTER_STATE_V1|ACK"
            : "POINTER_STATE_V1|STALE");
    return true;
}

bool keyboard_mouse_service_handle_line(
    const char *line, const nice64_response_sink_t *response)
{
    if (line == NULL || response == NULL || response->write == NULL)
        return false;
    if (strncmp(line, KEYBOARD_STATE_COMMAND,
                sizeof(KEYBOARD_STATE_COMMAND) - 1u) == 0)
        return handle_keyboard(line + sizeof(KEYBOARD_STATE_COMMAND) - 1u,
                               response);
    if (strncmp(line, POINTER_STATE_COMMAND,
                sizeof(POINTER_STATE_COMMAND) - 1u) == 0)
        return handle_pointer(line + sizeof(POINTER_STATE_COMMAND) - 1u,
                              response);
    if (strcmp(line, "KEYBOARD_LEDS_V1?") == 0) {
        nice64_response_printf(response, "KEYBOARD_LEDS_V1|%02X\n",
                               (unsigned)n64_keyboard_led_request());
        return true;
    }
    return false;
}
