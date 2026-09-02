#include "standalone_diagnostics.h"

#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "bridge_line_dispatch.h"
#include "hardware/gpio.h"
#include "joybus_transport.h"
#include "nice64_line_framer.h"
#include "nice64_response.h"
#include "nice64_tcp_service.h"
#include "nice64_usb_transport.h"
#include "pico/stdlib.h"
#include "pico/time.h"
#include "role_manager.h"

#define N64_DATA_PIN 2

static volatile uint32_t diag_core0_ticks = 0;
static volatile uint32_t diag_core1_ticks = 0;
static volatile uint32_t diag_stat_count = 0;
static volatile uint32_t diag_clr_count = 0;
static volatile uint32_t diag_unknown_count = 0;

static void standalone_diagnostics_print_stat(
    const nice64_response_sink_t *response) {
    ++diag_stat_count;
    nice64_response_printf(
           response,
           "OK SDIAG ROLE STANDALONE SERVICE DIAGNOSTICS RX %u TX %u C0 %lu C1 %lu STAT %lu CLR %lu UNK %lu\n",
           (unsigned)JOYBUS_ACTIVE_RX_BACKEND,
           (unsigned)JOYBUS_ACTIVE_TX_BACKEND,
           (unsigned long)diag_core0_ticks,
           (unsigned long)diag_core1_ticks,
           (unsigned long)diag_stat_count,
           (unsigned long)diag_clr_count,
           (unsigned long)diag_unknown_count);
}

static void standalone_diagnostics_clear(
    const nice64_response_sink_t *response) {
    diag_core0_ticks = 0;
    diag_core1_ticks = 0;
    diag_stat_count = 0;
    diag_unknown_count = 0;
    ++diag_clr_count;
    nice64_response_puts(response, "OK CLR");
}

bool standalone_diagnostics_handle_line(
    const char *line,
    const nice64_response_sink_t *response) {
    if (strcmp(line, "STAT") == 0 || strcmp(line, "SDIAG?") == 0) {
        standalone_diagnostics_print_stat(response);
    } else if (strcmp(line, "CLR") == 0) {
        standalone_diagnostics_clear(response);
    } else if (line[0] != '\0') {
        ++diag_unknown_count;
        nice64_response_puts(response, "ERR SDIAG");
    }

    return line[0] != '\0';
}

static void standalone_diagnostics_dispatch_usb_line(void *context,
                                                     const char *line) {
    const nice64_response_sink_t *response = context;
    if (!nice64_tcp_usb_command_allowed(line)) {
        nice64_response_puts(response, "NICE64|BUSY|OWNER=TCP");
        return;
    }
    bridge_line_dispatch(line, response, standalone_diagnostics_handle_line);
}

static void standalone_diagnostics_poll_serial(
    const nice64_response_sink_t *response) {
    static char line[48];
    static nice64_line_framer_t framer;
    static bool initialized = false;

    if (!initialized) {
        nice64_line_framer_init(&framer, line, sizeof(line));
        initialized = true;
    }

    nice64_usb_poll_lines(&framer,
                          standalone_diagnostics_dispatch_usb_line,
                          (void *)response);
}

void standalone_diagnostics_init(void) {
    gpio_init(N64_DATA_PIN);
    gpio_set_dir(N64_DATA_PIN, GPIO_IN);
    gpio_pull_up(N64_DATA_PIN);

    diag_core0_ticks = 0;
    diag_core1_ticks = 0;
    diag_stat_count = 0;
    diag_clr_count = 0;
    diag_unknown_count = 0;
}

void standalone_diagnostics_write_banner(void) {
    puts("Pico N64 standalone diagnostics starting");
}

void standalone_diagnostics_core0_service(void) {
    ++diag_core0_ticks;
}

void standalone_diagnostics_core1_service(void) {
    ++diag_core1_ticks;
    standalone_diagnostics_poll_serial(nice64_usb_response_sink());
}
