#include "joybus.h"

#include <stdbool.h>
#include <stdint.h>
#include <string.h>

#include "hardware/clocks.h"
#include "hardware/gpio.h"
#include "hardware/irq.h"
#include "hardware/pio.h"
#include "hardware/pio_instructions.h"
#include "hardware/sync.h"
#include "n64_accessory.h"
#include "n64_controller.h"
#include "n64_keyboard.h"
#include "n64_mouse.h"
#include "n64_protocol.h"
#include "network_pak.h"
#include "joybus_transport.h"
#include "nice64_realtime_memops.h"
#include "pico/stdlib.h"
#include "pico/time.h"
#include "role_manager.h"

#define N64_DATA_PIN 2
#define TX_MARKER_PIN 16

// First-party controller capture target:
// reply delay ~2.2 us, 1-low ~1.07 us, 0-low ~3.04 us.
#define N64_BIT_US 4
#define N64_ONE_LOW_US 2
#if PICO_RP2350
#define N64_FIRST_ONE_LOW_US 2
#else
#define N64_FIRST_ONE_LOW_US N64_ONE_LOW_US
#endif
#define N64_ZERO_LOW_US 3
#define N64_FIRST_ZERO_LOW_US 4
#define N64_FIRST_BIT_US 5
#define N64_FIRST_ONE_LOW_CYCLES 120u
#define N64_ONE_LOW_CYCLES 120u
#define N64_STOP_LOW_US 2
#define N64_REPLY_DELAY_US 0
#define N64_HOT_REPLY_DELAY_US 2
#define N64_ACCESSORY_READ_REPLY_DELAY_US 6
#define N64_ACCESSORY_WRITE_REPLY_DELAY_US 3

#define RX_STOP_TIMEOUT_US 12
#define RX_NEW_STREAM_US 300
#if PICO_RP2350
#define RX_SAMPLE_DELAY_US 2
#else
#define RX_SAMPLE_DELAY_US 1
#endif
#define MAX_COMMAND_BYTES 35
#define N64_CMD_GAME_ID 0x1D

#ifndef NICE_GCN_DIAG_MODE
#define NICE_GCN_DIAG_MODE 1
#endif

#define NICE_GCN_DIAG_MODE_A_80 1
#define NICE_GCN_DIAG_MODE_B_7F 2
#define NICE_GCN_DIAG_MODE_C_ZERO 3
#define NICE_GCN_DIAG_MODE_D_NO_REPLY 4

#ifndef NICE_FORCE_NEUTRAL_STATUS
#define NICE_FORCE_NEUTRAL_STATUS 0
#endif

#ifndef NICE_DISABLE_ACCESSORY_HANDLING
#define NICE_DISABLE_ACCESSORY_HANDLING 0
#endif

#ifndef NICE64_BUILD_ROLE_STANDALONE
#define NICE64_BUILD_ROLE_STANDALONE 0
#endif

#ifndef NICE64_STANDALONE_SERVICE_NETWORK
#define NICE64_STANDALONE_SERVICE_NETWORK 0
#endif

#ifndef NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK
#define NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK 0
#endif

#define NICE64_STANDALONE_NETWORK_ACTIVE \
    ((NICE64_BUILD_ROLE_STANDALONE && NICE64_STANDALONE_SERVICE_NETWORK) || \
     NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK)

#define JOYBUS_TX_BACKEND_NEEDS_BITBANG 1
#define JOYBUS_TX_BACKEND_NEEDS_PIO \
    (!JOYBUS_TX_BACKEND_IS_CPU_BITBANG || NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK)

static inline bool nice64_network_personality_active(void) {
#if NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK
    const nice64_active_mode_t mode = role_manager_active_mode();
    return mode.role == NICE64_ROLE_STANDALONE &&
           mode.standalone_service == NICE64_SERVICE_NETWORK;
#else
    return true;
#endif
}

static inline bool nice64_keyboard_mouse_personality_active(void) {
    const nice64_active_mode_t mode = role_manager_active_mode();
    return mode.role == NICE64_ROLE_STANDALONE &&
           mode.standalone_service == NICE64_SERVICE_KEYBOARD_MOUSE;
}

static volatile uint8_t rx_bytes[MAX_COMMAND_BYTES];
static volatile int rx_byte_index = 0;
static volatile int rx_bit = 7;
static volatile int rx_expected_bytes = 1;
static volatile bool rx_waiting_stop = false;
static volatile uint32_t rx_last_edge_us = 0;

static volatile joybus_debug_t debug;

static PIO n64_tx_pio = pio0;
static uint n64_tx_sm = 0;
static uint n64_tx_offset = 0;
static bool n64_tx_ready = false;

#if JOYBUS_RX_BACKEND_IS_PIO
static PIO n64_rx_pio = pio1;
static uint n64_rx_sm = 0;
static uint n64_rx_offset = 0;
static bool n64_rx_ready = false;
#endif

