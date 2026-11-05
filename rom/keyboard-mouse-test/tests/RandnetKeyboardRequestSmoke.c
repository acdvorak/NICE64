#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "randnet_keyboard.h"

static int captured_port;
static size_t captured_send_len;
static size_t captured_recv_len;
static uint8_t captured_request[2];

void joybus_exec_cmd(int port, size_t send_len, size_t recv_len,
                     const void *send_data, void *recv_data)
{
    static const uint8_t reply[7] = { 0x0D, 0x07, 0, 0, 0, 0, 0 };
    captured_port = port;
    captured_send_len = send_len;
    captured_recv_len = recv_len;
    memcpy(captured_request, send_data, sizeof(captured_request));
    memcpy(recv_data, reply, sizeof(reply));
}

int main(void)
{
    randnet_keyboard_report_t report = {0};
    assert(randnet_keyboard_poll(2, 0xFFu, &report));
    assert(captured_port == 2);
    assert(captured_send_len == 2u);
    assert(captured_recv_len == 7u);
    assert(captured_request[0] == 0x13u);
    assert(captured_request[1] == 0x07u);
    assert(report.request[0] == 0x13u);
    assert(report.request[1] == 0x07u);
    assert(report.keys[0] == 0x0D07u);
    puts("RandnetKeyboardRequestSmoke: PASS");
    return 0;
}
