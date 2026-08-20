#include "browser_shell.h"

#include "browser_audio.h"
#include "browser_transport.h"
#include "browser_profile.h"
#include "document_loader.h"
#include "image_transport.h"
#include "nice_document.h"

#include <ctype.h>
#include <stdio.h>
#include <string.h>

#define COLOR_TRANSPARENT graphics_make_color(0, 0, 0, 0)
#define BROWSER_INPUT_MAX 128u
#define BROWSER_RESOURCE_MAX 128u
#define KEYBOARD_ROWS 7u
#define MENU_ITEMS 11u
#define OPTIONS_ITEMS 5u
#define PROFILE_VISIBLE_ROWS 8u
#define PROFILE_FAVORITE_VISIBLE_ROWS 5u
#define HISTORY_LIMIT 16u
#define CACHE_LIMIT 16u
#define CACHE_TEXT_BYTES BROWSER_TRANSPORT_TEXT_POOL_SIZE
#define HOME_RESOURCE "nice://home"
#define BROWSER_POLL_FRAME_INTERVAL 45u
#define BROWSER_POLL_MAX_ATTEMPTS 27u
#define BROWSER_IMAGE_ROUTE_LENGTH 16u
#define BROWSER_LOADING_ANIMATION_FRAMES 24u
#define BROWSER_HOME_PIN_LIMIT 6u
#define BROWSER_BANNER_ON_LOADING_ERROR 0

typedef enum browser_ui_mode_e
{
    BROWSER_MODE_PAGE,
    BROWSER_MODE_MENU,
    BROWSER_MODE_KEYBOARD,
    BROWSER_MODE_LOADING,
    BROWSER_MODE_ERROR,
    BROWSER_MODE_PROFILE_LIST,
    BROWSER_MODE_CONFIRM_REMOVE_FAVORITE,
    BROWSER_MODE_OPTIONS,
    BROWSER_MODE_CONFIRM_CLEAR
} BrowserUIMode;

typedef enum browser_clear_action_e
{
    BROWSER_CLEAR_NONE = 0,
    BROWSER_CLEAR_HISTORY,
    BROWSER_CLEAR_RECENTS,
    BROWSER_CLEAR_CACHE
} BrowserClearAction;

typedef enum browser_profile_view_e
{
    BROWSER_PROFILE_VIEW_FAVORITES,
    BROWSER_PROFILE_VIEW_HISTORY,
    BROWSER_PROFILE_VIEW_RECENTS
} BrowserProfileView;

typedef enum keyboard_key_type_e
{
    KEY_TEXT,
    KEY_SPACE,
    KEY_BACKSPACE,
    KEY_CLEAR,
    KEY_CANCEL,
    KEY_ENTER
} KeyboardKeyType;

typedef enum keyboard_mode_e
{
    KEYBOARD_MODE_URL = 0,
    KEYBOARD_MODE_SEARCH
} KeyboardMode;

typedef struct keyboard_key_s
{
    const char *label;
    const char *value;
    KeyboardKeyType type;
} KeyboardKey;

typedef struct cache_entry_s
{
    bool valid;
    char resource[BROWSER_RESOURCE_MAX];
    char canonical_url[BROWSER_INPUT_MAX + 1u];
    uint32_t page_id;
    uint32_t last_used;
    NICE_Block blocks[BROWSER_TRANSPORT_MAX_BLOCKS + 1u];
    NICE_Page page;
} CacheEntry;

typedef struct history_entry_s
{
    bool valid;
    char resource[BROWSER_RESOURCE_MAX];
    char title[64];
    char canonical_url[BROWSER_INPUT_MAX + 1u];
    uint32_t page_id;
    int scroll;
    int focus;
} HistoryEntry;

typedef struct browser_image_request_s
{
    bool valid;
    bool terminal_logged;
    uint32_t page_id;
    const NICE_Page *document;
    char route[BROWSER_IMAGE_ROUTE_LENGTH + 1u];
    uint16_t expected_width;
    uint16_t expected_height;
} BrowserImageRequest;

static uint8_t g_future_page_cache[1024 * 1024];
static CacheEntry g_cache[CACHE_LIMIT];
static HistoryEntry g_history[HISTORY_LIMIT];
static BrowserUIMode g_mode;
static BrowserUIMode g_previous_mode;
static uint32_t g_cache_clock;
static int g_history_count;
static int g_history_index;
static uint8_t g_menu_index;
static uint8_t g_options_index;
static uint8_t g_confirm_index;
static BrowserClearAction g_pending_clear_action;
static bool g_network_document_ready;
static bool g_network_document_failed;
static char g_current_resource[BROWSER_RESOURCE_MAX] = HOME_RESOURCE;
static char g_status_line[96] = "Ready";
static char g_error_stage[64] = "";
static char g_error_detail[96] = "";
static char g_error_bytes[64] = "";
static char g_error_resource[BROWSER_RESOURCE_MAX] = HOME_RESOURCE;
static bool g_poll_active;
static bool g_poll_add_history;
static uint32_t g_poll_frames;
static uint32_t g_poll_attempts;
static char g_poll_route[BROWSER_RESOURCE_MAX];
static char g_poll_origin[BROWSER_RESOURCE_MAX];
static char g_profile_pending_url[BROWSER_INPUT_MAX + 1u];
static char g_current_canonical_url[BROWSER_INPUT_MAX + 1u];
static BrowserProfileView g_profile_view;
static uint16_t g_profile_indices[BROWSER_PROFILE_HISTORY_LIMIT];
static uint16_t g_profile_list_count;
static uint16_t g_profile_list_index;
static uint16_t g_profile_remove_index;
static char g_input_buffer[BROWSER_INPUT_MAX + 1u];
static char g_input_backup[BROWSER_INPUT_MAX + 1u];
static size_t g_input_length;
static size_t g_cursor_position;
static uint8_t g_keyboard_row;
static uint8_t g_keyboard_col;
static KeyboardMode g_keyboard_mode;
static bool g_document_work_this_frame;
static bool g_loading_add_history;
static uint32_t g_loading_animation_frames;
static BrowserImageRequest g_image_request;
static bool g_browser_started;
static bool g_rdp_image_work_last_frame;
static bool g_banner_policy_initialized;
static bool g_banner_policy_last_frame;
static BrowserUIMode g_banner_policy_last_mode;
static NICE_Block g_home_blocks[8u + BROWSER_HOME_PIN_LIMIT];
static char g_home_titles[BROWSER_HOME_PIN_LIMIT][BROWSER_INPUT_MAX + 1u];
static char g_home_urls[BROWSER_HOME_PIN_LIMIT][BROWSER_INPUT_MAX + 1u];
static NICE_Page g_home_page = { .title = "NICE Home", .blocks = g_home_blocks };

static bool image_transport_is_active(ImageTransportState state);
static bool load_browser_document_internal(const char *resource, bool add_history, bool prefer_cache, bool is_poll_request);
static void profile_open_view(BrowserProfileView view);
static void keyboard_open_with_purpose(KeyboardMode purpose);

static bool Browser_ShouldDrawAppBanner(const char **reason)
{
    bool is_home_page = strcmp(g_current_resource, HOME_RESOURCE) == 0;
    bool is_web_resource = strncmp(g_current_resource, "browser://", 10) == 0;
    bool show = false;

    /* Home owns its banner through NICE_Draw's home-artwork path. */
    if (is_home_page)
    {
        if (reason != NULL) *reason = "HOME_ARTWORK";
        return false;
    }

    switch (g_mode)
    {
    case BROWSER_MODE_LOADING:
    case BROWSER_MODE_ERROR:
#if BROWSER_BANNER_ON_LOADING_ERROR
        show = true;
        if (reason != NULL) *reason = "LOCAL_LOADING_ERROR_UI";
#else
        if (reason != NULL) *reason = "LOADING_ERROR_DISABLED";
#endif
        break;
    case BROWSER_MODE_MENU:
    case BROWSER_MODE_KEYBOARD:
    case BROWSER_MODE_PROFILE_LIST:
    case BROWSER_MODE_CONFIRM_REMOVE_FAVORITE:
    case BROWSER_MODE_OPTIONS:
    case BROWSER_MODE_CONFIRM_CLEAR:
        show = true;
        if (reason != NULL) *reason = "LOCAL_SHELL_UI";
        break;
    case BROWSER_MODE_PAGE:
        show = !is_web_resource;
        if (reason != NULL) *reason = show ? "LOCAL_PAGE" : "WEB_CONTENT";
        break;
    default:
        if (reason != NULL) *reason = "UNSUPPORTED_MODE";
        break;
    }

    return show && NICE_HasAppBanner();
}

static const KeyboardKey g_keyboard_row0[] =
{
    { "A", "A", KEY_TEXT }, { "B", "B", KEY_TEXT }, { "C", "C", KEY_TEXT }, { "D", "D", KEY_TEXT }, { "E", "E", KEY_TEXT },
    { "F", "F", KEY_TEXT }, { "G", "G", KEY_TEXT }, { "H", "H", KEY_TEXT }, { "I", "I", KEY_TEXT }, { "J", "J", KEY_TEXT }
};

static const KeyboardKey g_keyboard_row1[] =
{
    { "K", "K", KEY_TEXT }, { "L", "L", KEY_TEXT }, { "M", "M", KEY_TEXT }, { "N", "N", KEY_TEXT }, { "O", "O", KEY_TEXT },
    { "P", "P", KEY_TEXT }, { "Q", "Q", KEY_TEXT }, { "R", "R", KEY_TEXT }, { "S", "S", KEY_TEXT }, { "T", "T", KEY_TEXT }
};

static const KeyboardKey g_keyboard_row2[] =
{
    { "U", "U", KEY_TEXT }, { "V", "V", KEY_TEXT }, { "W", "W", KEY_TEXT }, { "X", "X", KEY_TEXT }, { "Y", "Y", KEY_TEXT },
    { "Z", "Z", KEY_TEXT }, { "0", "0", KEY_TEXT }, { "1", "1", KEY_TEXT }, { "2", "2", KEY_TEXT }, { "3", "3", KEY_TEXT }
};

static const KeyboardKey g_keyboard_row3[] =
{
    { "4", "4", KEY_TEXT }, { "5", "5", KEY_TEXT }, { "6", "6", KEY_TEXT }, { "7", "7", KEY_TEXT }, { "8", "8", KEY_TEXT },
    { "9", "9", KEY_TEXT }, { ".", ".", KEY_TEXT }, { "/", "/", KEY_TEXT }, { ":", ":", KEY_TEXT }, { "-", "-", KEY_TEXT }
};

static const KeyboardKey g_keyboard_row4[] =
{
    { "_", "_", KEY_TEXT }, { "?", "?", KEY_TEXT }, { "&", "&", KEY_TEXT }, { "=", "=", KEY_TEXT }, { "%", "%", KEY_TEXT },
    { "+", "+", KEY_TEXT }, { "#", "#", KEY_TEXT }, { "@", "@", KEY_TEXT }
};

static const KeyboardKey g_keyboard_row5[] =
{
    { "SPACE", " ", KEY_SPACE }, { "BACK", NULL, KEY_BACKSPACE }, { "CLEAR", NULL, KEY_CLEAR }, { "CANCEL", NULL, KEY_CANCEL }, { "ENTER", NULL, KEY_ENTER }
};

