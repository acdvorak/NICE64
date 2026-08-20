#include "nice_document.h"

#include "document_loader.h"
#include "image_transport.h"

#include <stdio.h>
#include <string.h>

#define SCREEN_WIDTH 320
#define SCREEN_HEIGHT 240
#define VIEW_TOP 66
#define VIEW_BOTTOM 214
#define VIEW_LEFT 14
#define VIEW_RIGHT 306
#define TEXT_COLUMNS 36
#define MAX_LAYOUT_ITEMS 512
#define MAX_DOCUMENT_BLOCKS 128
#define COLOR_TRANSPARENT graphics_make_color(0, 0, 0, 0)
#define IMAGE_BORDER_SIZE 1
#define IMAGE_CAPTION_GAP 4
#define IMAGE_CAPTION_HEIGHT 11

typedef struct nice_layout_item_s
{
    const NICE_Block *block;
    int y;
    int height;
    int line_start;
    int line_length;
    int focus_index;
} nice_layout_item_t;

static const NICE_Page *g_page;
static sprite_t *g_app_banner;
static bool g_app_banner_attempted;
static sprite_t *g_home_background;
static bool g_home_background_attempted;
static bool g_home_background_enabled;
static nice_layout_item_t g_layout[MAX_LAYOUT_ITEMS];
static int g_layout_count;
static int g_scroll_y;
static int g_content_height;
static int g_focus_index;
static int g_focus_count;
static int g_smooth_scroll;
static bool g_image_first_draw_logged;
static bool g_image_draw_mismatch_logged;
static const char *g_image_draw_status = "IMAGE_DRAW_PENDING";
static bool g_image_draw_status_logged;
static bool g_image_reference_pattern_logged;

#define RGB5A1_BLACK 0x0001u
#define RGB5A1_RED   0xF801u
#define RGB5A1_GREEN 0x07C1u
#define RGB5A1_BLUE  0x003Fu
#define RGB5A1_WHITE 0xFFFFu
#define REF_BORDER_ROW RGB5A1_BLACK, RGB5A1_BLACK, RGB5A1_BLACK, RGB5A1_BLACK, RGB5A1_BLACK, RGB5A1_BLACK, RGB5A1_BLACK, RGB5A1_BLACK, RGB5A1_BLACK, RGB5A1_BLACK, RGB5A1_BLACK, RGB5A1_BLACK, RGB5A1_BLACK, RGB5A1_BLACK, RGB5A1_BLACK, RGB5A1_BLACK
#define REF_QUADRANT_ROW(left, right) RGB5A1_BLACK, left, left, left, left, left, left, left, right, right, right, right, right, right, right, RGB5A1_BLACK

/* This non-const diagnostic surface lives in RDRAM. It is drawn only when a
 * validated network image is rejected before submission, isolating the RDP path
 * from network image bytes without changing normal page rendering. */
static uint16_t g_rgb5a1_reference_pattern[16 * 16] __attribute__((aligned(16))) =
{
    REF_BORDER_ROW,
    REF_QUADRANT_ROW(RGB5A1_RED, RGB5A1_GREEN),
    REF_QUADRANT_ROW(RGB5A1_RED, RGB5A1_GREEN),
    REF_QUADRANT_ROW(RGB5A1_RED, RGB5A1_GREEN),
    REF_QUADRANT_ROW(RGB5A1_RED, RGB5A1_GREEN),
    REF_QUADRANT_ROW(RGB5A1_RED, RGB5A1_GREEN),
    REF_QUADRANT_ROW(RGB5A1_RED, RGB5A1_GREEN),
    REF_QUADRANT_ROW(RGB5A1_RED, RGB5A1_GREEN),
    REF_QUADRANT_ROW(RGB5A1_RED, RGB5A1_GREEN),
    REF_QUADRANT_ROW(RGB5A1_BLUE, RGB5A1_WHITE),
    REF_QUADRANT_ROW(RGB5A1_BLUE, RGB5A1_WHITE),
    REF_QUADRANT_ROW(RGB5A1_BLUE, RGB5A1_WHITE),
    REF_QUADRANT_ROW(RGB5A1_BLUE, RGB5A1_WHITE),
    REF_QUADRANT_ROW(RGB5A1_BLUE, RGB5A1_WHITE),
    REF_QUADRANT_ROW(RGB5A1_BLUE, RGB5A1_WHITE),
    REF_BORDER_ROW,
};

static void set_image_draw_status(const char *status)
{
    g_image_draw_status = status;
}

