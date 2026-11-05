package com.melfegor.nice64.api

import com.melfegor.nice64.core.input.AxisRangeV1
import com.melfegor.nice64.core.input.ControllerCapabilitiesV1
import com.melfegor.nice64.core.input.ControllerMappingEvaluatorV1
import com.melfegor.nice64.core.input.ControllerProfileV1
import com.melfegor.nice64.core.input.ControllerProfileValidatorV1
import com.melfegor.nice64.core.input.MappingEvaluationStateV1
import com.melfegor.nice64.core.input.N64InputContribution
import com.melfegor.nice64.core.input.PhysicalInputStateV1

internal sealed interface LiveControllerMappingV1 {
    data class StandardAndroidGamepad(val reason: String? = null) : LiveControllerMappingV1
    data class Custom(val profile: ControllerProfileV1) : LiveControllerMappingV1
    data object Ineligible : LiveControllerMappingV1
}

internal data class LiveControllerProfileSnapshot(
    val profiles: List<ControllerProfileV1> = emptyList(),
    val selectedProfileId: String? = null,
    val unavailableReason: String? = "Controller profiles are still loading.",
)

internal object LiveControllerMappingResolverV1 {
    fun resolve(
        device: BridgeControllerDiagnosticDevice?,
        snapshot: LiveControllerProfileSnapshot,
    ): LiveControllerMappingV1 {
        if (device == null || !device.hasGamepadSource && !device.hasJoystickSource) {
            return LiveControllerMappingV1.Ineligible
        }
        snapshot.unavailableReason?.let {
            return LiveControllerMappingV1.StandardAndroidGamepad(it)
        }
        val identity = device.toStableIdentity()
        val selectedId = snapshot.selectedProfileId
        if (selectedId != null) {
            val selected = snapshot.profiles.singleOrNull { it.profileId == selectedId }
                ?: return LiveControllerMappingV1.StandardAndroidGamepad(
                    "The selected custom profile is unavailable.",
                )
            if (!ControllerProfileValidatorV1.validate(selected).isValid) {
                return LiveControllerMappingV1.StandardAndroidGamepad(
                    "The selected custom profile is invalid.",
                )
            }
            return when (ControllerProfileMatcherV1.match(identity, listOf(selected))) {
                is ControllerProfileMatchResultV1.ExactMatch,
                is ControllerProfileMatchResultV1.FallbackMatch -> LiveControllerMappingV1.Custom(selected)
                else -> LiveControllerMappingV1.StandardAndroidGamepad(
                    "The selected custom profile does not match the active controller.",
                )
            }
        }

        return when (val match = ControllerProfileMatcherV1.match(identity, snapshot.profiles)) {
            is ControllerProfileMatchResultV1.ExactMatch ->
                matchedProfile(snapshot, match.profileId, "The exact custom profile is invalid.")
            is ControllerProfileMatchResultV1.FallbackMatch ->
                matchedProfile(snapshot, match.profileId, "The fallback custom profile is invalid.")
            is ControllerProfileMatchResultV1.Ambiguous ->
                LiveControllerMappingV1.StandardAndroidGamepad("Multiple custom profiles match the active controller.")
            ControllerProfileMatchResultV1.NoMatch -> LiveControllerMappingV1.StandardAndroidGamepad()
        }
    }

    private fun matchedProfile(
        snapshot: LiveControllerProfileSnapshot,
        profileId: String,
        invalidReason: String,
    ): LiveControllerMappingV1 {
        val profile = snapshot.profiles.singleOrNull { it.profileId == profileId }
            ?: return LiveControllerMappingV1.StandardAndroidGamepad("The matched custom profile is unavailable.")
        return if (ControllerProfileValidatorV1.validate(profile).isValid) {
            LiveControllerMappingV1.Custom(profile)
        } else LiveControllerMappingV1.StandardAndroidGamepad(invalidReason)
    }
}

