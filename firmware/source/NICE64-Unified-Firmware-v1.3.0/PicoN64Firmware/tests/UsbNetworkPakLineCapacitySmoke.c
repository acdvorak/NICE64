#include <assert.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "network_pak.h"
#include "nice64_line_framer.h"

typedef struct {
    unsigned callback_count;
    uint16_t expected_offset;
    uint16_t expected_length;
} line_expectation_t;

static int hex_nibble(char value) {
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    return -1;
}

static uint32_t parse_hex(const char *text, size_t digits) {
    uint32_t value = 0u;
    for (size_t index = 0u; index < digits; ++index) {
        const int nibble = hex_nibble(text[index]);
        assert(nibble >= 0);
        value = (value << 4u) | (uint32_t)nibble;
    }
    assert(text[digits] == '\0');
    return value;
}

static void assert_segment_parser_invoked(void *context, const char *line) {
    line_expectation_t *expectation = context;
    static const char prefix[] = "NETPAK_BULK_SEG|";
    assert(strncmp(line, prefix, sizeof(prefix) - 1u) == 0);

    char copy[NETWORK_PAK_USB_LINE_CAPACITY];
    const size_t length = strlen(line);
    assert(length < sizeof(copy));
    memcpy(copy, line + sizeof(prefix) - 1u,
           length - (sizeof(prefix) - 1u) + 1u);

    char *fields[10];
    char *cursor = copy;
    for (size_t index = 0u; index < 9u; ++index) {
        fields[index] = cursor;
        cursor = strchr(cursor, '|');
        assert(cursor != NULL);
        *cursor++ = '\0';
    }
    fields[9] = cursor;
    assert(strchr(fields[9], '|') == NULL);
    assert(parse_hex(fields[5], 4u) == expectation->expected_offset);
    assert(parse_hex(fields[6], 2u) == expectation->expected_length);
    assert(strlen(fields[9]) == (size_t)expectation->expected_length * 2u);
    expectation->callback_count++;
}

static void feed_segment(nice64_line_framer_t *framer,
                         line_expectation_t *expectation,
                         uint16_t offset,
                         uint16_t data_length) {
    char data[224u * 2u + 1u];
    for (uint16_t index = 0u; index < data_length; ++index) {
        static const char hex[] = "0123456789ABCDEF";
        const uint8_t value = (uint8_t)(index + offset);
        data[index * 2u] = hex[value >> 4u];
        data[index * 2u + 1u] = hex[value & 0x0Fu];
    }
    data[data_length * 2u] = '\0';

    char line[NETWORK_PAK_USB_LINE_CAPACITY];
    const int written = snprintf(
        line, sizeof(line),
        "NETPAK_BULK_SEG|7|0001|02|00001000|0200|%04X|%02X|1A2B3C4D|00|%s\r\n",
        offset, data_length, data);
    assert(written > 0);
    assert((size_t)written < sizeof(line));
    if (data_length == 224u) assert(written - 2 > 500);

    expectation->expected_offset = offset;
    expectation->expected_length = data_length;
    for (int index = 0; index < written; ++index) {
        nice64_line_framer_feed(framer, line[index],
                                assert_segment_parser_invoked, expectation);
    }
}

int main(void) {
    assert(NETWORK_PAK_USB_LINE_CAPACITY == 600u);

    char line[NETWORK_PAK_USB_LINE_CAPACITY];
    nice64_line_framer_t framer;
    line_expectation_t expectation = {0};
    nice64_line_framer_init(&framer, line, sizeof(line));

    feed_segment(&framer, &expectation, 0u, 224u);
    feed_segment(&framer, &expectation, 224u, 224u);
    feed_segment(&framer, &expectation, 448u, 64u);

    assert(expectation.callback_count == 3u);
    assert(!framer.discarding_overlong);
    assert(framer.length == 0u);
    puts("UsbNetworkPakLineCapacitySmoke PASS");
    return 0;
}
