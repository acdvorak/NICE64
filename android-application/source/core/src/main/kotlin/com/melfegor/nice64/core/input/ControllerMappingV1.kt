package com.melfegor.nice64.core.input

import com.melfegor.nice64.core.model.N64ButtonMask
import kotlin.math.abs
import kotlin.math.roundToInt

const val CONTROLLER_PROFILE_SCHEMA_V1 = 1

enum class ControllerSourceClassV1 {
    GAMEPAD,
    JOYSTICK,
    GAMEPAD_AND_JOYSTICK;

    companion object {
        fun fromSources(hasGamepad: Boolean, hasJoystick: Boolean): ControllerSourceClassV1? = when {
            hasGamepad && hasJoystick -> GAMEPAD_AND_JOYSTICK
            hasGamepad -> GAMEPAD
            hasJoystick -> JOYSTICK
            else -> null
        }
    }
}

data class ControllerIdentityV1(
    val descriptor: String,
    val vendorId: Int,
    val productId: Int,
    val normalizedName: String,
    val sourceClass: ControllerSourceClassV1,
    val motionSignature: String,
)

data class PhysicalButtonInput(val keyCode: Int, val pressed: Boolean)

data class PhysicalAxisInput(val axisCode: Int, val value: Float)

data class PhysicalInputStateV1(
    val pressedKeys: Set<Int> = emptySet(),
    val axisValues: Map<Int, Float> = emptyMap(),
)

data class AxisRangeV1(
    val axisCode: Int,
    val minimum: Float,
    val maximum: Float,
    val flat: Float = 0f,
    val center: Float? = null,
) {
    init {
        require(minimum.isFinite() && maximum.isFinite() && maximum > minimum)
        require(flat.isFinite() && flat >= 0f)
        require(center == null || center.isFinite() && center in minimum..maximum)
    }

    val isSigned: Boolean get() = minimum < 0f && maximum > 0f
    val effectiveCenter: Float get() = center ?: if (isSigned) 0f else minimum
}

data class ControllerCapabilitiesV1(
    val sourceClass: ControllerSourceClassV1,
    val motionRanges: Map<Int, AxisRangeV1>,
    val keyCodes: Set<Int> = emptySet(),
)

enum class N64SemanticTargetV1 {
    A, B, START, Z, L, R,
    DPAD_UP, DPAD_DOWN, DPAD_LEFT, DPAD_RIGHT,
    C_UP, C_DOWN, C_LEFT, C_RIGHT,
    ANALOG_X, ANALOG_Y;

    val isAnalog: Boolean get() = this == ANALOG_X || this == ANALOG_Y
}

enum class AxisDirectionV1 { BOTH, POSITIVE, NEGATIVE }

data class ButtonBindingV1(
    val keyCode: Int,
    val target: N64SemanticTargetV1,
)

data class AxisBindingV1(
    val axisCode: Int,
    val direction: AxisDirectionV1,
    val target: N64SemanticTargetV1,
    val inverted: Boolean = false,
    val deadZone: Float? = null,
    val scale: Float = 1f,
    val pressThreshold: Float? = null,
    val releaseThreshold: Float? = null,
)

/** One capability-selected variant. The lowest-priority supported variant wins per choiceGroup. */
data class AxisBindingAlternativeV1(
    val choiceGroup: String,
    val variantId: String,
    val priority: Int,
    val requiredAxes: Set<Int>,
    val bindings: List<AxisBindingV1>,
)

data class ControllerProfileV1(
    val schemaVersion: Int = CONTROLLER_PROFILE_SCHEMA_V1,
    val profileId: String,
    val profileName: String,
    val identity: ControllerIdentityV1? = null,
    val buttonBindings: List<ButtonBindingV1>,
    val axisBindings: List<AxisBindingV1>,
    val axisAlternatives: List<AxisBindingAlternativeV1> = emptyList(),
)

