#include "network_pak_transport.h"
#include "protocol.h"

#include <libdragon.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#define MENU_RESOURCE "bridge://menu"
#define TIME_RESOURCE "bridge://time"
#define HEADLINES_RESOURCE "bridge://headlines"
#define OLLAMA_RESOURCE "bridge://ollama"
#define OLLAMA_RESULT_RESOURCE "bridge://aires"
#define SPLASH_FRAME_COUNT 150u
#define CLOCK_SYNC_SECONDS 60u
#define STREAM_BUFFER_CAPACITY 16384u
#define PAGE_LINE_CAPACITY 256u
#define PAGE_WRAP_COLUMNS 35u
#define PAGE_VISIBLE_LINES 16u
#define KEYBOARD_TEXT_CAPACITY 128u
#define PORTAL_MENU_CAPACITY 12u
#define PORTAL_TITLE_LENGTH 32u
#define PORTAL_RESOURCE_LENGTH 64u
#define PORTAL_ICON_LENGTH 16u
#define SCREEN_WIDTH 320
#define SCREEN_HEIGHT 240
#define COLOR_TRANSPARENT graphics_make_color(0, 0, 0, 0)

typedef enum portal_screen_e
{
    PORTAL_SCREEN_SPLASH,
    PORTAL_SCREEN_TITLE,
    PORTAL_SCREEN_CONNECTING,
    PORTAL_SCREEN_STATUS,
    PORTAL_SCREEN_MENU,
    PORTAL_SCREEN_KEYBOARD,
    PORTAL_SCREEN_LOADING,
    PORTAL_SCREEN_PAGE,
    PORTAL_SCREEN_ERROR
} portal_screen_t;

typedef struct portal_menu_item_s
{
    char title[PORTAL_TITLE_LENGTH];
    char resource[PORTAL_RESOURCE_LENGTH];
    char icon[PORTAL_ICON_LENGTH];
} portal_menu_item_t;

typedef struct stream_state_s
{
    bool open;
    uint8_t id;
    uint8_t chunk_size;
    uint32_t total_length;
    uint32_t expected_crc32;
    uint32_t next_offset;
    uint32_t bytes_received;
    char resource[PORTAL_RESOURCE_LENGTH];
    char last_error[32];
} stream_state_t;

typedef struct portal_clock_s
{
    bool valid;
    bool sync_ok;
    uint16_t year;
    uint8_t month;
    uint8_t day;
    uint8_t hour;
    uint8_t minute;
    uint8_t second;
    uint16_t frame_accumulator;
    uint16_t seconds_since_sync;
} portal_clock_t;

typedef struct feed_source_s
{
    const char *name;
    const char *resource;
} feed_source_t;

static const feed_source_t g_feed_sources[] =
{
    { "Retro Gaming", "bridge://rssr" },
    { "General News", "bridge://rssn" }
};

static const char *g_keyboard_keys[] =
{
    "A", "B", "C", "D", "E", "F", "G", "H", "I", "J",
    "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T",
    "U", "V", "W", "X", "Y", "Z",
    "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
    "SPACE", "<-", "SEND"
};

static const size_t g_keyboard_key_count = sizeof(g_keyboard_keys) / sizeof(g_keyboard_keys[0]);

static portal_screen_t g_screen = PORTAL_SCREEN_SPLASH;
static portal_menu_item_t g_menu_items[PORTAL_MENU_CAPACITY];
static size_t g_menu_count;
static size_t g_selected_menu_item;
static size_t g_current_menu_item;
static size_t g_feed_source_index;
static size_t g_scroll_line;
static size_t g_keyboard_selected;
static size_t g_keyboard_text_length;
static uint8_t g_keyboard_move_cooldown;
static uint16_t g_splash_frames;
static uint8_t g_sequence;
static bool g_network_ok;
static bool g_bridge_ok;
static bool g_menu_ok;
static char g_status_message[64] = "Starting Portal";
static char g_error_message[64] = "OK";
static portal_clock_t g_clock;
static stream_state_t g_stream;
static uint8_t g_stream_buffer[STREAM_BUFFER_CAPACITY + 1u];
static char g_keyboard_text[KEYBOARD_TEXT_CAPACITY + 1u];
static uint16_t g_line_offsets[PAGE_LINE_CAPACITY];
static size_t g_line_count;
static uint32_t g_stream_open_count;
static uint32_t g_stream_chunk_count;
static uint32_t g_stream_close_count;
static uint32_t g_stream_error_count;
static sprite_t *g_logo_sprite;
static sprite_t *g_splash_banner_sprite;
static sprite_t *g_header_banner_sprite;
static sprite_t *g_cursor_sprite;
static sprite_t *g_status_online_sprite;
static sprite_t *g_icon_smile;
static sprite_t *g_icon_news;
static sprite_t *g_icon_web;
static sprite_t *g_icon_info;
static sprite_t *g_icon_diag;
static sprite_t *g_icon_folder;
static sprite_t *g_icon_clock;
static sprite_t *g_icon_chat;
static sprite_t *g_icon_generic;

static void draw_screen(void);
static bool load_page(size_t menu_index);

static uint32_t read_be32(const uint8_t *data)
{
    return ((uint32_t)data[0] << 24) |
        ((uint32_t)data[1] << 16) |
        ((uint32_t)data[2] << 8) |
        (uint32_t)data[3];
}

static void write_be32(uint8_t *data, uint32_t value)
{
    data[0] = (uint8_t)(value >> 24);
    data[1] = (uint8_t)(value >> 16);
    data[2] = (uint8_t)(value >> 8);
    data[3] = (uint8_t)value;
}

static uint32_t stream_crc32(const uint8_t *data, uint32_t length)
{
    uint32_t crc = 0xFFFFFFFFu;
    for (uint32_t i = 0; i < length; i++)
    {
        crc ^= data[i];
        for (uint8_t bit = 0; bit < 8; bit++)
        {
            crc = (crc >> 1) ^ (0xEDB88320u & (uint32_t)-(int32_t)(crc & 1u));
        }
    }
    return ~crc;
}

static bool command_matches_request(uint8_t request_command, uint8_t response_command)
{
    switch ((nice_net_command_t)request_command)
    {
    case NICE_NET_CMD_PING: return response_command == NICE_NET_CMD_PONG;
    case NICE_NET_CMD_GET_STATUS: return response_command == NICE_NET_CMD_STATUS;
    case NICE_NET_CMD_PING_BRIDGE:
    case NICE_NET_CMD_GET_BRIDGE_STATUS:
        return response_command == NICE_NET_CMD_BRIDGE_RESPONSE;
    case NICE_NET_CMD_OPEN_STREAM: return response_command == NICE_NET_CMD_STREAM_OPENED;
    case NICE_NET_CMD_GET_STREAM_INFO: return response_command == NICE_NET_CMD_STREAM_INFO;
    case NICE_NET_CMD_READ_STREAM_CHUNK: return response_command == NICE_NET_CMD_STREAM_CHUNK;
    case NICE_NET_CMD_WRITE_STREAM_CHUNK: return response_command == NICE_NET_CMD_STREAM_WRITE_ACK;
    case NICE_NET_CMD_CLOSE_STREAM: return response_command == NICE_NET_CMD_STREAM_CLOSED;
    default: return response_command == NICE_NET_CMD_ERROR;
    }
}

