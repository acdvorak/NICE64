package com.melfegor.nice64.core.protocol

object MobileInputV1Contract {
    const val VERSION = 1
    const val TYPE_KEYBOARD = 1
    const val TYPE_POINTER = 2
    const val KEYBOARD_PREFIX_BYTES = 3
    const val POINTER_PAYLOAD_BYTES = 12
    const val BUTTON_LEFT = 0x01
    const val BUTTON_RIGHT = 0x02
}

object MobileInputV1Encoder {
    fun keyboard(
        sessionId: SenderSessionId,
        sequence: ControllerSequence,
        logicalKeyIds: Collection<Int>,
    ): ByteArray {
        val keys = logicalKeyIds.distinct().sorted()
        require(keys.size <= 255) { "Too many logical keys." }
        require(keys.all { it in 1..255 }) { "Logical key IDs must fit one byte." }
        val payload = ByteArray(MobileInputV1Contract.KEYBOARD_PREFIX_BYTES + keys.size)
        payload[0] = MobileInputV1Contract.VERSION.toByte()
        payload[1] = MobileInputV1Contract.TYPE_KEYBOARD.toByte()
        payload[2] = keys.size.toByte()
        keys.forEachIndexed { index, key ->
            payload[MobileInputV1Contract.KEYBOARD_PREFIX_BYTES + index] = key.toByte()
        }
        return LatestStateV1Encoder.encodePayload(sessionId, sequence, payload)
    }

    fun pointer(
        sessionId: SenderSessionId,
        sequence: ControllerSequence,
        left: Boolean,
        right: Boolean,
        totalX: Int,
        totalY: Int,
    ): ByteArray {
        val payload = ByteArray(MobileInputV1Contract.POINTER_PAYLOAD_BYTES)
        payload[0] = MobileInputV1Contract.VERSION.toByte()
        payload[1] = MobileInputV1Contract.TYPE_POINTER.toByte()
        payload[2] = ((if (left) MobileInputV1Contract.BUTTON_LEFT else 0) or
            (if (right) MobileInputV1Contract.BUTTON_RIGHT else 0)).toByte()
        writeInt32BigEndian(payload, 4, totalX)
        writeInt32BigEndian(payload, 8, totalY)
        return LatestStateV1Encoder.encodePayload(sessionId, sequence, payload)
    }

    private fun writeInt32BigEndian(destination: ByteArray, offset: Int, value: Int) {
        destination[offset] = (value ushr 24).toByte()
        destination[offset + 1] = (value ushr 16).toByte()
        destination[offset + 2] = (value ushr 8).toByte()
        destination[offset + 3] = value.toByte()
    }
}
