package com.melfegor.nice64.api

import android.view.InputDevice
import android.view.KeyEvent
import com.melfegor.nice64.dispatchNormalizedControllerButtonWithPrecedence
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertSame
import kotlin.test.assertTrue
import org.junit.Test

class AndroidPhysicalButtonInputTest {
    @Test fun normalGamepadAndJoystickSourcesRemainDirectlyEligible() {
        listOf(InputDevice.SOURCE_GAMEPAD, InputDevice.SOURCE_JOYSTICK).forEach { source ->
            val result = normalized(source = source, device = null)
            assertTrue(result.eligible)
            assertEquals(AndroidPhysicalButtonEligibility.DIRECT_CONTROLLER_SOURCE, result.eligibility)
        }
    }

    @Test fun mixedControllerAllowsNumberedButtonsOneAndNine() {
        listOf(KeyEvent.KEYCODE_BUTTON_1, KeyEvent.KEYCODE_BUTTON_9).forEach { keyCode ->
            val result = normalized(keyCode = keyCode, device = mixedDevice())
            assertTrue(result.eligible)
            assertEquals(AndroidPhysicalButtonEligibility.MIXED_CONTROLLER_FALLBACK, result.eligibility)
        }
    }

    @Test fun fallbackRejectsPureKeyboardVirtualAndWrongDevice() {
        assertFalse(normalized(device = mixedDevice(sources = InputDevice.SOURCE_KEYBOARD)).eligible)
        assertFalse(normalized(device = mixedDevice(virtual = true)).eligible)
        assertFalse(normalized(deviceId = 7, device = mixedDevice(id = 8)).eligible)
    }

    @Test fun fallbackRejectsArbitraryKeyboardAndNavigationKeys() {
        listOf(
            KeyEvent.KEYCODE_A,
            KeyEvent.KEYCODE_SPACE,
            KeyEvent.KEYCODE_ENTER,
            KeyEvent.KEYCODE_TAB,
            KeyEvent.KEYCODE_BACK,
            KeyEvent.KEYCODE_ESCAPE,
            KeyEvent.KEYCODE_HOME,
            KeyEvent.KEYCODE_MENU,
            KeyEvent.KEYCODE_DPAD_CENTER,
        ).forEach { keyCode ->
            assertFalse(normalized(keyCode = keyCode, device = mixedDevice()).eligible, "key=$keyCode")
        }
    }

    @Test fun observationPreservesAllRuntimeMetadata() {
        val result = AndroidPhysicalButtonEligibilityPolicy.normalize(
            runtimeDeviceId = 7,
            eventSource = InputDevice.SOURCE_KEYBOARD,
            keyCode = KeyEvent.KEYCODE_BUTTON_9,
            scanCode = 296,
            action = KeyEvent.ACTION_UP,
            repeatCount = 3,
            eventTimeMillis = 1_234L,
            downTimeMillis = 1_111L,
            device = mixedDevice(),
        )
        assertEquals(7, result.runtimeDeviceId)
        assertEquals("stable", result.stableDescriptor)
        assertEquals("Mixed controller", result.deviceName)
        assertEquals(InputDevice.SOURCE_JOYSTICK or InputDevice.SOURCE_KEYBOARD, result.aggregateDeviceSources)
        assertEquals(InputDevice.SOURCE_KEYBOARD, result.eventSource)
        assertEquals(KeyEvent.KEYCODE_BUTTON_9, result.keyCode)
        assertEquals(296, result.scanCode)
        assertTrue(result.released)
        assertFalse(result.pressed)
        assertEquals(3, result.repeatCount)
        assertEquals(1_234L, result.eventTimeMillis)
        assertEquals(1_111L, result.downTimeMillis)
    }

    @Test fun preferredDownAndUpPassImmediatelyAndTrailingAliasesAreSuppressed() {
        val pipeline = AndroidPhysicalButtonPipeline()
        assertIs<AndroidPhysicalButtonPipelineResult.Ready>(
            pipeline.normalizeButton(KeyEvent.KEYCODE_BUTTON_1, 288, KeyEvent.ACTION_DOWN, 1_000),
        )
        assertIs<AndroidPhysicalButtonPipelineResult.SuppressedAlias>(
            pipeline.normalizeButton(KeyEvent.KEYCODE_DPAD_CENTER, 288, KeyEvent.ACTION_DOWN, 1_036),
        )
        assertIs<AndroidPhysicalButtonPipelineResult.Ready>(
            pipeline.normalizeButton(KeyEvent.KEYCODE_BUTTON_1, 288, KeyEvent.ACTION_UP, 1_100),
        )
        assertIs<AndroidPhysicalButtonPipelineResult.SuppressedAlias>(
            pipeline.normalizeButton(KeyEvent.KEYCODE_DPAD_CENTER, 288, KeyEvent.ACTION_UP, 1_105),
        )
    }