static bool run_packet_command(
    nice_net_command_t command,
    const void *payload,
    size_t payload_length,
    bool require_success,
    nice_net_packet_t *response)
{
    nice_net_packet_t request;
    nice_net_error_t error = NICE_NET_ERR_NONE;
    bool ok;

    g_sequence++;
    nice_net_packet_init(&request, command, 0, g_sequence, payload, payload_length);
    ok = network_pak_exchange(&request, response, &error);

    if (!ok)
    {
        snprintf(g_error_message, sizeof(g_error_message), "%s", nice_net_error_name(error));
        return false;
    }

    if (response->sequence != request.sequence)
    {
        snprintf(g_error_message, sizeof(g_error_message), "BAD_SEQUENCE");
        return false;
    }

    if (!command_matches_request(command, response->command))
    {
        if (response->command == NICE_NET_CMD_ERROR && response->payload_length > 0)
        {
            size_t copy_length = response->payload_length < sizeof(g_error_message) - 1u
                ? response->payload_length
                : sizeof(g_error_message) - 1u;
            memcpy(g_error_message, response->payload, copy_length);
            g_error_message[copy_length] = '\0';
        }
        else
        {
            snprintf(g_error_message, sizeof(g_error_message), "BAD_RESPONSE %02X", response->command);
        }
        return !require_success;
    }

    if (response->command == NICE_NET_CMD_ERROR && require_success)
    {
        snprintf(g_error_message, sizeof(g_error_message), "COMMAND_ERROR");
        return false;
    }

    snprintf(g_error_message, sizeof(g_error_message), "OK");
    return true;
}

static bool stream_open(const char *resource)
{
    nice_net_packet_t response;
    size_t resource_length = strlen(resource);
    if (resource_length == 0 || resource_length > NICE_NET_MAX_PAYLOAD)
    {
        snprintf(g_error_message, sizeof(g_error_message), "RESOURCE_TOO_LONG");
        g_stream_error_count++;
        return false;
    }

    if (!run_packet_command(NICE_NET_CMD_OPEN_STREAM, resource, resource_length, true, &response))
    {
        g_stream_error_count++;
        return false;
    }

    if (response.payload_length != 10)
    {
        snprintf(g_error_message, sizeof(g_error_message), "BAD_OPEN_METADATA");
        g_stream_error_count++;
        return false;
    }

    memset(&g_stream, 0, sizeof(g_stream));
    g_stream.open = true;
    g_stream.id = response.payload[0];
    g_stream.total_length = read_be32(&response.payload[1]);
    g_stream.chunk_size = response.payload[5];
    g_stream.expected_crc32 = read_be32(&response.payload[6]);
    snprintf(g_stream.resource, sizeof(g_stream.resource), "%s", resource);
    snprintf(g_stream.last_error, sizeof(g_stream.last_error), "None");

    if (g_stream.id == 0 ||
        g_stream.chunk_size == 0 ||
        g_stream.chunk_size > NICE_NET_STREAM_CHUNK_SIZE ||
        g_stream.total_length > STREAM_BUFFER_CAPACITY)
    {
        g_stream.open = false;
        snprintf(g_error_message, sizeof(g_error_message), "UNSUPPORTED_STREAM");
        g_stream_error_count++;
        return false;
    }

    g_stream_open_count++;
    return true;
}

static bool stream_read_next(void)
{
    uint8_t request[5];
    nice_net_packet_t response;
    uint8_t count;
    bool done;

    if (!g_stream.open || g_stream.next_offset >= g_stream.total_length)
    {
        return false;
    }

    request[0] = g_stream.id;
    write_be32(&request[1], g_stream.next_offset);

    if (!run_packet_command(NICE_NET_CMD_READ_STREAM_CHUNK, request, sizeof(request), true, &response))
    {
        g_stream_error_count++;
        return false;
    }

    if (response.payload_length < 7 ||
        response.payload[0] != g_stream.id ||
        read_be32(&response.payload[1]) != g_stream.next_offset)
    {
        snprintf(g_error_message, sizeof(g_error_message), "BAD_CHUNK_HEADER");
        g_stream_error_count++;
        return false;
    }

    count = response.payload[5];
    done = (response.payload[6] & 1u) != 0;
    if (count > g_stream.chunk_size ||
        response.payload_length != (uint8_t)(7u + count) ||
        g_stream.next_offset + count > g_stream.total_length ||
        done != (g_stream.next_offset + count == g_stream.total_length))
    {
        snprintf(g_error_message, sizeof(g_error_message), "BAD_CHUNK");
        g_stream_error_count++;
        return false;
    }

    memcpy(&g_stream_buffer[g_stream.next_offset], &response.payload[7], count);
    g_stream.next_offset += count;
    g_stream.bytes_received += count;
    g_stream_chunk_count++;
    return true;
}

static bool stream_close(void)
{
    nice_net_packet_t response;
    uint8_t request[1];

    if (!g_stream.open)
    {
        return true;
    }

    request[0] = g_stream.id;
    if (!run_packet_command(NICE_NET_CMD_CLOSE_STREAM, request, sizeof(request), true, &response) ||
        response.payload_length != 1 ||
        response.payload[0] != g_stream.id)
    {
        snprintf(g_error_message, sizeof(g_error_message), "CLOSE_FAILED");
        g_stream_error_count++;
        return false;
    }

    g_stream.open = false;
    g_stream_close_count++;
    return true;
}

static bool stream_write_query(const char *text)
{
    size_t length = strlen(text);
    size_t offset = 0;
    bool first = true;

    if (length == 0)
    {
        snprintf(g_error_message, sizeof(g_error_message), "EMPTY_QUERY");
        return false;
    }

    while (offset < length || first)
    {
        uint8_t request[NICE_NET_MAX_PAYLOAD];
        nice_net_packet_t response;
        size_t remaining = length - offset;
        size_t count = remaining > 21u ? 21u : remaining;
        bool last = offset + count >= length;

        request[0] = (uint8_t)((first ? 1u : 0u) | (last ? 2u : 0u));
        request[1] = (uint8_t)count;
        if (count > 0)
        {
            memcpy(&request[2], &text[offset], count);
        }

        if (!run_packet_command(NICE_NET_CMD_WRITE_STREAM_CHUNK, request, 2u + count, true, &response))
        {
            return false;
        }
        if (response.payload_length < 1 || response.payload[0] != 1u)
        {
            snprintf(g_error_message, sizeof(g_error_message), "WRITE_FAILED");
            return false;
        }

        offset += count;
        first = false;
        if (last)
        {
            break;
        }
    }

    return true;
}

