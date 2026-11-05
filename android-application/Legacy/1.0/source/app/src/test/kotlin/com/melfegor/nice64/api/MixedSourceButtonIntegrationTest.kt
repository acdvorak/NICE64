package com.melfegor.nice64.api

import android.hardware.input.InputManager
import android.view.InputDevice
import android.view.KeyEvent
import com.melfegor.nice64.core.input.ButtonBindingV1
import com.melfegor.nice64.core.input.ControllerProfileV1
import com.melfegor.nice64.core.input.N64SemanticTargetV1
import com.melfegor.nice64.core.model.N64ButtonMask
import com.melfegor.nice64.core.model.N64ControllerState
import kotlinx.coroutines.test.runTest
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertTrue
import org.junit.Test

class MixedSourceButtonIntegrationTest {
    @Test fun mixedButtonOneCreatesAndPersistsOneCaptureCandidate() = runTest {
        val device = device()
        val repository = FakeRepository(profile(device))
        val editor = ControllerMappingEditorCoordinator(repository)
        editor.open("mixed", device)
        assertTrue(editor.beginButtonCapture(N64SemanticTargetV1.A))
        val pipeline = AndroidPhysicalButtonPipeline()

        val preferred = assertIs<AndroidPhysicalButtonPipelineResult.Ready>(
            pipeline.edge(KeyEvent.KEYCODE_BUTTON_1, KeyEvent.ACTION_DOWN, 1_000L),
        ).observation
        val captureInput = MappingCaptureKeyInput(
            preferred.runtimeDeviceId,
            preferred.eligible,
            preferred.pressed,
            preferred.released,
            preferred.repeatCount,
            preferred.keyCode,
        )
        assertTrue(editor.shouldPreDispatchKey(captureInput))
        assertTrue(editor.handleKey(captureInput))
        val candidate = assertIs<MappingCaptureCandidate.Button>(editor.state.value.candidate)
        assertEquals(KeyEvent.KEYCODE_BUTTON_1, candidate.keyCode)

        assertIs<AndroidPhysicalButtonPipelineResult.SuppressedAlias>(
            pipeline.edge(KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.ACTION_DOWN, 1_030L),
        )
        assertEquals(candidate, editor.state.value.candidate)
        editor.applyCandidate()
        assertEquals(
            KeyEvent.KEYCODE_BUTTON_1,
            editor.state.value.working!!.buttonBindings.single {
                it.target == N64SemanticTargetV1.A
            }.keyCode,
        )
    }

    @Test fun wrongSelectedDeviceStillCannotCreateCaptureCandidate() = runTest {
        val editor = ControllerMappingEditorCoordinator(FakeRepository(profile(device())))
        editor.open("mixed", device())
        editor.beginButtonCapture(N64SemanticTargetV1.A)
        val input = MappingCaptureKeyInput(
            deviceId = 8,
            eligibleSource = true,
            down = true,
            up = false,
            repeatCount = 0,
            keyCode = KeyEvent.KEYCODE_BUTTON_1,
        )
        assertFalse(editor.shouldPreDispatchKey(input))
        assertTrue(editor.state.value.captureActive)
    }

    @Test fun mappedMixedButtonProducesOneDownAndOneUpWithoutAliasMutation() = runTest {
        val device = device()
        val repository = FakeRepository(profile(device).copy(buttonBindings = listOf(
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_1, N64SemanticTargetV1.A),
        )))
        val cache = LiveControllerProfileCache(repository)
        cache.refresh()
        val holder = BridgeLatestControllerStateHolder()
        val source = FakeDeviceSource(mutableMapOf(7 to device))
        val pipeline = AndroidPhysicalButtonPipeline()
        val coordinator = BridgeControllerHumanInputCoordinator(holder, source, cache, pipeline)
        coordinator.setEnabled(true)
        coordinator.setInputMode(MobileInputMode.CONTROLLER)

        val down = assertIs<AndroidPhysicalButtonPipelineResult.Ready>(
            pipeline.edge(KeyEvent.KEYCODE_BUTTON_1, KeyEvent.ACTION_DOWN, 1_000L),
        ).observation
        assertTrue(coordinator.handleKeyEvent(down))
        assertEquals(N64ButtonMask.A, holder.current().buttons)

        assertIs<AndroidPhysicalButtonPipelineResult.SuppressedAlias>(
            pipeline.edge(KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.ACTION_DOWN, 1_030L),
        )
        assertEquals(N64ButtonMask.A, holder.current().buttons)

