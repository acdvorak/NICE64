package com.melfegor.nice64.core

import com.melfegor.nice64.core.input.N64AnalogScaling
import com.melfegor.nice64.core.input.N64InputComposer
import com.melfegor.nice64.core.input.N64InputSource
import com.melfegor.nice64.core.model.N64ButtonMask
import com.melfegor.nice64.core.model.N64ControllerState
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class N64InputComposerTest {
    @Test fun disabledAndEnabledNeutralAreExact() {
        val subject = N64InputComposer()
        assertEquals(N64ControllerState.Neutral, subject.current())
        assertEquals(N64ControllerState.Neutral, subject.setEnabled(true))
    }

    @Test fun touchButtonsSupportPressReleaseAndCombinations() {
        val subject = N64InputComposer().apply { setEnabled(true) }
        assertEquals(N64ButtonMask.A, subject.setTouchButtons(N64ButtonMask.A).buttons)
        assertEquals(N64ButtonMask.A or N64ButtonMask.B,
            subject.setTouchButtons(N64ButtonMask.A or N64ButtonMask.B).buttons)
        assertEquals(0u, subject.setTouchButtons(0u).buttons)
    }

    @Test fun touchDpadUsesFrozenBits() {
        val subject = N64InputComposer().apply { setEnabled(true) }
        val mask = N64ButtonMask.DPAD_UP or N64ButtonMask.DPAD_LEFT
        assertEquals(mask, subject.setTouchButtons(mask).buttons)
    }

    @Test fun touchAnalogCentersClampsAndClears() {
        val subject = N64InputComposer().apply { setEnabled(true) }
        assertEquals(N64ControllerState.Neutral, subject.setTouchAnalog(0, 0))
        assertEquals(-128, subject.setTouchAnalog(-999, 999).analogX)
        assertEquals(127, subject.current().analogY)
        assertEquals(N64ControllerState.Neutral, subject.clearTouch())
    }

    @Test fun inactiveGamepadCannotCombineWithTouchButtons() {
        val subject = N64InputComposer().apply { setEnabled(true) }
        subject.setTouchButtons(N64ButtonMask.A or N64ButtonMask.L)
        val state = subject.setGamepad(N64ButtonMask.B or N64ButtonMask.R, 0, 0)
        assertEquals(N64ButtonMask.A or N64ButtonMask.L, state.buttons)
    }

    @Test fun sourceSwitchNeutralizesAndOnlyNewSourceCanPublish() {
        val subject = N64InputComposer().apply { setEnabled(true) }
        subject.setTouchAnalog(30, 40)
        assertEquals(N64ControllerState.Neutral, subject.setActiveSource(N64InputSource.GAMEPAD))
        assertEquals(N64ControllerState.Neutral, subject.setTouchAnalog(90, 80))
        assertEquals(N64ControllerState(0u, -50, 60), subject.setGamepad(0u, -50, 60))
    }

    @Test fun inactiveSourceClearCannotChangeActiveState() {
        val subject = N64InputComposer().apply { setEnabled(true) }
        subject.setTouchButtons(N64ButtonMask.A)
        assertEquals(N64ButtonMask.A, subject.clearGamepad().buttons)
        assertEquals(N64ControllerState.Neutral, subject.clearTouch())
    }

    @Test fun controllerModeIgnoresTouchAndClearsWithoutFallback() {
        val subject = N64InputComposer().apply { setEnabled(true) }
        subject.setActiveSource(N64InputSource.GAMEPAD)
        subject.setGamepad(N64ButtonMask.B or N64ButtonMask.START, -72, 64)

        assertEquals(
            N64ControllerState(N64ButtonMask.B or N64ButtonMask.START, -72, 64),
            subject.setTouchButtons(N64ButtonMask.A or N64ButtonMask.Z),
        )
        assertEquals(
            N64ControllerState(N64ButtonMask.B or N64ButtonMask.START, -72, 64),
            subject.setTouchAnalog(25, -30),
        )
        assertEquals(N64ControllerState.Neutral, subject.clearGamepad())
        assertTrue(subject.touchContribution().isNeutral)
        assertTrue(subject.gamepadContribution().isNeutral)
    }

    @Test fun heldButtonsAndAnalogNeverCrossModeSwitches() {
        val subject = N64InputComposer().apply { setEnabled(true) }
        subject.setTouchButtons(N64ButtonMask.A or N64ButtonMask.Z)
        subject.setTouchAnalog(25, -30)
        assertEquals(N64ControllerState.Neutral,
            subject.setActiveSource(N64InputSource.GAMEPAD))
        subject.setGamepad(N64ButtonMask.B, -72, 64)
        assertEquals(N64ControllerState.Neutral,
            subject.setActiveSource(N64InputSource.TOUCH))
        assertEquals(N64ControllerState.Neutral, subject.current())
    }

    @Test fun defaultSourceIsTouchAndIsProcessLocalToEachComposer() {
        val first = N64InputComposer().apply {
            setEnabled(true)
            setActiveSource(N64InputSource.GAMEPAD)
        }
        val restarted = N64InputComposer().apply { setEnabled(true) }

        assertEquals(N64InputSource.GAMEPAD, first.source())
        assertEquals(N64InputSource.TOUCH, restarted.source())
        assertEquals(N64ButtonMask.A, restarted.setTouchButtons(N64ButtonMask.A).buttons)
    }

    @Test fun gateLossClearsAllAndPreventsPreload() {
        val subject = N64InputComposer().apply { setEnabled(true) }
        subject.setTouchButtons(N64ButtonMask.A)
        assertEquals(N64ControllerState.Neutral, subject.setEnabled(false))
        subject.setTouchButtons(N64ButtonMask.Z)
        subject.setActiveSource(N64InputSource.GAMEPAD)
        subject.setGamepad(N64ButtonMask.START, 100, 100)
        assertEquals(N64ControllerState.Neutral, subject.setEnabled(true))
    }

    @Test fun deadzoneAndRescalingCoverFullSignedRange() {
        assertEquals(0, N64AnalogScaling.scaledAxis(0.1f, 0.12f))
        assertEquals(0, N64AnalogScaling.scaledAxis(-0.12f, 0.12f))
        assertEquals(127, N64AnalogScaling.scaledAxis(1f, 0.12f))
        assertEquals(-128, N64AnalogScaling.scaledAxis(-1f, 0.12f))
        assertTrue(N64AnalogScaling.scaledAxis(0.56f, 0.12f) in 62..65)
        assertEquals(127, N64AnalogScaling.scaledAxis(5f))
    }
}
