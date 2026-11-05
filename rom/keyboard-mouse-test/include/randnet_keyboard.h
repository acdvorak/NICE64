#ifndef NICE64_TEST_RANDNET_KEYBOARD_H
#define NICE64_TEST_RANDNET_KEYBOARD_H

#include <stdbool.h>
#include <stdint.h>

#define RANDNET_KEYBOARD_IDENTIFIER 0x0002u
#define RANDNET_KEYBOARD_COMMAND 0x13u
#define RANDNET_KEYBOARD_STATUS_TOO_MANY_KEYS 0x10u
#define RANDNET_KEYBOARD_STATUS_HOME 0x01u

typedef struct {
    uint8_t request[2];
    uint8_t raw[7];
    uint16_t keys[3];
    uint8_t status;
} randnet_keyboard_report_t;

bool randnet_keyboard_poll(int port, uint8_t led_request,
                           randnet_keyboard_report_t *report);
void randnet_keyboard_build_request(uint8_t led_request, uint8_t request[2]);
void randnet_keyboard_decode(const uint8_t raw[7],
                             randnet_keyboard_report_t *report);
const char *randnet_keyboard_key_name(uint16_t matrix_value);

#endif
