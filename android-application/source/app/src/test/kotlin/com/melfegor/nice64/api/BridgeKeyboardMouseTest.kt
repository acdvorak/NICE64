package com.melfegor.nice64.api

import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import com.melfegor.nice64.core.protocol.ControllerSequence
import com.melfegor.nice64.core.protocol.MobileInputV1Contract
import com.melfegor.nice64.core.protocol.SenderSessionId
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.file.Files

@OptIn(ExperimentalCoroutinesApi::class)
class BridgeKeyboardMouseTest {
    @Test
    fun logicalKeyNamespaceMatchesEstablishedBridgeIds() {
        assertEquals(44, Nice64LogicalKey.A.id)
        assertEquals(70, Nice64LogicalKey.LEFT_SHIFT.id)
        assertEquals(84, Nice64LogicalKey.HOME.id)
    }

    @Test
    fun keyboardMaintainsCompleteHeldStateOnlyAfterVerification() = runTest {
        val holder = BridgeKeyboardMouseDirectStateHolder()
        val subject = BridgeKeyboardMouseCoordinator(Gateway(), this, holder)
        subject.onScreenKey(Nice64LogicalKey.A, true)
        assertTrue(holder.current().heldKeyIds.isEmpty())

        subject.setVerifiedMode(BridgeKeyboardMouseMode.KEYBOARD)
        subject.onScreenKey(Nice64LogicalKey.A, true)
        subject.onScreenKey(Nice64LogicalKey.LEFT_SHIFT, true)
        subject.onScreenKey(Nice64LogicalKey.S, true)
        subject.onScreenKey(Nice64LogicalKey.D, true)
        assertEquals(setOf(44, 70, 45, 46), holder.current().heldKeyIds)
        assertEquals(4, subject.state.value.heldKeys.size)
        subject.onScreenKey(Nice64LogicalKey.A, false)
        assertFalse(holder.current().heldKeyIds.contains(44))
        subject.releaseAll(sendNeutral = true)
        assertTrue(holder.current().heldKeyIds.isEmpty())
    }

    @Test
    fun pointerUsesPersistentButtonsAndCumulativeWrappingTotals() = runTest {
        val holder = BridgeKeyboardMouseDirectStateHolder()
        val subject = BridgeKeyboardMouseCoordinator(Gateway(), this, holder)
        subject.setVerifiedMode(BridgeKeyboardMouseMode.MOUSE)
        subject.onScreenButton(left = true)
        subject.onScreenPointer(10, -5)
        subject.onScreenPointer(300, -300)
        subject.onScreenButton(right = true)

        val current = holder.current()
        assertEquals(310, current.totalX)
        assertEquals(305, current.totalY)
        assertTrue(current.left)
        assertTrue(current.right)
        subject.releaseAll(sendNeutral = true)
        assertEquals(310, holder.current().totalX)
        assertEquals(305, holder.current().totalY)
        assertFalse(holder.current().left)
        assertFalse(holder.current().right)
    }

    @Test
    fun sourceChangePublishesNeutralWithoutResettingPointerTotals() = runTest {
        val holder = BridgeKeyboardMouseDirectStateHolder()
        val subject = BridgeKeyboardMouseCoordinator(Gateway(), this, holder)
        subject.setVerifiedMode(BridgeKeyboardMouseMode.MOUSE)
        subject.onScreenPointer(10, -5)
        subject.onScreenButton(left = true)
        subject.selectPointerSource(AndroidInputSource(
            AndroidInputSourceKind.ANY, displayName = "System / Any Mouse",
        ))
        assertEquals(10, holder.current().totalX)
        assertEquals(5, holder.current().totalY)
        assertFalse(holder.current().left)
    }

