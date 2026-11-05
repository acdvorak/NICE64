#include "boot_selector.h"

#include "hardware/structs/watchdog.h"
#include "hardware/watchdog.h"

#define BOOT_SELECTOR_MAGIC 0x4E365253u
#define BOOT_SELECTOR_CHECK_XOR 0xA64B0075u
#define BOOT_SELECTOR_FORMAT_VERSION 1u
#define BOOT_SELECTOR_MAX_ATTEMPTS 2u

#define BOOT_SELECTOR_WORD1_VERSION_SHIFT 24u
#define BOOT_SELECTOR_WORD1_ROLE_SHIFT 16u
#define BOOT_SELECTOR_WORD1_SERVICE_SHIFT 8u
#define BOOT_SELECTOR_WORD1_FLAGS_SHIFT 0u

#define BOOT_SELECTOR_WORD2_SEQUENCE_SHIFT 16u
#define BOOT_SELECTOR_WORD2_ATTEMPT_SHIFT 8u

#define BOOT_SELECTOR_WORD1_BYTE_MASK 0xFFu
#define BOOT_SELECTOR_WORD2_SEQUENCE_MASK 0xFFFFu

static uint32_t boot_selector_word1(nice64_active_mode_t mode, uint8_t flags) {
    return (BOOT_SELECTOR_FORMAT_VERSION << BOOT_SELECTOR_WORD1_VERSION_SHIFT) |
           ((uint32_t)mode.role << BOOT_SELECTOR_WORD1_ROLE_SHIFT) |
           ((uint32_t)mode.standalone_service << BOOT_SELECTOR_WORD1_SERVICE_SHIFT) |
           ((uint32_t)flags << BOOT_SELECTOR_WORD1_FLAGS_SHIFT);
}

static uint32_t boot_selector_word2(uint16_t sequence, uint8_t attempt_count) {
    return ((uint32_t)sequence << BOOT_SELECTOR_WORD2_SEQUENCE_SHIFT) |
           ((uint32_t)attempt_count << BOOT_SELECTOR_WORD2_ATTEMPT_SHIFT);
}

static uint32_t boot_selector_check(uint32_t word0, uint32_t word1, uint32_t word2) {
    return word0 ^ word1 ^ word2 ^ BOOT_SELECTOR_CHECK_XOR;
}

static bool boot_selector_target_is_supported(nice64_active_mode_t mode) {
    if (mode.role == NICE64_ROLE_CONTROLLER) {
        return mode.standalone_service == NICE64_SERVICE_NONE;
    }

    if (mode.role == NICE64_ROLE_STANDALONE) {
        return mode.standalone_service == NICE64_SERVICE_DIAGNOSTICS ||
               mode.standalone_service == NICE64_SERVICE_NETWORK ||
               mode.standalone_service == NICE64_SERVICE_KEYBOARD_MOUSE;
    }

    return false;
}

static void boot_selector_write_raw(uint32_t word0,
                                    uint32_t word1,
                                    uint32_t word2,
                                    uint32_t word3) {
    watchdog_hw->scratch[0] = word0;
    watchdog_hw->scratch[1] = word1;
    watchdog_hw->scratch[2] = word2;
    watchdog_hw->scratch[3] = word3;
}

boot_selector_record_t boot_selector_read(void) {
    boot_selector_record_t record = {
        .status = BOOT_SELECTOR_STATUS_EMPTY,
        .mode = {
            .role = NICE64_ROLE_CONTROLLER,
            .standalone_service = NICE64_SERVICE_NONE,
        },
        .flags = 0u,
        .sequence = 0u,
        .attempt_count = 0u,
        .raw = {
            watchdog_hw->scratch[0],
            watchdog_hw->scratch[1],
            watchdog_hw->scratch[2],
            watchdog_hw->scratch[3],
        },
    };

    if (record.raw[0] == 0u &&
        record.raw[1] == 0u &&
        record.raw[2] == 0u &&
        record.raw[3] == 0u) {
        return record;
    }

    if (record.raw[0] != BOOT_SELECTOR_MAGIC) {
        record.status = BOOT_SELECTOR_STATUS_BAD_MAGIC;
        return record;
    }

    const uint8_t version = (uint8_t)((record.raw[1] >> BOOT_SELECTOR_WORD1_VERSION_SHIFT) &
                                      BOOT_SELECTOR_WORD1_BYTE_MASK);
    if (version != BOOT_SELECTOR_FORMAT_VERSION) {
        record.status = BOOT_SELECTOR_STATUS_BAD_VERSION;
        return record;
    }

    if (record.raw[3] != boot_selector_check(record.raw[0], record.raw[1], record.raw[2])) {
        record.status = BOOT_SELECTOR_STATUS_BAD_CHECK;
        return record;
    }

    record.mode.role = (nice64_role_t)((record.raw[1] >> BOOT_SELECTOR_WORD1_ROLE_SHIFT) &
                                       BOOT_SELECTOR_WORD1_BYTE_MASK);
    record.mode.standalone_service =
        (nice64_standalone_service_t)((record.raw[1] >> BOOT_SELECTOR_WORD1_SERVICE_SHIFT) &
                                      BOOT_SELECTOR_WORD1_BYTE_MASK);
    record.flags = (uint8_t)((record.raw[1] >> BOOT_SELECTOR_WORD1_FLAGS_SHIFT) &
                             BOOT_SELECTOR_WORD1_BYTE_MASK);
    record.sequence = (uint16_t)((record.raw[2] >> BOOT_SELECTOR_WORD2_SEQUENCE_SHIFT) &
                                 BOOT_SELECTOR_WORD2_SEQUENCE_MASK);
    record.attempt_count = (uint8_t)((record.raw[2] >> BOOT_SELECTOR_WORD2_ATTEMPT_SHIFT) &
                                     BOOT_SELECTOR_WORD1_BYTE_MASK);

    if (!boot_selector_target_is_supported(record.mode)) {
        record.status = BOOT_SELECTOR_STATUS_BAD_MODE;
        return record;
    }

    if (record.attempt_count > BOOT_SELECTOR_MAX_ATTEMPTS) {
        record.status = BOOT_SELECTOR_STATUS_ATTEMPTS_EXCEEDED;
        return record;
    }

    record.status = BOOT_SELECTOR_STATUS_VALID;
    return record;
}