static void log_image_draw_rejection(const NICE_Block *block, const ImageTransportImage *image)
{
    const NICE_LoadedDocument *loaded;
    ImageTransportStatus status;

    if (g_image_draw_status_logged)
    {
        return;
    }

    loaded = NICE_CurrentLoadedDocument();
    status = ImageTransport_GetStatus();
    debugf("Browser: image draw eligibility=NO reason=%s state=%d nimg=%d currentPage=%lu imagePage=%lu document=%p blockRoute=%s imageRoute=%s block=%dx%d nimg=%ux%u surface=%p pixels=%p stride=%u payload=%lu\n",
        NICE_GetImageDrawStatus(), status.state, status.nimg.validation_state,
        loaded == NULL ? 0ul : (unsigned long)loaded->page_id,
        image == NULL ? 0ul : (unsigned long)image->page_id,
        loaded == NULL ? NULL : (void *)loaded->page,
        block == NULL || block->target == NULL ? "(none)" : block->target,
        image == NULL || image->route == NULL ? "(none)" : image->route,
        block == NULL ? 0 : block->image_width, block == NULL ? 0 : block->image_height,
        image == NULL ? 0 : image->width, image == NULL ? 0 : image->height,
        image == NULL || image->surface == NULL ? NULL : image->surface->buffer,
        image == NULL ? NULL : image->pixels,
        image == NULL ? 0 : image->stride,
        image == NULL ? 0ul : (unsigned long)image->payload_length);
    g_image_draw_status_logged = true;
}

static int max_scroll_value(void)
{
    int visible_height = VIEW_BOTTOM - VIEW_TOP;
    int max_scroll = g_content_height - visible_height;

    if (visible_height < 1 || max_scroll < 0)
    {
        return 0;
    }

    return max_scroll;
}

static uint32_t color_rgb(uint8_t r, uint8_t g, uint8_t b)
{
    return graphics_make_color(r, g, b, 255);
}

static void draw_text(surface_t *display, int x, int y, uint32_t color, const char *text)
{
    graphics_set_color(color, COLOR_TRANSPARENT);
    graphics_draw_text(display, x, y, text);
}

static int text_length(const char *text)
{
    return text == NULL ? 0 : (int)strlen(text);
}

static void draw_text_limited(surface_t *display, int x, int y, uint32_t color, const char *text, int start, int length)
{
    char buffer[TEXT_COLUMNS + 2];
    int full_length = text_length(text);

    if (text == NULL || start < 0 || start >= full_length || length <= 0)
    {
        return;
    }
    if (start + length > full_length)
    {
        length = full_length - start;
    }
    if (length > TEXT_COLUMNS)
    {
        length = TEXT_COLUMNS;
    }
    snprintf(buffer, sizeof(buffer), "%.*s", length, text + start);
    draw_text(display, x, y, color, buffer);
}

/* graphics_draw_box does not clip to the browser viewport.  A focused link can
 * wrap across several layout rows, so clamp its shared highlight before it
 * reaches the framebuffer edge. */
static void draw_viewport_box(surface_t *display, int x, int top, int width, int bottom, uint32_t color)
{
    if (top < VIEW_TOP)
    {
        top = VIEW_TOP;
    }
    if (bottom > VIEW_BOTTOM)
    {
        bottom = VIEW_BOTTOM;
    }
    if (bottom > top)
    {
        graphics_draw_box(display, x, top, width, bottom - top, color);
    }
}

static void draw_centered_text(surface_t *display, int x, int width, int y, uint32_t color, const char *text)
{
    int text_width = text_length(text) * 8;
    int text_x = x + (width - text_width) / 2;

    if (text_x < x + 2)
    {
        text_x = x + 2;
    }
    draw_text(display, text_x, y, color, text);
}

static int find_wrap(const char *text, int start, int max_columns)
{
    int length = text_length(text);
    int end = start + max_columns;
    int probe;

    if (text == NULL || start < 0 || start >= length || max_columns <= 0)
    {
        return 0;
    }

    if (end >= length)
    {
        return length - start;
    }

    probe = end;
    while (probe > start + 8 && text[probe] != ' ')
    {
        probe--;
    }
    if (probe <= start + 8)
    {
        probe = end;
    }

    return probe - start;
}

static int block_line_height(NICE_BlockType type)
{
    switch (type)
    {
    case NICE_TITLE: return 16;
    case NICE_H1: return 15;
    case NICE_H2: return 13;
    default: return 11;
    }
}

static int block_spacing(NICE_BlockType type)
{
    switch (type)
    {
    case NICE_TITLE: return 10;
    case NICE_H1: return 9;
    case NICE_H2: return 8;
    case NICE_HORIZONTAL_RULE: return 10;
    case NICE_IMAGE_PLACEHOLDER: return 10;
    default: return 6;
    }
}

static bool image_has_caption(const NICE_Block *block)
{
    return block != NULL && block->text != NULL && block->text[0] != '\0' &&
        strcmp(block->text, "Image placeholder") != 0;
}

