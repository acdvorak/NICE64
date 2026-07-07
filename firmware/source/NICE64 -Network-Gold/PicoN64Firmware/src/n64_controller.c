#include "n64_controller.h"

#include <ctype.h>
#include <string.h>

#include "hardware/sync.h"

static volatile uint8_t controller_state[4] = {0x00, 0x00, 0x00, 0x00};
static volatile bool ignore_serial_states = false;
static volatile uint32_t serial_states = 0;
static volatile uint32_t serial_ignored = 0;
static volatile uint32_t serial_a_on = 0;

static int hex_value(int c) {
    if (c >= '0' && c <= '9') {
        return c - '0';
    }

    c = toupper(c);
    if (c >= 'A' && c <= 'F') {
        return c - 'A' + 10;
    }

    return -1;
}

void n64_controller_init(void) {
    const uint32_t save = save_and_disable_interrupts();
    controller_state[0] = 0x00;
    controller_state[1] = 0x00;
    controller_state[2] = 0x00;
    controller_state[3] = 0x00;
    ignore_serial_states = false;
    serial_states = 0;
    serial_ignored = 0;
    serial_a_on = 0;
    restore_interrupts(save);
}

bool n64_controller_parse_serial_state(const char *line, uint8_t out[4]) {
    const char *p = strstr(line, "N64");
    if (p) {
        p += 3;
    } else {
        p = line;
    }

    while (*p && isspace((unsigned char)*p)) {
        p++;
    }

    for (int i = 0; i < 4; ++i) {
        while (*p && isspace((unsigned char)*p)) {
            p++;
        }

        const int hi = hex_value((unsigned char)p[0]);
        const int lo = hex_value((unsigned char)p[1]);
        if (hi < 0 || lo < 0) {
            return false;
        }

        out[i] = (uint8_t)((hi << 4) | lo);
        p += 2;
    }

    return true;
}

void n64_controller_set_state(const uint8_t state[4]) {
    const uint32_t save = save_and_disable_interrupts();
    serial_states++;
    if ((state[0] & 0x80u) != 0) {
        serial_a_on++;
    }

    if (ignore_serial_states) {
        serial_ignored++;
        restore_interrupts(save);
        return;
    }

    controller_state[0] = state[0];
    controller_state[1] = state[1];
    controller_state[2] = state[2];
    controller_state[3] = state[3];
    restore_interrupts(save);
}

void n64_controller_set_ignore_serial(bool ignore_serial) {
    const uint32_t save = save_and_disable_interrupts();
    ignore_serial_states = ignore_serial;
    restore_interrupts(save);
}

bool n64_controller_ignore_serial(void) {
    const uint32_t save = save_and_disable_interrupts();
    const bool ignore_serial = ignore_serial_states;
    restore_interrupts(save);
    return ignore_serial;
}

void n64_controller_get_status(uint8_t out[4]) {
    const uint32_t save = save_and_disable_interrupts();
    out[0] = controller_state[0];
    out[1] = controller_state[1];
    out[2] = controller_state[2];
    out[3] = controller_state[3];
    restore_interrupts(save);
}

void n64_controller_get_status_irq(uint8_t out[4]) {
    out[0] = controller_state[0];
    out[1] = controller_state[1];
    out[2] = controller_state[2];
    out[3] = controller_state[3];
}

uint32_t n64_controller_serial_state_count(void) {
    const uint32_t save = save_and_disable_interrupts();
    const uint32_t count = serial_states;
    restore_interrupts(save);
    return count;
}

uint32_t n64_controller_serial_ignored_count(void) {
    const uint32_t save = save_and_disable_interrupts();
    const uint32_t count = serial_ignored;
    restore_interrupts(save);
    return count;
}

uint32_t n64_controller_serial_a_on_count(void) {
    const uint32_t save = save_and_disable_interrupts();
    const uint32_t count = serial_a_on;
    restore_interrupts(save);
    return count;
}

void n64_controller_clear_debug(void) {
    const uint32_t save = save_and_disable_interrupts();
    serial_states = 0;
    serial_ignored = 0;
    serial_a_on = 0;
    restore_interrupts(save);
}
