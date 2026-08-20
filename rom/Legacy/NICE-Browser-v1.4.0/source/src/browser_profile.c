#include "browser_profile.h"
#include "browser_profile_storage.h"

#include <stdio.h>
#include <string.h>

#define BROWSER_PROFILE_MAGIC 0x4E504631u
#define BROWSER_PROFILE_VERSION 1u
#define BROWSER_PROFILE_STRING_POOL_BYTES 28000u
#define BROWSER_PROFILE_SAVE_BATCH 8u

typedef struct browser_profile_header_s
{
    uint32_t magic;
    uint32_t version;
    uint32_t generation;
    uint32_t payload_length;
    uint32_t crc32;
    uint32_t reserved[3];
} BrowserProfileHeader;

typedef struct browser_profile_favorite_s
{
    uint16_t title_offset;
    uint16_t url_offset;
    uint16_t folder_id;
    uint16_t flags;
    uint32_t created;
    uint32_t last_used;
} BrowserProfileFavorite;

typedef struct browser_profile_folder_s
{
    uint16_t name_offset;
    uint16_t folder_id;
    uint16_t flags;
    uint16_t reserved;
} BrowserProfileFolder;

typedef struct browser_profile_history_s
{
    uint16_t title_offset;
    uint16_t url_offset;
    uint32_t visited;
} BrowserProfileHistory;

typedef struct browser_profile_payload_s
{
    uint16_t homepage_offset;
    uint16_t image_preference;
    uint16_t string_used;
    uint16_t reserved0;
    uint32_t flags;
    uint32_t logical_time;
    BrowserProfileFavorite favorites[BROWSER_PROFILE_FAVORITE_LIMIT];
    BrowserProfileFolder folders[BROWSER_PROFILE_FOLDER_LIMIT];
    BrowserProfileHistory history[BROWSER_PROFILE_HISTORY_LIMIT];
    uint16_t recent_offsets[BROWSER_PROFILE_RECENT_LIMIT];
    uint8_t reserved[1024];
    char string_pool[BROWSER_PROFILE_STRING_POOL_BYTES];
} BrowserProfilePayload;

typedef struct browser_profile_image_s
{
    BrowserProfileHeader header;
    BrowserProfilePayload payload;
} BrowserProfileImage;

static BrowserProfileImage g_profile __attribute__((aligned(8)));
static char g_compact_pool[BROWSER_PROFILE_STRING_POOL_BYTES];
static bool g_loaded;
static bool g_dirty;
static uint8_t g_pending_changes;
static char g_last_error[64] = "Not initialized";

static uint32_t profile_crc32(const void *data, uint32_t length)
{
    const uint8_t *bytes = data;
    uint32_t crc = 0xFFFFFFFFu;
    uint32_t i;
    while (length-- > 0u)
    {
        crc ^= *bytes++;
        for (i = 0u; i < 8u; i++) crc = (crc >> 1u) ^ ((crc & 1u) ? 0xEDB88320u : 0u);
    }
    return ~crc;
}

static const char *profile_string(uint16_t offset)
{
    if (offset == 0u || offset >= g_profile.payload.string_used || offset >= BROWSER_PROFILE_STRING_POOL_BYTES) return "";
    return &g_profile.payload.string_pool[offset];
}

static bool profile_string_valid(uint16_t offset)
{
    uint16_t cursor;
    if (offset == 0u) return true;
    if (offset >= g_profile.payload.string_used || offset >= BROWSER_PROFILE_STRING_POOL_BYTES) return false;
    for (cursor = offset; cursor < g_profile.payload.string_used; cursor++)
    {
        if (g_profile.payload.string_pool[cursor] == '\0') return true;
    }
    return false;
}

static void profile_mark_dirty(void)
{
    g_dirty = true;
    if (g_pending_changes < 0xFFu) g_pending_changes++;
}

