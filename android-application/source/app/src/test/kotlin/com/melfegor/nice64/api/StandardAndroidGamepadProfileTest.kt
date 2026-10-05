package com.melfegor.nice64.api

import android.view.KeyEvent
import android.view.MotionEvent
import com.melfegor.nice64.core.input.AxisRangeV1
import com.melfegor.nice64.core.input.ControllerCapabilitiesV1
import com.melfegor.nice64.core.input.ControllerMappingEvaluatorV1
import com.melfegor.nice64.core.input.ControllerProfileValidatorV1
import com.melfegor.nice64.core.input.ControllerSourceClassV1
import com.melfegor.nice64.core.input.PhysicalInputStateV1
import com.melfegor.nice64.core.model.N64ButtonMask
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import org.junit.Test

class StandardAndroidGamepadProfileTest {
    private val profile = StandardAndroidGamepadProfile.profile

    @Test fun defaultProfileIsValid() {
        assertTrue(ControllerProfileValidatorV1.validate(profile).isValid)
    }

    @Test fun everyFrozenButtonMappingHasExactParity() {
        val keys = listOf(
            KeyEvent.KEYCODE_BUTTON_A, KeyEvent.KEYCODE_BUTTON_B,
            KeyEvent.KEYCODE_BUTTON_X, KeyEvent.KEYCODE_BUTTON_Y,
            KeyEvent.KEYCODE_BUTTON_L1, KeyEvent.KEYCODE_BUTTON_R1,
            KeyEvent.KEYCODE_BUTTON_L2, KeyEvent.KEYCODE_BUTTON_R2,
            KeyEvent.KEYCODE_BUTTON_START,
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN,
            KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT,
        )
        keys.forEach { key ->
            assertEquals(AndroidGamepadMapping.buttonMask(key), evaluate(keys = setOf(key)).buttons, "key=$key")
        }
    }

    @Test fun simultaneousButtonsCollapseIntoSemanticMask() {
        val keys = setOf(KeyEvent.KEYCODE_BUTTON_A, KeyEvent.KEYCODE_BUTTON_L1,
            KeyEvent.KEYCODE_BUTTON_R2, KeyEvent.KEYCODE_DPAD_UP)
        assertEquals(N64ButtonMask.A or N64ButtonMask.L or N64ButtonMask.Z or N64ButtonMask.DPAD_UP,
            evaluate(keys = keys).buttons)
    }

    @Test fun neutralInputIsNeutral() {
        assertTrue(evaluate().isNeutral)
    }

    @Test fun leftStickProducesScaledAnalogAndDiagonal() {
        val result = evaluate(axes = mapOf(MotionEvent.AXIS_X to 1f, MotionEvent.AXIS_Y to 1f))
        assertEquals(127, result.analogX)
        assertEquals(-128, result.analogY)
    }

    @Test fun preferredZAndRzDriveRightStickCButtons() {
        val result = evaluate(axes = mapOf(MotionEvent.AXIS_Z to 1f, MotionEvent.AXIS_RZ to -1f))
        assertEquals(N64ButtonMask.C_RIGHT or N64ButtonMask.C_UP, result.buttons)
    }

    @Test fun fallbackRxAndRyDriveRightStickWhenPreferredPairAbsent() {
        val capabilities = capabilities(axisCodes = setOf(
            MotionEvent.AXIS_X, MotionEvent.AXIS_Y, MotionEvent.AXIS_RX, MotionEvent.AXIS_RY,
        ))
        val result = ControllerMappingEvaluatorV1.evaluate(profile, capabilities,
            PhysicalInputStateV1(axisValues = mapOf(MotionEvent.AXIS_RX to -1f, MotionEvent.AXIS_RY to 1f)))
        assertEquals(N64ButtonMask.C_LEFT or N64ButtonMask.C_DOWN, result.contribution.buttons)
    }

    @Test fun preferredPairWinsWhenBothRightStickPairsExist() {
        val capabilities = capabilities(axisCodes = setOf(
            MotionEvent.AXIS_X, MotionEvent.AXIS_Y,
            MotionEvent.AXIS_Z, MotionEvent.AXIS_RZ,
            MotionEvent.AXIS_RX, MotionEvent.AXIS_RY,
        ))
        val result = ControllerMappingEvaluatorV1.evaluate(profile, capabilities,
            PhysicalInputStateV1(axisValues = mapOf(
                MotionEvent.AXIS_Z to 1f, MotionEvent.AXIS_RX to -1f,
            )))
        assertEquals(N64ButtonMask.C_RIGHT, result.contribution.buttons)
    }

