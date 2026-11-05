package com.melfegor.nice64.api

import android.content.Context
import android.hardware.input.InputManager
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import com.melfegor.nice64.core.input.N64AnalogScaling
import com.melfegor.nice64.core.input.N64InputComposer
import com.melfegor.nice64.core.input.N64InputSource
import com.melfegor.nice64.core.model.N64ButtonMask
import com.melfegor.nice64.core.model.N64ControllerState
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlin.math.max

data class BridgeControllerInputPresentation(
    val enabled: Boolean = false,
    val inputMode: MobileInputMode = MobileInputMode.ON_SCREEN,
    val sourceLabel: String = "None",
    val gameplayMappingLabel: String = "Standard Android Gamepad",
    val gameplayMappingReason: String? = null,
)

enum class MobileInputMode {
    ON_SCREEN,
    CONTROLLER,
}

internal object AndroidGamepadMapping {
    fun buttonMask(keyCode: Int): UInt? = when (keyCode) {
        KeyEvent.KEYCODE_BUTTON_A -> N64ButtonMask.A
        KeyEvent.KEYCODE_BUTTON_B -> N64ButtonMask.B
        KeyEvent.KEYCODE_BUTTON_X -> N64ButtonMask.C_LEFT
        KeyEvent.KEYCODE_BUTTON_Y -> N64ButtonMask.C_UP
        KeyEvent.KEYCODE_BUTTON_L1 -> N64ButtonMask.L
        KeyEvent.KEYCODE_BUTTON_R1 -> N64ButtonMask.R
        KeyEvent.KEYCODE_BUTTON_L2, KeyEvent.KEYCODE_BUTTON_R2 -> N64ButtonMask.Z
        KeyEvent.KEYCODE_BUTTON_START -> N64ButtonMask.START
        KeyEvent.KEYCODE_DPAD_UP -> N64ButtonMask.DPAD_UP
        KeyEvent.KEYCODE_DPAD_DOWN -> N64ButtonMask.DPAD_DOWN
        KeyEvent.KEYCODE_DPAD_LEFT -> N64ButtonMask.DPAD_LEFT
        KeyEvent.KEYCODE_DPAD_RIGHT -> N64ButtonMask.DPAD_RIGHT
        else -> null
    }

    fun rightStickButtons(x: Int, y: Int, threshold: Int = 64): UInt {
        var result = 0u
        if (x <= -threshold) result = result or N64ButtonMask.C_LEFT
        if (x >= threshold) result = result or N64ButtonMask.C_RIGHT
        if (y >= threshold) result = result or N64ButtonMask.C_UP
        if (y <= -threshold) result = result or N64ButtonMask.C_DOWN
        return result
    }

    fun semanticTarget(keyCode: Int): String? = when (keyCode) {
        KeyEvent.KEYCODE_BUTTON_A -> "A"
        KeyEvent.KEYCODE_BUTTON_B -> "B"
        KeyEvent.KEYCODE_BUTTON_X -> "C_LEFT"
        KeyEvent.KEYCODE_BUTTON_Y -> "C_UP"
        KeyEvent.KEYCODE_BUTTON_L1 -> "L"
        KeyEvent.KEYCODE_BUTTON_R1 -> "R"
        KeyEvent.KEYCODE_BUTTON_L2, KeyEvent.KEYCODE_BUTTON_R2 -> "Z"
        KeyEvent.KEYCODE_BUTTON_START -> "START"
        KeyEvent.KEYCODE_DPAD_UP -> "DPAD_UP"
        KeyEvent.KEYCODE_DPAD_DOWN -> "DPAD_DOWN"
        KeyEvent.KEYCODE_DPAD_LEFT -> "DPAD_LEFT"
        KeyEvent.KEYCODE_DPAD_RIGHT -> "DPAD_RIGHT"
        else -> null
    }
}

/**
 * Owns Android human-input contributions only. It publishes one complete semantic state into the
 * holder sampled by D8I; it never performs network I/O and maintains no event queue.
 */
