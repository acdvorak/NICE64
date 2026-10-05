package com.melfegor.nice64

import org.junit.Assert.assertEquals
import org.junit.Test

class BridgeControllerActivityLifecycleTest {
    @Test
    fun configurationRecreationClearsTouchWithoutBackgroundingRuntime() {
        var touchClears = 0
        var backgrounds = 0

        handleControllerActivityStop(
            isChangingConfigurations = true,
            clearTransientTouch = { touchClears++ },
            backgroundControllerRuntime = { backgrounds++ },
        )

        assertEquals(1, touchClears)
        assertEquals(0, backgrounds)
    }

    @Test
    fun genuineBackgroundUsesExistingFailClosedRuntimeTeardown() {
        var touchClears = 0
        var backgrounds = 0

        handleControllerActivityStop(
            isChangingConfigurations = false,
            clearTransientTouch = { touchClears++ },
            backgroundControllerRuntime = { backgrounds++ },
        )

        assertEquals(0, touchClears)
        assertEquals(1, backgrounds)
    }
}
