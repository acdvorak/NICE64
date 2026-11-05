#include "control_plane.h"

#include <string.h>

#include "boot_selector.h"
#include "firmware_version.h"
#include "role_manager.h"

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

#define NICE64_CONTROL_PLANE_VERSION 1
#define NICE64_FIRMWARE_ID "TIMING_CORE_V2"
#define NICE64_BOOTREQ_REBOOT_DELAY_MS 100u
#define NICE64_BOOT_FLAG_KEYBOARD_MOUSE_MOUSE 0x01u

static const char *control_plane_current_capability(void) {
#if NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK
    return "CONTROLLER|STANDALONE/DIAGNOSTICS|STANDALONE/NETWORK|STANDALONE/KEYBOARD_MOUSE/KEYBOARD|STANDALONE/KEYBOARD_MOUSE/MOUSE";
#elif NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS
    return "CONTROLLER|STANDALONE/DIAGNOSTICS";
#else
#if NICE64_BUILD_ROLE_STANDALONE
#if NICE64_STANDALONE_SERVICE_NETWORK
    return "STANDALONE/NETWORK";
#else
    return "STANDALONE/DIAGNOSTICS";
#endif
#else
    return "CONTROLLER";
#endif
#endif
}

static void control_plane_print_bootreq(const nice64_response_sink_t *response) {
    const boot_selector_record_t record = boot_selector_read();
    nice64_response_printf(response,
           "BOOTREQ|%s|ROLE=%s|SERVICE=%s|FLAGS=0x%02X|SEQ=%u|ATTEMPT=%u|RAW=%08lX,%08lX,%08lX,%08lX\n",
           boot_selector_status_name(record.status),
           role_manager_role_name(record.mode.role),
           role_manager_standalone_service_name(record.mode.standalone_service),
           (unsigned)record.flags,
           (unsigned)record.sequence,
           (unsigned)record.attempt_count,
           (unsigned long)record.raw[0],
           (unsigned long)record.raw[1],
           (unsigned long)record.raw[2],
           (unsigned long)record.raw[3]);
}

static void control_plane_write_bootreq(nice64_role_t role,
                                        nice64_standalone_service_t service,
                                        uint8_t flags,
                                        const nice64_response_sink_t *response) {
    const nice64_active_mode_t mode = {
        .role = role,
        .standalone_service = service,
    };

    if (boot_selector_write(mode, flags)) {
        nice64_response_puts(response, "OK BOOTREQ TEST");
    } else {
        nice64_response_puts(response, "ERR BOOTREQ MODE");
    }
}

#if NICE64_UNIVERSAL_ANY
static bool control_plane_universal_mode_supported(nice64_active_mode_t mode) {
    if (mode.role == NICE64_ROLE_CONTROLLER) {
        return mode.standalone_service == NICE64_SERVICE_NONE;
    }

#if NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK
    return mode.role == NICE64_ROLE_STANDALONE &&
           (mode.standalone_service == NICE64_SERVICE_DIAGNOSTICS ||
            mode.standalone_service == NICE64_SERVICE_NETWORK ||
            mode.standalone_service == NICE64_SERVICE_KEYBOARD_MOUSE);
#else
    return mode.role == NICE64_ROLE_STANDALONE &&
           mode.standalone_service == NICE64_SERVICE_DIAGNOSTICS;
#endif
}