enum {
    N64_TX_PULL_COUNT = 0,
    N64_TX_MOV_COUNT,
    N64_TX_BITLOOP,
    N64_TX_PULL_BIT,
    N64_TX_MOV_BIT,
    N64_TX_JMP_ZERO,
    N64_TX_ONE_LOW,
    N64_TX_ONE_HIGH,
    N64_TX_ONE_NEXT,
    N64_TX_JMP_STOP,
    N64_TX_ZERO_LOW,
    N64_TX_ZERO_HIGH,
    N64_TX_ZERO_NEXT,
    N64_TX_STOP_LOW,
    N64_TX_STOP_RELEASE,
    N64_TX_IRQ_DONE,
    N64_TX_PROGRAM_LEN,
};

#if JOYBUS_RX_BACKEND_IS_PIO
enum {
    N64_RX_WAIT_HIGH = 0,
    N64_RX_WAIT_FALL,
    N64_RX_SET_COUNT,
    N64_RX_LOW_LOOP,
    N64_RX_DEC_OR_LONG,
    N64_RX_LONG_WAIT_HIGH,
    N64_RX_LONG_SET,
    N64_RX_LONG_MOV,
    N64_RX_LONG_PUSH,
    N64_RX_LONG_JMP,
    N64_RX_SHORT_SET,
    N64_RX_SHORT_MOV,
    N64_RX_SHORT_PUSH,
    N64_RX_PROGRAM_LEN,
};
#endif

static inline uint32_t now_us32(void) {
    return time_us_32();
}

static inline bool elapsed_us(uint32_t start, uint32_t us) {
    return (uint32_t)(now_us32() - start) >= us;
}

static inline void wait_until_us(uint32_t start, uint32_t delay_us) {
    while (!elapsed_us(start, delay_us)) {
        tight_loop_contents();
    }
}

static inline void n64_release(void) {
    gpio_set_dir(N64_DATA_PIN, GPIO_IN);
}

static inline void n64_pull_low(void) {
    gpio_put(N64_DATA_PIN, 0);
    gpio_set_dir(N64_DATA_PIN, GPIO_OUT);
}

static inline void tx_marker_set(bool active) {
    gpio_put(TX_MARKER_PIN, active ? 1 : 0);
}

#if JOYBUS_RX_BACKEND_IS_PIO
static void __no_inline_not_in_flash_func(n64_rx_pio_set_enabled)(bool enabled) {
    if (!n64_rx_ready) {
        return;
    }

    pio_set_irq0_source_enabled(n64_rx_pio, (enum pio_interrupt_source)(pis_sm0_rx_fifo_not_empty + n64_rx_sm), false);
    pio_sm_set_enabled(n64_rx_pio, n64_rx_sm, false);
    gpio_set_function(N64_DATA_PIN, GPIO_FUNC_SIO);
    gpio_set_dir(N64_DATA_PIN, GPIO_IN);

    while (!pio_sm_is_rx_fifo_empty(n64_rx_pio, n64_rx_sm)) {
        (void)pio_sm_get(n64_rx_pio, n64_rx_sm);
    }

    pio_sm_clear_fifos(n64_rx_pio, n64_rx_sm);
    pio_sm_restart(n64_rx_pio, n64_rx_sm);

    if (enabled) {
        gpio_set_dir(N64_DATA_PIN, GPIO_IN);
        gpio_set_function(N64_DATA_PIN, GPIO_FUNC_PIO1);
        pio_sm_set_enabled(n64_rx_pio, n64_rx_sm, true);
        pio_set_irq0_source_enabled(n64_rx_pio, (enum pio_interrupt_source)(pis_sm0_rx_fifo_not_empty + n64_rx_sm), true);
    }
}
#endif

