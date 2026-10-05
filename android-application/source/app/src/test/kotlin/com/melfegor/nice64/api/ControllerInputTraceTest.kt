package com.melfegor.nice64.api

import android.hardware.input.InputManager
import android.view.InputDevice
import android.view.KeyEvent
import com.melfegor.nice64.core.model.N64ButtonMask
import com.melfegor.nice64.core.model.N64ControllerState
import java.io.File
import java.nio.file.Files
import java.util.concurrent.CountDownLatch
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertTrue
import org.junit.Test

class ControllerInputTraceTest {
    @Test fun androidEventTimestampUsesOnlyTheUptimeMillisecondBoundary() {
        val boundary = AndroidInputDispatchBoundary(
            eventTimeMs = 995,
            t0UptimeMs = 1_000,
            t0ElapsedNanos = 9_000_000_000,
        )

        assertEquals(5, boundary.androidEventToDispatchMs)
        assertEquals(
            5,
            (boundary.t0UptimeMs - boundary.eventTimeMs).coerceAtLeast(0),
        )
        assertNotEquals(
            boundary.t0ElapsedNanos - boundary.eventTimeMs,
            boundary.androidEventToDispatchMs,
        )
    }

    @Test fun reachedBoundariesAndRawIdentityArePreservedInJsonl() {
        val directory = Files.createTempDirectory("nice64-trace-record").toFile()
        val subject = ControllerInputTraceCoordinator(directory)
        try {
            val session = subject.start()
            subject.submit(record(
                sessionId = requireNotNull(session.sessionId),
                number = 1,
                keyCode = 96,
                target = "N64_A",
                t0 = 2_000_000,
            ))
            val stopped = subject.stop()
            val text = File(directory, requireNotNull(stopped.fileName)).readText()

            assertTrue(text.contains("\"runtimeDeviceId\":42"))
            assertTrue(text.contains("\"stableDeviceDescriptor\":\"controller-descriptor\""))
            assertTrue(text.contains("\"keyCode\":96"))
            assertTrue(text.contains("\"resolvedN64SemanticTarget\":\"N64_A\""))
            assertTrue(text.contains("\"eventTimeMs\":995"))
            assertTrue(text.contains("\"t0UptimeMs\":1000"))
            assertTrue(text.contains("\"t1ElapsedNanos\":2100000"))
            assertTrue(text.contains("\"t5ElapsedNanos\":2500000"))
        } finally {
            subject.close()
            directory.deleteRecursively()
        }
    }

    @Test fun boundedQueueDropsRatherThanBlockingSubmitter() {
        val directory = Files.createTempDirectory("nice64-trace-overflow").toFile()
        val gate = CountDownLatch(1)
        val subject = ControllerInputTraceCoordinator(
            directory = directory,
            queueCapacity = 1,
            recordWriteGateForTests = gate,
        )
        try {
            val session = subject.start()
            val id = requireNotNull(session.sessionId)
            val started = System.nanoTime()
            repeat(100) { subject.submit(record(id, it.toLong() + 1)) }
            val elapsedMs = (System.nanoTime() - started) / 1_000_000

            assertTrue(subject.state.value.dropped > 0)
            assertTrue(elapsedMs < 1_000, "submit must not wait for the writer")
            gate.countDown()
            subject.stop()
        } finally {
            gate.countDown()
            subject.close()
            directory.deleteRecursively()
        }
    }

    @Test fun stopFlushesAndNextStartCreatesAnIndependentSession() {
        val directory = Files.createTempDirectory("nice64-trace-session").toFile()
        val subject = ControllerInputTraceCoordinator(directory)
        try {
            val first = subject.start()
            subject.submit(record(requireNotNull(first.sessionId), 1))
            val firstStopped = subject.stop()
            val firstTrace = File(directory, requireNotNull(firstStopped.fileName))
            val firstSummary = File(directory, requireNotNull(firstStopped.summaryFileName))
            assertFalse(firstStopped.running)
            assertTrue(firstTrace.readLines().size == 1)
            assertTrue(firstSummary.readText().contains("records=1"))

            val second = subject.start()
            assertNotEquals(first.sessionId, second.sessionId)
            assertNotEquals(first.fileName, second.fileName)
            assertEquals(0, second.records)
            assertEquals(0, second.dropped)
            val secondStopped = subject.stop()
            assertTrue(File(directory, requireNotNull(secondStopped.fileName)).readText().isEmpty())
            assertTrue(File(directory, requireNotNull(secondStopped.summaryFileName))
                .readText().contains("records=0"))
        } finally {
            subject.close()
            directory.deleteRecursively()
        }
    }

    @Test fun inactiveTraceDoesNotAcceptOrMutateRecords() {
        val directory = Files.createTempDirectory("nice64-trace-disabled").toFile()
        val subject = ControllerInputTraceCoordinator(directory)
        try {
            val initial = subject.state.value
            subject.submit(record("not-active", 1))
            assertEquals(initial, subject.state.value)
            assertTrue(directory.listFiles().isNullOrEmpty())
        } finally {
            subject.close()
            directory.deleteRecursively()
        }
    }

