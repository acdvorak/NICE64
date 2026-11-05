package com.melfegor.nice64

import com.melfegor.nice64.api.BridgeControllerInputPresentation
import com.melfegor.nice64.api.MobileInputMode
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ControllerBlackoutPresentationModelTest {
    @Test fun eligibilityRequiresMobilePrimaryEnabledControllerMode() {
        val controller = BridgeControllerInputPresentation(
            enabled = true,
            inputMode = MobileInputMode.CONTROLLER,
        )
        assertTrue(controllerBlackoutEligible(true, controller))
        assertFalse(controllerBlackoutEligible(false, controller))
        assertFalse(controllerBlackoutEligible(true, controller.copy(enabled = false)))
        assertFalse(controllerBlackoutEligible(
            true,
            controller.copy(inputMode = MobileInputMode.ON_SCREEN),
        ))
    }

    @Test fun entryRequiresEligibilityAndDoesNotPersistAcrossNewModel() {
        val model = ControllerBlackoutPresentationModel()
        assertFalse(model.enter(false))
        assertFalse(model.active)
        assertTrue(model.enter(true))
        assertTrue(model.active)
        assertFalse(ControllerBlackoutPresentationModel().active)
    }

    @Test fun invalidationAndExplicitExitRestoreNormalPresentation() {
        val model = ControllerBlackoutPresentationModel()
        model.enter(true)
        model.reconcileEligibility(false)
        assertFalse(model.active)
        model.enter(true)
        model.exit()
        assertFalse(model.active)
    }
}
