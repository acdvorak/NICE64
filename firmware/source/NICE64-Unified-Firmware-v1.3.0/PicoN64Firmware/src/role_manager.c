#include "role_manager.h"

#include "pico/platform/compiler.h"
#include "pico/platform/sections.h"

#ifndef NICE64_BUILD_ROLE_STANDALONE
#define NICE64_BUILD_ROLE_STANDALONE 0
#endif

#ifndef NICE64_STANDALONE_SERVICE_NETWORK
#define NICE64_STANDALONE_SERVICE_NETWORK 0
#endif

#define NICE64_BUILD_DEFAULT_ROLE \
    (NICE64_BUILD_ROLE_STANDALONE ? NICE64_ROLE_STANDALONE : NICE64_ROLE_CONTROLLER)

#define NICE64_BUILD_DEFAULT_STANDALONE_SERVICE \
    (NICE64_BUILD_ROLE_STANDALONE ? \
        (NICE64_STANDALONE_SERVICE_NETWORK ? \
            NICE64_SERVICE_NETWORK : NICE64_SERVICE_DIAGNOSTICS) : \
        NICE64_SERVICE_NONE)

static volatile nice64_role_t current_role = NICE64_BUILD_DEFAULT_ROLE;
static volatile nice64_standalone_service_t current_standalone_service =
    NICE64_BUILD_DEFAULT_STANDALONE_SERVICE;
static volatile nice64_keyboard_mouse_device_t current_keyboard_mouse_device =
    NICE64_KEYBOARD_MOUSE_KEYBOARD;

void __attribute__((noinline)) role_manager_init(void) {
    current_role = NICE64_BUILD_DEFAULT_ROLE;
    current_standalone_service = NICE64_BUILD_DEFAULT_STANDALONE_SERVICE;
    current_keyboard_mouse_device = NICE64_KEYBOARD_MOUSE_KEYBOARD;
}

bool __attribute__((noinline)) role_manager_set_active_mode(nice64_active_mode_t mode) {
    if (!role_manager_active_mode_is_valid(mode)) {
        return false;
    }

    current_role = mode.role;
    current_standalone_service = mode.standalone_service;
    return true;
}

nice64_role_t __no_inline_not_in_flash_func(role_manager_current)(void) {
    if (current_role != NICE64_ROLE_CONTROLLER && current_role != NICE64_ROLE_STANDALONE) {
        return NICE64_BUILD_DEFAULT_ROLE;
    }
    return current_role;
}

bool __attribute__((noinline)) role_manager_is_controller(void) {
    return role_manager_current() == NICE64_ROLE_CONTROLLER;
}

bool __attribute__((noinline)) role_manager_is_standalone(void) {
    return role_manager_current() == NICE64_ROLE_STANDALONE;
}

nice64_active_mode_t __no_inline_not_in_flash_func(role_manager_active_mode)(void) {
    nice64_active_mode_t mode = {
        .role = role_manager_current(),
        .standalone_service = current_standalone_service,
    };

    if (mode.role == NICE64_ROLE_CONTROLLER) {
        mode.standalone_service = NICE64_SERVICE_NONE;
    }

    if (!role_manager_active_mode_is_valid(mode)) {
        mode.role = NICE64_ROLE_CONTROLLER;
        mode.standalone_service = NICE64_SERVICE_NONE;
    }

    return mode;
}

bool role_manager_role_is_valid(nice64_role_t role) {
    return role == NICE64_ROLE_CONTROLLER || role == NICE64_ROLE_STANDALONE;
}

bool role_manager_standalone_service_is_valid(nice64_standalone_service_t service) {
    return service == NICE64_SERVICE_NONE ||
           service == NICE64_SERVICE_DIAGNOSTICS ||
           service == NICE64_SERVICE_NETWORK ||
           service == NICE64_SERVICE_KEYBOARD_MOUSE;
}

