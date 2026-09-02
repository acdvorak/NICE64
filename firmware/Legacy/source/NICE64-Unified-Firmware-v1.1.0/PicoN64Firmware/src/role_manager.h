#ifndef ROLE_MANAGER_H
#define ROLE_MANAGER_H

#include <stdbool.h>

typedef enum {
    NICE64_ROLE_CONTROLLER = 0,
    NICE64_ROLE_STANDALONE = 1,
} nice64_role_t;

typedef enum {
    NICE64_SERVICE_NONE = 0,
    NICE64_SERVICE_DIAGNOSTICS = 1,
    NICE64_SERVICE_NETWORK = 2,
    NICE64_SERVICE_VRU = 3,
    NICE64_SERVICE_STORAGE = 4,
} nice64_standalone_service_t;

typedef struct {
    nice64_role_t role;
    nice64_standalone_service_t standalone_service;
} nice64_active_mode_t;

void role_manager_init(void);
bool role_manager_set_active_mode(nice64_active_mode_t mode);
nice64_role_t role_manager_current(void);
bool role_manager_is_controller(void);
bool role_manager_is_standalone(void);
nice64_active_mode_t role_manager_active_mode(void);
bool role_manager_role_is_valid(nice64_role_t role);
bool role_manager_standalone_service_is_valid(nice64_standalone_service_t service);
bool role_manager_active_mode_is_valid(nice64_active_mode_t mode);
const char *role_manager_role_name(nice64_role_t role);
const char *role_manager_standalone_service_name(nice64_standalone_service_t service);
const char *role_manager_active_mode_name(nice64_active_mode_t mode);

#endif
