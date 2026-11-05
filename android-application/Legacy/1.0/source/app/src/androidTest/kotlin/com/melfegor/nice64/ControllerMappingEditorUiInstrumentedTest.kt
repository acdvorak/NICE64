package com.melfegor.nice64

import android.view.KeyEvent
import android.view.InputDevice
import android.hardware.input.InputManager
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.api.MappingCaptureCandidate
import com.melfegor.nice64.api.MappingCapturePhase
import com.melfegor.nice64.api.MappingEditorState
import com.melfegor.nice64.api.MappingReconnectCandidate
import com.melfegor.nice64.api.StandardAndroidGamepadProfile
import com.melfegor.nice64.api.BridgeControllerDeviceSource
import com.melfegor.nice64.api.BridgeControllerHumanInputCoordinator
import com.melfegor.nice64.api.BridgeLatestControllerStateHolder
import com.melfegor.nice64.api.MobileInputMode
import com.melfegor.nice64.core.model.N64ButtonMask
import com.melfegor.nice64.core.input.N64SemanticTargetV1
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ControllerMappingEditorUiInstrumentedTest {
    @get:Rule
    @Suppress("DEPRECATION")
    val composeRule = createAndroidComposeRule<MainActivity>()

    @Test fun editorRendersEveryTargetAndCurrentMappings() {
        show(state())
        composeRule.onNodeWithText("Edit controller mapping").assertIsDisplayed()
        composeRule.onAllNodesWithText("My Switch Pro").assertCountEquals(2)
        composeRule.onNodeWithText("Custom profile").assertIsDisplayed()
        composeRule.onAllNodesWithText("No unsaved changes").assertCountEquals(2)
        composeRule.onNodeWithText("Face buttons").assertIsDisplayed()
        composeRule.onNodeWithText("Shoulder / Z / Start").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("N64 A").assertIsDisplayed()
        composeRule.onNodeWithText("Button A", substring = true).assertIsDisplayed()
        composeRule.onNodeWithText("Analog Y").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("Axis Y · both").performScrollTo().assertIsDisplayed()
        composeRule.onAllNodesWithText("Analog settings").assertCountEquals(2)
        composeRule.onAllNodesWithText("0.12").assertCountEquals(2)
    }

    @Test fun enteringAndCancellingCaptureUsesBoundedAction() {
        val captured = AtomicReference<N64SemanticTargetV1?>()
        val cancelled = AtomicBoolean(false)
        show(
            state(),
            MappingEditorActions(
                onButtonCapture = { captured.set(it) },
                onCancelCapture = { cancelled.set(true) },
            ),
        )
        composeRule.onNodeWithTag("map-button-A").performClick()
        assertEquals(N64SemanticTargetV1.A, captured.get())

        show(
            state().copy(
                phase = MappingCapturePhase.WAITING_FOR_BUTTON,
                captureTarget = N64SemanticTargetV1.A,
                statusMessage = "Press a controller button.",
            ),
            MappingEditorActions(onCancelCapture = { cancelled.set(true) }),
        )
        composeRule.onNodeWithTag("cancel-capture")
            .performScrollTo()
            .assertIsEnabled()
            .performClick()
        assertTrue(cancelled.get())
        composeRule.onNodeWithText("Capture active").assertIsDisplayed()
        composeRule.onAllNodesWithText("N64 A").assertCountEquals(2)
    }

    @Test fun conflictRequiresExplicitReplace() {
        val replaced = AtomicBoolean(false)
        val conflicted = state().copy(
            phase = MappingCapturePhase.CONFLICT,
            captureTarget = N64SemanticTargetV1.B,
            candidate = MappingCaptureCandidate.Button(KeyEvent.KEYCODE_BUTTON_A, N64SemanticTargetV1.B),
            conflicts = listOf("Button 96 is mapped to N64 A."),
        )
        show(conflicted, MappingEditorActions(onApplyCandidate = { replaced.set(true) }))
        composeRule.onNodeWithText("Replace conflicting mapping?").assertIsDisplayed()
        composeRule.onNodeWithText("Requested target").assertIsDisplayed()
        composeRule.onNodeWithText("Detected input").assertIsDisplayed()
        composeRule.onNodeWithText("Existing assignment conflict").assertIsDisplayed()
        composeRule.onNodeWithText("Button 96 is mapped to N64 A.").assertIsDisplayed()
        composeRule.onNodeWithTag("apply-candidate").performClick()
        assertTrue(replaced.get())
    }

    @Test fun clearRestoreSaveAndUnsavedDiscardAreExplicit() {
        val cleared = AtomicReference<N64SemanticTargetV1?>()
        val restore = AtomicBoolean(false)
        val saved = AtomicBoolean(false)
        val closed = AtomicBoolean(false)
        val discarded = AtomicBoolean(false)
        val actions = MappingEditorActions(
            onClear = { cleared.set(it) },
            onRequestRestore = { restore.set(true) },
            onSave = { saved.set(true) },
            onClose = { closed.set(true) },
            onDiscard = { discarded.set(true) },
        )
        show(state().copy(dirty = true), actions)
        composeRule.onAllNodesWithText("Unsaved changes").assertCountEquals(2)
        composeRule.onNodeWithTag("clear-A").performClick()
        assertEquals(N64SemanticTargetV1.A, cleared.get())
        composeRule.onNodeWithTag("restore-defaults").performScrollTo().performClick()
        assertTrue(restore.get())
        composeRule.onNodeWithTag("save-mappings").performScrollTo().performClick()
        assertTrue(saved.get())
        composeRule.onNodeWithTag("close-editor").performScrollTo().performClick()
        assertTrue(closed.get())

        show(state().copy(dirty = true, discardConfirmation = true), actions)
        composeRule.onNodeWithText("Discard changes?").assertIsDisplayed()
        composeRule.onNodeWithTag("confirm-discard").performClick()
        assertTrue(discarded.get())
    }

    @Test fun restoreConfirmationAndSavedAuthoritativeStateRender() {
        val restored = AtomicBoolean(false)
        show(
            state().copy(restoreConfirmation = true),
            MappingEditorActions(onConfirmRestore = { restored.set(true) }),
        )
        composeRule.onNodeWithText("Restore mapping?").assertIsDisplayed()
        composeRule.onNodeWithTag("confirm-restore").performClick()
        assertTrue(restored.get())

        show(state().copy(dirty = false, statusMessage = "Profile saved and reloaded."))
        composeRule.onNodeWithText("Profile saved and reloaded.").assertIsDisplayed()
    }

    @Test fun physicalCaptureGateClearsAndSuppressesOnlyGamepadContribution() {
        val holder = BridgeLatestControllerStateHolder()
        val coordinator = BridgeControllerHumanInputCoordinator(holder, EmptyDeviceSource())
        coordinator.setEnabled(true)
        coordinator.setInputMode(MobileInputMode.CONTROLLER)

        assertTrue(coordinator.handleKeyEvent(gamepadKey(KeyEvent.KEYCODE_BUTTON_A)))
        assertEquals(N64ButtonMask.A, holder.current().buttons)
        coordinator.setPhysicalCaptureSuppressed(true)
        assertEquals(0u, holder.current().buttons)
        assertTrue(coordinator.handleKeyEvent(gamepadKey(KeyEvent.KEYCODE_BUTTON_B)))
        assertEquals(0u, holder.current().buttons)

        coordinator.setPhysicalCaptureSuppressed(false)
        assertTrue(coordinator.handleKeyEvent(gamepadKey(KeyEvent.KEYCODE_BUTTON_B)))
        assertEquals(N64ButtonMask.B, holder.current().buttons)
        coordinator.close()
    }

    @Test fun captureStatusKeepsTargetViewportStableAcrossCaptureAndCandidate() {
        var editorState by mutableStateOf(state())
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    ControllerMappingEditorScreen(editorState)
                }
            }
        }
        val target = composeRule.onNodeWithTag("map-button-DPAD_UP")
        target.performScrollTo()
        val idleTop = target.fetchSemanticsNode().boundsInRoot.top

        composeRule.runOnIdle {
            editorState = editorState.copy(
                phase = MappingCapturePhase.WAITING_FOR_BUTTON,
                captureTarget = N64SemanticTargetV1.DPAD_UP,
                statusMessage = "Press a controller button.",
            )
        }
        composeRule.waitForIdle()
        val captureTop = target.fetchSemanticsNode().boundsInRoot.top
        assertEquals(idleTop, captureTop, 1f)

        composeRule.runOnIdle {
            editorState = editorState.copy(
                phase = MappingCapturePhase.CONFLICT,
                candidate = MappingCaptureCandidate.Button(
                    KeyEvent.KEYCODE_DPAD_UP,
                    N64SemanticTargetV1.DPAD_UP,
                ),
                conflicts = listOf("D-pad Up already has a mapping."),
                statusMessage = null,
            )
        }
        composeRule.onNodeWithText("Replace conflicting mapping?").assertIsDisplayed()
        val candidateTop = target.fetchSemanticsNode().boundsInRoot.top
        assertEquals(idleTop, candidateTop, 1f)
    }

    @Test fun reconnectRecoveryAndMajorControlsExposeAccessibleSemantics() {
        show(
            state().copy(
                deviceAvailable = false,
                reconnectCandidate = MappingReconnectCandidate(101, "Nintendo Switch Pro Controller"),
            ),
        )
        composeRule.onNodeWithContentDescription("Use the exact reconnected controller for mapping capture")
            .assertIsDisplayed()
        composeRule.onNodeWithContentDescription("Capture a controller button for N64 A")
            .performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithContentDescription("Map the N64 analog stick X and Y axes")
            .performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithContentDescription("Save and verify this custom controller profile")
            .performScrollTo().assertIsDisplayed()
    }

    @Test fun cleanRenderingDoesNotInvokeMappingActions() {
        var mutations = 0
        show(
            state(),
            MappingEditorActions(
                onName = { mutations++ },
                onButtonCapture = { mutations++ },
                onAxisCapture = { mutations++ },
                onAnalogPairCapture = { mutations++ },
                onCancelCapture = { mutations++ },
                onApplyCandidate = { mutations++ },
                onClear = { mutations++ },
                onToggleInvert = { mutations++ },
                onDeadZone = { _, _ -> mutations++ },
                onScale = { _, _ -> mutations++ },
                onRequestRestore = { mutations++ },
                onSave = { mutations++ },
                onClose = { mutations++ },
            ),
        )

        composeRule.onNodeWithText("Edit controller mapping").assertIsDisplayed()
        composeRule.runOnIdle { assertEquals(0, mutations) }
    }

    private fun show(state: MappingEditorState, actions: MappingEditorActions = MappingEditorActions()) {
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    ControllerMappingEditorScreen(state, actions)
                }
            }
        }
    }

    private fun state() = MappingEditorState(
        active = true,
        original = profile(),
        working = profile(),
        managementRuntimeDeviceId = 7,
        managementDeviceName = "Nintendo Switch Pro Controller",
        deviceAvailable = true,
        phase = MappingCapturePhase.IDLE,
    )

    private fun profile() = StandardAndroidGamepadProfile.profile.copy(
        profileId = "custom-ui",
        profileName = "My Switch Pro",
    )

    private fun gamepadKey(keyCode: Int) = KeyEvent(KeyEvent.ACTION_DOWN, keyCode).also {
        it.source = InputDevice.SOURCE_GAMEPAD
    }

    private class EmptyDeviceSource : BridgeControllerDeviceSource {
        override fun inventory() = emptyList<com.melfegor.nice64.api.BridgeControllerDiagnosticDevice>()
        override fun descriptor(runtimeDeviceId: Int) = null
        override fun register(listener: InputManager.InputDeviceListener) = Unit
        override fun unregister(listener: InputManager.InputDeviceListener) = Unit
    }
}