    @Test
    fun androidSourcesNormalizeDownPositiveCoordinatesBeforeUserInversion() {
        val natural = MouseInputPreferences()
        assertEquals(Nice64PointerDelta(10, 5),
            AndroidNice64PointerTransform.physical(10, -5, natural))
        assertEquals(Nice64PointerDelta(-10, -5),
            AndroidNice64PointerTransform.onScreenTrackpad(-10, 5, natural))

        assertEquals(Nice64PointerDelta(-10, 5),
            AndroidNice64PointerTransform.physical(
                10, -5, natural.copy(invertX = true)))
        assertEquals(Nice64PointerDelta(10, -5),
            AndroidNice64PointerTransform.onScreenTrackpad(
                10, -5, natural.copy(invertY = true)))
        assertEquals(Nice64PointerDelta(-10, -5),
            AndroidNice64PointerTransform.physical(
                10, -5, natural.copy(invertX = true, invertY = true)))
    }

    @Test
    fun inversionIsAppliedBeforeCumulativeTotalsAndChangingItDoesNotMove() = runTest {
        val holder = BridgeKeyboardMouseDirectStateHolder()
        val subject = BridgeKeyboardMouseCoordinator(Gateway(), this, holder)
        subject.setVerifiedMode(BridgeKeyboardMouseMode.MOUSE)
        subject.onScreenPointer(10, -5)
        assertEquals(BridgeKeyboardMouseDirectSnapshot(
            BridgeKeyboardMouseMode.MOUSE, totalX = 10, totalY = 5,
        ), holder.current())

        subject.setPointerInvertX(true)
        subject.setPointerInvertY(true)
        assertEquals(10, holder.current().totalX)
        assertEquals(5, holder.current().totalY)
        subject.onScreenPointer(3, -2)
        assertEquals(7, holder.current().totalX)
        assertEquals(3, holder.current().totalY)
    }

    @Test
    fun mousePreferencesPersistAcrossCoordinatorRecreation() = runTest {
        val store = MemoryMousePreferencesStore()
        val first = BridgeKeyboardMouseCoordinator(
            Gateway(), this, preferencesStore = store,
        )
        first.setPointerInvertX(true)
        first.setPointerInvertY(true)
        advanceUntilIdle()

        val second = BridgeKeyboardMouseCoordinator(
            Gateway(), this, preferencesStore = store,
        )
        advanceUntilIdle()
        assertTrue(second.state.value.invertX)
        assertTrue(second.state.value.invertY)
    }

    @Test
    fun androidMousePreferenceDataStorePersistsBothAxes() = runTest {
        val directory = Files.createTempDirectory("nice64-mouse-preferences-").toFile()
        try {
            val dataStore = PreferenceDataStoreFactory.create(
                scope = backgroundScope,
                produceFile = { directory.resolve("mouse.preferences_pb") },
            )
            val store = AndroidMouseInputPreferencesStore(dataStore)
            store.save(MouseInputPreferences(invertX = true, invertY = true))
            assertEquals(
                MouseInputPreferences(invertX = true, invertY = true),
                store.load(),
            )
        } finally {
            directory.deleteRecursively()
        }
    }