static bool load_resource_to_buffer(const char *resource)
{
    bool ok;

    memset(g_stream_buffer, 0, sizeof(g_stream_buffer));
    ok = stream_open(resource);
    while (ok && g_stream.bytes_received < g_stream.total_length)
    {
        ok = stream_read_next();
    }

    if (ok)
    {
        ok = g_stream.bytes_received == g_stream.total_length &&
            stream_crc32(g_stream_buffer, g_stream.bytes_received) == g_stream.expected_crc32;
        if (!ok)
        {
            snprintf(g_error_message, sizeof(g_error_message), "CRC_MISMATCH");
        }
    }

    if (g_stream.open)
    {
        ok = stream_close() && ok;
    }

    if (ok)
    {
        g_stream_buffer[g_stream.bytes_received] = '\0';
    }

    return ok;
}

static bool is_leap_year(uint16_t year)
{
    return (year % 4u == 0u && year % 100u != 0u) || (year % 400u == 0u);
}

static uint8_t days_in_month(uint16_t year, uint8_t month)
{
    static const uint8_t days[] = { 31u, 28u, 31u, 30u, 31u, 30u, 31u, 31u, 30u, 31u, 30u, 31u };
    if (month == 2u && is_leap_year(year))
    {
        return 29u;
    }
    if (month < 1u || month > 12u)
    {
        return 31u;
    }
    return days[month - 1u];
}

static void clock_increment_one_second(void)
{
    if (!g_clock.valid)
    {
        return;
    }

    g_clock.second++;
    if (g_clock.second < 60u)
    {
        return;
    }
    g_clock.second = 0;
    g_clock.minute++;
    if (g_clock.minute < 60u)
    {
        return;
    }
    g_clock.minute = 0;
    g_clock.hour++;
    if (g_clock.hour < 24u)
    {
        return;
    }
    g_clock.hour = 0;
    g_clock.day++;
    if (g_clock.day <= days_in_month(g_clock.year, g_clock.month))
    {
        return;
    }
    g_clock.day = 1;
    g_clock.month++;
    if (g_clock.month <= 12u)
    {
        return;
    }
    g_clock.month = 1;
    g_clock.year++;
}

static bool parse_clock_buffer(void)
{
    const char *date_label = strstr((const char *)g_stream_buffer, "Date:");
    const char *time_label = strstr((const char *)g_stream_buffer, "Time:");
    unsigned int year;
    unsigned int month;
    unsigned int day;
    unsigned int hour;
    unsigned int minute;
    unsigned int second;

    if (date_label == NULL || time_label == NULL)
    {
        return false;
    }

    if (sscanf(date_label, "Date:\n%u-%u-%u", &year, &month, &day) != 3 ||
        sscanf(time_label, "Time:\n%u:%u:%u", &hour, &minute, &second) != 3)
    {
        return false;
    }

    if (year < 2020u || month < 1u || month > 12u || day < 1u || day > 31u ||
        hour > 23u || minute > 59u || second > 59u)
    {
        return false;
    }

    g_clock.valid = true;
    g_clock.sync_ok = true;
    g_clock.year = (uint16_t)year;
    g_clock.month = (uint8_t)month;
    g_clock.day = (uint8_t)day;
    g_clock.hour = (uint8_t)hour;
    g_clock.minute = (uint8_t)minute;
    g_clock.second = (uint8_t)second;
    g_clock.frame_accumulator = 0;
    g_clock.seconds_since_sync = 0;
    return true;
}

static bool sync_clock_from_bridge(void)
{
    if (!load_resource_to_buffer(TIME_RESOURCE))
    {
        g_clock.sync_ok = false;
        if (!g_clock.valid)
        {
            memset(&g_clock, 0, sizeof(g_clock));
        }
        return false;
    }

    if (!parse_clock_buffer())
    {
        g_clock.sync_ok = false;
        if (!g_clock.valid)
        {
            memset(&g_clock, 0, sizeof(g_clock));
        }
        return false;
    }

    return true;
}

static void tick_portal_clock(void)
{
    g_clock.frame_accumulator++;
    if (g_clock.frame_accumulator < 60u)
    {
        return;
    }

    g_clock.frame_accumulator = 0;
    clock_increment_one_second();
    if (g_screen == PORTAL_SCREEN_MENU && g_bridge_ok)
    {
        g_clock.seconds_since_sync++;
        if (g_clock.seconds_since_sync >= CLOCK_SYNC_SECONDS)
        {
            sync_clock_from_bridge();
        }
    }
}

static void copy_trimmed(char *destination, size_t destination_size, const char *start, size_t length)
{
    while (length > 0 && (*start == ' ' || *start == '\t'))
    {
        start++;
        length--;
    }
    while (length > 0 && (start[length - 1u] == ' ' || start[length - 1u] == '\t' || start[length - 1u] == '\r'))
    {
        length--;
    }

    if (length >= destination_size)
    {
        length = destination_size - 1u;
    }
    memcpy(destination, start, length);
    destination[length] = '\0';
}

static bool parse_menu_buffer(void)
{
    char *cursor = (char *)g_stream_buffer;
    g_menu_count = 0;
    g_selected_menu_item = 0;

    while (*cursor != '\0' && g_menu_count < PORTAL_MENU_CAPACITY)
    {
        char *line_start = cursor;
        char *line_end;
        char *separator;
        char *icon_separator;

        while (*cursor != '\0' && *cursor != '\n')
        {
            cursor++;
        }
        line_end = cursor;
        if (*cursor == '\n')
        {
            *cursor = '\0';
            cursor++;
        }
        if (line_end > line_start && line_end[-1] == '\r')
        {
            line_end[-1] = '\0';
            line_end--;
        }

        if (line_start[0] == '\0' || line_start[0] == '#')
        {
            continue;
        }

        separator = strchr(line_start, '|');
        if (separator == NULL)
        {
            continue;
        }

        icon_separator = strchr(separator + 1, '|');
        if (icon_separator != NULL)
        {
            *icon_separator = '\0';
        }

        copy_trimmed(g_menu_items[g_menu_count].title, sizeof(g_menu_items[g_menu_count].title),
            line_start, (size_t)(separator - line_start));
        copy_trimmed(g_menu_items[g_menu_count].resource, sizeof(g_menu_items[g_menu_count].resource),
            separator + 1, strlen(separator + 1));
        if (icon_separator != NULL)
        {
            copy_trimmed(g_menu_items[g_menu_count].icon, sizeof(g_menu_items[g_menu_count].icon),
                icon_separator + 1, strlen(icon_separator + 1));
        }
        else
        {
            g_menu_items[g_menu_count].icon[0] = '\0';
        }

        if (g_menu_items[g_menu_count].title[0] != '\0' &&
            g_menu_items[g_menu_count].resource[0] != '\0')
        {
            g_menu_count++;
        }
    }

    if (g_menu_count == 0)
    {
        snprintf(g_error_message, sizeof(g_error_message), "EMPTY_MENU");
        return false;
    }

    return true;
}

