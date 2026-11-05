package com.melfegor.nice64.api

import android.content.Context
import android.hardware.input.InputManager
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

internal data class BridgeControllerMotionRange(
    val axisCode: Int,
    val axisName: String,
    val minimum: Float,
    val maximum: Float,
    val flat: Float,
    val source: Int,
)

internal data class BridgeControllerDiagnosticDevice(
    val runtimeDeviceId: Int,
    val descriptor: String,
    val name: String,
    val vendorId: Int,
    val productId: Int,
    val sources: Int,
    val hasGamepadSource: Boolean,
    val hasJoystickSource: Boolean,
    val controllerNumber: Int,
    val motionRanges: List<BridgeControllerMotionRange>,
    val isVirtual: Boolean = false,
) {
    val descriptorSuffix: String
        get() = descriptor.trim().ifEmpty { "unavailable" }.takeLast(DESCRIPTOR_SUFFIX_LENGTH)

    companion object {
        private const val DESCRIPTOR_SUFFIX_LENGTH = 16
    }
}

internal data class BridgeControllerButtonDiagnostic(
    val runtimeDeviceId: Int,
    val keyCode: Int,
    val keyName: String,
    val action: String,
    val source: Int,
)

internal data class BridgeControllerAxisDiagnostic(
    val runtimeDeviceId: Int,
    val axisCode: Int,
    val axisName: String,
    val rawValue: Float,
    val normalizedValue: Float,
    val minimum: Float,
    val maximum: Float,
    val flat: Float,
    val source: Int,
)

internal data class BridgeControllerDiagnosticsPresentation(
    val devices: List<BridgeControllerDiagnosticDevice> = emptyList(),
    val activeRuntimeDeviceId: Int? = null,
    val recentButtons: List<BridgeControllerButtonDiagnostic> = emptyList(),
    val currentAxes: List<BridgeControllerAxisDiagnostic> = emptyList(),
) {
    val activeDevice: BridgeControllerDiagnosticDevice?
        get() = devices.firstOrNull { it.runtimeDeviceId == activeRuntimeDeviceId }

    val viewedDevice: BridgeControllerDiagnosticDevice?
        get() = activeDevice ?: devices.firstOrNull()
}

/**
 * Pure bounded state used by the read-only P1 diagnostics. It owns no Android listener and cannot
 * affect semantic controller state.
 */
internal class BridgeControllerDiagnosticsStore(
    initialDevices: List<BridgeControllerDiagnosticDevice> = emptyList(),
) {
    private val mutableState = MutableStateFlow(
        BridgeControllerDiagnosticsPresentation(devices = sorted(initialDevices)),
    )
    val state: StateFlow<BridgeControllerDiagnosticsPresentation> = mutableState.asStateFlow()

    @Synchronized
    fun replaceDevices(devices: List<BridgeControllerDiagnosticDevice>) {
        val ids = devices.mapTo(mutableSetOf()) { it.runtimeDeviceId }
        mutableState.value = mutableState.value.copy(
            devices = sorted(devices),
            activeRuntimeDeviceId = mutableState.value.activeRuntimeDeviceId?.takeIf(ids::contains),
            recentButtons = mutableState.value.recentButtons.filter { it.runtimeDeviceId in ids },
            currentAxes = mutableState.value.currentAxes.filter { it.runtimeDeviceId in ids },
        )
    }

    @Synchronized
    fun upsert(device: BridgeControllerDiagnosticDevice?) {
        if (device == null) return
        val next = mutableState.value.devices
            .filterNot { it.runtimeDeviceId == device.runtimeDeviceId } + device
        mutableState.value = mutableState.value.copy(devices = sorted(next))
    }

    @Synchronized
    fun remove(runtimeDeviceId: Int) {
        mutableState.value = mutableState.value.copy(
            devices = mutableState.value.devices.filterNot {
                it.runtimeDeviceId == runtimeDeviceId
            },
            activeRuntimeDeviceId = mutableState.value.activeRuntimeDeviceId
                ?.takeUnless { it == runtimeDeviceId },
            recentButtons = mutableState.value.recentButtons.filterNot {
                it.runtimeDeviceId == runtimeDeviceId
            },
            currentAxes = mutableState.value.currentAxes.filterNot {
                it.runtimeDeviceId == runtimeDeviceId
            },
        )
    }

    @Synchronized
    fun setActive(runtimeDeviceId: Int?) {
        mutableState.value = mutableState.value.copy(
            activeRuntimeDeviceId = runtimeDeviceId?.takeIf { id ->
                mutableState.value.devices.any { it.runtimeDeviceId == id }
            },
        )
    }

    @Synchronized
    fun observeButton(event: BridgeControllerButtonDiagnostic) {
        mutableState.value = mutableState.value.copy(
            recentButtons = (listOf(event) + mutableState.value.recentButtons)
                .take(MAX_RECENT_BUTTONS),
        )
    }

    @Synchronized
    fun observeAxes(runtimeDeviceId: Int, axes: List<BridgeControllerAxisDiagnostic>) {
        mutableState.value = mutableState.value.copy(
            currentAxes = mutableState.value.currentAxes
                .filterNot { it.runtimeDeviceId == runtimeDeviceId } + axes,
        )
    }

    private companion object {
        const val MAX_RECENT_BUTTONS = 12

        fun sorted(devices: List<BridgeControllerDiagnosticDevice>) = devices.sortedWith(
            compareBy<BridgeControllerDiagnosticDevice>({ it.name.lowercase() }, { it.runtimeDeviceId }),
        )
    }
}

