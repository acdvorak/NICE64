#include "n64_mouse.h"

#include "pico/platform/compiler.h"
#include "pico/platform/sections.h"
#include "pointer_state.h"

void n64_mouse_init(void)
{
    pointer_state_init();
}

void __no_inline_not_in_flash_func(n64_mouse_identity)(uint8_t out[3])
{
    out[0] = 0x02u;
    out[1] = 0x00u;
    out[2] = 0x00u;
}

void __no_inline_not_in_flash_func(n64_mouse_read)(uint8_t out[4])
{
    pointer_state_consume_report(out);
}
