package com.melfegor.nice64.core.input

import com.melfegor.nice64.core.model.N64ButtonMask
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import org.junit.Test

class ControllerMappingV1Test {
    @Test fun sourceClassificationExcludesKeyboardMouseOnly() {
        assertEquals(null, ControllerSourceClassV1.fromSources(false, false))
        assertEquals(ControllerSourceClassV1.GAMEPAD, ControllerSourceClassV1.fromSources(true, false))
        assertEquals(ControllerSourceClassV1.JOYSTICK, ControllerSourceClassV1.fromSources(false, true))
        assertEquals(ControllerSourceClassV1.GAMEPAD_AND_JOYSTICK,
            ControllerSourceClassV1.fromSources(true, true))
    }

    @Test fun signedNormalizationSupportsAsymmetricRangeAndCenter() {
        val range = AxisRangeV1(1, -20f, 80f, center = 10f)
        assertEquals(-1f, AxisNormalizerV1.normalize(-20f, range))
        assertEquals(0f, AxisNormalizerV1.normalize(10f, range))
        assertEquals(1f, AxisNormalizerV1.normalize(80f, range))
        assertEquals(0.5f, AxisNormalizerV1.normalize(45f, range))
    }

    @Test fun oneSidedNormalizationClampsToZeroAndOne() {
        val range = AxisRangeV1(2, 5f, 15f)
        assertEquals(0f, AxisNormalizerV1.normalize(-5f, range))
        assertEquals(0.5f, AxisNormalizerV1.normalize(10f, range))
        assertEquals(1f, AxisNormalizerV1.normalize(20f, range))
    }

    @Test fun reportedFlatIsFloorAndRemainingTravelIsRescaled() {
        val range = AxisRangeV1(1, -1f, 1f, flat = 0.2f)
        assertEquals(0f, AxisNormalizerV1.applyDeadZone(0.19f, range, 0.1f))
        assertEquals(0.5f, AxisNormalizerV1.applyDeadZone(0.6f, range, 0.1f), 0.0001f)
        assertEquals(0f, AxisNormalizerV1.applyDeadZone(0.29f, range, 0.3f))
    }

    @Test fun explicitZeroDeadzoneSupportsOneSidedTrigger() {
        val range = AxisRangeV1(9, 0f, 1f)
        assertEquals(0.25f, AxisNormalizerV1.applyDeadZone(0.25f, range, 0f))
    }

    @Test fun evaluatorAppliesInversionAndLinearScale() {
        val profile = profile(axisBindings = listOf(
            AxisBindingV1(1, AxisDirectionV1.BOTH, N64SemanticTargetV1.ANALOG_Y,
                inverted = true, deadZone = 0f, scale = 0.5f),
        ))
        val result = evaluate(profile, ranges = mapOf(1 to AxisRangeV1(1, -1f, 1f)), axes = mapOf(1 to 1f))
        assertEquals(-64, result.contribution.analogY)
    }

    @Test fun digitalAxisUsesPressAndLowerReleaseThresholds() {
        val profile = profile(axisBindings = listOf(digital(1, AxisDirectionV1.POSITIVE, N64SemanticTargetV1.C_RIGHT)))
        val ranges = mapOf(1 to AxisRangeV1(1, -1f, 1f, flat = 0.01f))
        val belowPress = evaluate(profile, ranges, mapOf(1 to 0.49f))
        assertEquals(0u, belowPress.contribution.buttons)
        val pressed = evaluate(profile, ranges, mapOf(1 to 0.6f))
        assertEquals(N64ButtonMask.C_RIGHT, pressed.contribution.buttons)
        val held = evaluate(profile, ranges, mapOf(1 to 0.48f), pressed.state)
        assertEquals(N64ButtonMask.C_RIGHT, held.contribution.buttons)
        val released = evaluate(profile, ranges, mapOf(1 to 0.4f), held.state)
        assertEquals(0u, released.contribution.buttons)
    }

    @Test fun exactDuplicateDigitalSourcesCollapseIntoOneMask() {
        val profile = profile(
            buttons = listOf(ButtonBindingV1(10, N64SemanticTargetV1.DPAD_LEFT)),
            axisBindings = listOf(digital(1, AxisDirectionV1.NEGATIVE, N64SemanticTargetV1.DPAD_LEFT)),
        )
        val result = evaluate(profile, mapOf(1 to AxisRangeV1(1, -1f, 1f, flat = 0.01f)),
            mapOf(1 to -1f), keys = setOf(10))
        assertEquals(N64ButtonMask.DPAD_LEFT, result.contribution.buttons)
    }

    @Test fun capabilityAlternativeChoosesPreferredCompletePairOnly() {
        val preferred = AxisBindingAlternativeV1("right", "preferred", 0, setOf(1, 2),
            listOf(digital(1, AxisDirectionV1.POSITIVE, N64SemanticTargetV1.C_RIGHT)))
        val fallback = AxisBindingAlternativeV1("right", "fallback", 1, setOf(3, 4),
            listOf(digital(3, AxisDirectionV1.NEGATIVE, N64SemanticTargetV1.C_LEFT)))
        val profile = profile(alternatives = listOf(preferred, fallback))
        val ranges = (1..4).associateWith { AxisRangeV1(it, -1f, 1f, flat = 0.01f) }
        val result = evaluate(profile, ranges, mapOf(1 to 1f, 3 to -1f))
        assertEquals(N64ButtonMask.C_RIGHT, result.contribution.buttons)
    }

