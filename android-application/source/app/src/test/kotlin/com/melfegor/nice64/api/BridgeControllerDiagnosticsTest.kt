package com.melfegor.nice64.api

import android.hardware.input.InputManager
import android.view.InputDevice
import android.view.MotionEvent
import com.melfegor.nice64.core.model.N64ButtonMask
import com.melfegor.nice64.core.model.N64ControllerState
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue
import org.junit.Test

class BridgeControllerDiagnosticsTest {
    @Test
    fun gamepadJoystickAndCombinedSourcesAreEligibleButKeyboardMouseAreNot() {
        assertTrue(BridgeControllerDeviceProjection.isEligibleSource(InputDevice.SOURCE_GAMEPAD))
        assertTrue(BridgeControllerDeviceProjection.isEligibleSource(InputDevice.SOURCE_JOYSTICK))
        assertTrue(BridgeControllerDeviceProjection.isEligibleSource(
            InputDevice.SOURCE_GAMEPAD or InputDevice.SOURCE_JOYSTICK,
        ))
        assertFalse(BridgeControllerDeviceProjection.isEligibleSource(InputDevice.SOURCE_KEYBOARD))
        assertFalse(BridgeControllerDeviceProjection.isEligibleSource(InputDevice.SOURCE_MOUSE))
    }

    @Test
    fun descriptorMetadataAndRangesRemainReadOnlyPresentationData() {
        val range = BridgeControllerMotionRange(
            MotionEvent.AXIS_X, "AXIS_X", -1f, 1f, 0.125f,
            InputDevice.SOURCE_JOYSTICK,
        )
        val device = device(41, "persistent-descriptor-abcdef", ranges = listOf(range))
        val store = BridgeControllerDiagnosticsStore(listOf(device))

        val presented = store.state.value.devices.single()
        assertEquals(41, presented.runtimeDeviceId)
        assertEquals("persistent-descriptor-abcdef", presented.descriptor)
        assertEquals("escriptor-abcdef", presented.descriptorSuffix)
        assertEquals(1118, presented.vendorId)
        assertEquals(654, presented.productId)
        assertTrue(presented.hasGamepadSource)
        assertTrue(presented.hasJoystickSource)
        assertEquals(range, presented.motionRanges.single())
    }

    @Test
    fun addChangeAndRemoveUpdateInventoryAndRefreshRanges() {
        val store = BridgeControllerDiagnosticsStore()
        val original = device(7, "descriptor", ranges = listOf(axis(MotionEvent.AXIS_X)))
        store.upsert(original)
        assertEquals(listOf(7), store.state.value.devices.map { it.runtimeDeviceId })

        val changed = original.copy(
            name = "Changed controller",
            motionRanges = listOf(axis(MotionEvent.AXIS_X), axis(MotionEvent.AXIS_RZ)),
        )
        store.upsert(changed)
        assertEquals("Changed controller", store.state.value.devices.single().name)
        assertEquals(2, store.state.value.devices.single().motionRanges.size)

        store.setActive(7)
        store.observeAxes(7, listOf(axisValue(7, MotionEvent.AXIS_X, 0.5f)))
        store.remove(7)
        assertTrue(store.state.value.devices.isEmpty())
        assertNull(store.state.value.activeRuntimeDeviceId)
        assertTrue(store.state.value.currentAxes.isEmpty())
    }

    @Test
    fun buttonDiagnosticsAreNamedBoundedAndNewestFirst() {
        val store = BridgeControllerDiagnosticsStore(listOf(device(9)))
        repeat(20) { keyCode ->
            store.observeButton(BridgeControllerButtonDiagnostic(
                runtimeDeviceId = 9,
                keyCode = keyCode,
                keyName = "KEY_$keyCode",
                action = if (keyCode % 2 == 0) "DOWN" else "UP",
                source = InputDevice.SOURCE_GAMEPAD,
            ))
        }
        assertEquals(12, store.state.value.recentButtons.size)
        assertEquals(19, store.state.value.recentButtons.first().keyCode)
        assertEquals("UP", store.state.value.recentButtons.first().action)
    }