static void control_plane_request_mode(nice64_active_mode_t requested,
                                       uint8_t flags,
                                       const nice64_response_sink_t *response) {
    if (!control_plane_universal_mode_supported(requested)) {
        nice64_response_puts(response, "REQUEST_MODE|ERR|UNSUPPORTED");
        return;
    }

    const nice64_active_mode_t current = role_manager_active_mode();
    const bool same_keyboard_mouse_device =
        requested.standalone_service != NICE64_SERVICE_KEYBOARD_MOUSE ||
        role_manager_keyboard_mouse_device() ==
            (((flags & NICE64_BOOT_FLAG_KEYBOARD_MOUSE_MOUSE) != 0u)
                ? NICE64_KEYBOARD_MOUSE_MOUSE
                : NICE64_KEYBOARD_MOUSE_KEYBOARD);
    if (current.role == requested.role &&
        current.standalone_service == requested.standalone_service &&
        same_keyboard_mouse_device) {
        if (current.standalone_service == NICE64_SERVICE_KEYBOARD_MOUSE) {
            nice64_response_printf(response,
                   "REQUEST_MODE|ALREADY_ACTIVE|%s|%s|%s\n",
                   role_manager_role_name(current.role),
                   role_manager_standalone_service_name(current.standalone_service),
                   role_manager_keyboard_mouse_device_name(
                       role_manager_keyboard_mouse_device()));
        } else {
            nice64_response_printf(response,
                   "REQUEST_MODE|ALREADY_ACTIVE|%s|%s\n",
                   role_manager_role_name(current.role),
                   role_manager_standalone_service_name(current.standalone_service));
        }
        return;
    }

    if (!boot_selector_write(requested, flags)) {
        nice64_response_puts(response, "REQUEST_MODE|ERR|WRITE_FAILED");
        return;
    }

    if (requested.standalone_service == NICE64_SERVICE_KEYBOARD_MOUSE) {
        nice64_response_printf(response, "REQUEST_MODE|ACK|%s|%s|%s\n",
               role_manager_role_name(requested.role),
               role_manager_standalone_service_name(requested.standalone_service),
               (flags & NICE64_BOOT_FLAG_KEYBOARD_MOUSE_MOUSE) != 0u
                   ? "MOUSE" : "KEYBOARD");
    } else {
        nice64_response_printf(response, "REQUEST_MODE|ACK|%s|%s\n",
               role_manager_role_name(requested.role),
               role_manager_standalone_service_name(requested.standalone_service));
    }
    nice64_response_flush(response);
    boot_selector_reboot(NICE64_BOOTREQ_REBOOT_DELAY_MS);
}
#endif

