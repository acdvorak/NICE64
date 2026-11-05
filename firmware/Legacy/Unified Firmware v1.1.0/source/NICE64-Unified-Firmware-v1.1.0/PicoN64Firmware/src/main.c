#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>

#include "boot_selector.h"
#include "bridge_service.h"
#include "controller_state.h"
#include "core_transport.h"
#include "joybus.h"
#include "n64_accessory.h"
#include "n64_controller.h"
#include "network_pak.h"
#include "nice64_tcp_service.h"
#include "nice64_udp_service.h"
#include "pico/multicore.h"
#include "pico/stdlib.h"
#include "pico/time.h"
#include "role_manager.h"
#include "standalone_diagnostics.h"
#include "wifi_service.h"
#include "wifi_persistence.h"

#ifndef NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT
#define NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT 0
#endif

#if !NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT
#define PICO_LED_PIN 25
#endif
#define CONTROLLER_PAK_BLOCK_SIZE 32

#ifndef NICE64_BUILD_ROLE_STANDALONE
#define NICE64_BUILD_ROLE_STANDALONE 0
#endif

#ifndef NICE64_STANDALONE_SERVICE_NETWORK
#define NICE64_STANDALONE_SERVICE_NETWORK 0
#endif

#ifndef NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS
#define NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS 0
#endif

#ifndef NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK
#define NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK 0
#endif

#define NICE64_UNIVERSAL_ANY \
    (NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS || NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK)

static volatile uint32_t phase2b_canary2_state = 0;

static uint32_t __attribute__((noinline, used)) phase2b_canary2_identity(uint32_t value) {
    return value ^ 0x2B2B2B2Bu;
}

static void __attribute__((noreturn)) core1_entry(void) {
    wifi_service_init();
    nice64_tcp_service_init();
    nice64_udp_service_init();

    while (true) {
        bridge_personality_line_handler_fn tcp_handler = bridge_service_handle_line;
#if NICE64_UNIVERSAL_ANY
        const nice64_active_mode_t mode = role_manager_active_mode();
        if (mode.role == NICE64_ROLE_STANDALONE &&
            mode.standalone_service == NICE64_SERVICE_DIAGNOSTICS) {
            standalone_diagnostics_core1_service();
            tcp_handler = standalone_diagnostics_handle_line;
        } else if (mode.role == NICE64_ROLE_STANDALONE &&
                   mode.standalone_service == NICE64_SERVICE_NETWORK) {
            network_pak_core1_service();
            tcp_handler = network_pak_handle_bridge_line;
        } else {
            bridge_service_poll();
            controller_state_service();
        }
#else
#if NICE64_BUILD_ROLE_STANDALONE
#if NICE64_STANDALONE_SERVICE_NETWORK
        network_pak_core1_service();
        tcp_handler = network_pak_handle_bridge_line;
#else
        standalone_diagnostics_core1_service();
        tcp_handler = standalone_diagnostics_handle_line;
#endif
#else
        bridge_service_poll();
        controller_state_service();
#endif
#endif
        wifi_service_poll();
        nice64_tcp_service_poll(tcp_handler);
        nice64_udp_service_poll();
        tight_loop_contents();
    }
}

static bool elapsed_us(uint32_t start, uint32_t us) {
    return (uint32_t)(time_us_32() - start) >= us;
}

