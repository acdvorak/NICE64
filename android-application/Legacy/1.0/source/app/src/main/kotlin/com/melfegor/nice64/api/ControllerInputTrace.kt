package com.melfegor.nice64.api

import android.os.SystemClock
import android.view.KeyEvent
import com.melfegor.nice64.core.model.N64ControllerState
import java.io.BufferedWriter
import java.io.File
import java.io.FileOutputStream
import java.io.OutputStreamWriter
import java.nio.charset.StandardCharsets
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import java.util.UUID
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.CountDownLatch
import java.util.concurrent.atomic.AtomicLong
import kotlin.math.ceil
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

internal data class AndroidInputDispatchBoundary(
    val eventTimeMs: Long,
    val t0UptimeMs: Long,
    val t0ElapsedNanos: Long,
) {
    val androidEventToDispatchMs: Long
        get() = (t0UptimeMs - eventTimeMs).coerceAtLeast(0L)

    companion object {
        fun capture(event: KeyEvent) = AndroidInputDispatchBoundary(
            eventTimeMs = event.eventTime,
            t0UptimeMs = SystemClock.uptimeMillis(),
            t0ElapsedNanos = SystemClock.elapsedRealtimeNanos(),
        )
    }
}

internal interface ControllerInputTraceClock {
    fun elapsedRealtimeNanos(): Long
}

internal object AndroidControllerInputTraceClock : ControllerInputTraceClock {
    override fun elapsedRealtimeNanos(): Long = SystemClock.elapsedRealtimeNanos()
}

internal data class ControllerInputTracePresentation(
    val running: Boolean = false,
    val sessionId: String? = null,
    val records: Long = 0,
    val dropped: Long = 0,
    val fileName: String? = null,
    val summaryFileName: String? = null,
    val latestLabel: String? = null,
    val latestAndroidEventToDispatchMs: Long? = null,
    val latestDispatchToMappedMs: Double? = null,
    val latestDispatchToHolderMs: Double? = null,
    val latestDispatchToUiModelMs: Double? = null,
    val failure: String? = null,
)

@Serializable
internal data class ControllerInputTraceRecord(
    val diagnosticSessionId: String,
    val recordNumber: Long,
    val runtimeDeviceId: Int,
    val stableDeviceDescriptor: String? = null,
    val deviceName: String? = null,
    val aggregateDeviceSources: Int = 0,
    val eventSource: Int,
    val keyCode: Int,
    val keyCodeName: String,
    val scanCode: Int,
    val action: String,
    val repeatCount: Int,
    val downTimeMs: Long,
    val eventTimeMs: Long,
    val t0UptimeMs: Long,
    val t0ElapsedNanos: Long,
    val t1ElapsedNanos: Long? = null,
    val t2ElapsedNanos: Long? = null,
    val resolvedN64SemanticTarget: String? = null,
    val t3ElapsedNanos: Long? = null,
    val t4ElapsedNanos: Long? = null,
    val t5ElapsedNanos: Long? = null,
    val t6ElapsedNanos: Long? = null,
    val senderSequence: Long? = null,
    val mixedSourceFallback: Boolean = false,
    val aliasCoalescedOrSuppressed: Boolean = false,
    val mobileInputMode: String,
    val activePrimary: String,
    val profileIdentityOrType: String? = null,
    val resultingButtons: Long? = null,
    val resultingAnalogX: Int? = null,
    val resultingAnalogY: Int? = null,
    val rejectionReason: String? = null,
    val androidEventToDispatchMs: Long,
    val dispatchToNormalizedMs: Double? = null,
    val dispatchToMappedMs: Double? = null,
    val dispatchToSemanticHandlerMs: Double? = null,
    val dispatchToHolderMs: Double? = null,
    val dispatchToUiModelMs: Double? = null,
    val dispatchToSenderSampleMs: Double? = null,
    val semanticHandlerToHolderMs: Double? = null,
)