/**
 * Caches one immutable P3 snapshot. Repository I/O occurs only from explicit refresh calls;
 * Android input-event processing is always a synchronous in-memory resolution.
 */
internal class LiveControllerProfileCache(
    private val repository: ControllerProfileUiRepository,
) {
    @Volatile
    private var snapshot = LiveControllerProfileSnapshot()

    suspend fun refresh(): LiveControllerProfileSnapshot {
        snapshot = try {
            val loaded = repository.load()
            LiveControllerProfileSnapshot(
                profiles = loaded.profiles,
                selectedProfileId = loaded.selectedProfileId,
                unavailableReason = null,
            )
        } catch (failure: ControllerProfileUiRepositoryException) {
            LiveControllerProfileSnapshot(
                unavailableReason = failure.message ?: "Controller profiles are unavailable.",
            )
        }
        return snapshot
    }

    fun resolve(device: BridgeControllerDiagnosticDevice?): LiveControllerMappingV1 =
        LiveControllerMappingResolverV1.resolve(device, snapshot)
}

internal fun BridgeControllerDiagnosticDevice.toControllerCapabilitiesV1(): ControllerCapabilitiesV1 =
    ControllerCapabilitiesV1(
        sourceClass = toStableIdentity().sourceClass,
        motionRanges = motionRanges.associate { range ->
            range.axisCode to AxisRangeV1(
                axisCode = range.axisCode,
                minimum = range.minimum,
                maximum = range.maximum,
                flat = range.flat,
            )
        },
    )

/** Minimal non-persistent runtime state required by the accepted P2 evaluator. */
internal class LiveCustomControllerRuntime {
    private var profile: ControllerProfileV1? = null
    private var capabilities: ControllerCapabilitiesV1? = null
    private val pressedKeys = mutableSetOf<Int>()
    private val axisValues = mutableMapOf<Int, Float>()
    private var evaluationState = MappingEvaluationStateV1()

    fun activate(
        nextProfile: ControllerProfileV1,
        nextCapabilities: ControllerCapabilitiesV1,
    ): Boolean {
        val changed = profile != nextProfile || capabilities != nextCapabilities
        if (changed) {
            reset()
            profile = nextProfile
            capabilities = nextCapabilities
        }
        return changed
    }

    fun handlesKey(keyCode: Int): Boolean = profile?.buttonBindings?.any { it.keyCode == keyCode } == true

    fun updateKey(keyCode: Int, pressed: Boolean): N64InputContribution {
        if (pressed) pressedKeys += keyCode else pressedKeys -= keyCode
        return evaluate()
    }

    fun updateAxes(values: Map<Int, Float>): N64InputContribution {
        axisValues.putAll(values)
        return evaluate()
    }

    fun reset() {
        pressedKeys.clear()
        axisValues.clear()
        evaluationState = MappingEvaluationStateV1()
    }

    fun deactivate() {
        reset()
        profile = null
        capabilities = null
    }

    private fun evaluate(): N64InputContribution {
        val activeProfile = requireNotNull(profile)
        val activeCapabilities = requireNotNull(capabilities)
        val result = ControllerMappingEvaluatorV1.evaluate(
            activeProfile,
            activeCapabilities,
            PhysicalInputStateV1(pressedKeys.toSet(), axisValues.toMap()),
            evaluationState,
        )
        evaluationState = result.state
        return result.contribution
    }
}

internal val LiveControllerMappingV1.displayLabel: String
    get() = when (this) {
        is LiveControllerMappingV1.Custom -> "Custom — ${profile.profileName}"
        is LiveControllerMappingV1.StandardAndroidGamepad -> "Standard Android Gamepad"
        LiveControllerMappingV1.Ineligible -> "No eligible active controller"
    }

internal val LiveControllerMappingV1.displayReason: String?
    get() = (this as? LiveControllerMappingV1.StandardAndroidGamepad)?.reason
