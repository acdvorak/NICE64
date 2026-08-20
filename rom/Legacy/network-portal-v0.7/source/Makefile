BUILD_DIR = build
SOURCE_DIR = src
INCLUDE_DIR = include

include $(N64_INST)/include/n64.mk

PROJECT = nice-network-pak-client
ROM = $(PROJECT).z64

SRC = $(wildcard $(SOURCE_DIR)/*.c)
OBJS = $(patsubst $(SOURCE_DIR)/%.c,$(BUILD_DIR)/%.o,$(SRC))
ASSETS_PNG = \
	assets/logo/nice64_logo.png \
	assets/logo/nice64_banner.png \
	assets/icons/splash_banner.png \
	assets/icons/nice64_header_banner.png \
	assets/icons/icon_welcome.png \
	assets/icons/icon_headlines.png \
	assets/icons/icon_web.png \
	assets/icons/icon_about.png \
	assets/icons/icon_diagnostics.png \
	assets/icons/icon_news.png \
	assets/icons/icon_projects.png \
	assets/icons/icon_time.png \
	assets/icons/icon_chat.png \
	assets/icons/icon_default.png \
	assets/ui/panel_bg.png \
	assets/ui/panel_border.png \
	assets/ui/selector_arrow.png \
	assets/ui/status_online.png \
	assets/ui/status_offline.png \
	assets/ui/footer_bar.png
ASSETS_CONV = $(addprefix filesystem/,$(notdir $(ASSETS_PNG:.png=.sprite)))

CFLAGS += -I$(INCLUDE_DIR)

all: $(ROM)
.PHONY: all

$(ROM): N64_ROM_TITLE = "NICE Network Pak"
$(ROM): $(BUILD_DIR)/$(PROJECT).dfs

$(BUILD_DIR)/$(PROJECT).elf: $(OBJS)
$(BUILD_DIR)/$(PROJECT).dfs: $(ASSETS_CONV)

filesystem/%.sprite: assets/logo/%.png
	@mkdir -p filesystem
	@$(N64_MKSPRITE) --format RGBA16 -o filesystem "$<"

filesystem/%.sprite: assets/icons/%.png
	@mkdir -p filesystem
	@$(N64_MKSPRITE) --format RGBA16 -o filesystem "$<"

filesystem/%.sprite: assets/ui/%.png
	@mkdir -p filesystem
	@$(N64_MKSPRITE) --format RGBA16 -o filesystem "$<"

clean:
	rm -rf $(BUILD_DIR) filesystem *.z64 *.n64
.PHONY: clean

-include $(wildcard $(BUILD_DIR)/*.d)
