BUILD_DIR = build
SOURCE_DIR = src

include $(N64_INST)/include/n64.mk

PROJECT = nice64-checkpoint-c-bulk-proof
ROM = $(PROJECT).z64
SRC = $(wildcard $(SOURCE_DIR)/*.c)
OBJS = $(patsubst $(SOURCE_DIR)/%.c,$(BUILD_DIR)/%.o,$(SRC))

all: $(ROM)
.PHONY: all

$(ROM): N64_ROM_TITLE = "NICE64 C Bulk Proof"
$(ROM): $(BUILD_DIR)/$(PROJECT).dfs

$(BUILD_DIR)/$(PROJECT).elf: $(OBJS)

filesystem/.keep:
	mkdir -p filesystem
	touch filesystem/.keep

$(BUILD_DIR)/$(PROJECT).dfs: filesystem/.keep

clean:
	rm -rf $(BUILD_DIR) filesystem *.z64 *.n64
.PHONY: clean

-include $(wildcard $(BUILD_DIR)/*.d)