static void n64_tx_pio_init(void) {
    if (n64_tx_ready) {
        return;
    }

    uint16_t instructions[N64_TX_PROGRAM_LEN];
    instructions[N64_TX_PULL_COUNT] = pio_encode_pull(false, true);
    instructions[N64_TX_MOV_COUNT] = pio_encode_mov(pio_y, pio_osr);
    instructions[N64_TX_BITLOOP] = pio_encode_pull(false, true);
    instructions[N64_TX_PULL_BIT] = pio_encode_mov(pio_x, pio_osr);
    instructions[N64_TX_MOV_BIT] = pio_encode_jmp_not_x(N64_TX_ZERO_LOW);
    instructions[N64_TX_JMP_ZERO] = pio_encode_nop();
    instructions[N64_TX_ONE_LOW] = pio_encode_set(pio_pindirs, 1) | pio_encode_delay(1);
    instructions[N64_TX_ONE_HIGH] = pio_encode_set(pio_pindirs, 0) | pio_encode_delay(4);
    instructions[N64_TX_ONE_NEXT] = pio_encode_jmp_y_dec(N64_TX_BITLOOP);
    instructions[N64_TX_JMP_STOP] = pio_encode_jmp(N64_TX_STOP_LOW);
    instructions[N64_TX_ZERO_LOW] = pio_encode_set(pio_pindirs, 1) | pio_encode_delay(5);
    instructions[N64_TX_ZERO_HIGH] = pio_encode_set(pio_pindirs, 0);
    instructions[N64_TX_ZERO_NEXT] = pio_encode_jmp_y_dec(N64_TX_BITLOOP);
    instructions[N64_TX_STOP_LOW] = pio_encode_set(pio_pindirs, 1) | pio_encode_delay(3);
    instructions[N64_TX_STOP_RELEASE] = pio_encode_set(pio_pindirs, 0);
    instructions[N64_TX_IRQ_DONE] = pio_encode_irq_set(false, 0);

    const struct pio_program n64_tx_program_manual = {
        .instructions = instructions,
        .length = N64_TX_PROGRAM_LEN,
        .origin = -1,
    };

    n64_tx_sm = pio_claim_unused_sm(n64_tx_pio, true);
    n64_tx_offset = pio_add_program(n64_tx_pio, &n64_tx_program_manual);
    pio_gpio_init(n64_tx_pio, N64_DATA_PIN);
    pio_sm_set_consecutive_pindirs(n64_tx_pio, n64_tx_sm, N64_DATA_PIN, 1, false);
    pio_sm_set_pins_with_mask(n64_tx_pio, n64_tx_sm, 0, 1u << N64_DATA_PIN);

    pio_sm_config config = pio_get_default_sm_config();
    sm_config_set_set_pins(&config, N64_DATA_PIN, 1);
    sm_config_set_out_shift(&config, true, false, 32);
    sm_config_set_fifo_join(&config, PIO_FIFO_JOIN_TX);
    sm_config_set_clkdiv(&config, (float)clock_get_hz(clk_sys) / 2000000.0f);
    sm_config_set_wrap(&config, n64_tx_offset + N64_TX_PULL_COUNT, n64_tx_offset + N64_TX_IRQ_DONE);
    pio_sm_init(n64_tx_pio, n64_tx_sm, n64_tx_offset, &config);

    n64_tx_ready = true;
}

static void __no_inline_not_in_flash_func(n64_send_bytes_pio)(const uint8_t *bytes, int count) {
    if (count <= 0) {
        return;
    }

    const uint32_t bit_count = (uint32_t)count * 8u;
    uint32_t bits[264];
    uint32_t bit_index = 0;

    for (int i = 0; i < count; ++i) {
        for (int bit = 7; bit >= 0; --bit) {
            bits[bit_index++] = (bytes[i] >> bit) & 0x1u;
        }
    }

    pio_sm_set_enabled(n64_tx_pio, n64_tx_sm, false);
    pio_sm_clear_fifos(n64_tx_pio, n64_tx_sm);
    pio_sm_restart(n64_tx_pio, n64_tx_sm);
    pio_interrupt_clear(n64_tx_pio, 0);
    pio_sm_put_blocking(n64_tx_pio, n64_tx_sm, bit_count - 1u);
    for (uint32_t i = 0; i < bit_count && i < 7; ++i) {
        pio_sm_put_blocking(n64_tx_pio, n64_tx_sm, bits[i]);
    }
    pio_sm_set_enabled(n64_tx_pio, n64_tx_sm, true);

    for (uint32_t i = 7; i < bit_count; ++i) {
        pio_sm_put_blocking(n64_tx_pio, n64_tx_sm, bits[i]);
    }

    while (!pio_interrupt_get(n64_tx_pio, 0)) {
        tight_loop_contents();
    }
    pio_interrupt_clear(n64_tx_pio, 0);
    pio_sm_set_enabled(n64_tx_pio, n64_tx_sm, false);
    pio_sm_set_consecutive_pindirs(n64_tx_pio, n64_tx_sm, N64_DATA_PIN, 1, false);
}

static void n64_send_bit(bool bit) {
    const uint32_t low_us = bit ? N64_ONE_LOW_US : N64_ZERO_LOW_US;
    n64_pull_low();
    const uint32_t start = now_us32();
    if (bit) {
#if PICO_RP2350
        busy_wait_at_least_cycles(N64_ONE_LOW_CYCLES);
#else
        wait_until_us(start, low_us);
#endif
    } else {
        wait_until_us(start, low_us);
    }
    n64_release();
    wait_until_us(start, N64_BIT_US);
}

static void n64_send_first_bit(bool bit) {
    n64_pull_low();
    const uint32_t start = now_us32();
    if (bit) {
        busy_wait_at_least_cycles(N64_FIRST_ONE_LOW_CYCLES);
    } else {
        wait_until_us(start, N64_FIRST_ZERO_LOW_US);
    }
    n64_release();
    wait_until_us(start, bit ? N64_BIT_US : N64_FIRST_BIT_US);
}

static void n64_send_stop(void) {
    n64_pull_low();
    const uint32_t start = now_us32();
    wait_until_us(start, N64_STOP_LOW_US);
    n64_release();
    wait_until_us(start, N64_BIT_US);
}

static void n64_send_byte(uint8_t value) {
    for (int bit = 7; bit >= 0; --bit) {
        n64_send_bit((value & (1u << bit)) != 0);
    }
}

