if(NOT EXISTS "${IMAGE_FILE}")
    message(FATAL_ERROR "Firmware binary not found for persistence boundary check: ${IMAGE_FILE}")
endif()

file(SIZE "${IMAGE_FILE}" IMAGE_BYTES)
if(IMAGE_BYTES GREATER MAX_IMAGE_BYTES)
    message(FATAL_ERROR
        "Firmware binary (${IMAGE_BYTES} bytes) overlaps Wi-Fi persistence at ${MAX_IMAGE_BYTES}")
endif()

message(STATUS
    "Persistence boundary check passed: ${IMAGE_BYTES} / ${MAX_IMAGE_BYTES} bytes")
