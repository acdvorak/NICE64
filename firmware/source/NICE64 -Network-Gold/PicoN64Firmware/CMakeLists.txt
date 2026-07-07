cmake_minimum_required(VERSION 3.13)

include($ENV{PICO_SDK_PATH}/external/pico_sdk_import.cmake)

project(pico_n64_controller_bridge C CXX ASM)
set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)

pico_sdk_init()

add_executable(pico_n64_controller_bridge
    src/main.c
    src/joybus.c
    src/n64_accessory.c
    src/n64_controller.c
)

target_include_directories(pico_n64_controller_bridge PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}/src
)

set(NICE_GCN_DIAG_MODE "1" CACHE STRING "GCN 0x40 diagnostic mode: 1=A_80, 2=B_7F, 3=C_ZERO, 4=D_NO_REPLY")
set(NICE_FORCE_NEUTRAL_STATUS "0" CACHE STRING "Force 0x01 controller status replies to hard-coded 00 00 00 00")
set(NICE_DISABLE_ACCESSORY_HANDLING "0" CACHE STRING "Disable accessory identity/read/write replies for strict controller diagnostics")
set(NICE_TX_BACKEND_BITBANG "0" CACHE STRING "Use CPU bit-banged Joybus TX instead of PIO TX")
set(NICE_RX_BACKEND_PIO "0" CACHE STRING "Use PIO pulse-width Joybus RX instead of GPIO delayed-sample RX")
set(NICE_NETWORK_PAK_ONLY "0" CACHE STRING "Keep accessory mode locked to Network Pak for Network Pak POC builds")
target_compile_definitions(pico_n64_controller_bridge PRIVATE
    NICE_GCN_DIAG_MODE=${NICE_GCN_DIAG_MODE}
    NICE_FORCE_NEUTRAL_STATUS=${NICE_FORCE_NEUTRAL_STATUS}
    NICE_DISABLE_ACCESSORY_HANDLING=${NICE_DISABLE_ACCESSORY_HANDLING}
    NICE_TX_BACKEND_BITBANG=${NICE_TX_BACKEND_BITBANG}
    NICE_RX_BACKEND_PIO=${NICE_RX_BACKEND_PIO}
    NICE_NETWORK_PAK_ONLY=${NICE_NETWORK_PAK_ONLY}
)

target_link_libraries(pico_n64_controller_bridge
    pico_stdlib
    hardware_pio
)

pico_enable_stdio_usb(pico_n64_controller_bridge 1)
pico_enable_stdio_uart(pico_n64_controller_bridge 0)
pico_add_extra_outputs(pico_n64_controller_bridge)
