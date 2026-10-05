package com.melfegor.nice64.core.model

object N64ButtonMask {
    const val A: UInt = 0x00000001u
    const val B: UInt = 0x00000002u
    const val Z: UInt = 0x00000004u
    const val START: UInt = 0x00000008u
    const val DPAD_UP: UInt = 0x00000010u
    const val DPAD_DOWN: UInt = 0x00000020u
    const val DPAD_LEFT: UInt = 0x00000040u
    const val DPAD_RIGHT: UInt = 0x00000080u
    const val L: UInt = 0x00000400u
    const val R: UInt = 0x00000800u
    const val C_UP: UInt = 0x00001000u
    const val C_DOWN: UInt = 0x00002000u
    const val STICK_UP_COMPATIBILITY: UInt = 0x00004000u
    const val STICK_DOWN_COMPATIBILITY: UInt = 0x00008000u
    const val STICK_LEFT_COMPATIBILITY: UInt = 0x00010000u
    const val STICK_RIGHT_COMPATIBILITY: UInt = 0x00020000u
    const val C_LEFT: UInt = 0x00040000u
    const val C_RIGHT: UInt = 0x00080000u
}

data class N64ControllerState(
    val buttons: UInt,
    val analogX: Int,
    val analogY: Int,
    val analogActive: Boolean = true,
) {
    init {
        require(analogX in -128..127) { "Analog X must fit signed 8-bit." }
        require(analogY in -128..127) { "Analog Y must fit signed 8-bit." }
    }

    val isNeutral: Boolean
        get() = buttons == 0u && analogX == 0 && analogY == 0

    companion object {
        val Neutral = N64ControllerState(0u, 0, 0)
    }
}
