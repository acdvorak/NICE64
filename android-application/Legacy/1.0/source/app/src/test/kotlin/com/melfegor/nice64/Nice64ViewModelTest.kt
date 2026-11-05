package com.melfegor.nice64

import com.melfegor.nice64.core.model.PairingState
import com.melfegor.nice64.api.BridgeControllerDataPlanePhase
import com.melfegor.nice64.api.BridgeControllerPrimaryPhase
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class Nice64ViewModelTest {
    @Test
    fun initialStateIsStableAndUnpaired() {
        val state = Nice64ViewModel().state.value
        assertTrue(state.pairing is PairingState.Unpaired)
        assertEquals(null, state.operation)
    }

    @Test
    fun backgroundThenForegroundNeverSynthesizesControllerAuthority() {
        val model = Nice64ViewModel()

        model.controllerPrimaryBackgrounded()
        model.controllerPrimaryForegrounded()

        assertEquals(BridgeControllerPrimaryPhase.BRIDGE_PRIMARY, model.controllerPrimaryState.value.phase)
        assertEquals(BridgeControllerDataPlanePhase.STOPPED, model.controllerDataPlaneState.value.phase)
        assertTrue(!model.controllerInputState.value.enabled)
    }

    @Test
    fun newViewModelAfterProcessRestartBeginsNeutralAndUnowned() {
        val restarted = Nice64ViewModel()

        assertEquals(BridgeControllerPrimaryPhase.BRIDGE_PRIMARY, restarted.controllerPrimaryState.value.phase)
        assertEquals(BridgeControllerDataPlanePhase.STOPPED, restarted.controllerDataPlaneState.value.phase)
        assertTrue(!restarted.controllerInputState.value.enabled)
    }
}