static const KeyboardKey g_keyboard_row6[] =
{
    { ".com", ".com", KEY_TEXT }, { "www.", "www.", KEY_TEXT }, { "https://", "https://", KEY_TEXT }
};

static const KeyboardKey * const g_keyboard_rows[KEYBOARD_ROWS] =
{
    g_keyboard_row0,
    g_keyboard_row1,
    g_keyboard_row2,
    g_keyboard_row3,
    g_keyboard_row4,
    g_keyboard_row5,
    g_keyboard_row6
};

static const uint8_t g_keyboard_counts[KEYBOARD_ROWS] =
{
    10, 10, 10, 10, 8, 5, 3
};

static const char * const g_menu_labels[MENU_ITEMS] =
{
    "Continue",
    "Home",
    "Enter Address",
    "Reload",
    "Back",
    "Forward",
    "History",
    "Recent Addresses",
    "Favorites",
    "Add Favorite",
    "Options"
};

static const char * const g_options_labels[OPTIONS_ITEMS] =
{
    "Browser Test",
    "Clear History",
    "Clear Recent Addresses",
    "Clear Cache",
    "About"
};

static NICE_Block g_loading_blocks[] =
{
    { NICE_TITLE, "NICE64 Browser", NULL, 0, 0 },
    { NICE_END, NULL, NULL, 0, 0 }
};

static NICE_Block g_error_blocks[] =
{
    { NICE_TITLE, "NICE64 Browser", NULL, 0, 0 },
    { NICE_H1, "Unable To Load NICE Document", NULL, 0, 0 },
    { NICE_H2, g_error_stage, NULL, 0, 0 },
    { NICE_PARAGRAPH, g_error_resource, NULL, 0, 0 },
    { NICE_PARAGRAPH, g_error_bytes, NULL, 0, 0 },
    { NICE_PARAGRAPH, g_error_detail, NULL, 0, 0 },
    { NICE_HORIZONTAL_RULE, NULL, NULL, 0, 0 },
    { NICE_LINK, "Retry", "nice://retry", 0, 0 },
    { NICE_LINK, "Home", HOME_RESOURCE, 0, 0 },
    { NICE_LINK, "Back", "nice://back", 0, 0 },
    { NICE_END, NULL, NULL, 0, 0 }
};

static const NICE_Page g_loading_page =
{
    .title = "NICE64 Browser",
    .blocks = g_loading_blocks
};

static const NICE_Page g_error_page =
{
    .title = "NICE64 Browser Error",
    .blocks = g_error_blocks
};

static NICE_Block g_about_blocks[] =
{
    { NICE_TITLE, "NICE64 Browser", NULL, 0, 0 },
    { NICE_H1, "Controls", NULL, 0, 0 },
    { NICE_H2, "Page", NULL, 0, 0 },
    { NICE_PARAGRAPH, "D-Pad: select links. A: open. L/R: back/forward. L+R: reload.", NULL, 0, 0 },
    { NICE_PARAGRAPH, "C-Up/Down: page. C-Left: top. C-Right: next section. Z: keyboard. Start: menu.", NULL, 0, 0 },
    { NICE_H2, "Keyboard", NULL, 0, 0 },
    { NICE_PARAGRAPH, "D-Pad: select key. A: type. B: backspace or cancel. C: move cursor. Start: accept. Z: close.", NULL, 0, 0 },
    { NICE_END, NULL, NULL, 0, 0 }
};

static const NICE_Page g_about_page = { .title = "NICE64 Browser Controls", .blocks = g_about_blocks };

static uint32_t color_rgb(uint8_t r, uint8_t g, uint8_t b)
{
    return graphics_make_color(r, g, b, 255);
}

static void Browser_ClearImageRequest(void)
{
    memset(&g_image_request, 0, sizeof(g_image_request));
}

static const char *Browser_ImageRouteReason(const char *route)
{
    if (route == NULL) return "IMAGE_INVALID_ROUTE";
    if (strlen(route) != BROWSER_IMAGE_ROUTE_LENGTH) return "IMAGE_ROUTE_LENGTH_INVALID";
    if (strncmp(route, "browser://i/", 12) != 0) return "IMAGE_INVALID_ROUTE";
    for (uint8_t i = 12; i < BROWSER_IMAGE_ROUTE_LENGTH; i++)
    {
        if (!isxdigit((unsigned char)route[i])) return "IMAGE_INVALID_ROUTE";
    }
    return "IMAGE_BLOCK_ACCEPTED";
}

static bool Browser_FindFirstImageRequest(BrowserImageRequest *request)
{
    const NICE_LoadedDocument *loaded = NICE_CurrentLoadedDocument();
    const NICE_Page *page = NICE_CurrentDocument();

    if (request == NULL)
    {
        debugf("Browser: image scan rejected reason=IMAGE_REQUEST_POINTER_NULL\n");
        return false;
    }
    if (loaded == NULL || !loaded->loaded || page == NULL || page->blocks == NULL)
    {
        debugf("Browser: image scan rejected reason=IMAGE_LOADING_DOCUMENT\n");
        return false;
    }
    if (g_mode != BROWSER_MODE_PAGE || !g_network_document_ready || g_network_document_failed ||
        strcmp(g_current_resource, "nice://about") == 0)
    {
        debugf("Browser: image scan rejected reason=IMAGE_PAGE_INELIGIBLE page=%lu document=%p mode=%d ready=%d failed=%d resource=%s\n",
            (unsigned long)loaded->page_id, (void *)page, g_mode, g_network_document_ready,
            g_network_document_failed, g_current_resource);
        return false;
    }

    for (uint32_t index = 0; index < BROWSER_TRANSPORT_MAX_BLOCKS; index++)
    {
        const NICE_Block *block = &page->blocks[index];

        if (block->type == NICE_END)
        {
            break;
        }
        if (block->type != NICE_IMAGE_PLACEHOLDER)
        {
            continue;
        }

        const char *reason = Browser_ImageRouteReason(block->target);
        if (strcmp(reason, "IMAGE_BLOCK_ACCEPTED") == 0 && block->image_width <= 0) reason = "IMAGE_ZERO_WIDTH";
        if (strcmp(reason, "IMAGE_BLOCK_ACCEPTED") == 0 && block->image_height <= 0) reason = "IMAGE_ZERO_HEIGHT";
        if (strcmp(reason, "IMAGE_BLOCK_ACCEPTED") == 0 && block->image_width > IMAGE_MAX_WIDTH) reason = "IMAGE_WIDTH_TOO_LARGE";
        if (strcmp(reason, "IMAGE_BLOCK_ACCEPTED") == 0 && block->image_height > IMAGE_MAX_HEIGHT) reason = "IMAGE_HEIGHT_TOO_LARGE";
        debugf("Browser: image block=%lu page=%lu document=%p route=%s routeLength=%d size=%dx%d caption=%s decision=%s\n",
            (unsigned long)index, (unsigned long)loaded->page_id, (void *)page,
            block->target == NULL ? "(null)" : block->target, block->target == NULL ? 0 : (int)strlen(block->target),
            block->image_width, block->image_height,
            block->text == NULL || block->text[0] == '\0' ? "no" : "yes", reason);
        if (strcmp(reason, "IMAGE_BLOCK_ACCEPTED") != 0) continue;

        memset(request, 0, sizeof(*request));
        request->valid = true;
        request->page_id = loaded->page_id;
        request->document = page;
        request->expected_width = (uint16_t)block->image_width;
        request->expected_height = (uint16_t)block->image_height;
        memcpy(request->route, block->target, BROWSER_IMAGE_ROUTE_LENGTH + 1u);
        return true;
    }
    return false;
}

static bool Browser_CurrentImageRequestMatches(void)
{
    BrowserImageRequest current;
    const NICE_LoadedDocument *loaded = NICE_CurrentLoadedDocument();

    if (!g_image_request.valid || loaded == NULL || !loaded->loaded ||
        loaded->page_id != g_image_request.page_id || loaded->page != g_image_request.document ||
        !Browser_FindFirstImageRequest(&current))
    {
        return false;
    }

    return strcmp(current.route, g_image_request.route) == 0 &&
        current.expected_width == g_image_request.expected_width &&
        current.expected_height == g_image_request.expected_height;
}

static bool Browser_CurrentImagePageMatches(void)
{
    const NICE_LoadedDocument *loaded = NICE_CurrentLoadedDocument();

    return g_image_request.valid && loaded != NULL && loaded->loaded &&
        loaded->page_id == g_image_request.page_id &&
        loaded->page == g_image_request.document &&
        g_mode == BROWSER_MODE_PAGE;
}

static void Browser_StartFirstImageForCurrentDocument(void)
{
    BrowserImageRequest request;
    ImageTransportStatus status = ImageTransport_GetStatus();

    debugf("Browser: image scan started\n");
    if (!Browser_FindFirstImageRequest(&request))
    {
        debugf("Browser: no eligible image found\n");
        return;
    }

    debugf("Browser: image route %s page=%lu size=%ux%u\n", request.route,
        (unsigned long)request.page_id, request.expected_width, request.expected_height);
    if (g_image_request.valid && Browser_CurrentImageRequestMatches())
    {
        debugf("Browser: duplicate image request suppressed\n");
        return;
    }

    if (image_transport_is_active(status.state))
    {
        ImageTransport_Cancel();
        ImageTransport_Reset();
    }

    g_image_request = request;
    if (ImageTransport_Start(g_image_request.route, g_image_request.page_id,
        g_image_request.expected_width, g_image_request.expected_height))
    {
        debugf("Browser: automatic image request started\n");
    }
    else
    {
        Browser_ClearImageRequest();
        debugf("Browser: automatic image request rejected\n");
    }
}

static bool image_transport_is_active(ImageTransportState state)
{
    return state == IMAGE_TRANSPORT_OPENING ||
        state == IMAGE_TRANSPORT_READING ||
        state == IMAGE_TRANSPORT_VERIFYING ||
        state == IMAGE_TRANSPORT_VALIDATING_NIMG;
}

static const char *image_transport_state_name(ImageTransportState state)
{
    switch (state)
    {
    case IMAGE_TRANSPORT_OPENING: return "OPENING";
    case IMAGE_TRANSPORT_READING: return "READING";
    case IMAGE_TRANSPORT_VERIFYING: return "VERIFYING";
    case IMAGE_TRANSPORT_VALIDATING_NIMG: return "VALIDATING_NIMG";
    case IMAGE_TRANSPORT_COMPLETE: return "COMPLETE";
    case IMAGE_TRANSPORT_FAILED: return "FAILED";
    case IMAGE_TRANSPORT_CANCELLED: return "CANCELLED";
    default: return "IDLE";
    }
}

/* Document work owns the Network Pak stream until it returns to the page. */
static void Browser_PrepareForDocumentRequest(void)
{
    ImageTransportStatus image_status = ImageTransport_GetStatus();

    if (g_document_work_this_frame)
    {
        return;
    }

    g_document_work_this_frame = true;
    BrowserTransport_Cancel();
    if (image_transport_is_active(image_status.state))
    {
        debugf("Browser: document preempts image %s, discarding %lu bytes\n",
            image_transport_state_name(image_status.state),
            (unsigned long)image_status.bytes_received);
        ImageTransport_Cancel();
        image_status = ImageTransport_GetStatus();
        debugf("Browser: image close %s\n",
            image_status.close_attempted ?
                (image_status.close_succeeded ? "OK" : "FAILED") : "NOT_REQUIRED");
    }

    ImageTransport_Reset();
    Browser_ClearImageRequest();
    debugf("Browser: document stream arbitration complete\n");
}