enum class ProfileValidationCodeV1 {
    INVALID_SCHEMA,
    INVALID_PROFILE_ID,
    INVALID_PROFILE_NAME,
    BUTTON_TO_ANALOG,
    DUPLICATE_PHYSICAL_BUTTON,
    INVALID_AXIS_DIRECTION,
    INVALID_DEADZONE,
    INVALID_SCALE,
    ANALOG_WITH_THRESHOLDS,
    DIGITAL_WITHOUT_THRESHOLDS,
    DIGITAL_WITH_SCALE,
    INVALID_THRESHOLDS,
    DUPLICATE_AXIS_BINDING,
    AMBIGUOUS_ANALOG_TARGET,
    INVALID_ALTERNATIVE,
}

data class ProfileValidationIssueV1(val code: ProfileValidationCodeV1, val detail: String)

data class ProfileValidationResultV1(val issues: List<ProfileValidationIssueV1>) {
    val isValid: Boolean get() = issues.isEmpty()
}

object ControllerProfileValidatorV1 {
    fun validate(profile: ControllerProfileV1): ProfileValidationResultV1 {
        val issues = mutableListOf<ProfileValidationIssueV1>()
        fun issue(code: ProfileValidationCodeV1, detail: String) {
            issues += ProfileValidationIssueV1(code, detail)
        }

        if (profile.schemaVersion != CONTROLLER_PROFILE_SCHEMA_V1) {
            issue(ProfileValidationCodeV1.INVALID_SCHEMA, "schemaVersion=${profile.schemaVersion}")
        }
        if (profile.profileId.isBlank()) issue(ProfileValidationCodeV1.INVALID_PROFILE_ID, "blank profileId")
        if (profile.profileName.isBlank()) issue(ProfileValidationCodeV1.INVALID_PROFILE_NAME, "blank profileName")

        profile.buttonBindings.groupBy { it.keyCode }.filterValues { it.size > 1 }.forEach { (key, _) ->
            issue(ProfileValidationCodeV1.DUPLICATE_PHYSICAL_BUTTON, "keyCode=$key")
        }
        profile.buttonBindings.filter { it.target.isAnalog }.forEach {
            issue(ProfileValidationCodeV1.BUTTON_TO_ANALOG, "keyCode=${it.keyCode}")
        }

        val alternativesByGroup = profile.axisAlternatives.groupBy { it.choiceGroup }
        alternativesByGroup.forEach { (group, alternatives) ->
            if (group.isBlank() || alternatives.any { it.variantId.isBlank() || it.requiredAxes.isEmpty() || it.bindings.isEmpty() } ||
                alternatives.map { it.variantId }.distinct().size != alternatives.size ||
                alternatives.map { it.priority }.distinct().size != alternatives.size
            ) issue(ProfileValidationCodeV1.INVALID_ALTERNATIVE, "choiceGroup=$group")
        }

        validateAxisList(profile.axisBindings, "direct", issues)
        profile.axisAlternatives.forEach { alternative ->
            validateAxisList(alternative.bindings, "${alternative.choiceGroup}/${alternative.variantId}", issues)
            if (!alternative.bindings.all { it.axisCode in alternative.requiredAxes }) {
                issue(ProfileValidationCodeV1.INVALID_ALTERNATIVE, "requiredAxes incomplete for ${alternative.variantId}")
            }
        }

        profile.axisBindings.filter { it.target.isAnalog }.groupBy { it.target }
            .filterValues { it.size > 1 }.forEach { (target, _) ->
                issue(ProfileValidationCodeV1.AMBIGUOUS_ANALOG_TARGET, "target=$target")
            }
        return ProfileValidationResultV1(issues)
    }

