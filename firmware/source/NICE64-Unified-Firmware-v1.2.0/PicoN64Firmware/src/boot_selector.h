#ifndef BOOT_SELECTOR_H
#define BOOT_SELECTOR_H

#include <stdbool.h>
#include <stdint.h>

#include "role_manager.h"

typedef enum {
    BOOT_SELECTOR_STATUS_EMPTY = 0,
    BOOT_SELECTOR_STATUS_VALID = 1,
    BOOT_SELECTOR_STATUS_BAD_MAGIC = 2,
    BOOT_SELECTOR_STATUS_BAD_VERSION = 3,
    BOOT_SELECTOR_STATUS_BAD_CHECK = 4,
    BOOT_SELECTOR_STATUS_BAD_MODE = 5,
    BOOT_SELECTOR_STATUS_ATTEMPTS_EXCEEDED = 6,
} boot_selector_status_t;

typedef struct {
    boot_selector_status_t status;
    nice64_active_mode_t mode;
    uint8_t flags;
    uint16_t sequence;
    uint8_t attempt_count;
    uint32_t raw[4];
} boot_selector_record_t;

void boot_selector_boot_observe(void);
boot_selector_record_t boot_selector_read(void);
bool boot_selector_write(nice64_active_mode_t mode, uint8_t flags);
void boot_selector_clear(void);
boot_selector_status_t boot_selector_mark_attempt(void);
void boot_selector_corrupt_magic(void);
void boot_selector_corrupt_version(void);
void boot_selector_corrupt_check(void);
const char *boot_selector_status_name(boot_selector_status_t status);
void boot_selector_reboot(uint32_t delay_ms);

#endif