static bool profile_copy_string(uint16_t *offset, uint16_t *used, const char *value)
{
    size_t length;
    if (value == NULL || value[0] == '\0')
    {
        *offset = 0u;
        return true;
    }
    length = strlen(value) + 1u;
    if (length > 1024u || *used > BROWSER_PROFILE_STRING_POOL_BYTES || length > BROWSER_PROFILE_STRING_POOL_BYTES - *used)
    {
        return false;
    }
    *offset = *used;
    memcpy(&g_compact_pool[*used], value, length);
    *used = (uint16_t)(*used + length);
    return true;
}

static bool profile_compact_strings(void)
{
    uint16_t used = 1u;
    uint32_t i;
    memset(g_compact_pool, 0, sizeof(g_compact_pool));
    if (!profile_copy_string(&g_profile.payload.homepage_offset, &used, profile_string(g_profile.payload.homepage_offset))) return false;
    for (i = 0u; i < BROWSER_PROFILE_FAVORITE_LIMIT; i++)
    {
        BrowserProfileFavorite *record = &g_profile.payload.favorites[i];
        if (record->url_offset == 0u) continue;
        if (!profile_copy_string(&record->title_offset, &used, profile_string(record->title_offset)) ||
            !profile_copy_string(&record->url_offset, &used, profile_string(record->url_offset))) return false;
    }
    for (i = 0u; i < BROWSER_PROFILE_FOLDER_LIMIT; i++)
    {
        BrowserProfileFolder *record = &g_profile.payload.folders[i];
        if (record->name_offset != 0u && !profile_copy_string(&record->name_offset, &used, profile_string(record->name_offset))) return false;
    }
    for (i = 0u; i < BROWSER_PROFILE_HISTORY_LIMIT; i++)
    {
        BrowserProfileHistory *record = &g_profile.payload.history[i];
        if (record->url_offset == 0u) continue;
        if (!profile_copy_string(&record->title_offset, &used, profile_string(record->title_offset)) ||
            !profile_copy_string(&record->url_offset, &used, profile_string(record->url_offset))) return false;
    }
    for (i = 0u; i < BROWSER_PROFILE_RECENT_LIMIT; i++)
    {
        if (g_profile.payload.recent_offsets[i] != 0u &&
            !profile_copy_string(&g_profile.payload.recent_offsets[i], &used, profile_string(g_profile.payload.recent_offsets[i]))) return false;
    }
    memcpy(g_profile.payload.string_pool, g_compact_pool, sizeof(g_compact_pool));
    g_profile.payload.string_used = used;
    return true;
}

static bool profile_store_string(uint16_t *offset, const char *value)
{
    size_t length;
    if (value == NULL || value[0] == '\0') { *offset = 0u; return true; }
    length = strlen(value) + 1u;
    if (length > 1024u) { snprintf(g_last_error, sizeof(g_last_error), "String too long"); return false; }
    if (g_profile.payload.string_used + length > BROWSER_PROFILE_STRING_POOL_BYTES && !profile_compact_strings())
    {
        snprintf(g_last_error, sizeof(g_last_error), "String pool full");
        return false;
    }
    if (g_profile.payload.string_used + length > BROWSER_PROFILE_STRING_POOL_BYTES)
    {
        snprintf(g_last_error, sizeof(g_last_error), "String pool full");
        return false;
    }
    *offset = g_profile.payload.string_used;
    memcpy(&g_profile.payload.string_pool[*offset], value, length);
    g_profile.payload.string_used = (uint16_t)(g_profile.payload.string_used + length);
    return true;
}

static void profile_create_fresh(void)
{
    memset(&g_profile, 0, sizeof(g_profile));
    g_profile.header.magic = BROWSER_PROFILE_MAGIC;
    g_profile.header.version = BROWSER_PROFILE_VERSION;
    g_profile.header.generation = 1u;
    g_profile.header.payload_length = sizeof(g_profile.payload);
    g_profile.payload.string_used = 1u;
    g_profile.payload.string_pool[0] = '\0';
    profile_store_string(&g_profile.payload.homepage_offset, "nice://home");
    g_loaded = true;
    g_dirty = true;
    g_pending_changes = 0u;
    snprintf(g_last_error, sizeof(g_last_error), "Fresh profile");
}

