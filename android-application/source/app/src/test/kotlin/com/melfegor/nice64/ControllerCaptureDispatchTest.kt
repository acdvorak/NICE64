package com.melfegor.nice64

import android.view.KeyEvent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ControllerCaptureDispatchTest {
    @Test fun consumedCaptureKeyStopsAtActivityPredispatch() {
        val event = KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_DPAD_UP)
        var captureCalled = false

        val consumed = preDispatchControllerCaptureKeyEvent(event) {
            captureCalled = true
            true
        }

        assertTrue(captureCalled)
        assertTrue(consumed)
    }

    @Test fun noncaptureKeyRemainsAvailableToNormalActivityAndComposeDispatch() {
        val event = KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_DPAD_DOWN)
        assertFalse(preDispatchControllerCaptureKeyEvent(event) { false })
    }

    @Test fun captureGameplayAndUiRunInStrictPrecedence() {
        val calls = mutableListOf<String>()
        assertTrue(dispatchControllerInputWithPrecedence(
            event = "key",
            capture = { calls += "capture"; true },
            gameplay = { calls += "gameplay"; true },
            ui = { calls += "ui"; true },
        ))
        assertEquals(listOf("capture"), calls)

        calls.clear()
        assertTrue(dispatchControllerInputWithPrecedence(
            event = "key",
            capture = { calls += "capture"; false },
            gameplay = { calls += "gameplay"; true },
            ui = { calls += "ui"; true },
        ))
        assertEquals(listOf("capture", "gameplay"), calls)

        calls.clear()
        assertTrue(dispatchControllerInputWithPrecedence(
            event = "key",
            capture = { calls += "capture"; false },
            gameplay = { calls += "gameplay"; false },
            ui = { calls += "ui"; true },
        ))
        assertEquals(listOf("capture", "gameplay", "ui"), calls)
    }

    @Test fun gameplayOwnedKeyOrMotionIsAppliedExactlyOnceBeforeUi() {
        listOf("key", "motion").forEach { event ->
            var gameplayCalls = 0
            var uiCalls = 0
            assertTrue(dispatchControllerInputWithPrecedence(
                event,
                capture = { false },
                gameplay = { gameplayCalls++; true },
                ui = { uiCalls++; true },
            ))
            assertEquals(1, gameplayCalls)
            assertEquals(0, uiCalls)
        }
    }

    @Test fun allDpadDirectionsUseTheSameGameplayFirstPolicy() {
        listOf(
            KeyEvent.KEYCODE_DPAD_UP,
            KeyEvent.KEYCODE_DPAD_DOWN,
            KeyEvent.KEYCODE_DPAD_LEFT,
            KeyEvent.KEYCODE_DPAD_RIGHT,
        ).forEach { keyCode ->
            val event = KeyEvent(KeyEvent.ACTION_DOWN, keyCode)
            var uiReached = false
            assertTrue(dispatchControllerInputWithPrecedence(
                event,
                capture = { false },
                gameplay = { true },
                ui = { uiReached = true; true },
            ))
            assertFalse(uiReached)
        }
    }

    @Test fun inactiveGameplayLeavesEveryDpadDirectionForUi() {
        listOf(
            KeyEvent.KEYCODE_DPAD_UP,
            KeyEvent.KEYCODE_DPAD_DOWN,
            KeyEvent.KEYCODE_DPAD_LEFT,
            KeyEvent.KEYCODE_DPAD_RIGHT,
        ).forEach { keyCode ->
            var uiReached = false
            assertTrue(dispatchControllerInputWithPrecedence(
                KeyEvent(KeyEvent.ACTION_DOWN, keyCode),
                capture = { false },
                gameplay = { false },
                ui = { uiReached = true; true },
            ))
            assertTrue(uiReached)
        }
    }
}