static void Browser_UpdateImageTransport(void)
{
    ImageTransportStatus image_status = ImageTransport_GetStatus();

    if (g_document_work_this_frame)
    {
        if (image_transport_is_active(image_status.state))
        {
            debugf("Browser: image update skipped; document work has priority\n");
        }
        return;
    }

    if (g_mode == BROWSER_MODE_PAGE && image_transport_is_active(image_status.state))
    {
        if (!Browser_CurrentImagePageMatches())
        {
            debugf("Browser: page changed before image completion\n");
            ImageTransport_InvalidatePageRoute();
            Browser_ClearImageRequest();
            return;
        }
        ImageTransport_Update();
        image_status = ImageTransport_GetStatus();
        if ((image_status.state == IMAGE_TRANSPORT_COMPLETE || image_status.state == IMAGE_TRANSPORT_FAILED) &&
            g_image_request.valid && !g_image_request.terminal_logged)
        {
            g_image_request.terminal_logged = true;
            if (image_status.state == IMAGE_TRANSPORT_COMPLETE && Browser_CurrentImageRequestMatches() &&
                image_status.nimg.width == g_image_request.expected_width &&
                image_status.nimg.height == g_image_request.expected_height)
            {
                debugf("Browser: image completion accepted\n");
            }
            else if (image_status.state == IMAGE_TRANSPORT_COMPLETE)
            {
                debugf("Browser: image completion rejected; page or dimensions changed\n");
                ImageTransport_InvalidatePageRoute();
                Browser_ClearImageRequest();
            }
            else
            {
                debugf("Browser: image transfer failed: %s\n", image_status.error == NULL ? "UNKNOWN" : image_status.error);
            }
        }
    }
}

static void draw_text(surface_t *display, int x, int y, uint32_t color, const char *text)
{
    graphics_set_color(color, COLOR_TRANSPARENT);
    graphics_draw_text(display, x, y, text);
}

static void set_input_text(const char *text)
{
    snprintf(g_input_buffer, sizeof(g_input_buffer), "%s", text == NULL ? "" : text);
    g_input_length = strlen(g_input_buffer);
    g_cursor_position = g_input_length;
}

static bool contains_ci(const char *text, const char *needle)
{
    size_t text_len = strlen(text);
    size_t needle_len = strlen(needle);
    if (needle_len == 0 || text_len < needle_len)
    {
        return false;
    }

    for (size_t i = 0; i <= text_len - needle_len; i++)
    {
        size_t j = 0;
        while (j < needle_len && tolower((unsigned char)text[i + j]) == tolower((unsigned char)needle[j]))
        {
            j++;
        }
        if (j == needle_len)
        {
            return true;
        }
    }
    return false;
}

static void normalize_resource(const char *input, char *out, size_t out_size)
{
    if (input == NULL || input[0] == '\0')
    {
        snprintf(out, out_size, "%s", HOME_RESOURCE);
    }
    else if (strncmp(input, "nice://", 7) == 0 || strncmp(input, "browser://", 10) == 0)
    {
        snprintf(out, out_size, "%s", input);
    }
    else if (contains_ci(input, "cnn"))
    {
        snprintf(out, out_size, "browser://live?u=cnn");
    }
    else if (contains_ci(input, "wiki"))
    {
        snprintf(out, out_size, "browser://live?u=wiki");
    }
    else if (contains_ci(input, "nasa"))
    {
        snprintf(out, out_size, "browser://live?u=nasa");
    }
    else if (contains_ci(input, "radio"))
    {
        snprintf(out, out_size, "browser://live?u=rs");
    }
    else if (contains_ci(input, "tom"))
    {
        snprintf(out, out_size, "browser://live?u=tom");
    }
    else
    {
        snprintf(out, out_size, "browser://live");
    }
}

static char *cache_text_base(int slot)
{
    return (char *)&g_future_page_cache[(size_t)slot * CACHE_TEXT_BYTES];
}

static void history_update_current(void)
{
    if (g_history_index < 0 || g_history_index >= g_history_count)
    {
        return;
    }

    g_history[g_history_index].scroll = NICE_GetScroll();
    g_history[g_history_index].focus = NICE_GetFocus();
}

static void history_push(const char *resource)
{
    const NICE_LoadedDocument *loaded = NICE_CurrentLoadedDocument();
    const NICE_Page *page = NICE_CurrentDocument();
    char display_title[BROWSER_INPUT_MAX + 1u];
    const char *page_title;
    if (page == NULL || resource == NULL)
    {
        return;
    }

    history_update_current();

    if (g_history_index >= 0 &&
        g_history_index < g_history_count &&
        strcmp(g_history[g_history_index].resource, resource) == 0)
    {
        return;
    }

    if (g_history_index < g_history_count - 1)
    {
        g_history_count = g_history_index + 1;
    }

    if (g_history_count == HISTORY_LIMIT)
    {
        memmove(&g_history[0], &g_history[1], sizeof(g_history[0]) * (HISTORY_LIMIT - 1u));
        g_history_count--;
        g_history_index--;
    }

    g_history_index++;
    g_history_count = g_history_index + 1;
    g_history[g_history_index].valid = true;
    snprintf(g_history[g_history_index].resource, sizeof(g_history[g_history_index].resource), "%s", resource);
    page_title = page->title == NULL ? "NICE Page" : page->title;
    snprintf(display_title, sizeof(display_title), "%s", page_title);
    if (strncmp(resource, "browser://search?q=", 19u) == 0)
    {
        const char *query = resource + 19u;
        size_t output = 0u;
        snprintf(display_title, sizeof(display_title), "Search: ");
        output = strlen(display_title);
        while (*query != '\0' && output < sizeof(display_title) - 1u)
        {
            if (*query == '%' && isxdigit((unsigned char)query[1]) && isxdigit((unsigned char)query[2]))
            {
                int high = isdigit((unsigned char)query[1]) ? query[1] - '0' : (tolower((unsigned char)query[1]) - 'a' + 10);
                int low = isdigit((unsigned char)query[2]) ? query[2] - '0' : (tolower((unsigned char)query[2]) - 'a' + 10);
                display_title[output++] = (char)((high << 4) | low);
                query += 3;
            }
            else
            {
                display_title[output++] = *query == '+' ? ' ' : *query;
                query++;
            }
        }
        display_title[output] = '\0';
    }
    snprintf(g_history[g_history_index].title, sizeof(g_history[g_history_index].title), "%s", display_title);
    snprintf(g_history[g_history_index].canonical_url, sizeof(g_history[g_history_index].canonical_url), "%s", g_current_canonical_url);
    g_history[g_history_index].page_id = loaded == NULL ? 0 : loaded->page_id;
    g_history[g_history_index].scroll = 0;
    g_history[g_history_index].focus = 0;

    if (g_current_canonical_url[0] != '\0')
    {
        BrowserProfile_RecordVisit(display_title, g_current_canonical_url);
        debugf("Browser: canonical URL stored url=%s\n", g_current_canonical_url);
        g_profile_pending_url[0] = '\0';
        BrowserProfile_SaveNow();
    }
}

static int cache_find(const char *resource)
{
    for (int i = 0; i < CACHE_LIMIT; i++)
    {
        if (g_cache[i].valid && strcmp(g_cache[i].resource, resource) == 0)
        {
            return i;
        }
    }
    return -1;
}

static int cache_pick_slot(void)
{
    int oldest = 0;
    for (int i = 0; i < CACHE_LIMIT; i++)
    {
        if (!g_cache[i].valid)
        {
            return i;
        }
        if (g_cache[i].last_used < g_cache[oldest].last_used)
        {
            oldest = i;
        }
    }
    return oldest;
}

static uint16_t browser_clear_page_cache(void)
{
    const NICE_Page *active_page = NICE_CurrentDocument();
    uint16_t count = 0u;

    for (int i = 0; i < CACHE_LIMIT; i++)
    {
        if (g_cache[i].valid) count++;
        g_cache[i].valid = false;
        g_cache[i].last_used = 0u;
        g_cache[i].resource[0] = '\0';
        g_cache[i].canonical_url[0] = '\0';
        g_cache[i].page_id = 0u;

        /* The current renderer can still reference this slot. Keep its copied
         * strings alive until the next document replaces the active page. */
        if (active_page != &g_cache[i].page)
        {
            memset(&g_cache[i], 0, sizeof(g_cache[i]));
            memset(cache_text_base(i), 0, CACHE_TEXT_BYTES);
        }
    }

    g_cache_clock = 0u;
    return count;
}

static void browser_clear_session_history(void)
{
    memset(g_history, 0, sizeof(g_history));
    g_history_count = 0;
    g_history_index = -1;
}

static const char *browser_clear_action_name(BrowserClearAction action)
{
    switch (action)
    {
    case BROWSER_CLEAR_HISTORY: return "HISTORY";
    case BROWSER_CLEAR_RECENTS: return "RECENTS";
    case BROWSER_CLEAR_CACHE: return "CACHE";
    default: return "NONE";
    }
}

static bool cache_copy_string(int slot, char **cursor, size_t *remaining, const char *source, const char **target)
{
    size_t length;
    if (source == NULL)
    {
        *target = NULL;
        return true;
    }

    length = strlen(source);
    if (length + 1u > *remaining)
    {
        return false;
    }

    memcpy(*cursor, source, length + 1u);
    *target = *cursor;
    *cursor += length + 1u;
    *remaining -= length + 1u;
    return true;
}

static void cache_store_current(const char *resource)
{
    const NICE_LoadedDocument *loaded = NICE_CurrentLoadedDocument();
    const NICE_Page *page = NICE_CurrentDocument();
    int slot;
    char *cursor;
    size_t remaining;

    if (loaded == NULL || !loaded->loaded || page == NULL || page->blocks == NULL || resource == NULL)
    {
        return;
    }

    slot = cache_find(resource);
    if (slot < 0)
    {
        slot = cache_pick_slot();
    }

    memset(&g_cache[slot], 0, sizeof(g_cache[slot]));
    memset(cache_text_base(slot), 0, CACHE_TEXT_BYTES);
    cursor = cache_text_base(slot);
    remaining = CACHE_TEXT_BYTES;

    snprintf(g_cache[slot].resource, sizeof(g_cache[slot].resource), "%s", resource);
    snprintf(g_cache[slot].canonical_url, sizeof(g_cache[slot].canonical_url), "%s", g_current_canonical_url);
    g_cache[slot].page_id = loaded->page_id;
    g_cache[slot].last_used = ++g_cache_clock;
    g_cache[slot].page.blocks = g_cache[slot].blocks;

    if (!cache_copy_string(slot, &cursor, &remaining, page->title, &g_cache[slot].page.title))
    {
        return;
    }

    for (uint32_t i = 0; i < BROWSER_TRANSPORT_MAX_BLOCKS; i++)
    {
        g_cache[slot].blocks[i] = page->blocks[i];
        if (!cache_copy_string(slot, &cursor, &remaining, page->blocks[i].text, &g_cache[slot].blocks[i].text) ||
            !cache_copy_string(slot, &cursor, &remaining, page->blocks[i].target, &g_cache[slot].blocks[i].target))
        {
            memset(&g_cache[slot], 0, sizeof(g_cache[slot]));
            return;
        }
        if (page->blocks[i].type == NICE_END)
        {
            g_cache[slot].valid = true;
            return;
        }
    }

    g_cache[slot].blocks[BROWSER_TRANSPORT_MAX_BLOCKS].type = NICE_END;
    g_cache[slot].valid = true;
}

