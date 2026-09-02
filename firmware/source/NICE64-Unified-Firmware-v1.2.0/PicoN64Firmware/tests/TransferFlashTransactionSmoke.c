#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#include "transfer_flash_transaction.h"

#define PARTITION_OFFSET 0x00100000u
#define PARTITION_SIZE   0x00200000u
#define PARTITION_END    0x00300000u
#define PAGE_SIZE        256u
#define BLOCK_SIZE       32u

static void require(int condition, const char *message) {
    if (!condition) {
        fprintf(stderr, "FAIL: %s\n", message);
        exit(1);
    }
}

static void require_result(transfer_flash_result_t actual,
                           transfer_flash_result_t expected,
                           const char *message) {
    if (actual != expected) {
        fprintf(stderr, "FAIL: %s (expected %s, got %s)\n", message,
                transfer_flash_result_name(expected),
                transfer_flash_result_name(actual));
        exit(1);
    }
}

static transfer_flash_result_t validate_page(
    transfer_flash_transaction_t *transaction,
    uint32_t offset,
    uint32_t partition_size,
    uint32_t page_size) {
    bool is_replay = false;
    return transfer_flash_transaction_validate_page(
        transaction, offset, partition_size, page_size,
        0xA5A50000u ^ offset, &is_replay);
}

static void record_page(transfer_flash_transaction_t *transaction,
                        uint32_t offset, uint32_t page_size) {
    transfer_flash_transaction_record_page(
        transaction, offset, page_size, 0xA5A50000u ^ offset);
}

