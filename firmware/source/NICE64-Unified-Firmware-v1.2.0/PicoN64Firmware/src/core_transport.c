#include "core_transport.h"

#include <string.h>

#include "pico/stdlib.h"

#define CORE_TRANSPORT_REQUEST_PATTERN 0x43305251u
#define CORE_TRANSPORT_RESPONSE_PATTERN 0x43305253u

_Static_assert(CORE_TRANSPORT_MAILBOX_DATA_SIZE >= 256u,
               "Core 0 mailbox must carry one Transfer Pak flash page");

static core_transport_request_t core_transport_request_slot;
static core_transport_response_t core_transport_response_slot;
static volatile uint32_t core_transport_request_generation = 0;
static volatile uint32_t core_transport_response_generation = 0;

void core_transport_init(void) {
    core_transport_request_slot = (core_transport_request_t){0};
    core_transport_response_slot = (core_transport_response_t){0};
    __atomic_thread_fence(__ATOMIC_RELEASE);
    __atomic_store_n(&core_transport_request_generation, 0u, __ATOMIC_RELEASE);
    __atomic_store_n(&core_transport_response_generation, 0u, __ATOMIC_RELEASE);
}

bool core_transport_core0_take_request(core_transport_request_t *request,
                                       uint32_t *request_generation) {
    static uint32_t last_generation = 0;

    const uint32_t generation =
        __atomic_load_n(&core_transport_request_generation, __ATOMIC_ACQUIRE);
    if (generation == last_generation) {
        return false;
    }

    *request = core_transport_request_slot;
    *request_generation = generation;
    last_generation = generation;
    return true;
}

bool core_transport_request_is_valid(const core_transport_request_t *request,
                                     uint32_t request_generation) {
    return request->generation == request_generation &&
           request->validation_pattern == (request->generation ^ CORE_TRANSPORT_REQUEST_PATTERN);
}

void core_transport_response_init(core_transport_response_t *response,
                                  uint32_t generation,
                                  uint32_t status) {
    *response = (core_transport_response_t){0};
    response->generation = generation;
    response->status = status;
    response->validation_pattern = generation ^ CORE_TRANSPORT_RESPONSE_PATTERN;
}

void core_transport_core0_publish_response(const core_transport_response_t *response) {
    core_transport_response_slot = *response;
    __atomic_thread_fence(__ATOMIC_RELEASE);
    __atomic_store_n(&core_transport_response_generation,
                     response->generation,
                     __ATOMIC_RELEASE);
}

bool core_transport_core1_call_core0(core_transport_request_t *request,
                                     core_transport_response_t *response,
                                     core_transport_wait_service_t wait_service) {
    static uint32_t next_generation = 0;

    const uint32_t generation = ++next_generation;
    request->generation = generation;
    request->validation_pattern = generation ^ CORE_TRANSPORT_REQUEST_PATTERN;

    core_transport_request_slot = *request;
    __atomic_thread_fence(__ATOMIC_RELEASE);
    __atomic_store_n(&core_transport_request_generation, generation, __ATOMIC_RELEASE);

    while (__atomic_load_n(&core_transport_response_generation, __ATOMIC_ACQUIRE) != generation) {
        if (wait_service != NULL) {
            wait_service();
        }
        tight_loop_contents();
    }

    const core_transport_response_t local_response = core_transport_response_slot;
    if (local_response.generation != generation ||
        local_response.validation_pattern != (generation ^ CORE_TRANSPORT_RESPONSE_PATTERN)) {
        return false;
    }

    *response = local_response;
    return local_response.status == 0u;
}

bool core_transport_core1_simple_request(core_transport_request_command_t command,
                                         uint32_t arg0,
                                         uint32_t arg1,
                                         uint32_t arg2,
                                         uint32_t length,
                                         const uint8_t *data,
                                         core_transport_response_t *response,
                                         core_transport_wait_service_t wait_service) {
    core_transport_request_t request = {0};
    request.command = (uint32_t)command;
    request.arg0 = arg0;
    request.arg1 = arg1;
    request.arg2 = arg2;
    request.length = length;
    if (data != NULL && length <= CORE_TRANSPORT_MAILBOX_DATA_SIZE) {
        memcpy(request.data, data, length);
    }

    return core_transport_core1_call_core0(&request, response, wait_service);
}
