#ifndef BROWSER_PROFILE_H
#define BROWSER_PROFILE_H

#include <stdbool.h>
#include <stdint.h>

#define BROWSER_PROFILE_FAVORITE_LIMIT 64u
#define BROWSER_PROFILE_FOLDER_LIMIT 16u
#define BROWSER_PROFILE_HISTORY_LIMIT 128u
#define BROWSER_PROFILE_RECENT_LIMIT 32u
#define BROWSER_PROFILE_FAVORITE_FLAG_HOME_PINNED 0x0001u

typedef struct browser_profile_favorite_view_s
{
    const char *title;
    const char *url;
    uint16_t folder_id;
    uint16_t flags;
} BrowserProfileFavoriteView;

typedef struct browser_profile_history_view_s
{
    const char *title;
    const char *url;
    uint32_t visited;
} BrowserProfileHistoryView;

void BrowserProfile_Init(void);
bool BrowserProfile_Loaded(void);
bool BrowserProfile_IsDirty(void);
bool BrowserProfile_SaveNow(void);
void BrowserProfile_SaveIfNeeded(void);
const char *BrowserProfile_LastError(void);

const char *BrowserProfile_Homepage(void);
bool BrowserProfile_SetHomepage(const char *url);
bool BrowserProfile_SetImagePreference(uint16_t preference);
void BrowserProfile_RecordVisit(const char *title, const char *canonical_url);
void BrowserProfile_RecordRecentAddress(const char *canonical_url);
bool BrowserProfile_ClearHistory(void);
bool BrowserProfile_ClearRecentAddresses(void);
bool BrowserProfile_AddFavorite(const char *title, const char *canonical_url, uint16_t folder_id, uint16_t flags);
bool BrowserProfile_RemoveFavorite(uint16_t index);
bool BrowserProfile_ToggleFavoriteHomePin(uint16_t index, bool *pinned_out);
bool BrowserProfile_AddFolder(const char *name, uint16_t folder_id, uint16_t flags);
bool BrowserProfile_HasFavoriteUrl(const char *canonical_url);
bool BrowserProfile_GetFavorite(uint16_t index, BrowserProfileFavoriteView *out);
bool BrowserProfile_GetHistory(uint16_t index, BrowserProfileHistoryView *out);
const char *BrowserProfile_GetRecentAddress(uint16_t index);
uint16_t BrowserProfile_FavoriteCount(void);
uint16_t BrowserProfile_HistoryCount(void);
uint16_t BrowserProfile_RecentCount(void);

#endif
