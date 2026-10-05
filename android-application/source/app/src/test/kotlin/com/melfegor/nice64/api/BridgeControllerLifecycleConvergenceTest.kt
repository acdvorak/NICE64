package com.melfegor.nice64.api

import kotlinx.coroutines.test.runTest
import org.junit.Test
import kotlin.test.assertEquals

class BridgeControllerLifecycleConvergenceTest {
    @Test fun mobilePrimaryUnpairCleanStopsThenExplicitlySelectsBridge() = runTest {
        val actions = RecordingActions()
        BridgeControllerLifecycleConvergence.removePairingAuthority(true, actions) {
            actions.trace += "delete"
        }
        assertEquals(listOf("neutralize", "prepare", "stop-clean", "bridge", "clear", "delete"), actions.trace)
    }
    @Test fun bridgePrimaryUnpairDoesNotIssuePrimaryMutation() = runTest {
        val actions = RecordingActions()
        BridgeControllerLifecycleConvergence.removePairingAuthority(false, actions) {
            actions.trace += "delete"
        }
        assertEquals(listOf("neutralize", "immediate", "stop-immediate", "clear", "delete"), actions.trace)
    }
    private class RecordingActions : BridgeControllerLifecycleActions {
        val trace = mutableListOf<String>()
        override fun neutralizeInput() { trace += "neutralize" }
        override fun prepareCleanSenderStop() { trace += "prepare" }
        override fun requestImmediateSenderStop() { trace += "immediate" }
        override suspend fun stopSender(cleanCertain: Boolean) { trace += if (cleanCertain) "stop-clean" else "stop-immediate" }
        override suspend fun selectBridgePrimary() { trace += "bridge" }
        override fun clearLocalAuthority() { trace += "clear" }
    }
}