static bool profile_validate(void)
{
    uint32_t i;
    if (g_profile.header.magic != BROWSER_PROFILE_MAGIC || g_profile.header.version != BROWSER_PROFILE_VERSION ||
        g_profile.header.payload_length != sizeof(g_profile.payload) ||
        g_profile.header.crc32 != profile_crc32(&g_profile.payload, sizeof(g_profile.payload)) ||
        g_profile.payload.string_used == 0u || g_profile.payload.string_used > BROWSER_PROFILE_STRING_POOL_BYTES ||
        !profile_string_valid(g_profile.payload.homepage_offset)) return false;
    for (i = 0u; i < BROWSER_PROFILE_FAVORITE_LIMIT; i++)
    {
        if (!profile_string_valid(g_profile.payload.favorites[i].title_offset) || !profile_string_valid(g_profile.payload.favorites[i].url_offset)) return false;
    }
    for (i = 0u; i < BROWSER_PROFILE_HISTORY_LIMIT; i++)
    {
        if (!profile_string_valid(g_profile.payload.history[i].title_offset) || !profile_string_valid(g_profile.payload.history[i].url_offset)) return false;
    }
    return true;
}

void BrowserProfile_Init(void)
{
    if (!BrowserProfileStorage_Init() || !BrowserProfileStorage_Read(0u, &g_profile, sizeof(g_profile)) || !profile_validate())
    {
        profile_create_fresh();
        return;
    }
    g_loaded = true;
    g_dirty = false;
    g_pending_changes = 0u;
    snprintf(g_last_error, sizeof(g_last_error), "Loaded generation %lu", (unsigned long)g_profile.header.generation);
}

bool BrowserProfile_Loaded(void) { return g_loaded; }
bool BrowserProfile_IsDirty(void) { return g_dirty; }
const char *BrowserProfile_LastError(void) { return g_last_error; }
const char *BrowserProfile_Homepage(void) { return profile_string(g_profile.payload.homepage_offset); }

bool BrowserProfile_SaveNow(void)
{
    if (!g_loaded || !g_dirty) return true;
    if (!profile_compact_strings()) { snprintf(g_last_error, sizeof(g_last_error), "String pool compact failed"); return false; }
    g_profile.header.generation++;
    g_profile.header.payload_length = sizeof(g_profile.payload);
    g_profile.header.crc32 = profile_crc32(&g_profile.payload, sizeof(g_profile.payload));
    if (!BrowserProfileStorage_Write(0u, &g_profile, sizeof(g_profile)) ||
        !BrowserProfileStorage_Verify(0u, &g_profile, sizeof(g_profile)))
    {
        snprintf(g_last_error, sizeof(g_last_error), "SRAM save failed");
        return false;
    }
    g_dirty = false;
    g_pending_changes = 0u;
    snprintf(g_last_error, sizeof(g_last_error), "Saved generation %lu", (unsigned long)g_profile.header.generation);
    return true;
}

void BrowserProfile_SaveIfNeeded(void)
{
    if (g_dirty && g_pending_changes >= BROWSER_PROFILE_SAVE_BATCH) BrowserProfile_SaveNow();
}

bool BrowserProfile_SetHomepage(const char *url)
{
    if (!profile_store_string(&g_profile.payload.homepage_offset, url)) return false;
    profile_mark_dirty();
    return BrowserProfile_SaveNow();
}

bool BrowserProfile_SetImagePreference(uint16_t preference)
{
    g_profile.payload.image_preference = preference;
    profile_mark_dirty();
    return BrowserProfile_SaveNow();
}