internal class ControllerInputTraceEdge internal constructor(
    private val owner: ControllerInputTraceCoordinator,
    private val sessionId: String,
    private val recordNumber: Long,
    private val rawDeviceId: Int,
    private val rawEventSource: Int,
    private val rawKeyCode: Int,
    private val rawKeyName: String,
    private val rawScanCode: Int,
    private val rawAction: Int,
    private val rawRepeatCount: Int,
    private val rawDownTimeMs: Long,
    private val boundary: AndroidInputDispatchBoundary,
    private val clock: ControllerInputTraceClock,
) {
    private var observation: AndroidPhysicalButtonObservation? = null
    private var t1: Long? = null
    private var t2: Long? = null
    private var t3: Long? = null
    private var t4: Long? = null
    private var target: String? = null
    private var profile: String? = null
    private var mode = MobileInputMode.ON_SCREEN.name
    private var primary = "bridge"
    private var result: N64ControllerState? = null
    private var completed = false

    fun normalized(value: AndroidPhysicalButtonObservation) {
        observation = value
        t1 = clock.elapsedRealtimeNanos()
    }

    fun context(inputMode: MobileInputMode, mobilePrimary: Boolean) {
        mode = inputMode.name
        primary = if (mobilePrimary) "nice-mobile" else "bridge"
    }

    fun mapped(semanticTarget: String, profileIdentityOrType: String?) {
        target = semanticTarget
        profile = profileIdentityOrType
        t2 = clock.elapsedRealtimeNanos()
    }

    fun semanticHandlerInvoked() {
        t3 = clock.elapsedRealtimeNanos()
    }

    fun holderUpdated(state: N64ControllerState) {
        result = state
        t4 = clock.elapsedRealtimeNanos()
    }

    fun complete(
        rejectionReason: String? = null,
        aliasSuppressed: Boolean = false,
    ) {
        if (completed) return
        completed = true
        val t5 = clock.elapsedRealtimeNanos()
        val normalized = observation
        val eventTime = normalized?.eventTimeMillis ?: boundary.eventTimeMs
        owner.submit(ControllerInputTraceRecord(
            diagnosticSessionId = sessionId,
            recordNumber = recordNumber,
            runtimeDeviceId = normalized?.runtimeDeviceId ?: rawDeviceId,
            stableDeviceDescriptor = normalized?.stableDescriptor,
            deviceName = normalized?.deviceName,
            aggregateDeviceSources = normalized?.aggregateDeviceSources ?: 0,
            eventSource = normalized?.eventSource ?: rawEventSource,
            keyCode = normalized?.keyCode ?: rawKeyCode,
            keyCodeName = normalized?.keyName ?: rawKeyName,
            scanCode = normalized?.scanCode ?: rawScanCode,
            action = when (normalized?.action ?: rawAction) {
                KeyEvent.ACTION_DOWN -> "DOWN"
                KeyEvent.ACTION_UP -> "UP"
                else -> "OTHER"
            },
            repeatCount = normalized?.repeatCount ?: rawRepeatCount,
            downTimeMs = normalized?.downTimeMillis ?: rawDownTimeMs,
            eventTimeMs = eventTime,
            t0UptimeMs = boundary.t0UptimeMs,
            t0ElapsedNanos = boundary.t0ElapsedNanos,
            t1ElapsedNanos = t1,
            t2ElapsedNanos = t2,
            resolvedN64SemanticTarget = target,
            t3ElapsedNanos = t3,
            t4ElapsedNanos = t4,
            t5ElapsedNanos = t5,
            mixedSourceFallback = normalized?.eligibility ==
                AndroidPhysicalButtonEligibility.MIXED_CONTROLLER_FALLBACK,
            aliasCoalescedOrSuppressed = aliasSuppressed,
            mobileInputMode = mode,
            activePrimary = primary,
            profileIdentityOrType = profile,
            resultingButtons = result?.buttons?.toLong(),
            resultingAnalogX = result?.analogX,
            resultingAnalogY = result?.analogY,
            rejectionReason = rejectionReason,
            androidEventToDispatchMs =
                (boundary.t0UptimeMs - eventTime).coerceAtLeast(0L),
            dispatchToNormalizedMs = t1.deltaFromT0(),
            dispatchToMappedMs = t2.deltaFromT0(),
            dispatchToSemanticHandlerMs = t3.deltaFromT0(),
            dispatchToHolderMs = t4.deltaFromT0(),
            dispatchToUiModelMs = nanosToMillis(t5 - boundary.t0ElapsedNanos),
            semanticHandlerToHolderMs = if (t3 != null && t4 != null) {
                nanosToMillis(t4!! - t3!!)
            } else null,
        ))
    }

    fun isComplete(): Boolean = completed

    private fun Long?.deltaFromT0(): Double? =
        this?.let { nanosToMillis(it - boundary.t0ElapsedNanos) }
}

