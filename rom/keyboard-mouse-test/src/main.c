#include "n64_mouse_test.h"
#include "randnet_keyboard.h"

#include <libdragon.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define PORT_COUNT 4
#define IDENTIFY_COMMAND 0x00u
#define CONTROLLER_IDENTIFIER 0x0500u
#define N64_BUTTON_D_UP 0x0800u
#define N64_BUTTON_D_LEFT 0x0200u
#define N64_BUTTON_D_RIGHT 0x0100u
#define CURSOR_MIN_X 205
#define CURSOR_MAX_X 313
#define CURSOR_MIN_Y 103
#define CURSOR_MAX_Y 219

typedef struct {
    uint8_t raw[3];
    uint16_t identifier;
} port_identity_t;

static port_identity_t identities[PORT_COUNT];
static randnet_keyboard_report_t keyboard_reports[PORT_COUNT];
static n64_mouse_report_t mouse_reports[PORT_COUNT];
static int cursor_x = 260;
static int cursor_y = 160;
static int32_t total_dx;
static int32_t total_dy;
static uint8_t led_request;
static uint16_t previous_controller_buttons[PORT_COUNT];
static uint16_t current_controller_buttons[PORT_COUNT];

static int clamp_int(int value, int minimum, int maximum)
{
    if (value < minimum) return minimum;
    if (value > maximum) return maximum;
    return value;
}

static port_identity_t identify_port(int port)
{
    const uint8_t request = IDENTIFY_COMMAND;
    port_identity_t identity = {0};
    joybus_exec_cmd(port, sizeof(request), sizeof(identity.raw),
                    &request, identity.raw);
    identity.identifier = (uint16_t)(((uint16_t)identity.raw[0] << 8) |
                                     identity.raw[1]);
    return identity;
}

static uint16_t poll_controller_buttons(int port)
{
    const uint8_t request = 0x01u;
    uint8_t response[4] = {0};
    joybus_exec_cmd(port, sizeof(request), sizeof(response), &request, response);
    return (uint16_t)(((uint16_t)response[0] << 8) | response[1]);
}

static void update_led_controls(void)
{
    for (int port = 0; port < PORT_COUNT; ++port) {
        if (identities[port].identifier != CONTROLLER_IDENTIFIER)
            continue;

        uint16_t buttons = poll_controller_buttons(port);
        current_controller_buttons[port] = buttons;
        uint16_t pressed = (uint16_t)(buttons & ~previous_controller_buttons[port]);
        previous_controller_buttons[port] = buttons;
        if ((pressed & N64_BUTTON_D_UP) != 0) led_request ^= 0x04u;
        if ((pressed & N64_BUTTON_D_LEFT) != 0) led_request ^= 0x02u;
        if ((pressed & N64_BUTTON_D_RIGHT) != 0) led_request ^= 0x01u;
    }
}

static void poll_devices(void)
{
    for (int port = 0; port < PORT_COUNT; ++port) {
        if (identities[port].identifier == RANDNET_KEYBOARD_IDENTIFIER) {
            randnet_keyboard_poll(port, led_request, &keyboard_reports[port]);
        } else if (identities[port].identifier == N64_MOUSE_IDENTIFIER) {
            n64_mouse_test_poll(port, &mouse_reports[port]);
            total_dx += mouse_reports[port].dx;
            total_dy += mouse_reports[port].dy;
            cursor_x = clamp_int(cursor_x + mouse_reports[port].dx,
                                 CURSOR_MIN_X, CURSOR_MAX_X);
            cursor_y = clamp_int(cursor_y + mouse_reports[port].dy,
                                 CURSOR_MIN_Y, CURSOR_MAX_Y);
        }
    }
}

static void draw_line(surface_t *display, int y, const char *text)
{
    graphics_draw_text(display, 8, y, text);
}