static bool load_menu(void)
{
    if (!load_resource_to_buffer(MENU_RESOURCE))
    {
        return false;
    }
    return parse_menu_buffer();
}

static const char *resolve_menu_resource(size_t menu_index)
{
    if (menu_index >= g_menu_count)
    {
        return "";
    }

    if (strcmp(g_menu_items[menu_index].resource, HEADLINES_RESOURCE) == 0 ||
        strcmp(g_menu_items[menu_index].resource, "bridge://rssr") == 0 ||
        strcmp(g_menu_items[menu_index].resource, "bridge://rssn") == 0)
    {
        return g_feed_sources[g_feed_source_index].resource;
    }

    if (strcmp(g_menu_items[menu_index].resource, OLLAMA_RESOURCE) == 0)
    {
        return OLLAMA_RESULT_RESOURCE;
    }

    return g_menu_items[menu_index].resource;
}

static bool is_ollama_menu_item(size_t menu_index)
{
    return menu_index < g_menu_count &&
        strcmp(g_menu_items[menu_index].resource, OLLAMA_RESOURCE) == 0;
}

static void reset_keyboard(void)
{
    memset(g_keyboard_text, 0, sizeof(g_keyboard_text));
    g_keyboard_text_length = 0;
    g_keyboard_selected = 0;
    g_keyboard_move_cooldown = 0;
}

static size_t keyboard_row_start(size_t row)
{
    switch (row)
    {
    case 0: return 0;
    case 1: return 10;
    case 2: return 20;
    case 3: return 26;
    default: return 36;
    }
}

static size_t keyboard_row_count(size_t row)
{
    switch (row)
    {
    case 0:
    case 1:
    case 3:
        return 10;
    case 2:
        return 6;
    default:
        return 3;
    }
}

static size_t keyboard_selected_row(void)
{
    if (g_keyboard_selected < 10u) return 0;
    if (g_keyboard_selected < 20u) return 1;
    if (g_keyboard_selected < 26u) return 2;
    if (g_keyboard_selected < 36u) return 3;
    return 4;
}

static size_t keyboard_selected_column(void)
{
    size_t row = keyboard_selected_row();
    return g_keyboard_selected - keyboard_row_start(row);
}

static void keyboard_move(int dx, int dy)
{
    size_t row = keyboard_selected_row();
    size_t column = keyboard_selected_column();
    size_t count;

    if (dy < 0)
    {
        row = row == 0u ? 4u : row - 1u;
    }
    else if (dy > 0)
    {
        row = row == 4u ? 0u : row + 1u;
    }

    count = keyboard_row_count(row);
    if (dx < 0)
    {
        column = column == 0u ? count - 1u : column - 1u;
    }
    else if (dx > 0)
    {
        column = (column + 1u) % count;
    }

    if (column >= count)
    {
        column = count - 1u;
    }
    g_keyboard_selected = keyboard_row_start(row) + column;
    if (g_keyboard_selected >= g_keyboard_key_count)
    {
        g_keyboard_selected = g_keyboard_key_count - 1u;
    }
}

static void keyboard_append(const char *text)
{
    while (*text != '\0' && g_keyboard_text_length < KEYBOARD_TEXT_CAPACITY)
    {
        g_keyboard_text[g_keyboard_text_length++] = *text++;
    }
    g_keyboard_text[g_keyboard_text_length] = '\0';
}

static void keyboard_backspace(void)
{
    if (g_keyboard_text_length > 0)
    {
        g_keyboard_text[--g_keyboard_text_length] = '\0';
    }
}

static bool submit_keyboard_query(void)
{
    g_screen = PORTAL_SCREEN_LOADING;
    draw_screen();
    if (!stream_write_query(g_keyboard_text))
    {
        return false;
    }
    return load_page(g_selected_menu_item);
}

static void split_page_lines(void)
{
    size_t cursor = 0;
    g_line_count = 0;

    while (cursor < g_stream.bytes_received && g_line_count < PAGE_LINE_CAPACITY)
    {
        size_t line_start;
        size_t line_end;
        size_t segment_start;

        while (cursor < g_stream.bytes_received &&
            (g_stream_buffer[cursor] == '\r' || g_stream_buffer[cursor] == '\n'))
        {
            g_stream_buffer[cursor++] = '\0';
        }

        if (cursor >= g_stream.bytes_received)
        {
            break;
        }

        line_start = cursor;
        line_end = line_start;
        while (line_end < g_stream.bytes_received &&
            g_stream_buffer[line_end] != '\r' &&
            g_stream_buffer[line_end] != '\n')
        {
            line_end++;
        }

        segment_start = line_start;
        while (segment_start < line_end && g_line_count < PAGE_LINE_CAPACITY)
        {
            size_t remaining;
            while (segment_start < line_end && g_stream_buffer[segment_start] == ' ')
            {
                segment_start++;
            }
            if (segment_start >= line_end)
            {
                break;
            }

            g_line_offsets[g_line_count++] = (uint16_t)segment_start;
            remaining = line_end - segment_start;
            if (remaining <= PAGE_WRAP_COLUMNS)
            {
                break;
            }

            size_t wrap = segment_start + PAGE_WRAP_COLUMNS;
            size_t probe = wrap;
            while (probe > segment_start + 8u && g_stream_buffer[probe] != ' ')
            {
                probe--;
            }
            if (probe <= segment_start + 8u)
            {
                probe = wrap;
            }

            g_stream_buffer[probe] = '\0';
            segment_start = probe + 1u;
        }

        if (line_end < g_stream.bytes_received)
        {
            g_stream_buffer[line_end] = '\0';
            cursor = line_end + 1u;
        }
        else
        {
            cursor = line_end;
        }
    }

    if (g_line_count == 0)
    {
        g_line_offsets[g_line_count++] = 0;
    }
}

static bool load_page(size_t menu_index)
{
    if (menu_index >= g_menu_count)
    {
        snprintf(g_error_message, sizeof(g_error_message), "BAD_MENU_ITEM");
        return false;
    }

    g_scroll_line = 0;
    g_line_count = 0;
    if (!load_resource_to_buffer(resolve_menu_resource(menu_index)))
    {
        return false;
    }

    split_page_lines();
    g_current_menu_item = menu_index;
    return true;
}

static bool check_connection_and_menu(void)
{
    nice_net_packet_t response;

    network_pak_detect_port();
    g_network_ok = run_packet_command(NICE_NET_CMD_PING, "", 0, true, &response);
    g_bridge_ok = false;
    g_menu_ok = false;

    if (!g_network_ok)
    {
        snprintf(g_status_message, sizeof(g_status_message), "Network Pak Not Detected");
        return false;
    }

    g_bridge_ok = run_packet_command(NICE_NET_CMD_PING_BRIDGE, "", 0, true, &response);
    if (!g_bridge_ok)
    {
        snprintf(g_status_message, sizeof(g_status_message), "Bridge Not Connected");
        return false;
    }

    g_menu_ok = load_menu();
    if (!g_menu_ok)
    {
        snprintf(g_status_message, sizeof(g_status_message), "Unable To Load Menu");
        return false;
    }

    sync_clock_from_bridge();
    snprintf(g_status_message, sizeof(g_status_message), "Portal Menu Loaded");
    return true;
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

static void draw_text_limited(surface_t *display, int x, int y, uint32_t color, const char *text, size_t max_chars)
{
    char buffer[48];
    snprintf(buffer, sizeof(buffer), "%.*s", (int)max_chars, text);
    draw_text(display, x, y, color, buffer);
}

static void draw_textf(surface_t *display, int x, int y, uint32_t color, const char *format, ...)
{
    char buffer[96];
    va_list args;
    va_start(args, format);
    vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);
    draw_text(display, x, y, color, buffer);
}

