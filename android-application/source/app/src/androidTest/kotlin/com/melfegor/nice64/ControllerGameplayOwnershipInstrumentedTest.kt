package com.melfegor.nice64

import android.hardware.input.InputManager
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.api.BridgeControllerDeviceSource
import com.melfegor.nice64.api.BridgeControllerDiagnosticDevice
import com.melfegor.nice64.api.BridgeControllerHumanInputCoordinator
import com.melfegor.nice64.api.BridgeControllerMotionRange
import com.melfegor.nice64.api.BridgeLatestControllerStateHolder
import com.melfegor.nice64.api.ControllerProfileUiRepository
import com.melfegor.nice64.api.ControllerProfileUiRepositorySnapshot
import com.melfegor.nice64.api.LiveControllerProfileCache
import com.melfegor.nice64.api.MobileInputMode
import com.melfegor.nice64.api.StandardAndroidGamepadProfile
import com.melfegor.nice64.api.toStableIdentity
import com.melfegor.nice64.core.input.ButtonBindingV1
import com.melfegor.nice64.core.input.ControllerIdentityV1
import com.melfegor.nice64.core.input.ControllerProfileV1
import com.melfegor.nice64.core.input.N64SemanticTargetV1
import com.melfegor.nice64.core.model.N64ButtonMask
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ControllerGameplayOwnershipInstrumentedTest {
    @Test fun captureThenGameplayThenUiPrecedenceAppliesToKeysAndMotion() {
        val key = key(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_DPAD_LEFT)
        val motion = MotionEvent.obtain(0, 0, MotionEvent.ACTION_MOVE, 0f, 0f, 0).apply {
            source = InputDevice.SOURCE_JOYSTICK
        }
        try {
            listOf<Any>(key, motion).forEach { event ->
                val captureCalls = mutableListOf<String>()
                assertTrue(dispatchControllerInputWithPrecedence(
                    event,
                    capture = { captureCalls += "capture"; true },
                    gameplay = { captureCalls += "gameplay"; true },
                    ui = { captureCalls += "ui"; true },
                ))
                assertEquals(listOf("capture"), captureCalls)

                val gameplayCalls = mutableListOf<String>()
                assertTrue(dispatchControllerInputWithPrecedence(
                    event,
                    capture = { gameplayCalls += "capture"; false },
                    gameplay = { gameplayCalls += "gameplay"; true },
                    ui = { gameplayCalls += "ui"; true },
                ))
                assertEquals(listOf("capture", "gameplay"), gameplayCalls)

                val uiCalls = mutableListOf<String>()
                assertTrue(dispatchControllerInputWithPrecedence(
                    event,
                    capture = { uiCalls += "capture"; false },
                    gameplay = { uiCalls += "gameplay"; false },
                    ui = { uiCalls += "ui"; true },
                ))
                assertEquals(listOf("capture", "gameplay", "ui"), uiCalls)
            }
        } finally {
            motion.recycle()
        }
    }

    @Test fun gameplayOwnedDpadAndFaceButtonsNeverReachUiAndApplyOnce() {
        listOf(
            KeyEvent.KEYCODE_DPAD_UP,
            KeyEvent.KEYCODE_DPAD_DOWN,
            KeyEvent.KEYCODE_DPAD_LEFT,
            KeyEvent.KEYCODE_DPAD_RIGHT,
            KeyEvent.KEYCODE_BUTTON_A,
        ).forEach { keyCode ->
            var gameplayCount = 0
            var uiCount = 0
            assertTrue(dispatchControllerInputWithPrecedence(
                key(KeyEvent.ACTION_DOWN, keyCode),
                capture = { false },
                gameplay = { gameplayCount++; true },
                ui = { uiCount++; true },
            ))
            assertEquals(1, gameplayCount)
            assertEquals(0, uiCount)
        }
    }

    @Test fun standardAndCustomDpadUseTheSameCoordinatorStream() = runBlocking {
        val diagnostic = device()
        val source = FakeDeviceSource(diagnostic)
        val repository = FakeRepository()
        val cache = LiveControllerProfileCache(repository).also { it.refresh() }
        val holder = BridgeLatestControllerStateHolder()
        val coordinator = BridgeControllerHumanInputCoordinator(holder, source, cache)
        coordinator.setEnabled(true)
        coordinator.setInputMode(MobileInputMode.CONTROLLER)

        val mappings = listOf(
            KeyEvent.KEYCODE_DPAD_UP to N64ButtonMask.DPAD_UP,
            KeyEvent.KEYCODE_DPAD_DOWN to N64ButtonMask.DPAD_DOWN,
            KeyEvent.KEYCODE_DPAD_LEFT to N64ButtonMask.DPAD_LEFT,
            KeyEvent.KEYCODE_DPAD_RIGHT to N64ButtonMask.DPAD_RIGHT,
        )
        mappings.forEach { (keyCode, mask) ->
            assertTrue(coordinator.handleKeyEvent(key(KeyEvent.ACTION_DOWN, keyCode)))
            assertEquals(mask, holder.current().buttons)
            assertTrue(coordinator.handleKeyEvent(key(KeyEvent.ACTION_UP, keyCode)))
            assertTrue(holder.current().isNeutral)
        }

        val custom = StandardAndroidGamepadProfile.profile.copy(
            profileId = "ownership-custom",
            profileName = "Ownership Custom",
            identity = diagnostic.toStableIdentity(),
            buttonBindings = mappings.map { (keyCode, mask) ->
                ButtonBindingV1(keyCode, when (mask) {
                    N64ButtonMask.DPAD_UP -> N64SemanticTargetV1.DPAD_UP
                    N64ButtonMask.DPAD_DOWN -> N64SemanticTargetV1.DPAD_DOWN
                    N64ButtonMask.DPAD_LEFT -> N64SemanticTargetV1.DPAD_LEFT
                    else -> N64SemanticTargetV1.DPAD_RIGHT
                })
            },
            axisBindings = emptyList(),
            axisAlternatives = emptyList(),
        )
        repository.snapshot = ControllerProfileUiRepositorySnapshot(listOf(custom), custom.profileId, null)
        cache.refresh()
        coordinator.reconcileLiveMapping()
        mappings.forEach { (keyCode, mask) ->
            assertTrue(coordinator.handleKeyEvent(key(KeyEvent.ACTION_DOWN, keyCode)))
            assertEquals(mask, holder.current().buttons)
            assertTrue(coordinator.handleKeyEvent(key(KeyEvent.ACTION_UP, keyCode)))
            assertTrue(holder.current().isNeutral)
        }
        assertEquals(2, repository.loads)
        assertEquals(1, source.registerCalls)
        coordinator.close()
        assertEquals(1, source.unregisterCalls)
    }

    @Test fun rapidCustomDownUpDownUpProducesFourOrderedSemanticTransitions() = runBlocking {
        val diagnostic = device()
        val source = FakeDeviceSource(diagnostic)
        val custom = StandardAndroidGamepadProfile.profile.copy(
            profileId = "rapid-custom",
            profileName = "Rapid Custom",
            identity = diagnostic.toStableIdentity(),
            buttonBindings = listOf(ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_A, N64SemanticTargetV1.A)),
            axisBindings = emptyList(),
            axisAlternatives = emptyList(),
        )
        val repository = FakeRepository(
            ControllerProfileUiRepositorySnapshot(listOf(custom), custom.profileId, null),
        )
        val cache = LiveControllerProfileCache(repository).also { it.refresh() }
        val holder = BridgeLatestControllerStateHolder()
        val coordinator = BridgeControllerHumanInputCoordinator(holder, source, cache)
        coordinator.setEnabled(true)
        coordinator.setInputMode(MobileInputMode.CONTROLLER)

        val states = listOf(
            key(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BUTTON_A),
            key(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_BUTTON_A),
            key(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BUTTON_A),
            key(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_BUTTON_A),
        ).map { event ->
            assertTrue(coordinator.handleKeyEvent(event))
            holder.current().buttons
        }
        assertEquals(listOf(N64ButtonMask.A, 0u, N64ButtonMask.A, 0u), states)

        assertTrue(coordinator.handleKeyEvent(key(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BUTTON_A)))
        assertTrue(coordinator.handleKeyEvent(key(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BUTTON_A, repeat = 1)))
        assertTrue(coordinator.handleKeyEvent(key(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_BUTTON_A)))
        assertTrue(holder.current().isNeutral)
        assertTrue(coordinator.handleKeyEvent(key(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BUTTON_A)))
        assertEquals(N64ButtonMask.A, holder.current().buttons)
        assertEquals(1, repository.loads)
        coordinator.close()
    }

    private fun key(action: Int, keyCode: Int, repeat: Int = 0) = KeyEvent(
        0L, 0L, action, keyCode, repeat, 0, DEVICE_ID, 0, 0, InputDevice.SOURCE_GAMEPAD,
    )

    private fun device() = BridgeControllerDiagnosticDevice(
        runtimeDeviceId = DEVICE_ID,
        descriptor = "ownership-controller",
        name = "Ownership Controller",
        vendorId = 0x057e,
        productId = 0x2009,
        sources = InputDevice.SOURCE_GAMEPAD or InputDevice.SOURCE_JOYSTICK,
        hasGamepadSource = true,
        hasJoystickSource = true,
        controllerNumber = 1,
        motionRanges = listOf(
            BridgeControllerMotionRange(
                MotionEvent.AXIS_X, "AXIS_X", -1f, 1f, 0.08f, InputDevice.SOURCE_JOYSTICK,
            ),
        ),
    )

    private class FakeRepository(
        var snapshot: ControllerProfileUiRepositorySnapshot = ControllerProfileUiRepositorySnapshot(
            profiles = emptyList(),
            selectedProfileId = null,
            selectedControllerIdentity = null,
        ),
    ) : ControllerProfileUiRepository {
        var loads = 0
        override suspend fun load(): ControllerProfileUiRepositorySnapshot {
            loads++
            return snapshot
        }
        override suspend fun create(profile: ControllerProfileV1) = Unit
        override suspend fun update(profile: ControllerProfileV1) = Unit
        override suspend fun select(profileId: String?) = Unit
        override suspend fun delete(profileId: String) = Unit
        override suspend fun selectController(identity: ControllerIdentityV1?) = Unit
    }

    private class FakeDeviceSource(
        private val device: BridgeControllerDiagnosticDevice,
    ) : BridgeControllerDeviceSource {
        var registerCalls = 0
        var unregisterCalls = 0
        override fun inventory() = listOf(device)
        override fun descriptor(runtimeDeviceId: Int) = device.takeIf { runtimeDeviceId == DEVICE_ID }
        override fun register(listener: InputManager.InputDeviceListener) { registerCalls++ }
        override fun unregister(listener: InputManager.InputDeviceListener) { unregisterCalls++ }
    }

    companion object {
        private const val DEVICE_ID = 41
    }
}
