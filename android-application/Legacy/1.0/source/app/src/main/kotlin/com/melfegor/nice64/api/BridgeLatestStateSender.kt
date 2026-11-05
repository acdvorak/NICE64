package com.melfegor.nice64.api

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.os.SystemClock
import android.util.Log
import com.melfegor.nice64.core.model.N64ControllerState
import com.melfegor.nice64.core.protocol.ControllerSequence
import com.melfegor.nice64.core.protocol.LatestStateV1Contract
import com.melfegor.nice64.core.protocol.LatestStateV1Encoder
import com.melfegor.nice64.core.protocol.SenderSessionId
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.security.SecureRandom
import java.util.Locale
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.locks.LockSupport
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

enum class BridgeControllerDataPlanePhase { STOPPED, STARTING, ACTIVE, STOPPING, FAILED }

data class BridgeControllerDataPlanePresentation(
    val phase: BridgeControllerDataPlanePhase = BridgeControllerDataPlanePhase.STOPPED,
    val neutralOnly: Boolean = true,
    val safeMessage: String? = null,
) {
    override fun toString(): String =
        "BridgeControllerDataPlanePresentation(phase=$phase, neutralOnly=$neutralOnly)"
}

internal data class BridgeControllerSenderAuthority(
    val profileId: String,
    val sessionId: String,
    val acquisitionGeneration: Long,
)

internal fun BridgeControllerPrimaryPresentation.senderAuthorityAt(
    monotonicMilliseconds: Long,
): BridgeControllerSenderAuthority? = if (
    mobilePrimary && profileId != null && sessionId != null
) BridgeControllerSenderAuthority(profileId, sessionId, acquisitionGeneration) else null

internal class BridgeLatestControllerStateHolder(
    initial: N64ControllerState = N64ControllerState.Neutral,
) {
    private val latest = AtomicReference(initial)
    fun current(): N64ControllerState = latest.get()
    fun update(state: N64ControllerState) = latest.set(state)
    fun neutralize() = latest.set(N64ControllerState.Neutral)
}

internal data class BridgeControllerNetworkBinding(
    val networkHandle: Long,
    val bindSocket: (DatagramSocket) -> Unit,
)

internal interface BridgeControllerNetworkEnvironment : AutoCloseable {
    fun currentWifi(): BridgeControllerNetworkBinding?
    fun observeChanges(observer: () -> Unit)
    override fun close()
}

internal class AndroidBridgeControllerNetworkEnvironment(context: Context) :
    BridgeControllerNetworkEnvironment {
    private val connectivity = context.applicationContext
        .getSystemService(ConnectivityManager::class.java)
    @Volatile private var observer: (() -> Unit)? = null
    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) { changed() }
        override fun onLost(network: Network) { changed() }
        override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) {
            changed()
        }
        private fun changed() = observer?.invoke()
    }
    private val registered = AtomicBoolean(false)

    override fun currentWifi(): BridgeControllerNetworkBinding? {
        val network = connectivity.activeNetwork ?: return null
        val capabilities = connectivity.getNetworkCapabilities(network) ?: return null
        if (!capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) return null
        return BridgeControllerNetworkBinding(network.networkHandle) { socket ->
            network.bindSocket(socket)
        }
    }

    override fun observeChanges(observer: () -> Unit) {
        this.observer = observer
        if (registered.compareAndSet(false, true)) {
            connectivity.registerNetworkCallback(
                NetworkRequest.Builder()
                    .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
                    .build(),
                callback,
            )
        }
    }

    override fun close() {
        observer = null
        if (registered.compareAndSet(true, false)) {
            runCatching { connectivity.unregisterNetworkCallback(callback) }
        }
    }
}

internal interface BridgeLatestStateDatagramTransport : AutoCloseable {
    fun send(packet: ByteArray)
    override fun close()
}