#if JOYBUS_TX_BACKEND_NEEDS_BITBANG
static void n64_send_bytes_bitbang(const uint8_t *bytes, int count) {
    if (count <= 0) {
        return;
    }

    n64_send_first_bit((bytes[0] & 0x80u) != 0);
    for (int bit = 6; bit >= 0; --bit) {
        n64_send_bit((bytes[0] & (1u << bit)) != 0);
    }

    for (int i = 1; i < count; ++i) {
        n64_send_byte(bytes[i]);
    }

    n64_send_stop();
}
#endif

static void n64_send_bytes(uint8_t command, const uint8_t *bytes, int count) {
#if JOYBUS_RX_BACKEND_IS_PIO
    n64_rx_pio_set_enabled(false);
#else
    gpio_set_irq_enabled(N64_DATA_PIN, GPIO_IRQ_EDGE_FALL, false);
#endif

    const uint32_t save = save_and_disable_interrupts();
    debug.tx_start_count++;
    debug.last_tx_command = command;
    debug.last_tx_reply_len = (uint8_t)(count > 255 ? 255 : count);
    if (command == N64_CMD_IDENTIFY) {
        debug.tx_hot_ident_count++;
    } else if (command == N64_CMD_STATUS) {
        debug.tx_hot_status_count++;
    }

    tx_marker_set(true);
#if NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK
    const bool network_mode = nice64_network_personality_active();
    const bool network_pak_accessory_reply =
        network_mode &&
        ((command == N64_CMD_ACCESSORY_READ && count == NETWORK_PAK_REPLY_SIZE) ||
         (command == N64_CMD_ACCESSORY_WRITE && count == 1));

    if (network_mode && !network_pak_accessory_reply) {
        n64_send_bytes_pio(bytes, count);
    } else {
        n64_send_bytes_bitbang(bytes, count);
    }
#elif JOYBUS_TX_BACKEND_IS_CPU_BITBANG
    n64_send_bytes_bitbang(bytes, count);
#else
    const bool network_pak_accessory_reply =
        NICE64_STANDALONE_NETWORK_ACTIVE &&
        nice64_network_personality_active() &&
        ((command == N64_CMD_ACCESSORY_READ && count == NETWORK_PAK_REPLY_SIZE) ||
         (command == N64_CMD_ACCESSORY_WRITE && count == 1));
    if (network_pak_accessory_reply) {
        n64_send_bytes_bitbang(bytes, count);
    } else {
        n64_send_bytes_pio(bytes, count);
    }
#endif
    tx_marker_set(false);
    rx_bytes[0] = 0;
    rx_byte_index = 0;
    rx_bit = 7;
    rx_expected_bytes = 1;
    rx_waiting_stop = false;
    rx_last_edge_us = now_us32();
#if JOYBUS_RX_BACKEND_IS_PIO
    n64_rx_pio_set_enabled(true);
#else
    gpio_acknowledge_irq(N64_DATA_PIN, GPIO_IRQ_EDGE_FALL);
    gpio_set_irq_enabled(N64_DATA_PIN, GPIO_IRQ_EDGE_FALL, true);
#endif
    debug.tx_done_count++;
    restore_interrupts(save);
}

static int expected_command_bytes(uint8_t command) {
    if (nice64_keyboard_mouse_personality_active() &&
        role_manager_keyboard_mouse_device() == NICE64_KEYBOARD_MOUSE_KEYBOARD &&
        command == N64_KEYBOARD_COMMAND_READ) {
        return 2;
    }

    if (command == N64_CMD_GAME_ID) {
        return 11;
    }

    if (command == N64_CMD_GCN_CONTROLLER_READ) {
        return 3;
    }

    if (command == N64_CMD_ACCESSORY_READ) {
        return 3;
    }

    if (command == N64_CMD_ACCESSORY_WRITE) {
        return 35;
    }

    return 1;
}

static void reset_rx_state(void) {
    rx_bytes[0] = 0;
    rx_byte_index = 0;
    rx_bit = 7;
    rx_expected_bytes = 1;
    rx_waiting_stop = false;
    rx_last_edge_us = now_us32();
}

static void remember_gcn_reply(const uint8_t *reply, uint8_t len) {
    debug.last_gcn_reply_len = len;
    memset((void *)debug.last_gcn_reply, 0, sizeof(debug.last_gcn_reply));
    if (reply && len) {
        memcpy((void *)debug.last_gcn_reply, reply, len > 10u ? 10u : len);
    }
}

