#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "joybus.h"
#include "n64_accessory.h"
#include "n64_controller.h"
#include "pico/stdlib.h"
#include "pico/time.h"

#define PICO_LED_PIN 25
#define CONTROLLER_PAK_BLOCK_SIZE 32

static bool elapsed_us(uint32_t start, uint32_t us) {
    return (uint32_t)(time_us_32() - start) >= us;
}

static int hex_value(int c) {
    if (c >= '0' && c <= '9') {
        return c - '0';
    }

    if (c >= 'a' && c <= 'f') {
        return c - 'a' + 10;
    }

    if (c >= 'A' && c <= 'F') {
        return c - 'A' + 10;
    }

    return -1;
}

static bool parse_hex_bytes(const char *text, uint8_t *out, int count) {
    for (int i = 0; i < count; ++i) {
        const int hi = hex_value((unsigned char)text[i * 2]);
        const int lo = hex_value((unsigned char)text[i * 2 + 1]);
        if (hi < 0 || lo < 0) {
            return false;
        }

        out[i] = (uint8_t)((hi << 4) | lo);
    }

    return true;
}

static bool parse_hex_address4(const char *text, uint16_t *address) {
    const int a0 = hex_value((unsigned char)text[0]);
    const int a1 = hex_value((unsigned char)text[1]);
    const int a2 = hex_value((unsigned char)text[2]);
    const int a3 = hex_value((unsigned char)text[3]);
    if (a0 < 0 || a1 < 0 || a2 < 0 || a3 < 0) {
        return false;
    }

    *address = (uint16_t)((a0 << 12) | (a1 << 8) | (a2 << 4) | a3);
    return true;
}

static bool parse_hex_uint32(const char *text, int digits, uint32_t *value) {
    uint32_t parsed = 0;
    for (int i = 0; i < digits; ++i) {
        const int nibble = hex_value((unsigned char)text[i]);
        if (nibble < 0) {
            return false;
        }
        parsed = (parsed << 4) | (uint32_t)nibble;
    }

    *value = parsed;
    return true;
}