internal fun interface BridgeLatestStateDatagramTransportFactory {
    fun create(
        endpoint: BridgeControllerEndpointDto,
        network: BridgeControllerNetworkBinding,
    ): BridgeLatestStateDatagramTransport
}

internal object AndroidBridgeLatestStateDatagramTransportFactory :
    BridgeLatestStateDatagramTransportFactory {
    override fun create(
        endpoint: BridgeControllerEndpointDto,
        network: BridgeControllerNetworkBinding,
    ): BridgeLatestStateDatagramTransport {
        val address = parseIpv4(endpoint.address ?: error("Controller endpoint has no address."))
        val port = endpoint.port ?: error("Controller endpoint has no port.")
        val socket = DatagramSocket(null)
        try {
            socket.reuseAddress = false
            socket.bind(InetSocketAddress(0))
            network.bindSocket(socket)
            socket.connect(address, port)
        } catch (failure: Exception) {
            socket.close()
            throw failure
        }
        return object : BridgeLatestStateDatagramTransport {
            override fun send(packet: ByteArray) = socket.send(DatagramPacket(packet, packet.size))
            override fun close() = socket.close()
        }
    }

    private fun parseIpv4(value: String): InetAddress {
        val parts = value.split('.')
        require(parts.size == 4) { "Controller endpoint is not an IPv4 literal." }
        val bytes = parts.map { part ->
            require(part.isNotEmpty() && part.length <= 3 && part.all(Char::isDigit))
            val parsed = part.toInt()
            require(parsed in 0..255 && (part == "0" || !part.startsWith('0')))
            parsed.toByte()
        }.toByteArray()
        return InetAddress.getByAddress(bytes)
    }
}

internal interface BridgeLatestStateClock {
    fun monotonicNanos(): Long
    suspend fun waitUntil(targetNanos: Long)
}

internal object SystemBridgeLatestStateClock : BridgeLatestStateClock {
    override fun monotonicNanos(): Long = SystemClock.elapsedRealtimeNanos()
    override suspend fun waitUntil(targetNanos: Long) {
        while (true) {
            val remaining = targetNanos - monotonicNanos()
            if (remaining <= 0L) return
            if (remaining > 2_000_000L) delay((remaining - 1_000_000L) / 1_000_000L)
            else LockSupport.parkNanos(remaining)
        }
    }
}

internal object BridgeLatestStateSchedule {
    const val INTERVAL_NANOS: Long = 1_000_000_000L /
        LatestStateV1Contract.TARGET_REPORTS_PER_SECOND

    fun nextFutureTarget(previousTarget: Long, now: Long): Long {
        var next = previousTarget + INTERVAL_NANOS
        if (next <= now) {
            val skipped = ((now - next) / INTERVAL_NANOS) + 1L
            next += skipped * INTERVAL_NANOS
        }
        return next
    }

    fun skippedSlots(previousTarget: Long, now: Long): Long {
        val next = previousTarget + INTERVAL_NANOS
        return if (next <= now) ((now - next) / INTERVAL_NANOS) + 1L else 0L
    }
}

internal object BridgeLatestStateEndpointPolicy {
    fun compatible(endpoint: BridgeControllerEndpointDto): Boolean =
        endpoint.available && endpoint.transport == "udp" &&
            endpoint.protocol == LatestStateV1Contract.PROTOCOL &&
            endpoint.controllerType == "n64" && endpoint.address != null &&
            endpoint.port?.let { it in 1..65535 } == true
}

internal fun interface BridgeLatestStateIdentitySource {
    fun nextNonzeroUInt(): UInt
}

internal object SecureBridgeLatestStateIdentitySource : BridgeLatestStateIdentitySource {
    private val random = SecureRandom()
    override fun nextNonzeroUInt(): UInt {
        var value: UInt
        do value = random.nextInt().toUInt() while (value == 0u)
        return value
    }
}

