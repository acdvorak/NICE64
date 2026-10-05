package com.melfegor.nice64

import java.time.ZoneOffset
import java.util.Locale
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

class HistoryTimestampPresentationTest {
    @Test
    fun validUtcTimestampIsFormattedForPresentation() {
        val raw = "2026-09-17T12:34:56Z"

        val formatted = formatHistoryTimestamp(raw, ZoneOffset.UTC, Locale.US)

        assertNotEquals(raw, formatted)
    }

    @Test
    fun malformedTimestampRemainsTruthfullyVisible() {
        assertEquals(
            "not-a-timestamp",
            formatHistoryTimestamp("not-a-timestamp", ZoneOffset.UTC, Locale.US),
        )
    }
}