internal class ControllerInputTraceCoordinator(
    private val directory: File,
    private val clock: ControllerInputTraceClock = AndroidControllerInputTraceClock,
    queueCapacity: Int = DEFAULT_QUEUE_CAPACITY,
    private val maxRecords: Long = DEFAULT_MAX_RECORDS,
    private val recordWriteGateForTests: CountDownLatch? = null,
) : AutoCloseable {
    private sealed interface Command {
        data class Start(val session: Session, val complete: CountDownLatch) : Command
        data class Record(val value: ControllerInputTraceRecord) : Command
        data class Stop(val complete: CountDownLatch) : Command
        data object Shutdown : Command
    }

    private data class Session(
        val id: String,
        val fileName: String,
        val summaryFileName: String,
    )

    private val json = Json { encodeDefaults = true; explicitNulls = true }
    private val queue = ArrayBlockingQueue<Command>(queueCapacity.coerceAtLeast(1))
    private val mutableState = MutableStateFlow(ControllerInputTracePresentation())
    val state: StateFlow<ControllerInputTracePresentation> = mutableState.asStateFlow()
    private val nextRecord = AtomicLong(0)
    private val dropped = AtomicLong(0)
    private val controlLock = Any()
    @Volatile private var activeSession: Session? = null
    @Volatile private var closed = false
    private val worker = Thread(::writerLoop, "nice64-controller-input-trace").apply {
        isDaemon = true
        start()
    }

    fun start(): ControllerInputTracePresentation = synchronized(controlLock) {
        check(!closed) { "Trace coordinator is closed." }
        if (activeSession != null) stopLocked()
        val id = UUID.randomUUID().toString()
        val stamp = requireNotNull(UTC_FILE_FORMAT.get()).format(Date())
        val stem = "touch-trigger-$stamp-${id.take(8)}"
        val session = Session(id, "$stem.jsonl", "$stem-summary.txt")
        nextRecord.set(0)
        dropped.set(0)
        val complete = CountDownLatch(1)
        queue.put(Command.Start(session, complete))
        complete.await()
        mutableState.value
    }

    fun stop(): ControllerInputTracePresentation = synchronized(controlLock) {
        stopLocked()
    }

    fun beginEdge(event: KeyEvent, boundary: AndroidInputDispatchBoundary): ControllerInputTraceEdge? {
        val session = activeSession ?: return null
        val number = nextRecord.incrementAndGet()
        if (number > maxRecords) {
            recordDropped()
            return null
        }
        return ControllerInputTraceEdge(
            this,
            session.id,
            number,
            event.deviceId,
            event.source,
            event.keyCode,
            KeyEvent.keyCodeToString(event.keyCode),
            event.scanCode,
            event.action,
            event.repeatCount,
            event.downTime,
            boundary,
            clock,
        )
    }

    internal fun submit(record: ControllerInputTraceRecord) {
        val session = activeSession
        if (session == null || session.id != record.diagnosticSessionId) return
        if (!queue.offer(Command.Record(record))) {
            recordDropped()
            return
        }
        val current = mutableState.value
        mutableState.value = current.copy(
            records = current.records + 1,
            latestLabel = buildString {
                append(record.resolvedN64SemanticTarget ?: record.keyCodeName)
                append(' ')
                append(record.action)
                record.rejectionReason?.let { append(" — ").append(it) }
            },
            latestAndroidEventToDispatchMs = record.androidEventToDispatchMs,
            latestDispatchToMappedMs = record.dispatchToMappedMs,
            latestDispatchToHolderMs = record.dispatchToHolderMs,
            latestDispatchToUiModelMs = record.dispatchToUiModelMs,
        )
    }

    override fun close() {
        synchronized(controlLock) {
            if (closed) return
            stopLocked()
            closed = true
            queue.put(Command.Shutdown)
        }
        worker.join(5_000)
    }

    private fun stopLocked(): ControllerInputTracePresentation {
        activeSession = null
        if (!mutableState.value.running) return mutableState.value
        val complete = CountDownLatch(1)
        queue.put(Command.Stop(complete))
        complete.await()
        return mutableState.value
    }

    private fun recordDropped() {
        val count = dropped.incrementAndGet()
        mutableState.value = mutableState.value.copy(dropped = count)
    }

    private fun writerLoop() {
        var session: Session? = null
        var writer: BufferedWriter? = null
        var records = mutableListOf<ControllerInputTraceRecord>()
        while (true) {
            when (val command = queue.take()) {
                is Command.Start -> {
                    writer?.close()
                    records = mutableListOf()
                    try {
                        directory.mkdirs()
                        val target = File(directory, command.session.fileName)
                        writer = BufferedWriter(OutputStreamWriter(
                            FileOutputStream(target, false), StandardCharsets.UTF_8,
                        ))
                        session = command.session
                        activeSession = command.session
                        mutableState.value = ControllerInputTracePresentation(
                            running = true,
                            sessionId = command.session.id,
                            fileName = command.session.fileName,
                            summaryFileName = command.session.summaryFileName,
                        )
                    } catch (_: Exception) {
                        writer = null
                        session = null
                        activeSession = null
                        mutableState.value = ControllerInputTracePresentation(
                            failure = "Trace file could not be created.",
                        )
                    } finally {
                        command.complete.countDown()
                    }
                }
                is Command.Record -> {
                    if (session?.id == command.value.diagnosticSessionId && writer != null) {
                        recordWriteGateForTests?.await()
                        writer.write(json.encodeToString(command.value))
                        writer.newLine()
                        records += command.value
                    }
                }
                is Command.Stop -> {
                    val stopped = session
                    try {
                        writer?.flush()
                        writer?.close()
                        if (stopped != null) writeSummary(stopped, records)
                    } finally {
                        writer = null
                        session = null
                        activeSession = null
                        mutableState.value = mutableState.value.copy(running = false)
                        command.complete.countDown()
                    }
                }
                Command.Shutdown -> {
                    writer?.flush()
                    writer?.close()
                    return
                }
            }
        }
    }

    private fun writeSummary(session: Session, records: List<ControllerInputTraceRecord>) {
        val down = records.count { it.action == "DOWN" }
        val up = records.count { it.action == "UP" }
        val aliases = records.count { it.aliasCoalescedOrSuppressed }
        val rejected = records.count { it.rejectionReason != null }
        File(directory, session.summaryFileName).writeText(buildString {
            appendLine("NICE64 controller input trace summary")
            appendLine("sessionId=${session.id}")
            appendLine("records=${records.size}")
            appendLine("down=$down")
            appendLine("up=$up")
            appendLine("suppressedAliases=$aliases")
            appendLine("rejected=$rejected")
            appendLine("dropped=${dropped.get()}")
            appendMetric("androidEventToAppDispatchMs", records.map {
                it.androidEventToDispatchMs.toDouble()
            })
            appendMetric("dispatchToMappingMs", records.mapNotNull { it.dispatchToMappedMs })
            appendMetric("dispatchToHolderMs", records.mapNotNull { it.dispatchToHolderMs })
            appendMetric("dispatchToUiModelMs", records.mapNotNull { it.dispatchToUiModelMs })
            appendLine("dispatchToSenderSampleMs=NOT_CAPTURED")
            appendLine("eventTimeMeaning=Android event timestamp; not physical button actuation time")
        }, StandardCharsets.UTF_8)
    }

    private fun StringBuilder.appendMetric(name: String, values: List<Double>) {
        if (values.isEmpty()) {
            appendLine("$name=NOT_AVAILABLE")
            return
        }
        val sorted = values.sorted()
        val median = percentile(sorted, 0.50)
        val p95 = percentile(sorted, 0.95)
        appendLine(String.format(
            Locale.ROOT,
            "%s min=%.3f median=%.3f p95=%.3f max=%.3f",
            name, sorted.first(), median, p95, sorted.last(),
        ))
    }

    private fun percentile(sorted: List<Double>, percentile: Double): Double {
        val index = (ceil(percentile * sorted.size).toInt() - 1).coerceIn(0, sorted.lastIndex)
        return sorted[index]
    }

    companion object {
        const val DEFAULT_QUEUE_CAPACITY = 512
        const val DEFAULT_MAX_RECORDS = 50_000L
        private val UTC_FILE_FORMAT = ThreadLocal.withInitial {
            SimpleDateFormat("yyyyMMdd-HHmmss", Locale.ROOT).apply {
                timeZone = TimeZone.getTimeZone("UTC")
            }
        }
    }
}

private fun nanosToMillis(value: Long): Double = value / 1_000_000.0
