package com.melfegor.nice64.api

import android.view.KeyEvent
import com.melfegor.nice64.core.model.N64ButtonMask
import kotlin.test.assertEquals
import kotlin.test.assertNull
import org.junit.Test

class AndroidGamepadMappingTest {
    @Test fun standardFaceAndSystemButtonsMapDeterministically() {
        assertEquals(N64ButtonMask.A, AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_BUTTON_A))
        assertEquals(N64ButtonMask.B, AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_BUTTON_B))
        assertEquals(N64ButtonMask.C_LEFT, AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_BUTTON_X))
        assertEquals(N64ButtonMask.C_UP, AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_BUTTON_Y))
        assertEquals(N64ButtonMask.START, AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_BUTTON_START))
    }

    @Test fun shouldersTriggersAndDpadUseFrozenMasks() {
        assertEquals(N64ButtonMask.L, AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_BUTTON_L1))
        assertEquals(N64ButtonMask.R, AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_BUTTON_R1))
        assertEquals(N64ButtonMask.Z, AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_BUTTON_L2))
        assertEquals(N64ButtonMask.Z, AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_BUTTON_R2))
        assertEquals(N64ButtonMask.DPAD_UP, AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_DPAD_UP))
        assertEquals(N64ButtonMask.DPAD_DOWN, AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_DPAD_DOWN))
        assertEquals(N64ButtonMask.DPAD_LEFT, AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_DPAD_LEFT))
        assertEquals(N64ButtonMask.DPAD_RIGHT, AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_DPAD_RIGHT))
        assertNull(AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_VOLUME_UP))
    }

    @Test fun rightStickThresholdSupportsDirectionsAndDiagonals() {
        assertEquals(0u, AndroidGamepadMapping.rightStickButtons(63, -63))
        assertEquals(N64ButtonMask.C_LEFT,
            AndroidGamepadMapping.rightStickButtons(-64, 0))
        assertEquals(N64ButtonMask.C_RIGHT or N64ButtonMask.C_UP,
            AndroidGamepadMapping.rightStickButtons(127, 127))
        assertEquals(N64ButtonMask.C_LEFT or N64ButtonMask.C_DOWN,
            AndroidGamepadMapping.rightStickButtons(-128, -128))
    }
}