static void phase3b_core0_service_request(void) {
    uint32_t request_generation = 0;
    core_transport_request_t request;
    if (!core_transport_core0_take_request(&request, &request_generation)) {
        return;
    }

    core_transport_response_t response;
    core_transport_response_init(&response, request_generation, 1u);

    if (!core_transport_request_is_valid(&request, request_generation)) {
        core_transport_core0_publish_response(&response);
        return;
    }

    switch ((core_transport_request_command_t)request.command) {
    case CORE_TRANSPORT_REQUEST_SET_ACCESSORY_MODE:
        n64_accessory_set_mode((n64_accessory_mode_t)request.arg0);
        response.status = 0u;
        break;
    case CORE_TRANSPORT_REQUEST_CLEAR_MEMORY:
        n64_accessory_clear_memory();
        response.status = 0u;
        break;
    case CORE_TRANSPORT_REQUEST_CLEAR_TRANSFER_ROM:
        n64_accessory_clear_transfer_rom();
        response.status = 0u;
        break;
    case CORE_TRANSPORT_REQUEST_SET_TRANSFER_PROFILE:
        n64_accessory_set_transfer_profile((uint8_t)request.arg0,
                                           request.arg1,
                                           request.arg2,
                                           (uint8_t)request.length);
        response.status = 0u;
        break;
    case CORE_TRANSPORT_REQUEST_RUMBLE_QUERY:
        response.value0 = n64_accessory_rumble_active() ? 1u : 0u;
        response.status = 0u;
        break;
    case CORE_TRANSPORT_REQUEST_TRANSFER_FLASH_CAPACITY_QUERY:
        response.value0 = n64_accessory_transfer_flash_capacity();
        response.status = 0u;
        break;
    case CORE_TRANSPORT_REQUEST_WRITE_TRANSFER_SAVE_BLOCK:
        if (request.length == CONTROLLER_PAK_BLOCK_SIZE) {
            n64_accessory_write_transfer_save_block((uint16_t)request.arg0, request.data);
            response.status = 0u;
        }
        break;
    case CORE_TRANSPORT_REQUEST_READ_TRANSFER_SAVE_BLOCK:
        n64_accessory_read_transfer_save_block((uint16_t)request.arg0, response.data);
        response.length = CONTROLLER_PAK_BLOCK_SIZE;
        response.status = 0u;
        break;
    case CORE_TRANSPORT_REQUEST_WRITE_TRANSFER_ROM_BLOCK:
        if (request.length == CONTROLLER_PAK_BLOCK_SIZE) {
            n64_accessory_write_transfer_rom_block((uint16_t)request.arg0, request.data);
            response.status = 0u;
        }
        break;
    case CORE_TRANSPORT_REQUEST_BEGIN_TRANSFER_FLASH:
        n64_accessory_begin_transfer_flash(request.arg0);
        response.status = 0u;
        break;
    case CORE_TRANSPORT_REQUEST_WRITE_TRANSFER_FLASH_BLOCK:
        if (request.length == CONTROLLER_PAK_BLOCK_SIZE) {
            n64_accessory_write_transfer_flash_block(request.arg0, request.data);
            response.status = 0u;
        }
        break;
    case CORE_TRANSPORT_REQUEST_WRITE_TRANSFER_FLASH_PAGE:
        if (request.length == CORE_TRANSPORT_MAILBOX_DATA_SIZE) {
            n64_accessory_write_transfer_flash_page(request.arg0, request.data);
            response.status = 0u;
        }
        break;
    case CORE_TRANSPORT_REQUEST_COMMIT_TRANSFER_FLASH:
        n64_accessory_commit_transfer_flash();
        response.status = 0u;
        break;
    case CORE_TRANSPORT_REQUEST_WRITE_MEMORY_BLOCK:
        if (request.length == CONTROLLER_PAK_BLOCK_SIZE) {
            n64_accessory_write_block((uint16_t)request.arg0, request.data);
            response.status = 0u;
        }
        break;
    case CORE_TRANSPORT_REQUEST_READ_MEMORY_BLOCK:
        n64_accessory_read_block((uint16_t)request.arg0, response.data);
        response.length = CONTROLLER_PAK_BLOCK_SIZE;
        response.status = 0u;
        break;
    case CORE_TRANSPORT_REQUEST_STAT_SNAPSHOT:
        joybus_get_debug(&response.debug);
        response.value0 = (uint32_t)n64_accessory_mode();
        response.value1 = n64_accessory_rumble_active() ? 1u : 0u;
        response.status = 0u;
        break;
    case CORE_TRANSPORT_REQUEST_CLEAR_JOYBUS_DEBUG:
        joybus_clear_debug();
        response.status = 0u;
        break;
    default:
        break;
    }

    core_transport_core0_publish_response(&response);
}

static nice64_active_mode_t controller_mode(void) {
    const nice64_active_mode_t mode = {
        .role = NICE64_ROLE_CONTROLLER,
        .standalone_service = NICE64_SERVICE_NONE,
    };
    return mode;
}

#if NICE64_UNIVERSAL_ANY
static bool universal_mode_supported(nice64_active_mode_t mode) {
    if (mode.role == NICE64_ROLE_CONTROLLER) {
        return mode.standalone_service == NICE64_SERVICE_NONE;
    }

#if NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK
    return mode.role == NICE64_ROLE_STANDALONE &&
           (mode.standalone_service == NICE64_SERVICE_DIAGNOSTICS ||
            mode.standalone_service == NICE64_SERVICE_NETWORK);
#else
    return mode.role == NICE64_ROLE_STANDALONE &&
           mode.standalone_service == NICE64_SERVICE_DIAGNOSTICS;
#endif
}

static nice64_active_mode_t universal_boot_mode(bool *consume_selector_after_init) {
    *consume_selector_after_init = false;

    const boot_selector_record_t record = boot_selector_read();
    if (record.status != BOOT_SELECTOR_STATUS_VALID) {
        if (record.status == BOOT_SELECTOR_STATUS_ATTEMPTS_EXCEEDED) {
            boot_selector_clear();
        }
        return controller_mode();
    }

    if (!universal_mode_supported(record.mode)) {
        boot_selector_clear();
        return controller_mode();
    }

    if (boot_selector_mark_attempt() != BOOT_SELECTOR_STATUS_VALID) {
        return controller_mode();
    }

    *consume_selector_after_init = true;
    return record.mode;
}
#endif


