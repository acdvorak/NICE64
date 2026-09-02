cmake_minimum_required(VERSION 3.13)

include($ENV{PICO_SDK_PATH}/external/pico_sdk_import.cmake)

project(pico_n64_controller_bridge VERSION 1.2.0 LANGUAGES C CXX ASM)
set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(NICE64_TRANSFER_ROM_FLASH_OFFSET_BYTES 1048576)

pico_sdk_init()

add_executable(pico_n64_controller_bridge
    src/main.c
    src/boot_selector.c
    src/bridge_line_dispatch.c
    src/bridge_service.c
    src/control_plane.c
    src/core_transport.c
    src/controller_state.c
    src/joybus.c
    src/n64_accessory.c
    src/n64_controller.c
    src/nice64_line_framer.c
    src/nice64_latest_state.c
    src/nice64_network_rudp_adapter.c
    src/nice64_realtime_memops.c
    src/nice64_response.c
    src/nice64_rudp_codec.c
    src/nice64_rudp_engine.c
    src/nice64_tcp_service.c
    src/nice64_udp_service.c
    src/nice64_usb_transport.c
    src/role_manager.c
    src/standalone_diagnostics.c
    src/transfer_flash_transaction.c
    src/wifi_provisioning.c
    src/wifi_persistence.c
    src/wifi_persistence_record.c
    src/wifi_service.c
)

set(NICE64_STANDALONE_SERVICE_NETWORK "0" CACHE STRING "Build Standalone Role NETWORK service instead of Standalone diagnostics")
set(NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK "0" CACHE STRING "Build universal Controller + Standalone Diagnostics + Standalone Network image")
set(NICE64_RUDPC_HARDWARE_CANDIDATE "0" CACHE STRING "Require Universal Network + PIO RX for an RUDP hardware candidate")

if(NICE64_RUDPC_HARDWARE_CANDIDATE AND
   (NOT NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK OR
    NOT NICE_RX_BACKEND_PIO))
    message(FATAL_ERROR
        "RUDP hardware candidates require NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK=1 and NICE_RX_BACKEND_PIO=1")
endif()

if(NICE64_STANDALONE_SERVICE_NETWORK OR NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK)
    target_sources(pico_n64_controller_bridge PRIVATE
        src/bulk_stage.c
        src/network_pak.c
    )
endif()

target_include_directories(pico_n64_controller_bridge PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}
    ${CMAKE_CURRENT_LIST_DIR}/src
)

set(NICE_GCN_DIAG_MODE "1" CACHE STRING "GCN 0x40 diagnostic mode: 1=A_80, 2=B_7F, 3=C_ZERO, 4=D_NO_REPLY")
set(NICE_FORCE_NEUTRAL_STATUS "0" CACHE STRING "Force 0x01 controller status replies to hard-coded 00 00 00 00")
set(NICE_DISABLE_ACCESSORY_HANDLING "0" CACHE STRING "Disable accessory identity/read/write replies for strict controller diagnostics")
set(NICE_TX_BACKEND_BITBANG "0" CACHE STRING "Use CPU bit-banged Joybus TX instead of PIO TX")
set(NICE_RX_BACKEND_PIO "1" CACHE STRING "Use PIO pulse-width Joybus RX instead of GPIO delayed-sample RX")
set(NICE64_BUILD_ROLE_STANDALONE "0" CACHE STRING "Build Standalone Role diagnostics instead of default Controller Role")
set(NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS "0" CACHE STRING "Build first universal Controller + Standalone Diagnostics image")
target_compile_definitions(pico_n64_controller_bridge PRIVATE
    "CYW43_RESOURCE_ATTRIBUTE=__attribute__((aligned(4),section(\".flashdata.cyw43\")))"
    NICE_GCN_DIAG_MODE=${NICE_GCN_DIAG_MODE}
    NICE_FORCE_NEUTRAL_STATUS=${NICE_FORCE_NEUTRAL_STATUS}
    NICE_DISABLE_ACCESSORY_HANDLING=${NICE_DISABLE_ACCESSORY_HANDLING}
    NICE_TX_BACKEND_BITBANG=${NICE_TX_BACKEND_BITBANG}
    NICE_RX_BACKEND_PIO=${NICE_RX_BACKEND_PIO}
    NICE64_BUILD_ROLE_STANDALONE=${NICE64_BUILD_ROLE_STANDALONE}
    NICE64_STANDALONE_SERVICE_NETWORK=${NICE64_STANDALONE_SERVICE_NETWORK}
    NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS=${NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS}
    NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK=${NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK}
    NICE64_RUDPC_HARDWARE_CANDIDATE=${NICE64_RUDPC_HARDWARE_CANDIDATE}
    NICE64_TRANSFER_ROM_FLASH_OFFSET_BYTES=${NICE64_TRANSFER_ROM_FLASH_OFFSET_BYTES}u
)

if(PICO_BOARD STREQUAL "pico2_w")
    target_compile_definitions(pico_n64_controller_bridge PRIVATE
        NICE64_DISABLE_LEGACY_GPIO25_HEARTBEAT=1
    )
endif()

target_link_libraries(pico_n64_controller_bridge
    pico_stdlib
    pico_multicore
    pico_flash
    hardware_watchdog
    hardware_pio
    pico_cyw43_arch_lwip_poll
)

pico_enable_stdio_usb(pico_n64_controller_bridge 1)
pico_enable_stdio_uart(pico_n64_controller_bridge 0)
pico_add_extra_outputs(pico_n64_controller_bridge)

add_custom_command(TARGET pico_n64_controller_bridge POST_BUILD
    COMMAND ${CMAKE_COMMAND}
        -DIMAGE_FILE=$<TARGET_FILE_DIR:pico_n64_controller_bridge>/pico_n64_controller_bridge.bin
        -DMAX_IMAGE_BYTES=${NICE64_TRANSFER_ROM_FLASH_OFFSET_BYTES}
        -P ${CMAKE_CURRENT_LIST_DIR}/cmake/check_persistence_flash_boundary.cmake
    COMMENT "Checking firmware image does not overlap Transfer Pak flash"
)
