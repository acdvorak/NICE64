package com.melfegor.nice64.core.protocol

import com.melfegor.nice64.core.model.N64ButtonMask
import com.melfegor.nice64.core.model.N64ControllerState

object LatestStateV1Contract {
    const val PROTOCOL = "latest-state-v1"
    const val UDP_PORT = 6465
    const val PACKET_SIZE = 32
    const val HEADER_LENGTH = 28
    const val PAYLOAD_SIZE = 4
    const val MAGIC: UInt = 0x4E363455u
    const val VERSION: UByte = 0x01u
    const val DATA_FLAGS: UByte = 0x01u
    const val DELIVERY_CLASS_LATEST_STATE: UByte = 0x01u
    const val TARGET_REPORTS_PER_SECOND = 120
}

@JvmInline
value class SenderSessionId private constructor(val value: UInt) {
    companion object {
        fun from(value: UInt): SenderSessionId {
            require(value != 0u) { "LATEST_STATE sender session ID must be nonzero." }
            return SenderSessionId(value)
        }
    }
}

@JvmInline
value class ControllerSequence(val value: UInt) {
    fun next(): ControllerSequence = ControllerSequence(value + 1u)
}

object LatestStateV1Encoder {
    fun encode(
        sessionId: SenderSessionId,
        sequence: ControllerSequence,
        state: N64ControllerState,
    ): ByteArray = ByteArray(LatestStateV1Contract.PACKET_SIZE).also { packet ->
        writeUInt32BigEndian(packet, 0, LatestStateV1Contract.MAGIC)
        packet[4] = LatestStateV1Contract.VERSION.toByte()
        packet[5] = LatestStateV1Contract.DATA_FLAGS.toByte()
        packet[6] = LatestStateV1Contract.DELIVERY_CLASS_LATEST_STATE.toByte()
        packet[7] = LatestStateV1Contract.HEADER_LENGTH.toByte()
        writeUInt32BigEndian(packet, 8, sessionId.value)
        writeUInt32BigEndian(packet, 12, sequence.value)
        packet[24] = 0
        packet[25] = LatestStateV1Contract.PAYLOAD_SIZE.toByte()
        writeCanonicalN64State(packet, LatestStateV1Contract.HEADER_LENGTH, state)
    }

    private fun writeCanonicalN64State(
        destination: ByteArray,
        offset: Int,
        state: N64ControllerState,
    ) {
        val buttons = state.buttons
        var stickX = state.analogX
        var stickY = state.analogY
        if (!state.analogActive) {
            stickX = when {
                buttons has N64ButtonMask.STICK_LEFT_COMPATIBILITY -> -80
                buttons has N64ButtonMask.STICK_RIGHT_COMPATIBILITY -> 80
                else -> 0
            }
            stickY = when {
                buttons has N64ButtonMask.STICK_UP_COMPATIBILITY -> 80
                buttons has N64ButtonMask.STICK_DOWN_COMPATIBILITY -> -80
                else -> 0
            }
        }

        var byte0 = 0
        var byte1 = 0
        byte0 = setWireBit(buttons, N64ButtonMask.A, 7, byte0)
        byte0 = setWireBit(buttons, N64ButtonMask.B, 6, byte0)
        byte0 = setWireBit(buttons, N64ButtonMask.Z, 5, byte0)
        byte0 = setWireBit(buttons, N64ButtonMask.START, 4, byte0)
        byte0 = setWireBit(buttons, N64ButtonMask.DPAD_UP, 3, byte0)
        byte0 = setWireBit(buttons, N64ButtonMask.DPAD_DOWN, 2, byte0)
        byte0 = setWireBit(buttons, N64ButtonMask.DPAD_LEFT, 1, byte0)
        byte0 = setWireBit(buttons, N64ButtonMask.DPAD_RIGHT, 0, byte0)
        byte1 = setWireBit(buttons, N64ButtonMask.L, 5, byte1)
        byte1 = setWireBit(buttons, N64ButtonMask.R, 4, byte1)
        byte1 = setWireBit(buttons, N64ButtonMask.C_UP, 3, byte1)
        byte1 = setWireBit(buttons, N64ButtonMask.C_DOWN, 2, byte1)
        byte1 = setWireBit(buttons, N64ButtonMask.C_LEFT, 1, byte1)
        byte1 = setWireBit(buttons, N64ButtonMask.C_RIGHT, 0, byte1)
        destination[offset] = byte0.toByte()
        destination[offset + 1] = byte1.toByte()
        destination[offset + 2] = stickX.toByte()
        destination[offset + 3] = stickY.toByte()
    }

    private infix fun UInt.has(mask: UInt): Boolean = this and mask != 0u

    private fun setWireBit(buttons: UInt, mask: UInt, bit: Int, value: Int): Int =
        if (buttons has mask) value or (1 shl bit) else value

    private fun writeUInt32BigEndian(destination: ByteArray, offset: Int, value: UInt) {
        destination[offset] = (value shr 24).toByte()
        destination[offset + 1] = (value shr 16).toByte()
        destination[offset + 2] = (value shr 8).toByte()
        destination[offset + 3] = value.toByte()
    }
}
