#ifndef NICE64_WIFI_PERSISTENCE_RECORD_H
#define NICE64_WIFI_PERSISTENCE_RECORD_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define NICE64_WIFI_PERSIST_RECORD_SIZE 256u
#define NICE64_WIFI_PERSIST_SSID_MAX 32u
#define NICE64_WIFI_PERSIST_PASSWORD_MAX 64u

typedef struct {
    uint8_t ssid[NICE64_WIFI_PERSIST_SSID_MAX];
    uint8_t password[NICE64_WIFI_PERSIST_PASSWORD_MAX];
    uint8_t ssid_length;
    uint8_t password_length;
    uint8_t address_mode;
    bool tombstone;
    uint32_t generation;
} wifi_persistence_record_t;

typedef enum {
    WIFI_PERSIST_RECORD_OK = 0,
    WIFI_PERSIST_RECORD_EMPTY,
    WIFI_PERSIST_RECORD_BAD_MAGIC,
    WIFI_PERSIST_RECORD_UNSUPPORTED_VERSION,
    WIFI_PERSIST_RECORD_BAD_BOUNDS,
    WIFI_PERSIST_RECORD_BAD_CRC,
} wifi_persistence_record_status_t;

void wifi_persistence_record_encode(
    uint8_t output[NICE64_WIFI_PERSIST_RECORD_SIZE],
    const wifi_persistence_record_t *record);
wifi_persistence_record_status_t wifi_persistence_record_decode(
    const uint8_t input[NICE64_WIFI_PERSIST_RECORD_SIZE],
    wifi_persistence_record_t *record);
bool wifi_persistence_generation_newer(uint32_t candidate, uint32_t reference);
int wifi_persistence_record_select(
    const uint8_t slot_a[NICE64_WIFI_PERSIST_RECORD_SIZE],
    const uint8_t slot_b[NICE64_WIFI_PERSIST_RECORD_SIZE],
    wifi_persistence_record_t *selected);
bool wifi_persistence_record_credentials_equal(
    const wifi_persistence_record_t *left,
    const wifi_persistence_record_t *right);

#endif
