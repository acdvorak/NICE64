package com.melfegor.nice64.core.input

import com.melfegor.nice64.core.model.N64ControllerState
import kotlin.math.abs
import kotlin.math.roundToInt

data class N64InputContribution(
    val buttons: UInt = 0u,
    val analogX: Int = 0,
    val analogY: Int = 0,
) {
    init {
        require(analogX in -128..127)
        require(analogY in -128..127)
    }

    val isNeutral: Boolean get() = buttons == 0u && analogX == 0 && analogY == 0
    val analogDisplaced: Boolean get() = analogX != 0 || analogY != 0
}

enum class N64InputSource { TOUCH, GAMEPAD }

/**
 * Pure, queue-free routing for exactly one active human-input source.
 *
 * Source changes establish neutral state and discard the prior source. Inactive source updates are
 * ignored, so touch and physical input can never be combined into one gameplay state.
 */
class N64InputComposer {
    private var enabled = false
    private var activeSource = N64InputSource.TOUCH
    private var active = N64InputContribution()

    @Synchronized
    fun setEnabled(value: Boolean): N64ControllerState {
        enabled = value
        if (!value) {
            active = N64InputContribution()
        }
        return compose()
    }

    @Synchronized
    fun setActiveSource(value: N64InputSource): N64ControllerState {
        if (activeSource != value) {
            activeSource = value
            active = N64InputContribution()
        }
        return compose()
    }

    @Synchronized
    fun clearCurrent(): N64ControllerState {
        active = N64InputContribution()
        return compose()
    }

    @Synchronized
    fun setTouchButtons(buttons: UInt): N64ControllerState {
        if (enabled && activeSource == N64InputSource.TOUCH) {
            active = active.copy(buttons = buttons)
        }
        return compose()
    }

    @Synchronized
    fun setTouchAnalog(x: Int, y: Int): N64ControllerState {
        if (enabled && activeSource == N64InputSource.TOUCH) {
            active = active.copy(
                analogX = x.coerceIn(-128, 127),
                analogY = y.coerceIn(-128, 127),
            )
        }
        return compose()
    }

    @Synchronized
    fun clearTouch(): N64ControllerState {
        if (activeSource == N64InputSource.TOUCH) active = N64InputContribution()
        return compose()
    }

    @Synchronized
    fun setGamepad(buttons: UInt, x: Int, y: Int): N64ControllerState {
        if (enabled && activeSource == N64InputSource.GAMEPAD) {
            active = N64InputContribution(
                buttons,
                x.coerceIn(-128, 127),
                y.coerceIn(-128, 127),
            )
        }
        return compose()
    }

    @Synchronized
    fun clearGamepad(): N64ControllerState {
        if (activeSource == N64InputSource.GAMEPAD) active = N64InputContribution()
        return compose()
    }

    @Synchronized
    fun current(): N64ControllerState = compose()

    @Synchronized
    fun touchContribution(): N64InputContribution =
        if (activeSource == N64InputSource.TOUCH) active else N64InputContribution()

    @Synchronized
    fun gamepadContribution(): N64InputContribution =
        if (activeSource == N64InputSource.GAMEPAD) active else N64InputContribution()

    @Synchronized
    fun source(): N64InputSource = activeSource

    private fun compose(): N64ControllerState {
        if (!enabled) return N64ControllerState.Neutral
        return N64ControllerState(
            buttons = active.buttons,
            analogX = active.analogX,
            analogY = active.analogY,
        )
    }
}

object N64AnalogScaling {
    const val DEFAULT_DEADZONE = 0.12f

    fun scaledAxis(value: Float, deadzone: Float = DEFAULT_DEADZONE): Int {
        val clamped = value.coerceIn(-1f, 1f)
        val boundedDeadzone = deadzone.coerceIn(0f, 0.95f)
        val magnitude = abs(clamped)
        if (magnitude <= boundedDeadzone) return 0
        val normalized = ((magnitude - boundedDeadzone) / (1f - boundedDeadzone)).coerceIn(0f, 1f)
        return if (clamped < 0f) (-128f * normalized).roundToInt().coerceIn(-128, 0)
        else (127f * normalized).roundToInt().coerceIn(0, 127)
    }
}