    @Test
    fun typedPacketsPreserveEnvelopeAndCumulativePointerState() {
        val holder = BridgeKeyboardMouseDirectStateHolder()
        val transport = PacketTransport()
        val keyboardAuthority = BridgeKeyboardMouseSenderAuthority(
            "profile", "session", BridgeKeyboardMouseMode.KEYBOARD, 1,
        )
        holder.beginGeneration(BridgeKeyboardMouseMode.KEYBOARD)
        holder.keyboard(setOf(Nice64LogicalKey.A, Nice64LogicalKey.LEFT_SHIFT))
        val keyboard = BridgeKeyboardMouseDirectGeneration(
            keyboardAuthority, 1, SenderSessionId.from(7u), ControllerSequence(9u),
            holder, transport,
        )
        keyboard.sendCurrent()
        assertEquals(MobileInputV1Contract.VERSION, transport.packets[0][28].toInt())
        assertEquals(MobileInputV1Contract.TYPE_KEYBOARD, transport.packets[0][29].toInt())
        assertEquals(listOf(44, 70), transport.packets[0].drop(31).map { it.toInt() and 0xff })
        holder.keyboard(emptySet())
        keyboard.sendCurrent()
        assertEquals(0, transport.packets[1][30].toInt())

        holder.beginGeneration(BridgeKeyboardMouseMode.MOUSE)
        holder.pointer(true, false, 10, -5)
        val mouse = BridgeKeyboardMouseDirectGeneration(
            keyboardAuthority.copy(mode = BridgeKeyboardMouseMode.MOUSE),
            1, SenderSessionId.from(8u), ControllerSequence(10u), holder, transport,
        )
        mouse.sendCurrent()
        holder.pointer(true, false, 20, -15)
        mouse.sendCurrent()
        val first = transport.packets[2]
        val second = transport.packets[3]
        assertEquals(MobileInputV1Contract.TYPE_POINTER, first[29].toInt())
        assertEquals(10, first.readInt(32))
        assertEquals(-5, first.readInt(36))
        assertEquals(30, second.readInt(32))
        assertEquals(-20, second.readInt(36))
        assertEquals(first[30], second[30])
    }

    @Test
    fun foregroundLossClearsStateAndStopsGeneration() = runTest {
        val holder = BridgeKeyboardMouseDirectStateHolder()
        val direct = RecordingDirect(holder)
        val subject = BridgeKeyboardMouseCoordinator(Gateway(), this, holder, direct)
        holder.beginGeneration(BridgeKeyboardMouseMode.MOUSE)
        holder.pointer(true, true, 10, -5)
        subject.foregroundLost()
        advanceUntilIdle()
        assertFalse(holder.current().left)
        assertFalse(holder.current().right)
        assertEquals(1, direct.stops)
    }

    private class Gateway : BridgeKeyboardMouseGateway {
        override suspend fun directEndpoint(sessionId: String) = endpoint()
        override suspend fun leds() = BridgeKeyboardLeds(raw = 3, capsLock = true, numLock = true)
    }

    private class RecordingDirect(
        private val holder: BridgeKeyboardMouseDirectStateHolder,
    ) : BridgeKeyboardMouseDirectDataPlane {
        var stops = 0
        override fun observeNetworkChanges(observer: () -> Unit) = Unit
        override fun currentNetworkStillMatches() = true
        override suspend fun start(
            authority: BridgeKeyboardMouseSenderAuthority,
            endpoint: BridgeControllerEndpointDto,
            authorityStillUsable: (BridgeKeyboardMouseSenderAuthority) -> Boolean,
        ): Boolean { holder.beginGeneration(authority.mode); return true }
        override fun requestImmediateStop() = Unit
        override suspend fun stop(
            cleanCertain: Boolean,
            authorityStillUsable: (BridgeKeyboardMouseSenderAuthority) -> Boolean,
        ) { stops++ }
        override fun close() = Unit
    }

    private class PacketTransport : BridgeLatestStateDatagramTransport {
        val packets = mutableListOf<ByteArray>()
        override fun send(packet: ByteArray) { packets += packet.copyOf() }
        override fun close() = Unit
    }

    private class MemoryMousePreferencesStore : MouseInputPreferencesStore {
        private var value = MouseInputPreferences()
        override suspend fun load() = value
        override suspend fun save(preferences: MouseInputPreferences) {
            value = preferences
        }
    }

    companion object {
        private fun endpoint() = BridgeControllerEndpointDto(
            true, null, "192.168.2.81", 6465, "udp", "latest-state-v1", "n64",
        )
        private fun ByteArray.readInt(offset: Int): Int =
            ((this[offset].toInt() and 0xff) shl 24) or
                ((this[offset + 1].toInt() and 0xff) shl 16) or
                ((this[offset + 2].toInt() and 0xff) shl 8) or
                (this[offset + 3].toInt() and 0xff)
    }
}
