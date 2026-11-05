#ifndef TEST_LIBDRAGON_H
#define TEST_LIBDRAGON_H

#include <stddef.h>

void joybus_exec_cmd(int port, size_t send_len, size_t recv_len,
                     const void *send_data, void *recv_data);

#endif
