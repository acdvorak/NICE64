package com.melfegor.nice64.core

import com.melfegor.nice64.core.model.N64ButtonMask
import com.melfegor.nice64.core.model.N64ControllerState
import com.melfegor.nice64.core.protocol.ControllerSequence
import com.melfegor.nice64.core.protocol.LatestStateV1Encoder
import com.melfegor.nice64.core.protocol.SenderSessionId
import org.junit.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class N64ButtonMappingTest {
    @Test
    fun everyFrozenN64ControlMapsToItsCanonicalWireBit() {
        val mappings = listOf(
            N64ButtonMask.A to byteArrayOf(0x80.toByte(), 0),
            N64ButtonMask.B to byteArrayOf(0x40, 0),
            N64ButtonMask.Z to byteArrayOf(0x20, 0),
            N64ButtonMask.START to byteArrayOf(0x10, 0),
            N64ButtonMask.DPAD_UP to byteArrayOf(0x08, 0),
            N64ButtonMask.DPAD_DOWN to byteArrayOf(0x04, 0),
            N64ButtonMask.DPAD_LEFT to byteArrayOf(0x02, 0),
            N64ButtonMask.DPAD_RIGHT to byteArrayOf(0x01, 0),
            N64ButtonMask.L to byteArrayOf(0, 0x20),
            N64ButtonMask.R to byteArrayOf(0, 0x10),
            N64ButtonMask.C_UP to byteArrayOf(0, 0x08),
            N64ButtonMask.C_DOWN to byteArrayOf(0, 0x04),
            N64ButtonMask.C_LEFT to byteArrayOf(0, 0x02),
            N64ButtonMask.C_RIGHT to byteArrayOf(0, 0x01),
        )
        mappings.forEach { (button, expected) ->
            val packet = LatestStateV1Encoder.encode(
                SenderSessionId.from(1u),
                ControllerSequence(1u),
                N64ControllerState(button, 0, 0),
            )
            assertContentEquals(expected, packet.copyOfRange(28, 30), "mask=$button")
        }
    }

    @Test
    fun semanticStateEnforcesSignedByteRangeAndNeutralMeaning() {
        assertTrue(N64ControllerState.Neutral.isNeutral)
        assertFailsWith<IllegalArgumentException> { N64ControllerState(0u, 128, 0) }
        assertFailsWith<IllegalArgumentException> { N64ControllerState(0u, 0, -129) }
    }
}