static int image_content_width(const NICE_Block *block)
{
    int width = block != NULL && block->image_width > 0 ? block->image_width : 160;
    int maximum = VIEW_RIGHT - VIEW_LEFT - IMAGE_BORDER_SIZE * 2;

    return width > maximum ? maximum : width;
}

static int image_content_height(const NICE_Block *block)
{
    return block != NULL && block->image_height > 0 ? block->image_height : 56;
}

static int image_frame_width(const NICE_Block *block)
{
    return image_content_width(block) + IMAGE_BORDER_SIZE * 2;
}

static int image_frame_height(const NICE_Block *block)
{
    return image_content_height(block) + IMAGE_BORDER_SIZE * 2;
}

static int image_layout_height(const NICE_Block *block)
{
    int height = image_frame_height(block);

    if (image_has_caption(block))
    {
        height += IMAGE_CAPTION_GAP + IMAGE_CAPTION_HEIGHT;
    }
    return height;
}

static int image_frame_x(const NICE_Block *block)
{
    return VIEW_LEFT + (VIEW_RIGHT - VIEW_LEFT - image_frame_width(block)) / 2;
}

static bool is_section_block(const NICE_Block *block)
{
    return block != NULL && (block->type == NICE_H1 || block->type == NICE_H2);
}

static bool is_focusable(const NICE_Block *block)
{
    return block != NULL && block->type == NICE_LINK;
}

static void add_layout_item(const NICE_Block *block, int y, int height, int line_start, int line_length, int focus_index)
{
    if (block == NULL || g_layout_count >= MAX_LAYOUT_ITEMS || height <= 0)
    {
        return;
    }

    g_layout[g_layout_count++] = (nice_layout_item_t)
    {
        .block = block,
        .y = y,
        .height = height,
        .line_start = line_start,
        .line_length = line_length,
        .focus_index = focus_index
    };
}

static void layout_text_block(const NICE_Block *block, int *y)
{
    const char *text = block->text == NULL ? "" : block->text;
    int length = text_length(text);
    int line_height = block_line_height(block->type);
    int start = 0;
    int focus_index = is_focusable(block) ? g_focus_count++ : -1;

    if (length == 0)
    {
        add_layout_item(block, *y, line_height, 0, 0, focus_index);
        *y += line_height + block_spacing(block->type);
        return;
    }

    while (start < length)
    {
        int line_length;
        while (start < length && text[start] == ' ')
        {
            start++;
        }
        if (start >= length)
        {
            break;
        }
        line_length = find_wrap(text, start, TEXT_COLUMNS);
        if (line_length <= 0)
        {
            break;
        }
        add_layout_item(block, *y, line_height, start, line_length, focus_index);
        *y += line_height;
        start += line_length;
        if (start < length && text[start] == ' ')
        {
            start++;
        }
    }

    *y += block_spacing(block->type);
}

static void layout_image_block(const NICE_Block *block, int *y)
{
    int height = image_layout_height(block);
    add_layout_item(block, *y, height, 0, 0, -1);
    *y += height + block_spacing(block->type);
}

static void layout_rule_block(const NICE_Block *block, int *y)
{
    add_layout_item(block, *y, 7, 0, 0, -1);
    *y += 7 + block_spacing(block->type);
}

static void clamp_scroll(void)
{
    int max_scroll = max_scroll_value();
    if (g_scroll_y < 0)
    {
        g_scroll_y = 0;
    }
    if (g_scroll_y > max_scroll)
    {
        g_scroll_y = max_scroll;
    }
    g_smooth_scroll = g_scroll_y;
}

static void ensure_focus_visible(void)
{
    for (int i = 0; i < g_layout_count; i++)
    {
        if (g_layout[i].focus_index != g_focus_index)
        {
            continue;
        }

        if (g_layout[i].y < g_scroll_y)
        {
            g_scroll_y = g_layout[i].y;
        }
        else if (g_layout[i].y + g_layout[i].height > g_scroll_y + (VIEW_BOTTOM - VIEW_TOP))
        {
            g_scroll_y = g_layout[i].y + g_layout[i].height - (VIEW_BOTTOM - VIEW_TOP);
        }
        clamp_scroll();
        g_smooth_scroll = g_scroll_y;
        return;
    }
}

