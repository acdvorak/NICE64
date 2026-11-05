#include "n64_keyboard.h"

#include "keyboard_state.h"
#include "pico/platform/compiler.h"
#include "pico/platform/sections.h"

static volatile uint8_t requested_leds;

void n64_keyboard_init(void)
{
    __atomic_store_n(&requested_leds, 0u, __ATOMIC_RELEASE);
    keyboard_state_init();
}

void __no_inline_not_in_flash_func(n64_keyboard_identity)(uint8_t out[3])
{
    out[0] = 0x00u;
    out[1] = 0x02u;
    out[2] = 0x00u;
}

void __no_inline_not_in_flash_func(n64_keyboard_read)(uint8_t led_request,
                                                       uint8_t out[7])
{
    __atomic_store_n(&requested_leds, (uint8_t)(led_request & 0x07u),
                     __ATOMIC_RELEASE);
    keyboard_state_read_report(out);
}

uint8_t n64_keyboard_led_request(void)
{
    return __atomic_load_n(&requested_leds, __ATOMIC_ACQUIRE);
}
