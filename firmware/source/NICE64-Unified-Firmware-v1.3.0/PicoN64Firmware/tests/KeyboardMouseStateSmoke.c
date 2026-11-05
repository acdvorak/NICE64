#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "keyboard_state.h"
#include "pointer_state.h"
#include "randnet_keymap.h"

static void expect_bytes(const uint8_t *actual, const uint8_t *expected,
                         size_t length)
{
    if (memcmp(actual, expected, length) != 0) {
        for (size_t i = 0; i < length; ++i)
            fprintf(stderr, "%02X%s", actual[i], i + 1u == length ? "\n" : " ");
        assert(!"byte mismatch");
    }
}

static void publish_keyboard(const nice64_logical_key_t *keys, size_t count,
                             const uint8_t expected[7])
{
    uint8_t report[7];
    assert(keyboard_state_publish_held(keys, count));
    keyboard_state_service();
    keyboard_state_read_report(report);
    expect_bytes(report, expected, 7u);
}

static void test_keyboard(void)
{
    keyboard_state_init();

    const nice64_logical_key_t a[] = { NICE64_KEY_A };
    const uint8_t a_expected[7] = { 0x0D, 0x07, 0, 0, 0, 0, 0 };
    publish_keyboard(a, 1u, a_expected);

    const nice64_logical_key_t shift_a[] = {
        NICE64_KEY_LEFT_SHIFT, NICE64_KEY_A
    };
    const uint8_t shift_a_expected[7] = {
        0x0D, 0x07, 0x0E, 0x01, 0, 0, 0
    };
    publish_keyboard(shift_a, 2u, shift_a_expected);

    const nice64_logical_key_t three[] = {
        NICE64_KEY_A, NICE64_KEY_LEFT_SHIFT, NICE64_KEY_CAPS_LOCK
    };
    const uint8_t three_expected[7] = {
        0x0F, 0x05, 0x0D, 0x07, 0x0E, 0x01, 0
    };
    publish_keyboard(three, 3u, three_expected);

    const nice64_logical_key_t four[] = {
        NICE64_KEY_A, NICE64_KEY_S, NICE64_KEY_D, NICE64_KEY_F
    };
    const uint8_t four_expected[7] = { 0, 0, 0, 0, 0, 0, 0x10 };
    publish_keyboard(four, 4u, four_expected);

    const nice64_logical_key_t home[] = { NICE64_KEY_HOME };
    const uint8_t home_expected[7] = { 0, 0, 0, 0, 0, 0, 0x01 };
    publish_keyboard(home, 1u, home_expected);

    const uint8_t released[7] = {0};
    publish_keyboard(NULL, 0u, released);

    assert(randnet_keymap_matrix_value(NICE64_KEY_A) == 0x0D07u);
    assert(randnet_keymap_matrix_value(NICE64_KEY_LEFT_SHIFT) == 0x0E01u);
    assert(randnet_keymap_matrix_value(NICE64_KEY_RIGHT_SHIFT) == 0x0E06u);
    assert(randnet_keymap_matrix_value(NICE64_KEY_CAPS_LOCK) == 0x0F05u);

    assert(keyboard_state_accept_held(2u, 0u, a, 1u));
    assert(!keyboard_state_accept_held(2u, 0u, a, 1u));
    assert(keyboard_state_accept_held(3u, 0u, NULL, 0u));
    assert(!keyboard_state_accept_held(2u, 1u, a, 1u));
}

static void test_pointer(void)
{
    uint8_t report[4];
    pointer_state_init();

    pointer_state_publish_delta(POINTER_BUTTON_LEFT, 10, -5);
    pointer_state_service();
    pointer_state_consume_report(report);
    const uint8_t first[4] = { 0x80, 0x00, 0x0A, 0xFB };
    expect_bytes(report, first, 4u);

    pointer_state_consume_report(report);
    const uint8_t consumed[4] = { 0x80, 0x00, 0x00, 0x00 };
    expect_bytes(report, consumed, 4u);

    pointer_state_publish_delta(POINTER_BUTTON_LEFT, 300, -300);
    pointer_state_service();
    pointer_state_consume_report(report);
    const uint8_t large_1[4] = { 0x80, 0x00, 0x7F, 0x80 };
    expect_bytes(report, large_1, 4u);
    pointer_state_consume_report(report);
    expect_bytes(report, large_1, 4u);
    pointer_state_consume_report(report);
    const uint8_t large_3[4] = { 0x80, 0x00, 0x2E, 0xD4 };
    expect_bytes(report, large_3, 4u);
    pointer_state_consume_report(report);
    expect_bytes(report, consumed, 4u);

    assert(pointer_state_accept_cumulative(7u, 1u, POINTER_BUTTON_RIGHT,
                                           10, -5));
    pointer_state_service();
    pointer_state_consume_report(report);
    const uint8_t cumulative_1[4] = { 0x40, 0x00, 0x0A, 0xFB };
    expect_bytes(report, cumulative_1, 4u);
    assert(!pointer_state_accept_cumulative(7u, 1u, POINTER_BUTTON_RIGHT,
                                            10, -5));
    assert(pointer_state_accept_cumulative(7u, 2u, POINTER_BUTTON_RIGHT,
                                           20, -5));
    pointer_state_service();
    pointer_state_consume_report(report);
    const uint8_t cumulative_2[4] = { 0x40, 0x00, 0x0A, 0x00 };
    expect_bytes(report, cumulative_2, 4u);

    assert(pointer_state_accept_cumulative(8u, 0u, 0u, 5, 5));
    assert(!pointer_state_accept_cumulative(7u, 3u, 0u, 30, 0));
    pointer_state_service();
    pointer_state_consume_report(report);
    const uint8_t new_session[4] = { 0x00, 0x00, 0x05, 0x05 };
    expect_bytes(report, new_session, 4u);
}

int main(void)
{
    test_keyboard();
    test_pointer();
    puts("KeyboardMouseStateSmoke: PASS");
    return 0;
}
