package com.melfegor.nice64.api

import kotlinx.coroutines.CancellationException

/**
 * Bounded integration policy for removing a paired controller context from Android.
 *
 * It composes the frozen D8H/D8I/D8J lifecycle operations; it does not introduce another
 * controller-primary or sender state machine.
 */
internal interface BridgeControllerLifecycleActions {
    fun neutralizeInput()
    fun prepareCleanSenderStop()
    fun requestImmediateSenderStop()
    suspend fun stopSender(cleanCertain: Boolean)
    suspend fun selectBridgePrimary()
    fun clearLocalAuthority()
}

internal object BridgeControllerLifecycleConvergence {
    /** Immediately fails closed when the current controller authority can no longer be proved. */
    fun invalidate(actions: BridgeControllerLifecycleActions) {
        actions.neutralizeInput()
        actions.requestImmediateSenderStop()
        actions.clearLocalAuthority()
    }

    /**
     * Removes pairing authority only after controller activity has converged to a safe stop.
     * NICE Mobile Primary gets a clean sender stop followed by explicit Bridge selection.
     */
    suspend fun <T> removePairingAuthority(
        mobilePrimary: Boolean,
        actions: BridgeControllerLifecycleActions,
        removePairingAuthority: suspend () -> T,
    ): T {
        actions.neutralizeInput()
        try {
            if (mobilePrimary) {
                actions.prepareCleanSenderStop()
                actions.stopSender(cleanCertain = true)
                actions.selectBridgePrimary()
            } else {
                actions.requestImmediateSenderStop()
                actions.stopSender(cleanCertain = false)
            }
        } catch (cancelled: CancellationException) {
            actions.requestImmediateSenderStop()
            throw cancelled
        } catch (_: Exception) {
            // A confirmed local unpair must still remove local pairing state. The Bridge retains
            // its last explicit primary selection and no automatic failback is inferred here.
            actions.requestImmediateSenderStop()
            try {
                actions.stopSender(cleanCertain = false)
            } catch (_: Exception) {
                // The immediate-stop request already closed the active datagram transport.
            }
        } finally {
            actions.clearLocalAuthority()
        }
        return removePairingAuthority()
    }
}