internal class BridgeLatestStateGeneration(
    val authority: BridgeControllerSenderAuthority,
    val networkHandle: Long,
    val senderId: SenderSessionId,
    initialSequence: ControllerSequence,
    private val state: BridgeLatestControllerStateHolder,
    private val transport: BridgeLatestStateDatagramTransport,
) : AutoCloseable {
    var nextSequence: ControllerSequence = initialSequence
        private set
    var datagramsSent: Long = 0
        private set
    var scheduledReports: Long = 0
        private set
    var skippedSlots: Long = 0
        private set
    private var firstReportNanos: Long = 0
    private var lastReportNanos: Long = 0
    private var minimumIntervalNanos: Long = Long.MAX_VALUE
    private var maximumIntervalNanos: Long = 0

    fun sendCurrent() = send(state.current())
    fun sendNeutral() = send(N64ControllerState.Neutral)

    fun recordScheduledReport(nowNanos: Long, skipped: Long = 0) {
        if (scheduledReports == 0L) {
            firstReportNanos = nowNanos
        } else {
            val interval = nowNanos - lastReportNanos
            minimumIntervalNanos = minOf(minimumIntervalNanos, interval)
            maximumIntervalNanos = maxOf(maximumIntervalNanos, interval)
        }
        lastReportNanos = nowNanos
        scheduledReports++
        skippedSlots += skipped
    }

    fun cadenceSummary(): BridgeLatestStateCadenceSummary {
        val intervals = (scheduledReports - 1L).coerceAtLeast(0L)
        val elapsed = if (intervals == 0L) 0L else lastReportNanos - firstReportNanos
        return BridgeLatestStateCadenceSummary(
            scheduledReports,
            elapsed,
            if (intervals == 0L) 0.0 else elapsed.toDouble() / intervals,
            if (intervals == 0L) 0L else minimumIntervalNanos,
            if (intervals == 0L) 0L else maximumIntervalNanos,
            skippedSlots,
        )
    }

    private fun send(controller: N64ControllerState) {
        transport.send(LatestStateV1Encoder.encode(senderId, nextSequence, controller))
        nextSequence = nextSequence.next()
        datagramsSent++
    }

    override fun close() = transport.close()
}

internal data class BridgeLatestStateCadenceSummary(
    val reports: Long,
    val elapsedNanos: Long,
    val meanIntervalNanos: Double,
    val minimumIntervalNanos: Long,
    val maximumIntervalNanos: Long,
    val skippedSlots: Long,
) {
    val effectiveHz: Double
        get() = if (meanIntervalNanos <= 0.0) 0.0 else 1_000_000_000.0 / meanIntervalNanos
}

