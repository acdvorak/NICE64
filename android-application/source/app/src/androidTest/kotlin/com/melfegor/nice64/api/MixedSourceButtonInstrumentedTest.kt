package com.melfegor.nice64.api

import android.view.InputDevice
import android.view.KeyEvent
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class MixedSourceButtonInstrumentedTest {
    @Test fun mixedPhysicalControllerNumberedButtonIsEligibleButKeyboardKeyIsNot() {
        val device = AndroidPhysicalButtonDevice(
            77, "mixed", "Mixed controller",
            InputDevice.SOURCE_JOYSTICK or InputDevice.SOURCE_KEYBOARD,
            false,
        )
        assertTrue(observation(KeyEvent.KEYCODE_BUTTON_1, device).eligible)
        assertFalse(observation(KeyEvent.KEYCODE_A, device).eligible)
    }

    @Test fun preferredNumberedEdgeIsImmediateAndTrailingAliasIsSuppressed() {
        val pipeline = AndroidPhysicalButtonPipeline()
        assertTrue(pipeline.edge(KeyEvent.KEYCODE_BUTTON_1, 1_000) is
            AndroidPhysicalButtonPipelineResult.Ready)
        assertTrue(pipeline.edge(KeyEvent.KEYCODE_DPAD_CENTER, 1_036) is
            AndroidPhysicalButtonPipelineResult.SuppressedAlias)
    }

    @Test fun directGamepadSourcePreservesExistingEligibility() {
        assertTrue(AndroidPhysicalButtonEligibilityPolicy.normalize(
            runtimeDeviceId = 1,
            eventSource = InputDevice.SOURCE_GAMEPAD,
            keyCode = KeyEvent.KEYCODE_BUTTON_A,
            scanCode = 304,
            action = KeyEvent.ACTION_DOWN,
            repeatCount = 0,
            eventTimeMillis = 100,
            downTimeMillis = 100,
            device = null,
        ).eligible)
    }

    private fun observation(keyCode: Int, device: AndroidPhysicalButtonDevice) =
        AndroidPhysicalButtonEligibilityPolicy.normalize(
            runtimeDeviceId = device.runtimeDeviceId,
            eventSource = InputDevice.SOURCE_KEYBOARD,
            keyCode = keyCode,
            scanCode = 288,
            action = KeyEvent.ACTION_DOWN,
            repeatCount = 0,
            eventTimeMillis = 100,
            downTimeMillis = 100,
            device = device,
        )

    private fun AndroidPhysicalButtonPipeline.edge(keyCode: Int, time: Long) = normalize(
        runtimeDeviceId = 77,
        eventSource = InputDevice.SOURCE_KEYBOARD,
        keyCode = keyCode,
        scanCode = 288,
        action = KeyEvent.ACTION_DOWN,
        repeatCount = 0,
        eventTimeMillis = time,
        downTimeMillis = 1_000,
        device = AndroidPhysicalButtonDevice(
            77, "mixed", "Mixed controller",
            InputDevice.SOURCE_JOYSTICK or InputDevice.SOURCE_KEYBOARD,
            false,
        ),
    )
}
