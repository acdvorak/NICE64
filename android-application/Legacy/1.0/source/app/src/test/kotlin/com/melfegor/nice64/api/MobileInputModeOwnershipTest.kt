package com.melfegor.nice64.api

import android.hardware.input.InputManager
import android.view.InputDevice
import android.view.KeyEvent
import com.melfegor.nice64.core.model.N64ButtonMask
import com.melfegor.nice64.core.model.N64ControllerState
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import org.junit.Test

class MobileInputModeOwnershipTest {
    @Test fun defaultIsOnScreenAndNewRuntimeDoesNotPersistControllerMode() {
        val first = coordinator()
        assertEquals(MobileInputMode.ON_SCREEN, first.presentation.value.inputMode)
        first.setInputMode(MobileInputMode.CONTROLLER)
        assertEquals(MobileInputMode.CONTROLLER, first.presentation.value.inputMode)

        val restarted = coordinator()
        assertEquals(MobileInputMode.ON_SCREEN, restarted.presentation.value.inputMode)
        first.close()
        restarted.close()
    }

    @Test fun onScreenOwnsButtonsAndAnalogWhilePhysicalGameplayIsIgnored() {
        val holder = BridgeLatestControllerStateHolder()
        val subject = coordinator(holder)
        subject.setEnabled(true)
        subject.setTouchButton(N64ButtonMask.A, true)
        subject.setTouchAnalog(25, -30)

        assertEquals(N64ControllerState(N64ButtonMask.A, 25, -30), holder.current())
        assertFalse(subject.handleKeyEvent(button(KeyEvent.KEYCODE_BUTTON_B, true)))
        assertEquals(N64ControllerState(N64ButtonMask.A, 25, -30), holder.current())
        subject.close()
    }

    @Test fun controllerOwnsButtonsWhileTouchGameplayIsIgnored() {
        val holder = BridgeLatestControllerStateHolder()
        val subject = coordinator(holder)
        subject.setEnabled(true)
        subject.setInputMode(MobileInputMode.CONTROLLER)
        subject.setTouchButton(N64ButtonMask.A, true)
        subject.setTouchAnalog(80, 70)
        assertEquals(N64ControllerState.Neutral, holder.current())

        assertTrue(subject.handleKeyEvent(button(KeyEvent.KEYCODE_BUTTON_B, true)))
        assertEquals(N64ButtonMask.B, holder.current().buttons)
        assertTrue(subject.handleKeyEvent(button(KeyEvent.KEYCODE_BUTTON_B, false)))
        assertEquals(N64ControllerState.Neutral, holder.current())
        subject.close()
    }

    @Test fun everyModeSwitchNeutralizesAndClearsHeldSourceState() {
        val holder = BridgeLatestControllerStateHolder()
        val subject = coordinator(holder)
        subject.setEnabled(true)
        subject.setTouchButton(N64ButtonMask.A, true)
        subject.setTouchAnalog(40, 50)

        subject.setInputMode(MobileInputMode.CONTROLLER)
        assertEquals(N64ControllerState.Neutral, holder.current())
        subject.handleKeyEvent(button(KeyEvent.KEYCODE_BUTTON_B, true))
        assertEquals(N64ButtonMask.B, holder.current().buttons)

        subject.setInputMode(MobileInputMode.ON_SCREEN)
        assertEquals(N64ControllerState.Neutral, holder.current())
        subject.setTouchButton(N64ButtonMask.Z, true)
        assertEquals(N64ButtonMask.Z, holder.current().buttons)
        subject.close()
    }

    @Test fun controllerDisconnectNeutralizesWithoutChangingModeAndReconnectStartsNeutral() {
        val holder = BridgeLatestControllerStateHolder()
        val source = FakeDeviceSource(mutableMapOf(7 to device()))
        val subject = BridgeControllerHumanInputCoordinator(holder, source)
        subject.setEnabled(true)
        subject.setInputMode(MobileInputMode.CONTROLLER)
        subject.handleKeyEvent(button(KeyEvent.KEYCODE_BUTTON_A, true))
        assertEquals(N64ButtonMask.A, holder.current().buttons)

        source.devices.remove(7)
        subject.onInputDeviceRemoved(7)
        assertEquals(N64ControllerState.Neutral, holder.current())
        assertEquals(MobileInputMode.CONTROLLER, subject.presentation.value.inputMode)

        source.devices[7] = device()
        subject.onInputDeviceAdded(7)
        assertEquals(N64ControllerState.Neutral, holder.current())
        assertEquals(MobileInputMode.CONTROLLER, subject.presentation.value.inputMode)
        subject.close()
    }

    private fun coordinator(
        holder: BridgeLatestControllerStateHolder = BridgeLatestControllerStateHolder(),
    ) = BridgeControllerHumanInputCoordinator(
        holder,
        FakeDeviceSource(mutableMapOf(7 to device())),
    )

    private fun button(keyCode: Int, down: Boolean) =
        AndroidPhysicalButtonEligibilityPolicy.normalize(
            runtimeDeviceId = 7,
            eventSource = InputDevice.SOURCE_GAMEPAD,
            keyCode = keyCode,
            scanCode = keyCode,
            action = if (down) KeyEvent.ACTION_DOWN else KeyEvent.ACTION_UP,
            repeatCount = 0,
            eventTimeMillis = 1_000L,
            downTimeMillis = 1_000L,
            device = AndroidPhysicalButtonDevice(
                7,
                "controller-descriptor",
                "Test controller",
                InputDevice.SOURCE_GAMEPAD or InputDevice.SOURCE_JOYSTICK,
                false,
            ),
        )

    private fun device() = BridgeControllerDiagnosticDevice(
        runtimeDeviceId = 7,
        descriptor = "controller-descriptor",
        name = "Test controller",
        vendorId = 1,
        productId = 2,
        sources = InputDevice.SOURCE_GAMEPAD or InputDevice.SOURCE_JOYSTICK,
        hasGamepadSource = true,
        hasJoystickSource = true,
        controllerNumber = 1,
        motionRanges = emptyList(),
    )

    private class FakeDeviceSource(
        val devices: MutableMap<Int, BridgeControllerDiagnosticDevice>,
    ) : BridgeControllerDeviceSource {
        override fun inventory() = devices.values.toList()
        override fun descriptor(runtimeDeviceId: Int) = devices[runtimeDeviceId]
        override fun register(listener: InputManager.InputDeviceListener) = Unit
        override fun unregister(listener: InputManager.InputDeviceListener) = Unit
    }
}
