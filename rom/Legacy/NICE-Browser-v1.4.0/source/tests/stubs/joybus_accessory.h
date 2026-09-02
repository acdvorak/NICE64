#ifndef CHECKPOINT_E_STUB_JOYBUS_ACCESSORY_H
#define CHECKPOINT_E_STUB_JOYBUS_ACCESSORY_H

#include <stdint.h>

enum
{
    JOYBUS_ACCESSORY_IO_STATUS_OK = 0,
    JOYBUS_ACCESSORY_IO_STATUS_NO_DEVICE = -1,
    JOYBUS_ACCESSORY_IO_STATUS_NO_PAK = -2,
    JOYBUS_ACCESSORY_IO_STATUS_BAD_CRC = -3
};

int joybus_accessory_read(int port, uint16_t addr, uint8_t *data);
int joybus_accessory_write(int port, uint16_t addr, const uint8_t *data);

#endif
