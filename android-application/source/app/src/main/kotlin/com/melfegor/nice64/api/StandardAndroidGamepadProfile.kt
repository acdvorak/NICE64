package com.melfegor.nice64.api

import android.view.KeyEvent
import android.view.MotionEvent
import com.melfegor.nice64.core.input.AxisBindingAlternativeV1
import com.melfegor.nice64.core.input.AxisBindingV1
import com.melfegor.nice64.core.input.AxisDirectionV1
import com.melfegor.nice64.core.input.ButtonBindingV1
import com.melfegor.nice64.core.input.ControllerProfileV1
import com.melfegor.nice64.core.input.N64SemanticTargetV1

/** Code-only P2 contract. It is not connected to the live P1 gameplay mapper. */
internal object StandardAndroidGamepadProfile {
    val profile = ControllerProfileV1(
        profileId = "standard-android-gamepad-v1",
        profileName = "Standard Android Gamepad",
        buttonBindings = listOf(
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_A, N64SemanticTargetV1.A),
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_B, N64SemanticTargetV1.B),
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_X, N64SemanticTargetV1.C_LEFT),
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_Y, N64SemanticTargetV1.C_UP),
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_L1, N64SemanticTargetV1.L),
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_R1, N64SemanticTargetV1.R),
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_L2, N64SemanticTargetV1.Z),
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_R2, N64SemanticTargetV1.Z),
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_START, N64SemanticTargetV1.START),
            ButtonBindingV1(KeyEvent.KEYCODE_DPAD_UP, N64SemanticTargetV1.DPAD_UP),
            ButtonBindingV1(KeyEvent.KEYCODE_DPAD_DOWN, N64SemanticTargetV1.DPAD_DOWN),
            ButtonBindingV1(KeyEvent.KEYCODE_DPAD_LEFT, N64SemanticTargetV1.DPAD_LEFT),
            ButtonBindingV1(KeyEvent.KEYCODE_DPAD_RIGHT, N64SemanticTargetV1.DPAD_RIGHT),
        ),
        axisBindings = listOf(
            AxisBindingV1(MotionEvent.AXIS_X, AxisDirectionV1.BOTH, N64SemanticTargetV1.ANALOG_X),
            AxisBindingV1(MotionEvent.AXIS_Y, AxisDirectionV1.BOTH, N64SemanticTargetV1.ANALOG_Y, inverted = true),
            digital(MotionEvent.AXIS_HAT_X, AxisDirectionV1.NEGATIVE, N64SemanticTargetV1.DPAD_LEFT),
            digital(MotionEvent.AXIS_HAT_X, AxisDirectionV1.POSITIVE, N64SemanticTargetV1.DPAD_RIGHT),
            digital(MotionEvent.AXIS_HAT_Y, AxisDirectionV1.NEGATIVE, N64SemanticTargetV1.DPAD_UP),
            digital(MotionEvent.AXIS_HAT_Y, AxisDirectionV1.POSITIVE, N64SemanticTargetV1.DPAD_DOWN),
            trigger(MotionEvent.AXIS_LTRIGGER),
            trigger(MotionEvent.AXIS_RTRIGGER),
            trigger(MotionEvent.AXIS_BRAKE),
            trigger(MotionEvent.AXIS_GAS),
        ),
        axisAlternatives = listOf(
            rightStickVariant("z-rz", 0, MotionEvent.AXIS_Z, MotionEvent.AXIS_RZ),
            rightStickVariant("rx-ry", 1, MotionEvent.AXIS_RX, MotionEvent.AXIS_RY),
        ),
    )

    private fun trigger(axis: Int) = digital(
        axis,
        AxisDirectionV1.POSITIVE,
        N64SemanticTargetV1.Z,
        deadZone = 0f,
    )

    private fun digital(
        axis: Int,
        direction: AxisDirectionV1,
        target: N64SemanticTargetV1,
        deadZone: Float? = null,
    ) = AxisBindingV1(
        axisCode = axis,
        direction = direction,
        target = target,
        deadZone = deadZone,
        pressThreshold = 0.5f,
        releaseThreshold = 0.45f,
    )

    private fun rightStickVariant(id: String, priority: Int, xAxis: Int, yAxis: Int) =
        AxisBindingAlternativeV1(
            choiceGroup = "right-stick-c-buttons",
            variantId = id,
            priority = priority,
            requiredAxes = setOf(xAxis, yAxis),
            bindings = listOf(
                digital(xAxis, AxisDirectionV1.NEGATIVE, N64SemanticTargetV1.C_LEFT),
                digital(xAxis, AxisDirectionV1.POSITIVE, N64SemanticTargetV1.C_RIGHT),
                digital(yAxis, AxisDirectionV1.NEGATIVE, N64SemanticTargetV1.C_UP),
                digital(yAxis, AxisDirectionV1.POSITIVE, N64SemanticTargetV1.C_DOWN),
            ),
        )
}
