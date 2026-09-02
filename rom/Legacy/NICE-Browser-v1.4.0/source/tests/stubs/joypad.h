#ifndef CHECKPOINT_E_STUB_JOYPAD_H
#define CHECKPOINT_E_STUB_JOYPAD_H

#include <stdbool.h>
#include <stdint.h>

typedef int joypad_port_t;

uint16_t joypad_get_identifier(joypad_port_t port);
bool joypad_is_connected(joypad_port_t port);

#endif