static void ui_draw_panel(surface_t *display, int x, int y, int width, int height, const char *title)
{
    uint32_t shadow = color_rgb(4, 6, 18);
    uint32_t border = color_rgb(55, 70, 156);
    uint32_t bright = color_rgb(28, 190, 210);
    uint32_t fill = color_rgb(12, 18, 45);
    uint32_t header = color_rgb(19, 28, 72);

    graphics_draw_box(display, x + 2, y + 2, width, height, shadow);
    graphics_draw_box(display, x, y, width, height, border);
    graphics_draw_box(display, x + 2, y + 2, width - 4, height - 4, fill);
    graphics_draw_box(display, x + 3, y + 3, width - 6, 13, header);
    graphics_draw_line(display, x + 2, y + 17, x + width - 3, y + 17, bright);
    if (title != NULL && title[0] != '\0')
    {
        draw_text(display, x + 6, y + 5, color_rgb(140, 245, 255), title);
    }
}

static void ui_draw_footer(surface_t *display, const char *extra)
{
    ui_draw_panel(display, 10, 211, 300, 21, "");
    draw_text(display, 18, 218, color_rgb(134, 255, 84), "A");
    draw_text(display, 28, 218, color_rgb(235, 238, 245), "Open");
    draw_text(display, 69, 218, color_rgb(255, 86, 74), "B");
    draw_text(display, 79, 218, color_rgb(235, 238, 245), "Back");
    draw_text(display, 121, 218, color_rgb(254, 229, 76), "D-Pad");
    draw_text(display, 171, 218, color_rgb(235, 238, 245), "Move");
    if (extra != NULL)
    {
        draw_text(display, 217, 218, color_rgb(145, 225, 255), extra);
    }
}

static void draw_nice64_logo(surface_t *display, int x, int y, bool large)
{
    if (g_logo_sprite != NULL)
    {
        graphics_draw_sprite_trans(display, x, y, g_logo_sprite);
        return;
    }

    int step = large ? 30 : 18;
    uint32_t colors[] =
    {
        color_rgb(238, 50, 48),
        color_rgb(255, 206, 42),
        color_rgb(96, 205, 58),
        color_rgb(38, 132, 255),
        color_rgb(116, 82, 215),
        color_rgb(152, 119, 236)
    };
    const char *letters[] = { "N", "i", "C", "E", "6", "4" };

    for (int i = 0; i < 6; i++)
    {
        graphics_draw_box(display, x + (i * step) + 1, y + 2, large ? 18 : 11, large ? 18 : 9, color_rgb(8, 10, 28));
        draw_text(display, x + (i * step), y, colors[i], letters[i]);
        if (large)
        {
            draw_text(display, x + (i * step) + 1, y + 8, colors[i], letters[i]);
        }
    }
    graphics_draw_line(display, x + (large ? 158 : 98), y + 15, x + (large ? 207 : 126), y + 2, color_rgb(112, 78, 220));
    graphics_draw_line(display, x + (large ? 159 : 99), y + 16, x + (large ? 208 : 127), y + 3, color_rgb(56, 215, 232));
}

static void draw_dark_fade_overlay(surface_t *display, uint8_t level)
{
    if (level == 0u)
    {
        return;
    }

    for (int y = 0; y < SCREEN_HEIGHT; y += 4)
    {
        for (int x = 0; x < SCREEN_WIDTH; x += 4)
        {
            bool draw = false;
            if (level >= 1u && (((x + y) / 4) & 1) == 0)
            {
                draw = true;
            }
            if (level >= 2u && (((x / 4) & 1) == 0))
            {
                draw = true;
            }
            if (level >= 3u)
            {
                draw = true;
            }
            if (draw)
            {
                graphics_draw_box(display, x, y, 4, 4, color_rgb(0, 0, 0));
            }
        }
    }
}

static void draw_icon(surface_t *display, int x, int y, const char *icon)
{
    uint32_t color = color_rgb(112, 170, 255);
    const char *mark = "*";
    sprite_t *sprite = g_icon_generic;

    if (strcmp(icon, "smile") == 0) { color = color_rgb(255, 207, 46); mark = ":)"; sprite = g_icon_smile; }
    else if (strcmp(icon, "news") == 0) { color = color_rgb(238, 72, 64); mark = "N"; sprite = g_icon_news; }
    else if (strcmp(icon, "web") == 0) { color = color_rgb(67, 220, 238); mark = "@"; sprite = g_icon_web; }
    else if (strcmp(icon, "info") == 0) { color = color_rgb(58, 142, 255); mark = "i"; sprite = g_icon_info; }
    else if (strcmp(icon, "diag") == 0) { color = color_rgb(90, 220, 84); mark = "~"; sprite = g_icon_diag; }
    else if (strcmp(icon, "folder") == 0) { color = color_rgb(255, 194, 55); mark = "F"; sprite = g_icon_folder; }
    else if (strcmp(icon, "clock") == 0) { color = color_rgb(154, 104, 224); mark = "T"; sprite = g_icon_clock; }
    else if (strcmp(icon, "chat") == 0) { color = color_rgb(220, 220, 235); mark = "?"; sprite = g_icon_chat; }

    if (sprite != NULL)
    {
        graphics_draw_sprite_trans(display, x - 3, y - 3, sprite);
        return;
    }

    graphics_draw_box(display, x, y, 10, 10, color);
    graphics_draw_box(display, x + 2, y + 2, 6, 6, color_rgb(10, 14, 35));
    draw_text(display, x + 1, y + 1, color, mark);
}

static void ui_load_assets(void)
{
    if (dfs_init(DFS_DEFAULT_LOCATION) != DFS_ESUCCESS)
    {
        return;
    }

    g_logo_sprite = sprite_load("rom:/nice64_logo.sprite");
    g_splash_banner_sprite = sprite_load("rom:/splash_banner.sprite");
    g_header_banner_sprite = sprite_load("rom:/nice64_header_banner.sprite");
    g_cursor_sprite = sprite_load("rom:/selector_arrow.sprite");
    g_status_online_sprite = sprite_load("rom:/status_online.sprite");
    g_icon_smile = sprite_load("rom:/icon_welcome.sprite");
    g_icon_news = sprite_load("rom:/icon_headlines.sprite");
    g_icon_web = sprite_load("rom:/icon_web.sprite");
    g_icon_info = sprite_load("rom:/icon_about.sprite");
    g_icon_diag = sprite_load("rom:/icon_diagnostics.sprite");
    g_icon_folder = sprite_load("rom:/icon_projects.sprite");
    g_icon_clock = sprite_load("rom:/icon_time.sprite");
    g_icon_chat = sprite_load("rom:/icon_chat.sprite");
    g_icon_generic = sprite_load("rom:/icon_default.sprite");
}

