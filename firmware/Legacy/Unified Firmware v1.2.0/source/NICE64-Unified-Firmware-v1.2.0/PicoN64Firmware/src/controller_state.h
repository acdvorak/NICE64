#ifndef CONTROLLER_STATE_H
#define CONTROLLER_STATE_H

#include <stdint.h>

void controller_state_init(void);
void controller_state_publish_raw(const uint8_t state[4]);
void controller_state_service(void);
void controller_state_read_snapshot(uint8_t out_state[4]);
void controller_state_poll_validation(void);

#endif
