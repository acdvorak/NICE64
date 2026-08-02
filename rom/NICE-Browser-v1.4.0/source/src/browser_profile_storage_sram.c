#include "browser_profile_storage.h"

#include <libdragon.h>
#include <stdio.h>
#include <string.h>

#define SRAM_PI_BASE 0x08000000u

static char g_last_error[64] = "Not initialized";

static bool storage_range_valid(uint32_t offset, uint32_t length)
{
    if (length == 0u || (offset % BROWSER_PROFILE_STORAGE_ALIGNMENT) != 0u ||
        (length % BROWSER_PROFILE_STORAGE_ALIGNMENT) != 0u ||
        offset > BROWSER_PROFILE_STORAGE_CAPACITY_BYTES ||
        length > BROWSER_PROFILE_STORAGE_CAPACITY_BYTES - offset)
    {
        snprintf(g_last_error, sizeof(g_last_error), "SRAM bounds/alignment");
        return false;
    }
    return true;
}

bool BrowserProfileStorage_Init(void)
{
    if (!io_accessible(SRAM_PI_BASE))
    {
        snprintf(g_last_error, sizeof(g_last_error), "SRAM PI unavailable");
        return false;
    }
    snprintf(g_last_error, sizeof(g_last_error), "OK");
    return true;
}

bool BrowserProfileStorage_Read(uint32_t offset, void *buffer, uint32_t length)
{
    uint32_t cursor;
    if (buffer == NULL || !storage_range_valid(offset, length)) return false;
    for (cursor = 0u; cursor < length; cursor += 4u)
    {
        uint32_t value = io_read(SRAM_PI_BASE + offset + cursor);
        memcpy((uint8_t *)buffer + cursor, &value, sizeof(value));
    }
    return true;
}

bool BrowserProfileStorage_Write(uint32_t offset, const void *buffer, uint32_t length)
{
    uint32_t cursor;
    if (buffer == NULL || !storage_range_valid(offset, length)) return false;
    for (cursor = 0u; cursor < length; cursor += 4u)
    {
        uint32_t value;
        memcpy(&value, (const uint8_t *)buffer + cursor, sizeof(value));
        io_write(SRAM_PI_BASE + offset + cursor, value);
        dma_wait();
    }
    return true;
}

bool BrowserProfileStorage_Erase(uint32_t offset, uint32_t length)
{
    static uint32_t erased_words[64] __attribute__((aligned(8)));
    uint32_t cursor;
    if (!storage_range_valid(offset, length)) return false;
    for (cursor = 0u; cursor < sizeof(erased_words) / sizeof(erased_words[0]); cursor++) erased_words[cursor] = 0xFFFFFFFFu;
    for (cursor = 0u; cursor < length; cursor += sizeof(erased_words))
    {
        uint32_t chunk = length - cursor;
        if (chunk > sizeof(erased_words)) chunk = sizeof(erased_words);
        if (!BrowserProfileStorage_Write(offset + cursor, erased_words, chunk)) return false;
    }
    return true;
}

bool BrowserProfileStorage_Verify(uint32_t offset, const void *buffer, uint32_t length)
{
    static uint32_t readback_words[64] __attribute__((aligned(8)));
    uint32_t cursor;
    if (buffer == NULL || !storage_range_valid(offset, length)) return false;
    for (cursor = 0u; cursor < length; cursor += sizeof(readback_words))
    {
        uint32_t chunk = length - cursor;
        if (chunk > sizeof(readback_words)) chunk = sizeof(readback_words);
        if (!BrowserProfileStorage_Read(offset + cursor, readback_words, chunk) ||
            memcmp(readback_words, (const uint8_t *)buffer + cursor, chunk) != 0)
        {
            snprintf(g_last_error, sizeof(g_last_error), "SRAM verify mismatch +%lu", (unsigned long)cursor);
            return false;
        }
    }
    return true;
}

const char *BrowserProfileStorage_LastError(void)
{
    return g_last_error;
}