static bool cache_load(const char *resource)
{
    int slot = cache_find(resource);
    if (slot < 0)
    {
        return false;
    }

    g_cache[slot].last_used = ++g_cache_clock;
    NICE_LoadDocument(g_cache[slot].page_id, &g_cache[slot].page);
    NICE_RenderPage(NICE_CurrentDocument());
    snprintf(g_current_resource, sizeof(g_current_resource), "%s", resource);
    snprintf(g_current_canonical_url, sizeof(g_current_canonical_url), "%s", g_cache[slot].canonical_url);
    snprintf(g_status_line, sizeof(g_status_line), "Cached NICE");
    g_network_document_ready = true;
    g_network_document_failed = false;
    g_mode = BROWSER_MODE_PAGE;
    Browser_StartFirstImageForCurrentDocument();
    return true;
}

static void update_error_document(BrowserTransportStatus status, const char *resource)
{
    snprintf(g_error_stage, sizeof(g_error_stage), "Stage: %s", BrowserTransport_ResultName(status.result));
    snprintf(g_error_resource, sizeof(g_error_resource), "Resource: %.80s", resource == NULL ? g_current_resource : resource);
    snprintf(g_error_bytes, sizeof(g_error_bytes), "Bytes: %lu / %lu",
        (unsigned long)status.bytes_received,
        (unsigned long)status.expected_size);
    snprintf(g_error_detail, sizeof(g_error_detail), "Detail: %.80s", status.message);
}

static void load_error_page(BrowserTransportStatus status, const char *resource)
{
    g_network_document_ready = false;
    g_network_document_failed = true;
    update_error_document(status, resource);
    NICE_LoadDocument(0, &g_error_page);
    NICE_RenderPage(NICE_CurrentDocument());
    snprintf(g_status_line, sizeof(g_status_line), "%.95s", BrowserTransport_ResultName(status.result));
    g_mode = BROWSER_MODE_ERROR;
    BrowserAudio_PlayError();
}

static bool browser_is_poll_route(const char *resource)
{
    return resource != NULL && strncmp(resource, "browser://p/", 12) == 0 && resource[12] != '\0';
}

static void browser_clear_pending_poll(const char *reason)
{
    if (g_poll_active)
    {
        debugf("Browser: pending poll cleared reason=%s route=%s attempts=%lu\n",
            reason, g_poll_route, (unsigned long)g_poll_attempts);
    }

    g_poll_active = false;
    g_poll_add_history = false;
    g_poll_frames = 0;
    g_poll_attempts = 0;
    g_poll_route[0] = '\0';
    g_poll_origin[0] = '\0';
}

static void browser_begin_pending_poll(const char *origin, bool add_history, const char *route)
{
    if (!browser_is_poll_route(route))
    {
        debugf("Browser: rejected invalid poll route=%s\n", route == NULL ? "(null)" : route);
        return;
    }

    if (!g_poll_active)
    {
        snprintf(g_poll_origin, sizeof(g_poll_origin), "%s", origin);
        g_poll_add_history = add_history;
        g_poll_attempts = 0;
    }

    snprintf(g_poll_route, sizeof(g_poll_route), "%s", route);
    g_poll_active = true;
    g_poll_frames = 0;
    debugf("Browser: pending poll route=%s origin=%s attempts=%lu\n",
        g_poll_route, g_poll_origin, (unsigned long)g_poll_attempts);
}

static bool browser_complete_document_load(void)
{
    char poll_route[BROWSER_RESOURCE_MAX];
    const char *resource = g_current_resource;
    bool add_history = g_loading_add_history;
    BrowserTransportStatus status = BrowserTransport_GetStatus();

    if (status.canonical_url[0] != '\0')
    {
        snprintf(g_current_canonical_url, sizeof(g_current_canonical_url), "%s", status.canonical_url);
        debugf("Browser: canonical URL detected url=%s\n", g_current_canonical_url);
    }
    else
    {
        debugf("Browser: canonical URL missing route=%s\n", resource);
    }

    if (BrowserTransport_GetPendingPollRoute(poll_route, sizeof(poll_route)))
    {
        browser_begin_pending_poll(g_poll_active ? g_poll_origin : resource,
            g_poll_active ? g_poll_add_history : add_history, poll_route);
        if (!g_poll_active)
        {
            status.result = BROWSER_ERROR_RESOURCE_REQUEST;
            snprintf(status.message, sizeof(status.message), "INVALID_POLL_ROUTE");
            load_error_page(status, resource);
            return false;
        }
        snprintf(g_status_line, sizeof(g_status_line), "Loading NICE");
        return true;
    }

    g_network_document_ready = true;
    g_network_document_failed = false;
    NICE_RenderPage(NICE_CurrentDocument());
    if (g_poll_active)
    {
        resource = g_poll_origin;
        add_history = g_poll_add_history;
        debugf("Browser: poll completed route=%s attempts=%lu\n",
            g_poll_route, (unsigned long)g_poll_attempts);
        browser_clear_pending_poll("document ready");
        snprintf(g_current_resource, sizeof(g_current_resource), "%s", resource);
    }
    cache_store_current(resource);
    if (add_history)
    {
        history_push(resource);
    }
    g_profile_pending_url[0] = '\0';
    snprintf(g_status_line, sizeof(g_status_line), "Loaded NICE %lu bytes", (unsigned long)status.bytes_received);
    g_mode = BROWSER_MODE_PAGE;
    Browser_StartFirstImageForCurrentDocument();
    return true;
}

static bool load_local_home(bool add_history)
{
    uint16_t favorite_index;
    uint16_t pinned_count = 0u;
    uint16_t block_count = 0u;

    Browser_PrepareForDocumentRequest();
    memset(g_home_blocks, 0, sizeof(g_home_blocks));
    g_home_blocks[block_count++] = (NICE_Block){ NICE_H1, "Welcome.", NULL, 0, 0 };
    g_home_blocks[block_count++] = (NICE_Block){ NICE_LINK, "Enter URL", "nice://enter", 0, 0 };
    g_home_blocks[block_count++] = (NICE_Block){ NICE_LINK, "Search Web", "nice://search", 0, 0 };

    for (favorite_index = 0u;
         favorite_index < BROWSER_PROFILE_FAVORITE_LIMIT && pinned_count < BROWSER_HOME_PIN_LIMIT;
         favorite_index++)
    {
        BrowserProfileFavoriteView favorite;
        if (BrowserProfile_GetFavorite(favorite_index, &favorite) &&
            (favorite.flags & BROWSER_PROFILE_FAVORITE_FLAG_HOME_PINNED) != 0u)
        {
            snprintf(g_home_titles[pinned_count], sizeof(g_home_titles[pinned_count]), "%s",
                favorite.title[0] == '\0' ? "Untitled Favorite" : favorite.title);
            snprintf(g_home_urls[pinned_count], sizeof(g_home_urls[pinned_count]), "%s", favorite.url);
            g_home_blocks[block_count++] = (NICE_Block){ NICE_LINK, g_home_titles[pinned_count], g_home_urls[pinned_count], 0, 0 };
            pinned_count++;
        }
    }
    g_home_blocks[block_count++] = (NICE_Block){ NICE_LINK, "Favorites", "nice://favorites", 0, 0 };
    g_home_blocks[block_count++] = (NICE_Block){ NICE_LINK, "History", "nice://history", 0, 0 };
    g_home_blocks[block_count++] = (NICE_Block){ NICE_LINK, "Options", "nice://options", 0, 0 };
    g_home_blocks[block_count] = (NICE_Block){ NICE_END, NULL, NULL, 0, 0 };

    g_current_canonical_url[0] = '\0';
    g_network_document_ready = true;
    g_network_document_failed = false;
    NICE_LoadDocument(0x00001001u, &g_home_page);
    NICE_RenderPage(NICE_CurrentDocument());
    snprintf(g_current_resource, sizeof(g_current_resource), "%s", HOME_RESOURCE);
    snprintf(g_status_line, sizeof(g_status_line), "NICE Home");
    g_mode = BROWSER_MODE_PAGE;
    if (add_history) history_push(HOME_RESOURCE);
    return true;
}

static bool load_browser_document(const char *resource, bool add_history, bool prefer_cache)
{
    return load_browser_document_internal(resource, add_history, prefer_cache, false);
}

static bool load_browser_document_internal(const char *resource, bool add_history, bool prefer_cache, bool is_poll_request)
{
    if (resource == NULL || resource[0] == '\0')
    {
        resource = HOME_RESOURCE;
    }

    if (!is_poll_request && g_poll_active)
    {
        browser_clear_pending_poll("new navigation");
    }

    if (!is_poll_request && strcmp(resource, HOME_RESOURCE) == 0)
    {
        return load_local_home(add_history);
    }

    if (is_poll_request && !browser_is_poll_route(resource))
    {
        BrowserTransportStatus status = BrowserTransport_GetStatus();
        status.result = BROWSER_ERROR_RESOURCE_REQUEST;
        snprintf(status.message, sizeof(status.message), "INVALID_POLL_ROUTE");
        load_error_page(status, g_poll_origin);
        browser_clear_pending_poll("invalid route");
        return false;
    }

    Browser_PrepareForDocumentRequest();

    if (prefer_cache && cache_load(resource))
    {
        if (add_history)
        {
            history_push(resource);
        }
        return true;
    }

    g_current_canonical_url[0] = '\0';
    g_mode = BROWSER_MODE_LOADING;
    g_network_document_failed = false;
    NICE_LoadDocument(0, &g_loading_page);
    NICE_RenderPage(NICE_CurrentDocument());
    snprintf(g_current_resource, sizeof(g_current_resource), "%s", resource);
    snprintf(g_status_line, sizeof(g_status_line), "Loading NICE");
    g_loading_add_history = add_history;
    g_loading_animation_frames = 0;

    if (is_poll_request)
    {
        debugf("Browser: poll request attempt=%lu route=%s\n",
            (unsigned long)g_poll_attempts, resource);
    }

    if (!BrowserTransport_StartResource(resource))
    {
        load_error_page(BrowserTransport_GetStatus(), resource);
        return false;
    }
    return true;
}

static void reload_current(void)
{
    load_browser_document(g_current_resource, false, false);
    if (g_history_index >= 0 && g_history_index < g_history_count)
    {
        snprintf(g_history[g_history_index].resource, sizeof(g_history[g_history_index].resource), "%s", g_current_resource);
    }
}