    private fun validateAxisList(
        bindings: List<AxisBindingV1>,
        scope: String,
        issues: MutableList<ProfileValidationIssueV1>,
    ) {
        fun issue(code: ProfileValidationCodeV1, detail: String) {
            issues += ProfileValidationIssueV1(code, "$scope: $detail")
        }
        bindings.groupBy { it.axisCode to it.direction }.filterValues { it.size > 1 }.forEach { (key, _) ->
            issue(ProfileValidationCodeV1.DUPLICATE_AXIS_BINDING, "axis/direction=$key")
        }
        bindings.forEach { binding ->
            if (binding.deadZone != null && (!binding.deadZone.isFinite() || binding.deadZone !in 0f..<1f)) {
                issue(ProfileValidationCodeV1.INVALID_DEADZONE, "axis=${binding.axisCode}")
            }
            if (!binding.scale.isFinite() || binding.scale <= 0f || binding.scale > 4f) {
                issue(ProfileValidationCodeV1.INVALID_SCALE, "axis=${binding.axisCode}")
            }
            if (binding.target.isAnalog) {
                if (binding.direction != AxisDirectionV1.BOTH) {
                    issue(ProfileValidationCodeV1.INVALID_AXIS_DIRECTION, "analog axis=${binding.axisCode}")
                }
                if (binding.pressThreshold != null || binding.releaseThreshold != null) {
                    issue(ProfileValidationCodeV1.ANALOG_WITH_THRESHOLDS, "axis=${binding.axisCode}")
                }
            } else {
                if (binding.direction == AxisDirectionV1.BOTH) {
                    issue(ProfileValidationCodeV1.INVALID_AXIS_DIRECTION, "digital axis=${binding.axisCode}")
                }
                if (binding.scale != 1f) {
                    issue(ProfileValidationCodeV1.DIGITAL_WITH_SCALE, "axis=${binding.axisCode}")
                }
                val press = binding.pressThreshold
                val release = binding.releaseThreshold
                if (press == null || release == null) {
                    issue(ProfileValidationCodeV1.DIGITAL_WITHOUT_THRESHOLDS, "axis=${binding.axisCode}")
                } else if (!press.isFinite() || !release.isFinite() || press !in 0f..1f ||
                    release !in 0f..1f || release >= press
                ) {
                    issue(ProfileValidationCodeV1.INVALID_THRESHOLDS, "axis=${binding.axisCode}")
                }
            }
        }
    }
}

object AxisNormalizerV1 {
    const val FALLBACK_DEADZONE = 0.12f

    fun normalize(value: Float, range: AxisRangeV1): Float {
        val clamped = value.coerceIn(range.minimum, range.maximum)
        val center = range.effectiveCenter
        return if (range.isSigned || center > range.minimum) {
            if (clamped >= center) {
                ((clamped - center) / (range.maximum - center).coerceAtLeast(Float.MIN_VALUE)).coerceIn(0f, 1f)
            } else {
                -((center - clamped) / (center - range.minimum).coerceAtLeast(Float.MIN_VALUE)).coerceIn(0f, 1f)
            }
        } else {
            ((clamped - range.minimum) / (range.maximum - range.minimum)).coerceIn(0f, 1f)
        }
    }

    fun applyDeadZone(normalized: Float, range: AxisRangeV1, profileDeadZone: Float?): Float {
        val extent = if (range.isSigned || range.effectiveCenter > range.minimum) {
            maxOf(range.maximum - range.effectiveCenter, range.effectiveCenter - range.minimum)
        } else range.maximum - range.minimum
        val reported = (range.flat / extent.coerceAtLeast(Float.MIN_VALUE)).coerceIn(0f, 0.95f)
        val deadZone = if (profileDeadZone != null) maxOf(reported, profileDeadZone)
        else if (range.flat > 0f) reported else FALLBACK_DEADZONE
        val boundedDeadZone = deadZone.coerceIn(0f, 0.95f)
        val magnitude = abs(normalized)
        if (magnitude <= boundedDeadZone) return 0f
        val rescaled = ((magnitude - boundedDeadZone) / (1f - boundedDeadZone)).coerceIn(0f, 1f)
        return if (normalized < 0f) -rescaled else rescaled
    }
}

data class MappingEvaluationStateV1(val activeDigitalAxes: Set<String> = emptySet())

data class MappingEvaluationResultV1(
    val contribution: N64InputContribution,
    val state: MappingEvaluationStateV1,
)