    @Test fun collectionHelperUsesOnlyReadOnlyPhoneOperations() {
        val scriptFile = listOf(
            File("tools/collect-touch-trigger-diagnostics.ps1"),
            File("../tools/collect-touch-trigger-diagnostics.ps1"),
        ).first { it.isFile }
        val script = scriptFile.readText()
        assertTrue(script.contains("shell run-as"))
        assertTrue(script.contains("exec-out"))
        assertTrue(script.contains("'cat'"))
        assertFalse(script.contains("pm clear", ignoreCase = true))
        assertFalse(script.contains("uninstall", ignoreCase = true))
        assertFalse(script.contains(" install ", ignoreCase = true))
        assertFalse(script.contains("shell rm", ignoreCase = true))
        assertFalse(script.contains("shell del", ignoreCase = true))
    }

    @Test fun modeChangeAndDisconnectNeutralizeGameplayWithoutCorruptingTraceSession() {
        val directory = Files.createTempDirectory("nice64-trace-lifecycle").toFile()
        val trace = ControllerInputTraceCoordinator(directory)
        val holder = BridgeLatestControllerStateHolder()
        val devices = mutableMapOf(7 to testDevice())
        val input = BridgeControllerHumanInputCoordinator(holder, object : BridgeControllerDeviceSource {
            override fun inventory() = devices.values.toList()
            override fun descriptor(runtimeDeviceId: Int) = devices[runtimeDeviceId]
            override fun register(listener: InputManager.InputDeviceListener) = Unit
            override fun unregister(listener: InputManager.InputDeviceListener) = Unit
        })
        try {
            val session = trace.start()
            trace.submit(record(requireNotNull(session.sessionId), 1))
            input.setEnabled(true)
            input.setInputMode(MobileInputMode.CONTROLLER)
            input.handleKeyEvent(testButton(down = true))
            assertEquals(N64ButtonMask.A, holder.current().buttons)

            input.setInputMode(MobileInputMode.ON_SCREEN)
            assertEquals(N64ControllerState.Neutral, holder.current())
            assertTrue(trace.state.value.running)
            assertEquals(1, trace.state.value.records)

            input.setInputMode(MobileInputMode.CONTROLLER)
            input.handleKeyEvent(testButton(down = true))
            devices.remove(7)
            input.onInputDeviceRemoved(7)
            assertEquals(N64ControllerState.Neutral, holder.current())
            assertTrue(trace.state.value.running)
            assertEquals(1, trace.state.value.records)
        } finally {
            input.close()
            trace.close()
            directory.deleteRecursively()
        }
    }

    private fun record(
        sessionId: String,
        number: Long,
        keyCode: Int = 96,
        target: String = "N64_A",
        t0: Long = 2_000_000,
    ) = ControllerInputTraceRecord(
        diagnosticSessionId = sessionId,
        recordNumber = number,
        runtimeDeviceId = 42,
        stableDeviceDescriptor = "controller-descriptor",
        deviceName = "Test Controller",
        aggregateDeviceSources = 0x501,
        eventSource = 0x401,
        keyCode = keyCode,
        keyCodeName = "KEYCODE_BUTTON_A",
        scanCode = 304,
        action = "DOWN",
        repeatCount = 0,
        downTimeMs = 990,
        eventTimeMs = 995,
        t0UptimeMs = 1_000,
        t0ElapsedNanos = t0,
        t1ElapsedNanos = t0 + 100_000,
        t2ElapsedNanos = t0 + 200_000,
        resolvedN64SemanticTarget = target,
        t3ElapsedNanos = t0 + 300_000,
        t4ElapsedNanos = t0 + 400_000,
        t5ElapsedNanos = t0 + 500_000,
        mixedSourceFallback = true,
        mobileInputMode = MobileInputMode.CONTROLLER.name,
        activePrimary = "nice-mobile",
        profileIdentityOrType = "standard",
        resultingButtons = 0x8000,
        resultingAnalogX = 0,
        resultingAnalogY = 0,
        androidEventToDispatchMs = 5,
        dispatchToNormalizedMs = 0.1,
        dispatchToMappedMs = 0.2,
        dispatchToSemanticHandlerMs = 0.3,
        dispatchToHolderMs = 0.4,
        dispatchToUiModelMs = 0.5,
        semanticHandlerToHolderMs = 0.1,
    )

    private fun testButton(down: Boolean) =
        AndroidPhysicalButtonEligibilityPolicy.normalize(
            runtimeDeviceId = 7,
            eventSource = InputDevice.SOURCE_GAMEPAD,
            keyCode = KeyEvent.KEYCODE_BUTTON_A,
            scanCode = KeyEvent.KEYCODE_BUTTON_A,
            action = if (down) KeyEvent.ACTION_DOWN else KeyEvent.ACTION_UP,
            repeatCount = 0,
            eventTimeMillis = 1_000,
            downTimeMillis = 1_000,
            device = AndroidPhysicalButtonDevice(
                runtimeDeviceId = 7,
                stableDescriptor = "controller-descriptor",
                deviceName = "Test controller",
                aggregateSources = InputDevice.SOURCE_GAMEPAD or InputDevice.SOURCE_JOYSTICK,
                isVirtual = false,
            ),
        )

    private fun testDevice() = BridgeControllerDiagnosticDevice(
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
}
