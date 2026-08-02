#ifndef BROWSER_INTRO_H
#define BROWSER_INTRO_H

#include <libdragon.h>
#include <stdbool.h>

/* Standalone boot presentation. It owns no Browser or Network Pak state. */
void BrowserIntro_Init(void);
void BrowserIntro_Update(joypad_buttons_t pressed);
void BrowserIntro_Draw(surface_t *display);
bool BrowserIntro_IsComplete(void);

#endif
