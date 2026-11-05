#ifndef POINTER_STATE_H
#define POINTER_STATE_H

#include <stdbool.h>
#include <stdint.h>

#define POINTER_BUTTON_LEFT 0x01u
#define POINTER_BUTTON_RIGHT 0x02u

void pointer_state_init(void);
void pointer_state_publish_delta(uint8_t buttons, int32_t dx, int32_t dy);
bool pointer_state_accept_cumulative(uint32_t session_id, uint32_t sequence,
                                     uint8_t buttons, int32_t total_x,
                                     int32_t total_y);
void pointer_state_service(void);
void pointer_state_consume_report(uint8_t out[4]);
void pointer_state_poll_validation(void);

#endif