internal class BridgeLatestStateSenderCoordinator(
    private val network: BridgeControllerNetworkEnvironment,
    private val transports: BridgeLatestStateDatagramTransportFactory =
        AndroidBridgeLatestStateDatagramTransportFactory,
    private val clock: BridgeLatestStateClock = SystemBridgeLatestStateClock,
    private val identities: BridgeLatestStateIdentitySource =
        SecureBridgeLatestStateIdentitySource,
    private val controllerState: BridgeLatestControllerStateHolder =
        BridgeLatestControllerStateHolder(),
    dispatcher: CoroutineDispatcher? = null,
    private val periodicLoopEnabled: Boolean = true,
) : AutoCloseable {
    private val ownedDispatcher = dispatcher == null
    private val senderDispatcher = dispatcher ?: Executors.newSingleThreadExecutor { task ->
        Thread(task, "NICE64-LATEST_STATE").apply { isDaemon = true }
    }.asCoroutineDispatcher()
    private val scope = CoroutineScope(SupervisorJob() + senderDispatcher)
    private val lifecycle = Mutex()
    private val periodicAllowed = AtomicBoolean(false)
    private val active = AtomicReference<BridgeLatestStateGeneration?>(null)
    private val usedSenderIds = mutableSetOf<UInt>()
    private var loop: Job? = null
    @Volatile private var publish: (BridgeControllerDataPlanePresentation) -> Unit = {}

    fun observeNetworkChanges(observer: () -> Unit) = network.observeChanges(observer)
    fun currentNetworkStillMatches(): Boolean {
        val running = active.get() ?: return true
        return network.currentWifi()?.networkHandle == running.networkHandle
    }

    suspend fun start(
        primary: BridgeControllerPrimaryPresentation,
        endpoint: BridgeControllerEndpointDto,
        authorityStillUsable: (BridgeControllerSenderAuthority) -> Boolean,
        publish: (BridgeControllerDataPlanePresentation) -> Unit,
    ): BridgeControllerDataPlanePresentation = lifecycle.withLock {
        stopLocked(false, authorityStillUsable)
        this.publish = publish
        publish(BridgeControllerDataPlanePresentation(BridgeControllerDataPlanePhase.STARTING))
        val authority = primary.senderAuthorityAt(clock.monotonicNanos() / 1_000_000L)
            ?: return@withLock failed("NICE Mobile Primary is required.")
        if (!BridgeLatestStateEndpointPolicy.compatible(endpoint)) return@withLock failed(
            "The authoritative controller endpoint is incompatible.",
        )
        val binding = network.currentWifi()
            ?: return@withLock failed("A current Wi-Fi network is required.")
        if (!authorityStillUsable(authority)) return@withLock failed(
            "Controller primary changed before the sender started.",
        )
        var createdTransport: BridgeLatestStateDatagramTransport? = null
        val generation = try {
            val transport = withContext(senderDispatcher) {
                transports.create(endpoint, binding)
            }.also { createdTransport = it }
            BridgeLatestStateGeneration(
                authority,
                binding.networkHandle,
                SenderSessionId.from(nextUniqueSenderId()),
                ControllerSequence(identities.nextNonzeroUInt()),
                controllerState,
                transport,
            ).also { current ->
                withContext(senderDispatcher) {
                    if (!authorityStillUsable(authority)) throw CancellationException()
                    current.sendNeutral()
                    current.recordScheduledReport(clock.monotonicNanos())
                }
            }
        } catch (_: Exception) {
            createdTransport?.close()
            return@withLock failed("The controller UDP sender could not start.")
        }
        active.set(generation)
        periodicAllowed.set(true)
        val presentation = BridgeControllerDataPlanePresentation(
            BridgeControllerDataPlanePhase.ACTIVE,
            safeMessage = "Controller stream active — neutral state",
        )
        publish(presentation)
        if (periodicLoopEnabled) {
            loop = scope.launch { runLoop(generation, authorityStillUsable) }
        }
        presentation
    }

    fun prepareCleanStop() {
        periodicAllowed.set(false)
        loop?.cancel()
    }

    fun requestImmediateStop() {
        periodicAllowed.set(false)
        loop?.cancel()
        active.get()?.close()
    }

    suspend fun stop(
        cleanCertain: Boolean,
        authorityStillUsable: (BridgeControllerSenderAuthority) -> Boolean,
    ): BridgeControllerDataPlanePresentation = lifecycle.withLock {
        stopLocked(cleanCertain, authorityStillUsable)
    }

    private suspend fun stopLocked(
        cleanCertain: Boolean,
        authorityStillUsable: (BridgeControllerSenderAuthority) -> Boolean,
    ): BridgeControllerDataPlanePresentation {
        val generation = active.getAndSet(null) ?: return BridgeControllerDataPlanePresentation()
        periodicAllowed.set(false)
        loop?.cancel()
        loop = null
        publish(BridgeControllerDataPlanePresentation(BridgeControllerDataPlanePhase.STOPPING))
        if (cleanCertain) {
            runCatching {
                withContext(senderDispatcher) {
                    sendCleanNeutralTail(generation, clock, authorityStillUsable)
                }
            }
        }
        generation.close()
        logCadence(generation)
        return BridgeControllerDataPlanePresentation().also(publish)
    }

    private suspend fun runLoop(
        generation: BridgeLatestStateGeneration,
        authorityStillUsable: (BridgeControllerSenderAuthority) -> Boolean,
    ) {
        var target = clock.monotonicNanos() + BridgeLatestStateSchedule.INTERVAL_NANOS
        try {
            while (periodicAllowed.get() && active.get() === generation) {
                clock.waitUntil(target)
                if (!periodicAllowed.get() || active.get() !== generation ||
                    !authorityStillUsable(generation.authority)
                ) break
                generation.sendCurrent()
                val sentAt = clock.monotonicNanos()
                val skipped = BridgeLatestStateSchedule.skippedSlots(target, sentAt)
                generation.recordScheduledReport(sentAt, skipped)
                target = BridgeLatestStateSchedule.nextFutureTarget(target, sentAt)
            }
        } catch (_: CancellationException) {
            // Lifecycle stop owns cleanup.
        } catch (_: Exception) {
            if (active.compareAndSet(generation, null)) {
                periodicAllowed.set(false)
                generation.close()
                logCadence(generation)
                publish(BridgeControllerDataPlanePresentation(
                    BridgeControllerDataPlanePhase.FAILED,
                    safeMessage = "The controller UDP sender stopped after a send failure.",
                ))
            }
        }
    }

    private fun failed(message: String) = BridgeControllerDataPlanePresentation(
        BridgeControllerDataPlanePhase.FAILED,
        safeMessage = message,
    ).also(publish)

    private fun nextUniqueSenderId(): UInt {
        repeat(MAX_IDENTITY_ATTEMPTS) {
            val candidate = identities.nextNonzeroUInt()
            if (candidate != 0u && usedSenderIds.add(candidate)) return candidate
        }
        error("A unique controller sender identity could not be generated.")
    }

    private fun logCadence(generation: BridgeLatestStateGeneration) {
        val summary = generation.cadenceSummary()
        val message = "LATEST_STATE summary reports=${summary.reports} " +
                "elapsedMs=${format("%.3f", summary.elapsedNanos / 1_000_000.0)} " +
                "meanIntervalMs=${format("%.3f", summary.meanIntervalNanos / 1_000_000.0)} " +
                "effectiveHz=${format("%.2f", summary.effectiveHz)} " +
                "minIntervalMs=${format("%.3f", summary.minimumIntervalNanos / 1_000_000.0)} " +
                "maxIntervalMs=${format("%.3f", summary.maximumIntervalNanos / 1_000_000.0)} " +
                "skippedSlots=${summary.skippedSlots}"
        runCatching { Log.i(LOG_TAG, message) }
    }

    private fun format(pattern: String, value: Double): String =
        String.format(Locale.US, pattern, value)

    override fun close() {
        requestImmediateStop()
        network.close()
        scope.cancel()
        if (ownedDispatcher) (senderDispatcher as? AutoCloseable)?.close()
    }

    companion object {
        const val NEUTRAL_TAIL_COUNT = 3
        const val NEUTRAL_TAIL_SPACING_NANOS = 12_500_000L
        private const val LOG_TAG = "NICE64Controller"
        private const val MAX_IDENTITY_ATTEMPTS = 128
    }
}

internal suspend fun sendCleanNeutralTail(
    generation: BridgeLatestStateGeneration,
    clock: BridgeLatestStateClock,
    authorityStillUsable: (BridgeControllerSenderAuthority) -> Boolean,
): Int {
    var sent = 0
    repeat(BridgeLatestStateSenderCoordinator.NEUTRAL_TAIL_COUNT) { index ->
        if (!authorityStillUsable(generation.authority)) return sent
        generation.sendNeutral()
        sent++
        if (index + 1 < BridgeLatestStateSenderCoordinator.NEUTRAL_TAIL_COUNT) {
            clock.waitUntil(
                clock.monotonicNanos() +
                    BridgeLatestStateSenderCoordinator.NEUTRAL_TAIL_SPACING_NANOS,
            )
        }
    }
    return sent
}
