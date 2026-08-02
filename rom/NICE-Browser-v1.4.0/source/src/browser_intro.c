#include "browser_intro.h"
#include "browser_audio.h"

#include <stdio.h>
#include <string.h>

typedef enum browser_intro_state_e
{
    INTRO_FADE_IN_LOGO,
    INTRO_HOLD_LOGO,
    INTRO_TRANSITION_ART,
    INTRO_HOLD_ART,
    INTRO_FADE_OUT,
    INTRO_COMPLETE
} BrowserIntroState;

#define INTRO_LOGO_FADE_MS 500u
#define INTRO_LOGO_HOLD_MS 800u
#define INTRO_ART_TRANSITION_MS 450u
#define INTRO_ART_HOLD_MS 1650u
#define INTRO_FADE_OUT_MS 350u

static BrowserIntroState g_state;
static uint64_t g_state_started_ms;
static sprite_t *g_logo;
static sprite_t *g_art;
static bool g_complete;
static bool g_assets_released;

static uint32_t elapsed_ms(void)
{
    return (uint32_t)(get_ticks_ms() - g_state_started_ms);
}

static void release_assets(void)
{
    if (g_assets_released) return;
    if (g_logo != NULL) sprite_free(g_logo);
    if (g_art != NULL) sprite_free(g_art);
    g_logo = NULL;
    g_art = NULL;
    g_assets_released = true;
}

static void complete_intro(const char *reason)
{
    if (g_complete) return;
    BrowserAudio_StopIntro();
    g_complete = true;
    g_state = INTRO_COMPLETE;
    release_assets();
    debugf("Browser intro: %s\n", reason);
}

static void advance_state(BrowserIntroState next)
{
    g_state = next;
    g_state_started_ms = get_ticks_ms();
}

void BrowserIntro_Init(void)
{
    g_state = INTRO_FADE_IN_LOGO;
    g_state_started_ms = get_ticks_ms();
    g_complete = false;
    g_assets_released = false;
    g_logo = sprite_load("rom:/nice64_intro.sprite");
    g_art = sprite_load("rom:/melfegor_intro.sprite");

    if (g_logo == NULL) debugf("Browser intro: logo asset unavailable\n");
    if (g_art == NULL) debugf("Browser intro: artwork asset unavailable\n");
    if (g_logo == NULL && g_art == NULL) complete_intro("assets unavailable; skipped");
    else
    {
        BrowserAudio_PlayIntro();
        debugf("Browser intro: started logo=%p art=%p\n", (void *)g_logo, (void *)g_art);
    }
}

void BrowserIntro_Update(joypad_buttons_t pressed)
{
    if (g_complete) return;
    if (pressed.start || pressed.a)
    {
        complete_intro("skipped");
        return;
    }

    switch (g_state)
    {
    case INTRO_FADE_IN_LOGO:
        if (elapsed_ms() >= INTRO_LOGO_FADE_MS) advance_state(INTRO_HOLD_LOGO);
        break;
    case INTRO_HOLD_LOGO:
        if (elapsed_ms() >= INTRO_LOGO_HOLD_MS) advance_state(INTRO_TRANSITION_ART);
        break;
    case INTRO_TRANSITION_ART:
        if (elapsed_ms() >= INTRO_ART_TRANSITION_MS) advance_state(INTRO_HOLD_ART);
        break;
    case INTRO_HOLD_ART:
        if (elapsed_ms() >= INTRO_ART_HOLD_MS) advance_state(INTRO_FADE_OUT);
        break;
    case INTRO_FADE_OUT:
        if (elapsed_ms() >= INTRO_FADE_OUT_MS) complete_intro("completed");
        break;
    default:
        break;
    }
}

static void draw_logo(surface_t *display, float scale)
{
    if (g_logo == NULL) return;
    rdpq_attach(display, NULL);
    rdpq_set_mode_standard();
    rdpq_sprite_blit(g_logo, 160.0f, 105.0f, &(rdpq_blitparms_t){
        .cx = g_logo->width / 2,
        .cy = g_logo->height / 2,
        .scale_x = scale,
        .scale_y = scale
    });
    rdpq_detach_wait();
}

static void draw_art(surface_t *display, float scale)
{
    if (g_art == NULL) return;
    rdpq_attach(display, NULL);
    rdpq_set_mode_standard();
    rdpq_sprite_blit(g_art, 160.0f, 93.0f, &(rdpq_blitparms_t){
        .cx = g_art->width / 2,
        .cy = g_art->height / 2,
        .scale_x = scale,
        .scale_y = scale
    });
    rdpq_detach_wait();
}

static void draw_text(surface_t *display, int x, int y, uint32_t color, const char *text)
{
    graphics_set_color(color, graphics_make_color(0, 0, 0, 0));
    graphics_draw_text(display, x, y, text);
}

static void draw_centered_text(surface_t *display, int y, uint32_t color, const char *text)
{
    int text_width = (int)strlen(text) * 8;
    draw_text(display, (320 - text_width) / 2, y, color, text);
}

void BrowserIntro_Draw(surface_t *display)
{
    uint32_t elapsed;
    float scale;

    graphics_fill_screen(display, graphics_make_color(0, 0, 0, 255));
    if (g_complete) return;

    elapsed = elapsed_ms();
    if (g_state == INTRO_FADE_IN_LOGO || g_state == INTRO_HOLD_LOGO)
    {
        scale = g_state == INTRO_FADE_IN_LOGO ? 0.80f + 0.20f * ((float)elapsed / (float)INTRO_LOGO_FADE_MS) : 1.0f;
        draw_logo(display, scale);
        return;
    }

    if (g_state == INTRO_TRANSITION_ART)
    {
        scale = 0.84f + 0.06f * ((float)elapsed / (float)INTRO_ART_TRANSITION_MS);
        draw_art(display, scale);
    }
    else
    {
        draw_art(display, 0.90f);
    }

    draw_centered_text(display, 184, graphics_make_color(255, 255, 255, 255), "NICE64 Browser");
    draw_centered_text(display, 197, graphics_make_color(140, 230, 255, 255), "Created by Melfegor");
    draw_centered_text(display, 209, graphics_make_color(180, 198, 218, 255), "Powered by the");
    draw_centered_text(display, 221, graphics_make_color(180, 198, 218, 255), "NICE64 Network Pak");
}

bool BrowserIntro_IsComplete(void)
{
    return g_complete;
}
