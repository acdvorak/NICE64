package com.melfegor.nice64.core

import com.melfegor.nice64.core.model.N64ButtonMask
import com.melfegor.nice64.core.model.N64ControllerState
import com.melfegor.nice64.core.protocol.ControllerSequence
import com.melfegor.nice64.core.protocol.LatestStateV1Contract
import com.melfegor.nice64.core.protocol.LatestStateV1Encoder
import com.melfegor.nice64.core.protocol.SenderSessionId
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class LatestStateV1EncoderTest {
    @Test
    fun allAuthorizedD3VectorsAreByteExact() {
        val stream = checkNotNull(
            javaClass.classLoader.getResourceAsStream("latest_state_v1_test_vectors.json"),
        )
        val root = stream.bufferedReader().use { reader ->
            Json.parseToJsonElement(reader.readText()).jsonObject
        }
        assertEquals(LatestStateV1Contract.PROTOCOL, root.getValue("protocol").jsonPrimitive.content)
        assertEquals(LatestStateV1Contract.PACKET_SIZE, root.getValue("packetLength").jsonPrimitive.int)

        val vectors = root.getValue("vectors").jsonArray
        assertEquals(14, vectors.size)
        vectors.forEach { element ->
            val vector = element.jsonObject
            val packet = LatestStateV1Encoder.encode(
                SenderSessionId.from(parseUInt(vector.getValue("sessionId").jsonPrimitive.content)),
                ControllerSequence(parseUInt(vector.getValue("sequence").jsonPrimitive.content)),
                N64ControllerState(
                    buttons = parseUInt(vector.getValue("buttons").jsonPrimitive.content),
                    analogX = vector.getValue("analogX").jsonPrimitive.int,
                    analogY = vector.getValue("analogY").jsonPrimitive.int,
                    analogActive = vector.getValue("analogActive").jsonPrimitive.content.toBooleanStrict(),
                ),
            )
            assertContentEquals(
                vector.getValue("continuousHex").jsonPrimitive.content.hexToBytes(),
                packet,
                vector.getValue("name").jsonPrimitive.content,
            )
        }
    }

    @Test
    fun fixedHeaderAndBigEndianFieldsRemainFrozen() {
        val packet = LatestStateV1Encoder.encode(
            SenderSessionId.from(0x10203040u),
            ControllerSequence(0xA0B0C0D0u),
            N64ControllerState.Neutral,
        )
        assertEquals(32, packet.size)
        assertContentEquals(
            byteArrayOf(0x4E, 0x36, 0x34, 0x55, 1, 1, 1, 28),
            packet.copyOfRange(0, 8),
        )
        assertContentEquals(byteArrayOf(0x10, 0x20, 0x30, 0x40), packet.copyOfRange(8, 12))
        assertContentEquals(
            byteArrayOf(0xA0.toByte(), 0xB0.toByte(), 0xC0.toByte(), 0xD0.toByte()),
            packet.copyOfRange(12, 16),
        )
        assertContentEquals(ByteArray(8), packet.copyOfRange(16, 24))
        assertContentEquals(byteArrayOf(0, 4, 0, 0), packet.copyOfRange(24, 28))
    }

    @Test
    fun senderSessionMustBeNonzeroAndSequenceWrapsModuloUint32() {
        assertFailsWith<IllegalArgumentException> { SenderSessionId.from(0u) }
        assertEquals(0u, ControllerSequence(UInt.MAX_VALUE).next().value)
    }

    @Test
    fun compatibilityStickBitsApplyOnlyWhenAnalogIsInactive() {
        val session = SenderSessionId.from(1u)
        val inactive = LatestStateV1Encoder.encode(
            session,
            ControllerSequence(1u),
            N64ControllerState(
                N64ButtonMask.STICK_RIGHT_COMPATIBILITY or
                    N64ButtonMask.STICK_UP_COMPATIBILITY,
                12,
                -12,
                analogActive = false,
            ),
        )
        assertContentEquals(byteArrayOf(0, 0, 80, 80), inactive.copyOfRange(28, 32))

        val active = LatestStateV1Encoder.encode(
            session,
            ControllerSequence(2u),
            N64ControllerState(
                N64ButtonMask.STICK_RIGHT_COMPATIBILITY,
                12,
                -12,
                analogActive = true,
            ),
        )
        assertContentEquals(byteArrayOf(0, 0, 12, (-12).toByte()), active.copyOfRange(28, 32))
    }

    private fun parseUInt(text: String): UInt = text.removePrefix("0x").toUInt(16)

    private fun String.hexToBytes(): ByteArray = chunked(2)
        .map { it.toInt(16).toByte() }
        .toByteArray()
}
