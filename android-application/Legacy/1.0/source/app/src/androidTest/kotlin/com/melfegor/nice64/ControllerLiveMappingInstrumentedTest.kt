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
class ControllerLiveMappingInstrumentedTest {
    @Test fun standardCustomCaptureDeleteDisconnectAndTouchUseOneExistingContribution() = runBlocking {
        val first = device(100, "first")
        val second = device(101, "second", vendor = 9, product = 9)
        val source = FakeDeviceSource(mutableMapOf(100 to first, 101 to second))
        val repository = FakeRepository()
        val cache = LiveControllerProfileCache(repository)
        cache.refresh()
        val holder = BridgeLatestControllerStateHolder()
        val coordinator = BridgeControllerHumanInputCoordinator(holder, source, cache)
        coordinator.setEnabled(true)
        coordinator.setInputMode(MobileInputMode.CONTROLLER)
        assertEquals(1, source.listeners.size)

        assertTrue(coordinator.handleKeyEvent(key(100, KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BUTTON_A)))
        assertEquals(N64ButtonMask.A, holder.current().buttons)
        assertEquals("Standard Android Gamepad", coordinator.presentation.value.gameplayMappingLabel)

        val custom = StandardAndroidGamepadProfile.profile.copy(
            profileId = "live-custom",
            profileName = "Live Custom",
            identity = first.toStableIdentity(),
            buttonBindings = listOf(
                ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_A, N64SemanticTargetV1.B),
            ),
            axisBindings = emptyList(),
            axisAlternatives = emptyList(),
        )
        repository.profiles = mutableListOf(custom)
        repository.selected = custom.profileId
        cache.refresh()
        coordinator.reconcileLiveMapping()
        assertEquals(0u, holder.current().buttons)
        assertEquals("Custom — Live Custom", coordinator.presentation.value.gameplayMappingLabel)

        assertTrue(coordinator.handleKeyEvent(key(100, KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BUTTON_A)))
        assertEquals(N64ButtonMask.B, holder.current().buttons)
        coordinator.setTouchButton(N64ButtonMask.Z, true)
        assertEquals(N64ButtonMask.B, holder.current().buttons)
        coordinator.setTouchButton(N64ButtonMask.Z, false)
        assertEquals(N64ButtonMask.B, holder.current().buttons)
        assertTrue(coordinator.handleKeyEvent(key(100, KeyEvent.ACTION_UP, KeyEvent.KEYCODE_BUTTON_A)))
        assertTrue(holder.current().isNeutral)

        assertTrue(coordinator.handleKeyEvent(key(100, KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BUTTON_A)))
        assertEquals(N64ButtonMask.B, holder.current().buttons)
        coordinator.setPhysicalCaptureSuppressed(true)
        assertTrue(holder.current().isNeutral)
        assertTrue(coordinator.handleKeyEvent(key(100, KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BUTTON_A)))
        assertTrue(holder.current().isNeutral)
        coordinator.setPhysicalCaptureSuppressed(false)
        assertTrue(holder.current().isNeutral)
        assertTrue(coordinator.handleKeyEvent(key(100, KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BUTTON_A)))
        assertEquals(N64ButtonMask.B, holder.current().buttons)

        coordinator.setInputMode(MobileInputMode.ON_SCREEN)
        assertTrue(holder.current().isNeutral)
        coordinator.setTouchButton(N64ButtonMask.Z, true)
        assertEquals(N64ButtonMask.Z, holder.current().buttons)
        assertFalse(coordinator.handleKeyEvent(key(100, KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BUTTON_A)))
        assertEquals(N64ButtonMask.Z, holder.current().buttons)
        assertFalse(coordinator.handleKeyEvent(key(100, KeyEvent.ACTION_UP, KeyEvent.KEYCODE_BUTTON_A)))
        assertEquals(N64ButtonMask.Z, holder.current().buttons)
        coordinator.setTouchButton(N64ButtonMask.Z, false)
        assertTrue(holder.current().isNeutral)
        coordinator.setTouchButton(N64ButtonMask.Z, true)
        assertEquals(N64ButtonMask.Z, holder.current().buttons)
        coordinator.setInputMode(MobileInputMode.CONTROLLER)
        assertTrue(holder.current().isNeutral)
        coordinator.setTouchButton(N64ButtonMask.Z, true)
        assertTrue(holder.current().isNeutral)

