#ifndef BROWSER_AUDIO_H
#define BROWSER_AUDIO_H

#include <stdbool.h>

/* Browser-facing semantic sound events. The mixer and ROMFS details stay private. */
void BrowserAudio_Init(void);
void BrowserAudio_Shutdown(void);
void BrowserAudio_BeginFrame(void);
void BrowserAudio_Update(void);

void BrowserAudio_PlayIntro(void);
void BrowserAudio_StopIntro(void);
void BrowserAudio_PlayClick(void);
void BrowserAudio_PlayBack(void);
void BrowserAudio_PlayForward(void);
void BrowserAudio_PlayOpen(void);
void BrowserAudio_PlayMenu(void);
void BrowserAudio_PlayFavorite(void);
void BrowserAudio_PlayHistory(void);
void BrowserAudio_PlayRecent(void);
void BrowserAudio_PlayError(void);

#endif
