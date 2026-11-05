#include "wifi_persistence.h"

#include <stdint.h>
#include <string.h>

#include "hardware/flash.h"
#include "hardware/regs/addressmap.h"
#include "pico/flash.h"
#include "pico/multicore.h"
#include "pico/platform.h"
#include "transfer_pak_flash_layout.h"
#include "wifi_persistence_record.h"

#define WIFI_PERSIST_SECTOR_SIZE FLASH_SECTOR_SIZE
#define WIFI_PERSIST_PAGE_SIZE FLASH_PAGE_SIZE
#define WIFI_PERSIST_SLOT_A_OFFSET \
    (PICO_FLASH_SIZE_BYTES - NICE64_WIFI_PERSIST_RESERVED_BYTES)
#define WIFI_PERSIST_SLOT_B_OFFSET \
    (PICO_FLASH_SIZE_BYTES - WIFI_PERSIST_SECTOR_SIZE)
#define FLASH_SAFE_TIMEOUT_MS 1000u

_Static_assert(PICO_FLASH_SIZE_BYTES >= 4u * 1024u * 1024u,
               "Step 2C.3 persistence layout requires 4 MiB flash");
_Static_assert(WIFI_PERSIST_SECTOR_SIZE == 4096u,
               "Unexpected flash sector size");
_Static_assert(WIFI_PERSIST_PAGE_SIZE == NICE64_WIFI_PERSIST_RECORD_SIZE,
               "Record must occupy exactly one flash page");
_Static_assert(WIFI_PERSIST_SLOT_A_OFFSET >= TRANSFER_ROM_FLASH_END,
               "Wi-Fi persistence overlaps Transfer Pak flash");
_Static_assert((WIFI_PERSIST_SLOT_A_OFFSET % WIFI_PERSIST_SECTOR_SIZE) == 0u,
               "Wi-Fi persistence slot A is not sector aligned");
_Static_assert((WIFI_PERSIST_SLOT_B_OFFSET % WIFI_PERSIST_SECTOR_SIZE) == 0u,
               "Wi-Fi persistence slot B is not sector aligned");

extern uint8_t __flash_binary_end;

typedef struct {
    uint32_t offset;
    uint8_t page[WIFI_PERSIST_PAGE_SIZE];
    bool erase_only;
} flash_operation_t;

static bool core0_lockout_ready;
static bool persistence_available;
static int selected_slot = -1;
static wifi_persistence_record_t selected_record;

static const uint8_t *slot_address(unsigned slot) {
    const uint32_t offset = slot == 0u
        ? WIFI_PERSIST_SLOT_A_OFFSET : WIFI_PERSIST_SLOT_B_OFFSET;
    return (const uint8_t *)(XIP_BASE + offset);
}

static bool image_fits(void) {
    const uintptr_t image_end = (uintptr_t)&__flash_binary_end;
    return image_end >= XIP_BASE &&
           image_end - XIP_BASE <= WIFI_PERSIST_SLOT_A_OFFSET;
}

static void __not_in_flash_func(run_flash_operation)(void *opaque) {
    flash_operation_t *operation = opaque;
    flash_range_erase(operation->offset, WIFI_PERSIST_SECTOR_SIZE);
    if (!operation->erase_only) {
        flash_range_program(operation->offset, operation->page,
                            WIFI_PERSIST_PAGE_SIZE);
    }
}

static wifi_persistence_result_t perform_operation(
    flash_operation_t *operation) {
    if (get_core_num() != 1u) return WIFI_PERSISTENCE_UNSAFE_CONTEXT;
    if (!persistence_available) return WIFI_PERSISTENCE_NOT_AVAILABLE;
    if (!image_fits()) return WIFI_PERSISTENCE_IMAGE_OVERLAP;
    const int result = flash_safe_execute(run_flash_operation, operation,
                                          FLASH_SAFE_TIMEOUT_MS);
    return result == PICO_OK ? WIFI_PERSISTENCE_OK
                             : WIFI_PERSISTENCE_FLASH_ERROR;
}

static void refresh_selection(void) {
    selected_slot = wifi_persistence_record_select(
        slot_address(0u), slot_address(1u), &selected_record);
}

static void record_from_config(wifi_persistence_record_t *record,
                               const wifi_config_t *config) {
    memset(record, 0, sizeof(*record));
    record->ssid_length = config->ssid_length;
    record->password_length = config->password_length;
    record->address_mode = (uint8_t)config->address_mode;
    memcpy(record->ssid, config->ssid, config->ssid_length);
    memcpy(record->password, config->password, config->password_length);
}

