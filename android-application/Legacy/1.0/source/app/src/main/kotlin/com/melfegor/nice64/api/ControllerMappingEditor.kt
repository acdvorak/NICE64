package com.melfegor.nice64.api

import android.view.KeyEvent
import com.melfegor.nice64.core.input.AxisBindingAlternativeV1
import com.melfegor.nice64.core.input.AxisBindingV1
import com.melfegor.nice64.core.input.AxisDirectionV1
import com.melfegor.nice64.core.input.ButtonBindingV1
import com.melfegor.nice64.core.input.ControllerIdentityV1
import com.melfegor.nice64.core.input.ControllerProfileV1
import com.melfegor.nice64.core.input.ControllerProfileValidatorV1
import com.melfegor.nice64.core.input.N64SemanticTargetV1
import kotlin.math.abs
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

internal enum class MappingCapturePhase {
    IDLE,
    WAITING_FOR_BUTTON,
    WAITING_FOR_AXIS,
    WAITING_FOR_ANALOG_PAIR_X,
    WAITING_FOR_ANALOG_PAIR_Y,
    CANDIDATE_READY,
    CONFLICT,
    SAVING,
    ERROR,
}

internal sealed interface MappingCaptureCandidate {
    data class Button(val keyCode: Int, val target: N64SemanticTargetV1) : MappingCaptureCandidate
    data class Axis(val binding: AxisBindingV1) : MappingCaptureCandidate
    data class AnalogPair(val xAxisCode: Int, val yAxisCode: Int) : MappingCaptureCandidate
}

internal data class MappingReconnectCandidate(
    val runtimeDeviceId: Int,
    val deviceName: String,
)

internal data class MappingEditorState(
    val active: Boolean = false,
    val original: ControllerProfileV1? = null,
    val working: ControllerProfileV1? = null,
    val managementRuntimeDeviceId: Int? = null,
    val managementDeviceName: String? = null,
    val deviceAvailable: Boolean = false,
    val reconnectCandidate: MappingReconnectCandidate? = null,
    val reconnectAmbiguous: Boolean = false,
    val phase: MappingCapturePhase = MappingCapturePhase.IDLE,
    val captureTarget: N64SemanticTargetV1? = null,
    val candidate: MappingCaptureCandidate? = null,
    val conflicts: List<String> = emptyList(),
    val dirty: Boolean = false,
    val validationErrors: List<String> = emptyList(),
    val errorMessage: String? = null,
    val statusMessage: String? = null,
    val discardConfirmation: Boolean = false,
    val restoreConfirmation: Boolean = false,
) {
    val captureActive: Boolean get() = phase in setOf(
        MappingCapturePhase.WAITING_FOR_BUTTON,
        MappingCapturePhase.WAITING_FOR_AXIS,
        MappingCapturePhase.WAITING_FOR_ANALOG_PAIR_X,
        MappingCapturePhase.WAITING_FOR_ANALOG_PAIR_Y,
    )
    val canSave: Boolean get() = active && dirty && validationErrors.isEmpty() &&
        phase !in setOf(MappingCapturePhase.SAVING) && !captureActive
}

internal data class MappingCaptureKeyInput(
    val deviceId: Int,
    val eligibleSource: Boolean,
    val down: Boolean,
    val up: Boolean,
    val repeatCount: Int,
    val keyCode: Int,
)

internal data class MappingCaptureAxisInput(
    val deviceId: Int,
    val eligibleSource: Boolean,
    val values: Map<Int, Float>,
)

internal fun shouldPreDispatchMappingCaptureKey(
    state: MappingEditorState,
    deviceId: Int,
    eligibleSource: Boolean,
): Boolean = state.captureActive && eligibleSource && deviceId == state.managementRuntimeDeviceId

private fun isDpadKeyCode(keyCode: Int): Boolean = keyCode in setOf(
    KeyEvent.KEYCODE_DPAD_UP,
    KeyEvent.KEYCODE_DPAD_DOWN,
    KeyEvent.KEYCODE_DPAD_LEFT,
    KeyEvent.KEYCODE_DPAD_RIGHT,
)

