package com.melfegor.nice64.api

import com.melfegor.nice64.core.model.N64ButtonMask
import com.melfegor.nice64.core.model.N64ControllerState
import com.melfegor.nice64.core.protocol.ControllerSequence
import com.melfegor.nice64.core.protocol.LatestStateV1Contract
import com.melfegor.nice64.core.protocol.SenderSessionId
import kotlinx.coroutines.test.runTest
import org.junit.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class BridgeLatestStateSenderTest {
    @Test
    fun frozenEncoderProducesExactNeutralVector() {
        val transport = FakeTransport()
        val generation = generation(transport, 0x10203040u, 1u)
        generation.sendNeutral()
        assertEquals(32, transport.packets.single().size)
        assertEquals(
            "4E3634550101011C102030400000000100000000000000000004000000000000",
            transport.packets.single().hex(),
        )
    }

    @Test
    fun completeCurrentStateIsEncodedWithoutQueueOrDelta() {
        val state = BridgeLatestControllerStateHolder()
        val transport = FakeTransport()
        val generation = generation(transport, state = state)
        state.update(N64ControllerState(N64ButtonMask.A, 1, 2))
        state.update(N64ControllerState(N64ButtonMask.B, 64, -64))
        generation.sendCurrent()
        assertContentEquals(byteArrayOf(0x40, 0x00, 0x40, 0xC0.toByte()),
            transport.packets.single().copyOfRange(28, 32))
        assertEquals(1, transport.packets.size)
    }

    @Test
    fun sequenceAdvancesOnlyAfterActualDatagramAndWrapsUnsigned() {
        val transport = FakeTransport()
        val generation = generation(transport, sequence = UInt.MAX_VALUE)
        generation.sendCurrent()
        assertEquals(0u, generation.nextSequence.value)
        assertEquals(1, generation.datagramsSent)
        transport.fail = true
        runCatching { generation.sendCurrent() }
        assertEquals(0u, generation.nextSequence.value)
        assertEquals(1, generation.datagramsSent)
    }

    @Test
    fun distinctAcquisitionsUseDistinctNonzeroIdentityAndSequence() {
        val values = ArrayDeque(listOf(11u, 101u, 22u, 202u))
        val source = BridgeLatestStateIdentitySource { values.removeFirst() }
        val firstSender = source.nextNonzeroUInt()
        val firstSequence = source.nextNonzeroUInt()
        val secondSender = source.nextNonzeroUInt()
        val secondSequence = source.nextNonzeroUInt()
        assertTrue(firstSender != 0u && firstSequence != 0u)
        assertTrue(secondSender != 0u && secondSequence != 0u)
        assertNotEquals(firstSender, secondSender)
        assertNotEquals(firstSequence, secondSequence)
    }

    @Test
    fun senderAuthorityRequiresConfirmedUsableD8hGeneration() {
        val owned = owned()
        assertNotNull(owned.senderAuthorityAt(4_999))
        assertNotNull(owned.senderAuthorityAt(5_000))
        listOf(
            BridgeControllerPrimaryPhase.BRIDGE_PRIMARY,
            BridgeControllerPrimaryPhase.SWITCHING_TO_MOBILE,
            BridgeControllerPrimaryPhase.SWITCHING_TO_BRIDGE,
            BridgeControllerPrimaryPhase.ERROR,
        ).forEach { phase ->
            assertNull(owned.copy(phase = phase).senderAuthorityAt(1_000), phase.name)
        }
    }

    @Test
    fun authorityIsProfileSessionAndAcquisitionGenerationScoped() {
        val expected = owned().senderAuthorityAt(1_000)
        assertNotNull(expected)
        assertNotEquals(expected, owned().copy(profileId = "profile-b").senderAuthorityAt(1_000))
        assertNotEquals(expected, owned().copy(sessionId = "session-b").senderAuthorityAt(1_000))
        assertNotEquals(expected, owned().copy(acquisitionGeneration = 8).senderAuthorityAt(1_000))
    }

    @Test
    fun endpointMustBeExactAuthoritativeUdpLatestStateN64Tuple() {
        val valid = endpoint()
        assertTrue(BridgeLatestStateEndpointPolicy.compatible(valid))
        assertFalse(BridgeLatestStateEndpointPolicy.compatible(valid.copy(available = false)))
        assertFalse(BridgeLatestStateEndpointPolicy.compatible(valid.copy(transport = "tcp")))
        assertFalse(BridgeLatestStateEndpointPolicy.compatible(valid.copy(protocol = "rudp-v1")))
        assertFalse(BridgeLatestStateEndpointPolicy.compatible(valid.copy(controllerType = "snes")))
        assertFalse(BridgeLatestStateEndpointPolicy.compatible(valid.copy(address = null)))
        assertFalse(BridgeLatestStateEndpointPolicy.compatible(valid.copy(port = null)))
    }

    @Test
    fun schedulerUsesNominal120HzAbsoluteTargets() {
        assertEquals(8_333_333L, BridgeLatestStateSchedule.INTERVAL_NANOS)
        assertEquals(108_333_333L,
            BridgeLatestStateSchedule.nextFutureTarget(100_000_000L, 101_000_000L))
    }

    @Test
    fun missedSchedulerSlotsAreSkippedWithoutCatchupBurst() {
        val next = BridgeLatestStateSchedule.nextFutureTarget(100_000_000L, 130_000_000L)
        assertEquals(133_333_332L, next)
        assertTrue(next > 130_000_000L)
        assertTrue(next - 130_000_000L <= BridgeLatestStateSchedule.INTERVAL_NANOS)
        assertEquals(3L, BridgeLatestStateSchedule.skippedSlots(100_000_000L, 130_000_000L))
    }

    @Test
    fun cadenceSummaryMeasuresSuccessfulScheduledReportsOnly() {
        val generation = generation(FakeTransport())
        generation.recordScheduledReport(1_000_000_000L)
        generation.recordScheduledReport(1_008_000_000L)
        generation.recordScheduledReport(1_017_000_000L, skipped = 1)
        val summary = generation.cadenceSummary()
        assertEquals(3, summary.reports)
        assertEquals(17_000_000L, summary.elapsedNanos)
        assertEquals(8_500_000.0, summary.meanIntervalNanos)
        assertEquals(8_000_000L, summary.minimumIntervalNanos)
        assertEquals(9_000_000L, summary.maximumIntervalNanos)
        assertEquals(1, summary.skippedSlots)
    }

    @Test
    fun cleanCertainStopSendsExactlyThreeNeutralOpportunities() = runTest {
        val transport = FakeTransport()
        val generation = generation(transport)
        val clock = FakeClock()
        val sent = sendCleanNeutralTail(generation, clock) { true }
        assertEquals(3, sent)
        assertEquals(3, transport.packets.size)
        transport.packets.forEach { assertContentEquals(byteArrayOf(0, 0, 0, 0),
            it.copyOfRange(28, 32)) }
        assertEquals(listOf(12_500_000L, 25_000_000L), clock.targets)
    }

    @Test
    fun uncertainStopSendsNoNeutralTail() = runTest {
        val transport = FakeTransport()
        val generation = generation(transport)
        val sent = sendCleanNeutralTail(generation, FakeClock()) { false }
        assertEquals(0, sent)
        assertTrue(transport.packets.isEmpty())
    }

    @Test
    fun lossDuringNeutralTailStopsAdditionalDatagramsImmediately() = runTest {
        val transport = FakeTransport()
        val generation = generation(transport)
        var checks = 0
        val sent = sendCleanNeutralTail(generation, FakeClock()) { ++checks == 1 }
        assertEquals(1, sent)
        assertEquals(1, transport.packets.size)
    }

    @Test
    fun dataPlanePresentationDoesNotExposeSenderIdentity() {
        val presentation = BridgeControllerDataPlanePresentation(
            BridgeControllerDataPlanePhase.ACTIVE,
            safeMessage = "Controller stream active — neutral state",
        )
        assertFalse(presentation.toString().contains("sender", ignoreCase = true))
        assertFalse(presentation.toString().contains("lease", ignoreCase = true))
    }

    @Test
    fun coordinatorStartsOnlyWhenOwnedSendsImmediateNeutralAndCleanTail() = runTest {
        val network = FakeNetwork()
        val factory = FakeTransportFactory()
        val ids = ArrayDeque(listOf(0x11111111u, 9u))
        val coordinator = BridgeLatestStateSenderCoordinator(
            network, factory, FakeClock(),
            BridgeLatestStateIdentitySource { ids.removeFirst() },
            periodicLoopEnabled = false,
        )
        val result = coordinator.start(owned(), endpoint(), { true }, {})
        assertEquals(BridgeControllerDataPlanePhase.ACTIVE, result.phase)
        assertEquals(1, factory.transports.single().packets.size)
        assertContentEquals(byteArrayOf(0, 0, 0, 0),
            factory.transports.single().packets.single().copyOfRange(28, 32))
        coordinator.prepareCleanStop()
        coordinator.stop(true) { true }
        assertEquals(4, factory.transports.single().packets.size)
        coordinator.close()
    }

    @Test
    fun coordinatorRefusesUnownedIncompatibleOrNonWifiStart() = runTest {
        suspend fun attempt(
            ownership: BridgeControllerPrimaryPresentation = owned(),
            endpoint: BridgeControllerEndpointDto = endpoint(),
            wifi: Boolean = true,
        ): BridgeControllerDataPlanePresentation {
            val coordinator = BridgeLatestStateSenderCoordinator(
                FakeNetwork(if (wifi) 44 else null), FakeTransportFactory(), FakeClock(),
                BridgeLatestStateIdentitySource { 1u }, periodicLoopEnabled = false,
            )
            return coordinator.start(ownership, endpoint, { true }, {}).also { coordinator.close() }
        }
        assertEquals(BridgeControllerDataPlanePhase.FAILED,
            attempt(owned().copy(phase = BridgeControllerPrimaryPhase.BRIDGE_PRIMARY)).phase)
        assertEquals(BridgeControllerDataPlanePhase.FAILED,
            attempt(endpoint = endpoint().copy(protocol = "rudp-v1")).phase)
        assertEquals(BridgeControllerDataPlanePhase.FAILED, attempt(wifi = false).phase)
    }

    @Test
    fun uncertainStopClosesWithoutTailAndNetworkReplacementIsDetectable() = runTest {
        val network = FakeNetwork()
        val factory = FakeTransportFactory()
        val coordinator = BridgeLatestStateSenderCoordinator(
            network, factory, FakeClock(), BridgeLatestStateIdentitySource { 7u },
            periodicLoopEnabled = false,
        )
        coordinator.start(owned(), endpoint(), { true }, {})
        network.handle = 99
        assertFalse(coordinator.currentNetworkStillMatches())
        coordinator.requestImmediateStop()
        coordinator.stop(false) { false }
        assertEquals(1, factory.transports.single().packets.size)
        assertTrue(factory.transports.single().closed)
        coordinator.close()
    }

    @Test
    fun udpSocketCreationRunsOnDedicatedSenderThread() = runTest {
        val factory = FakeTransportFactory()
        val coordinator = BridgeLatestStateSenderCoordinator(
            FakeNetwork(), factory, FakeClock(), BridgeLatestStateIdentitySource { 7u },
            periodicLoopEnabled = false,
        )
        coordinator.start(owned(), endpoint(), { true }, {})
        assertTrue(factory.creationThread.startsWith("NICE64-LATEST_STATE"))
        coordinator.stop(false) { false }
        coordinator.close()
    }

    @Test
    fun coordinatorNeverReusesSenderIdentityWithinProcessLifetime() = runTest {
        val factory = FakeTransportFactory()
        val values = ArrayDeque(listOf(11u, 101u, 11u, 22u, 202u))
        val coordinator = BridgeLatestStateSenderCoordinator(
            FakeNetwork(), factory, FakeClock(),
            BridgeLatestStateIdentitySource { values.removeFirst() },
            periodicLoopEnabled = false,
        )
        coordinator.start(owned(), endpoint(), { true }, {})
        coordinator.stop(false) { false }
        coordinator.start(
            owned().copy(acquisitionGeneration = 8), endpoint(), { true }, {},
        )
        val first = factory.transports[0].packets[0].copyOfRange(8, 12)
        val second = factory.transports[1].packets[0].copyOfRange(8, 12)
        assertContentEquals(byteArrayOf(0, 0, 0, 11), first)
        assertContentEquals(byteArrayOf(0, 0, 0, 22), second)
        coordinator.stop(false) { false }
        coordinator.close()
    }

    private fun generation(
        transport: FakeTransport,
        sender: UInt = 7u,
        sequence: UInt = 9u,
        state: BridgeLatestControllerStateHolder = BridgeLatestControllerStateHolder(),
    ) = BridgeLatestStateGeneration(
        BridgeControllerSenderAuthority("profile-a", "session-a", 1),
        44L,
        SenderSessionId.from(sender),
        ControllerSequence(sequence),
        state,
        transport,
    )

    private fun owned() = BridgeControllerPrimaryPresentation(
        BridgeControllerPrimaryPhase.MOBILE_PRIMARY,
        "profile-a", "session-a", endpoint(), 7,
    )

    private fun endpoint() = BridgeControllerEndpointDto(
        true, null, "192.168.2.81", LatestStateV1Contract.UDP_PORT,
        "udp", LatestStateV1Contract.PROTOCOL, "n64",
    )

    private class FakeTransport : BridgeLatestStateDatagramTransport {
        val packets = mutableListOf<ByteArray>()
        var fail = false
        var closed = false
        override fun send(packet: ByteArray) {
            if (fail) error("simulated send failure")
            packets += packet.copyOf()
        }
        override fun close() { closed = true }
    }

    private class FakeTransportFactory : BridgeLatestStateDatagramTransportFactory {
        val transports = mutableListOf<FakeTransport>()
        var creationThread = ""
        override fun create(
            endpoint: BridgeControllerEndpointDto,
            network: BridgeControllerNetworkBinding,
        ): BridgeLatestStateDatagramTransport = FakeTransport().also {
            creationThread = Thread.currentThread().name
            transports += it
        }
    }

    private class FakeNetwork(initialHandle: Int? = 44) : BridgeControllerNetworkEnvironment {
        var handle: Int? = initialHandle
        override fun currentWifi(): BridgeControllerNetworkBinding? = handle?.let { value ->
            BridgeControllerNetworkBinding(value.toLong()) {}
        }
        override fun observeChanges(observer: () -> Unit) = Unit
        override fun close() = Unit
    }

    private class FakeClock : BridgeLatestStateClock {
        var now = 0L
        val targets = mutableListOf<Long>()
        override fun monotonicNanos(): Long = now
        override suspend fun waitUntil(targetNanos: Long) {
            targets += targetNanos
            now = targetNanos
        }
    }

    private fun ByteArray.hex(): String = joinToString("") { "%02X".format(it) }
}