        repository.profiles = mutableListOf()
        cache.refresh()
        coordinator.reconcileLiveMapping()
        assertTrue(holder.current().isNeutral)
        assertEquals("No eligible active controller", coordinator.presentation.value.gameplayMappingLabel)

        assertTrue(coordinator.handleKeyEvent(key(101, KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BUTTON_A)))
        assertEquals(N64ButtonMask.A, holder.current().buttons)
        assertEquals("Standard Android Gamepad", coordinator.presentation.value.gameplayMappingLabel)
        assertTrue(coordinator.presentation.value.gameplayMappingReason!!.contains("unavailable"))
        coordinator.onInputDeviceRemoved(101)
        assertTrue(holder.current().isNeutral)

        coordinator.close()
        assertTrue(holder.current().isNeutral)
        assertEquals(0, source.listeners.size)
        assertEquals(1, source.registerCalls)
        assertEquals(1, source.unregisterCalls)
    }

    @Test fun unrelatedAndRepeatedKeysPreserveSelectedControllerRestrictions() = runBlocking {
        val selected = device(100, "selected")
        val source = FakeDeviceSource(mutableMapOf(100 to selected))
        val custom = StandardAndroidGamepadProfile.profile.copy(
            profileId = "buttons",
            profileName = "Buttons",
            identity = selected.toStableIdentity(),
            buttonBindings = listOf(ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_B, N64SemanticTargetV1.A)),
            axisBindings = emptyList(),
            axisAlternatives = emptyList(),
        )
        val repository = FakeRepository(mutableListOf(custom), custom.profileId)
        val cache = LiveControllerProfileCache(repository).also { it.refresh() }
        val holder = BridgeLatestControllerStateHolder()
        val coordinator = BridgeControllerHumanInputCoordinator(holder, source, cache)
        coordinator.setEnabled(true)
        coordinator.setInputMode(MobileInputMode.CONTROLLER)

        assertFalse(coordinator.handleKeyEvent(key(999, KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BUTTON_B)))
        assertFalse(coordinator.handleKeyEvent(key(100, KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_VOLUME_UP)))
        assertTrue(holder.current().isNeutral)
        assertTrue(coordinator.handleKeyEvent(key(100, KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BUTTON_B)))
        assertEquals(N64ButtonMask.A, holder.current().buttons)
        assertTrue(coordinator.handleKeyEvent(key(100, KeyEvent.ACTION_UP, KeyEvent.KEYCODE_BUTTON_B)))
        assertTrue(holder.current().isNeutral)
        coordinator.close()
    }

    private fun key(deviceId: Int, action: Int, keyCode: Int) = KeyEvent(
        0L, 0L, action, keyCode, 0, 0, deviceId, 0, 0, InputDevice.SOURCE_GAMEPAD,
    )

    private fun device(
        id: Int,
        descriptor: String,
        vendor: Int = 0x057e,
        product: Int = 0x2009,
    ) = BridgeControllerDiagnosticDevice(
        runtimeDeviceId = id,
        descriptor = descriptor,
        name = "Test Controller",
        vendorId = vendor,
        productId = product,
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
        var profiles: MutableList<ControllerProfileV1> = mutableListOf(),
        var selected: String? = null,
    ) : ControllerProfileUiRepository {
        override suspend fun load() = ControllerProfileUiRepositorySnapshot(profiles, selected, null)
        override suspend fun create(profile: ControllerProfileV1) { profiles += profile }
        override suspend fun update(profile: ControllerProfileV1) {
            profiles = profiles.filterNot { it.profileId == profile.profileId }.toMutableList().apply { add(profile) }
        }
        override suspend fun select(profileId: String?) { selected = profileId }
        override suspend fun delete(profileId: String) { profiles.removeAll { it.profileId == profileId } }
        override suspend fun selectController(identity: ControllerIdentityV1?) = Unit
    }

    private class FakeDeviceSource(
        private val devices: MutableMap<Int, BridgeControllerDiagnosticDevice>,
    ) : BridgeControllerDeviceSource {
        val listeners = mutableSetOf<InputManager.InputDeviceListener>()
        var registerCalls = 0
        var unregisterCalls = 0
        override fun inventory() = devices.values.toList()
        override fun descriptor(runtimeDeviceId: Int) = devices[runtimeDeviceId]
        override fun register(listener: InputManager.InputDeviceListener) {
            registerCalls++
            listeners += listener
        }
        override fun unregister(listener: InputManager.InputDeviceListener) {
            unregisterCalls++
            listeners -= listener
        }
    }
}
