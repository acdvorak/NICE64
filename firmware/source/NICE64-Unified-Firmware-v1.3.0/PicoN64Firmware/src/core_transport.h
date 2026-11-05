#ifndef CORE_TRANSPORT_H
#define CORE_TRANSPORT_H

#include <stdbool.h>
#include <stdint.h>

#include "joybus.h"

#define CORE_TRANSPORT_MAILBOX_DATA_SIZE 256u

typedef enum {
    CORE_TRANSPORT_REQUEST_NONE = 0,
    CORE_TRANSPORT_REQUEST_SET_ACCESSORY_MODE,
    CORE_TRANSPORT_REQUEST_CLEAR_MEMORY,
    CORE_TRANSPORT_REQUEST_CLEAR_TRANSFER_ROM,
    CORE_TRANSPORT_REQUEST_SET_TRANSFER_PROFILE,
    CORE_TRANSPORT_REQUEST_RUMBLE_QUERY,
    CORE_TRANSPORT_REQUEST_TRANSFER_FLASH_CAPACITY_QUERY,
    CORE_TRANSPORT_REQUEST_WRITE_TRANSFER_SAVE_BLOCK,
    CORE_TRANSPORT_REQUEST_READ_TRANSFER_SAVE_BLOCK,
    CORE_TRANSPORT_REQUEST_WRITE_TRANSFER_ROM_BLOCK,
    CORE_TRANSPORT_REQUEST_BEGIN_TRANSFER_FLASH,
    CORE_TRANSPORT_REQUEST_WRITE_TRANSFER_FLASH_BLOCK,
    CORE_TRANSPORT_REQUEST_WRITE_TRANSFER_FLASH_PAGE,
    CORE_TRANSPORT_REQUEST_COMMIT_TRANSFER_FLASH,
    CORE_TRANSPORT_REQUEST_WRITE_MEMORY_BLOCK,
    CORE_TRANSPORT_REQUEST_READ_MEMORY_BLOCK,
    CORE_TRANSPORT_REQUEST_STAT_SNAPSHOT,
    CORE_TRANSPORT_REQUEST_CLEAR_JOYBUS_DEBUG,
} core_transport_request_command_t;

typedef struct {
    uint32_t generation;
    uint32_t command;
    uint32_t arg0;
    uint32_t arg1;
    uint32_t arg2;
    uint32_t length;
    uint8_t data[CORE_TRANSPORT_MAILBOX_DATA_SIZE];
    uint32_t validation_pattern;
} core_transport_request_t;

typedef struct {
    uint32_t generation;
    uint32_t status;
    uint32_t value0;
    uint32_t value1;
    uint32_t value2;
    uint32_t length;
    uint8_t data[CORE_TRANSPORT_MAILBOX_DATA_SIZE];
    joybus_debug_t debug;
    uint32_t validation_pattern;
} core_transport_response_t;

typedef void (*core_transport_wait_service_t)(void);

void core_transport_init(void);

bool core_transport_core0_take_request(core_transport_request_t *request,
                                       uint32_t *request_generation);
bool core_transport_request_is_valid(const core_transport_request_t *request,
                                     uint32_t request_generation);
void core_transport_response_init(core_transport_response_t *response,
                                  uint32_t generation,
                                  uint32_t status);
void core_transport_core0_publish_response(const core_transport_response_t *response);

bool core_transport_core1_call_core0(core_transport_request_t *request,
                                     core_transport_response_t *response,
                                     core_transport_wait_service_t wait_service);
bool core_transport_core1_simple_request(core_transport_request_command_t command,
                                         uint32_t arg0,
                                         uint32_t arg1,
                                         uint32_t arg2,
                                         uint32_t length,
                                         const uint8_t *data,
                                         core_transport_response_t *response,
                                         core_transport_wait_service_t wait_service);

#endif
