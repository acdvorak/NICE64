package com.melfegor.nice64.api

import com.melfegor.nice64.core.protocol.ControllerSequence
import com.melfegor.nice64.core.protocol.MobileInputV1Encoder
import com.melfegor.nice64.core.protocol.SenderSessionId
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

internal data class BridgeKeyboardMouseDirectSnapshot(
    val mode: BridgeKeyboardMouseMode = BridgeKeyboardMouseMode.NONE,
    val heldKeyIds: Set<Int> = emptySet(),
    val left: Boolean = false,
    val right: Boolean = false,
    val totalX: Int = 0,
    val totalY: Int = 0,
)

internal class BridgeKeyboardMouseDirectStateHolder {
    private val latest = AtomicReference(BridgeKeyboardMouseDirectSnapshot())

    fun current(): BridgeKeyboardMouseDirectSnapshot = latest.get()

    fun beginGeneration(mode: BridgeKeyboardMouseMode) {
        require(mode != BridgeKeyboardMouseMode.NONE)
        latest.set(BridgeKeyboardMouseDirectSnapshot(mode = mode))
    }

    fun keyboard(keys: Set<Nice64LogicalKey>) {
        latest.updateAndGet { current ->
            if (current.mode != BridgeKeyboardMouseMode.KEYBOARD) current
            else current.copy(heldKeyIds = keys.mapTo(linkedSetOf()) { it.id })
        }
    }

    fun pointer(left: Boolean, right: Boolean, dx: Int, dy: Int) {
        latest.updateAndGet { current ->
            if (current.mode != BridgeKeyboardMouseMode.MOUSE) current
            else current.copy(
                left = left,
                right = right,
                totalX = current.totalX + dx,
                totalY = current.totalY + dy,
            )
        }
    }

    fun neutralize() {
        latest.updateAndGet { current -> current.copy(
            heldKeyIds = emptySet(), left = false, right = false,
        ) }
    }

    fun clear() = latest.set(BridgeKeyboardMouseDirectSnapshot())
}

internal data class BridgeKeyboardMouseSenderAuthority(
    val profileId: String,
    val sessionId: String,
    val mode: BridgeKeyboardMouseMode,
    val generation: Long,
)

internal interface BridgeKeyboardMouseDirectDataPlane : AutoCloseable {
    fun observeNetworkChanges(observer: () -> Unit)
    fun currentNetworkStillMatches(): Boolean
    suspend fun start(
        authority: BridgeKeyboardMouseSenderAuthority,
        endpoint: BridgeControllerEndpointDto,
        authorityStillUsable: (BridgeKeyboardMouseSenderAuthority) -> Boolean,
    ): Boolean
    fun requestImmediateStop()
    suspend fun stop(
        cleanCertain: Boolean,
        authorityStillUsable: (BridgeKeyboardMouseSenderAuthority) -> Boolean,
    )
}

internal class BridgeKeyboardMouseDirectGeneration(
    val authority: BridgeKeyboardMouseSenderAuthority,
    val networkHandle: Long,
    val senderId: SenderSessionId,
    initialSequence: ControllerSequence,
    private val state: BridgeKeyboardMouseDirectStateHolder,
    private val transport: BridgeLatestStateDatagramTransport,
) : AutoCloseable {
    private var nextSequence = initialSequence

    fun sendCurrent() = send(state.current(), neutral = false)
    fun sendNeutral() = send(state.current(), neutral = true)

    private fun send(snapshot: BridgeKeyboardMouseDirectSnapshot, neutral: Boolean) {
        val packet = when (authority.mode) {
            BridgeKeyboardMouseMode.KEYBOARD -> MobileInputV1Encoder.keyboard(
                senderId, nextSequence,
                if (neutral) emptySet() else snapshot.heldKeyIds,
            )
            BridgeKeyboardMouseMode.MOUSE -> MobileInputV1Encoder.pointer(
                senderId, nextSequence,
                left = !neutral && snapshot.left,
                right = !neutral && snapshot.right,
                totalX = snapshot.totalX,
                totalY = snapshot.totalY,
            )
            BridgeKeyboardMouseMode.NONE -> error("A direct sender requires an active mode.")
        }
        transport.send(packet)
        nextSequence = nextSequence.next()
    }

    override fun close() = transport.close()
}