    @Test fun staleDifferentScanDeviceAndActionDoNotSuppress() {
        val pipeline = AndroidPhysicalButtonPipeline()
        pipeline.normalizeButton(KeyEvent.KEYCODE_BUTTON_1, 288, KeyEvent.ACTION_DOWN, 1_000)
        assertIs<AndroidPhysicalButtonPipelineResult.Ready>(
            pipeline.normalizeButton(KeyEvent.KEYCODE_DPAD_CENTER, 288, KeyEvent.ACTION_DOWN, 1_051),
        )

        pipeline.normalizeButton(KeyEvent.KEYCODE_BUTTON_1, 288, KeyEvent.ACTION_DOWN, 2_000)
        assertIs<AndroidPhysicalButtonPipelineResult.Ready>(
            pipeline.normalizeButton(KeyEvent.KEYCODE_DPAD_CENTER, 289, KeyEvent.ACTION_DOWN, 2_010),
        )
        assertIs<AndroidPhysicalButtonPipelineResult.Ready>(
            pipeline.normalizeButton(KeyEvent.KEYCODE_DPAD_CENTER, 288, KeyEvent.ACTION_DOWN, 2_010, deviceId = 8),
        )
        assertIs<AndroidPhysicalButtonPipelineResult.Ready>(
            pipeline.normalizeButton(KeyEvent.KEYCODE_DPAD_CENTER, 288, KeyEvent.ACTION_UP, 2_010),
        )
    }

    @Test fun genuineCenterWithoutPreferredEdgeIsNotSuppressed() {
        assertIs<AndroidPhysicalButtonPipelineResult.Ready>(
            AndroidPhysicalButtonPipeline().normalizeButton(
                KeyEvent.KEYCODE_DPAD_CENTER, 288, KeyEvent.ACTION_DOWN, 1_000,
                source = InputDevice.SOURCE_GAMEPAD,
            ),
        )
    }

    @Test fun zeroScanCodeNeverCreatesAmbiguousAliasHistory() {
        val pipeline = AndroidPhysicalButtonPipeline()
        assertIs<AndroidPhysicalButtonPipelineResult.Ready>(
            pipeline.normalizeButton(KeyEvent.KEYCODE_BUTTON_1, 0, KeyEvent.ACTION_DOWN, 1_000),
        )
        assertIs<AndroidPhysicalButtonPipelineResult.Ready>(
            pipeline.normalizeButton(KeyEvent.KEYCODE_DPAD_CENTER, 0, KeyEvent.ACTION_DOWN, 1_010),
        )
    }

    @Test fun deviceAndLifecycleClearsRemoveAliasHistory() {
        val pipeline = AndroidPhysicalButtonPipeline()
        pipeline.normalizeButton(KeyEvent.KEYCODE_BUTTON_1, 288, KeyEvent.ACTION_DOWN, 1_000)
        pipeline.clearDevice(7)
        assertIs<AndroidPhysicalButtonPipelineResult.Ready>(
            pipeline.normalizeButton(KeyEvent.KEYCODE_DPAD_CENTER, 288, KeyEvent.ACTION_DOWN, 1_010),
        )

        pipeline.normalizeButton(KeyEvent.KEYCODE_BUTTON_1, 288, KeyEvent.ACTION_DOWN, 2_000)
        pipeline.clear()
        assertIs<AndroidPhysicalButtonPipelineResult.Ready>(
            pipeline.normalizeButton(KeyEvent.KEYCODE_DPAD_CENTER, 288, KeyEvent.ACTION_DOWN, 2_010),
        )
    }

    @Test fun captureRetainsPriorityAndBothConsumersReceiveSameObservation() {
        val observation = normalized(device = mixedDevice())
        var captured: AndroidPhysicalButtonObservation? = null
        var gameplayCalls = 0
        assertTrue(dispatchNormalizedControllerButtonWithPrecedence(
            observation,
            capture = { captured = it; true },
            gameplay = { gameplayCalls++; true },
        ))
        assertSame(observation, captured)
        assertEquals(0, gameplayCalls)

        var gameplay: AndroidPhysicalButtonObservation? = null
        assertTrue(dispatchNormalizedControllerButtonWithPrecedence(
            observation,
            capture = { false },
            gameplay = { gameplay = it; true },
        ))
        assertSame(observation, gameplay)
    }

    private fun AndroidPhysicalButtonPipeline.normalizeButton(
        keyCode: Int,
        scanCode: Int,
        action: Int,
        time: Long,
        deviceId: Int = 7,
        source: Int = InputDevice.SOURCE_KEYBOARD,
    ) = normalize(
        runtimeDeviceId = deviceId,
        eventSource = source,
        keyCode = keyCode,
        scanCode = scanCode,
        action = action,
        repeatCount = 0,
        eventTimeMillis = time,
        downTimeMillis = time,
        device = mixedDevice(id = deviceId),
    )

    private fun normalized(
        deviceId: Int = 7,
        source: Int = InputDevice.SOURCE_KEYBOARD,
        keyCode: Int = KeyEvent.KEYCODE_BUTTON_1,
        device: AndroidPhysicalButtonDevice? = mixedDevice(),
    ) = AndroidPhysicalButtonEligibilityPolicy.normalize(
        runtimeDeviceId = deviceId,
        eventSource = source,
        keyCode = keyCode,
        scanCode = 288,
        action = KeyEvent.ACTION_DOWN,
        repeatCount = 0,
        eventTimeMillis = 100L,
        downTimeMillis = 100L,
        device = device,
    )

    private fun mixedDevice(
        id: Int = 7,
        sources: Int = InputDevice.SOURCE_JOYSTICK or InputDevice.SOURCE_KEYBOARD,
        virtual: Boolean = false,
    ) = AndroidPhysicalButtonDevice(
        runtimeDeviceId = id,
        stableDescriptor = "stable",
        deviceName = "Mixed controller",
        aggregateSources = sources,
        isVirtual = virtual,
    )
}