bool control_plane_try_handle_line(const char *line,
                                   const nice64_response_sink_t *response) {
    if (line == NULL || response == NULL || response->write == NULL) {
        return false;
    }

    if (strcmp(line, "WHO") == 0) {
        nice64_response_printf(response, "WHO|NICE64|CTRL=%u|FW=%s\n",
               (unsigned)NICE64_CONTROL_PLANE_VERSION,
               NICE64_FIRMWARE_ID);
        return true;
    }

    if (strcmp(line, "VERSION?") == 0) {
        nice64_response_printf(response,
               "VERSION|NICE64|PRODUCT=%s|VERSION=%s|CTRL=%u\n",
               NICE64_FIRMWARE_PRODUCT,
               NICE64_FIRMWARE_VERSION_STRING,
               (unsigned)NICE64_CONTROL_PLANE_VERSION);
        return true;
    }

    if (strcmp(line, "MODE?") == 0) {
        const nice64_active_mode_t mode = role_manager_active_mode();
        if (mode.role == NICE64_ROLE_STANDALONE &&
            mode.standalone_service == NICE64_SERVICE_KEYBOARD_MOUSE) {
            nice64_response_printf(response, "MODE|%s|%s|%s\n",
                   role_manager_role_name(mode.role),
                   role_manager_standalone_service_name(mode.standalone_service),
                   role_manager_keyboard_mouse_device_name(
                       role_manager_keyboard_mouse_device()));
            return true;
        }
        nice64_response_printf(response, "MODE|%s|%s\n",
               role_manager_role_name(mode.role),
               role_manager_standalone_service_name(mode.standalone_service));
        return true;
    }

    if (strcmp(line, "CAPS?") == 0) {
        nice64_response_printf(response, "CAPS|%s\n", control_plane_current_capability());
        return true;
    }

    if (strcmp(line, "BOOTREQ?") == 0) {
        control_plane_print_bootreq(response);
        return true;
    }

    if (strcmp(line, "BOOTREQ TEST CONTROLLER") == 0) {
        control_plane_write_bootreq(NICE64_ROLE_CONTROLLER, NICE64_SERVICE_NONE, 0u, response);
        return true;
    }

    if (strcmp(line, "BOOTREQ TEST DIAGNOSTICS") == 0) {
        control_plane_write_bootreq(NICE64_ROLE_STANDALONE, NICE64_SERVICE_DIAGNOSTICS, 0u, response);
        return true;
    }

    if (strcmp(line, "BOOTREQ TEST NETWORK") == 0) {
        control_plane_write_bootreq(NICE64_ROLE_STANDALONE, NICE64_SERVICE_NETWORK, 0u, response);
        return true;
    }

    if (strcmp(line, "BOOTREQ TEST KEYBOARD") == 0) {
        control_plane_write_bootreq(NICE64_ROLE_STANDALONE,
                                    NICE64_SERVICE_KEYBOARD_MOUSE, 0u, response);
        return true;
    }

    if (strcmp(line, "BOOTREQ TEST MOUSE") == 0) {
        control_plane_write_bootreq(NICE64_ROLE_STANDALONE,
                                    NICE64_SERVICE_KEYBOARD_MOUSE,
                                    NICE64_BOOT_FLAG_KEYBOARD_MOUSE_MOUSE,
                                    response);
        return true;
    }

    if (strcmp(line, "BOOTREQ CLEAR") == 0) {
        boot_selector_clear();
        nice64_response_puts(response, "OK BOOTREQ CLEAR");
        return true;
    }

    if (strcmp(line, "BOOTREQ MARK") == 0) {
        const boot_selector_status_t status = boot_selector_mark_attempt();
        nice64_response_printf(response, "OK BOOTREQ MARK %s\n", boot_selector_status_name(status));
        return true;
    }

    if (strcmp(line, "BOOTREQ CORRUPT MAGIC") == 0) {
        boot_selector_corrupt_magic();
        nice64_response_puts(response, "OK BOOTREQ CORRUPT MAGIC");
        return true;
    }

    if (strcmp(line, "BOOTREQ CORRUPT VERSION") == 0) {
        boot_selector_corrupt_version();
        nice64_response_puts(response, "OK BOOTREQ CORRUPT VERSION");
        return true;
    }

    if (strcmp(line, "BOOTREQ CORRUPT CHECK") == 0) {
        boot_selector_corrupt_check();
        nice64_response_puts(response, "OK BOOTREQ CORRUPT CHECK");
        return true;
    }

    if (strcmp(line, "BOOTREQ REBOOT") == 0) {
        nice64_response_puts(response, "OK BOOTREQ REBOOT");
        nice64_response_flush(response);
        boot_selector_reboot(NICE64_BOOTREQ_REBOOT_DELAY_MS);
        return true;
    }

#if NICE64_UNIVERSAL_ANY
    if (strcmp(line, "REQUEST_MODE CONTROLLER") == 0) {
        const nice64_active_mode_t requested = {
            .role = NICE64_ROLE_CONTROLLER,
            .standalone_service = NICE64_SERVICE_NONE,
        };
        control_plane_request_mode(requested, 0u, response);
        return true;
    }

    if (strcmp(line, "REQUEST_MODE DIAGNOSTICS") == 0) {
        const nice64_active_mode_t requested = {
            .role = NICE64_ROLE_STANDALONE,
            .standalone_service = NICE64_SERVICE_DIAGNOSTICS,
        };
        control_plane_request_mode(requested, 0u, response);
        return true;
    }

#if NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK
    if (strcmp(line, "REQUEST_MODE NETWORK") == 0) {
        const nice64_active_mode_t requested = {
            .role = NICE64_ROLE_STANDALONE,
            .standalone_service = NICE64_SERVICE_NETWORK,
        };
        control_plane_request_mode(requested, 0u, response);
        return true;
    }

    if (strcmp(line, "REQUEST_MODE KEYBOARD") == 0) {
        const nice64_active_mode_t requested = {
            .role = NICE64_ROLE_STANDALONE,
            .standalone_service = NICE64_SERVICE_KEYBOARD_MOUSE,
        };
        control_plane_request_mode(requested, 0u, response);
        return true;
    }

    if (strcmp(line, "REQUEST_MODE MOUSE") == 0) {
        const nice64_active_mode_t requested = {
            .role = NICE64_ROLE_STANDALONE,
            .standalone_service = NICE64_SERVICE_KEYBOARD_MOUSE,
        };
        control_plane_request_mode(requested,
                                   NICE64_BOOT_FLAG_KEYBOARD_MOUSE_MOUSE,
                                   response);
        return true;
    }
#endif

    if (strncmp(line, "REQUEST_MODE ", 13) == 0) {
        nice64_response_puts(response, "REQUEST_MODE|ERR|UNSUPPORTED");
        return true;
    }
#endif

    return false;
}