        val up = assertIs<AndroidPhysicalButtonPipelineResult.Ready>(
            pipeline.edge(KeyEvent.KEYCODE_BUTTON_1, KeyEvent.ACTION_UP, 1_100L),
        ).observation
        assertTrue(coordinator.handleKeyEvent(up))
        assertEquals(N64ControllerState.Neutral, holder.current())
        assertIs<AndroidPhysicalButtonPipelineResult.SuppressedAlias>(
            pipeline.edge(KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.ACTION_UP, 1_120L),
        )
        assertEquals(N64ControllerState.Neutral, holder.current())
        coordinator.close()
    }

    @Test fun unboundNumberedButtonRetainsExistingGameplayFallback() = runTest {
        val device = device()
        val repository = FakeRepository(profile(device).copy(buttonBindings = emptyList()))
        val cache = LiveControllerProfileCache(repository)
        cache.refresh()
        val coordinator = BridgeControllerHumanInputCoordinator(
            BridgeLatestControllerStateHolder(),
            FakeDeviceSource(mutableMapOf(7 to device)),
            cache,
        )
        coordinator.setEnabled(true)
        coordinator.setInputMode(MobileInputMode.CONTROLLER)
        assertFalse(coordinator.handleKeyEvent(observation(KeyEvent.KEYCODE_BUTTON_1, true)))
        coordinator.close()
    }

    private fun AndroidPhysicalButtonPipeline.edge(
        keyCode: Int,
        action: Int,
        time: Long,
    ) = normalize(
        runtimeDeviceId = 7,
        eventSource = InputDevice.SOURCE_KEYBOARD,
        keyCode = keyCode,
        scanCode = 288,
        action = action,
        repeatCount = 0,
        eventTimeMillis = time,
        downTimeMillis = 1_000L,
        device = AndroidPhysicalButtonDevice(
            7, "mixed-descriptor", "Mixed controller",
            InputDevice.SOURCE_JOYSTICK or InputDevice.SOURCE_KEYBOARD,
            false,
        ),
    )

    private fun observation(keyCode: Int, down: Boolean) =
        AndroidPhysicalButtonEligibilityPolicy.normalize(
            runtimeDeviceId = 7,
            eventSource = InputDevice.SOURCE_KEYBOARD,
            keyCode = keyCode,
            scanCode = 288,
            action = if (down) KeyEvent.ACTION_DOWN else KeyEvent.ACTION_UP,
            repeatCount = 0,
            eventTimeMillis = 1_000L,
            downTimeMillis = 1_000L,
            device = AndroidPhysicalButtonDevice(
                7, "mixed-descriptor", "Mixed controller",
                InputDevice.SOURCE_JOYSTICK or InputDevice.SOURCE_KEYBOARD,
                false,
            ),
        )

    private fun profile(device: BridgeControllerDiagnosticDevice): ControllerProfileV1 =
        StandardAndroidGamepadProfile.profile.copy(
            profileId = "mixed",
            profileName = "Mixed",
            identity = device.toStableIdentity(),
        )

    private fun device() = BridgeControllerDiagnosticDevice(
        runtimeDeviceId = 7,
        descriptor = "mixed-descriptor",
        name = "Mixed controller",
        vendorId = 0x0079,
        productId = 0x0006,
        sources = InputDevice.SOURCE_JOYSTICK or InputDevice.SOURCE_KEYBOARD,
        hasGamepadSource = false,
        hasJoystickSource = true,
        controllerNumber = 0,
        motionRanges = emptyList(),
        isVirtual = false,
    )

    private class FakeRepository(initial: ControllerProfileV1) : ControllerProfileUiRepository {
        private var snapshot = ControllerProfileUiRepositorySnapshot(
            listOf(initial), initial.profileId, initial.identity,
        )
        override suspend fun load() = snapshot
        override suspend fun create(profile: ControllerProfileV1) = Unit
        override suspend fun update(profile: ControllerProfileV1) {
            snapshot = snapshot.copy(profiles = listOf(profile))
        }
        override suspend fun select(profileId: String?) = Unit
        override suspend fun delete(profileId: String) = Unit
        override suspend fun selectController(
            identity: com.melfegor.nice64.core.input.ControllerIdentityV1?,
        ) = Unit
    }

    private class FakeDeviceSource(
        private val devices: MutableMap<Int, BridgeControllerDiagnosticDevice>,
    ) : BridgeControllerDeviceSource {
        override fun inventory() = devices.values.toList()
        override fun descriptor(runtimeDeviceId: Int) = devices[runtimeDeviceId]
        override fun register(listener: InputManager.InputDeviceListener) = Unit
        override fun unregister(listener: InputManager.InputDeviceListener) = Unit
    }
}