static void draw_keyboard(surface_t *display, int port, int y)
{
    char line[96];
    const randnet_keyboard_report_t *r = &keyboard_reports[port];
    snprintf(line, sizeof(line),
             "P%d KB tx %02X %02X raw %02X %02X %02X %02X %02X %02X %02X",
             port + 1, r->request[0], r->request[1],
             r->raw[0], r->raw[1], r->raw[2], r->raw[3],
             r->raw[4], r->raw[5], r->raw[6]);
    draw_line(display, y, line);
    snprintf(line, sizeof(line), " K1 %04X %-12s K2 %04X %-12s",
             r->keys[0], randnet_keyboard_key_name(r->keys[0]),
             r->keys[1], randnet_keyboard_key_name(r->keys[1]));
    draw_line(display, y + 9, line);
    snprintf(line, sizeof(line), " K3 %04X %-12s status %02X 4+:%s Home:%s",
             r->keys[2], randnet_keyboard_key_name(r->keys[2]), r->status,
             (r->status & RANDNET_KEYBOARD_STATUS_TOO_MANY_KEYS) ? "YES" : "no",
             (r->status & RANDNET_KEYBOARD_STATUS_HOME) ? "YES" : "no");
    draw_line(display, y + 18, line);
}

static void draw_mouse(surface_t *display, int port, int y)
{
    char line[96];
    const n64_mouse_report_t *r = &mouse_reports[port];
    snprintf(line, sizeof(line),
             "P%d Mouse raw %02X %02X %02X %02X  A/L:%d B/R:%d",
             port + 1, r->raw[0], r->raw[1], r->raw[2], r->raw[3],
             r->left, r->right);
    draw_line(display, y, line);
    snprintf(line, sizeof(line), " dx %+4d dy %+4d totals %+ld,%+ld cursor %d,%d",
             r->dx, r->dy, (long)total_dx, (long)total_dy, cursor_x, cursor_y);
    draw_line(display, y + 9, line);
}

static void draw_screen(surface_t *display)
{
    char line[96];
    int y = 8;
    graphics_fill_screen(display, graphics_make_color(10, 14, 22, 255));
    graphics_set_color(graphics_make_color(235, 240, 250, 255), 0);
    draw_line(display, y, "NICE64 Keyboard / Mouse Joybus Diagnostic");
    y += 12;

    snprintf(line, sizeof(line), "IDs raw: P1 %02X%02X/%02X P2 %02X%02X/%02X",
             identities[0].raw[0], identities[0].raw[1], identities[0].raw[2],
             identities[1].raw[0], identities[1].raw[1], identities[1].raw[2]);
    draw_line(display, y, line);
    y += 9;
    snprintf(line, sizeof(line), "         P3 %02X%02X/%02X P4 %02X%02X/%02X",
             identities[2].raw[0], identities[2].raw[1], identities[2].raw[2],
             identities[3].raw[0], identities[3].raw[1], identities[3].raw[2]);
    draw_line(display, y, line);
    y += 12;
    snprintf(line, sizeof(line), "LED req %02X  Power:%s Caps:%s Num:%s",
             led_request, (led_request & 4) ? "on" : "off",
             (led_request & 2) ? "on" : "off",
             (led_request & 1) ? "on" : "off");
    draw_line(display, y, line);
    y += 9;
    draw_line(display, y, "Controller D-Up=Power D-Left=Caps D-Right=Num");
    y += 9;
    snprintf(line, sizeof(line), "Pad raw: P1 %04X P2 %04X P3 %04X P4 %04X",
             current_controller_buttons[0], current_controller_buttons[1],
             current_controller_buttons[2], current_controller_buttons[3]);
    draw_line(display, y, line);
    y += 13;

    for (int port = 0; port < PORT_COUNT; ++port) {
        if (identities[port].identifier == RANDNET_KEYBOARD_IDENTIFIER) {
            draw_keyboard(display, port, y);
            y += 31;
        } else if (identities[port].identifier == N64_MOUSE_IDENTIFIER) {
            draw_mouse(display, port, y);
            y += 22;
        }
    }

    graphics_draw_box(display, 202, 100, 114, 122,
                      graphics_make_color(35, 42, 55, 255));
    graphics_draw_box(display, cursor_x - 2, cursor_y - 2, 5, 5,
                      graphics_make_color(255, 210, 55, 255));
}

int main(void)
{
    unsigned frame = 0;
    display_init(RESOLUTION_320x240, DEPTH_16_BPP, 2, GAMMA_NONE,
                 FILTERS_RESAMPLE);
    graphics_set_default_font();
    timer_init();

    for (int port = 0; port < PORT_COUNT; ++port)
        identities[port] = identify_port(port);

    while (1) {
        if ((frame % 60u) == 0u) {
            for (int port = 0; port < PORT_COUNT; ++port)
                identities[port] = identify_port(port);
        }
        update_led_controls();
        poll_devices();

        surface_t *display = display_get();
        draw_screen(display);
        display_show(display);
        ++frame;
        wait_ms(16);
    }
}