static surface_t *ui_draw_frame(void)
{
    surface_t *display = display_get();
    graphics_fill_screen(display, color_rgb(6, 9, 24));
    graphics_draw_box(display, 0, 0, SCREEN_WIDTH, SCREEN_HEIGHT, color_rgb(7, 10, 28));
    graphics_draw_line(display, 0, 0, SCREEN_WIDTH - 1, 0, color_rgb(38, 62, 132));
    graphics_draw_line(display, 0, SCREEN_HEIGHT - 1, SCREEN_WIDTH - 1, SCREEN_HEIGHT - 1, color_rgb(18, 155, 180));
    return display;
}

static void end_frame(surface_t *display)
{
    display_show(display);
}

static void ui_draw_header(surface_t *display, bool show_online)
{
    graphics_draw_box(display, 10, 8, 300, 29, color_rgb(55, 70, 156));
    graphics_draw_box(display, 12, 10, 296, 25, color_rgb(12, 18, 45));
    if (g_header_banner_sprite != NULL)
    {
        graphics_draw_sprite_trans(display, 10, 10, g_header_banner_sprite);
    }
    else
    {
        draw_nice64_logo(display, 12, 13, false);
    }
    if (show_online)
    {
        graphics_draw_box(display, 220, 14, 76, 14, color_rgb(16, 42, 48));
        draw_text(display, 226, 18, color_rgb(117, 255, 76), "ONLINE");
        if (g_status_online_sprite != NULL)
        {
            graphics_draw_sprite_trans(display, 286, 17, g_status_online_sprite);
        }
        else
        {
            graphics_draw_box(display, 287, 18, 6, 6, color_rgb(75, 220, 54));
        }
    }
}

static void ui_draw_online_dot(surface_t *display, int x, int y)
{
    if (g_status_online_sprite != NULL)
    {
        graphics_draw_sprite_trans(display, x, y, g_status_online_sprite);
    }
    else
    {
        graphics_draw_box(display, x + 1, y + 1, 6, 6, color_rgb(75, 220, 54));
    }
}

static void draw_splash(void)
{
    bool flash = ((g_splash_frames / 15u) % 2u) == 0u;
    uint8_t fade_level = 0u;
    surface_t *display = ui_draw_frame();

    ui_draw_panel(display, 20, 30, 280, 176, "");
    if (g_splash_banner_sprite != NULL)
    {
        graphics_draw_sprite_trans(display, 24, 48, g_splash_banner_sprite);
    }
    else
    {
        draw_nice64_logo(display, 58, 69, true);
    }

    if (flash)
    {
        graphics_draw_box(display, 34, 144, 252, 35, color_rgb(6, 38, 52));
        graphics_draw_box(display, 36, 146, 248, 31, color_rgb(11, 23, 42));
        draw_text(display, 80, 151, color_rgb(54, 232, 245), "NETWORK INTERCONNECT");
        draw_text(display, 84, 164, color_rgb(54, 232, 245), "CONTROLLER EMULATOR");
    }
    draw_text(display, 52, 188, color_rgb(222, 226, 234), "Powered by N64 Network Pak");

    if (g_splash_frames > 105u)
    {
        fade_level = (uint8_t)((g_splash_frames - 105u) / 12u);
        if (fade_level > 3u)
        {
            fade_level = 3u;
        }
        draw_dark_fade_overlay(display, fade_level);
    }
    end_frame(display);
}

static void draw_title(void)
{
    surface_t *display = ui_draw_frame();
    ui_draw_panel(display, 30, 48, 260, 132, "NICE64 NETWORK PAK");
    draw_nice64_logo(display, 77, 83, true);
    draw_text(display, 68, 135, color_rgb(54, 232, 245), "Content Navigation Portal");
    draw_text(display, 110, 158, color_rgb(255, 255, 255), "Press Start");
    end_frame(display);
}

static void draw_connecting(void)
{
    surface_t *display = ui_draw_frame();
    ui_draw_panel(display, 28, 56, 264, 118, "CONNECTING");
    draw_nice64_logo(display, 42, 30, false);
    draw_text(display, 50, 84, color_rgb(235, 238, 245), "Checking Network Pak...");
    draw_text(display, 50, 104, color_rgb(235, 238, 245), "Checking Desktop Bridge...");
    draw_text(display, 50, 124, color_rgb(235, 238, 245), "Loading portal menu...");
    draw_text(display, 50, 151, color_rgb(140, 245, 255), "Please wait.");
    end_frame(display);
}

static void ui_draw_status(void)
{
    surface_t *display = ui_draw_frame();
    ui_draw_header(display, true);
    ui_draw_panel(display, 10, 41, 300, 163, "SYSTEM STATUS");
    if (g_network_ok && g_bridge_ok && g_menu_ok)
    {
        draw_text(display, 84, 78, color_rgb(116, 255, 90), "Network Pak: Online");
        draw_text(display, 104, 98, color_rgb(116, 255, 90), "Bridge: Online");
        draw_textf(display, 104, 118, color_rgb(116, 255, 90), "Menu: %u items", (unsigned int)g_menu_count);
        draw_text(display, 100, 138, color_rgb(235, 238, 245), g_clock.valid ? "Clock: Synced" : "Clock: Waiting");
        draw_text(display, 72, 170, color_rgb(255, 255, 255), "Press A to continue");
    }
    else
    {
        draw_text(display, 54, 86, color_rgb(255, 110, 92), g_status_message);
        draw_text(display, 54, 112, color_rgb(235, 238, 245), g_error_message);
        draw_text(display, 82, 160, color_rgb(255, 255, 255), "A Retry   B Splash");
    }
    end_frame(display);
}

