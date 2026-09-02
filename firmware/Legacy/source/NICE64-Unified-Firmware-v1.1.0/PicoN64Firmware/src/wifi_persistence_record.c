#include "wifi_persistence_record.h"

#include <string.h>

#define RECORD_MAGIC 0x5734364eu /* N64W, little-endian */
#define RECORD_VERSION 1u
#define RECORD_HEADER_LENGTH 32u
#define RECORD_PAYLOAD_LENGTH 96u
#define RECORD_FLAG_ACTIVE 0x01u
#define RECORD_FLAG_TOMBSTONE 0x02u
#define RECORD_CRC_OFFSET 252u

static uint16_t read_u16(const uint8_t *data) {
    return (uint16_t)data[0] | ((uint16_t)data[1] << 8u);
}

static uint32_t read_u32(const uint8_t *data) {
    return (uint32_t)data[0] | ((uint32_t)data[1] << 8u) |
           ((uint32_t)data[2] << 16u) | ((uint32_t)data[3] << 24u);
}

static void write_u16(uint8_t *data, uint16_t value) {
    data[0] = (uint8_t)value;
    data[1] = (uint8_t)(value >> 8u);
}

static void write_u32(uint8_t *data, uint32_t value) {
    data[0] = (uint8_t)value;
    data[1] = (uint8_t)(value >> 8u);
    data[2] = (uint8_t)(value >> 16u);
    data[3] = (uint8_t)(value >> 24u);
}

static uint32_t crc32(const uint8_t *data, size_t length) {
    uint32_t crc = 0xffffffffu;
    for (size_t index = 0u; index < length; ++index) {
        crc ^= data[index];
        for (unsigned bit = 0u; bit < 8u; ++bit) {
            const uint32_t mask = (uint32_t)-(int32_t)(crc & 1u);
            crc = (crc >> 1u) ^ (0xedb88320u & mask);
        }
    }
    return ~crc;
}

static bool all_value(const uint8_t *data, size_t length, uint8_t value) {
    for (size_t index = 0u; index < length; ++index) {
        if (data[index] != value) return false;
    }
    return true;
}

void wifi_persistence_record_encode(
    uint8_t output[NICE64_WIFI_PERSIST_RECORD_SIZE],
    const wifi_persistence_record_t *record) {
    memset(output, 0, NICE64_WIFI_PERSIST_RECORD_SIZE);
    write_u32(&output[0], RECORD_MAGIC);
    write_u16(&output[4], RECORD_VERSION);
    write_u16(&output[6], RECORD_HEADER_LENGTH);
    write_u32(&output[8], record->generation);
    write_u16(&output[12], RECORD_PAYLOAD_LENGTH);
    output[14] = record->tombstone ? 0u : record->ssid_length;
    output[15] = record->tombstone ? 0u : record->password_length;
    output[16] = record->address_mode;
    output[17] = record->tombstone ? RECORD_FLAG_TOMBSTONE : RECORD_FLAG_ACTIVE;
    if (!record->tombstone) {
        memcpy(&output[32], record->ssid, record->ssid_length);
        memcpy(&output[64], record->password, record->password_length);
    }
    write_u32(&output[RECORD_CRC_OFFSET], crc32(output, RECORD_CRC_OFFSET));
}

wifi_persistence_record_status_t wifi_persistence_record_decode(
    const uint8_t input[NICE64_WIFI_PERSIST_RECORD_SIZE],
    wifi_persistence_record_t *record) {
    if (all_value(input, NICE64_WIFI_PERSIST_RECORD_SIZE, 0xffu)) {
        return WIFI_PERSIST_RECORD_EMPTY;
    }
    if (read_u32(&input[0]) != RECORD_MAGIC) {
        return WIFI_PERSIST_RECORD_BAD_MAGIC;
    }
    if (read_u16(&input[4]) != RECORD_VERSION) {
        return WIFI_PERSIST_RECORD_UNSUPPORTED_VERSION;
    }
    if (read_u16(&input[6]) != RECORD_HEADER_LENGTH ||
        read_u16(&input[12]) != RECORD_PAYLOAD_LENGTH ||
        !all_value(&input[18], 14u, 0u) ||
        !all_value(&input[128], 124u, 0u)) {
        return WIFI_PERSIST_RECORD_BAD_BOUNDS;
    }
    if (read_u32(&input[RECORD_CRC_OFFSET]) !=
        crc32(input, RECORD_CRC_OFFSET)) {
        return WIFI_PERSIST_RECORD_BAD_CRC;
    }

    const uint8_t flags = input[17];
    const bool tombstone = flags == RECORD_FLAG_TOMBSTONE;
    if ((flags != RECORD_FLAG_ACTIVE && !tombstone) || input[16] != 0u ||
        (!tombstone && (input[14] == 0u ||
                        input[14] > NICE64_WIFI_PERSIST_SSID_MAX ||
                        input[15] > NICE64_WIFI_PERSIST_PASSWORD_MAX)) ||
        (tombstone && (input[14] != 0u || input[15] != 0u)) ||
        !all_value(&input[32u + input[14]],
                   NICE64_WIFI_PERSIST_SSID_MAX - input[14], 0u) ||
        !all_value(&input[64u + input[15]],
                   NICE64_WIFI_PERSIST_PASSWORD_MAX - input[15], 0u)) {
        return WIFI_PERSIST_RECORD_BAD_BOUNDS;
    }

    memset(record, 0, sizeof(*record));
    record->generation = read_u32(&input[8]);
    record->ssid_length = input[14];
    record->password_length = input[15];
    record->address_mode = input[16];
    record->tombstone = tombstone;
    memcpy(record->ssid, &input[32], record->ssid_length);
    memcpy(record->password, &input[64], record->password_length);
    return WIFI_PERSIST_RECORD_OK;
}

bool wifi_persistence_generation_newer(uint32_t candidate, uint32_t reference) {
    return candidate != reference &&
           (uint32_t)(candidate - reference) < 0x80000000u;
}

int wifi_persistence_record_select(
    const uint8_t slot_a[NICE64_WIFI_PERSIST_RECORD_SIZE],
    const uint8_t slot_b[NICE64_WIFI_PERSIST_RECORD_SIZE],
    wifi_persistence_record_t *selected) {
    wifi_persistence_record_t a;
    wifi_persistence_record_t b;
    const bool a_valid = wifi_persistence_record_decode(slot_a, &a) ==
                         WIFI_PERSIST_RECORD_OK;
    const bool b_valid = wifi_persistence_record_decode(slot_b, &b) ==
                         WIFI_PERSIST_RECORD_OK;
    if (!a_valid && !b_valid) return -1;
    if (a_valid && (!b_valid || !wifi_persistence_generation_newer(
                                     b.generation, a.generation))) {
        if (selected != NULL) *selected = a;
        return 0;
    }
    if (selected != NULL) *selected = b;
    return 1;
}

bool wifi_persistence_record_credentials_equal(
    const wifi_persistence_record_t *left,
    const wifi_persistence_record_t *right) {
    return left != NULL && right != NULL && !left->tombstone &&
           !right->tombstone && left->address_mode == right->address_mode &&
           left->ssid_length == right->ssid_length &&
           left->password_length == right->password_length &&
           memcmp(left->ssid, right->ssid, left->ssid_length) == 0 &&
           memcmp(left->password, right->password, left->password_length) == 0;
}