void NICE_RenderPage(const NICE_Page *page)
{
    int y = 0;
    int block_count = 0;
    g_page = page;
    g_layout_count = 0;
    g_focus_count = 0;
    g_scroll_y = 0;
    g_focus_index = 0;
    g_smooth_scroll = 0;
    g_content_height = 0;
    g_image_first_draw_logged = false;
    g_image_draw_mismatch_logged = false;
    g_image_draw_status = "IMAGE_DRAW_PENDING";
    g_image_draw_status_logged = false;
    g_image_reference_pattern_logged = false;

    if (page == NULL || page->blocks == NULL)
    {
        return;
    }

    for (const NICE_Block *block = page->blocks; block_count < MAX_DOCUMENT_BLOCKS; block++, block_count++)
    {
        if (block == NULL || block->type == NICE_END)
        {
            break;
        }

        switch (block->type)
        {
        case NICE_IMAGE_PLACEHOLDER:
            layout_image_block(block, &y);
            break;
        case NICE_HORIZONTAL_RULE:
            layout_rule_block(block, &y);
            break;
        default:
            layout_text_block(block, &y);
            break;
        }
    }

    g_content_height = y;
    clamp_scroll();
}

void NICE_SetScroll(int scroll_y)
{
    g_scroll_y = scroll_y;
    clamp_scroll();
}

void NICE_SetFocus(int index)
{
    if (g_focus_count == 0)
    {
        g_focus_index = 0;
        return;
    }
    if (index < 0)
    {
        index = g_focus_count - 1;
    }
    if (index >= g_focus_count)
    {
        index = 0;
    }
    if (index < 0)
    {
        index = 0;
    }
    if (index >= g_focus_count)
    {
        index = g_focus_count - 1;
    }
    g_focus_index = index;
    ensure_focus_visible();
}

void NICE_JumpToNextSection(void)
{
    int view_anchor = g_scroll_y + 8;

    for (int i = 0; i < g_layout_count; i++)
    {
        if (g_layout[i].y <= view_anchor || !is_section_block(g_layout[i].block))
        {
            continue;
        }

        NICE_SetScroll(g_layout[i].y);
        return;
    }

    NICE_SetScroll(max_scroll_value());
}

void NICE_HandleInput(joypad_buttons_t pressed, joypad_inputs_t inputs)
{
    if (pressed.d_down)
    {
        NICE_SetFocus(g_focus_index + 1);
    }
    else if (pressed.d_up)
    {
        NICE_SetFocus(g_focus_index - 1);
    }

    if (pressed.c_up)
    {
        NICE_SetScroll(g_scroll_y - 94);
    }
    else if (pressed.c_down)
    {
        NICE_SetScroll(g_scroll_y + 94);
    }
    else if (pressed.c_left)
    {
        NICE_SetScroll(0);
    }
    else if (pressed.c_right)
    {
        NICE_JumpToNextSection();
    }

    if (inputs.stick_y > 32)
    {
        if (g_smooth_scroll > 0)
        {
            g_smooth_scroll -= 6;
        }
        NICE_SetScroll(g_smooth_scroll);
    }
    else if (inputs.stick_y < -32)
    {
        if (g_smooth_scroll < max_scroll_value())
        {
            g_smooth_scroll += 6;
        }
        NICE_SetScroll(g_smooth_scroll);
    }
    else
    {
        g_smooth_scroll = g_scroll_y;
    }
}

static uint32_t block_color(const NICE_Block *block, bool focused)
{
    if (focused)
    {
        return color_rgb(255, 246, 118);
    }

    switch (block->type)
    {
    case NICE_TITLE: return color_rgb(255, 255, 255);
    case NICE_H1: return color_rgb(114, 232, 255);
    case NICE_H2: return color_rgb(174, 220, 255);
    case NICE_LIST_ITEM: return color_rgb(230, 236, 245);
    case NICE_QUOTE: return color_rgb(186, 202, 224);
    case NICE_LINK: return color_rgb(130, 214, 255);
    default: return color_rgb(232, 236, 242);
    }
}

static int block_x(const NICE_Block *block)
{
    switch (block->type)
    {
    case NICE_LIST_ITEM: return VIEW_LEFT + 14;
    case NICE_QUOTE: return VIEW_LEFT + 13;
    case NICE_LINK: return VIEW_LEFT + 12;
    default: return VIEW_LEFT;
    }
}

static bool image_matches_block(const NICE_Block *block, ImageTransportImage *image)
{
    const NICE_LoadedDocument *loaded = NICE_CurrentLoadedDocument();

    if (block == NULL || image == NULL || loaded == NULL || !loaded->loaded)
    {
        set_image_draw_status("IMAGE_DOCUMENT_MISMATCH");
        return false;
    }
    if (!ImageTransport_GetValidatedImage(image))
    {
        set_image_draw_status("IMAGE_SURFACE_INVALID");
        return false;
    }
    if (image->page_id != loaded->page_id)
    {
        set_image_draw_status("IMAGE_PAGE_ID_MISMATCH");
        return false;
    }
    if (block->target == NULL || image->route == NULL || strcmp(block->target, image->route) != 0)
    {
        set_image_draw_status("IMAGE_ROUTE_MISMATCH");
        return false;
    }
    if (block->image_width != image->width || block->image_height != image->height)
    {
        set_image_draw_status("IMAGE_DIMENSION_MISMATCH");
        return false;
    }
    if (image->pixels == NULL || image->surface == NULL || image->surface->buffer == NULL)
    {
        set_image_draw_status("IMAGE_PIXEL_POINTER_NULL");
        return false;
    }
    return true;
}

