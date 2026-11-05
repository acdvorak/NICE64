#include "randnet_keymap.h"

typedef struct {
    nice64_logical_key_t key;
    uint16_t matrix;
} randnet_keymap_entry_t;

/*
 * RND-001 matrix positions from documented hardware captures. Host-specific
 * scan codes deliberately do not appear here.
 */
static const randnet_keymap_entry_t keymap[] = {
    { NICE64_KEY_ESCAPE, 0x0A08 },
    { NICE64_KEY_F1, 0x0B01 }, { NICE64_KEY_F2, 0x0A01 },
    { NICE64_KEY_F3, 0x0B08 }, { NICE64_KEY_F4, 0x0A07 },
    { NICE64_KEY_F5, 0x0B07 }, { NICE64_KEY_F6, 0x0A02 },
    { NICE64_KEY_F7, 0x0B02 }, { NICE64_KEY_F8, 0x0A03 },
    { NICE64_KEY_F9, 0x0B03 }, { NICE64_KEY_F10, 0x0A04 },
    { NICE64_KEY_F11, 0x0203 }, { NICE64_KEY_F12, 0x0B06 },
    { NICE64_KEY_NUM_LOCK, 0x0A05 }, { NICE64_KEY_CAPS_LOCK, 0x0F05 },
    { NICE64_KEY_SCROLL_LOCK, 0x0208 },
    { NICE64_KEY_GRAVE, 0x0D05 },
    { NICE64_KEY_1, 0x0C05 }, { NICE64_KEY_2, 0x0505 },
    { NICE64_KEY_3, 0x0605 }, { NICE64_KEY_4, 0x0705 },
    { NICE64_KEY_5, 0x0805 }, { NICE64_KEY_6, 0x0905 },
    { NICE64_KEY_7, 0x0906 }, { NICE64_KEY_8, 0x0806 },
    { NICE64_KEY_9, 0x0706 }, { NICE64_KEY_0, 0x0606 },
    { NICE64_KEY_MINUS, 0x1004 }, { NICE64_KEY_EQUALS, 0x0C06 },
    { NICE64_KEY_BACKSPACE, 0x0D06 }, { NICE64_KEY_TAB, 0x0D01 },
    { NICE64_KEY_ENTER, 0x0D04 }, { NICE64_KEY_SPACE, 0x0602 },
    { NICE64_KEY_Q, 0x0C01 }, { NICE64_KEY_W, 0x0501 },
    { NICE64_KEY_E, 0x0601 }, { NICE64_KEY_R, 0x0701 },
    { NICE64_KEY_T, 0x0801 }, { NICE64_KEY_Y, 0x0901 },
    { NICE64_KEY_U, 0x0904 }, { NICE64_KEY_I, 0x0804 },
    { NICE64_KEY_O, 0x0704 }, { NICE64_KEY_P, 0x0604 },
    { NICE64_KEY_A, 0x0D07 }, { NICE64_KEY_S, 0x0C07 },
    { NICE64_KEY_D, 0x0507 }, { NICE64_KEY_F, 0x0607 },
    { NICE64_KEY_G, 0x0707 }, { NICE64_KEY_H, 0x0807 },
    { NICE64_KEY_J, 0x0907 }, { NICE64_KEY_K, 0x0903 },
    { NICE64_KEY_L, 0x0803 }, { NICE64_KEY_Z, 0x0D08 },
    { NICE64_KEY_X, 0x0C08 }, { NICE64_KEY_C, 0x0508 },
    { NICE64_KEY_V, 0x0608 }, { NICE64_KEY_B, 0x0708 },
    { NICE64_KEY_N, 0x0808 }, { NICE64_KEY_M, 0x0908 },
    { NICE64_KEY_APOSTROPHE, 0x0504 },
    { NICE64_KEY_LEFT_BRACKET, 0x0C04 },
    { NICE64_KEY_RIGHT_BRACKET, 0x0406 },
    { NICE64_KEY_SEMICOLON, 0x1105 },
    { NICE64_KEY_COMMA, 0x0902 }, { NICE64_KEY_PERIOD, 0x0802 },
    { NICE64_KEY_SLASH, 0x0702 },
    { NICE64_KEY_KEYPAD_MINUS, 0x0506 },
    { NICE64_KEY_KEYPAD_PLUS, 0x0703 },
    { NICE64_KEY_KEYPAD_MULTIPLY, 0x0603 },
    { NICE64_KEY_LEFT_SHIFT, 0x0E01 },
    { NICE64_KEY_RIGHT_SHIFT, 0x0E06 },
    { NICE64_KEY_LEFT_CONTROL, 0x1107 },
    { NICE64_KEY_RIGHT_CONTROL, 0x1107 },
    { NICE64_KEY_LEFT_ALT, 0x1008 },
    { NICE64_KEY_RIGHT_ALT, 0x1008 },
    { NICE64_KEY_OPTION, 0x0F07 },
    { NICE64_KEY_PRINT_SCREEN, 0x0B05 },
    { NICE64_KEY_PAGE_UP, 0x0207 },
    { NICE64_KEY_UP, 0x0204 }, { NICE64_KEY_LEFT, 0x0205 },
    { NICE64_KEY_DOWN, 0x0305 }, { NICE64_KEY_RIGHT, 0x0405 },
    { NICE64_KEY_END, 0x0206 },
    { NICE64_KEY_JAPANESE_1, 0x0208 },
    { NICE64_KEY_JAPANESE_2, 0x0D05 },
    { NICE64_KEY_JAPANESE_3, 0x0B05 },
    { NICE64_KEY_JAPANESE_4, 0x0207 },
    { NICE64_KEY_JAPANESE_5, 0x1002 },
    { NICE64_KEY_JAPANESE_6, 0x0E02 },
    { NICE64_KEY_JAPANESE_7, 0x1006 },
    { NICE64_KEY_JAPANESE_8, 0x0F07 },
};

uint16_t randnet_keymap_matrix_value(nice64_logical_key_t key)
{
    for (unsigned i = 0; i < sizeof(keymap) / sizeof(keymap[0]); ++i) {
        if (keymap[i].key == key)
            return keymap[i].matrix;
    }
    return 0u;
}

bool randnet_keymap_is_home(nice64_logical_key_t key)
{
    return key == NICE64_KEY_HOME;
}
