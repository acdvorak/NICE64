#ifndef BROWSER_PROFILE_STORAGE_H
#define BROWSER_PROFILE_STORAGE_H

#include <stdbool.h>
#include <stdint.h>

#define BROWSER_PROFILE_STORAGE_CAPACITY_BYTES 32768u
#define BROWSER_PROFILE_STORAGE_ALIGNMENT 4u

bool BrowserProfileStorage_Init(void);
bool BrowserProfileStorage_Read(uint32_t offset, void *buffer, uint32_t length);
bool BrowserProfileStorage_Erase(uint32_t offset, uint32_t length);
bool BrowserProfileStorage_Write(uint32_t offset, const void *buffer, uint32_t length);
bool BrowserProfileStorage_Verify(uint32_t offset, const void *buffer, uint32_t length);
const char *BrowserProfileStorage_LastError(void);

#endif