    @Test fun analogTriggerVariantsMapToZAtThreshold() {
        listOf(MotionEvent.AXIS_LTRIGGER, MotionEvent.AXIS_RTRIGGER,
            MotionEvent.AXIS_BRAKE, MotionEvent.AXIS_GAS).forEach { axis ->
            val caps = capabilities(axisCodes = setOf(MotionEvent.AXIS_X, MotionEvent.AXIS_Y, axis),
                oneSided = setOf(axis))
            val below = ControllerMappingEvaluatorV1.evaluate(profile, caps,
                PhysicalInputStateV1(axisValues = mapOf(axis to 0.49f)))
            val pressed = ControllerMappingEvaluatorV1.evaluate(profile, caps,
                PhysicalInputStateV1(axisValues = mapOf(axis to 0.5f)))
            assertEquals(0u, below.contribution.buttons, "axis=$axis below")
            assertEquals(N64ButtonMask.Z, pressed.contribution.buttons, "axis=$axis pressed")
        }
    }

    @Test fun hatAxesMapAllDpadDirections() {
        val caps = capabilities(axisCodes = setOf(MotionEvent.AXIS_X, MotionEvent.AXIS_Y,
            MotionEvent.AXIS_HAT_X, MotionEvent.AXIS_HAT_Y))
        val result = ControllerMappingEvaluatorV1.evaluate(profile, caps,
            PhysicalInputStateV1(axisValues = mapOf(
                MotionEvent.AXIS_HAT_X to -1f,
                MotionEvent.AXIS_HAT_Y to 1f,
            )))
        assertEquals(N64ButtonMask.DPAD_LEFT or N64ButtonMask.DPAD_DOWN, result.contribution.buttons)
    }

    @Test fun keyAndHatDpadRepresentationsCollapseSafely() {
        val result = evaluate(
            keys = setOf(KeyEvent.KEYCODE_DPAD_LEFT),
            axes = mapOf(MotionEvent.AXIS_HAT_X to -1f),
        )
        assertEquals(N64ButtonMask.DPAD_LEFT, result.buttons)
    }

    @Test fun switchProObservedCapabilitiesEvaluateWithoutTriggerOrHatAxes() {
        val caps = ControllerCapabilitiesV1(
            ControllerSourceClassV1.GAMEPAD_AND_JOYSTICK,
            listOf(MotionEvent.AXIS_X, MotionEvent.AXIS_Y, MotionEvent.AXIS_Z, MotionEvent.AXIS_RZ)
                .associateWith { AxisRangeV1(it, -1f, 1f, flat = 0.015f) },
            setOf(KeyEvent.KEYCODE_BUTTON_B, KeyEvent.KEYCODE_BUTTON_L2,
                KeyEvent.KEYCODE_DPAD_RIGHT),
        )
        val result = ControllerMappingEvaluatorV1.evaluate(profile, caps, PhysicalInputStateV1(
            pressedKeys = setOf(KeyEvent.KEYCODE_BUTTON_B, KeyEvent.KEYCODE_BUTTON_L2,
                KeyEvent.KEYCODE_DPAD_RIGHT),
            axisValues = mapOf(MotionEvent.AXIS_X to -1f, MotionEvent.AXIS_Y to -1f,
                MotionEvent.AXIS_Z to 1f),
        )).contribution
        assertEquals(N64ButtonMask.B or N64ButtonMask.Z or N64ButtonMask.DPAD_RIGHT or N64ButtonMask.C_RIGHT,
            result.buttons)
        assertEquals(-128, result.analogX)
        assertEquals(127, result.analogY)
    }

    private fun evaluate(
        keys: Set<Int> = emptySet(),
        axes: Map<Int, Float> = emptyMap(),
    ) = ControllerMappingEvaluatorV1.evaluate(
        profile,
        capabilities(),
        PhysicalInputStateV1(keys, axes),
    ).contribution

    private fun capabilities(
        axisCodes: Set<Int> = setOf(
            MotionEvent.AXIS_X, MotionEvent.AXIS_Y,
            MotionEvent.AXIS_Z, MotionEvent.AXIS_RZ,
            MotionEvent.AXIS_HAT_X, MotionEvent.AXIS_HAT_Y,
            MotionEvent.AXIS_LTRIGGER, MotionEvent.AXIS_RTRIGGER,
            MotionEvent.AXIS_BRAKE, MotionEvent.AXIS_GAS,
        ),
        oneSided: Set<Int> = setOf(
            MotionEvent.AXIS_LTRIGGER, MotionEvent.AXIS_RTRIGGER,
            MotionEvent.AXIS_BRAKE, MotionEvent.AXIS_GAS,
        ),
    ) = ControllerCapabilitiesV1(
        ControllerSourceClassV1.GAMEPAD_AND_JOYSTICK,
        axisCodes.associateWith { axis ->
            if (axis in oneSided) AxisRangeV1(axis, 0f, 1f)
            else AxisRangeV1(axis, -1f, 1f, flat = 0.01f)
        },
    )
}
