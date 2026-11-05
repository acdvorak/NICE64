package com.melfegor.nice64

import android.view.InputDevice
import android.view.MotionEvent
import androidx.activity.compose.setContent
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.api.BridgeControllerAxisDiagnostic
import com.melfegor.nice64.api.BridgeControllerButtonDiagnostic
import com.melfegor.nice64.api.BridgeControllerDiagnosticDevice
import com.melfegor.nice64.api.BridgeControllerDiagnosticsPresentation
import com.melfegor.nice64.api.BridgeControllerMotionRange
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ControllerDiagnosticsUiTest {
    @get:Rule
    @Suppress("DEPRECATION")
    val composeRule = createAndroidComposeRule<MainActivity>()

    @Test
    fun controllerMetadataAndInputDiagnosticsAreVisible() {
        val source = InputDevice.SOURCE_GAMEPAD or InputDevice.SOURCE_JOYSTICK
        val device = BridgeControllerDiagnosticDevice(
            runtimeDeviceId = 27,
            descriptor = "test-controller-descriptor-1234567890",
            name = "Test USB Controller",
            vendorId = 1118,
            productId = 654,
            sources = source,
            hasGamepadSource = true,
            hasJoystickSource = true,
            controllerNumber = 1,
            motionRanges = listOf(BridgeControllerMotionRange(
                MotionEvent.AXIS_X, "AXIS_X", -1f, 1f, 0.125f,
                InputDevice.SOURCE_JOYSTICK,
            )),
        )
        val state = BridgeControllerDiagnosticsPresentation(
            devices = listOf(device),
            activeRuntimeDeviceId = 27,
            recentButtons = listOf(BridgeControllerButtonDiagnostic(
                27, 96, "KEYCODE_BUTTON_A", "DOWN", InputDevice.SOURCE_GAMEPAD,
            )),
            currentAxes = listOf(BridgeControllerAxisDiagnostic(
                27, MotionEvent.AXIS_X, "AXIS_X", 0.5f, 0.5f,
                -1f, 1f, 0.125f, InputDevice.SOURCE_JOYSTICK,
            )),
        )

        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent { BridgeControllerDiagnostics(state) }
        }

        composeRule.onNodeWithText("Controller identity and raw input").assertIsDisplayed()
        composeRule.onNodeWithText("Runtime device ID").assertIsDisplayed()
        composeRule.onNodeWithText("27 (transient)").assertIsDisplayed()
        composeRule.onNodeWithText("Controller identifier suffix").assertIsDisplayed()
        composeRule.onNodeWithText("Android input sources").assertIsDisplayed()
        composeRule.onNodeWithText("View Inputs").performClick()
        composeRule.onNodeWithText("KEYCODE_BUTTON_A", substring = true).assertIsDisplayed()
        composeRule.onNodeWithText("AXIS_X", substring = true).assertIsDisplayed()
        composeRule.onNodeWithText("flat=0.125", substring = true).assertIsDisplayed()
    }
}