internal class AndroidBridgeKeyboardMouseDirectDataPlane(
    private val network: BridgeControllerNetworkEnvironment,
    private val state: BridgeKeyboardMouseDirectStateHolder,
    private val transports: BridgeLatestStateDatagramTransportFactory =
        AndroidBridgeLatestStateDatagramTransportFactory,
    private val clock: BridgeLatestStateClock = SystemBridgeLatestStateClock,
    private val identities: BridgeLatestStateIdentitySource =
        SecureBridgeLatestStateIdentitySource,
    dispatcher: CoroutineDispatcher? = null,
    private val periodicLoopEnabled: Boolean = true,
) : BridgeKeyboardMouseDirectDataPlane {
    private val ownedDispatcher = dispatcher == null
    private val senderDispatcher = dispatcher ?: Executors.newSingleThreadExecutor { task ->
        Thread(task, "NICE64-MOBILE-INPUT").apply { isDaemon = true }
    }.asCoroutineDispatcher()
    private val scope = CoroutineScope(SupervisorJob() + senderDispatcher)
    private val lifecycle = Mutex()
    private val periodicAllowed = AtomicBoolean(false)
    private val active = AtomicReference<BridgeKeyboardMouseDirectGeneration?>(null)
    private val usedSenderIds = mutableSetOf<UInt>()
    private var loop: Job? = null

    override fun observeNetworkChanges(observer: () -> Unit) = network.observeChanges(observer)

    override fun currentNetworkStillMatches(): Boolean {
        val running = active.get() ?: return true
        return network.currentWifi()?.networkHandle == running.networkHandle
    }

    override suspend fun start(
        authority: BridgeKeyboardMouseSenderAuthority,
        endpoint: BridgeControllerEndpointDto,
        authorityStillUsable: (BridgeKeyboardMouseSenderAuthority) -> Boolean,
    ): Boolean = lifecycle.withLock {
        stopLocked(false, authorityStillUsable)
        if (!BridgeLatestStateEndpointPolicy.compatible(endpoint)) return@withLock false
        val binding = network.currentWifi() ?: return@withLock false
        if (!authorityStillUsable(authority)) return@withLock false
        state.beginGeneration(authority.mode)
        var created: BridgeLatestStateDatagramTransport? = null
        val generation = try {
            val transport = withContext(senderDispatcher) {
                transports.create(endpoint, binding)
            }.also { created = it }
            BridgeKeyboardMouseDirectGeneration(
                authority, binding.networkHandle,
                SenderSessionId.from(nextUniqueSenderId()),
                ControllerSequence(identities.nextNonzeroUInt()), state, transport,
            ).also { current ->
                withContext(senderDispatcher) {
                    if (!authorityStillUsable(authority)) throw CancellationException()
                    current.sendNeutral()
                }
            }
        } catch (_: Exception) {
            created?.close()
            return@withLock false
        }
        active.set(generation)
        periodicAllowed.set(true)
        if (periodicLoopEnabled) loop = scope.launch {
            runLoop(generation, authorityStillUsable)
        }
        true
    }

    override fun requestImmediateStop() {
        periodicAllowed.set(false)
        loop?.cancel()
        active.get()?.close()
    }

    override suspend fun stop(
        cleanCertain: Boolean,
        authorityStillUsable: (BridgeKeyboardMouseSenderAuthority) -> Boolean,
    ) = lifecycle.withLock { stopLocked(cleanCertain, authorityStillUsable) }

    private suspend fun stopLocked(
        cleanCertain: Boolean,
        authorityStillUsable: (BridgeKeyboardMouseSenderAuthority) -> Boolean,
    ) {
        val generation = active.getAndSet(null) ?: return
        periodicAllowed.set(false)
        loop?.cancel()
        loop = null
        if (cleanCertain) runCatching {
            withContext(senderDispatcher) {
                repeat(BridgeLatestStateSenderCoordinator.NEUTRAL_TAIL_COUNT) { index ->
                    if (!authorityStillUsable(generation.authority)) return@withContext
                    generation.sendNeutral()
                    if (index + 1 < BridgeLatestStateSenderCoordinator.NEUTRAL_TAIL_COUNT) {
                        clock.waitUntil(clock.monotonicNanos() +
                            BridgeLatestStateSenderCoordinator.NEUTRAL_TAIL_SPACING_NANOS)
                    }
                }
            }
        }
        generation.close()
    }

    private suspend fun runLoop(
        generation: BridgeKeyboardMouseDirectGeneration,
        authorityStillUsable: (BridgeKeyboardMouseSenderAuthority) -> Boolean,
    ) {
        var target = clock.monotonicNanos() + BridgeLatestStateSchedule.INTERVAL_NANOS
        try {
            while (periodicAllowed.get() && active.get() === generation) {
                clock.waitUntil(target)
                if (!periodicAllowed.get() || active.get() !== generation ||
                    !authorityStillUsable(generation.authority)) break
                generation.sendCurrent()
                target = BridgeLatestStateSchedule.nextFutureTarget(
                    target, clock.monotonicNanos(),
                )
            }
        } catch (_: CancellationException) {
            // Lifecycle stop owns cleanup.
        } catch (_: Exception) {
            if (active.compareAndSet(generation, null)) {
                periodicAllowed.set(false)
                generation.close()
            }
        }
    }

    private fun nextUniqueSenderId(): UInt {
        repeat(128) {
            val candidate = identities.nextNonzeroUInt()
            if (candidate != 0u && usedSenderIds.add(candidate)) return candidate
        }
        error("A unique mobile-input sender identity could not be generated.")
    }

    override fun close() {
        requestImmediateStop()
        network.close()
        scope.cancel()
        if (ownedDispatcher) (senderDispatcher as? AutoCloseable)?.close()
    }
}