void BrowserProfile_RecordVisit(const char *title, const char *canonical_url)
{
    BrowserProfileHistory entry;
    if (canonical_url == NULL || canonical_url[0] == '\0') return;
    memset(&entry, 0, sizeof(entry));
    if (!profile_store_string(&entry.title_offset, title) || !profile_store_string(&entry.url_offset, canonical_url)) return;
    entry.visited = ++g_profile.payload.logical_time;
    memmove(&g_profile.payload.history[1], &g_profile.payload.history[0], sizeof(g_profile.payload.history) - sizeof(entry));
    g_profile.payload.history[0] = entry;
    profile_mark_dirty();
}

void BrowserProfile_RecordRecentAddress(const char *canonical_url)
{
    uint16_t offset;
    uint32_t i;
    if (canonical_url == NULL || canonical_url[0] == '\0') return;
    for (i = 0u; i < BROWSER_PROFILE_RECENT_LIMIT; i++)
    {
        if (g_profile.payload.recent_offsets[i] != 0u && strcmp(profile_string(g_profile.payload.recent_offsets[i]), canonical_url) == 0)
        {
            offset = g_profile.payload.recent_offsets[i];
            memmove(&g_profile.payload.recent_offsets[1], &g_profile.payload.recent_offsets[0], sizeof(uint16_t) * i);
            g_profile.payload.recent_offsets[0] = offset;
            profile_mark_dirty();
            return;
        }
    }
    if (!profile_store_string(&offset, canonical_url)) return;
    memmove(&g_profile.payload.recent_offsets[1], &g_profile.payload.recent_offsets[0], sizeof(g_profile.payload.recent_offsets) - sizeof(offset));
    g_profile.payload.recent_offsets[0] = offset;
    profile_mark_dirty();
}

bool BrowserProfile_ClearHistory(void)
{
    memset(g_profile.payload.history, 0, sizeof(g_profile.payload.history));
    profile_mark_dirty();
    return BrowserProfile_SaveNow();
}

bool BrowserProfile_ClearRecentAddresses(void)
{
    memset(g_profile.payload.recent_offsets, 0, sizeof(g_profile.payload.recent_offsets));
    profile_mark_dirty();
    return BrowserProfile_SaveNow();
}

bool BrowserProfile_AddFavorite(const char *title, const char *canonical_url, uint16_t folder_id, uint16_t flags)
{
    uint32_t i;
    if (canonical_url == NULL || canonical_url[0] == '\0') { snprintf(g_last_error, sizeof(g_last_error), "No canonical URL"); return false; }
    for (i = 0u; i < BROWSER_PROFILE_FAVORITE_LIMIT; i++)
    {
        BrowserProfileFavorite *entry = &g_profile.payload.favorites[i];
        if (entry->url_offset != 0u && strcmp(profile_string(entry->url_offset), canonical_url) == 0)
        {
            entry->last_used = ++g_profile.payload.logical_time;
            profile_mark_dirty();
            return BrowserProfile_SaveNow();
        }
        if (entry->url_offset == 0u)
        {
            memset(entry, 0, sizeof(*entry));
            if (!profile_store_string(&entry->title_offset, title) || !profile_store_string(&entry->url_offset, canonical_url)) return false;
            entry->folder_id = folder_id; entry->flags = flags;
            entry->created = entry->last_used = ++g_profile.payload.logical_time;
            profile_mark_dirty();
            return BrowserProfile_SaveNow();
        }
    }
    snprintf(g_last_error, sizeof(g_last_error), "Favorites full");
    return false;
}

bool BrowserProfile_RemoveFavorite(uint16_t index)
{
    if (index >= BROWSER_PROFILE_FAVORITE_LIMIT) return false;
    memset(&g_profile.payload.favorites[index], 0, sizeof(g_profile.payload.favorites[index]));
    profile_mark_dirty();
    return BrowserProfile_SaveNow();
}

