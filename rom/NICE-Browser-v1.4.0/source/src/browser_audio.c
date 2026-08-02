#include "browser_audio.h"

#include <libdragon.h>

#define BROWSER_AUDIO_OUTPUT_RATE 22050
#define BROWSER_AUDIO_BUFFERS 2
#define BROWSER_AUDIO_CHANNEL_INTRO 0
#define BROWSER_AUDIO_CHANNEL_UI 1
#define BROWSER_AUDIO_CHANNEL_ERROR 2
#define BROWSER_AUDIO_CHANNEL_COUNT 3

#define BROWSER_AUDIO_MASTER_VOLUME 0.42f
#define BROWSER_AUDIO_INTRO_VOLUME 0.54f
#define BROWSER_AUDIO_UI_VOLUME 0.34f
#define BROWSER_AUDIO_ERROR_VOLUME 0.38f
#define BROWSER_AUDIO_UI_MIN_FREQUENCY 8000.0f
#define BROWSER_AUDIO_UI_MAX_FREQUENCY 25000.0f

static bool g_initialized;
static wav64_t g_intro;
static wav64_t g_keyboard;
static wav64_t g_error;

#ifdef BROWSER_AUDIO_DEBUG_TIMING
static uint64_t g_ui_request_tick;
#endif

#ifdef BROWSER_AUDIO_DEBUG_POLL
static uint32_t g_debug_frame;
static uint8_t g_debug_polls_this_frame;

static void debug_play_request(const char *request)
{
    debugf("AUDIO_PLAY request=%s frame=%lu\n", request, (unsigned long)g_debug_frame);
}
#else
static void debug_play_request(const char *request)
{
    (void)request;
}
#endif

static void play_ui(const char *request, float pitch)
{
    float frequency;
    if (!g_initialized) return;

#ifdef BROWSER_AUDIO_DEBUG_TIMING
    g_ui_request_tick = get_ticks_ms();
#endif
    wav64_play(&g_keyboard, BROWSER_AUDIO_CHANNEL_UI);
    frequency = (float)g_keyboard.wave.frequency * pitch;
    if (frequency < BROWSER_AUDIO_UI_MIN_FREQUENCY) frequency = BROWSER_AUDIO_UI_MIN_FREQUENCY;
    if (frequency > BROWSER_AUDIO_UI_MAX_FREQUENCY) frequency = BROWSER_AUDIO_UI_MAX_FREQUENCY;
    mixer_ch_set_freq(BROWSER_AUDIO_CHANNEL_UI, frequency);
    mixer_ch_set_vol_pan(BROWSER_AUDIO_CHANNEL_UI, BROWSER_AUDIO_UI_VOLUME, 0.5f);
    debug_play_request(request);
}

void BrowserAudio_Init(void)
{
    if (g_initialized) return;

#ifdef BROWSER_AUDIO_DISABLED
    debugf("Browser audio: disabled by diagnostic build\n");
    return;
#endif
    audio_init(BROWSER_AUDIO_OUTPUT_RATE, BROWSER_AUDIO_BUFFERS);
    mixer_init(BROWSER_AUDIO_CHANNEL_COUNT);
    /* Open/link clicks use 110% of the 22.05 kHz source rate. */
    mixer_ch_set_limits(BROWSER_AUDIO_CHANNEL_UI, 0, BROWSER_AUDIO_UI_MAX_FREQUENCY, 0);
    mixer_set_vol(BROWSER_AUDIO_MASTER_VOLUME);

    wav64_open(&g_intro, "rom:/intro.wav64");
    wav64_open(&g_keyboard, "rom:/keyboard_click.wav64");
    wav64_open(&g_error, "rom:/error.wav64");
    wav64_set_loop(&g_intro, false);
    wav64_set_loop(&g_keyboard, false);
    wav64_set_loop(&g_error, false);

    g_initialized = true;
    debugf("Browser audio: initialized ROMFS WAV64 channels=3\n");
}

void BrowserAudio_Shutdown(void)
{
    if (!g_initialized) return;

    mixer_ch_stop(BROWSER_AUDIO_CHANNEL_INTRO);
    mixer_ch_stop(BROWSER_AUDIO_CHANNEL_UI);
    mixer_ch_stop(BROWSER_AUDIO_CHANNEL_ERROR);
    wav64_close(&g_intro);
    wav64_close(&g_keyboard);
    wav64_close(&g_error);
    mixer_close();
    audio_close();
    g_initialized = false;
}

void BrowserAudio_BeginFrame(void)
{
#ifdef BROWSER_AUDIO_DEBUG_POLL
    g_debug_frame++;
    g_debug_polls_this_frame = 0;
#endif
}

void BrowserAudio_Update(void)
{
    short *buffer;
    if (!g_initialized || !audio_can_write()) return;

#ifdef BROWSER_AUDIO_DEBUG_POLL
    g_debug_polls_this_frame++;
    debugf("AUDIO_POLL frame=%lu countThisFrame=%u\n",
        (unsigned long)g_debug_frame, g_debug_polls_this_frame);
    if (g_debug_polls_this_frame > 1u)
    {
        debugf("AUDIO_DOUBLE_POLL frame=%lu\n", (unsigned long)g_debug_frame);
    }
#endif
    buffer = audio_write_begin();
    mixer_poll(buffer, audio_get_buffer_length());
    audio_write_end();
#ifdef BROWSER_AUDIO_DEBUG_TIMING
    if (g_ui_request_tick != 0)
    {
        debugf("Browser audio: UI request-to-mix=%llu ms\n",
            (unsigned long long)(get_ticks_ms() - g_ui_request_tick));
        g_ui_request_tick = 0;
    }
#endif
}

void BrowserAudio_PlayIntro(void)
{
    if (!g_initialized) return;
    wav64_play(&g_intro, BROWSER_AUDIO_CHANNEL_INTRO);
    mixer_ch_set_vol_pan(BROWSER_AUDIO_CHANNEL_INTRO, BROWSER_AUDIO_INTRO_VOLUME, 0.5f);
}

void BrowserAudio_StopIntro(void)
{
    if (g_initialized) mixer_ch_stop(BROWSER_AUDIO_CHANNEL_INTRO);
}

void BrowserAudio_PlayClick(void)    { play_ui("click", 1.00f); }
void BrowserAudio_PlayBack(void)     { play_ui("back", 0.90f); }
void BrowserAudio_PlayForward(void)  { play_ui("forward", 1.05f); }
void BrowserAudio_PlayOpen(void)     { play_ui("open", 1.10f); }
void BrowserAudio_PlayMenu(void)     { play_ui("menu", 0.95f); }
void BrowserAudio_PlayFavorite(void) { play_ui("favorite", 1.00f); }
void BrowserAudio_PlayHistory(void)  { play_ui("history", 0.98f); }
void BrowserAudio_PlayRecent(void)   { play_ui("recent", 1.02f); }

void BrowserAudio_PlayError(void)
{
    if (!g_initialized) return;
    wav64_play(&g_error, BROWSER_AUDIO_CHANNEL_ERROR);
    mixer_ch_set_vol_pan(BROWSER_AUDIO_CHANNEL_ERROR, BROWSER_AUDIO_ERROR_VOLUME, 0.5f);
    debug_play_request("error");
}
