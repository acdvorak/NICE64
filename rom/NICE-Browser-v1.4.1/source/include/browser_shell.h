#ifndef BROWSER_SHELL_H
#define BROWSER_SHELL_H

#include <libdragon.h>
#include <stdbool.h>

void Browser_Init(void);
void Browser_Start(void);
void Browser_Update(joypad_buttons_t pressed, joypad_inputs_t inputs);
void Browser_Draw(surface_t *display);
bool Browser_MenuVisible(void);

#endif
