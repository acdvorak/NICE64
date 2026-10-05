package com.melfegor.nice64.api

import android.view.InputDevice
import android.view.KeyEvent

/** Runtime-only Android metadata. Nothing here is persisted in ControllerProfileV1. */
internal data class AndroidPhysicalButtonDevice(
    val runtimeDeviceId: Int,
    val stableDescriptor: String?,
    val deviceName: String?,
    val aggregateSources: Int,
    val isVirtual: Boolean,
)

/** One platform-normalized physical button edge shared by capture and gameplay. */
internal data class AndroidPhysicalButtonObservation(
    val runtimeDeviceId: Int,
    val stableDescriptor: String?,
    val deviceName: String?,
    val aggregateDeviceSources: Int,
    val deviceIsVirtual: Boolean,
    val eventSource: Int,
    val keyCode: Int,
    val keyName: String,
    val scanCode: Int,
    val action: Int,
    val repeatCount: Int,
    val eventTimeMillis: Long,
    val downTimeMillis: Long,
    val eligible: Boolean = false,
    val eligibility: AndroidPhysicalButtonEligibility = AndroidPhysicalButtonEligibility.INELIGIBLE,
) {
    val pressed: Boolean get() = action == KeyEvent.ACTION_DOWN
    val released: Boolean get() = action == KeyEvent.ACTION_UP
}

internal enum class AndroidPhysicalButtonEligibility {
    DIRECT_CONTROLLER_SOURCE,
    MIXED_CONTROLLER_FALLBACK,
    INELIGIBLE,
}

internal object AndroidPhysicalButtonEligibilityPolicy {
    fun normalize(
        runtimeDeviceId: Int,
        eventSource: Int,
        keyCode: Int,
        keyName: String = "KEYCODE_$keyCode",
        scanCode: Int,
        action: Int,
        repeatCount: Int,
        eventTimeMillis: Long,
        downTimeMillis: Long,
        device: AndroidPhysicalButtonDevice?,
    ): AndroidPhysicalButtonObservation {
        val direct = isControllerSource(eventSource)
        val fallback = !direct && device != null &&
            device.runtimeDeviceId == runtimeDeviceId &&
            !device.isVirtual &&
            isControllerSource(device.aggregateSources) &&
            isExplicitControllerButton(keyCode)
        val eligibility = when {
            direct -> AndroidPhysicalButtonEligibility.DIRECT_CONTROLLER_SOURCE
            fallback -> AndroidPhysicalButtonEligibility.MIXED_CONTROLLER_FALLBACK
            else -> AndroidPhysicalButtonEligibility.INELIGIBLE
        }
        return AndroidPhysicalButtonObservation(
            runtimeDeviceId = runtimeDeviceId,
            stableDescriptor = device?.stableDescriptor,
            deviceName = device?.deviceName,
            aggregateDeviceSources = device?.aggregateSources ?: 0,
            deviceIsVirtual = device?.isVirtual ?: false,
            eventSource = eventSource,
            keyCode = keyCode,
            keyName = keyName,
            scanCode = scanCode,
            action = action,
            repeatCount = repeatCount,
            eventTimeMillis = eventTimeMillis,
            downTimeMillis = downTimeMillis,
            eligible = direct || fallback,
            eligibility = eligibility,
        )
    }

    fun isControllerSource(source: Int): Boolean =
        source and InputDevice.SOURCE_GAMEPAD == InputDevice.SOURCE_GAMEPAD ||
            source and InputDevice.SOURCE_JOYSTICK == InputDevice.SOURCE_JOYSTICK

    /** Bounded Android controller keys only; navigation and ordinary keyboard keys are excluded. */
    fun isExplicitControllerButton(keyCode: Int): Boolean =
        keyCode in KeyEvent.KEYCODE_BUTTON_A..KeyEvent.KEYCODE_BUTTON_MODE ||
            keyCode in KeyEvent.KEYCODE_BUTTON_1..KeyEvent.KEYCODE_BUTTON_16
}