    @Test fun capabilityAlternativeFallsBackWhenPreferredPairIncomplete() {
        val preferred = AxisBindingAlternativeV1("right", "preferred", 0, setOf(1, 2),
            listOf(digital(1, AxisDirectionV1.POSITIVE, N64SemanticTargetV1.C_RIGHT)))
        val fallback = AxisBindingAlternativeV1("right", "fallback", 1, setOf(3, 4),
            listOf(digital(3, AxisDirectionV1.NEGATIVE, N64SemanticTargetV1.C_LEFT)))
        val profile = profile(alternatives = listOf(preferred, fallback))
        val ranges = mapOf(1 to AxisRangeV1(1, -1f, 1f), 3 to AxisRangeV1(3, -1f, 1f),
            4 to AxisRangeV1(4, -1f, 1f))
        assertEquals(N64ButtonMask.C_LEFT,
            evaluate(profile, ranges, mapOf(3 to -1f)).contribution.buttons)
    }

    @Test fun validatorRejectsButtonToAnalogAndDuplicateKey() {
        val result = ControllerProfileValidatorV1.validate(profile(buttons = listOf(
            ButtonBindingV1(1, N64SemanticTargetV1.ANALOG_X),
            ButtonBindingV1(1, N64SemanticTargetV1.A),
        )))
        assertFalse(result.isValid)
        assertTrue(result.issues.any { it.code == ProfileValidationCodeV1.BUTTON_TO_ANALOG })
        assertTrue(result.issues.any { it.code == ProfileValidationCodeV1.DUPLICATE_PHYSICAL_BUTTON })
    }

    @Test fun validatorRejectsInvalidDigitalConfiguration() {
        val result = ControllerProfileValidatorV1.validate(profile(axisBindings = listOf(
            AxisBindingV1(1, AxisDirectionV1.BOTH, N64SemanticTargetV1.Z,
                scale = 2f, pressThreshold = 0.4f, releaseThreshold = 0.5f),
        )))
        assertFalse(result.isValid)
        assertTrue(result.issues.any { it.code == ProfileValidationCodeV1.INVALID_AXIS_DIRECTION })
        assertTrue(result.issues.any { it.code == ProfileValidationCodeV1.DIGITAL_WITH_SCALE })
        assertTrue(result.issues.any { it.code == ProfileValidationCodeV1.INVALID_THRESHOLDS })
    }

    @Test fun validatorRejectsAnalogThresholdsAndAmbiguousAnalogTarget() {
        val result = ControllerProfileValidatorV1.validate(profile(axisBindings = listOf(
            AxisBindingV1(1, AxisDirectionV1.BOTH, N64SemanticTargetV1.ANALOG_X,
                pressThreshold = 0.5f, releaseThreshold = 0.4f),
            AxisBindingV1(2, AxisDirectionV1.BOTH, N64SemanticTargetV1.ANALOG_X),
        )))
        assertFalse(result.isValid)
        assertTrue(result.issues.any { it.code == ProfileValidationCodeV1.ANALOG_WITH_THRESHOLDS })
        assertTrue(result.issues.any { it.code == ProfileValidationCodeV1.AMBIGUOUS_ANALOG_TARGET })
    }

    @Test fun validatorRejectsUnknownSchemaAndBounds() {
        val profile = profile(axisBindings = listOf(
            AxisBindingV1(1, AxisDirectionV1.BOTH, N64SemanticTargetV1.ANALOG_X,
                deadZone = 1f, scale = 0f),
        )).copy(schemaVersion = 2)
        val codes = ControllerProfileValidatorV1.validate(profile).issues.map { it.code }.toSet()
        assertTrue(ProfileValidationCodeV1.INVALID_SCHEMA in codes)
        assertTrue(ProfileValidationCodeV1.INVALID_DEADZONE in codes)
        assertTrue(ProfileValidationCodeV1.INVALID_SCALE in codes)
    }

    private fun profile(
        buttons: List<ButtonBindingV1> = emptyList(),
        axisBindings: List<AxisBindingV1> = emptyList(),
        alternatives: List<AxisBindingAlternativeV1> = emptyList(),
    ) = ControllerProfileV1(
        profileId = "test", profileName = "Test", buttonBindings = buttons,
        axisBindings = axisBindings, axisAlternatives = alternatives,
    )

    private fun digital(axis: Int, direction: AxisDirectionV1, target: N64SemanticTargetV1) =
        AxisBindingV1(axis, direction, target, deadZone = 0f,
            pressThreshold = 0.5f, releaseThreshold = 0.45f)

    private fun evaluate(
        profile: ControllerProfileV1,
        ranges: Map<Int, AxisRangeV1>,
        axes: Map<Int, Float>,
        previous: MappingEvaluationStateV1 = MappingEvaluationStateV1(),
        keys: Set<Int> = emptySet(),
    ) = ControllerMappingEvaluatorV1.evaluate(
        profile,
        ControllerCapabilitiesV1(ControllerSourceClassV1.GAMEPAD_AND_JOYSTICK, ranges),
        PhysicalInputStateV1(keys, axes),
        previous,
    )
}
