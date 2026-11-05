#ifndef NICE64_TEST_N64_MOUSE_H
#define NICE64_TEST_N64_MOUSE_H

#include <stdbool.h>
#include <stdint.h>

#define N64_MOUSE_IDENTIFIER 0x0200u
#define N64_MOUSE_COMMAND 0x01u

typedef struct {
    uint8_t raw[4];
    bool left;
    bool right;
    int8_t dx;
    int8_t dy;
} n64_mouse_report_t;

bool n64_mouse_test_poll(int port, n64_mouse_report_t *report);
void n64_mouse_test_decode(const uint8_t raw[4], n64_mouse_report_t *report);

#endif
