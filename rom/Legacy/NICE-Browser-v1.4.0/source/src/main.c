#include "browser_shell.h"
#include "browser_audio.h"
#include "browser_intro.h"

#include <libdragon.h>

int main(void)
{
    bool browser_started = false;
    timer_init();
    joypad_init();
    debug_init_emulog();
    display_init(RESOLUTION_320x240, DEPTH_16_BPP, 2, GAMMA_NONE, FILTERS_RESAMPLE);
    rdpq_init();
    graphics_set_default_font();
    dfs_init(DFS_DEFAULT_LOCATION);

    Browser_Init();
    BrowserAudio_Init();
    BrowserIntro_Init();

    while (1)
    {
        surface_t *display;
        joypad_buttons_t pressed;
        joypad_inputs_t inputs;

        joypad_poll();
        pressed = joypad_get_buttons_pressed(JOYPAD_PORT_1);
        inputs = joypad_get_inputs(JOYPAD_PORT_1);
        BrowserAudio_BeginFrame();

        display = display_get();
        if (!BrowserIntro_IsComplete())
        {
            BrowserIntro_Update(pressed);
            BrowserAudio_Update();
            BrowserIntro_Draw(display);
            display_show(display);
            wait_ms(16);
            continue;
        }

        if (!browser_started)
        {
            Browser_Start();
            browser_started = true;
            pressed = (joypad_buttons_t){0};
        }
        Browser_Update(pressed, inputs);
        BrowserAudio_Update();
        Browser_Draw(display);
        display_show(display);
        wait_ms(16);
    }
}
