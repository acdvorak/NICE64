package com.melfegor.nice64

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ControllerGameplayContextTest {
    @Test fun gameplayRequiresMobilePrimaryActiveDataPlaneAndEnabledCoordinator() {
        assertTrue(controllerGameplayContextActive(true, true, true))
        assertFalse(controllerGameplayContextActive(false, true, true))
        assertFalse(controllerGameplayContextActive(true, false, true))
        assertFalse(controllerGameplayContextActive(true, true, false))
        assertFalse(controllerGameplayContextActive(true, true, true, controllerMode = false))
    }

    @Test fun bridgePrimaryAndInactiveDataPlanePreserveUiNavigation() {
        assertFalse(controllerGameplayContextActive(
            mobilePrimary = false,
            dataPlaneActive = true,
            coordinatorEnabled = true,
        ))
        assertFalse(controllerGameplayContextActive(
            mobilePrimary = true,
            dataPlaneActive = false,
            coordinatorEnabled = false,
        ))
    }
}