object ControllerMappingEvaluatorV1 {
    fun evaluate(
        profile: ControllerProfileV1,
        capabilities: ControllerCapabilitiesV1,
        input: PhysicalInputStateV1,
        previous: MappingEvaluationStateV1 = MappingEvaluationStateV1(),
    ): MappingEvaluationResultV1 {
        val validation = ControllerProfileValidatorV1.validate(profile)
        require(validation.isValid) { "Invalid controller profile: ${validation.issues}" }

        var buttons = 0u
        profile.buttonBindings.filter { it.keyCode in input.pressedKeys }.forEach {
            buttons = buttons or maskFor(it.target)
        }

        val selectedAlternatives = profile.axisAlternatives.groupBy { it.choiceGroup }.values.mapNotNull { variants ->
            variants.sortedBy { it.priority }.firstOrNull { candidate ->
                candidate.requiredAxes.all(capabilities.motionRanges::containsKey)
            }
        }
        val scopedBindings = buildList {
            profile.axisBindings.forEachIndexed { index, binding -> add("direct:$index" to binding) }
            selectedAlternatives.forEach { alternative ->
                alternative.bindings.forEachIndexed { index, binding ->
                    add("${alternative.choiceGroup}:${alternative.variantId}:$index" to binding)
                }
            }
        }

        var analogX = 0
        var analogY = 0
        val active = mutableSetOf<String>()
        scopedBindings.forEach { (id, binding) ->
            val range = capabilities.motionRanges[binding.axisCode] ?: return@forEach
            val raw = input.axisValues[binding.axisCode] ?: range.effectiveCenter
            var normalized = AxisNormalizerV1.normalize(raw, range)
            normalized = AxisNormalizerV1.applyDeadZone(normalized, range, binding.deadZone)
            if (binding.inverted) normalized = -normalized
            if (binding.target.isAnalog) {
                val scaled = (normalized * binding.scale).coerceIn(-1f, 1f)
                val semantic = if (scaled < 0f) (scaled * 128f).roundToInt().coerceIn(-128, 0)
                else (scaled * 127f).roundToInt().coerceIn(0, 127)
                if (binding.target == N64SemanticTargetV1.ANALOG_X) analogX = semantic else analogY = semantic
            } else {
                val directional = when (binding.direction) {
                    AxisDirectionV1.POSITIVE -> normalized.coerceAtLeast(0f)
                    AxisDirectionV1.NEGATIVE -> (-normalized).coerceAtLeast(0f)
                    AxisDirectionV1.BOTH -> error("validated digital BOTH binding")
                }
                val threshold = if (id in previous.activeDigitalAxes) binding.releaseThreshold!! else binding.pressThreshold!!
                if (directional >= threshold) {
                    active += id
                    buttons = buttons or maskFor(binding.target)
                }
            }
        }

        return MappingEvaluationResultV1(
            N64InputContribution(buttons, analogX, analogY),
            MappingEvaluationStateV1(active),
        )
    }

    private fun maskFor(target: N64SemanticTargetV1): UInt = when (target) {
        N64SemanticTargetV1.A -> N64ButtonMask.A
        N64SemanticTargetV1.B -> N64ButtonMask.B
        N64SemanticTargetV1.START -> N64ButtonMask.START
        N64SemanticTargetV1.Z -> N64ButtonMask.Z
        N64SemanticTargetV1.L -> N64ButtonMask.L
        N64SemanticTargetV1.R -> N64ButtonMask.R
        N64SemanticTargetV1.DPAD_UP -> N64ButtonMask.DPAD_UP
        N64SemanticTargetV1.DPAD_DOWN -> N64ButtonMask.DPAD_DOWN
        N64SemanticTargetV1.DPAD_LEFT -> N64ButtonMask.DPAD_LEFT
        N64SemanticTargetV1.DPAD_RIGHT -> N64ButtonMask.DPAD_RIGHT
        N64SemanticTargetV1.C_UP -> N64ButtonMask.C_UP
        N64SemanticTargetV1.C_DOWN -> N64ButtonMask.C_DOWN
        N64SemanticTargetV1.C_LEFT -> N64ButtonMask.C_LEFT
        N64SemanticTargetV1.C_RIGHT -> N64ButtonMask.C_RIGHT
        N64SemanticTargetV1.ANALOG_X, N64SemanticTargetV1.ANALOG_Y ->
            error("Analog target cannot become a button mask")
    }
}
