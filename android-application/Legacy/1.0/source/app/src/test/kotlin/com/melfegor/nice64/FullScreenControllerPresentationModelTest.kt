package com.melfegor.nice64

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FullScreenControllerPresentationModelTest {
    @Test fun enterStoresPriorOrientationAndActivates() {
        val model = FullScreenControllerPresentationModel()

        model.enter(7)

        assertTrue(model.active)
        assertEquals(7, model.previousOrientation)
    }

    @Test fun repeatedEnterDoesNotReplaceOriginalOrientationAndExitDeactivates() {
        val model = FullScreenControllerPresentationModel()

        model.enter(7)
        model.enter(9)
        model.exit()

        assertFalse(model.active)
        assertEquals(7, model.previousOrientation)
    }

    @Test fun newPresentationModelDoesNotPersistFullScreenMode() {
        val first = FullScreenControllerPresentationModel().also { it.enter(7) }
        val replacement = FullScreenControllerPresentationModel()

        assertTrue(first.active)
        assertFalse(replacement.active)
    }
}