static void poll_usb_serial(void) {
    static char line[600];
    static int length = 0;

    int ch;
    while ((ch = getchar_timeout_us(0)) != PICO_ERROR_TIMEOUT) {
        if (ch == '\r') {
            continue;
        }

        if (ch == '\n') {
            line[length] = '\0';

            uint8_t parsed[4];
            if (n64_controller_parse_serial_state(line, parsed)) {
                n64_controller_set_state(parsed);
                puts("OK");
            } else if (strcmp(line, "MODE NEUTRAL") == 0 || strcmp(line, "mode neutral") == 0) {
                const uint8_t neutral[4] = {0x00, 0x00, 0x00, 0x00};
                n64_controller_set_state(neutral);
                n64_controller_set_ignore_serial(true);
                puts("OK MODE NEUTRAL");
            } else if (strcmp(line, "MODE SERIAL") == 0 || strcmp(line, "mode serial") == 0) {
                n64_controller_set_ignore_serial(false);
                puts("OK MODE SERIAL");
            } else if (strcmp(line, "MODE A") == 0 || strcmp(line, "mode a") == 0) {
                const uint8_t a_held[4] = {0x80, 0x00, 0x00, 0x00};
                n64_controller_set_state(a_held);
                n64_controller_set_ignore_serial(true);
                puts("OK MODE A");
            } else if (strcmp(line, "PAKMODE NONE") == 0 || strcmp(line, "pakmode none") == 0) {
                n64_accessory_set_mode(N64_ACCESSORY_NONE);
                puts("OK PAKMODE NONE");
            } else if (strcmp(line, "PAKMODE MEM") == 0 || strcmp(line, "pakmode mem") == 0) {
                n64_accessory_set_mode(N64_ACCESSORY_MEMORY_PAK);
                puts("OK PAKMODE MEM");
            } else if (strcmp(line, "PAKMODE RUMBLE") == 0 || strcmp(line, "pakmode rumble") == 0) {
                n64_accessory_set_mode(N64_ACCESSORY_RUMBLE_PAK);
                puts("OK PAKMODE RUMBLE");
            } else if (strcmp(line, "PAKMODE TRANSFER") == 0 || strcmp(line, "pakmode transfer") == 0) {
                n64_accessory_set_mode(N64_ACCESSORY_TRANSFER_PAK);
                puts("OK PAKMODE TRANSFER");
            } else if (strcmp(line, "PAKCLR") == 0 || strcmp(line, "pakclr") == 0) {
                n64_accessory_clear_memory();
                puts("OK PAKCLR");
            } else if (strcmp(line, "GBCLR") == 0 || strcmp(line, "gbclr") == 0) {
                n64_accessory_clear_transfer_rom();
                puts("OK GBCLR");
            } else if (strncmp(line, "GBP ", 4) == 0 || strncmp(line, "gbp ", 4) == 0) {
                uint32_t cart_type;
                uint32_t rom_size;
                uint32_t ram_size;
                uint32_t flags;
                if (strlen(line) < 27
                    || !parse_hex_uint32(&line[4], 2, &cart_type)
                    || line[6] != ' '
                    || !parse_hex_uint32(&line[7], 8, &rom_size)
                    || line[15] != ' '
                    || !parse_hex_uint32(&line[16], 8, &ram_size)
                    || line[24] != ' '
                    || !parse_hex_uint32(&line[25], 2, &flags)) {
                    puts("ERR GBP expected: GBP TT RRRRRRRR SSSSSSSS FF");
                } else {
                    n64_accessory_set_transfer_profile((uint8_t)cart_type, rom_size, ram_size, (uint8_t)flags);
                    printf("OK GBP %02lX %08lX %08lX %02lX\n", cart_type, rom_size, ram_size, flags);
                }
            } else if (strcmp(line, "RUMBLE?") == 0 || strcmp(line, "rumble?") == 0) {
                printf("RUMBLE %u\n", n64_accessory_rumble_active() ? 1u : 0u);
            } else if (strcmp(line, "GBFCAP?") == 0 || strcmp(line, "gbfcap?") == 0) {
                printf("GBFCAP %08lX\n", n64_accessory_transfer_flash_capacity());
            } else if (strncmp(line, "GBS ", 4) == 0 || strncmp(line, "gbs ", 4) == 0) {
                uint16_t address;
                uint8_t block[CONTROLLER_PAK_BLOCK_SIZE];
                if (strlen(line) < 73
                    || !parse_hex_address4(&line[4], &address)
                    || line[8] != ' '
                    || !parse_hex_bytes(&line[9], block, CONTROLLER_PAK_BLOCK_SIZE)) {
                    puts("ERR GBS expected: GBS 0000 64HEXCHARS");
                } else {
                    n64_accessory_write_transfer_save_block(address, block);
                    printf("OK GBS %04X\n", address);
                }
            } else if (strncmp(line, "GBSDUMP ", 8) == 0 || strncmp(line, "gbsdump ", 8) == 0) {
                uint16_t address;
                uint8_t block[CONTROLLER_PAK_BLOCK_SIZE];
                if (strlen(line) < 12 || !parse_hex_address4(&line[8], &address)) {
                    puts("ERR GBSDUMP expected: GBSDUMP 0000");
                } else {
                    n64_accessory_read_transfer_save_block(address, block);
                    printf("GBS %04X ", address);
                    for (int i = 0; i < CONTROLLER_PAK_BLOCK_SIZE; ++i) {
                        printf("%02X", block[i]);
                    }
                    putchar('\n');
                }
            } else if (strncmp(line, "GB ", 3) == 0 || strncmp(line, "gb ", 3) == 0) {
                uint16_t address;
                uint8_t block[CONTROLLER_PAK_BLOCK_SIZE];
                if (strlen(line) < 72
                    || !parse_hex_address4(&line[3], &address)
                    || line[7] != ' '
                    || !parse_hex_bytes(&line[8], block, CONTROLLER_PAK_BLOCK_SIZE)) {
                    puts("ERR GB expected: GB 0000 64HEXCHARS");
                } else {
                    n64_accessory_write_transfer_rom_block(address, block);
                    printf("OK GB %04X\n", address);
                }
            } else if (strncmp(line, "GBFBEGIN ", 9) == 0 || strncmp(line, "gbfbegin ", 9) == 0) {
                uint32_t size;
                if (strlen(line) < 17 || !parse_hex_uint32(&line[9], 8, &size)) {
                    puts("ERR GBFBEGIN expected: GBFBEGIN 00000000");
                } else {
                    n64_accessory_begin_transfer_flash(size);
                    printf("OK GBFBEGIN %08lX\n", size);
                }
            } else if (strncmp(line, "GBF ", 4) == 0 || strncmp(line, "gbf ", 4) == 0) {
                uint32_t offset;
                uint8_t block[CONTROLLER_PAK_BLOCK_SIZE];
                if (strlen(line) < 75
                    || !parse_hex_uint32(&line[4], 6, &offset)
                    || line[10] != ' '
                    || !parse_hex_bytes(&line[11], block, CONTROLLER_PAK_BLOCK_SIZE)) {
                    puts("ERR GBF expected: GBF 000000 64HEXCHARS");
                } else {
                    n64_accessory_write_transfer_flash_block(offset, block);
                    printf("OK GBF %06lX\n", offset);
                }
            } else if (strncmp(line, "GBFP ", 5) == 0 || strncmp(line, "gbfp ", 5) == 0) {
                uint32_t offset;
                uint8_t page[256];
                if (strlen(line) < 524
                    || !parse_hex_uint32(&line[5], 6, &offset)
                    || line[11] != ' '
                    || !parse_hex_bytes(&line[12], page, 256)) {
                    puts("ERR GBFP expected: GBFP 000000 512HEXCHARS");
                } else {
                    n64_accessory_write_transfer_flash_page(offset, page);
                    printf("OK GBFP %06lX\n", offset);
                }
            } else if (strcmp(line, "GBFCOMMIT") == 0 || strcmp(line, "gbfcommit") == 0) {
                n64_accessory_commit_transfer_flash();
                puts("OK GBFCOMMIT");
            } else if (strncmp(line, "PAK ", 4) == 0 || strncmp(line, "pak ", 4) == 0) {
                uint16_t address;
                uint8_t block[CONTROLLER_PAK_BLOCK_SIZE];
                if (strlen(line) < 73
                    || !parse_hex_address4(&line[4], &address)
                    || line[8] != ' '
                    || !parse_hex_bytes(&line[9], block, CONTROLLER_PAK_BLOCK_SIZE)) {
                    puts("ERR PAK expected: PAK 0000 64HEXCHARS");
                } else {
                    n64_accessory_write_block(address, block);
                    printf("OK PAK %04X\n", address);
                }
            } else if (strncmp(line, "PAKDUMP ", 8) == 0 || strncmp(line, "pakdump ", 8) == 0) {
                uint16_t address;
                uint8_t block[CONTROLLER_PAK_BLOCK_SIZE];
                if (strlen(line) < 12 || !parse_hex_address4(&line[8], &address)) {
                    puts("ERR PAKDUMP expected: PAKDUMP 0000");
                } else {
                    n64_accessory_read_block(address, block);
                    printf("PAK %04X ", address);
                    for (int i = 0; i < CONTROLLER_PAK_BLOCK_SIZE; ++i) {
                        printf("%02X", block[i]);
                    }
                    putchar('\n');
                }
            } else if (strcmp(line, "STAT") == 0 || strcmp(line, "stat") == 0) {
                joybus_debug_t debug;
                joybus_get_debug(&debug);

                printf("STAT edges=%lu streams=%lu commands=%lu identify=%lu status=%lu reset=%lu aread=%lu awrite=%lu other=%lu gcn40=%lu gcn41=%lu gcn43=%lu gcn_no=%lu rx_done=%lu rx_stop=%lu rx_hot=%lu rx_timeout=%lu rx_new=%lu rx_noreply=%lu tx_start=%lu tx_done=%lu tx_ident=%lu tx_status=%lu tx_block=%lu tx_last=0x%02X tx_len=%u gcn_len=%u gcn=%02X%02X%02X%02X%02X%02X%02X%02X%02X%02X last=0x%02X pak=0x%04lX mode=%u rumble=%u line=%lu serial=%lu ignored=%lu serial_a=%lu ignore=%u\n",
                       debug.edges,
                       debug.streams,
                       debug.commands,
                       debug.identify,
                       debug.status,
                       debug.reset,
                       debug.accessory_read,
                       debug.accessory_write,
                       debug.other,
                       debug.gcn_read_count,
                       debug.gcn_origin_count,
                       debug.gcn_long_count,
                       debug.gcn_no_reply_count,
                       debug.rx_command_complete_count,
                       debug.rx_stop_dispatch_count,
                       debug.rx_hot_reply_count,
                       debug.rx_stop_timeout_count,
                       debug.rx_waiting_new_stream_count,
                       debug.rx_no_reply_count,
                       debug.tx_start_count,
                       debug.tx_done_count,
                       debug.tx_hot_ident_count,
                       debug.tx_hot_status_count,
                       debug.tx_busy_or_blocked_count,
                       debug.last_tx_command,
                       (unsigned)debug.last_tx_reply_len,
                       (unsigned)debug.last_gcn_reply_len,
                       debug.last_gcn_reply[0],
                       debug.last_gcn_reply[1],
                       debug.last_gcn_reply[2],
                       debug.last_gcn_reply[3],
                       debug.last_gcn_reply[4],
                       debug.last_gcn_reply[5],
                       debug.last_gcn_reply[6],
                       debug.last_gcn_reply[7],
                       debug.last_gcn_reply[8],
                       debug.last_gcn_reply[9],
                       debug.last_command,
                       debug.last_accessory_address,
                       (unsigned)n64_accessory_mode(),
                       n64_accessory_rumble_active() ? 1u : 0u,
                       debug.line,
                       n64_controller_serial_state_count(),
                       n64_controller_serial_ignored_count(),
                       n64_controller_serial_a_on_count(),
                       n64_controller_ignore_serial() ? 1u : 0u);
            } else if (strcmp(line, "CLR") == 0 || strcmp(line, "clr") == 0) {
                joybus_clear_debug();
                n64_controller_clear_debug();
                puts("OK CLR");
            } else if (length > 0) {
                puts("ERR expected: N64 00000000, PAKMODE, PAK, PAKDUMP, PAKCLR, GB, GBS, GBSDUMP, GBF, GBCLR, GBFCAP?, RUMBLE?, STAT, or CLR");
            }

            length = 0;
            continue;
        }

        if (length < (int)sizeof(line) - 1) {
            line[length++] = (char)ch;
        } else {
            length = 0;
        }
    }
}

int main(void) {
    stdio_init_all();

    gpio_init(PICO_LED_PIN);
    gpio_set_dir(PICO_LED_PIN, GPIO_OUT);
    gpio_put(PICO_LED_PIN, 0);

    n64_controller_init();
    n64_accessory_init();
    joybus_init();

    sleep_ms(500);
    puts("Pico N64 RC-reference controller bridge starting in MODE SERIAL");

    uint32_t last_led_us = time_us_32();
    while (true) {
        poll_usb_serial();

        if (elapsed_us(last_led_us, 250000)) {
            gpio_put(PICO_LED_PIN, !gpio_get(PICO_LED_PIN));
            last_led_us = time_us_32();
        }

        tight_loop_contents();
    }
}