static bool prepare_gcn_controller_reply(uint8_t reply[10]) {
#if NICE_GCN_DIAG_MODE == NICE_GCN_DIAG_MODE_A_80
    static const uint8_t canned[8] = {0x00, 0x00, 0x80, 0x80, 0x80, 0x80, 0x00, 0x00};
    memcpy(reply, canned, sizeof(canned));
    remember_gcn_reply(reply, sizeof(canned));
    return true;
#elif NICE_GCN_DIAG_MODE == NICE_GCN_DIAG_MODE_B_7F
    static const uint8_t canned[8] = {0x00, 0x00, 0x7F, 0x7F, 0x7F, 0x7F, 0x00, 0x00};
    memcpy(reply, canned, sizeof(canned));
    remember_gcn_reply(reply, sizeof(canned));
    return true;
#elif NICE_GCN_DIAG_MODE == NICE_GCN_DIAG_MODE_C_ZERO
    static const uint8_t canned[8] = {0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
    memcpy(reply, canned, sizeof(canned));
    remember_gcn_reply(reply, sizeof(canned));
    return true;
#else
    remember_gcn_reply(NULL, 0);
    return false;
#endif
}

static bool prepare_hot_reply(uint8_t command, uint8_t reply[4], int *reply_count, uint32_t *reply_delay_us) {
    *reply_count = 0;
    *reply_delay_us = N64_HOT_REPLY_DELAY_US;

    if (command == N64_CMD_IDENTIFY) {
        debug.commands++;
        debug.last_command = command;
        debug.identify++;

        if (nice64_keyboard_mouse_personality_active()) {
            if (role_manager_keyboard_mouse_device() == NICE64_KEYBOARD_MOUSE_MOUSE)
                n64_mouse_identity(reply);
            else
                n64_keyboard_identity(reply);
            *reply_count = 3;
            return true;
        }

        reply[0] = N64_ID_CONTROLLER;
        reply[1] = 0x00;
#if NICE64_STANDALONE_NETWORK_ACTIVE
        if (nice64_network_personality_active()) {
            reply[2] = NETWORK_PAK_ACCESSORY_ID;
        } else {
#if NICE_DISABLE_ACCESSORY_HANDLING
            reply[2] = N64_ID_NO_ACCESSORY_REAL_CONTROLLER;
#else
            reply[2] = n64_accessory_identity_byte_irq();
#endif
        }
#else
#if NICE_DISABLE_ACCESSORY_HANDLING
        reply[2] = N64_ID_NO_ACCESSORY_REAL_CONTROLLER;
#else
        reply[2] = n64_accessory_identity_byte_irq();
#endif
#endif
        *reply_count = 3;
        return true;
    }

    if (command == N64_CMD_STATUS) {
        debug.commands++;
        debug.last_command = command;
        debug.status++;
        if (nice64_keyboard_mouse_personality_active()) {
            if (role_manager_keyboard_mouse_device() == NICE64_KEYBOARD_MOUSE_MOUSE) {
                n64_mouse_read(reply);
                *reply_count = 4;
            }
            return true;
        }
#if NICE64_STANDALONE_NETWORK_ACTIVE
        if (nice64_network_personality_active()) {
            nice64_realtime_fill(reply, 0, 4);
        } else {
#if NICE_FORCE_NEUTRAL_STATUS
            nice64_realtime_fill(reply, 0, 4);
#else
            n64_controller_get_status_irq(reply);
#endif
        }
#else
#if NICE_FORCE_NEUTRAL_STATUS
        nice64_realtime_fill(reply, 0, 4);
#else
        n64_controller_get_status_irq(reply);
#endif
#endif
        *reply_count = 4;
        return true;
    }

    return false;
}

static void prepare_reply(uint8_t command, uint8_t reply[33], int *reply_count, uint32_t *reply_delay_us) {
    *reply_count = 0;
    *reply_delay_us = N64_REPLY_DELAY_US;
    debug.commands++;
    debug.last_command = command;

    if (nice64_keyboard_mouse_personality_active()) {
        const nice64_keyboard_mouse_device_t device =
            role_manager_keyboard_mouse_device();
        if (command == N64_CMD_IDENTIFY || command == N64_CMD_RESET) {
            if (command == N64_CMD_RESET) debug.reset++;
            else debug.identify++;
            if (device == NICE64_KEYBOARD_MOUSE_MOUSE)
                n64_mouse_identity(reply);
            else
                n64_keyboard_identity(reply);
            *reply_count = 3;
        } else if (command == N64_CMD_STATUS &&
                   device == NICE64_KEYBOARD_MOUSE_MOUSE) {
            debug.status++;
            n64_mouse_read(reply);
            *reply_count = 4;
        } else if (command == N64_KEYBOARD_COMMAND_READ &&
                   device == NICE64_KEYBOARD_MOUSE_KEYBOARD) {
            n64_keyboard_read(rx_bytes[1], reply);
            *reply_count = N64_KEYBOARD_REPLY_SIZE;
        } else {
            debug.other++;
        }
        return;
    }

    if (command == N64_CMD_IDENTIFY || command == N64_CMD_RESET) {
        if (command == N64_CMD_RESET) {
            debug.reset++;
        } else {
            debug.identify++;
        }

        reply[0] = N64_ID_CONTROLLER;
        reply[1] = 0x00;
#if NICE64_STANDALONE_NETWORK_ACTIVE
        if (nice64_network_personality_active()) {
            reply[2] = NETWORK_PAK_ACCESSORY_ID;
        } else {
#if NICE_DISABLE_ACCESSORY_HANDLING
            reply[2] = N64_ID_NO_ACCESSORY_REAL_CONTROLLER;
#else
            reply[2] = n64_accessory_identity_byte();
#endif
        }
#else
#if NICE_DISABLE_ACCESSORY_HANDLING
        reply[2] = N64_ID_NO_ACCESSORY_REAL_CONTROLLER;
#else
        reply[2] = n64_accessory_identity_byte();
#endif
#endif
        *reply_count = 3;
    } else if (command == N64_CMD_STATUS) {
        debug.status++;
#if NICE64_STANDALONE_NETWORK_ACTIVE
        if (nice64_network_personality_active()) {
            nice64_realtime_fill(reply, 0, 4);
        } else {
#if NICE_FORCE_NEUTRAL_STATUS
            nice64_realtime_fill(reply, 0, 4);
#else
            n64_controller_get_status(reply);
#endif
        }
#else
#if NICE_FORCE_NEUTRAL_STATUS
        nice64_realtime_fill(reply, 0, 4);
#else
        n64_controller_get_status(reply);
#endif
#endif
        *reply_count = 4;
    } else if (command == N64_CMD_GCN_CONTROLLER_READ) {
#if NICE64_STANDALONE_NETWORK_ACTIVE
        if (nice64_network_personality_active()) {
            debug.gcn_no_reply_count++;
            debug.other++;
        } else {
            debug.gcn_read_count++;
            if (prepare_gcn_controller_reply(reply)) {
                *reply_count = 8;
            } else {
                debug.gcn_no_reply_count++;
            }
        }
#else
        debug.gcn_read_count++;
        if (prepare_gcn_controller_reply(reply)) {
            *reply_count = 8;
        } else {
            debug.gcn_no_reply_count++;
        }
#endif
    } else if (command == N64_CMD_GCN_CONTROLLER_ORIGIN || command == N64_CMD_GCN_CONTROLLER_RECALIBRATE) {
        debug.gcn_origin_count++;
        debug.gcn_no_reply_count++;
        debug.other++;
    } else if (command == N64_CMD_GCN_CONTROLLER_READ_LONG) {
        debug.gcn_long_count++;
        debug.gcn_no_reply_count++;
        debug.other++;
    } else if (command == N64_CMD_ACCESSORY_READ) {
        debug.accessory_read++;
#if NICE64_STANDALONE_NETWORK_ACTIVE
        if (nice64_network_personality_active()) {
            const uint16_t address = network_pak_decode_address(rx_bytes[1], rx_bytes[2]);
            debug.last_accessory_address = address;
            if (network_pak_handle_read(address, reply)) {
                *reply_count = NETWORK_PAK_REPLY_SIZE;
                *reply_delay_us = N64_ACCESSORY_READ_REPLY_DELAY_US;
            }
        } else {
#if NICE_DISABLE_ACCESSORY_HANDLING
            debug.other++;
            return;
#else
            const uint16_t address = n64_accessory_decode_address(rx_bytes[1], rx_bytes[2]);
            debug.last_accessory_address = address;
            if (n64_accessory_handle_read(address, reply)) {
                *reply_count = 33;
                *reply_delay_us = N64_ACCESSORY_READ_REPLY_DELAY_US;
            }
#endif
        }
#else
#if NICE_DISABLE_ACCESSORY_HANDLING
        debug.other++;
        return;
#else
        const uint16_t address = n64_accessory_decode_address(rx_bytes[1], rx_bytes[2]);
        debug.last_accessory_address = address;
        if (n64_accessory_handle_read(address, reply)) {
            *reply_count = 33;
            *reply_delay_us = N64_ACCESSORY_READ_REPLY_DELAY_US;
        }
#endif
#endif
    } else if (command == N64_CMD_ACCESSORY_WRITE) {
        debug.accessory_write++;
#if NICE64_STANDALONE_NETWORK_ACTIVE
        if (nice64_network_personality_active()) {
            const uint16_t address = network_pak_decode_address(rx_bytes[1], rx_bytes[2]);
            debug.last_accessory_address = address;
            if (network_pak_handle_write(address, (const uint8_t *)&rx_bytes[3], &reply[0])) {
                *reply_count = 1;
                *reply_delay_us = N64_ACCESSORY_WRITE_REPLY_DELAY_US;
            }
        } else {
#if NICE_DISABLE_ACCESSORY_HANDLING
            debug.other++;
            return;
#else
            const uint16_t address = n64_accessory_decode_address(rx_bytes[1], rx_bytes[2]);
            debug.last_accessory_address = address;
            if (n64_accessory_handle_write(address, (const uint8_t *)&rx_bytes[3], &reply[0])) {
                *reply_count = 1;
                *reply_delay_us = N64_ACCESSORY_WRITE_REPLY_DELAY_US;
            }
#endif
        }
#else
#if NICE_DISABLE_ACCESSORY_HANDLING
        debug.other++;
        return;
#else
        const uint16_t address = n64_accessory_decode_address(rx_bytes[1], rx_bytes[2]);
        debug.last_accessory_address = address;
        if (n64_accessory_handle_write(address, (const uint8_t *)&rx_bytes[3], &reply[0])) {
            *reply_count = 1;
            *reply_delay_us = N64_ACCESSORY_WRITE_REPLY_DELAY_US;
        }
#endif
#endif
    } else {
        debug.other++;
    }
}

#if JOYBUS_RX_BACKEND_IS_PIO
static void dispatch_reply_from_stop_release(uint8_t command, uint32_t release_us) {
    uint8_t reply[33];
    int reply_count = 0;
    uint32_t reply_delay_us = 0;

    debug.rx_stop_dispatch_count++;

    if (!prepare_hot_reply(command, reply, &reply_count, &reply_delay_us)) {
        prepare_reply(command, reply, &reply_count, &reply_delay_us);
    } else if (reply_count > 0) {
        debug.rx_hot_reply_count++;
    }

    if (reply_count > 0) {
        if (reply_delay_us > 0) {
            wait_until_us(release_us, reply_delay_us);
        }
        n64_send_bytes(command, reply, reply_count);
    } else {
        debug.rx_no_reply_count++;
    }

    reset_rx_state();
}

static void n64_rx_pio_accept_pulse(uint32_t pulse_bit) {
    const uint32_t pulse_us = now_us32();
    debug.edges++;

    if ((uint32_t)(pulse_us - rx_last_edge_us) > RX_NEW_STREAM_US) {
        if (rx_waiting_stop) {
            debug.rx_waiting_new_stream_count++;
        }

        rx_bytes[0] = 0;
        rx_byte_index = 0;
        rx_bit = 7;
        rx_expected_bytes = 1;
        rx_waiting_stop = false;
        debug.streams++;
    }

    if (rx_waiting_stop) {
        const uint8_t command = rx_bytes[0];
        rx_last_edge_us = pulse_us;
        dispatch_reply_from_stop_release(command, pulse_us);
        return;
    }

    if ((pulse_bit & 0x1u) != 0) {
        rx_bytes[rx_byte_index] |= (uint8_t)(1u << rx_bit);
    }

    rx_last_edge_us = pulse_us;
    rx_bit--;

    if (rx_bit < 0) {
        rx_byte_index++;
        rx_bit = 7;

        if (rx_byte_index == 1) {
            rx_expected_bytes = expected_command_bytes(rx_bytes[0]);
        }

        if (rx_byte_index >= rx_expected_bytes || rx_byte_index >= MAX_COMMAND_BYTES) {
            rx_waiting_stop = true;
            debug.rx_command_complete_count++;
        } else {
            rx_bytes[rx_byte_index] = 0;
        }
    }
}

static void __no_inline_not_in_flash_func(n64_rx_pio_irq)(void) {
    while (!pio_sm_is_rx_fifo_empty(n64_rx_pio, n64_rx_sm)) {
        n64_rx_pio_accept_pulse(pio_sm_get(n64_rx_pio, n64_rx_sm));
    }
}

static void n64_rx_pio_init(void) {
    if (n64_rx_ready) {
        return;
    }

    uint16_t instructions[N64_RX_PROGRAM_LEN];
    instructions[N64_RX_WAIT_HIGH] = pio_encode_wait_pin(true, 0);
    instructions[N64_RX_WAIT_FALL] = pio_encode_wait_pin(false, 0);
    instructions[N64_RX_SET_COUNT] = pio_encode_set(pio_x, 7);
    instructions[N64_RX_LOW_LOOP] = pio_encode_jmp_pin(N64_RX_SHORT_SET);
    instructions[N64_RX_DEC_OR_LONG] = pio_encode_jmp_x_dec(N64_RX_LOW_LOOP);
    instructions[N64_RX_LONG_WAIT_HIGH] = pio_encode_wait_pin(true, 0);
    instructions[N64_RX_LONG_SET] = pio_encode_set(pio_y, 0);
    instructions[N64_RX_LONG_MOV] = pio_encode_mov(pio_isr, pio_y);
    instructions[N64_RX_LONG_PUSH] = pio_encode_push(false, true);
    instructions[N64_RX_LONG_JMP] = pio_encode_jmp(N64_RX_WAIT_HIGH);
    instructions[N64_RX_SHORT_SET] = pio_encode_set(pio_y, 1);
    instructions[N64_RX_SHORT_MOV] = pio_encode_mov(pio_isr, pio_y);
    instructions[N64_RX_SHORT_PUSH] = pio_encode_push(false, true);

    const struct pio_program n64_rx_program_manual = {
        .instructions = instructions,
        .length = N64_RX_PROGRAM_LEN,
        .origin = -1,
    };

    n64_rx_sm = pio_claim_unused_sm(n64_rx_pio, true);
    n64_rx_offset = pio_add_program(n64_rx_pio, &n64_rx_program_manual);

    pio_sm_config config = pio_get_default_sm_config();
    sm_config_set_in_pins(&config, N64_DATA_PIN);
    sm_config_set_jmp_pin(&config, N64_DATA_PIN);
    sm_config_set_clkdiv(&config, (float)clock_get_hz(clk_sys) / 8000000.0f);
    sm_config_set_fifo_join(&config, PIO_FIFO_JOIN_RX);
    sm_config_set_wrap(&config, n64_rx_offset + N64_RX_WAIT_HIGH, n64_rx_offset + N64_RX_SHORT_PUSH);

    gpio_set_dir(N64_DATA_PIN, GPIO_IN);
    gpio_set_function(N64_DATA_PIN, GPIO_FUNC_PIO1);
    pio_sm_init(n64_rx_pio, n64_rx_sm, n64_rx_offset, &config);

    irq_set_exclusive_handler(PIO1_IRQ_0, n64_rx_pio_irq);
    irq_set_enabled(PIO1_IRQ_0, true);

    n64_rx_ready = true;
    n64_rx_pio_set_enabled(true);
}
#endif

#if !JOYBUS_RX_BACKEND_IS_PIO
static void n64_gpio_irq(uint gpio, uint32_t events) {
    if (gpio != N64_DATA_PIN || (events & GPIO_IRQ_EDGE_FALL) == 0) {
        return;
    }

    debug.edges++;

    const uint32_t edge_us = now_us32();
    if ((uint32_t)(edge_us - rx_last_edge_us) > RX_NEW_STREAM_US) {
        if (rx_waiting_stop) {
            debug.rx_waiting_new_stream_count++;
        }

        rx_bytes[0] = 0;
        rx_byte_index = 0;
        rx_bit = 7;
        rx_expected_bytes = 1;
        rx_waiting_stop = false;
        debug.streams++;
    }

    if (rx_waiting_stop) {
        uint8_t reply[33];
        int reply_count = 0;
        uint32_t reply_delay_us = 0;
        const uint8_t command = rx_bytes[0];

        debug.rx_stop_dispatch_count++;

        if (!prepare_hot_reply(command, reply, &reply_count, &reply_delay_us)) {
            prepare_reply(command, reply, &reply_count, &reply_delay_us);
        } else if (reply_count > 0) {
            debug.rx_hot_reply_count++;
        }

        while (!gpio_get(N64_DATA_PIN)) {
            if (elapsed_us(edge_us, RX_STOP_TIMEOUT_US)) {
                debug.rx_stop_timeout_count++;
                reset_rx_state();
                return;
            }
            tight_loop_contents();
        }

        if (reply_count > 0) {
            if (reply_delay_us > 0) {
                const uint32_t reply_delay_start = now_us32();
                wait_until_us(reply_delay_start, reply_delay_us);
            }
            n64_send_bytes(command, reply, reply_count);
        }

        if (reply_count <= 0) {
            debug.rx_no_reply_count++;
        }

        reset_rx_state();

        return;
    }

    wait_until_us(edge_us, RX_SAMPLE_DELAY_US);

    if (gpio_get(N64_DATA_PIN)) {
        rx_bytes[rx_byte_index] |= (uint8_t)(1u << rx_bit);
    }

    rx_last_edge_us = now_us32();
    rx_bit--;

    if (rx_bit < 0) {
        rx_byte_index++;
        rx_bit = 7;

        if (rx_byte_index == 1) {
            rx_expected_bytes = expected_command_bytes(rx_bytes[0]);
        }

        if (rx_byte_index >= rx_expected_bytes || rx_byte_index >= MAX_COMMAND_BYTES) {
            rx_waiting_stop = true;
            debug.rx_command_complete_count++;
        } else {
            rx_bytes[rx_byte_index] = 0;
        }
    }
}
#endif

void joybus_init(void) {
    gpio_init(N64_DATA_PIN);
    gpio_set_function(N64_DATA_PIN, GPIO_FUNC_SIO);
    gpio_set_dir(N64_DATA_PIN, GPIO_IN);
    gpio_disable_pulls(N64_DATA_PIN);
    gpio_init(TX_MARKER_PIN);
    gpio_set_function(TX_MARKER_PIN, GPIO_FUNC_SIO);
    gpio_put(TX_MARKER_PIN, 0);
    gpio_set_dir(TX_MARKER_PIN, GPIO_OUT);
#if JOYBUS_TX_BACKEND_NEEDS_PIO
    n64_tx_pio_init();
#endif
#if JOYBUS_RX_BACKEND_IS_PIO
    n64_rx_pio_init();
#else
    gpio_set_irq_enabled_with_callback(N64_DATA_PIN, GPIO_IRQ_EDGE_FALL, true, &n64_gpio_irq);
#endif
}

void joybus_get_debug(joybus_debug_t *out) {
    const uint32_t save = save_and_disable_interrupts();
    *out = debug;
    out->line = gpio_get(N64_DATA_PIN);
    restore_interrupts(save);
}

void joybus_clear_debug(void) {
    const uint32_t save = save_and_disable_interrupts();
    debug = (joybus_debug_t){0};
    restore_interrupts(save);
}