bool BrowserProfile_ToggleFavoriteHomePin(uint16_t index, bool *pinned_out)
{
    BrowserProfileFavorite *entry;

    if (index >= BROWSER_PROFILE_FAVORITE_LIMIT || g_profile.payload.favorites[index].url_offset == 0u)
    {
        snprintf(g_last_error, sizeof(g_last_error), "Favorite unavailable");
        return false;
    }

    entry = &g_profile.payload.favorites[index];
    entry->flags ^= BROWSER_PROFILE_FAVORITE_FLAG_HOME_PINNED;
    entry->last_used = ++g_profile.payload.logical_time;
    if (pinned_out != NULL)
    {
        *pinned_out = (entry->flags & BROWSER_PROFILE_FAVORITE_FLAG_HOME_PINNED) != 0u;
    }
    profile_mark_dirty();
    return BrowserProfile_SaveNow();
}

bool BrowserProfile_AddFolder(const char *name, uint16_t folder_id, uint16_t flags)
{
    uint32_t i;
    for (i = 0u; i < BROWSER_PROFILE_FOLDER_LIMIT; i++)
    {
        BrowserProfileFolder *entry = &g_profile.payload.folders[i];
        if (entry->name_offset == 0u)
        {
            if (!profile_store_string(&entry->name_offset, name)) return false;
            entry->folder_id = folder_id; entry->flags = flags;
            profile_mark_dirty();
            return BrowserProfile_SaveNow();
        }
    }
    snprintf(g_last_error, sizeof(g_last_error), "Folders full");
    return false;
}

bool BrowserProfile_HasFavoriteUrl(const char *canonical_url)
{
    uint32_t i;
    if (canonical_url == NULL) return false;
    for (i = 0u; i < BROWSER_PROFILE_FAVORITE_LIMIT; i++)
    {
        if (g_profile.payload.favorites[i].url_offset != 0u && strcmp(profile_string(g_profile.payload.favorites[i].url_offset), canonical_url) == 0) return true;
    }
    return false;
}

bool BrowserProfile_GetFavorite(uint16_t index, BrowserProfileFavoriteView *out)
{
    if (out == NULL || index >= BROWSER_PROFILE_FAVORITE_LIMIT || g_profile.payload.favorites[index].url_offset == 0u) return false;
    out->title = profile_string(g_profile.payload.favorites[index].title_offset);
    out->url = profile_string(g_profile.payload.favorites[index].url_offset);
    out->folder_id = g_profile.payload.favorites[index].folder_id;
    out->flags = g_profile.payload.favorites[index].flags;
    return true;
}

bool BrowserProfile_GetHistory(uint16_t index, BrowserProfileHistoryView *out)
{
    if (out == NULL || index >= BROWSER_PROFILE_HISTORY_LIMIT || g_profile.payload.history[index].url_offset == 0u) return false;
    out->title = profile_string(g_profile.payload.history[index].title_offset);
    out->url = profile_string(g_profile.payload.history[index].url_offset);
    out->visited = g_profile.payload.history[index].visited;
    return true;
}

const char *BrowserProfile_GetRecentAddress(uint16_t index)
{
    if (index >= BROWSER_PROFILE_RECENT_LIMIT) return "";
    return profile_string(g_profile.payload.recent_offsets[index]);
}

uint16_t BrowserProfile_FavoriteCount(void)
{
    uint16_t count = 0u;
    uint32_t i;
    for (i = 0u; i < BROWSER_PROFILE_FAVORITE_LIMIT; i++) if (g_profile.payload.favorites[i].url_offset != 0u) count++;
    return count;
}

uint16_t BrowserProfile_HistoryCount(void)
{
    uint16_t count = 0u;
    uint32_t i;
    for (i = 0u; i < BROWSER_PROFILE_HISTORY_LIMIT; i++) if (g_profile.payload.history[i].url_offset != 0u) count++;
    return count;
}

uint16_t BrowserProfile_RecentCount(void)
{
    uint16_t count = 0u;
    uint32_t i;
    for (i = 0u; i < BROWSER_PROFILE_RECENT_LIMIT; i++) if (g_profile.payload.recent_offsets[i] != 0u) count++;
    return count;
}