static void ui_draw_menu(void)
{
    size_t start = 0;
    size_t visible = 8u;
    size_t end;
    surface_t *display;

    if (g_selected_menu_item >= visible)
    {
        start = g_selected_menu_item - visible + 1u;
    }
    end = start + visible;
    if (end > g_menu_count)
    {
        end = g_menu_count;
    }

    display = ui_draw_frame();
    ui_draw_header(display, true);

    ui_draw_panel(display, 10, 41, 198, 163, "MAIN MENU");
    ui_draw_panel(display, 214, 41, 96, 79, "TIME");
    ui_draw_panel(display, 214, 124, 96, 80, "STATUS");

    if (g_clock.valid)
    {
        draw_textf(display, 231, 65, color_rgb(255, 255, 255), "%02u:%02u:%02u", g_clock.hour, g_clock.minute, g_clock.second);
        draw_textf(display, 222, 82, color_rgb(222, 226, 234), "%04u-%02u-%02u", g_clock.year, g_clock.month, g_clock.day);
        draw_text(display, 224, 99, color_rgb(160, 210, 230), "AZ/Phoenix");
    }
    else
    {
        draw_text(display, 233, 72, color_rgb(255, 255, 255), "--:--:--");
        draw_text(display, 224, 92, color_rgb(160, 210, 230), "Syncing...");
    }

    draw_text(display, 224, 145, color_rgb(94, 255, 88), "Portal");
    draw_text(display, 268, 145, color_rgb(94, 255, 88), "On");
    ui_draw_online_dot(display, 292, 146);
    draw_text(display, 224, 160, color_rgb(94, 255, 88), "Bridge");
    draw_text(display, 268, 160, color_rgb(94, 255, 88), "On");
    ui_draw_online_dot(display, 292, 161);
    draw_text(display, 224, 176, color_rgb(235, 238, 245), "Feed");
    if (g_feed_source_index == 0u)
    {
        draw_text(display, 224, 190, color_rgb(140, 245, 255), "Retro");
        draw_text(display, 266, 190, color_rgb(140, 245, 255), "Game");
    }
    else
    {
        draw_text(display, 224, 190, color_rgb(140, 245, 255), "General");
        draw_text(display, 282, 190, color_rgb(140, 245, 255), "News");
    }

    for (size_t i = start; i < end; i++)
    {
        int y = 65 + (int)((i - start) * 15u);
        if (i == g_selected_menu_item)
        {
            graphics_draw_box(display, 18, y - 2, 181, 13, color_rgb(38, 55, 174));
            graphics_draw_line(display, 18, y - 2, 199, y - 2, color_rgb(77, 120, 255));
            if (g_cursor_sprite != NULL)
            {
                graphics_draw_sprite_trans(display, 21, y - 2, g_cursor_sprite);
            }
            else
            {
                draw_text(display, 22, y, color_rgb(255, 230, 64), ">");
            }
        }
        draw_icon(display, 36, y - 1, g_menu_items[i].icon);
        draw_textf(display, 52, y, color_rgb(255, 255, 255), "%u.", (unsigned int)(i + 1u));
        draw_text_limited(display, 74, y, color_rgb(235, 238, 245), g_menu_items[i].title, 15u);
    }

    ui_draw_footer(display, "C-L/R Feed");
    end_frame(display);
}

static void draw_loading(void)
{
    surface_t *display = ui_draw_frame();
    ui_draw_panel(display, 30, 67, 260, 98, "LOADING");
    draw_text(display, 54, 96, color_rgb(235, 238, 245), "Opening");
    draw_text(display, 54, 112, color_rgb(255, 255, 255), g_menu_items[g_selected_menu_item].title);
    draw_text(display, 54, 137, color_rgb(140, 245, 255), "Reading stream from bridge...");
    end_frame(display);
}

static void draw_keyboard(void)
{
    surface_t *display = ui_draw_frame();
    ui_draw_header(display, true);
    ui_draw_panel(display, 10, 41, 300, 164, "ASK NICE64");
    draw_text(display, 22, 64, color_rgb(140, 245, 255), "QUESTION");
    graphics_draw_box(display, 20, 78, 280, 24, color_rgb(8, 12, 30));
    graphics_draw_line(display, 22, 101, 298, 101, color_rgb(28, 190, 210));
    draw_text_limited(display, 26, 86, color_rgb(235, 238, 245), g_keyboard_text, 31u);
    if (((g_splash_frames / 20u) & 1u) == 0u && g_keyboard_text_length < 31u)
    {
        draw_text(display, 26 + (int)(g_keyboard_text_length * 8u), 86, color_rgb(255, 230, 64), "_");
    }

    for (size_t row = 0; row < 5u; row++)
    {
        size_t start = keyboard_row_start(row);
        size_t count = keyboard_row_count(row);
        int y = 112 + (int)(row * 17u);
        int x_start = row == 2u ? 68 : (row == 4u ? 47 : 26);
        for (size_t column = 0; column < count; column++)
        {
            size_t index = start + column;
            const char *key = g_keyboard_keys[index];
            int width = row == 4u ? (column == 0u ? 58 : 74) : 20;
            int x = x_start + (int)(column * (row == 4u ? 78u : 25u));
            bool selected = index == g_keyboard_selected;
            graphics_draw_box(display, x, y, width, 13, selected ? color_rgb(38, 55, 174) : color_rgb(12, 18, 45));
            graphics_draw_box(display, x, y, width, 1, color_rgb(55, 70, 156));
            draw_text(display, x + 4, y + 3, selected ? color_rgb(255, 255, 255) : color_rgb(235, 238, 245), key);
        }
    }

    ui_draw_footer(display, "A Key  B Cancel");
    end_frame(display);
}

static void draw_page(void)
{
    size_t end_line;
    surface_t *display = ui_draw_frame();

    ui_draw_header(display, true);

    ui_draw_panel(display, 10, 41, 300, 164, g_menu_items[g_current_menu_item].title);

    end_line = g_scroll_line + 13u;
    if (end_line > g_line_count)
    {
        end_line = g_line_count;
    }

    for (size_t i = g_scroll_line; i < end_line; i++)
    {
        char line[40];
        snprintf(line, sizeof(line), "%s", (const char *)&g_stream_buffer[g_line_offsets[i]]);
        draw_text(display, 20, 66 + (int)((i - g_scroll_line) * 10u), color_rgb(235, 238, 245), line);
    }

    draw_textf(display, 20, 190, color_rgb(120, 190, 220), "Stream %u  Bytes %lu  CRC %08lX",
        g_stream.id,
        (unsigned long)g_stream.bytes_received,
        (unsigned long)g_stream.expected_crc32);
    ui_draw_footer(display, is_ollama_menu_item(g_current_menu_item) ? "A Refresh" : "Scroll");
    end_frame(display);
}

static void draw_error(void)
{
    surface_t *display = ui_draw_frame();
    ui_draw_panel(display, 30, 59, 260, 116, "ERROR");
    draw_text(display, 55, 88, color_rgb(255, 110, 92), "Unable To Open Page");
    draw_text(display, 55, 111, color_rgb(235, 238, 245), g_error_message);
    if (g_selected_menu_item < g_menu_count)
    {
        draw_text(display, 55, 134, color_rgb(140, 245, 255), resolve_menu_resource(g_selected_menu_item));
    }
    draw_text(display, 55, 157, color_rgb(255, 255, 255), "A Retry   B Menu");
    end_frame(display);
}

static void draw_screen(void)
{
    switch (g_screen)
    {
    case PORTAL_SCREEN_SPLASH: draw_splash(); break;
    case PORTAL_SCREEN_TITLE: draw_title(); break;
    case PORTAL_SCREEN_CONNECTING: draw_connecting(); break;
    case PORTAL_SCREEN_STATUS: ui_draw_status(); break;
    case PORTAL_SCREEN_MENU: ui_draw_menu(); break;
    case PORTAL_SCREEN_KEYBOARD: draw_keyboard(); break;
    case PORTAL_SCREEN_LOADING: draw_loading(); break;
    case PORTAL_SCREEN_PAGE: draw_page(); break;
    case PORTAL_SCREEN_ERROR: draw_error(); break;
    }
}