bool boot_selector_write(nice64_active_mode_t mode, uint8_t flags) {
    if (!boot_selector_target_is_supported(mode)) {
        return false;
    }

    boot_selector_record_t existing = boot_selector_read();
    uint16_t sequence = 1u;
    if (existing.status == BOOT_SELECTOR_STATUS_VALID) {
        sequence = (uint16_t)(existing.sequence + 1u);
        if (sequence == 0u) {
            sequence = 1u;
        }
    }

    const uint32_t word0 = BOOT_SELECTOR_MAGIC;
    const uint32_t word1 = boot_selector_word1(mode, flags);
    const uint32_t word2 = boot_selector_word2(sequence, 0u);
    const uint32_t word3 = boot_selector_check(word0, word1, word2);
    boot_selector_write_raw(word0, word1, word2, word3);
    return true;
}

void boot_selector_clear(void) {
    boot_selector_write_raw(0u, 0u, 0u, 0u);
}

boot_selector_status_t boot_selector_mark_attempt(void) {
    boot_selector_record_t record = boot_selector_read();
    if (record.status != BOOT_SELECTOR_STATUS_VALID) {
        return record.status;
    }

    if (record.attempt_count >= BOOT_SELECTOR_MAX_ATTEMPTS) {
        boot_selector_clear();
        return BOOT_SELECTOR_STATUS_ATTEMPTS_EXCEEDED;
    }

    const uint32_t word0 = BOOT_SELECTOR_MAGIC;
    const uint32_t word1 = boot_selector_word1(record.mode, record.flags);
    const uint32_t word2 = boot_selector_word2(record.sequence,
                                               (uint8_t)(record.attempt_count + 1u));
    const uint32_t word3 = boot_selector_check(word0, word1, word2);
    boot_selector_write_raw(word0, word1, word2, word3);
    return BOOT_SELECTOR_STATUS_VALID;
}

void boot_selector_boot_observe(void) {
    const boot_selector_status_t status = boot_selector_mark_attempt();
    if (status == BOOT_SELECTOR_STATUS_ATTEMPTS_EXCEEDED) {
        boot_selector_clear();
    }
}

void boot_selector_corrupt_magic(void) {
    watchdog_hw->scratch[0] ^= 0x00000001u;
}

void boot_selector_corrupt_version(void) {
    watchdog_hw->scratch[1] ^= (0x01u << BOOT_SELECTOR_WORD1_VERSION_SHIFT);
}

void boot_selector_corrupt_check(void) {
    watchdog_hw->scratch[3] ^= 0x00000001u;
}

const char *boot_selector_status_name(boot_selector_status_t status) {
    switch (status) {
    case BOOT_SELECTOR_STATUS_EMPTY:
        return "EMPTY";
    case BOOT_SELECTOR_STATUS_VALID:
        return "VALID";
    case BOOT_SELECTOR_STATUS_BAD_MAGIC:
        return "BAD_MAGIC";
    case BOOT_SELECTOR_STATUS_BAD_VERSION:
        return "BAD_VERSION";
    case BOOT_SELECTOR_STATUS_BAD_CHECK:
        return "BAD_CHECK";
    case BOOT_SELECTOR_STATUS_BAD_MODE:
        return "BAD_MODE";
    case BOOT_SELECTOR_STATUS_ATTEMPTS_EXCEEDED:
        return "ATTEMPTS_EXCEEDED";
    default:
        return "UNKNOWN";
    }
}

void boot_selector_reboot(uint32_t delay_ms) {
    watchdog_reboot(0u, 0u, delay_ms);
    while (true) {
        tight_loop_contents();
    }
}
