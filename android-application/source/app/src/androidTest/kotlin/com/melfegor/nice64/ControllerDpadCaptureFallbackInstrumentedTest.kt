package com.melfegor.nice64

import android.view.InputDevice
import android.view.KeyEvent
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.api.BridgeControllerDiagnosticDevice
import com.melfegor.nice64.api.ControllerMappingEditorCoordinator
import com.melfegor.nice64.api.ControllerProfileUiRepository
import com.melfegor.nice64.api.ControllerProfileUiRepositorySnapshot
import com.melfegor.nice64.api.MappingCaptureCandidate
import com.melfegor.nice64.api.MappingCaptureKeyInput
import com.melfegor.nice64.api.StandardAndroidGamepadProfile
import com.melfegor.nice64.api.toStableIdentity
import com.melfegor.nice64.core.input.ControllerIdentityV1
import com.melfegor.nice64.core.input.ControllerProfileV1
import com.melfegor.nice64.core.input.N64SemanticTargetV1
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ControllerDpadCaptureFallbackInstrumentedTest {
    @Test fun dpadUpFallbackCreatesNormalButtonCandidate() = runTest {
        val editor = editor()
        editor.beginButtonCapture(N64SemanticTargetV1.DPAD_UP)
        val up = input(down = false)

        assertTrue(editor.shouldPreDispatchKey(up))
        assertTrue(editor.handleKey(up))
        assertEquals(
            KeyEvent.KEYCODE_DPAD_UP,
            (editor.state.value.candidate as MappingCaptureCandidate.Button).keyCode,
        )
    }

    @Test fun consumedCaptureDpadStopsBeforeNormalUiDispatch() {
        val event = KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_DPAD_UP)
        var normalUiDispatchReached = false

        val consumed = preDispatchControllerCaptureKeyEvent(event) { true }
        if (!consumed) normalUiDispatchReached = true

        assertTrue(consumed)
        assertFalse(normalUiDispatchReached)
    }

    @Test fun dpadOutsideCaptureRemainsAvailableToNormalUiRouting() = runTest {
        val editor = editor()
        val up = input(down = false)

        assertFalse(editor.shouldPreDispatchKey(up))
        assertFalse(editor.handleKey(up))
    }

    @Test fun syntheticDpadDownThenUpCreatesOnlyOneCandidateAndConsumesRelease() = runTest {
        val editor = editor()
        editor.beginButtonCapture(N64SemanticTargetV1.DPAD_UP)
        val down = input(down = true)
        val up = input(down = false)

        assertTrue(editor.handleKey(down))
        val candidate = editor.state.value.candidate
        assertTrue(editor.shouldPreDispatchKey(up))
        assertTrue(editor.handleKey(up))
        assertEquals(candidate, editor.state.value.candidate)
        assertFalse(editor.shouldPreDispatchKey(up))
    }

    private suspend fun editor(): ControllerMappingEditorCoordinator {
        val editor = ControllerMappingEditorCoordinator(FakeRepository(profile()))
        editor.open("custom-dpad", device())
        return editor
    }

    private fun input(down: Boolean) = MappingCaptureKeyInput(
        deviceId = 7,
        eligibleSource = true,
        down = down,
        up = !down,
        repeatCount = 0,
        keyCode = KeyEvent.KEYCODE_DPAD_UP,
    )

    private fun profile() = StandardAndroidGamepadProfile.profile.copy(
        profileId = "custom-dpad",
        profileName = "Custom D-pad",
        identity = device().toStableIdentity(),
    )

    private fun device() = BridgeControllerDiagnosticDevice(
        runtimeDeviceId = 7,
        descriptor = "switch-pro-descriptor",
        name = "Nintendo Switch Pro Controller",
        vendorId = 0x057e,
        productId = 0x2009,
        sources = InputDevice.SOURCE_GAMEPAD or InputDevice.SOURCE_JOYSTICK,
        hasGamepadSource = true,
        hasJoystickSource = true,
        controllerNumber = 1,
        motionRanges = emptyList(),
    )

    private class FakeRepository(profile: ControllerProfileV1) : ControllerProfileUiRepository {
        private var snapshot = ControllerProfileUiRepositorySnapshot(
            profiles = listOf(profile),
            selectedProfileId = profile.profileId,
            selectedControllerIdentity = profile.identity,
        )

        override suspend fun load() = snapshot
        override suspend fun create(profile: ControllerProfileV1) = Unit
        override suspend fun update(profile: ControllerProfileV1) {
            snapshot = snapshot.copy(profiles = listOf(profile))
        }
        override suspend fun select(profileId: String?) = Unit
        override suspend fun delete(profileId: String) = Unit
        override suspend fun selectController(identity: ControllerIdentityV1?) = Unit
    }
}
