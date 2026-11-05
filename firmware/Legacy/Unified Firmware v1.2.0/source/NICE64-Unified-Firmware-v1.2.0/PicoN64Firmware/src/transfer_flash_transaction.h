#pragma once

#include <stdbool.h>
#include <stdint.h>

typedef enum {
    TRANSFER_FLASH_OK = 0,
    TRANSFER_FLASH_INVALID_SIZE,
    TRANSFER_FLASH_NO_TRANSACTION,
    TRANSFER_FLASH_TRANSACTION_FAILED,
    TRANSFER_FLASH_INVALID_ALIGNMENT,
    TRANSFER_FLASH_OUT_OF_PARTITION,
    TRANSFER_FLASH_OUT_OF_TRANSACTION,
    TRANSFER_FLASH_OUT_OF_ORDER,
    TRANSFER_FLASH_REPLAY_MISMATCH,
    TRANSFER_FLASH_STAGING_OVERFLOW,
    TRANSFER_FLASH_INCOMPLETE,
    TRANSFER_FLASH_EXECUTION_FAILED,
    TRANSFER_FLASH_NOT_COMMITTED,
} transfer_flash_result_t;

typedef struct {
    bool active;
    bool failed;
    uint32_t declared_length;
    uint32_t next_expected_offset;
    uint32_t pages_written;
    bool last_page_valid;
    uint32_t last_page_offset;
    uint32_t last_page_crc32;
} transfer_flash_transaction_t;

void transfer_flash_transaction_reset(transfer_flash_transaction_t *transaction);
transfer_flash_result_t transfer_flash_transaction_begin(
    transfer_flash_transaction_t *transaction,
    uint32_t declared_length,
    uint32_t partition_size);
transfer_flash_result_t transfer_flash_transaction_validate_page(
    transfer_flash_transaction_t *transaction,
    uint32_t offset,
    uint32_t partition_size,
    uint32_t page_size,
    uint32_t page_crc32,
    bool *is_replay);
void transfer_flash_transaction_record_page(
    transfer_flash_transaction_t *transaction,
    uint32_t offset,
    uint32_t page_size,
    uint32_t page_crc32);
transfer_flash_result_t transfer_flash_transaction_validate_block(
    transfer_flash_transaction_t *transaction,
    uint32_t offset,
    uint32_t partition_size,
    uint32_t page_size,
    uint32_t block_size);
void transfer_flash_transaction_record_block(
    transfer_flash_transaction_t *transaction,
    uint32_t offset,
    uint32_t block_size);
void transfer_flash_transaction_record_staged_page(
    transfer_flash_transaction_t *transaction);
void transfer_flash_transaction_fail(transfer_flash_transaction_t *transaction);
transfer_flash_result_t transfer_flash_transaction_validate_commit(
    transfer_flash_transaction_t *transaction,
    uint32_t page_size);
void transfer_flash_transaction_commit(transfer_flash_transaction_t *transaction);
const char *transfer_flash_result_name(transfer_flash_result_t result);