static bool image_status_matches_block(const NICE_Block *block, const ImageTransportStatus *status)
{
    return block != NULL && block->target != NULL && status != NULL && status->route != NULL &&
        strcmp(block->target, status->route) == 0;
}

static void draw_image_caption(surface_t *display, const NICE_Block *block, int frame_y)
{
    char caption[TEXT_COLUMNS + 1];
    int max_chars;
    int frame_x;

    if (!image_has_caption(block))
    {
        return;
    }

    max_chars = (image_content_width(block) - 12) / 8;
    if (max_chars < 1)
    {
        return;
    }
    if (max_chars > TEXT_COLUMNS)
    {
        max_chars = TEXT_COLUMNS;
    }

    snprintf(caption, sizeof(caption), "%.*s", max_chars, block->text);
    frame_x = image_frame_x(block);
    draw_text(display, frame_x + 6, frame_y + image_frame_height(block) + IMAGE_CAPTION_GAP,
        color_rgb(126, 216, 244), caption);
}

static void draw_image_placeholder(surface_t *display, const nice_layout_item_t *item, int y)
{
    ImageTransportStatus status = ImageTransport_GetStatus();
    ImageTransportImage image;
    char detail[32];
    char progress[14];
    int frame_x = image_frame_x(item->block);
    int frame_width = image_frame_width(item->block);
    int frame_height = image_frame_height(item->block);
    int content_x = frame_x + IMAGE_BORDER_SIZE;
    int content_y = y + IMAGE_BORDER_SIZE;
    int content_width = image_content_width(item->block);
    int content_height = image_content_height(item->block);
    bool active_block = image_status_matches_block(item->block, &status);

    graphics_draw_line(display, frame_x, y, frame_x + frame_width - 1, y, color_rgb(82, 122, 172));
    graphics_draw_line(display, frame_x, y + frame_height - 1, frame_x + frame_width - 1, y + frame_height - 1, color_rgb(82, 122, 172));
    graphics_draw_line(display, frame_x, y, frame_x, y + frame_height - 1, color_rgb(82, 122, 172));
    graphics_draw_line(display, frame_x + frame_width - 1, y, frame_x + frame_width - 1, y + frame_height - 1, color_rgb(82, 122, 172));
    if (active_block && image_matches_block(item->block, &image) &&
        item->height == image_layout_height(item->block))
    {
        draw_image_caption(display, item->block, y);
        return;
    }

    graphics_draw_box(display, content_x, content_y, content_width, content_height, color_rgb(18, 28, 52));
    if (active_block && (status.state == IMAGE_TRANSPORT_OPENING || status.state == IMAGE_TRANSPORT_READING ||
        status.state == IMAGE_TRANSPORT_VERIFYING || status.state == IMAGE_TRANSPORT_VALIDATING_NIMG)
        && content_width >= 112 && content_height >= 42)
    {
        draw_centered_text(display, content_x, content_width, content_y + content_height / 2 - 22,
            color_rgb(178, 230, 246), "Loading image...");
        if (status.expected_bytes == 0)
        {
            draw_centered_text(display, content_x, content_width, content_y + content_height / 2 - 6,
                color_rgb(226, 236, 246), "Opening stream...");
        }
        else
        {
            uint32_t filled = (status.bytes_received * 10u + status.expected_bytes - 1u) / status.expected_bytes;
            if (filled > 10u) filled = 10u;
            for (uint32_t index = 0; index < 10u; index++)
            {
                progress[index] = index < filled ? '#' : '-';
            }
            progress[10] = '\0';
            snprintf(detail, sizeof(detail), "[%s]", progress);
            draw_centered_text(display, content_x, content_width, content_y + content_height / 2 - 6,
                color_rgb(142, 236, 164), detail);
            snprintf(detail, sizeof(detail), "%lu / %lu B",
                (unsigned long)status.bytes_received, (unsigned long)status.expected_bytes);
            draw_centered_text(display, content_x, content_width, content_y + content_height / 2 + 10,
                color_rgb(226, 236, 246), detail);
        }
    }
    else if (active_block && status.state == IMAGE_TRANSPORT_COMPLETE && status.nimg.validation_state == IMAGE_NIMG_VALID)
    {
        draw_centered_text(display, content_x, content_width, content_y + content_height / 2 - 4,
            color_rgb(255, 214, 146), "IMAGE READY");
    }
    else if (active_block && status.state == IMAGE_TRANSPORT_FAILED)
    {
        draw_centered_text(display, content_x, content_width, content_y + content_height / 2 - 8,
            color_rgb(255, 178, 154), "IMAGE UNAVAILABLE");
        draw_centered_text(display, content_x, content_width, content_y + content_height / 2 + 6,
            color_rgb(226, 236, 246), status.error == NULL ? "IMAGE FAILED" : status.error);
    }
    else if (content_width >= 112 && content_height >= 20)
    {
        draw_centered_text(display, content_x, content_width, content_y + content_height / 2 - 4,
            color_rgb(130, 196, 220), "IMAGE AVAILABLE");
    }

    draw_image_caption(display, item->block, y);
}