static void restore_history(int index)
{
    HistoryEntry *entry;
    if (index < 0 || index >= g_history_count)
    {
        return;
    }

    history_update_current();
    g_history_index = index;
    entry = &g_history[index];
    snprintf(g_current_canonical_url, sizeof(g_current_canonical_url), "%s", entry->canonical_url);
    Browser_PrepareForDocumentRequest();
    if (cache_load(entry->resource))
    {
        NICE_SetScroll(entry->scroll);
        NICE_SetFocus(entry->focus);
        snprintf(g_status_line, sizeof(g_status_line), "History %.55s", entry->resource);
    }
    else
    {
        load_browser_document(entry->resource, false, false);
        NICE_SetScroll(entry->scroll);
        NICE_SetFocus(entry->focus);
    }
}

static void navigate_to_input(void)
{
    char resource[BROWSER_RESOURCE_MAX];

    if (strncmp(g_input_buffer, "http://", 7) == 0 || strncmp(g_input_buffer, "https://", 8) == 0)
    {
        snprintf(g_profile_pending_url, sizeof(g_profile_pending_url), "%s", g_input_buffer);
        g_current_canonical_url[0] = '\0';
        BrowserProfile_RecordRecentAddress(g_profile_pending_url);
        Browser_PrepareForDocumentRequest();
        if (!BrowserTransport_RegisterUrl(g_input_buffer, resource, sizeof(resource)))
        {
            g_profile_pending_url[0] = '\0';
            load_error_page(BrowserTransport_GetStatus(), g_input_buffer);
            return;
        }
    }
    else
    {
        normalize_resource(g_input_buffer, resource, sizeof(resource));
    }
    set_input_text(resource);
    load_browser_document(resource, true, true);
}

static void navigate_to_search(void)
{
    const unsigned char *input = (const unsigned char *)g_input_buffer;
    size_t start = 0u;
    size_t end = strlen(g_input_buffer);
    char query[BROWSER_INPUT_MAX + 1u];
    char resource[BROWSER_RESOURCE_MAX];

    while (start < end && isspace(input[start])) start++;
    while (end > start && isspace(input[end - 1u])) end--;
    if (start == end)
    {
        snprintf(g_status_line, sizeof(g_status_line), "Search query required");
        return;
    }

    snprintf(query, sizeof(query), "%.*s", (int)(end - start), &g_input_buffer[start]);
    Browser_PrepareForDocumentRequest();
    if (!BrowserTransport_RegisterSearchQuery(query, resource, sizeof(resource)))
    {
        load_error_page(BrowserTransport_GetStatus(), query);
        return;
    }
    set_input_text(query);
    load_browser_document(resource, true, true);
}

static void navigate_to_target(const char *target)
{
    char resource[BROWSER_RESOURCE_MAX];
    if (target == NULL)
    {
        return;
    }
    g_current_canonical_url[0] = '\0';
    if (strcmp(target, "nice://enter") == 0)
    {
        set_input_text("");
        keyboard_open_with_purpose(KEYBOARD_MODE_URL);
        return;
    }
    if (strcmp(target, "nice://search") == 0)
    {
        set_input_text("");
        keyboard_open_with_purpose(KEYBOARD_MODE_SEARCH);
        return;
    }
    if (strcmp(target, "nice://retry") == 0)
    {
        reload_current();
        return;
    }
    if (strcmp(target, "nice://back") == 0)
    {
        restore_history(g_history_index - 1);
        return;
    }
    if (strcmp(target, "nice://bookmarks") == 0)
    {
        snprintf(g_status_line, sizeof(g_status_line), "Bookmarks placeholder");
        return;
    }
    if (strcmp(target, "nice://history") == 0)
    {
        profile_open_view(BROWSER_PROFILE_VIEW_HISTORY);
        return;
    }
    if (strcmp(target, "nice://favorites") == 0)
    {
        profile_open_view(BROWSER_PROFILE_VIEW_FAVORITES);
        return;
    }
    if (strcmp(target, "nice://options") == 0)
    {
        g_previous_mode = BROWSER_MODE_PAGE;
        g_options_index = 0u;
        g_mode = BROWSER_MODE_OPTIONS;
        return;
    }

    if (strncmp(target, "http://", 7) == 0 || strncmp(target, "https://", 8) == 0)
    {
        set_input_text(target);
        navigate_to_input();
        return;
    }

    normalize_resource(target, resource, sizeof(resource));
    set_input_text(resource);
    load_browser_document(resource, true, true);
}

static void draw_menu_overlay(surface_t *display)
{
    graphics_draw_box(display, 34, 28, 252, 184, color_rgb(4, 7, 18));
    graphics_draw_box(display, 36, 30, 248, 180, color_rgb(18, 28, 62));
    graphics_draw_box(display, 38, 30, 244, 20, color_rgb(50, 72, 142));
    draw_text(display, 86, 36, color_rgb(255, 255, 255), "BROWSER MENU");

    for (uint8_t i = 0; i < MENU_ITEMS; i++)
    {
        int y = 54 + (int)i * 12;
        bool selected = i == g_menu_index;
        if (selected)
        {
            graphics_draw_box(display, 54, y - 3, 206, 14, color_rgb(60, 76, 152));
        }
        draw_text(display, 64, y, selected ? color_rgb(255, 246, 118) : color_rgb(226, 236, 246), selected ? ">" : " ");
        draw_text(display, 78, y, selected ? color_rgb(255, 246, 118) : color_rgb(226, 236, 246), g_menu_labels[i]);
    }

    draw_text(display, 58, 202, color_rgb(140, 230, 255), "A Select  B/Start Close");
}

static void draw_options_overlay(surface_t *display)
{
    graphics_draw_box(display, 42, 48, 236, 132, color_rgb(4, 7, 18));
    graphics_draw_box(display, 44, 50, 232, 128, color_rgb(18, 28, 62));
    graphics_draw_box(display, 46, 52, 228, 18, color_rgb(50, 72, 142));
    draw_text(display, 112, 58, color_rgb(255, 255, 255), "OPTIONS");

    for (uint8_t i = 0; i < OPTIONS_ITEMS; i++)
    {
        int y = 80 + (int)i * 17;
        bool selected = i == g_options_index;
        if (selected) graphics_draw_box(display, 58, y - 3, 204, 14, color_rgb(60, 76, 152));
        draw_text(display, 66, y, selected ? color_rgb(255, 246, 118) : color_rgb(226, 236, 246), selected ? ">" : " ");
        draw_text(display, 80, y, selected ? color_rgb(255, 246, 118) : color_rgb(226, 236, 246), g_options_labels[i]);
    }
    draw_text(display, 58, 194, color_rgb(140, 230, 255), "A Select  B/Start Back");
}

static const char *clear_confirmation_title(BrowserClearAction action)
{
    switch (action)
    {
    case BROWSER_CLEAR_HISTORY: return "CLEAR HISTORY?";
    case BROWSER_CLEAR_RECENTS: return "CLEAR RECENT ADDRESSES?";
    case BROWSER_CLEAR_CACHE: return "CLEAR CACHE?";
    default: return "CLEAR?";
    }
}

static const char *clear_confirmation_detail(BrowserClearAction action)
{
    switch (action)
    {
    case BROWSER_CLEAR_HISTORY: return "Remove saved browsing history.";
    case BROWSER_CLEAR_RECENTS: return "Remove manually entered URLs.";
    case BROWSER_CLEAR_CACHE: return "Remove cached pages for this session.";
    default: return "";
    }
}

static void draw_clear_confirmation_overlay(surface_t *display)
{
    bool clear_selected = g_confirm_index != 0u;
    graphics_draw_box(display, 38, 72, 244, 94, color_rgb(4, 7, 18));
    graphics_draw_box(display, 40, 74, 240, 90, color_rgb(18, 28, 62));
    graphics_draw_box(display, 42, 76, 236, 18, color_rgb(50, 72, 142));
    draw_text(display, 68, 82, color_rgb(255, 255, 255), clear_confirmation_title(g_pending_clear_action));
    draw_text(display, 54, 108, color_rgb(226, 236, 246), clear_confirmation_detail(g_pending_clear_action));
    if (!clear_selected) graphics_draw_box(display, 70, 126, 82, 14, color_rgb(60, 76, 152));
    draw_text(display, 78, 129, !clear_selected ? color_rgb(255, 246, 118) : color_rgb(226, 236, 246), "Cancel");
    if (clear_selected) graphics_draw_box(display, 168, 126, 54, 14, color_rgb(60, 76, 152));
    draw_text(display, 176, 129, clear_selected ? color_rgb(255, 246, 118) : color_rgb(226, 236, 246), "Clear");
    draw_text(display, 58, 150, color_rgb(140, 230, 255), "A Select  B/Start Cancel");
}

static const char *profile_view_title(void)
{
    switch (g_profile_view)
    {
    case BROWSER_PROFILE_VIEW_FAVORITES: return "FAVORITES";
    case BROWSER_PROFILE_VIEW_HISTORY: return "PERSISTENT HISTORY";
    default: return "RECENT ADDRESSES";
    }
}

static void profile_rebuild_indices(void)
{
    uint16_t i;
    g_profile_list_count = 0u;
    if (g_profile_view == BROWSER_PROFILE_VIEW_FAVORITES)
    {
        for (i = 0u; i < BROWSER_PROFILE_FAVORITE_LIMIT; i++)
        {
            BrowserProfileFavoriteView entry;
            if (BrowserProfile_GetFavorite(i, &entry)) g_profile_indices[g_profile_list_count++] = i;
        }
    }
    else if (g_profile_view == BROWSER_PROFILE_VIEW_HISTORY)
    {
        for (i = 0u; i < BROWSER_PROFILE_HISTORY_LIMIT; i++)
        {
            BrowserProfileHistoryView entry;
            if (BrowserProfile_GetHistory(i, &entry)) g_profile_indices[g_profile_list_count++] = i;
        }
    }
    else
    {
        for (i = 0u; i < BROWSER_PROFILE_RECENT_LIMIT; i++)
        {
            if (BrowserProfile_GetRecentAddress(i)[0] != '\0') g_profile_indices[g_profile_list_count++] = i;
        }
    }
    if (g_profile_list_count == 0u) g_profile_list_index = 0u;
    else if (g_profile_list_index >= g_profile_list_count) g_profile_list_index = (uint16_t)(g_profile_list_count - 1u);
}

static void profile_open_url(const char *url)
{
    if (url == NULL || (strncmp(url, "http://", 7) != 0 && strncmp(url, "https://", 8) != 0))
    {
        snprintf(g_status_line, sizeof(g_status_line), "No canonical URL");
        return;
    }
    set_input_text(url);
    g_mode = BROWSER_MODE_PAGE;
    navigate_to_input();
}

static void profile_open_view(BrowserProfileView view)
{
    g_profile_view = view;
    g_profile_list_index = 0u;
    profile_rebuild_indices();
    /* Start is an explicit overlay: B from a profile list returns to the page. */
    g_previous_mode = BROWSER_MODE_PAGE;
    g_mode = BROWSER_MODE_PROFILE_LIST;
}

static void profile_open_selected(void)
{
    uint16_t source_index;
    if (g_profile_list_count == 0u) return;
    source_index = g_profile_indices[g_profile_list_index];
    if (g_profile_view == BROWSER_PROFILE_VIEW_FAVORITES)
    {
        BrowserProfileFavoriteView entry;
        if (BrowserProfile_GetFavorite(source_index, &entry)) profile_open_url(entry.url);
    }
    else if (g_profile_view == BROWSER_PROFILE_VIEW_HISTORY)
    {
        BrowserProfileHistoryView entry;
        if (BrowserProfile_GetHistory(source_index, &entry)) profile_open_url(entry.url);
    }
    else
    {
        profile_open_url(BrowserProfile_GetRecentAddress(source_index));
    }
}