internal interface BridgeControllerDeviceSource {
    fun inventory(): List<BridgeControllerDiagnosticDevice>
    fun descriptor(runtimeDeviceId: Int): BridgeControllerDiagnosticDevice?
    fun register(listener: InputManager.InputDeviceListener)
    fun unregister(listener: InputManager.InputDeviceListener)
}

internal class AndroidBridgeControllerDeviceSource(context: Context) :
    BridgeControllerDeviceSource {
    private val inputManager = context.applicationContext
        .getSystemService(InputManager::class.java)

    override fun inventory(): List<BridgeControllerDiagnosticDevice> =
        inputManager.inputDeviceIds.map(::descriptor).filterNotNull()

    override fun descriptor(runtimeDeviceId: Int): BridgeControllerDiagnosticDevice? =
        inputManager.getInputDevice(runtimeDeviceId)
            ?.takeIf { BridgeControllerDeviceProjection.isEligibleSource(it.sources) }
            ?.let(BridgeControllerDeviceProjection::fromInputDevice)

    override fun register(listener: InputManager.InputDeviceListener) {
        inputManager.registerInputDeviceListener(listener, null)
    }

    override fun unregister(listener: InputManager.InputDeviceListener) {
        inputManager.unregisterInputDeviceListener(listener)
    }
}

internal object BridgeControllerDeviceProjection {
    val relevantAxes = intArrayOf(
        MotionEvent.AXIS_X,
        MotionEvent.AXIS_Y,
        MotionEvent.AXIS_Z,
        MotionEvent.AXIS_RZ,
        MotionEvent.AXIS_RX,
        MotionEvent.AXIS_RY,
        MotionEvent.AXIS_LTRIGGER,
        MotionEvent.AXIS_RTRIGGER,
        MotionEvent.AXIS_BRAKE,
        MotionEvent.AXIS_GAS,
        MotionEvent.AXIS_HAT_X,
        MotionEvent.AXIS_HAT_Y,
    )

    fun isEligibleSource(sources: Int): Boolean =
        sources and InputDevice.SOURCE_GAMEPAD == InputDevice.SOURCE_GAMEPAD ||
            sources and InputDevice.SOURCE_JOYSTICK == InputDevice.SOURCE_JOYSTICK

    fun fromInputDevice(device: InputDevice): BridgeControllerDiagnosticDevice {
        val relevant = relevantAxes.toSet()
        return BridgeControllerDiagnosticDevice(
            runtimeDeviceId = device.id,
            descriptor = device.descriptor.orEmpty(),
            name = device.name?.trim()?.takeIf(String::isNotEmpty)?.take(100)
                ?: "Android gamepad",
            vendorId = device.vendorId,
            productId = device.productId,
            sources = device.sources,
            hasGamepadSource =
                device.sources and InputDevice.SOURCE_GAMEPAD == InputDevice.SOURCE_GAMEPAD,
            hasJoystickSource =
                device.sources and InputDevice.SOURCE_JOYSTICK == InputDevice.SOURCE_JOYSTICK,
            controllerNumber = device.controllerNumber,
            motionRanges = device.motionRanges
                .filter { it.axis in relevant }
                .map {
                    BridgeControllerMotionRange(
                        axisCode = it.axis,
                        axisName = MotionEvent.axisToString(it.axis),
                        minimum = it.min,
                        maximum = it.max,
                        flat = it.flat,
                        source = it.source,
                    )
                }
                .sortedBy { it.axisCode },
            isVirtual = device.isVirtual,
        )
    }

    fun keyDiagnostic(event: KeyEvent) = BridgeControllerButtonDiagnostic(
        runtimeDeviceId = event.deviceId,
        keyCode = event.keyCode,
        keyName = KeyEvent.keyCodeToString(event.keyCode),
        action = when (event.action) {
            KeyEvent.ACTION_DOWN -> "DOWN"
            KeyEvent.ACTION_UP -> "UP"
            else -> "OTHER"
        },
        source = event.source,
    )

    fun keyDiagnostic(event: AndroidPhysicalButtonObservation) =
        BridgeControllerButtonDiagnostic(
            runtimeDeviceId = event.runtimeDeviceId,
            keyCode = event.keyCode,
            keyName = event.keyName,
            action = when (event.action) {
                KeyEvent.ACTION_DOWN -> "DOWN"
                KeyEvent.ACTION_UP -> "UP"
                else -> "OTHER"
            },
            source = event.eventSource,
        )

    fun axisDiagnostics(
        event: MotionEvent,
        device: InputDevice,
    ): List<BridgeControllerAxisDiagnostic> = relevantAxes.map { axis ->
        val range = device.getMotionRange(axis, event.source) ?: device.getMotionRange(axis)
            ?: return@map null
        val value = event.getAxisValue(axis)
        BridgeControllerAxisDiagnostic(
            runtimeDeviceId = event.deviceId,
            axisCode = axis,
            axisName = MotionEvent.axisToString(axis),
            rawValue = value,
            normalizedValue = normalize(value, range.min, range.max),
            minimum = range.min,
            maximum = range.max,
            flat = range.flat,
            source = range.source,
        )
    }.filterNotNull()

    internal fun normalize(value: Float, minimum: Float, maximum: Float): Float {
        val span = maximum - minimum
        if (span <= 0f) return 0f
        val midpoint = minimum + span / 2f
        val halfSpan = span / 2f
        return ((value - midpoint) / halfSpan).coerceIn(-1f, 1f)
    }
}