bool NICE_HasValidatedImage(void)
{
    ImageTransportImage image;

    return ImageTransport_GetValidatedImage(&image) && image.pixels != NULL &&
        image.surface != NULL && image.surface->buffer != NULL;
}

void NICE_DrawValidatedImage(void)
{
    ImageTransportImage image;
    const NICE_LoadedDocument *loaded = NICE_CurrentLoadedDocument();
    bool saw_image_placeholder = false;

    if (!ImageTransport_GetValidatedImage(&image) || image.surface == NULL)
    {
        set_image_draw_status("IMAGE_SURFACE_INVALID");
        log_image_draw_rejection(NULL, NULL);
        return;
    }

    for (int index = 0; index < g_layout_count; index++)
    {
        const nice_layout_item_t *item = &g_layout[index];
        const NICE_Block *block = item->block;
        int x;
        int y;
        int source_x = 0;
        int source_y = 0;
        int draw_width = image.width;
        int draw_height = image.height;

        if (block == NULL || block->type != NICE_IMAGE_PLACEHOLDER)
        {
            continue;
        }
        saw_image_placeholder = true;
        if (!image_matches_block(block, &image))
        {
            log_image_draw_rejection(block, &image);
            continue;
        }
        if (item->height != image_layout_height(block))
        {
            if (!g_image_draw_mismatch_logged)
            {
                debugf("Browser: image draw rejected; reserved height %d differs from image layout height %d\n", item->height, image_layout_height(block));
                g_image_draw_mismatch_logged = true;
            }
            set_image_draw_status("IMAGE_DIMENSION_MISMATCH");
            log_image_draw_rejection(block, &image);
            return;
        }

        x = image_frame_x(block) + IMAGE_BORDER_SIZE;
        y = VIEW_TOP + item->y - g_scroll_y + IMAGE_BORDER_SIZE;
        if (x < VIEW_LEFT || x + draw_width > VIEW_RIGHT || y + draw_height <= VIEW_TOP || y >= VIEW_BOTTOM)
        {
            set_image_draw_status("IMAGE_CLIPPED_EMPTY");
            log_image_draw_rejection(block, &image);
            return;
        }
        if (y < VIEW_TOP)
        {
            source_y = VIEW_TOP - y;
            draw_height -= source_y;
            y = VIEW_TOP;
        }
        if (y + draw_height > VIEW_BOTTOM)
        {
            draw_height = VIEW_BOTTOM - y;
        }
        if (draw_width <= 0 || draw_height <= 0)
        {
            set_image_draw_status("IMAGE_CLIPPED_EMPTY");
            log_image_draw_rejection(block, &image);
            return;
        }

        set_image_draw_status("IMAGE_DRAW_ELIGIBLE");
        data_cache_hit_writeback((volatile void *)image.pixels, image.payload_length);
        rdpq_tex_blit(image.surface, x, y, &(rdpq_blitparms_t){
            .s0 = source_x,
            .t0 = source_y,
            .width = draw_width,
            .height = draw_height,
        });
        set_image_draw_status("IMAGE_DRAW_SUBMITTED");
        if (!g_image_first_draw_logged)
        {
            debugf("Browser: image draw eligibility=YES state=%d nimg=%d currentPage=%lu imagePage=%lu document=%p blockRoute=%s imageRoute=%s block=%dx%d nimg=%ux%u surface=%p pixels=%p stride=%u payload=%lu submitted=YES\n",
                ImageTransport_GetStatus().state, ImageTransport_GetStatus().nimg.validation_state,
                loaded == NULL ? 0ul : (unsigned long)loaded->page_id, (unsigned long)image.page_id,
                loaded == NULL ? NULL : (void *)loaded->page, block->target, image.route,
                block->image_width, block->image_height, image.width, image.height,
                (void *)image.surface->buffer, (void *)image.pixels, image.stride,
                (unsigned long)image.payload_length);
            g_image_first_draw_logged = true;
        }
        return;
    }

    if (strcmp(NICE_GetImageDrawStatus(), "IMAGE_DRAW_PENDING") == 0)
    {
        set_image_draw_status(saw_image_placeholder ? "IMAGE_DRAW_NOT_SUBMITTED" : "IMAGE_BLOCK_NOT_FOUND");
    }
    log_image_draw_rejection(NULL, &image);
}