internal class BridgeControllerHumanInputCoordinator(
    private val holder: BridgeLatestControllerStateHolder,
    private val deviceSource: BridgeControllerDeviceSource,
    private val liveProfiles: LiveControllerProfileCache? = null,
    private val buttonPipeline: AndroidPhysicalButtonPipeline = AndroidPhysicalButtonPipeline(),
) : InputManager.InputDeviceListener, AutoCloseable {
    private val composer = N64InputComposer()
    private val diagnosticsStore = BridgeControllerDiagnosticsStore(deviceSource.inventory())
    private val mutablePresentation = MutableStateFlow(BridgeControllerInputPresentation())
    val presentation: StateFlow<BridgeControllerInputPresentation> =
        mutablePresentation.asStateFlow()
    val diagnostics: StateFlow<BridgeControllerDiagnosticsPresentation> = diagnosticsStore.state

    private var enabled = false
    private var inputMode = MobileInputMode.ON_SCREEN
    private var activeGamepadId: Int? = null
    private var activeGamepadName: String? = null
    private var gamepadKeyButtons = 0u
    private var gamepadAxisButtons = 0u
    private var gamepadX = 0
    private var gamepadY = 0
    private var activeGamepadDevice: BridgeControllerDiagnosticDevice? = null
    private var liveMapping: LiveControllerMappingV1 =
        LiveControllerMappingV1.StandardAndroidGamepad("No active eligible controller.")
    private val customRuntime = LiveCustomControllerRuntime()
    private var physicalCaptureSuppressed = false
    private var closed = false

    constructor(
        context: Context,
        holder: BridgeLatestControllerStateHolder,
        liveProfiles: LiveControllerProfileCache? = null,
    ) : this(holder, AndroidBridgeControllerDeviceSource(context), liveProfiles)

    init {
        deviceSource.register(this)
    }

    @Synchronized
    fun setEnabled(value: Boolean) {
        enabled = value
        if (!value) {
            buttonPipeline.clear()
            resetGamepadFields(clearIdentity = true)
        }
        publish(composer.setEnabled(value))
    }

    /** Neutral, synchronous ownership handoff. The selected mode is process-local only. */
    @Synchronized
    fun setInputMode(value: MobileInputMode) {
        if (inputMode == value) return
        publish(composer.clearCurrent())
        buttonPipeline.clear()
        resetGamepadFields(clearIdentity = true)
        inputMode = value
        publish(composer.setActiveSource(value.toN64InputSource()))
    }

    @Synchronized
    fun inputMode(): MobileInputMode = inputMode

    @Synchronized
    fun setTouchButton(mask: UInt, pressed: Boolean) {
        if (!enabled || inputMode != MobileInputMode.ON_SCREEN) return
        val current = composer.touchContribution().buttons
        publish(composer.setTouchButtons(if (pressed) current or mask else current and mask.inv()))
    }

    @Synchronized
    fun setTouchAnalog(x: Int, y: Int) {
        if (!enabled || inputMode != MobileInputMode.ON_SCREEN) return
        publish(composer.setTouchAnalog(x, y))
    }

    @Synchronized
    fun clearTouch() {
        publish(composer.clearTouch())
    }

    @Synchronized
    fun neutralize() {
        enabled = false
        buttonPipeline.clear()
        resetGamepadFields(clearIdentity = true)
        publish(composer.setEnabled(false))
    }

    /** Clears and gates only the physical gamepad contribution while P4B owns capture. */
    @Synchronized
    fun setPhysicalCaptureSuppressed(value: Boolean) {
        if (physicalCaptureSuppressed == value) return
        physicalCaptureSuppressed = value
        buttonPipeline.clear()
        if (value) {
            resetGamepadFields(clearIdentity = true)
            publish(composer.clearGamepad())
        }
    }

    /** Applies a freshly cached repository snapshot without doing repository I/O on this thread. */
    @Synchronized
    fun reconcileLiveMapping() {
        val next = resolveMapping(activeGamepadDevice)
        if (next == liveMapping) {
            publish(composer.current())
            return
        }
        changeLiveMapping(next)
        publish(composer.clearGamepad())
    }

    @Synchronized
    fun normalizeKeyEvent(event: KeyEvent): AndroidPhysicalButtonPipelineResult {
        val projected = deviceSource.descriptor(event.deviceId)
        val androidDevice = event.device?.takeIf { it.id == event.deviceId }
        val device = when {
            projected != null -> AndroidPhysicalButtonDevice(
                projected.runtimeDeviceId,
                projected.descriptor.takeIf(String::isNotBlank),
                projected.name,
                projected.sources,
                projected.isVirtual,
            )
            androidDevice != null -> AndroidPhysicalButtonDevice(
                androidDevice.id,
                androidDevice.descriptor?.takeIf(String::isNotBlank),
                androidDevice.name,
                androidDevice.sources,
                androidDevice.isVirtual,
            )
            else -> null
        }
        return buttonPipeline.normalize(
            runtimeDeviceId = event.deviceId,
            eventSource = event.source,
            keyCode = event.keyCode,
            keyName = KeyEvent.keyCodeToString(event.keyCode),
            scanCode = event.scanCode,
            action = event.action,
            repeatCount = event.repeatCount,
            eventTimeMillis = event.eventTime,
            downTimeMillis = event.downTime,
            device = device,
        )
    }

    @Synchronized
    fun handleKeyEvent(event: KeyEvent): Boolean = when (val normalized = normalizeKeyEvent(event)) {
        is AndroidPhysicalButtonPipelineResult.SuppressedAlias -> true
        is AndroidPhysicalButtonPipelineResult.Ready -> handleKeyEvent(normalized.observation)
    }

    @Synchronized
    fun handleKeyEvent(
        event: AndroidPhysicalButtonObservation,
        trace: ControllerInputTraceEdge? = null,
    ): Boolean {
        if (!event.eligible) return false
        if (!event.pressed && !event.released) return false
        diagnosticsStore.observeButton(BridgeControllerDeviceProjection.keyDiagnostic(event))
        if (physicalCaptureSuppressed) return true
        if (!enabled || inputMode != MobileInputMode.CONTROLLER) return false
        if (!activateDevice(event.runtimeDeviceId, event.deviceName)) return false
        return when (liveMapping) {
            is LiveControllerMappingV1.Custom -> {
                if (!customRuntime.handlesKey(event.keyCode)) return false
                if (event.pressed && event.repeatCount > 0) return true
                val custom = liveMapping as LiveControllerMappingV1.Custom
                val target = custom.profile.buttonBindings
                    .firstOrNull { it.keyCode == event.keyCode }
                    ?.target?.name ?: "CUSTOM_BUTTON"
                trace?.mapped(target, "custom:${custom.profile.profileId}")
                val contribution = customRuntime.updateKey(
                    event.keyCode,
                    event.pressed,
                )
                trace?.semanticHandlerInvoked()
                publish(composer.setGamepad(
                    contribution.buttons,
                    contribution.analogX,
                    contribution.analogY,
                ), trace)
                true
            }
            is LiveControllerMappingV1.StandardAndroidGamepad -> {
                val mask = AndroidGamepadMapping.buttonMask(event.keyCode) ?: return false
                trace?.mapped(
                    AndroidGamepadMapping.semanticTarget(event.keyCode) ?: "STANDARD_BUTTON",
                    "standard-android-gamepad",
                )
                gamepadKeyButtons = if (event.pressed) {
                    gamepadKeyButtons or mask
                } else {
                    gamepadKeyButtons and mask.inv()
                }
                trace?.semanticHandlerInvoked()
                publishGamepad(trace)
                true
            }
            LiveControllerMappingV1.Ineligible -> false
        }
    }

    @Synchronized
    fun handleMotionEvent(event: MotionEvent): Boolean {
        if (!isGamepadSource(event.source) || event.action != MotionEvent.ACTION_MOVE) {
            return false
        }
        val device = event.device ?: return false
        diagnosticsStore.observeAxes(
            event.deviceId,
            BridgeControllerDeviceProjection.axisDiagnostics(event, device),
        )
        if (physicalCaptureSuppressed) return true
        if (!enabled || inputMode != MobileInputMode.CONTROLLER) return false
        if (!activateDevice(event.deviceId, device.name)) return false
        when (liveMapping) {
            is LiveControllerMappingV1.Custom -> {
                val values = activeGamepadDevice?.motionRanges.orEmpty().associate { range ->
                    range.axisCode to event.getAxisValue(range.axisCode)
                }
                val contribution = customRuntime.updateAxes(values)
                publish(composer.setGamepad(
                    contribution.buttons,
                    contribution.analogX,
                    contribution.analogY,
                ))
            }
            is LiveControllerMappingV1.StandardAndroidGamepad -> {
                gamepadX = scaledAxis(event, device, MotionEvent.AXIS_X)
                // Android joystick Y is positive down; the N64 semantic axis is positive up.
                gamepadY = -scaledAxis(event, device, MotionEvent.AXIS_Y)
                val rightX = scaledPreferredAxis(event, device, MotionEvent.AXIS_Z, MotionEvent.AXIS_RX)
                val rightY = -scaledPreferredAxis(event, device, MotionEvent.AXIS_RZ, MotionEvent.AXIS_RY)
                gamepadAxisButtons = AndroidGamepadMapping.rightStickButtons(rightX, rightY)
                val leftTrigger = normalizedTrigger(event, device, MotionEvent.AXIS_LTRIGGER)
                val rightTrigger = normalizedTrigger(event, device, MotionEvent.AXIS_RTRIGGER)
                if (max(leftTrigger, rightTrigger) >= 0.5f) {
                    gamepadAxisButtons = gamepadAxisButtons or N64ButtonMask.Z
                }
                publishGamepad()
            }
            LiveControllerMappingV1.Ineligible -> return false
        }
        return true
    }

    @Synchronized
    override fun onInputDeviceRemoved(deviceId: Int) {
        buttonPipeline.clearDevice(deviceId)
        diagnosticsStore.remove(deviceId)
        if (activeGamepadId == deviceId) {
            resetGamepadFields(clearIdentity = true)
            publish(composer.clearGamepad())
        }
    }

    override fun onInputDeviceAdded(deviceId: Int) {
        diagnosticsStore.upsert(deviceSource.descriptor(deviceId))
    }

    override fun onInputDeviceChanged(deviceId: Int) {
        buttonPipeline.clearDevice(deviceId)
        val refreshed = deviceSource.descriptor(deviceId)
        if (refreshed == null) {
            diagnosticsStore.remove(deviceId)
            synchronized(this) {
                if (activeGamepadId == deviceId) {
                    resetGamepadFields(clearIdentity = true)
                    publish(composer.clearGamepad())
                }
            }
        } else {
            diagnosticsStore.upsert(refreshed)
            synchronized(this) {
                if (activeGamepadId == deviceId) {
                    activeGamepadDevice = refreshed
                    changeLiveMapping(resolveMapping(refreshed), forceReset = true)
                    publish(composer.clearGamepad())
                }
            }
        }
    }

    @Synchronized
    override fun close() {
        if (closed) return
        closed = true
        buttonPipeline.clear()
        neutralize()
        deviceSource.unregister(this)
    }

    private fun activateDevice(deviceId: Int, name: String?): Boolean {
        if (activeGamepadId == deviceId) return true
        val device = deviceSource.descriptor(deviceId)
        if (device == null && liveProfiles != null) return false
        if (activeGamepadId != null && activeGamepadId != deviceId) {
            resetGamepadFields(clearIdentity = true)
            publish(composer.clearGamepad())
        }
        activeGamepadId = deviceId
        activeGamepadName = name?.trim()?.takeIf(String::isNotEmpty)?.take(80) ?: "Android gamepad"
        activeGamepadDevice = device
        diagnosticsStore.setActive(deviceId)
        val next = resolveMapping(device)
        if (next != liveMapping) {
            changeLiveMapping(next)
            publish(composer.clearGamepad())
        } else if (next is LiveControllerMappingV1.Custom && device != null) {
            customRuntime.activate(next.profile, device.toControllerCapabilitiesV1())
        }
        return true
    }

    private fun resolveMapping(device: BridgeControllerDiagnosticDevice?): LiveControllerMappingV1 =
        liveProfiles?.resolve(device) ?: LiveControllerMappingV1.StandardAndroidGamepad()

    private fun changeLiveMapping(next: LiveControllerMappingV1, forceReset: Boolean = false) {
        if (!forceReset && next == liveMapping) return
        resetPhysicalState()
        liveMapping = next
        if (next is LiveControllerMappingV1.Custom && activeGamepadDevice != null) {
            customRuntime.activate(next.profile, activeGamepadDevice!!.toControllerCapabilitiesV1())
        }
    }

    private fun publishGamepad(trace: ControllerInputTraceEdge? = null) {
        publish(composer.setGamepad(
            gamepadKeyButtons or gamepadAxisButtons,
            gamepadX,
            gamepadY,
        ), trace)
    }

    private fun publish(
        state: N64ControllerState,
        trace: ControllerInputTraceEdge? = null,
    ) {
        holder.update(state)
        trace?.holderUpdated(state)
        val gamepadActive = !composer.gamepadContribution().isNeutral
        mutablePresentation.value = BridgeControllerInputPresentation(
            enabled = enabled,
            inputMode = inputMode,
            sourceLabel = when {
                !enabled -> "None"
                inputMode == MobileInputMode.ON_SCREEN -> "On-Screen"
                gamepadActive -> "Controller: ${activeGamepadName ?: "Android gamepad"}"
                activeGamepadId != null -> "Controller: ${activeGamepadName ?: "Android gamepad"}"
                diagnosticsStore.state.value.devices.isEmpty() -> "Controller: Disconnected"
                else -> "Controller: Waiting for input"
            },
            gameplayMappingLabel = liveMapping.displayLabel,
            gameplayMappingReason = liveMapping.displayReason,
        )
        trace?.complete()
    }

    private fun resetGamepadFields(clearIdentity: Boolean) {
        resetPhysicalState()
        if (clearIdentity) {
            activeGamepadId = null
            activeGamepadName = null
            activeGamepadDevice = null
            liveMapping = LiveControllerMappingV1.StandardAndroidGamepad("No active eligible controller.")
            diagnosticsStore.setActive(null)
        }
    }

    private fun resetPhysicalState() {
        gamepadKeyButtons = 0u
        gamepadAxisButtons = 0u
        gamepadX = 0
        gamepadY = 0
        customRuntime.deactivate()
    }

    private fun scaledAxis(event: MotionEvent, device: InputDevice, axis: Int): Int {
        val range = device.getMotionRange(axis, event.source)
            ?: device.getMotionRange(axis)
            ?: return 0
        val extent = max(kotlin.math.abs(range.min), kotlin.math.abs(range.max))
            .takeIf { it > 0f } ?: 1f
        val normalized = (event.getAxisValue(axis) / extent).coerceIn(-1f, 1f)
        val flat = (range.flat / extent).coerceIn(0f, 0.95f)
            .takeIf { it > 0f } ?: N64AnalogScaling.DEFAULT_DEADZONE
        return N64AnalogScaling.scaledAxis(normalized, flat)
    }

    private fun scaledPreferredAxis(
        event: MotionEvent,
        device: InputDevice,
        primary: Int,
        fallback: Int,
    ): Int = if (device.getMotionRange(primary, event.source) != null ||
        device.getMotionRange(primary) != null
    ) scaledAxis(event, device, primary) else scaledAxis(event, device, fallback)

    private fun normalizedTrigger(event: MotionEvent, device: InputDevice, axis: Int): Float {
        val range = device.getMotionRange(axis, event.source) ?: device.getMotionRange(axis)
            ?: return 0f
        val span = range.max - range.min
        return if (span <= 0f) 0f else ((event.getAxisValue(axis) - range.min) / span).coerceIn(0f, 1f)
    }

    companion object {
        internal fun isGamepadSource(source: Int): Boolean =
            AndroidPhysicalButtonEligibilityPolicy.isControllerSource(source)
    }
}

private fun MobileInputMode.toN64InputSource(): N64InputSource = when (this) {
    MobileInputMode.ON_SCREEN -> N64InputSource.TOUCH
    MobileInputMode.CONTROLLER -> N64InputSource.GAMEPAD
}