int main(void) {
    stdio_init_all();
    wifi_persistence_prepare_core0_lockout();
#if !NICE64_UNIVERSAL_ANY
    boot_selector_boot_observe();
#endif
    phase2b_canary2_state = phase2b_canary2_identity(0);
    role_manager_init();
#if NICE64_UNIVERSAL_ANY
    bool consume_selector_after_init = false;
    const nice64_active_mode_t active_mode =
        universal_boot_mode(&consume_selector_after_init);
    if (!role_manager_set_active_mode(active_mode)) {
        role_manager_set_active_mode(controller_mode());
    }
#endif
    if (!role_manager_is_controller() && !role_manager_is_standalone()) {
        role_manager_init();
    }

#if !NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT
    gpio_init(PICO_LED_PIN);
    gpio_set_dir(PICO_LED_PIN, GPIO_OUT);
    gpio_put(PICO_LED_PIN, 0);
#endif

#if NICE64_UNIVERSAL_ANY
    if (role_manager_is_standalone()) {
        const nice64_active_mode_t mode = role_manager_active_mode();
        if (mode.standalone_service == NICE64_SERVICE_NETWORK) {
            network_pak_init();
            joybus_init();
        } else {
            standalone_diagnostics_init();
        }
        multicore_launch_core1(core1_entry);
        if (consume_selector_after_init) {
            boot_selector_clear();
        }

        sleep_ms(500);
        if (mode.standalone_service == NICE64_SERVICE_NETWORK) {
            puts("Pico N64 standalone Network Pak starting");
        } else {
            standalone_diagnostics_write_banner();
        }

#if !NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT
        uint32_t last_led_us = time_us_32();
#endif
        while (true) {
            if (mode.standalone_service == NICE64_SERVICE_DIAGNOSTICS) {
                standalone_diagnostics_core0_service();
            }

#if !NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT
            if (elapsed_us(last_led_us, 250000)) {
                gpio_put(PICO_LED_PIN, !gpio_get(PICO_LED_PIN));
                last_led_us = time_us_32();
            }
#endif

            tight_loop_contents();
        }
    }

    n64_controller_init();
    controller_state_init();
    core_transport_init();
    n64_accessory_init();
    joybus_init();
    multicore_launch_core1(core1_entry);
    if (consume_selector_after_init) {
        boot_selector_clear();
    }

    sleep_ms(500);
    puts("Pico N64 RC-reference controller bridge starting in MODE SERIAL");

#if !NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT
    uint32_t last_led_us = time_us_32();
#endif
    while (true) {
        controller_state_poll_validation();
        phase3b_core0_service_request();

#if !NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT
        if (elapsed_us(last_led_us, 250000)) {
            gpio_put(PICO_LED_PIN, !gpio_get(PICO_LED_PIN));
            last_led_us = time_us_32();
        }
#endif

        tight_loop_contents();
    }
#else
#if NICE64_BUILD_ROLE_STANDALONE
#if NICE64_STANDALONE_SERVICE_NETWORK
    network_pak_init();
    joybus_init();
    multicore_launch_core1(core1_entry);

    sleep_ms(500);
    puts("Pico N64 standalone Network Pak starting");

#if !NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT
    uint32_t last_led_us = time_us_32();
#endif
    while (true) {
#if !NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT
        if (elapsed_us(last_led_us, 250000)) {
            gpio_put(PICO_LED_PIN, !gpio_get(PICO_LED_PIN));
            last_led_us = time_us_32();
        }
#endif

        tight_loop_contents();
    }
#else
    standalone_diagnostics_init();
    multicore_launch_core1(core1_entry);

    sleep_ms(500);
    standalone_diagnostics_write_banner();

#if !NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT
    uint32_t last_led_us = time_us_32();
#endif
    while (true) {
        standalone_diagnostics_core0_service();

#if !NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT
        if (elapsed_us(last_led_us, 250000)) {
            gpio_put(PICO_LED_PIN, !gpio_get(PICO_LED_PIN));
            last_led_us = time_us_32();
        }
#endif

        tight_loop_contents();
    }
#endif
#else
    n64_controller_init();
    controller_state_init();
    core_transport_init();
    n64_accessory_init();
    joybus_init();
    multicore_launch_core1(core1_entry);

    sleep_ms(500);
    puts("Pico N64 RC-reference controller bridge starting in MODE SERIAL");

#if !NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT
    uint32_t last_led_us = time_us_32();
#endif
    while (true) {
        controller_state_poll_validation();
        phase3b_core0_service_request();

#if !NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT
        if (elapsed_us(last_led_us, 250000)) {
            gpio_put(PICO_LED_PIN, !gpio_get(PICO_LED_PIN));
            last_led_us = time_us_32();
        }
#endif

        tight_loop_contents();
    }
#endif
#endif
}