internal sealed interface AndroidPhysicalButtonPipelineResult {
    data class Ready(val observation: AndroidPhysicalButtonObservation) :
        AndroidPhysicalButtonPipelineResult

    data class SuppressedAlias(val observation: AndroidPhysicalButtonObservation) :
        AndroidPhysicalButtonPipelineResult
}

/**
 * Suppresses only the proven preferred BUTTON_n -> trailing same-scan DPAD_CENTER presentation.
 * Preferred numbered edges are never delayed.
 */
internal class AndroidPhysicalButtonAliasCoalescer(
    private val windowMillis: Long = ALIAS_WINDOW_MILLIS,
) {
    private data class EdgeKey(val deviceId: Int, val scanCode: Int, val action: Int)
    private data class PreferredEdge(val eventTimeMillis: Long, val keyCode: Int)

    private val recentPreferred = mutableMapOf<EdgeKey, PreferredEdge>()

    init {
        require(windowMillis > 0)
    }

    @Synchronized
    fun shouldSuppress(observation: AndroidPhysicalButtonObservation): Boolean {
        expire(observation.eventTimeMillis)
        if (observation.action != KeyEvent.ACTION_DOWN &&
            observation.action != KeyEvent.ACTION_UP
        ) return false

        val key = EdgeKey(observation.runtimeDeviceId, observation.scanCode, observation.action)
        if (observation.keyCode in KeyEvent.KEYCODE_BUTTON_1..KeyEvent.KEYCODE_BUTTON_16) {
            if (!observation.eligible || observation.scanCode <= 0) return false
            recentPreferred[key] = PreferredEdge(observation.eventTimeMillis, observation.keyCode)
            return false
        }
        if (observation.keyCode != KeyEvent.KEYCODE_DPAD_CENTER || observation.scanCode <= 0) {
            return false
        }
        val preferred = recentPreferred[key] ?: return false
        val delay = observation.eventTimeMillis - preferred.eventTimeMillis
        return delay in 0..windowMillis
    }

    @Synchronized
    fun clearDevice(runtimeDeviceId: Int) {
        recentPreferred.keys.removeAll { it.deviceId == runtimeDeviceId }
    }

    @Synchronized
    fun clear() {
        recentPreferred.clear()
    }

    private fun expire(nowMillis: Long) {
        recentPreferred.entries.removeAll { (_, edge) ->
            val age = nowMillis - edge.eventTimeMillis
            age < 0 || age > windowMillis
        }
    }

    companion object {
        const val ALIAS_WINDOW_MILLIS = 50L
    }
}

internal class AndroidPhysicalButtonPipeline(
    private val coalescer: AndroidPhysicalButtonAliasCoalescer =
        AndroidPhysicalButtonAliasCoalescer(),
) {
    fun normalize(
        runtimeDeviceId: Int,
        eventSource: Int,
        keyCode: Int,
        keyName: String = "KEYCODE_$keyCode",
        scanCode: Int,
        action: Int,
        repeatCount: Int,
        eventTimeMillis: Long,
        downTimeMillis: Long,
        device: AndroidPhysicalButtonDevice?,
    ): AndroidPhysicalButtonPipelineResult {
        val observation = AndroidPhysicalButtonEligibilityPolicy.normalize(
            runtimeDeviceId, eventSource, keyCode, keyName, scanCode, action, repeatCount,
            eventTimeMillis, downTimeMillis, device,
        )
        return if (coalescer.shouldSuppress(observation)) {
            AndroidPhysicalButtonPipelineResult.SuppressedAlias(observation)
        } else {
            AndroidPhysicalButtonPipelineResult.Ready(observation)
        }
    }

    fun clearDevice(runtimeDeviceId: Int) = coalescer.clearDevice(runtimeDeviceId)
    fun clear() = coalescer.clear()
}
