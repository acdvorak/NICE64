#ifndef NICE_DOCUMENT_H
#define NICE_DOCUMENT_H

#include <libdragon.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef enum nice_block_type_e
{
    NICE_TITLE,
    NICE_H1,
    NICE_H2,
    NICE_PARAGRAPH,
    NICE_LIST_ITEM,
    NICE_QUOTE,
    NICE_LINK,
    NICE_IMAGE_PLACEHOLDER,
    NICE_HORIZONTAL_RULE,
    NICE_END,
    /* Non-rendered document metadata carried over the existing NICE stream. */
    NICE_CANONICAL_URL
} NICE_BlockType;

typedef struct nice_block_s
{
    NICE_BlockType type;
    const char *text;
    const char *target;
    int image_width;
    int image_height;
} NICE_Block;

typedef struct nice_page_s
{
    const char *title;
    const NICE_Block *blocks;
} NICE_Page;

void NICE_RenderPage(const NICE_Page *page);
void NICE_Draw(surface_t *display);
/* Browser chrome artwork is rendered by the shell in its existing RDPQ frame. */
bool NICE_HasAppBanner(void);
void NICE_DrawAppBanner(void);
void NICE_SetHomeBackgroundEnabled(bool enabled);
/* True only after the current image stream has fully validated and can be submitted to RDPQ. */
bool NICE_HasValidatedImage(void);
void NICE_DrawValidatedImage(void);
bool NICE_DrawImageReferencePatternIfNeeded(void);
void NICE_MarkImageDrawCompleted(void);
const char *NICE_GetImageDrawStatus(void);
void NICE_HandleInput(joypad_buttons_t pressed, joypad_inputs_t inputs);
void NICE_SetScroll(int scroll_y);
void NICE_SetFocus(int index);
void NICE_JumpToNextSection(void);
int NICE_GetScroll(void);
int NICE_GetFocus(void);
int NICE_GetContentHeight(void);
const char *NICE_GetFocusedTarget(void);

#endif