static void draw_profile_list_overlay(surface_t *display)
{
    uint16_t start;
    uint16_t visible_rows = g_profile_view == BROWSER_PROFILE_VIEW_FAVORITES ? PROFILE_FAVORITE_VISIBLE_ROWS : PROFILE_VISIBLE_ROWS;
    graphics_draw_box(display, 24, 32, 272, 178, color_rgb(4, 7, 18));
    graphics_draw_box(display, 26, 34, 268, 174, color_rgb(18, 28, 62));
    graphics_draw_box(display, 28, 34, 264, 20, color_rgb(50, 72, 142));
    draw_text(display, 62, 40, color_rgb(255, 255, 255), profile_view_title());
    if (g_profile_list_count == 0u)
    {
        const char *empty = g_profile_view == BROWSER_PROFILE_VIEW_FAVORITES ? "NO FAVORITES SAVED" :
            g_profile_view == BROWSER_PROFILE_VIEW_HISTORY ? "NO HISTORY" : "NO RECENT ADDRESSES";
        draw_text(display, 64, 98, color_rgb(140, 230, 255), empty);
    }
    start = g_profile_list_index >= visible_rows ? (uint16_t)(g_profile_list_index - visible_rows + 1u) : 0u;
    for (uint16_t row = 0u; row < visible_rows && start + row < g_profile_list_count; row++)
    {
        uint16_t source_index = g_profile_indices[start + row];
        bool selected = start + row == g_profile_list_index;
        char label[32];
        const char *title = "";
        if (g_profile_view == BROWSER_PROFILE_VIEW_FAVORITES)
        {
            BrowserProfileFavoriteView entry;
            if (BrowserProfile_GetFavorite(source_index, &entry)) title = entry.title;
        }
        else if (g_profile_view == BROWSER_PROFILE_VIEW_HISTORY)
        {
            BrowserProfileHistoryView entry;
            if (BrowserProfile_GetHistory(source_index, &entry)) title = entry.title;
        }
        else title = BrowserProfile_GetRecentAddress(source_index);
        if (g_profile_view == BROWSER_PROFILE_VIEW_FAVORITES)
        {
            const char *value = title[0] == '\0' ? "Untitled" : title;
            size_t length = strlen(value);
            size_t split = length > 28u ? 28u : length;
            while (split > 0u && split < length && value[split] != ' ') split--;
            if (split == 0u) split = length > 28u ? 28u : length;
            if (selected) graphics_draw_box(display, 36, 60 + (int)row * 25, 248, 23, color_rgb(50, 72, 142));
            BrowserProfileFavoriteView favorite;
            bool pinned = BrowserProfile_GetFavorite(source_index, &favorite) &&
                (favorite.flags & BROWSER_PROFILE_FAVORITE_FLAG_HOME_PINNED) != 0u;
            snprintf(label, sizeof(label), "%c %.*s", selected ? '>' : (pinned ? '+' : ' '), (int)split, value);
            draw_text(display, 42, 63 + (int)row * 25, selected ? color_rgb(255, 246, 118) : color_rgb(220, 232, 246), label);
            if (split < length)
            {
                while (value[split] == ' ') split++;
                snprintf(label, sizeof(label), "  %.28s", value + split);
                draw_text(display, 42, 74 + (int)row * 25, selected ? color_rgb(255, 246, 118) : color_rgb(220, 232, 246), label);
            }
        }
        else
        {
            snprintf(label, sizeof(label), "%c %.28s", selected ? '>' : ' ', title[0] == '\0' ? "Untitled" : title);
            if (selected) graphics_draw_box(display, 36, 62 + (int)row * 16, 248, 14, color_rgb(50, 72, 142));
            draw_text(display, 42, 65 + (int)row * 16, selected ? color_rgb(255, 246, 118) : color_rgb(220, 232, 246), label);
        }
    }
    draw_text(display, 42, 192, color_rgb(140, 230, 255), g_profile_view == BROWSER_PROFILE_VIEW_FAVORITES ? "A Open  Z Home  C Remove  B Back" : "A Open  B Back");
}

static void draw_remove_favorite_overlay(surface_t *display)
{
    graphics_draw_box(display, 42, 78, 236, 78, color_rgb(4, 7, 18));
    graphics_draw_box(display, 44, 80, 232, 74, color_rgb(18, 28, 62));
    graphics_draw_box(display, 46, 82, 228, 18, color_rgb(50, 72, 142));
    draw_text(display, 78, 86, color_rgb(255, 255, 255), "REMOVE FAVORITE?");
    draw_text(display, 72, 116, color_rgb(255, 246, 118), "A Confirm");
    draw_text(display, 72, 134, color_rgb(140, 230, 255), "B Cancel");
}

static uint8_t clamp_keyboard_col(uint8_t row, uint8_t col)
{
    uint8_t count = g_keyboard_counts[row];
    return col >= count ? (uint8_t)(count - 1u) : col;
}

static void keyboard_open_with_purpose(KeyboardMode purpose)
{
    snprintf(g_input_backup, sizeof(g_input_backup), "%s", g_input_buffer);
    g_keyboard_row = 0;
    g_keyboard_col = 0;
    g_keyboard_mode = purpose;
    g_previous_mode = g_mode == BROWSER_MODE_KEYBOARD ? BROWSER_MODE_PAGE : g_mode;
    g_mode = BROWSER_MODE_KEYBOARD;
}

static void keyboard_open_with_current(void)
{
    keyboard_open_with_purpose(KEYBOARD_MODE_URL);
}

static void keyboard_close(bool restore)
{
    if (restore)
    {
        set_input_text(g_input_backup);
    }
    g_mode = g_previous_mode == BROWSER_MODE_KEYBOARD ? BROWSER_MODE_PAGE : g_previous_mode;
}

static void keyboard_insert(const char *text)
{
    size_t insert_len = strlen(text);
    if (insert_len == 0 || g_input_length + insert_len > BROWSER_INPUT_MAX)
    {
        return;
    }

    memmove(&g_input_buffer[g_cursor_position + insert_len],
        &g_input_buffer[g_cursor_position],
        g_input_length - g_cursor_position + 1u);
    memcpy(&g_input_buffer[g_cursor_position], text, insert_len);
    g_input_length += insert_len;
    g_cursor_position += insert_len;
}

static void keyboard_backspace(void)
{
    if (g_cursor_position == 0)
    {
        return;
    }

    memmove(&g_input_buffer[g_cursor_position - 1u],
        &g_input_buffer[g_cursor_position],
        g_input_length - g_cursor_position + 1u);
    g_cursor_position--;
    g_input_length--;
}

static void keyboard_clear(void)
{
    g_input_buffer[0] = '\0';
    g_input_length = 0;
    g_cursor_position = 0;
}

static void keyboard_accept(void)
{
    KeyboardMode purpose = g_keyboard_mode;
    keyboard_close(false);
    if (purpose == KEYBOARD_MODE_SEARCH)
    {
        navigate_to_search();
    }
    else
    {
        navigate_to_input();
    }
}

static void keyboard_activate_selected(void)
{
    const KeyboardKey *key = &g_keyboard_rows[g_keyboard_row][g_keyboard_col];

    switch (key->type)
    {
    case KEY_TEXT:
    case KEY_SPACE:
        keyboard_insert(key->value);
        BrowserAudio_PlayClick();
        break;
    case KEY_BACKSPACE:
        keyboard_backspace();
        BrowserAudio_PlayBack();
        break;
    case KEY_CLEAR:
        keyboard_clear();
        BrowserAudio_PlayMenu();
        break;
    case KEY_CANCEL:
        keyboard_close(true);
        BrowserAudio_PlayBack();
        break;
    case KEY_ENTER:
        BrowserAudio_PlayOpen();
        keyboard_accept();
        break;
    }
}

static void keyboard_update(joypad_buttons_t pressed)
{
    if (g_keyboard_row == 6u && pressed.d_up)
    {
        g_keyboard_col = g_keyboard_col == 0u ? 2u : (uint8_t)(g_keyboard_col - 1u);
    }
    else if (g_keyboard_row == 6u && pressed.d_down)
    {
        g_keyboard_col = (uint8_t)((g_keyboard_col + 1u) % 3u);
    }
    else if (g_keyboard_row == 6u && pressed.d_left)
    {
        g_keyboard_row = g_keyboard_col;
        g_keyboard_col = (uint8_t)(g_keyboard_counts[g_keyboard_row] - 1u);
    }
    else if (g_keyboard_row < 3u && g_keyboard_col == (uint8_t)(g_keyboard_counts[g_keyboard_row] - 1u) && pressed.d_right)
    {
        g_keyboard_col = g_keyboard_row;
        g_keyboard_row = 6u;
    }
    else if (pressed.d_up)
    {
        g_keyboard_row = g_keyboard_row == 0 ? (uint8_t)(KEYBOARD_ROWS - 1u) : (uint8_t)(g_keyboard_row - 1u);
        g_keyboard_col = clamp_keyboard_col(g_keyboard_row, g_keyboard_col);
    }
    else if (pressed.d_down)
    {
        g_keyboard_row = (uint8_t)((g_keyboard_row + 1u) % KEYBOARD_ROWS);
        g_keyboard_col = clamp_keyboard_col(g_keyboard_row, g_keyboard_col);
    }
    else if (pressed.d_left)
    {
        g_keyboard_col = g_keyboard_col == 0 ? (uint8_t)(g_keyboard_counts[g_keyboard_row] - 1u) : (uint8_t)(g_keyboard_col - 1u);
    }
    else if (pressed.d_right)
    {
        g_keyboard_col = (uint8_t)((g_keyboard_col + 1u) % g_keyboard_counts[g_keyboard_row]);
    }

    if (pressed.c_left && g_cursor_position > 0)
    {
        g_cursor_position--;
    }
    else if (pressed.c_right && g_cursor_position < g_input_length)
    {
        g_cursor_position++;
    }
    else if (pressed.c_up)
    {
        g_cursor_position = 0;
    }
    else if (pressed.c_down)
    {
        g_cursor_position = g_input_length;
    }

    if (pressed.a)
    {
        keyboard_activate_selected();
    }
    else if (pressed.b)
    {
        if (g_input_length == 0)
        {
            keyboard_close(true);
        }
        else
        {
            keyboard_backspace();
        }
        BrowserAudio_PlayBack();
    }
    else if (pressed.z)
    {
        keyboard_close(false);
        BrowserAudio_PlayBack();
    }
    else if (pressed.start)
    {
        BrowserAudio_PlayOpen();
        keyboard_accept();
    }

}

