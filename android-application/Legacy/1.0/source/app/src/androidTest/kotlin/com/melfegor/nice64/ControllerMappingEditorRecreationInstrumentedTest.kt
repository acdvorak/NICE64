package com.melfegor.nice64

import android.hardware.input.InputManager
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.api.BridgeControllerDiagnosticDevice
import com.melfegor.nice64.api.BridgeControllerDiagnosticsPresentation
import com.melfegor.nice64.api.BridgeControllerMotionRange
import com.melfegor.nice64.api.MappingCaptureAxisInput
import com.melfegor.nice64.api.MappingCaptureKeyInput
import com.melfegor.nice64.api.MappingCapturePhase
import com.melfegor.nice64.api.StandardAndroidGamepadProfile
import com.melfegor.nice64.api.toStableIdentity
import com.melfegor.nice64.core.input.N64SemanticTargetV1
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ControllerMappingEditorRecreationInstrumentedTest {
    @get:Rule
    @Suppress("DEPRECATION")
    val composeRule = createAndroidComposeRule<ControllerMappingEditorRecreationTestActivity>()

    @Before fun resetEditor() {
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.model.editor.discardAndClose()
            RecreationEditorRepository.reset(profile())
            activity.model.open(profile(), device())
        }
        composeRule.waitForIdle()
    }

    @Test fun idleAndDirtyWorkingCopySurviveActivityRecreation() {
        val before = activity().model
        recreate()
        assertSame(before, activity().model)
        assertTrue(activity().model.editor.state.value.active)

        activity().model.editor.setProfileName("Dirty across rotation")
        recreate()
        val state = activity().model.editor.state.value
        assertEquals("Dirty across rotation", state.working!!.profileName)
        assertEquals("Custom", state.original!!.profileName)
        assertTrue(state.dirty)
        assertEquals(0, RecreationEditorRepository.updateCount)
    }

    @Test fun buttonAndAxisCaptureSurviveActivityRecreation() {
        activity().model.editor.beginButtonCapture(N64SemanticTargetV1.A)
        recreate()
        assertEquals(MappingCapturePhase.WAITING_FOR_BUTTON, activity().model.editor.state.value.phase)

        activity().model.editor.cancelCapture()
        activity().model.editor.beginAxisCapture(N64SemanticTargetV1.C_UP)
        recreate()
        assertEquals(MappingCapturePhase.WAITING_FOR_AXIS, activity().model.editor.state.value.phase)
    }

    @Test fun analogPairRetainsCapturedXAcrossActivityRecreation() {
        val editor = activity().model.editor
        editor.beginAnalogPairCapture()
        editor.handleAxes(MappingCaptureAxisInput(100, true, mapOf(MotionEvent.AXIS_X to 0.9f)))
        assertEquals(MappingCapturePhase.WAITING_FOR_ANALOG_PAIR_Y, editor.state.value.phase)

        recreate()
        val retained = activity().model.editor
        retained.handleAxes(MappingCaptureAxisInput(100, true, mapOf(MotionEvent.AXIS_Y to 0.9f)))
        assertEquals(MappingCapturePhase.CONFLICT, retained.state.value.phase)
        assertTrue(retained.state.value.candidate != null)
    }

    @Test fun conflictDiscardAndRestoreDialogsReappearAfterActivityRecreation() {
        val editor = activity().model.editor
        editor.beginButtonCapture(N64SemanticTargetV1.B)
        editor.handleKey(key(KeyEvent.KEYCODE_BUTTON_A))
        recreate()
        composeRule.onNodeWithText("Replace conflicting mapping?").assertIsDisplayed()

        editor.cancelCapture()
        editor.setProfileName("Dirty")
        editor.requestClose()
        recreate()
        composeRule.onNodeWithText("Discard changes?").assertIsDisplayed()

        editor.continueEditing()
        editor.requestRestoreDefaults()
        recreate()
        composeRule.onNodeWithText("Restore mapping?").assertIsDisplayed()
    }

    @Test fun saveInFlightSurvivesRecreationAndCannotWriteTwice() {
        val model = activity().model
        model.editor.setProfileName("Saved once")
        RecreationEditorRepository.delayUpdate = true
        model.save()
        composeRule.waitUntil(5_000) { RecreationEditorRepository.updateCount == 1 }
        assertEquals(MappingCapturePhase.SAVING, model.editor.state.value.phase)

        recreate()
        assertSame(model, activity().model)
        activity().model.save()
        composeRule.waitForIdle()
        assertEquals(1, RecreationEditorRepository.updateCount)

        RecreationEditorRepository.releaseUpdate.complete(Unit)
        composeRule.waitUntil(5_000) { !activity().model.editor.state.value.dirty }
        assertEquals(1, RecreationEditorRepository.updateCount)
        assertEquals(1, RecreationEditorRepository.authoritativeReloadCount)
        assertEquals("Profile saved and reloaded.", activity().model.editor.state.value.statusMessage)
    }

    private fun recreate() {
        composeRule.activityRule.scenario.recreate()
        composeRule.waitForIdle()
    }

    private fun activity(): ControllerMappingEditorRecreationTestActivity {
        lateinit var value: ControllerMappingEditorRecreationTestActivity
        composeRule.activityRule.scenario.onActivity { value = it }
        return value
    }

    private fun key(keyCode: Int) = MappingCaptureKeyInput(100, true, true, false, 0, keyCode)

    companion object {
        private fun profile() = StandardAndroidGamepadProfile.profile.copy(
            profileId = "recreation-profile",
            profileName = "Custom",
            identity = device().toStableIdentity(),
        )

        private fun device() = BridgeControllerDiagnosticDevice(
            runtimeDeviceId = 100,
            descriptor = "switch-pro-descriptor",
            name = "Nintendo Switch Pro Controller",
            vendorId = 0x057e,
            productId = 0x2009,
            sources = InputDevice.SOURCE_GAMEPAD or InputDevice.SOURCE_JOYSTICK,
            hasGamepadSource = true,
            hasJoystickSource = true,
            controllerNumber = 1,
            motionRanges = listOf(
                range(MotionEvent.AXIS_X), range(MotionEvent.AXIS_Y),
                range(MotionEvent.AXIS_Z), range(MotionEvent.AXIS_RZ),
            ),
        )

        private fun range(axis: Int) = BridgeControllerMotionRange(
            axis, MotionEvent.axisToString(axis), -1f, 1f, 0.08f, InputDevice.SOURCE_JOYSTICK,
        )
    }
}
