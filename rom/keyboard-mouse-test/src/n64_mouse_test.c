#include "n64_mouse_test.h"

#include <libdragon.h>
#include <stddef.h>
#include <string.h>

void n64_mouse_test_decode(const uint8_t raw[4], n64_mouse_report_t *report)
{
    memcpy(report->raw, raw, sizeof(report->raw));
    report->left = (raw[0] & 0x80u) != 0;
    report->right = (raw[0] & 0x40u) != 0;
    report->dx = (int8_t)raw[2];
    report->dy = (int8_t)raw[3];
}

bool n64_mouse_test_poll(int port, n64_mouse_report_t *report)
{
    const uint8_t request = N64_MOUSE_COMMAND;
    uint8_t response[4] = {0};

    if (report == NULL)
        return false;

    joybus_exec_cmd(port, sizeof(request), sizeof(response), &request, response);
    n64_mouse_test_decode(response, report);
    return true;
}