static void draw_input_field(surface_t *display)
{
    char visible[29];
    char cursor_line[29];
    size_t visible_chars = 27;
    size_t start = 0;
    size_t cursor_x;

    if (g_cursor_position >= visible_chars)
    {
        start = g_cursor_position - visible_chars + 1u;
    }
    if (start + visible_chars > g_input_length && g_input_length > visible_chars)
    {
        start = g_input_length - visible_chars;
    }

    snprintf(visible, sizeof(visible), "%.*s", (int)visible_chars, &g_input_buffer[start]);
    memset(cursor_line, ' ', visible_chars);
    cursor_line[visible_chars] = '\0';
    cursor_x = g_cursor_position >= start ? g_cursor_position - start : 0;
    if (cursor_x >= visible_chars)
    {
        cursor_x = visible_chars - 1u;
    }
    cursor_line[cursor_x] = '^';

    graphics_draw_box(display, 34, 54, 252, 26, color_rgb(8, 12, 28));
    draw_text(display, 40, 60, color_rgb(226, 236, 246), visible);
    draw_text(display, 40, 70, color_rgb(255, 246, 118), cursor_line);
}

static int key_width(const KeyboardKey *key)
{
    size_t len = strlen(key->label);
    if (len <= 1) return 20;
    if (len <= 4) return 42;
    return 64;
}

static void draw_keyboard_key(surface_t *display, const KeyboardKey *key, int x, int y, int width, bool selected)
{
    graphics_draw_box(display, x, y, width - 2, 14, selected ? color_rgb(60, 76, 152) : color_rgb(25, 38, 78));
    draw_text(display, x + 4, y + 4, selected ? color_rgb(255, 246, 118) : color_rgb(226, 236, 246), key->label);
}

static void draw_keyboard_overlay(surface_t *display)
{
    graphics_draw_box(display, 12, 28, 296, 190, color_rgb(4, 7, 18));
    graphics_draw_box(display, 14, 30, 292, 186, color_rgb(18, 28, 58));
    graphics_draw_box(display, 16, 32, 288, 18, color_rgb(48, 66, 132));
    draw_text(display, 82, 38, color_rgb(255, 255, 255), "ADDRESS / SEARCH");
    draw_input_field(display);

    for (uint8_t row = 0; row < 5; row++)
    {
        int x = 20;
        int y = 91 + (int)row * 17;
        for (uint8_t col = 0; col < g_keyboard_counts[row]; col++)
        {
            const KeyboardKey *key = &g_keyboard_rows[row][col];
            int width = key_width(key);
            bool selected = row == g_keyboard_row && col == g_keyboard_col;
            draw_keyboard_key(display, key, x, y, width, selected);
            x += width;
        }
    }
    {
        int x = 20;
        for (uint8_t col = 0; col < g_keyboard_counts[5]; col++)
        {
            const KeyboardKey *key = &g_keyboard_rows[5][col];
            int width = col == 0 ? 64 : 52;
            draw_keyboard_key(display, key, x, 181, width, g_keyboard_row == 5 && g_keyboard_col == col);
            x += width;
        }
    }
    for (uint8_t col = 0; col < g_keyboard_counts[6]; col++)
    {
        const KeyboardKey *key = &g_keyboard_rows[6][col];
        draw_keyboard_key(display, key, 236, 91 + (int)col * 27, 68, g_keyboard_row == 6 && g_keyboard_col == col);
    }
}

static void menu_activate(void)
{
    g_mode = BROWSER_MODE_PAGE;
    switch (g_menu_index)
    {
    case 0:
        break;
    case 1:
        set_input_text(HOME_RESOURCE);
        load_browser_document(HOME_RESOURCE, true, true);
        break;
    case 2:
        set_input_text("");
        keyboard_open_with_current();
        break;
    case 3:
        reload_current();
        break;
    case 4:
        restore_history(g_history_index - 1);
        break;
    case 5:
        restore_history(g_history_index + 1);
        break;
    case 6:
        profile_open_view(BROWSER_PROFILE_VIEW_HISTORY);
        break;
    case 7:
        profile_open_view(BROWSER_PROFILE_VIEW_RECENTS);
        break;
    case 8:
        profile_open_view(BROWSER_PROFILE_VIEW_FAVORITES);
        break;
    case 9:
        if (g_current_canonical_url[0] == '\0')
        {
            snprintf(g_status_line, sizeof(g_status_line), "FAVORITE UNAVAILABLE");
        }
        else if (BrowserProfile_HasFavoriteUrl(g_current_canonical_url))
        {
            snprintf(g_status_line, sizeof(g_status_line), "ALREADY IN FAVORITES");
        }
        else if (BrowserProfile_AddFavorite(NICE_CurrentDocument() == NULL || NICE_CurrentDocument()->title == NULL ? "NICE Page" : NICE_CurrentDocument()->title,
                                            g_current_canonical_url, 0u, 0u))
        {
            snprintf(g_status_line, sizeof(g_status_line), "FAVORITE SAVED");
        }
        else
        {
            snprintf(g_status_line, sizeof(g_status_line), "SAVE FAILED");
        }
        break;
    case 10:
        g_options_index = 0u;
        g_previous_mode = BROWSER_MODE_PAGE;
        g_mode = BROWSER_MODE_OPTIONS;
        break;
    }
}

static void begin_clear_confirmation(BrowserClearAction action)
{
    g_pending_clear_action = action;
    g_confirm_index = 0u;
    g_mode = BROWSER_MODE_CONFIRM_CLEAR;
    debugf("CLEAR_REQUEST action=%s\n", browser_clear_action_name(action));
}

static void complete_clear_confirmation(void)
{
    bool succeeded = false;
    uint16_t previous_count;

    switch (g_pending_clear_action)
    {
    case BROWSER_CLEAR_HISTORY:
        previous_count = BrowserProfile_HistoryCount();
        succeeded = BrowserProfile_ClearHistory();
        if (succeeded)
        {
            browser_clear_session_history();
            g_profile_list_count = 0u;
            g_profile_list_index = 0u;
            snprintf(g_status_line, sizeof(g_status_line), "HISTORY CLEARED");
        }
        debugf("CLEAR_%s action=HISTORY count=%u\n", succeeded ? "COMPLETE" : "FAILED", previous_count);
        break;
    case BROWSER_CLEAR_RECENTS:
        previous_count = BrowserProfile_RecentCount();
        succeeded = BrowserProfile_ClearRecentAddresses();
        if (succeeded)
        {
            g_profile_list_count = 0u;
            g_profile_list_index = 0u;
            snprintf(g_status_line, sizeof(g_status_line), "RECENT ADDRESSES CLEARED");
        }
        debugf("CLEAR_%s action=RECENTS count=%u\n", succeeded ? "COMPLETE" : "FAILED", previous_count);
        break;
    case BROWSER_CLEAR_CACHE:
        previous_count = browser_clear_page_cache();
        succeeded = true;
        snprintf(g_status_line, sizeof(g_status_line), "CACHE CLEARED");
        debugf("CLEAR_COMPLETE action=CACHE entries=%u\n", previous_count);
        break;
    default:
        break;
    }

    if (!succeeded)
    {
        snprintf(g_status_line, sizeof(g_status_line), "CLEAR FAILED");
        BrowserAudio_PlayError();
    }
    g_pending_clear_action = BROWSER_CLEAR_NONE;
    g_mode = BROWSER_MODE_OPTIONS;
}

static void options_activate(void)
{
    switch (g_options_index)
    {
    case 0:
        set_input_text("browser://test");
        load_browser_document("browser://test", true, true);
        break;
    case 1: begin_clear_confirmation(BROWSER_CLEAR_HISTORY); break;
    case 2: begin_clear_confirmation(BROWSER_CLEAR_RECENTS); break;
    case 3: begin_clear_confirmation(BROWSER_CLEAR_CACHE); break;
    case 4:
        Browser_PrepareForDocumentRequest();
        NICE_LoadDocument(0x00001111u, &g_about_page);
        NICE_RenderPage(NICE_CurrentDocument());
        snprintf(g_current_resource, sizeof(g_current_resource), "nice://about");
        snprintf(g_status_line, sizeof(g_status_line), "Controls");
        break;
    }
}

void Browser_Init(void)
{
    memset(g_future_page_cache, 0, sizeof(g_future_page_cache));
    memset(g_cache, 0, sizeof(g_cache));
    memset(g_history, 0, sizeof(g_history));
    g_cache_clock = 0;
    g_history_count = 0;
    g_history_index = -1;
    g_menu_index = 0;
    g_options_index = 0u;
    g_confirm_index = 0u;
    g_pending_clear_action = BROWSER_CLEAR_NONE;
    g_mode = BROWSER_MODE_PAGE;
    g_previous_mode = BROWSER_MODE_PAGE;
    g_network_document_ready = false;
    g_network_document_failed = false;
    g_poll_active = false;
    g_poll_route[0] = '\0';
    g_poll_origin[0] = '\0';
    g_poll_frames = 0;
    g_poll_attempts = 0;
    g_profile_pending_url[0] = '\0';
    g_current_canonical_url[0] = '\0';
    g_browser_started = false;
    BrowserProfile_Init();
    set_input_text(BrowserProfile_Homepage());
    snprintf(g_current_resource, sizeof(g_current_resource), "%s", BrowserProfile_Homepage());
    NICE_DocumentLoaderInit();
    ImageTransport_Init();
}

void Browser_Start(void)
{
    if (g_browser_started) return;
    g_browser_started = true;
    BrowserTransport_Init();
    load_browser_document(BrowserProfile_Homepage(), true, true);
}