static wifi_persistence_result_t write_record(
    const wifi_persistence_record_t *record, unsigned target_slot) {
    flash_operation_t operation = {
        .offset = target_slot == 0u ? WIFI_PERSIST_SLOT_A_OFFSET
                                    : WIFI_PERSIST_SLOT_B_OFFSET,
        .erase_only = false,
    };
    wifi_persistence_record_encode(operation.page, record);
    wifi_persistence_result_t result = perform_operation(&operation);
    if (result != WIFI_PERSISTENCE_OK) return result;

    wifi_persistence_record_t verified;
    if (wifi_persistence_record_decode(slot_address(target_slot), &verified) !=
            WIFI_PERSIST_RECORD_OK ||
        verified.generation != record->generation ||
        verified.tombstone != record->tombstone ||
        (!record->tombstone &&
         !wifi_persistence_record_credentials_equal(&verified, record))) {
        return WIFI_PERSISTENCE_VERIFY_ERROR;
    }
    refresh_selection();
    return selected_slot == (int)target_slot ? WIFI_PERSISTENCE_OK
                                              : WIFI_PERSISTENCE_VERIFY_ERROR;
}

bool wifi_persistence_prepare_core0_lockout(void) {
    if (get_core_num() != 0u) return false;
    core0_lockout_ready = flash_safe_execute_core_init();
    return core0_lockout_ready;
}

void wifi_persistence_init(void) {
    persistence_available = get_core_num() == 1u && core0_lockout_ready &&
                            image_fits();
    memset(&selected_record, 0, sizeof(selected_record));
    refresh_selection();
}

bool wifi_persistence_restore(wifi_config_t *config) {
    if (config == NULL || selected_slot < 0 || selected_record.tombstone) {
        return false;
    }
    memset(config, 0, sizeof(*config));
    memcpy(config->ssid, selected_record.ssid, selected_record.ssid_length);
    memcpy(config->password, selected_record.password,
           selected_record.password_length);
    config->ssid_length = selected_record.ssid_length;
    config->password_length = selected_record.password_length;
    config->address_mode = (wifi_address_mode_t)selected_record.address_mode;
    config->ssid_set = true;
    config->password_set = true;
    config->address_mode_set = true;
    return true;
}

bool wifi_persistence_has_stored_config(void) {
    return selected_slot >= 0 && !selected_record.tombstone;
}

wifi_persistence_result_t wifi_persistence_commit_validated(
    const wifi_config_t *config) {
    if (config == NULL || !config->ssid_set || !config->password_set ||
        !config->address_mode_set || config->ssid_length == 0u ||
        config->ssid_length > NICE64_WIFI_PERSIST_SSID_MAX ||
        config->password_length > NICE64_WIFI_PERSIST_PASSWORD_MAX ||
        config->address_mode != WIFI_ADDRESS_DHCP) {
        return WIFI_PERSISTENCE_NOT_AVAILABLE;
    }

    wifi_persistence_record_t candidate;
    record_from_config(&candidate, config);
    if (selected_slot >= 0 && !selected_record.tombstone &&
        wifi_persistence_record_credentials_equal(&candidate,
                                                  &selected_record)) {
        return WIFI_PERSISTENCE_UNCHANGED;
    }
    candidate.generation = selected_slot >= 0
        ? selected_record.generation + 1u : 1u;
    return write_record(&candidate, selected_slot == 0 ? 1u : 0u);
}

wifi_persistence_result_t wifi_persistence_forget(void) {
    if (selected_slot >= 0 && selected_record.tombstone) {
        return WIFI_PERSISTENCE_UNCHANGED;
    }

    wifi_persistence_record_t tombstone;
    memset(&tombstone, 0, sizeof(tombstone));
    tombstone.tombstone = true;
    tombstone.generation = selected_slot >= 0
        ? selected_record.generation + 1u : 1u;
    const unsigned tombstone_slot = selected_slot == 0 ? 1u : 0u;
    wifi_persistence_result_t result = write_record(&tombstone, tombstone_slot);
    if (result != WIFI_PERSISTENCE_OK) return result;

    const unsigned old_slot = tombstone_slot == 0u ? 1u : 0u;
    flash_operation_t cleanup = {
        .offset = old_slot == 0u ? WIFI_PERSIST_SLOT_A_OFFSET
                                 : WIFI_PERSIST_SLOT_B_OFFSET,
        .erase_only = true,
    };
    result = perform_operation(&cleanup);
    refresh_selection();
    return result;
}

const char *wifi_persistence_result_name(wifi_persistence_result_t result) {
    switch (result) {
    case WIFI_PERSISTENCE_OK: return "OK";
    case WIFI_PERSISTENCE_UNCHANGED: return "UNCHANGED";
    case WIFI_PERSISTENCE_NOT_AVAILABLE: return "NOT_AVAILABLE";
    case WIFI_PERSISTENCE_UNSAFE_CONTEXT: return "UNSAFE_CONTEXT";
    case WIFI_PERSISTENCE_IMAGE_OVERLAP: return "IMAGE_OVERLAP";
    case WIFI_PERSISTENCE_FLASH_ERROR: return "FLASH_ERROR";
    case WIFI_PERSISTENCE_VERIFY_ERROR: return "VERIFY_ERROR";
    default: return "UNKNOWN";
    }
}
