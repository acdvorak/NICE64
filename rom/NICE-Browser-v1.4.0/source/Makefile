BUILD_DIR = build
SOURCE_DIR = src
INCLUDE_DIR = include

include $(N64_INST)/include/n64.mk

PROJECT = nice-browser-rom-v1.4.0-Release
ROM = $(PROJECT).z64

ASSET_PNG_DIR = generated-assets/png
ASSET_AUDIO_DIR = generated-assets/audio
ROMFS_DIR = filesystem
SPRITES = $(ROMFS_DIR)/nice64_appbar.sprite $(ROMFS_DIR)/nice64_intro.sprite $(ROMFS_DIR)/nice64_banner.sprite $(ROMFS_DIR)/melfegor_intro.sprite $(ROMFS_DIR)/browser_home_background.sprite
WAV64S = $(ROMFS_DIR)/intro.wav64 $(ROMFS_DIR)/error.wav64 $(ROMFS_DIR)/keyboard_click.wav64
ASSETS = $(SPRITES) $(WAV64S)

N64_ROM_SAVETYPE = sram256k

SRC = $(wildcard $(SOURCE_DIR)/*.c)
OBJS = $(patsubst $(SOURCE_DIR)/%.c,$(BUILD_DIR)/%.o,$(SRC))

CFLAGS += -I$(INCLUDE_DIR)

all: assets $(ROM)
.PHONY: all

assets: $(ASSETS)
.PHONY: assets

$(ROMFS_DIR):
	mkdir -p $(ROMFS_DIR)

$(ROMFS_DIR)/%.sprite: $(ASSET_PNG_DIR)/%.png | $(ROMFS_DIR)
	$(N64_MKSPRITE) -f RGBA16 -o $(ROMFS_DIR) $<

$(ROMFS_DIR)/%.wav64: $(ASSET_AUDIO_DIR)/%.wav | $(ROMFS_DIR)
	$(N64_AUDIOCONV) --wav-mono --wav-resample 22050 --wav-compress 1 -o $(ROMFS_DIR) $<

$(ROMFS_DIR)/keyboard_click.wav64: $(ASSET_AUDIO_DIR)/keyboard_click.wav | $(ROMFS_DIR)
	$(N64_AUDIOCONV) --wav-mono --wav-resample 22050 --wav-compress 0 -o $(ROMFS_DIR) $<

$(ROMFS_DIR).dfs: assets

$(ROM): $(ROMFS_DIR).dfs

$(ROM): N64_ROM_TITLE = "NICE Browser"
$(ROM): assets

$(BUILD_DIR)/$(PROJECT).elf: $(OBJS)

clean:
	rm -rf $(BUILD_DIR) filesystem *.z64 *.n64
.PHONY: clean

-include $(wildcard $(BUILD_DIR)/*.d)