bool NICE_DrawImageReferencePatternIfNeeded(void)
{
    ImageTransportStatus status = ImageTransport_GetStatus();
    surface_t pattern;

    if (status.state != IMAGE_TRANSPORT_COMPLETE || status.nimg.validation_state != IMAGE_NIMG_VALID ||
        strcmp(NICE_GetImageDrawStatus(), "IMAGE_DRAW_PENDING") == 0 ||
        strcmp(NICE_GetImageDrawStatus(), "IMAGE_DRAW_SUBMITTED") == 0 ||
        strcmp(NICE_GetImageDrawStatus(), "IMAGE_DRAW_COMPLETED") == 0)
    {
        return false;
    }

    pattern = surface_make_linear(g_rgb5a1_reference_pattern, FMT_RGBA16, 16, 16);
    data_cache_hit_writeback(g_rgb5a1_reference_pattern, sizeof(g_rgb5a1_reference_pattern));
    rdpq_tex_blit(&pattern, VIEW_RIGHT - 22, VIEW_TOP + 4, NULL);
    if (!g_image_reference_pattern_logged)
    {
        debugf("Browser: RGB5A1 reference submitted red=F801 green=07C1 blue=003F white=FFFF black=0001 stride=%u buffer=%p\n",
            pattern.stride, pattern.buffer);
        g_image_reference_pattern_logged = true;
    }
    return true;
}

void NICE_MarkImageDrawCompleted(void)
{
    if (strcmp(NICE_GetImageDrawStatus(), "IMAGE_DRAW_SUBMITTED") == 0)
    {
        set_image_draw_status("IMAGE_DRAW_COMPLETED");
        debugf("Browser: image draw completed after rdpq_detach_wait\n");
    }
}

const char *NICE_GetImageDrawStatus(void)
{
    return g_image_draw_status;
}

static void draw_scrollbar(surface_t *display)
{
    int track_height = VIEW_BOTTOM - VIEW_TOP;
    int thumb_height;
    int thumb_y;

    if (track_height <= 0)
    {
        return;
    }

    graphics_draw_box(display, 311, VIEW_TOP, 3, track_height, color_rgb(18, 28, 50));

    if (g_content_height <= track_height)
    {
        graphics_draw_box(display, 311, VIEW_TOP, 3, track_height, color_rgb(75, 132, 180));
        return;
    }

    thumb_height = track_height * track_height / g_content_height;
    if (thumb_height < 12)
    {
        thumb_height = 12;
    }
    thumb_y = VIEW_TOP + (g_scroll_y * (track_height - thumb_height)) / (g_content_height - track_height);
    graphics_draw_box(display, 311, thumb_y, 3, thumb_height, color_rgb(105, 196, 220));
}

bool NICE_HasAppBanner(void)
{
    if (!g_app_banner_attempted)
    {
        g_app_banner_attempted = true;
        g_app_banner = sprite_load("rom:/nice64_banner.sprite");
        if (g_app_banner == NULL)
        {
            debugf("NICE: app banner unavailable\n");
        }
    }
    return g_app_banner != NULL;
}

void NICE_DrawAppBanner(void)
{
    if (g_app_banner == NULL)
    {
        return;
    }

    rdpq_sprite_blit(g_app_banner, 5.0f, 2.0f, &(rdpq_blitparms_t){
        .scale_x = 0.68f,
        .scale_y = 0.68f
    });
}

void NICE_SetHomeBackgroundEnabled(bool enabled)
{
    g_home_background_enabled = enabled;
}

static bool nice_has_home_background(void)
{
    if (!g_home_background_attempted)
    {
        g_home_background_attempted = true;
        g_home_background = sprite_load("rom:/browser_home_background.sprite");
        if (g_home_background == NULL)
        {
            debugf("NICE: home background unavailable\n");
        }
    }
    return g_home_background != NULL;
}

static void draw_home_artwork(surface_t *display)
{
    bool has_background = nice_has_home_background();
    bool has_banner = NICE_HasAppBanner();

    if (!has_background && !has_banner)
    {
        return;
    }

    rdpq_attach(display, NULL);
    rdpq_set_mode_standard();
    if (has_background)
    {
        rdpq_sprite_blit(g_home_background, 0.0f, (float)VIEW_TOP, NULL);
    }
    if (has_banner)
    {
        NICE_DrawAppBanner();
    }
    rdpq_detach_wait();
}

