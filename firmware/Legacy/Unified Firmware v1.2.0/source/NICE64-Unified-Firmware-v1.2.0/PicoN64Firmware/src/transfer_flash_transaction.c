#include "transfer_flash_transaction.h"

#include <stddef.h>

static transfer_flash_result_t fail(
    transfer_flash_transaction_t *transaction,
    transfer_flash_result_t result) {
    if (transaction != NULL && transaction->active) transaction->failed = true;
    return result;
}

void transfer_flash_transaction_reset(transfer_flash_transaction_t *transaction) {
    if (transaction == NULL) return;
    *transaction = (transfer_flash_transaction_t){0};
}

transfer_flash_result_t transfer_flash_transaction_begin(
    transfer_flash_transaction_t *transaction,
    uint32_t declared_length,
    uint32_t partition_size) {
    if (transaction == NULL) return TRANSFER_FLASH_TRANSACTION_FAILED;
    transfer_flash_transaction_reset(transaction);
    if (declared_length == 0u || declared_length > partition_size)
        return TRANSFER_FLASH_INVALID_SIZE;
    transaction->active = true;
    transaction->declared_length = declared_length;
    return TRANSFER_FLASH_OK;
}

transfer_flash_result_t transfer_flash_transaction_validate_page(
    transfer_flash_transaction_t *transaction,
    uint32_t offset,
    uint32_t partition_size,
    uint32_t page_size,
    uint32_t page_crc32,
    bool *is_replay) {
    if (is_replay != NULL) *is_replay = false;
    if (transaction == NULL || !transaction->active)
        return TRANSFER_FLASH_NO_TRANSACTION;
    if (transaction->failed) return TRANSFER_FLASH_TRANSACTION_FAILED;
    if (page_size == 0u || offset % page_size != 0u)
        return fail(transaction, TRANSFER_FLASH_INVALID_ALIGNMENT);
    if (offset > partition_size || page_size > partition_size - offset)
        return fail(transaction, TRANSFER_FLASH_OUT_OF_PARTITION);
    if (offset >= transaction->declared_length)
        return fail(transaction, TRANSFER_FLASH_OUT_OF_TRANSACTION);
    if (transaction->last_page_valid &&
        offset == transaction->last_page_offset) {
        if (page_crc32 != transaction->last_page_crc32)
            return fail(transaction, TRANSFER_FLASH_REPLAY_MISMATCH);
        if (is_replay != NULL) *is_replay = true;
        return TRANSFER_FLASH_OK;
    }
    if (offset != transaction->next_expected_offset)
        return fail(transaction, TRANSFER_FLASH_OUT_OF_ORDER);
    return TRANSFER_FLASH_OK;
}

void transfer_flash_transaction_record_page(
    transfer_flash_transaction_t *transaction,
    uint32_t offset,
    uint32_t page_size,
    uint32_t page_crc32) {
    if (transaction == NULL || !transaction->active || transaction->failed) return;
    const uint32_t remaining = transaction->declared_length - offset;
    transaction->next_expected_offset = offset +
        (remaining < page_size ? remaining : page_size);
    transaction->pages_written++;
    transaction->last_page_valid = true;
    transaction->last_page_offset = offset;
    transaction->last_page_crc32 = page_crc32;
}

