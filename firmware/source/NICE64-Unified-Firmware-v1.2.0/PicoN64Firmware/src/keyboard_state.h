#ifndef KEYBOARD_STATE_H
#define KEYBOARD_STATE_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "randnet_keymap.h"

#define KEYBOARD_STATE_REPORT_SIZE 7u
#define KEYBOARD_STATE_STATUS_HOME 0x01u
#define KEYBOARD_STATE_STATUS_TOO_MANY_KEYS 0x10u

void keyboard_state_init(void);
bool keyboard_state_publish_held(const nice64_logical_key_t *keys, size_t count);
bool keyboard_state_accept_held(uint32_t session_id, uint32_t sequence,
                                const nice64_logical_key_t *keys, size_t count);
void keyboard_state_service(void);
void keyboard_state_read_report(uint8_t out[KEYBOARD_STATE_REPORT_SIZE]);
void keyboard_state_poll_validation(void);

#endif