void NICE_Draw(surface_t *display)
{
    g_page = NICE_CurrentDocument();

    graphics_draw_box(display, 0, 0, SCREEN_WIDTH, SCREEN_HEIGHT, color_rgb(7, 10, 24));
    graphics_draw_box(display, 0, 0, SCREEN_WIDTH, 30, color_rgb(16, 30, 68));
    graphics_draw_box(display, 0, 2, SCREEN_WIDTH, 2, color_rgb(54, 99, 176));
    draw_text(display, 205, 7, color_rgb(255, 255, 255), "NICE Browser");
    draw_text(display, 236, 19, color_rgb(132, 226, 255), "ROM v1.4.0");
    graphics_draw_line(display, 0, 30, SCREEN_WIDTH - 1, 30, color_rgb(66, 114, 192));

    if (g_home_background_enabled)
    {
        draw_home_artwork(display);
    }

    for (int i = 0; i < g_layout_count; i++)
    {
        const nice_layout_item_t *item = &g_layout[i];
        const NICE_Block *block = item->block;
        int y = VIEW_TOP + item->y - g_scroll_y;
        bool focused = item->focus_index == g_focus_index && item->focus_index >= 0;

        if (block == NULL || item->height <= 0)
        {
            continue;
        }

        if (y < VIEW_TOP || y + item->height > VIEW_BOTTOM)
        {
            continue;
        }

        if (focused && (i == 0 || g_layout[i - 1].focus_index != item->focus_index))
        {
            int group_bottom = item->y + item->height;
            int highlight_top;
            int highlight_bottom;
            for (int next = i + 1; next < g_layout_count && g_layout[next].focus_index == item->focus_index; next++) group_bottom = g_layout[next].y + g_layout[next].height;
            highlight_top = VIEW_TOP + item->y - g_scroll_y;
            highlight_bottom = VIEW_TOP + group_bottom - g_scroll_y;
            draw_viewport_box(display, VIEW_LEFT - 6, highlight_top - 3, VIEW_RIGHT - VIEW_LEFT + 12, highlight_bottom + 2, color_rgb(60, 76, 152));
            draw_viewport_box(display, VIEW_LEFT - 3, highlight_top - 1, VIEW_RIGHT - VIEW_LEFT + 6, highlight_bottom, color_rgb(30, 44, 98));
        }

        switch (block->type)
        {
        case NICE_IMAGE_PLACEHOLDER:
            draw_image_placeholder(display, item, y);
            break;
        case NICE_HORIZONTAL_RULE:
            graphics_draw_line(display, VIEW_LEFT, y + 3, VIEW_RIGHT, y + 3, color_rgb(76, 104, 148));
            break;
        case NICE_LIST_ITEM:
            draw_text(display, VIEW_LEFT, y, color_rgb(140, 245, 255), "-");
            draw_text_limited(display, block_x(block), y, block_color(block, focused), block->text, item->line_start, item->line_length);
            break;
        case NICE_QUOTE:
            graphics_draw_line(display, VIEW_LEFT, y - 1, VIEW_LEFT, y + item->height + 1, color_rgb(80, 128, 170));
            draw_text_limited(display, block_x(block), y, block_color(block, focused), block->text, item->line_start, item->line_length);
            break;
        case NICE_LINK:
            draw_text(display, VIEW_LEFT, y, focused ? color_rgb(255, 246, 118) : color_rgb(105, 196, 220), ">");
            draw_text_limited(display, block_x(block), y, block_color(block, focused), block->text, item->line_start, item->line_length);
            break;
        default:
            draw_text_limited(display, block_x(block), y, block_color(block, focused), block->text, item->line_start, item->line_length);
            break;
        }
    }

    draw_scrollbar(display);
    graphics_draw_box(display, 0, 218, SCREEN_WIDTH, 22, color_rgb(12, 18, 44));
    graphics_draw_line(display, 0, 217, SCREEN_WIDTH - 1, 217, color_rgb(66, 114, 192));
    draw_text(display, 8, 227, color_rgb(190, 210, 230), "L:Back R:Fwd A:Open Z:Keys Start:Menu");
}

int NICE_GetScroll(void)
{
    return g_scroll_y;
}

int NICE_GetFocus(void)
{
    return g_focus_index;
}

int NICE_GetContentHeight(void)
{
    return g_content_height;
}

const char *NICE_GetFocusedTarget(void)
{
    for (int i = 0; i < g_layout_count; i++)
    {
        if (g_layout[i].focus_index == g_focus_index && g_layout[i].block != NULL)
        {
            return g_layout[i].block->target;
        }
    }
    return NULL;
}
