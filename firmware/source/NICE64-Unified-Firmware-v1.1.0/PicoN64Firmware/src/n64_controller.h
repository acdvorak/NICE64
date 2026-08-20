#pragma once

#include <stdbool.h>
#include <stdint.h>

void n64_controller_init(void);
bool n64_controller_parse_serial_state(const char *line, uint8_t out[4]);
void n64_controller_set_state(const uint8_t state[4]);
void n64_controller_set_ignore_serial(bool ignore_serial);
bool n64_controller_ignore_serial(void);
void n64_controller_get_status(uint8_t out[4]);
void n64_controller_get_status_irq(uint8_t out[4]);
uint32_t n64_controller_serial_state_count(void);
uint32_t n64_controller_serial_ignored_count(void);
uint32_t n64_controller_serial_a_on_count(void);
void n64_controller_clear_debug(void);