bool role_manager_active_mode_is_valid(nice64_active_mode_t mode) {
    if (!role_manager_role_is_valid(mode.role) ||
        !role_manager_standalone_service_is_valid(mode.standalone_service)) {
        return false;
    }

    if (mode.role == NICE64_ROLE_CONTROLLER) {
        return mode.standalone_service == NICE64_SERVICE_NONE;
    }

    return mode.standalone_service == NICE64_SERVICE_DIAGNOSTICS ||
           mode.standalone_service == NICE64_SERVICE_NETWORK ||
           mode.standalone_service == NICE64_SERVICE_KEYBOARD_MOUSE;
}

bool role_manager_set_keyboard_mouse_device(nice64_keyboard_mouse_device_t device) {
    if (!role_manager_keyboard_mouse_device_is_valid(device)) {
        return false;
    }
    current_keyboard_mouse_device = device;
    return true;
}

nice64_keyboard_mouse_device_t
__no_inline_not_in_flash_func(role_manager_keyboard_mouse_device)(void) {
    const nice64_keyboard_mouse_device_t device = current_keyboard_mouse_device;
    if (!role_manager_keyboard_mouse_device_is_valid(device)) {
        return NICE64_KEYBOARD_MOUSE_KEYBOARD;
    }
    return device;
}

bool role_manager_keyboard_mouse_device_is_valid(nice64_keyboard_mouse_device_t device) {
    return device == NICE64_KEYBOARD_MOUSE_KEYBOARD ||
           device == NICE64_KEYBOARD_MOUSE_MOUSE;
}

const char *role_manager_role_name(nice64_role_t role) {
    switch (role) {
    case NICE64_ROLE_CONTROLLER:
        return "CONTROLLER";
    case NICE64_ROLE_STANDALONE:
        return "STANDALONE";
    default:
        return "INVALID";
    }
}

const char *role_manager_standalone_service_name(nice64_standalone_service_t service) {
    switch (service) {
    case NICE64_SERVICE_NONE:
        return "NONE";
    case NICE64_SERVICE_DIAGNOSTICS:
        return "DIAGNOSTICS";
    case NICE64_SERVICE_NETWORK:
        return "NETWORK";
    case NICE64_SERVICE_VRU:
        return "VRU";
    case NICE64_SERVICE_STORAGE:
        return "STORAGE";
    case NICE64_SERVICE_KEYBOARD_MOUSE:
        return "KEYBOARD_MOUSE";
    default:
        return "INVALID";
    }
}

const char *role_manager_active_mode_name(nice64_active_mode_t mode) {
    if (mode.role == NICE64_ROLE_CONTROLLER &&
        mode.standalone_service == NICE64_SERVICE_NONE) {
        return "CONTROLLER";
    }

    if (mode.role == NICE64_ROLE_STANDALONE &&
        mode.standalone_service == NICE64_SERVICE_DIAGNOSTICS) {
        return "STANDALONE/DIAGNOSTICS";
    }

    if (mode.role == NICE64_ROLE_STANDALONE &&
        mode.standalone_service == NICE64_SERVICE_NETWORK) {
        return "STANDALONE/NETWORK";
    }

    if (mode.role == NICE64_ROLE_STANDALONE &&
        mode.standalone_service == NICE64_SERVICE_KEYBOARD_MOUSE) {
        return role_manager_keyboard_mouse_device() == NICE64_KEYBOARD_MOUSE_MOUSE
            ? "STANDALONE/KEYBOARD_MOUSE/MOUSE"
            : "STANDALONE/KEYBOARD_MOUSE/KEYBOARD";
    }

    return "INVALID";
}

const char *role_manager_keyboard_mouse_device_name(nice64_keyboard_mouse_device_t device) {
    switch (device) {
    case NICE64_KEYBOARD_MOUSE_KEYBOARD:
        return "KEYBOARD";
    case NICE64_KEYBOARD_MOUSE_MOUSE:
        return "MOUSE";
    default:
        return "INVALID";
    }
}