transfer_flash_result_t transfer_flash_transaction_validate_block(
    transfer_flash_transaction_t *transaction,
    uint32_t offset,
    uint32_t partition_size,
    uint32_t page_size,
    uint32_t block_size) {
    if (transaction == NULL || !transaction->active)
        return TRANSFER_FLASH_NO_TRANSACTION;
    if (transaction->failed) return TRANSFER_FLASH_TRANSACTION_FAILED;
    if (page_size == 0u || block_size == 0u)
        return fail(transaction, TRANSFER_FLASH_INVALID_SIZE);
    if (offset > partition_size || block_size > partition_size - offset)
        return fail(transaction, TRANSFER_FLASH_OUT_OF_PARTITION);
    if (offset >= transaction->declared_length)
        return fail(transaction, TRANSFER_FLASH_OUT_OF_TRANSACTION);
    const uint32_t page_offset = offset % page_size;
    if (page_offset > page_size || block_size > page_size - page_offset)
        return fail(transaction, TRANSFER_FLASH_STAGING_OVERFLOW);
    if (block_size > transaction->declared_length - offset)
        return fail(transaction, TRANSFER_FLASH_OUT_OF_TRANSACTION);
    if (offset != transaction->next_expected_offset)
        return fail(transaction, TRANSFER_FLASH_OUT_OF_ORDER);
    return TRANSFER_FLASH_OK;
}

void transfer_flash_transaction_record_block(
    transfer_flash_transaction_t *transaction,
    uint32_t offset,
    uint32_t block_size) {
    if (transaction == NULL || !transaction->active || transaction->failed) return;
    transaction->next_expected_offset = offset + block_size;
    transaction->last_page_valid = false;
}

void transfer_flash_transaction_record_staged_page(
    transfer_flash_transaction_t *transaction) {
    if (transaction != NULL && transaction->active && !transaction->failed)
        transaction->pages_written++;
}

void transfer_flash_transaction_fail(transfer_flash_transaction_t *transaction) {
    if (transaction != NULL && transaction->active) transaction->failed = true;
}

transfer_flash_result_t transfer_flash_transaction_validate_commit(
    transfer_flash_transaction_t *transaction,
    uint32_t page_size) {
    if (transaction == NULL || !transaction->active)
        return TRANSFER_FLASH_NO_TRANSACTION;
    if (transaction->failed) return TRANSFER_FLASH_TRANSACTION_FAILED;
    if (page_size == 0u)
        return fail(transaction, TRANSFER_FLASH_INVALID_SIZE);
    if (transaction->next_expected_offset != transaction->declared_length)
        return fail(transaction, TRANSFER_FLASH_INCOMPLETE);
    const uint32_t expected_pages =
        transaction->declared_length / page_size +
        (transaction->declared_length % page_size == 0u ? 0u : 1u);
    if (transaction->pages_written != expected_pages)
        return fail(transaction, TRANSFER_FLASH_INCOMPLETE);
    return TRANSFER_FLASH_OK;
}

void transfer_flash_transaction_commit(transfer_flash_transaction_t *transaction) {
    transfer_flash_transaction_reset(transaction);
}

const char *transfer_flash_result_name(transfer_flash_result_t result) {
    switch (result) {
    case TRANSFER_FLASH_OK: return "OK";
    case TRANSFER_FLASH_INVALID_SIZE: return "INVALID_SIZE";
    case TRANSFER_FLASH_NO_TRANSACTION: return "NO_TRANSACTION";
    case TRANSFER_FLASH_TRANSACTION_FAILED: return "TRANSACTION_FAILED";
    case TRANSFER_FLASH_INVALID_ALIGNMENT: return "INVALID_ALIGNMENT";
    case TRANSFER_FLASH_OUT_OF_PARTITION: return "OUT_OF_PARTITION";
    case TRANSFER_FLASH_OUT_OF_TRANSACTION: return "OUT_OF_TRANSACTION";
    case TRANSFER_FLASH_OUT_OF_ORDER: return "OUT_OF_ORDER";
    case TRANSFER_FLASH_REPLAY_MISMATCH: return "REPLAY_MISMATCH";
    case TRANSFER_FLASH_STAGING_OVERFLOW: return "STAGING_OVERFLOW";
    case TRANSFER_FLASH_INCOMPLETE: return "INCOMPLETE";
    case TRANSFER_FLASH_EXECUTION_FAILED: return "FLASH_EXECUTION_FAILED";
    case TRANSFER_FLASH_NOT_COMMITTED: return "NOT_COMMITTED";
    default: return "UNKNOWN";
    }
}