static void complete_page_transaction(uint32_t length) {
    transfer_flash_transaction_t transaction;
    require_result(transfer_flash_transaction_begin(&transaction, length,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "valid begin rejected");
    for (uint32_t offset = 0; offset < length; offset += PAGE_SIZE) {
        require_result(validate_page(
                           &transaction, offset, PARTITION_SIZE, PAGE_SIZE),
                       TRANSFER_FLASH_OK, "valid sequential page rejected");
        require(offset <= PARTITION_SIZE - PAGE_SIZE,
                "physical page exceeds partition");
        const uint32_t physical_start = PARTITION_OFFSET + offset;
        require(physical_start >= PARTITION_OFFSET,
                "physical page starts below partition");
        require(PAGE_SIZE <= PARTITION_END - physical_start,
                "physical page ends beyond partition");
        record_page(&transaction, offset, PAGE_SIZE);
    }
    require_result(transfer_flash_transaction_validate_commit(&transaction,
                                                               PAGE_SIZE),
                   TRANSFER_FLASH_OK, "complete transaction would not commit");
    transfer_flash_transaction_commit(&transaction);
    require(!transaction.active, "successful commit retained active state");
}

int main(void) {
    complete_page_transaction(1u);
    complete_page_transaction(64u * 1024u);
    complete_page_transaction(512u * 1024u);
    complete_page_transaction(1024u * 1024u);
    complete_page_transaction(PARTITION_SIZE - 1u);
    complete_page_transaction(PARTITION_SIZE);
    puts("PASS valid small/partial/512KiB/1MiB/2MiB transactions and flash bounds");

    transfer_flash_transaction_t transaction;
    require_result(transfer_flash_transaction_begin(&transaction, 0u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_INVALID_SIZE, "zero-length begin accepted");
    require_result(transfer_flash_transaction_begin(&transaction,
                                                     PARTITION_SIZE + 1u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_INVALID_SIZE, "oversized begin accepted");
    require_result(transfer_flash_transaction_begin(&transaction, UINT32_MAX,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_INVALID_SIZE, "UINT32_MAX begin accepted");
    puts("PASS invalid begin boundaries");

    transfer_flash_transaction_reset(&transaction);
        require_result(validate_page(
                       &transaction, 0u, PARTITION_SIZE, PAGE_SIZE),
                   TRANSFER_FLASH_NO_TRANSACTION, "page before begin accepted");

    require_result(transfer_flash_transaction_begin(&transaction, 1024u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "page-validation begin failed");
    require_result(validate_page(
                       &transaction, 1u, PARTITION_SIZE, PAGE_SIZE),
                   TRANSFER_FLASH_INVALID_ALIGNMENT, "unaligned page accepted");

    require_result(transfer_flash_transaction_begin(&transaction, PARTITION_SIZE,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "partition begin failed");
    require_result(validate_page(
                       &transaction, PARTITION_SIZE, PARTITION_SIZE, PAGE_SIZE),
                   TRANSFER_FLASH_OUT_OF_PARTITION,
                   "page at partition end accepted");
    require_result(transfer_flash_transaction_begin(&transaction, PARTITION_SIZE,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "partition begin failed");
    require_result(validate_page(
                       &transaction, PARTITION_SIZE + PAGE_SIZE,
                       PARTITION_SIZE, PAGE_SIZE),
                   TRANSFER_FLASH_OUT_OF_PARTITION,
                   "page beyond partition accepted");
    require_result(transfer_flash_transaction_begin(&transaction, PARTITION_SIZE,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "partition begin failed");
    require_result(validate_page(
                       &transaction, 0xFFFFFF00u, PARTITION_SIZE, PAGE_SIZE),
                   TRANSFER_FLASH_OUT_OF_PARTITION, "huge page offset accepted");

    require_result(transfer_flash_transaction_begin(&transaction, 256u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "short begin failed");
    require_result(validate_page(
                       &transaction, 256u, PARTITION_SIZE, PAGE_SIZE),
                   TRANSFER_FLASH_OUT_OF_TRANSACTION,
                   "page outside declared transaction accepted");
    require_result(transfer_flash_transaction_begin(&transaction, 1024u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "ordered begin failed");
    require_result(validate_page(
                       &transaction, 256u, PARTITION_SIZE, PAGE_SIZE),
                   TRANSFER_FLASH_OUT_OF_ORDER, "out-of-order page accepted");
    puts("PASS invalid page, ordering, and overflow-safe bounds");

    const uint32_t page0_crc = 0x10203040u;
    const uint32_t page1_crc = 0x50607080u;
    bool replay = false;
    uint32_t simulated_physical_writes = 0u;
    require_result(transfer_flash_transaction_begin(&transaction, 512u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "replay begin failed");
    require_result(transfer_flash_transaction_validate_page(
                       &transaction, 0u, PARTITION_SIZE, PAGE_SIZE,
                       page0_crc, &replay),
                   TRANSFER_FLASH_OK, "new page 0 rejected");
    require(!replay, "new page 0 classified as replay");
    simulated_physical_writes++;
    transfer_flash_transaction_record_page(&transaction, 0u, PAGE_SIZE,
                                            page0_crc);
    const uint32_t pages_after_page0 = transaction.pages_written;
    const uint32_t next_after_page0 = transaction.next_expected_offset;

    replay = false;
    require_result(transfer_flash_transaction_validate_page(
                       &transaction, 0u, PARTITION_SIZE, PAGE_SIZE,
                       page0_crc, &replay),
                   TRANSFER_FLASH_OK, "identical immediate replay rejected");
    require(replay, "identical previous page was not classified as replay");
    require(transaction.pages_written == pages_after_page0,
            "replay incremented pages_written");
    require(transaction.next_expected_offset == next_after_page0,
            "replay advanced next_expected_offset");
    require(!transaction.failed, "valid replay failed transaction");
    require(simulated_physical_writes == 1u,
            "replay caused a second simulated physical write");

    replay = false;
    require_result(transfer_flash_transaction_validate_page(
                       &transaction, PAGE_SIZE, PARTITION_SIZE, PAGE_SIZE,
                       page1_crc, &replay),
                   TRANSFER_FLASH_OK, "page 1 rejected after replay");
    require(!replay, "new page 1 classified as replay");
    simulated_physical_writes++;
    transfer_flash_transaction_record_page(&transaction, PAGE_SIZE, PAGE_SIZE,
                                            page1_crc);
    require_result(transfer_flash_transaction_validate_page(
                       &transaction, 0u, PARTITION_SIZE, PAGE_SIZE,
                       page0_crc, &replay),
                   TRANSFER_FLASH_OUT_OF_ORDER,
                   "older page 0 regained replay eligibility after page 1");

    require_result(transfer_flash_transaction_begin(&transaction, 512u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "mismatch begin failed");
    require_result(transfer_flash_transaction_validate_page(
                       &transaction, 0u, PARTITION_SIZE, PAGE_SIZE,
                       page0_crc, &replay),
                   TRANSFER_FLASH_OK, "mismatch setup page rejected");
    transfer_flash_transaction_record_page(&transaction, 0u, PAGE_SIZE,
                                            page0_crc);
    require_result(transfer_flash_transaction_validate_page(
                       &transaction, 0u, PARTITION_SIZE, PAGE_SIZE,
                       page0_crc ^ 1u, &replay),
                   TRANSFER_FLASH_REPLAY_MISMATCH,
                   "changed-data replay was accepted");
    require(transaction.failed, "replay mismatch did not fail transaction");

    require_result(transfer_flash_transaction_begin(&transaction, 768u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "skipped-page begin failed");
    require_result(transfer_flash_transaction_validate_page(
                       &transaction, 256u, PARTITION_SIZE, PAGE_SIZE,
                       page1_crc, &replay),
                   TRANSFER_FLASH_OUT_OF_ORDER, "skipped page was accepted");

    require_result(transfer_flash_transaction_begin(&transaction, 768u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "failed-replay begin failed");
    require_result(transfer_flash_transaction_validate_page(
                       &transaction, 0u, PARTITION_SIZE, PAGE_SIZE,
                       page0_crc, &replay),
                   TRANSFER_FLASH_OK, "failed-replay setup page rejected");
    transfer_flash_transaction_record_page(&transaction, 0u, PAGE_SIZE,
                                            page0_crc);
    require_result(transfer_flash_transaction_validate_page(
                       &transaction, 512u, PARTITION_SIZE, PAGE_SIZE,
                       0x90A0B0C0u, &replay),
                   TRANSFER_FLASH_OUT_OF_ORDER,
                   "failed-replay setup did not fail transaction");
    require_result(transfer_flash_transaction_validate_page(
                       &transaction, 0u, PARTITION_SIZE, PAGE_SIZE,
                       page0_crc, &replay),
                   TRANSFER_FLASH_TRANSACTION_FAILED,
                   "replay cleared an already failed transaction");

    require_result(transfer_flash_transaction_begin(&transaction, 512u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "reset-metadata begin failed");
    require(!transaction.last_page_valid,
            "new begin retained previous replay metadata");
    transfer_flash_transaction_record_page(&transaction, 0u, PAGE_SIZE,
                                            page0_crc);
    transfer_flash_transaction_reset(&transaction);
    require(!transaction.last_page_valid,
            "transaction reset retained replay metadata");
    require_result(transfer_flash_transaction_begin(&transaction, 512u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "failed-begin reset setup failed");
    transfer_flash_transaction_record_page(&transaction, 0u, PAGE_SIZE,
                                            page0_crc);
    require_result(transfer_flash_transaction_begin(&transaction, 0u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_INVALID_SIZE,
                   "zero-length replacement begin was accepted");
    require(!transaction.active && !transaction.last_page_valid,
            "failed begin retained replay metadata");

    require_result(transfer_flash_transaction_begin(&transaction, 512u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "replay commit begin failed");
    require_result(transfer_flash_transaction_validate_page(
                       &transaction, 0u, PARTITION_SIZE, PAGE_SIZE,
                       page0_crc, &replay),
                   TRANSFER_FLASH_OK, "replay commit page 0 rejected");
    transfer_flash_transaction_record_page(&transaction, 0u, PAGE_SIZE,
                                            page0_crc);
    require_result(transfer_flash_transaction_validate_page(
                       &transaction, 0u, PARTITION_SIZE, PAGE_SIZE,
                       page0_crc, &replay),
                   TRANSFER_FLASH_OK, "replay before commit rejected");
    require(replay, "replay before commit not recognized");
    require_result(transfer_flash_transaction_validate_page(
                       &transaction, 256u, PARTITION_SIZE, PAGE_SIZE,
                       page1_crc, &replay),
                   TRANSFER_FLASH_OK, "replay commit page 1 rejected");
    transfer_flash_transaction_record_page(&transaction, 256u, PAGE_SIZE,
                                            page1_crc);
    require_result(transfer_flash_transaction_validate_commit(&transaction,
                                                               PAGE_SIZE),
                   TRANSFER_FLASH_OK,
                   "complete transaction with replay would not commit");
    transfer_flash_transaction_commit(&transaction);
    require(!transaction.active && !transaction.last_page_valid,
            "successful commit retained replay metadata");
    puts("PASS exact immediate replay, mismatch, stale, failure, reset, and commit semantics");

    require_result(transfer_flash_transaction_begin(&transaction, PAGE_SIZE,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "legacy begin failed");
    for (uint32_t offset = 0; offset <= 224u; offset += BLOCK_SIZE) {
        require_result(transfer_flash_transaction_validate_block(
                           &transaction, offset, PARTITION_SIZE, PAGE_SIZE,
                           BLOCK_SIZE), TRANSFER_FLASH_OK,
                       "valid legacy block rejected");
        transfer_flash_transaction_record_block(&transaction, offset, BLOCK_SIZE);
    }
    require_result(transfer_flash_transaction_begin(&transaction, PAGE_SIZE,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "legacy overflow begin failed");
    require_result(transfer_flash_transaction_validate_block(
                       &transaction, 225u, PARTITION_SIZE, PAGE_SIZE, BLOCK_SIZE),
                   TRANSFER_FLASH_STAGING_OVERFLOW,
                   "legacy page offset 225 accepted");
    require_result(transfer_flash_transaction_begin(&transaction, PAGE_SIZE,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "legacy overflow begin failed");
    require_result(transfer_flash_transaction_validate_block(
                       &transaction, 255u, PARTITION_SIZE, PAGE_SIZE, BLOCK_SIZE),
                   TRANSFER_FLASH_STAGING_OVERFLOW,
                   "legacy page offset 255 accepted");
    puts("PASS legacy block offsets 0/224 valid and 225/255 rejected");

    transfer_flash_transaction_reset(&transaction);
    require_result(transfer_flash_transaction_validate_commit(&transaction,
                                                               PAGE_SIZE),
                   TRANSFER_FLASH_NO_TRANSACTION,
                   "commit without begin accepted");
    require_result(transfer_flash_transaction_begin(&transaction, 512u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "incomplete begin failed");
    require_result(validate_page(
                       &transaction, 0u, PARTITION_SIZE, PAGE_SIZE),
                   TRANSFER_FLASH_OK, "incomplete first page rejected");
    record_page(&transaction, 0u, PAGE_SIZE);
    require_result(transfer_flash_transaction_validate_commit(&transaction,
                                                               PAGE_SIZE),
                   TRANSFER_FLASH_INCOMPLETE, "incomplete commit accepted");
    require_result(transfer_flash_transaction_begin(&transaction, 512u,
                                                     PARTITION_SIZE),
                   TRANSFER_FLASH_OK, "failed-page begin failed");
    require_result(validate_page(
                       &transaction, 1u, PARTITION_SIZE, PAGE_SIZE),
                   TRANSFER_FLASH_INVALID_ALIGNMENT,
                   "failed-page setup did not fail");
    require_result(transfer_flash_transaction_validate_commit(&transaction,
                                                               PAGE_SIZE),
                   TRANSFER_FLASH_TRANSACTION_FAILED,
                   "commit after failed page accepted");
    complete_page_transaction(512u);
    puts("PASS commit without begin/incomplete/failed/success cases");

    puts("ALL TRANSFER FLASH TRANSACTION SMOKE TESTS PASSED");
    return 0;
}