    @Test
    fun axisDiagnosticsCarryRawNormalizedRangeFlatAndSource() {
        val value = axisValue(2, MotionEvent.AXIS_Y, -0.25f)
        val store = BridgeControllerDiagnosticsStore(listOf(device(2)))
        store.observeAxes(2, listOf(value))

        assertEquals(value, store.state.value.currentAxes.single())
        assertEquals(0f, BridgeControllerDeviceProjection.normalize(0f, -1f, 1f))
        assertEquals(1f, BridgeControllerDeviceProjection.normalize(10f, -1f, 1f))
        assertEquals(0f, BridgeControllerDeviceProjection.normalize(1f, 1f, 1f))
    }

    @Test
    fun diagnosticsCallbacksNeverAlterSemanticControllerState() {
        val semantic = N64ControllerState(N64ButtonMask.A, 12, -34, true)
        val holder = BridgeLatestControllerStateHolder(semantic)
        val source = FakeDeviceSource(mutableMapOf(5 to device(5)))
        val coordinator = BridgeControllerHumanInputCoordinator(holder, source)

        coordinator.onInputDeviceAdded(5)
        coordinator.onInputDeviceChanged(5)
        coordinator.onInputDeviceRemoved(5)

        assertEquals(semantic, holder.current())
        coordinator.close()
    }

    @Test
    fun oneCoordinatorOwnsOneListenerAndCloseIsIdempotent() {
        val source = FakeDeviceSource()
        val retained = BridgeControllerHumanInputCoordinator(
            BridgeLatestControllerStateHolder(), source,
        )
        assertEquals(1, source.activeListeners.size)

        // ViewModel-scoped ownership means Activity recreation reuses this retained coordinator;
        // it does not construct/register another coordinator.
        assertEquals(1, source.registerCalls)
        retained.close()
        retained.close()
        assertEquals(0, source.activeListeners.size)
        assertEquals(1, source.unregisterCalls)
    }

    private fun device(
        id: Int,
        descriptor: String = "descriptor-$id",
        ranges: List<BridgeControllerMotionRange> = emptyList(),
    ) = BridgeControllerDiagnosticDevice(
        runtimeDeviceId = id,
        descriptor = descriptor,
        name = "Test controller $id",
        vendorId = 1118,
        productId = 654,
        sources = InputDevice.SOURCE_GAMEPAD or InputDevice.SOURCE_JOYSTICK,
        hasGamepadSource = true,
        hasJoystickSource = true,
        controllerNumber = 1,
        motionRanges = ranges,
    )

    private fun axis(code: Int) = BridgeControllerMotionRange(
        code, "AXIS_$code", -1f, 1f, 0.1f,
        InputDevice.SOURCE_JOYSTICK,
    )

    private fun axisValue(deviceId: Int, code: Int, raw: Float) =
        BridgeControllerAxisDiagnostic(
            deviceId, code, "AXIS_$code", raw, raw,
            -1f, 1f, 0.1f, InputDevice.SOURCE_JOYSTICK,
        )

    private class FakeDeviceSource(
        private val devices: MutableMap<Int, BridgeControllerDiagnosticDevice> = mutableMapOf(),
    ) : BridgeControllerDeviceSource {
        val activeListeners = mutableSetOf<InputManager.InputDeviceListener>()
        var registerCalls = 0
        var unregisterCalls = 0

        override fun inventory() = devices.values.toList()
        override fun descriptor(runtimeDeviceId: Int) = devices[runtimeDeviceId]

        override fun register(listener: InputManager.InputDeviceListener) {
            registerCalls++
            activeListeners += listener
        }

        override fun unregister(listener: InputManager.InputDeviceListener) {
            unregisterCalls++
            activeListeners -= listener
        }
    }
}