internal class ControllerMappingEditorCoordinator(
    private val repository: ControllerProfileUiRepository,
    private val suppressPhysicalGameplay: (Boolean) -> Unit = {},
) {
    private val mutableState = MutableStateFlow(MappingEditorState())
    val state: StateFlow<MappingEditorState> = mutableState.asStateFlow()
    private var device: BridgeControllerDiagnosticDevice? = null
    private var managementStableIdentity: ControllerIdentityV1? = null
    private var latestDevices: List<BridgeControllerDiagnosticDevice> = emptyList()
    private var pairXAxis: Int? = null
    private var pendingDpadReleaseKeyCode: Int? = null
    private var suppression = false

    suspend fun open(profileId: String, managementDevice: BridgeControllerDiagnosticDevice?) {
        stopCapture()
        clearPendingDpadRelease()
        if (profileId == StandardAndroidGamepadProfile.profile.profileId) {
            mutableState.value = MappingEditorState(errorMessage = "The built-in profile is read-only.")
            return
        }
        try {
            val snapshot = repository.load()
            val profile = snapshot.profiles.singleOrNull { it.profileId == profileId }
                ?: throw ControllerProfileUiRepositoryException("The controller profile no longer exists.")
            device = managementDevice
            managementStableIdentity = managementDevice?.toStableIdentity()
            latestDevices = listOfNotNull(managementDevice)
            pairXAxis = null
            mutableState.value = validated(
                MappingEditorState(
                    active = true,
                    original = profile,
                    working = profile,
                    managementRuntimeDeviceId = managementDevice?.runtimeDeviceId,
                    managementDeviceName = managementDevice?.name,
                    deviceAvailable = managementDevice != null,
                ),
            )
        } catch (failure: ControllerProfileUiRepositoryException) {
            mutableState.value = MappingEditorState(errorMessage = safeMessage(failure))
        }
    }

    fun updateDevices(diagnostics: BridgeControllerDiagnosticsPresentation) {
        val current = mutableState.value
        if (!current.active) return
        latestDevices = diagnostics.devices
        val matched = current.managementRuntimeDeviceId?.let { id ->
            diagnostics.devices.firstOrNull { it.runtimeDeviceId == id }
        }
        val stableMatches = if (matched == null) {
            managementStableIdentity?.let { expected ->
                diagnostics.devices.filter { it.toStableIdentity() == expected }
            }.orEmpty()
        } else emptyList()
        val reconnectCandidate = stableMatches.singleOrNull()?.let {
            MappingReconnectCandidate(it.runtimeDeviceId, it.name)
        }
        device = matched
        if (matched == null && current.captureActive) {
            stopCapture()
            clearPendingDpadRelease()
            mutableState.value = mutableState.value.copy(
                deviceAvailable = false,
                phase = MappingCapturePhase.ERROR,
                errorMessage = "The selected controller disconnected; capture was cancelled.",
                candidate = null,
                conflicts = emptyList(),
                reconnectCandidate = reconnectCandidate,
                reconnectAmbiguous = stableMatches.size > 1,
            )
        } else {
            mutableState.value = current.copy(
                deviceAvailable = matched != null,
                managementDeviceName = matched?.name ?: current.managementDeviceName,
                reconnectCandidate = reconnectCandidate,
                reconnectAmbiguous = stableMatches.size > 1,
            )
        }
    }

    fun recoverWithReconnectedController(runtimeDeviceId: Int): Boolean {
        val current = mutableState.value
        val expected = managementStableIdentity ?: return fail("The original controller identity is unavailable.")
        val stableMatches = latestDevices.filter { it.toStableIdentity() == expected }
        val selected = stableMatches.singleOrNull { it.runtimeDeviceId == runtimeDeviceId }
        if (stableMatches.size != 1 || selected == null ||
            current.reconnectCandidate?.runtimeDeviceId != runtimeDeviceId
        ) {
            return fail("The reconnected controller could not be verified uniquely.")
        }
        stopCapture()
        clearPendingDpadRelease()
        pairXAxis = null
        device = selected
        mutableState.value = current.copy(
            managementRuntimeDeviceId = selected.runtimeDeviceId,
            managementDeviceName = selected.name,
            deviceAvailable = true,
            reconnectCandidate = null,
            reconnectAmbiguous = false,
            phase = MappingCapturePhase.IDLE,
            captureTarget = null,
            candidate = null,
            conflicts = emptyList(),
            errorMessage = null,
            statusMessage = "Reconnected controller selected for mapping capture.",
        )
        return true
    }

    fun beginButtonCapture(target: N64SemanticTargetV1): Boolean {
        if (target.isAnalog) return fail("Analog targets require axis capture.")
        return beginCapture(MappingCapturePhase.WAITING_FOR_BUTTON, target)
    }

    fun beginAxisCapture(target: N64SemanticTargetV1): Boolean =
        beginCapture(MappingCapturePhase.WAITING_FOR_AXIS, target)

    fun beginAnalogPairCapture(): Boolean {
        pairXAxis = null
        return beginCapture(MappingCapturePhase.WAITING_FOR_ANALOG_PAIR_X, N64SemanticTargetV1.ANALOG_X)
    }

    fun handleKey(input: MappingCaptureKeyInput): Boolean {
        val current = mutableState.value
        if (!current.captureActive) {
            if (input.up && input.eligibleSource &&
                input.deviceId == current.managementRuntimeDeviceId &&
                input.keyCode == pendingDpadReleaseKeyCode
            ) {
                clearPendingDpadRelease()
                return true
            }
            return false
        }
        if (current.phase != MappingCapturePhase.WAITING_FOR_BUTTON ||
            input.deviceId != current.managementRuntimeDeviceId || !input.eligibleSource ||
            input.repeatCount != 0
        ) return true
        val normalDown = input.down
        val dpadUpFallback = input.up && isDpadKeyCode(input.keyCode) &&
            pendingDpadReleaseKeyCode != input.keyCode
        if (!normalDown && !dpadUpFallback) return true
        val target = current.captureTarget ?: return true
        pendingDpadReleaseKeyCode = input.keyCode.takeIf { normalDown && isDpadKeyCode(it) }
        prepareCandidate(MappingCaptureCandidate.Button(input.keyCode, target), listOf(target))
        return true
    }

    fun shouldPreDispatchKey(input: MappingCaptureKeyInput): Boolean {
        val current = mutableState.value
        if (shouldPreDispatchMappingCaptureKey(current, input.deviceId, input.eligibleSource)) return true
        return input.up && input.eligibleSource &&
            input.deviceId == current.managementRuntimeDeviceId &&
            input.keyCode == pendingDpadReleaseKeyCode
    }

    fun handleAxes(input: MappingCaptureAxisInput): Boolean {
        val current = mutableState.value
        if (!current.captureActive) return false
        if (input.deviceId != current.managementRuntimeDeviceId || !input.eligibleSource) return true
        val selectedDevice = device ?: return true
        val excluded = if (current.phase == MappingCapturePhase.WAITING_FOR_ANALOG_PAIR_Y) setOfNotNull(pairXAxis)
        else emptySet()
        val moved = dominantAxis(selectedDevice, input.values, excluded) ?: return true
        when (current.phase) {
            MappingCapturePhase.WAITING_FOR_AXIS -> {
                val target = current.captureTarget ?: return true
                val binding = if (target.isAnalog) {
                    AxisBindingV1(
                        moved.first,
                        AxisDirectionV1.BOTH,
                        target,
                        inverted = target == N64SemanticTargetV1.ANALOG_Y,
                    )
                } else {
                    AxisBindingV1(
                        moved.first,
                        moved.second,
                        target,
                        pressThreshold = 0.5f,
                        releaseThreshold = 0.45f,
                    )
                }
                prepareCandidate(MappingCaptureCandidate.Axis(binding), listOf(target))
            }
            MappingCapturePhase.WAITING_FOR_ANALOG_PAIR_X -> {
                pairXAxis = moved.first
                mutableState.value = current.copy(
                    phase = MappingCapturePhase.WAITING_FOR_ANALOG_PAIR_Y,
                    captureTarget = N64SemanticTargetV1.ANALOG_Y,
                    statusMessage = "X axis captured. Move the desired Y axis.",
                )
            }
            MappingCapturePhase.WAITING_FOR_ANALOG_PAIR_Y -> {
                val x = pairXAxis ?: return true
                prepareCandidate(
                    MappingCaptureCandidate.AnalogPair(x, moved.first),
                    listOf(N64SemanticTargetV1.ANALOG_X, N64SemanticTargetV1.ANALOG_Y),
                )
            }
            else -> Unit
        }
        return true
    }

    fun applyCandidate() {
        val current = mutableState.value
        val profile = current.working ?: return
        val candidate = current.candidate ?: return
        var updated = profile
        val targets = when (candidate) {
            is MappingCaptureCandidate.Button -> listOf(candidate.target)
            is MappingCaptureCandidate.Axis -> listOf(candidate.binding.target)
            is MappingCaptureCandidate.AnalogPair -> listOf(
                N64SemanticTargetV1.ANALOG_X,
                N64SemanticTargetV1.ANALOG_Y,
            )
        }
        targets.forEach { updated = removeTarget(updated, it) }
        updated = when (candidate) {
            is MappingCaptureCandidate.Button -> {
                updated = removePhysicalButton(updated, candidate.keyCode)
                updated.copy(buttonBindings = updated.buttonBindings + ButtonBindingV1(candidate.keyCode, candidate.target))
            }
            is MappingCaptureCandidate.Axis -> {
                updated = removePhysicalAxis(updated, candidate.binding.axisCode, candidate.binding.direction)
                updated.copy(axisBindings = updated.axisBindings + candidate.binding)
            }
            is MappingCaptureCandidate.AnalogPair -> {
                updated = removePhysicalAxis(updated, candidate.xAxisCode, AxisDirectionV1.BOTH)
                updated = removePhysicalAxis(updated, candidate.yAxisCode, AxisDirectionV1.BOTH)
                updated.copy(axisBindings = updated.axisBindings + listOf(
                    AxisBindingV1(candidate.xAxisCode, AxisDirectionV1.BOTH, N64SemanticTargetV1.ANALOG_X),
                    AxisBindingV1(
                        candidate.yAxisCode,
                        AxisDirectionV1.BOTH,
                        N64SemanticTargetV1.ANALOG_Y,
                        inverted = true,
                    ),
                ))
            }
        }
        pairXAxis = null
        clearPendingDpadRelease()
        publishWorking(updated, "Captured mapping applied to the working copy.")
    }

    fun cancelCapture() {
        if (!mutableState.value.captureActive && mutableState.value.candidate == null) return
        stopCapture()
        clearPendingDpadRelease()
        pairXAxis = null
        mutableState.value = mutableState.value.copy(
            phase = MappingCapturePhase.IDLE,
            captureTarget = null,
            candidate = null,
            conflicts = emptyList(),
            statusMessage = "Capture cancelled; no mapping was changed.",
        )
    }

    fun timeoutCapture() {
        if (!mutableState.value.captureActive) return
        stopCapture()
        clearPendingDpadRelease()
        pairXAxis = null
        mutableState.value = mutableState.value.copy(
            phase = MappingCapturePhase.IDLE,
            captureTarget = null,
            candidate = null,
            conflicts = emptyList(),
            statusMessage = "Capture timed out; no mapping was changed.",
        )
    }

    fun clearTarget(target: N64SemanticTargetV1) {
        val profile = mutableState.value.working ?: return
        publishWorking(removeTarget(profile, target), "${target.displayName()} mapping cleared.")
    }

    fun setProfileName(name: String) {
        val profile = mutableState.value.working ?: return
        publishWorking(profile.copy(profileName = name.take(80)), null)
    }

    fun toggleInversion(target: N64SemanticTargetV1) = updateAnalog(target) {
        it.copy(inverted = !it.inverted)
    }

    fun setDeadZone(target: N64SemanticTargetV1, value: Float?) = updateAnalog(target) {
        it.copy(deadZone = value)
    }

    fun setScale(target: N64SemanticTargetV1, value: Float) = updateAnalog(target) {
        it.copy(scale = value)
    }

    fun requestRestoreDefaults() {
        if (mutableState.value.active) mutableState.value = mutableState.value.copy(restoreConfirmation = true)
    }

    fun cancelRestoreDefaults() {
        mutableState.value = mutableState.value.copy(restoreConfirmation = false)
    }

    fun confirmRestoreDefaults() {
        val profile = mutableState.value.working ?: return
        val standard = StandardAndroidGamepadProfile.profile
        publishWorking(
            profile.copy(
                buttonBindings = standard.buttonBindings,
                axisBindings = standard.axisBindings,
                axisAlternatives = standard.axisAlternatives,
            ),
            "Standard mappings restored in the working copy.",
        )
        mutableState.value = mutableState.value.copy(restoreConfirmation = false)
    }

    suspend fun save(): Boolean {
        val current = mutableState.value
        if (current.phase == MappingCapturePhase.SAVING) return false
        val working = current.working ?: return false
        val validation = ControllerProfileValidatorV1.validate(working)
        if (!validation.isValid) {
            mutableState.value = validated(current.copy(errorMessage = "Fix validation errors before saving."))
            return false
        }
        mutableState.value = current.copy(phase = MappingCapturePhase.SAVING, errorMessage = null)
        clearPendingDpadRelease()
        return try {
            repository.update(working)
            val authoritative = repository.load().profiles.singleOrNull { it.profileId == working.profileId }
                ?: throw ControllerProfileUiRepositoryException("Saved profile could not be reloaded.")
            val comparison = compareControllerProfilesSemantically(working, authoritative)
            if (!comparison.equivalent) {
                throw ControllerProfileUiRepositoryException(
                    "Saved profile did not match authoritative storage (${comparison.differingFields.joinToString()}).",
                )
            }
            mutableState.value = validated(current.copy(
                original = authoritative,
                working = authoritative,
                phase = MappingCapturePhase.IDLE,
                dirty = false,
                statusMessage = "Profile saved and reloaded.",
                errorMessage = null,
            ))
            true
        } catch (failure: ControllerProfileUiRepositoryException) {
            mutableState.value = current.copy(
                phase = MappingCapturePhase.ERROR,
                errorMessage = safeMessage(failure),
            )
            false
        }
    }

    fun requestClose() {
        val current = mutableState.value
        if (!current.active) return
        stopCapture()
        clearPendingDpadRelease()
        mutableState.value = if (current.dirty) current.copy(
            phase = MappingCapturePhase.IDLE,
            discardConfirmation = true,
            candidate = null,
            conflicts = emptyList(),
        ) else MappingEditorState()
    }

    fun continueEditing() {
        mutableState.value = mutableState.value.copy(discardConfirmation = false)
    }

    fun discardAndClose() {
        stopCapture()
        clearPendingDpadRelease()
        device = null
        managementStableIdentity = null
        latestDevices = emptyList()
        pairXAxis = null
        mutableState.value = MappingEditorState()
    }

    fun rejectManagementDeviceChange() {
        if (mutableState.value.active) {
            mutableState.value = mutableState.value.copy(
                errorMessage = "Save or discard this edit before selecting another controller.",
            )
        }
    }

    fun canChangeManagementDevice(): Boolean = !mutableState.value.active

    private fun beginCapture(phase: MappingCapturePhase, target: N64SemanticTargetV1): Boolean {
        val current = mutableState.value
        if (!current.active) return false
        if (!current.deviceAvailable) return fail("The selected controller is unavailable.")
        clearPendingDpadRelease()
        setSuppression(true)
        mutableState.value = current.copy(
            phase = phase,
            captureTarget = target,
            candidate = null,
            conflicts = emptyList(),
            errorMessage = null,
            statusMessage = when (phase) {
                MappingCapturePhase.WAITING_FOR_BUTTON -> "Press a controller button."
                MappingCapturePhase.WAITING_FOR_AXIS -> "Move one controller axis deliberately."
                MappingCapturePhase.WAITING_FOR_ANALOG_PAIR_X -> "Move the desired X axis."
                else -> null
            },
        )
        return true
    }

    private fun prepareCandidate(candidate: MappingCaptureCandidate, targets: List<N64SemanticTargetV1>) {
        stopCapture()
        val profile = mutableState.value.working ?: return
        val conflicts = findConflicts(profile, candidate, targets)
        mutableState.value = mutableState.value.copy(
            phase = if (conflicts.isEmpty()) MappingCapturePhase.CANDIDATE_READY else MappingCapturePhase.CONFLICT,
            candidate = candidate,
            conflicts = conflicts,
            statusMessage = null,
        )
    }

    private fun findConflicts(
        profile: ControllerProfileV1,
        candidate: MappingCaptureCandidate,
        targets: List<N64SemanticTargetV1>,
    ): List<String> = buildList {
        targets.forEach { target ->
            if (profile.buttonBindings.any { it.target == target } || allAxisBindings(profile).any { it.target == target }) {
                add("${target.displayName()} already has a mapping.")
            }
        }
        when (candidate) {
            is MappingCaptureCandidate.Button -> profile.buttonBindings
                .filter { it.keyCode == candidate.keyCode && it.target != candidate.target }
                .forEach { add("Button ${candidate.keyCode} is mapped to ${it.target.displayName()}.") }
            is MappingCaptureCandidate.Axis -> allAxisBindings(profile)
                .filter { samePhysicalAxis(it, candidate.binding) && it.target != candidate.binding.target }
                .forEach { add("Axis ${candidate.binding.axisCode} ${candidate.binding.direction} is mapped to ${it.target.displayName()}.") }
            is MappingCaptureCandidate.AnalogPair -> allAxisBindings(profile)
                .filter { it.axisCode == candidate.xAxisCode || it.axisCode == candidate.yAxisCode }
                .filter { it.target !in targets }
                .forEach { add("Axis ${it.axisCode} is mapped to ${it.target.displayName()}.") }
        }
    }.distinct()

    private fun dominantAxis(
        selectedDevice: BridgeControllerDiagnosticDevice,
        values: Map<Int, Float>,
        excluded: Set<Int>,
    ): Pair<Int, AxisDirectionV1>? = selectedDevice.motionRanges.asSequence()
        .filter { it.axisCode !in excluded && values.containsKey(it.axisCode) }
        .mapNotNull { range ->
            val span = range.maximum - range.minimum
            if (span <= 0f) return@mapNotNull null
            val center = if (range.minimum < 0f && range.maximum > 0f) 0f else range.minimum
            val value = values.getValue(range.axisCode).coerceIn(range.minimum, range.maximum)
            val normalized = if (value >= center) {
                (value - center) / (range.maximum - center).coerceAtLeast(Float.MIN_VALUE)
            } else {
                -((center - value) / (center - range.minimum).coerceAtLeast(Float.MIN_VALUE))
            }
            val flat = (range.flat / maxOf(range.maximum - center, center - range.minimum)
                .coerceAtLeast(Float.MIN_VALUE)).coerceIn(0f, 0.95f)
            val magnitude = abs(normalized)
            if (magnitude < maxOf(0.55f, flat + 0.2f)) null
            else Triple(range.axisCode, if (normalized < 0f) AxisDirectionV1.NEGATIVE else AxisDirectionV1.POSITIVE, magnitude)
        }
        .maxByOrNull { it.third }
        ?.let { it.first to it.second }

    private fun updateAnalog(
        target: N64SemanticTargetV1,
        transform: (AxisBindingV1) -> AxisBindingV1,
    ) {
        if (!target.isAnalog) return
        val profile = mutableState.value.working ?: return
        var found = false
        val bindings = profile.axisBindings.map {
            if (it.target == target) {
                found = true
                transform(it)
            } else it
        }
        if (!found) {
            mutableState.value = mutableState.value.copy(errorMessage = "Capture ${target.displayName()} before editing it.")
            return
        }
        publishWorking(profile.copy(axisBindings = bindings), null)
    }

    private fun publishWorking(profile: ControllerProfileV1, message: String?) {
        pairXAxis = null
        mutableState.value = validated(mutableState.value.copy(
            working = profile,
            dirty = profile != mutableState.value.original,
            phase = MappingCapturePhase.IDLE,
            captureTarget = null,
            candidate = null,
            conflicts = emptyList(),
            errorMessage = null,
            statusMessage = message,
        ))
    }

    private fun validated(state: MappingEditorState): MappingEditorState {
        val issues = state.working?.let(ControllerProfileValidatorV1::validate)?.issues.orEmpty()
        return state.copy(validationErrors = issues.map { "${it.code}: ${it.detail}" })
    }

    private fun removeTarget(profile: ControllerProfileV1, target: N64SemanticTargetV1) = profile.copy(
        buttonBindings = profile.buttonBindings.filterNot { it.target == target },
        axisBindings = profile.axisBindings.filterNot { it.target == target },
        axisAlternatives = mapAlternatives(profile.axisAlternatives) { it.target != target },
    )

    private fun removePhysicalButton(profile: ControllerProfileV1, keyCode: Int) = profile.copy(
        buttonBindings = profile.buttonBindings.filterNot { it.keyCode == keyCode },
    )

    private fun removePhysicalAxis(
        profile: ControllerProfileV1,
        axisCode: Int,
        direction: AxisDirectionV1,
    ) = profile.copy(
        axisBindings = profile.axisBindings.filterNot { samePhysicalAxis(it, axisCode, direction) },
        axisAlternatives = mapAlternatives(profile.axisAlternatives) {
            !samePhysicalAxis(it, axisCode, direction)
        },
    )

    private fun mapAlternatives(
        alternatives: List<AxisBindingAlternativeV1>,
        retain: (AxisBindingV1) -> Boolean,
    ): List<AxisBindingAlternativeV1> = alternatives.mapNotNull { alternative ->
        alternative.copy(bindings = alternative.bindings.filter(retain)).takeIf { it.bindings.isNotEmpty() }
    }

    private fun allAxisBindings(profile: ControllerProfileV1): List<AxisBindingV1> =
        profile.axisBindings + profile.axisAlternatives.flatMap { it.bindings }

    private fun samePhysicalAxis(first: AxisBindingV1, second: AxisBindingV1): Boolean =
        samePhysicalAxis(first, second.axisCode, second.direction)

    private fun samePhysicalAxis(first: AxisBindingV1, axisCode: Int, direction: AxisDirectionV1): Boolean =
        first.axisCode == axisCode && (first.direction == direction ||
            first.direction == AxisDirectionV1.BOTH || direction == AxisDirectionV1.BOTH)

    private fun stopCapture() = setSuppression(false)

    private fun clearPendingDpadRelease() {
        pendingDpadReleaseKeyCode = null
    }

    private fun setSuppression(value: Boolean) {
        if (suppression == value) return
        suppression = value
        suppressPhysicalGameplay(value)
    }

    private fun fail(message: String): Boolean {
        mutableState.value = mutableState.value.copy(errorMessage = message)
        return false
    }

    private fun safeMessage(failure: ControllerProfileUiRepositoryException): String =
        failure.message ?: "Controller profile storage is unavailable."
}

internal fun N64SemanticTargetV1.displayName(): String = when (this) {
    N64SemanticTargetV1.A -> "N64 A"
    N64SemanticTargetV1.B -> "N64 B"
    N64SemanticTargetV1.START -> "Start"
    N64SemanticTargetV1.Z -> "Z"
    N64SemanticTargetV1.L -> "L"
    N64SemanticTargetV1.R -> "R"
    N64SemanticTargetV1.DPAD_UP -> "D-pad Up"
    N64SemanticTargetV1.DPAD_DOWN -> "D-pad Down"
    N64SemanticTargetV1.DPAD_LEFT -> "D-pad Left"
    N64SemanticTargetV1.DPAD_RIGHT -> "D-pad Right"
    N64SemanticTargetV1.C_UP -> "C-Up"
    N64SemanticTargetV1.C_DOWN -> "C-Down"
    N64SemanticTargetV1.C_LEFT -> "C-Left"
    N64SemanticTargetV1.C_RIGHT -> "C-Right"
    N64SemanticTargetV1.ANALOG_X -> "Analog X"
    N64SemanticTargetV1.ANALOG_Y -> "Analog Y"
}