static void open_selected_page(void)
{
    if (is_ollama_menu_item(g_selected_menu_item))
    {
        reset_keyboard();
        g_screen = PORTAL_SCREEN_KEYBOARD;
        return;
    }

    g_screen = PORTAL_SCREEN_LOADING;
    draw_screen();
    if (load_page(g_selected_menu_item))
    {
        g_screen = PORTAL_SCREEN_PAGE;
    }
    else
    {
        if (strcmp(g_error_message, "TRANSPORT_TIMEOUT") == 0)
        {
            snprintf(g_error_message, sizeof(g_error_message), "Bridge Disconnected");
        }
        g_screen = PORTAL_SCREEN_ERROR;
    }
}

int main(void)
{
    timer_init();
    joypad_init();
    debug_init_emulog();
    display_init(RESOLUTION_320x240, DEPTH_16_BPP, 2, GAMMA_NONE, FILTERS_RESAMPLE);
    graphics_set_default_font();
    ui_load_assets();
    network_pak_init();
    network_pak_set_stub_fallback_enabled(false);
    network_pak_set_max_read_retries(0);

    while (1)
    {
        joypad_poll();
        joypad_buttons_t pressed = joypad_get_buttons_pressed(JOYPAD_PORT_1);
        joypad_inputs_t inputs = joypad_get_inputs(JOYPAD_PORT_1);
        tick_portal_clock();

        switch (g_screen)
        {
        case PORTAL_SCREEN_SPLASH:
            g_splash_frames++;
            if (pressed.start || g_splash_frames >= SPLASH_FRAME_COUNT)
            {
                g_screen = PORTAL_SCREEN_CONNECTING;
                draw_screen();
                check_connection_and_menu();
                g_screen = PORTAL_SCREEN_STATUS;
            }
            break;

        case PORTAL_SCREEN_TITLE:
            if (pressed.start)
            {
                g_screen = PORTAL_SCREEN_CONNECTING;
                draw_screen();
                check_connection_and_menu();
                g_screen = PORTAL_SCREEN_STATUS;
            }
            break;

        case PORTAL_SCREEN_STATUS:
            if (pressed.a)
            {
                if (g_network_ok && g_bridge_ok && g_menu_ok)
                {
                    g_screen = PORTAL_SCREEN_MENU;
                }
                else
                {
                    g_screen = PORTAL_SCREEN_CONNECTING;
                    draw_screen();
                    check_connection_and_menu();
                    g_screen = PORTAL_SCREEN_STATUS;
                }
            }
            else if (pressed.b)
            {
                g_screen = PORTAL_SCREEN_SPLASH;
                g_splash_frames = 0;
            }
            break;

        case PORTAL_SCREEN_MENU:
            if (pressed.d_up && g_menu_count > 0)
            {
                g_selected_menu_item = g_selected_menu_item == 0 ? g_menu_count - 1u : g_selected_menu_item - 1u;
            }
            else if (pressed.d_down && g_menu_count > 0)
            {
                g_selected_menu_item = (g_selected_menu_item + 1u) % g_menu_count;
            }
            else if (pressed.a && g_menu_count > 0)
            {
                open_selected_page();
            }
            else if (pressed.z && is_ollama_menu_item(g_selected_menu_item))
            {
                reset_keyboard();
                g_screen = PORTAL_SCREEN_KEYBOARD;
            }
            else if (pressed.c_left || pressed.c_right)
            {
                g_feed_source_index = (g_feed_source_index + 1u) %
                    (sizeof(g_feed_sources) / sizeof(g_feed_sources[0]));
            }
            else if (pressed.b)
            {
                g_screen = PORTAL_SCREEN_SPLASH;
                g_splash_frames = 0;
            }
            break;

        case PORTAL_SCREEN_KEYBOARD:
            if (g_keyboard_move_cooldown > 0)
            {
                g_keyboard_move_cooldown--;
            }
            if (pressed.d_left || (inputs.stick_x < -45 && g_keyboard_move_cooldown == 0))
            {
                keyboard_move(-1, 0);
                g_keyboard_move_cooldown = 8;
            }
            else if (pressed.d_right || (inputs.stick_x > 45 && g_keyboard_move_cooldown == 0))
            {
                keyboard_move(1, 0);
                g_keyboard_move_cooldown = 8;
            }
            else if (pressed.d_up || (inputs.stick_y > 45 && g_keyboard_move_cooldown == 0))
            {
                keyboard_move(0, -1);
                g_keyboard_move_cooldown = 8;
            }
            else if (pressed.d_down || (inputs.stick_y < -45 && g_keyboard_move_cooldown == 0))
            {
                keyboard_move(0, 1);
                g_keyboard_move_cooldown = 8;
            }
            else if (pressed.a)
            {
                const char *key = g_keyboard_keys[g_keyboard_selected];
                if (strcmp(key, "SPACE") == 0)
                {
                    keyboard_append(" ");
                }
                else if (strcmp(key, "<-") == 0)
                {
                    keyboard_backspace();
                }
                else if (strcmp(key, "SEND") == 0)
                {
                    if (submit_keyboard_query())
                    {
                        g_current_menu_item = g_selected_menu_item;
                        g_screen = PORTAL_SCREEN_PAGE;
                    }
                    else
                    {
                        g_screen = PORTAL_SCREEN_ERROR;
                    }
                }
                else
                {
                    keyboard_append(key);
                }
            }
            else if (pressed.b)
            {
                g_screen = PORTAL_SCREEN_MENU;
            }
            break;

        case PORTAL_SCREEN_PAGE:
            if (pressed.a && is_ollama_menu_item(g_current_menu_item))
            {
                g_screen = PORTAL_SCREEN_LOADING;
                draw_screen();
                if (load_page(g_current_menu_item))
                {
                    g_screen = PORTAL_SCREEN_PAGE;
                }
                else
                {
                    g_screen = PORTAL_SCREEN_ERROR;
                }
            }
            else if (pressed.d_up && g_scroll_line > 0)
            {
                g_scroll_line--;
            }
            else if (pressed.d_down && g_scroll_line + PAGE_VISIBLE_LINES < g_line_count)
            {
                g_scroll_line++;
            }
            else if (pressed.b)
            {
                g_screen = PORTAL_SCREEN_MENU;
            }
            break;

        case PORTAL_SCREEN_ERROR:
            if (pressed.a)
            {
                open_selected_page();
            }
            else if (pressed.b)
            {
                g_screen = PORTAL_SCREEN_MENU;
            }
            break;

        case PORTAL_SCREEN_CONNECTING:
        case PORTAL_SCREEN_LOADING:
            break;
        }

        draw_screen();
        wait_ms(16);
    }
}
