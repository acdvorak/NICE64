BUILD_DIR = build
SOURCE_DIR = src
INCLUDE_DIR = include

include $(N64_INST)/include/n64.mk

PROJECT = nice64-keyboard-mouse-test
ROM = $(PROJECT).z64

SRC = $(wildcard $(SOURCE_DIR)/*.c)
OBJS = $(patsubst $(SOURCE_DIR)/%.c,$(BUILD_DIR)/%.o,$(SRC))

CFLAGS += -I$(INCLUDE_DIR) -Wall -Wextra

all: $(ROM)
.PHONY: all

$(ROM): N64_ROM_TITLE = "NICE64 KB Mouse Test"

$(BUILD_DIR)/$(PROJECT).elf: $(OBJS)

clean:
	rm -rf $(BUILD_DIR) *.z64 *.n64
.PHONY: clean

-include $(wildcard $(BUILD_DIR)/*.d)