void Browser_Update(joypad_buttons_t pressed, joypad_inputs_t inputs)
{
    g_document_work_this_frame = false;
    if (g_mode == BROWSER_MODE_LOADING)
    {
        g_loading_animation_frames++;
        if (pressed.b)
        {
            BrowserTransport_Cancel();
            browser_clear_pending_poll("user cancel");
            if (g_history_index >= 0)
            {
                restore_history(g_history_index);
            }
            else
            {
                load_browser_document(HOME_RESOURCE, true, true);
            }
            return;
        }
        if (BrowserTransport_IsLoading())
        {
            BrowserTransport_Update();
            if (BrowserTransport_IsLoading())
            {
                return;
            }
            if (BrowserTransport_GetStatus().state == BROWSER_TRANSPORT_READY)
            {
                browser_complete_document_load();
                return;
            }
            if (g_poll_active)
            {
                BrowserTransportStatus status = BrowserTransport_GetStatus();
                debugf("Browser: poll transport failure attempt=%lu route=%s detail=%s; retrying\n",
                    (unsigned long)g_poll_attempts, g_current_resource, status.message);
                snprintf(g_status_line, sizeof(g_status_line), "Waiting for Bridge");
                return;
            }
            load_error_page(BrowserTransport_GetStatus(), g_current_resource);
            return;
        }
        if (g_poll_active && ++g_poll_frames >= BROWSER_POLL_FRAME_INTERVAL)
        {
            g_poll_frames = 0;
            if (++g_poll_attempts > BROWSER_POLL_MAX_ATTEMPTS)
            {
                BrowserTransportStatus status = BrowserTransport_GetStatus();
                status.result = BROWSER_ERROR_RESOURCE_REQUEST;
                snprintf(status.message, sizeof(status.message), "BRIDGE_POLL_TIMEOUT");
                load_error_page(status, g_poll_origin);
                browser_clear_pending_poll("timeout");
                return;
            }
            load_browser_document_internal(g_poll_route, false, false, true);
        }
        return;
    }
    if (g_mode == BROWSER_MODE_KEYBOARD)
    {
        keyboard_update(pressed);
        return;
    }

    if (g_mode == BROWSER_MODE_CONFIRM_REMOVE_FAVORITE)
    {
        if (pressed.a)
        {
            if (BrowserProfile_RemoveFavorite(g_profile_remove_index))
            {
                snprintf(g_status_line, sizeof(g_status_line), "FAVORITE REMOVED");
                profile_rebuild_indices();
            }
            else
            {
                snprintf(g_status_line, sizeof(g_status_line), "SAVE FAILED");
                BrowserAudio_PlayError();
            }
            g_mode = BROWSER_MODE_PROFILE_LIST;
        }
        else if (pressed.b || pressed.start)
        {
            g_mode = BROWSER_MODE_PROFILE_LIST;
        }
        return;
    }

    if (g_mode == BROWSER_MODE_CONFIRM_CLEAR)
    {
        if (pressed.d_left || pressed.d_right || pressed.d_up || pressed.d_down)
        {
            g_confirm_index = g_confirm_index == 0u ? 1u : 0u;
        }
        else if (pressed.a)
        {
            if (g_confirm_index == 0u)
            {
                debugf("CLEAR_CANCEL action=%s\n", browser_clear_action_name(g_pending_clear_action));
                g_pending_clear_action = BROWSER_CLEAR_NONE;
                g_mode = BROWSER_MODE_OPTIONS;
                BrowserAudio_PlayBack();
            }
            else
            {
                debugf("CLEAR_CONFIRM action=%s\n", browser_clear_action_name(g_pending_clear_action));
                complete_clear_confirmation();
            }
        }
        else if (pressed.b || pressed.start)
        {
            debugf("CLEAR_CANCEL action=%s\n", browser_clear_action_name(g_pending_clear_action));
            g_pending_clear_action = BROWSER_CLEAR_NONE;
            g_mode = BROWSER_MODE_OPTIONS;
            BrowserAudio_PlayBack();
        }
        return;
    }

    if (g_mode == BROWSER_MODE_OPTIONS)
    {
        if (pressed.d_up)
        {
            g_options_index = g_options_index == 0u ? (uint8_t)(OPTIONS_ITEMS - 1u) : (uint8_t)(g_options_index - 1u);
        }
        else if (pressed.d_down)
        {
            g_options_index = (uint8_t)((g_options_index + 1u) % OPTIONS_ITEMS);
        }
        else if (pressed.a)
        {
            BrowserAudio_PlayMenu();
            options_activate();
        }
        else if (pressed.b || pressed.start)
        {
            BrowserAudio_PlayBack();
            g_mode = g_previous_mode;
        }
        return;
    }

    if (g_mode == BROWSER_MODE_PROFILE_LIST)
    {
        if (pressed.d_up && g_profile_list_count > 0u)
        {
            g_profile_list_index = g_profile_list_index == 0u ? (uint16_t)(g_profile_list_count - 1u) : (uint16_t)(g_profile_list_index - 1u);
        }
        else if (pressed.d_down && g_profile_list_count > 0u)
        {
            g_profile_list_index = (uint16_t)((g_profile_list_index + 1u) % g_profile_list_count);
        }
        else if (pressed.a && g_profile_list_count > 0u)
        {
            if (g_profile_view == BROWSER_PROFILE_VIEW_FAVORITES) BrowserAudio_PlayFavorite();
            else if (g_profile_view == BROWSER_PROFILE_VIEW_HISTORY) BrowserAudio_PlayHistory();
            else BrowserAudio_PlayRecent();
            profile_open_selected();
        }
        else if (pressed.z && g_profile_view == BROWSER_PROFILE_VIEW_FAVORITES && g_profile_list_count > 0u)
        {
            bool pinned = false;
            uint16_t source_index = g_profile_indices[g_profile_list_index];
            if (BrowserProfile_ToggleFavoriteHomePin(source_index, &pinned))
            {
                snprintf(g_status_line, sizeof(g_status_line), pinned ? "Added to Home" : "Removed from Home");
                BrowserAudio_PlayFavorite();
            }
            else
            {
                snprintf(g_status_line, sizeof(g_status_line), "SAVE FAILED");
                BrowserAudio_PlayError();
            }
        }
        else if (pressed.c_down && g_profile_view == BROWSER_PROFILE_VIEW_FAVORITES && g_profile_list_count > 0u)
        {
            g_profile_remove_index = g_profile_indices[g_profile_list_index];
            g_mode = BROWSER_MODE_CONFIRM_REMOVE_FAVORITE;
        }
        else if (pressed.b || pressed.start)
        {
            BrowserAudio_PlayBack();
            g_mode = g_previous_mode;
        }
        return;
    }

    if (g_mode == BROWSER_MODE_MENU)
    {
        if (pressed.d_up)
        {
            g_menu_index = g_menu_index == 0 ? (uint8_t)(MENU_ITEMS - 1u) : (uint8_t)(g_menu_index - 1u);
        }
        else if (pressed.d_down)
        {
            g_menu_index = (uint8_t)((g_menu_index + 1u) % MENU_ITEMS);
        }
        else if (pressed.a)
        {
            BrowserAudio_PlayMenu();
            menu_activate();
        }
        else if (pressed.b || pressed.start)
        {
            BrowserAudio_PlayBack();
            g_mode = BROWSER_MODE_PAGE;
        }
        return;
    }

    if (pressed.l && pressed.r)
    {
        reload_current();
        return;
    }
    if (pressed.l)
    {
        if (g_history_index > 0)
        {
            BrowserAudio_PlayBack();
            restore_history(g_history_index - 1);
        }
        return;
    }
    if (pressed.r)
    {
        if (g_history_index >= 0 && g_history_index + 1 < g_history_count)
        {
            BrowserAudio_PlayForward();
            restore_history(g_history_index + 1);
        }
        return;
    }
    if (pressed.z)
    {
        keyboard_open_with_current();
        return;
    }
    if (pressed.start)
    {
        g_menu_index = 0;
        g_mode = BROWSER_MODE_MENU;
        return;
    }

    NICE_HandleInput(pressed, inputs);

    if (pressed.a)
    {
        if (g_network_document_failed || g_mode == BROWSER_MODE_ERROR)
        {
            BrowserAudio_PlayOpen();
            reload_current();
        }
        else
        {
            const char *target = NICE_GetFocusedTarget();
            if (target != NULL)
            {
                BrowserAudio_PlayOpen();
                navigate_to_target(target);
            }
        }
    }

    Browser_UpdateImageTransport();
    BrowserProfile_SaveIfNeeded();
}

void Browser_Draw(surface_t *display)
{
    char compact_status[24];
    const char *banner_reason;
    bool has_validated_image;
    bool has_app_banner;
    bool is_home_page = strcmp(g_current_resource, HOME_RESOURCE) == 0;

    NICE_SetHomeBackgroundEnabled(is_home_page);
    NICE_Draw(display);

    /* Do not schedule an empty RDPQ frame while the CPU is receiving image chunks. */
    has_validated_image = NICE_HasValidatedImage();
    has_app_banner = Browser_ShouldDrawAppBanner(&banner_reason);
    if (!g_banner_policy_initialized ||
        has_app_banner != g_banner_policy_last_frame ||
        g_mode != g_banner_policy_last_mode)
    {
        debugf("BANNER_POLICY mode=%d resource=%s show=%d reason=%s\n",
            g_mode, g_current_resource, has_app_banner, banner_reason);
        g_banner_policy_initialized = true;
        g_banner_policy_last_frame = has_app_banner;
        g_banner_policy_last_mode = g_mode;
    }
    if (has_validated_image != g_rdp_image_work_last_frame)
    {
        ImageTransportStatus status = ImageTransport_GetStatus();
        debugf("Browser: RDP image work %s state=%d received=%lu expected=%lu\n",
            has_validated_image ? "enabled" : "skipped", status.state,
            (unsigned long)status.bytes_received, (unsigned long)status.expected_bytes);
        g_rdp_image_work_last_frame = has_validated_image;
    }
    if (has_app_banner || has_validated_image)
    {
        data_cache_hit_writeback(display->buffer, (unsigned long)display->stride * display->height);
        rdpq_attach(display, NULL);
        if (has_app_banner)
        {
            rdpq_set_mode_standard();
            NICE_DrawAppBanner();
        }
        if (has_validated_image)
        {
            rdpq_set_mode_copy(true);
            NICE_DrawValidatedImage();
            NICE_DrawImageReferencePatternIfNeeded();
        }
        rdpq_detach_wait();
        if (has_validated_image) NICE_MarkImageDrawCompleted();
    }

    graphics_draw_box(display, 0, 32, 320, 30, color_rgb(7, 14, 34));
    graphics_draw_line(display, 0, 62, 319, 62, color_rgb(38, 72, 132));
    if (strncmp(g_current_resource, "browser://", 10) == 0) draw_text(display, 12, 50, color_rgb(180, 198, 218), "Web Page");
    else if (strncmp(g_current_resource, "nice://", 7) == 0) draw_text(display, 12, 50, color_rgb(180, 198, 218), "NICE Home");
    else draw_text(display, 12, 50, color_rgb(180, 198, 218), "Local Page");
    if (!is_home_page)
    {
        snprintf(compact_status, sizeof(compact_status), "%.22s", g_status_line);
        draw_text(display, 142, 50, color_rgb(140, 230, 255), compact_status);
    }

    if (g_mode == BROWSER_MODE_LOADING)
    {
        static const char * const loading_text[] = { "Loading", "Loading.", "Loading..", "Loading..." };
        const char *text = loading_text[(g_loading_animation_frames / BROWSER_LOADING_ANIMATION_FRAMES) % 4u];
        int width = (int)strlen(text) * 8;
        graphics_draw_box(display, 69, 92, 182, 54, color_rgb(4, 8, 20));
        graphics_draw_box(display, 71, 94, 178, 50, color_rgb(12, 22, 48));
        graphics_draw_line(display, 69, 92, 250, 92, color_rgb(62, 111, 184));
        graphics_draw_line(display, 69, 145, 250, 145, color_rgb(62, 111, 184));
        graphics_draw_line(display, 69, 92, 69, 145, color_rgb(62, 111, 184));
        graphics_draw_line(display, 250, 92, 250, 145, color_rgb(62, 111, 184));
        draw_text(display, (320 - width) / 2, 113, color_rgb(140, 230, 255), text);
    }

    if (g_mode == BROWSER_MODE_MENU)
    {
        draw_menu_overlay(display);
    }
    if (g_mode == BROWSER_MODE_PROFILE_LIST)
    {
        draw_profile_list_overlay(display);
    }
    if (g_mode == BROWSER_MODE_CONFIRM_REMOVE_FAVORITE)
    {
        draw_profile_list_overlay(display);
        draw_remove_favorite_overlay(display);
    }
    if (g_mode == BROWSER_MODE_OPTIONS)
    {
        draw_options_overlay(display);
    }
    if (g_mode == BROWSER_MODE_CONFIRM_CLEAR)
    {
        draw_options_overlay(display);
        draw_clear_confirmation_overlay(display);
    }
    if (g_mode == BROWSER_MODE_KEYBOARD)
    {
        draw_keyboard_overlay(display);
    }
}

bool Browser_MenuVisible(void)
{
    return g_mode == BROWSER_MODE_MENU;
}
