#include "randnet_keyboard.h"

#include <libdragon.h>
#include <stddef.h>
#include <string.h>

static uint16_t read_be16(const uint8_t *bytes)
{
    return (uint16_t)(((uint16_t)bytes[0] << 8) | bytes[1]);
}

void randnet_keyboard_decode(const uint8_t raw[7],
                             randnet_keyboard_report_t *report)
{
    memcpy(report->raw, raw, sizeof(report->raw));
    report->keys[0] = read_be16(&raw[0]);
    report->keys[1] = read_be16(&raw[2]);
    report->keys[2] = read_be16(&raw[4]);
    report->status = raw[6];
}

bool randnet_keyboard_poll(int port, uint8_t led_request,
                           randnet_keyboard_report_t *report)
{
    uint8_t request[2];
    uint8_t response[7] = {0};

    if (report == NULL)
        return false;

    randnet_keyboard_build_request(led_request, request);
    memcpy(report->request, request, sizeof(report->request));
    joybus_exec_cmd(port, sizeof(request), sizeof(response), request, response);
    randnet_keyboard_decode(response, report);
    return true;
}

void randnet_keyboard_build_request(uint8_t led_request, uint8_t request[2])
{
    request[0] = RANDNET_KEYBOARD_COMMAND;
    request[1] = (uint8_t)(led_request & 0x07u);
}

const char *randnet_keyboard_key_name(uint16_t matrix_value)
{
    switch (matrix_value) {
    case 0x0000: return "(none)";
    case 0x0D07: return "A";
    case 0x0E01: return "Left Shift";
    case 0x0E06: return "Right Shift";
    case 0x0F05: return "Caps Lock";
    default: return "(matrix key)";
    }
}
