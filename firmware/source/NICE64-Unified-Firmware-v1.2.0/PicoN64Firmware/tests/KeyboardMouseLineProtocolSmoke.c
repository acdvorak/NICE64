#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "keyboard_mouse_service.h"
#include "keyboard_state.h"
#include "n64_keyboard.h"
#include "pointer_state.h"
#include "role_manager.h"

typedef struct {
    char text[256];
    size_t length;
} capture_t;

static void capture_write(void *context, const char *data, size_t length)
{
    capture_t *capture = context;
    assert(capture->length + length < sizeof(capture->text));
    memcpy(capture->text + capture->length, data, length);
    capture->length += length;
    capture->text[capture->length] = '\0';
}

static void expect_line(const char *line, const char *expected,
                        nice64_response_sink_t *sink, capture_t *capture)
{
    capture->length = 0u;
    capture->text[0] = '\0';
    assert(keyboard_mouse_service_handle_line(line, sink));
    assert(strcmp(capture->text, expected) == 0);
}

int main(void)
{
    capture_t capture = {0};
    nice64_response_sink_t sink = {
        .write = capture_write,
        .context = &capture,
    };
    role_manager_init();
    const nice64_active_mode_t mode = {
        .role = NICE64_ROLE_STANDALONE,
        .standalone_service = NICE64_SERVICE_KEYBOARD_MOUSE,
    };
    assert(role_manager_set_active_mode(mode));
    assert(role_manager_set_keyboard_mouse_device(NICE64_KEYBOARD_MOUSE_KEYBOARD));
    n64_keyboard_init();
    pointer_state_init();

    expect_line("KEYBOARD_STATE_V1 12345678 00000000 012C",
                "KEYBOARD_STATE_V1|ACK\n", &sink, &capture);
    keyboard_state_service();
    uint8_t keyboard[7];
    keyboard_state_read_report(keyboard);
    const uint8_t a[7] = { 0x0D, 0x07, 0, 0, 0, 0, 0 };
    assert(memcmp(keyboard, a, sizeof(a)) == 0);
    expect_line("KEYBOARD_STATE_V1 12345678 00000000 012C",
                "KEYBOARD_STATE_V1|STALE\n", &sink, &capture);

    n64_keyboard_read(0x05u, keyboard);
    expect_line("KEYBOARD_LEDS_V1?", "KEYBOARD_LEDS_V1|05\n",
                &sink, &capture);

    assert(role_manager_set_keyboard_mouse_device(NICE64_KEYBOARD_MOUSE_MOUSE));
    expect_line("POINTER_STATE_V1 89ABCDEF 00000000 01 0000000A FFFFFFFB",
                "POINTER_STATE_V1|ACK\n", &sink, &capture);
    pointer_state_service();
    uint8_t pointer[4];
    pointer_state_consume_report(pointer);
    const uint8_t expected_pointer[4] = { 0x80, 0x00, 0x0A, 0xFB };
    assert(memcmp(pointer, expected_pointer, sizeof(pointer)) == 0);

    expect_line("POINTER_STATE_V1 89ABCDEF 00000000 01 0000000A FFFFFFFB",
                "POINTER_STATE_V1|STALE\n", &sink, &capture);
    expect_line("KEYBOARD_STATE_V1 bad", "KEYBOARD_STATE_V1|ERR|FORMAT\n",
                &sink, &capture);
    puts("KeyboardMouseLineProtocolSmoke: PASS");
    return 0;
}
