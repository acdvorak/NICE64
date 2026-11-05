package com.melfegor.nice64.api

import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import com.melfegor.nice64.core.input.AxisBindingAlternativeV1
import com.melfegor.nice64.core.input.AxisBindingV1
import com.melfegor.nice64.core.input.AxisDirectionV1
import com.melfegor.nice64.core.input.ButtonBindingV1
import com.melfegor.nice64.core.input.ControllerProfileV1
import com.melfegor.nice64.core.input.N64SemanticTargetV1
import com.melfegor.nice64.core.model.N64ButtonMask
import kotlinx.coroutines.test.runTest
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNotEquals
import kotlin.test.assertTrue
import org.junit.Test

class ControllerLiveMappingTest {
    @Test fun eligibleControllerWithoutCustomProfileUsesFrozenStandardPath() {
        assertIs<LiveControllerMappingV1.StandardAndroidGamepad>(resolve(device(), emptyList()))
    }

    @Test fun exactAndFallbackMatchesActivateCustomProfiles() {
        val active = device()
        val exact = profile("exact", active)
        assertEquals(exact, assertIs<LiveControllerMappingV1.Custom>(resolve(active, listOf(exact))).profile)

        val fallbackDevice = active.copy(runtimeDeviceId = 999, descriptor = "new-descriptor")
        assertEquals(exact, assertIs<LiveControllerMappingV1.Custom>(
            resolve(fallbackDevice, listOf(exact)),
        ).profile)
    }

    @Test fun ambiguousMatchFallsBackWithoutListOrderSelection() {
        val active = device(descriptor = "")
        val first = profile("first", active.copy(descriptor = "stored-a"))
        val second = profile("second", active.copy(descriptor = "stored-b"))
        val result = assertIs<LiveControllerMappingV1.StandardAndroidGamepad>(
            resolve(active, listOf(first, second)),
        )
        assertTrue(result.reason!!.contains("Multiple"))
    }

    @Test fun explicitSelectedCompatibleProfileWinsButIncompatibleAndMissingFallBack() {
        val active = device()
        val compatible = profile("compatible", active)
        val other = profile("other", device(descriptor = "other", vendor = 9, product = 9))
        assertEquals(compatible, assertIs<LiveControllerMappingV1.Custom>(
            resolve(active, listOf(compatible, other), compatible.profileId),
        ).profile)
        assertTrue(assertIs<LiveControllerMappingV1.StandardAndroidGamepad>(
            resolve(active, listOf(compatible, other), other.profileId),
        ).reason!!.contains("does not match"))
        assertTrue(assertIs<LiveControllerMappingV1.StandardAndroidGamepad>(
            resolve(active, listOf(compatible), "deleted"),
        ).reason!!.contains("unavailable"))
    }

    @Test fun corruptAndFutureStorageFailClosedToStandard() {
        listOf("Controller profile storage is corrupt.", "Controller profiles use a newer unsupported format.")
            .forEach { reason ->
                val result = LiveControllerMappingResolverV1.resolve(
                    device(), LiveControllerProfileSnapshot(unavailableReason = reason),
                )
                assertEquals(reason, assertIs<LiveControllerMappingV1.StandardAndroidGamepad>(result).reason)
            }
    }

    @Test fun keyboardMouseIsIneligibleAndRuntimeIdDoesNotParticipateInMatching() {
        assertIs<LiveControllerMappingV1.Ineligible>(
            LiveControllerMappingResolverV1.resolve(
                device().copy(hasGamepadSource = false, hasJoystickSource = false),
                LiveControllerProfileSnapshot(unavailableReason = null),
            ),
        )
        val stored = profile("stable", device(runtimeId = 100))
        assertIs<LiveControllerMappingV1.Custom>(resolve(device(runtimeId = 101), listOf(stored)))
    }

    @Test fun nameOnlySimilarityDoesNotIntroduceFuzzyMatching() {
        val storedDevice = device(descriptor = "stored", vendor = 1, product = 2)
        val profile = profile("custom", storedDevice)
        val merelySimilar = device(
            descriptor = "different",
            vendor = 44,
            product = 55,
            name = "Test Controller Pro",
        )
        assertIs<LiveControllerMappingV1.StandardAndroidGamepad>(resolve(merelySimilar, listOf(profile)))
    }

    @Test fun repositoryCacheLoadsOnlyOnExplicitRefreshAndPreservesFailureAsStandard() = runTest {
        val repository = FakeProfileRepository(listOf(profile("custom", device())))
        val cache = LiveControllerProfileCache(repository)
        assertIs<LiveControllerMappingV1.StandardAndroidGamepad>(cache.resolve(device()))
        assertEquals(0, repository.loads)

        cache.refresh()
        assertEquals(1, repository.loads)
        assertIs<LiveControllerMappingV1.Custom>(cache.resolve(device()))
        repeat(20) { cache.resolve(device()) }
        assertEquals(1, repository.loads)

        repository.failure = "Controller profile storage is corrupt."
        cache.refresh()
        assertIs<LiveControllerMappingV1.StandardAndroidGamepad>(cache.resolve(device()))
    }

    @Test fun customButtonsRemapAndReleaseWithoutUsingFixedMapping() {
        val runtime = runtime(profile("buttons", device()).copy(buttonBindings = listOf(
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_A, N64SemanticTargetV1.B),
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_B, N64SemanticTargetV1.A),
        )))
        assertEquals(N64ButtonMask.B, runtime.updateKey(KeyEvent.KEYCODE_BUTTON_A, true).buttons)
        assertNotEquals(
            AndroidGamepadMapping.buttonMask(KeyEvent.KEYCODE_BUTTON_A),
            runtime.updateKey(KeyEvent.KEYCODE_BUTTON_A, true).buttons,
        )
        assertEquals(0u, runtime.updateKey(KeyEvent.KEYCODE_BUTTON_A, false).buttons)
    }

    @Test fun dpadDigitalTriggerAndMultipleInputsUseDeterministicButtonEvaluation() {
        val custom = profile("digital", device()).copy(buttonBindings = listOf(
            ButtonBindingV1(KeyEvent.KEYCODE_DPAD_UP, N64SemanticTargetV1.DPAD_UP),
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_L2, N64SemanticTargetV1.Z),
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_R2, N64SemanticTargetV1.Z),
        ))
        val runtime = runtime(custom)
        assertEquals(N64ButtonMask.DPAD_UP, runtime.updateKey(KeyEvent.KEYCODE_DPAD_UP, true).buttons)
        assertEquals(
            N64ButtonMask.DPAD_UP or N64ButtonMask.Z,
            runtime.updateKey(KeyEvent.KEYCODE_BUTTON_L2, true).buttons,
        )
        assertEquals(
            N64ButtonMask.DPAD_UP or N64ButtonMask.Z,
            runtime.updateKey(KeyEvent.KEYCODE_BUTTON_R2, true).buttons,
        )
        assertEquals(
            N64ButtonMask.DPAD_UP or N64ButtonMask.Z,
            runtime.updateKey(KeyEvent.KEYCODE_BUTTON_L2, false).buttons,
        )
    }

    @Test fun analogInversionDeadzoneAndScaleUseP2Evaluator() {
        val custom = profile("analog", device()).copy(
            buttonBindings = emptyList(),
            axisBindings = listOf(
                AxisBindingV1(MotionEvent.AXIS_X, AxisDirectionV1.BOTH, N64SemanticTargetV1.ANALOG_X, deadZone = 0.2f, scale = 0.5f),
                AxisBindingV1(MotionEvent.AXIS_Y, AxisDirectionV1.BOTH, N64SemanticTargetV1.ANALOG_Y, inverted = true),
            ),
        )
        val runtime = runtime(custom)
        val insideDeadzone = runtime.updateAxes(mapOf(MotionEvent.AXIS_X to 0.1f, MotionEvent.AXIS_Y to 0f))
        assertEquals(0, insideDeadzone.analogX)
        val moved = runtime.updateAxes(mapOf(MotionEvent.AXIS_X to 1f, MotionEvent.AXIS_Y to 0.5f))
        assertEquals(64, moved.analogX)
        assertTrue(moved.analogY < 0)
    }

    @Test fun cDirectionHatAndAnalogTriggerBindingsEvaluateThroughP2() {
        val custom = profile("axes", device()).copy(
            buttonBindings = emptyList(),
            axisBindings = listOf(
                digitalAxis(MotionEvent.AXIS_Z, AxisDirectionV1.POSITIVE, N64SemanticTargetV1.C_RIGHT),
                digitalAxis(MotionEvent.AXIS_HAT_X, AxisDirectionV1.NEGATIVE, N64SemanticTargetV1.DPAD_LEFT),
                digitalAxis(MotionEvent.AXIS_LTRIGGER, AxisDirectionV1.POSITIVE, N64SemanticTargetV1.Z),
            ),
        )
        val result = runtime(custom).updateAxes(mapOf(
            MotionEvent.AXIS_Z to 0.9f,
            MotionEvent.AXIS_HAT_X to -1f,
            MotionEvent.AXIS_LTRIGGER to 1f,
        ))
        assertEquals(
            N64ButtonMask.C_RIGHT or N64ButtonMask.DPAD_LEFT or N64ButtonMask.Z,
            result.buttons,
        )
    }

    @Test fun hysteresisAndProfileSwitchResetAllTransientState() {
        val customA = profile("a", device()).copy(
            buttonBindings = emptyList(),
            axisBindings = listOf(digitalAxis(
                MotionEvent.AXIS_X, AxisDirectionV1.POSITIVE, N64SemanticTargetV1.C_RIGHT,
            )),
        )
        val customB = profile("b", device()).copy(buttonBindings = listOf(
            ButtonBindingV1(KeyEvent.KEYCODE_BUTTON_A, N64SemanticTargetV1.A),
        ))
        val capabilities = device().toControllerCapabilitiesV1()
        val runtime = LiveCustomControllerRuntime()
        assertTrue(runtime.activate(customA, capabilities))
        assertEquals(N64ButtonMask.C_RIGHT, runtime.updateAxes(mapOf(MotionEvent.AXIS_X to 0.7f)).buttons)
        assertEquals(N64ButtonMask.C_RIGHT, runtime.updateAxes(mapOf(MotionEvent.AXIS_X to 0.5f)).buttons)
        assertEquals(0u, runtime.updateAxes(mapOf(MotionEvent.AXIS_X to 0.45f)).buttons)

        assertTrue(runtime.activate(customB, capabilities))
        assertEquals(0u, runtime.updateAxes(emptyMap()).buttons)
        assertEquals(N64ButtonMask.A, runtime.updateKey(KeyEvent.KEYCODE_BUTTON_A, true).buttons)
        runtime.deactivate()
        assertTrue(runtime.activate(customA, capabilities))
        assertEquals(0u, runtime.updateAxes(emptyMap()).buttons)
    }

    @Test fun preferredAxisAlternativeSelectsSupportedLowestPriorityVariant() {
        val custom = profile("alternative", device()).copy(
            buttonBindings = emptyList(),
            axisBindings = emptyList(),
            axisAlternatives = listOf(
                AxisBindingAlternativeV1(
                    "right-x", "z", 0, setOf(MotionEvent.AXIS_Z),
                    listOf(digitalAxis(MotionEvent.AXIS_Z, AxisDirectionV1.POSITIVE, N64SemanticTargetV1.C_RIGHT)),
                ),
                AxisBindingAlternativeV1(
                    "right-x", "rx", 1, setOf(MotionEvent.AXIS_RX),
                    listOf(digitalAxis(MotionEvent.AXIS_RX, AxisDirectionV1.POSITIVE, N64SemanticTargetV1.C_RIGHT)),
                ),
            ),
        )
        val result = runtime(custom).updateAxes(mapOf(
            MotionEvent.AXIS_Z to 0.8f,
            MotionEvent.AXIS_RX to 0f,
        ))
        assertEquals(N64ButtonMask.C_RIGHT, result.buttons)
    }

    private fun resolve(
        device: BridgeControllerDiagnosticDevice,
        profiles: List<ControllerProfileV1>,
        selected: String? = null,
    ) = LiveControllerMappingResolverV1.resolve(
        device,
        LiveControllerProfileSnapshot(profiles, selected, unavailableReason = null),
    )

    private fun runtime(profile: ControllerProfileV1) = LiveCustomControllerRuntime().apply {
        assertTrue(activate(profile, device().toControllerCapabilitiesV1()))
    }

    private fun profile(id: String, device: BridgeControllerDiagnosticDevice) =
        StandardAndroidGamepadProfile.profile.copy(
            profileId = id,
            profileName = "Profile $id",
            identity = device.toStableIdentity(),
        )

    private fun device(
        runtimeId: Int = 100,
        descriptor: String = "controller-descriptor",
        vendor: Int = 0x057e,
        product: Int = 0x2009,
        name: String = "Nintendo Switch Pro Controller",
    ) = BridgeControllerDiagnosticDevice(
        runtimeDeviceId = runtimeId,
        descriptor = descriptor,
        name = name,
        vendorId = vendor,
        productId = product,
        sources = InputDevice.SOURCE_GAMEPAD or InputDevice.SOURCE_JOYSTICK,
        hasGamepadSource = true,
        hasJoystickSource = true,
        controllerNumber = 1,
        motionRanges = listOf(
            range(MotionEvent.AXIS_X), range(MotionEvent.AXIS_Y),
            range(MotionEvent.AXIS_Z), range(MotionEvent.AXIS_RX),
            range(MotionEvent.AXIS_RZ), range(MotionEvent.AXIS_RY),
            range(MotionEvent.AXIS_HAT_X), range(MotionEvent.AXIS_HAT_Y),
            BridgeControllerMotionRange(
                MotionEvent.AXIS_LTRIGGER, "AXIS_LTRIGGER", 0f, 1f, 0f,
                InputDevice.SOURCE_JOYSTICK,
            ),
        ),
    )

    private fun range(axis: Int) = BridgeControllerMotionRange(
        axis, "AXIS_$axis", -1f, 1f, 0.08f, InputDevice.SOURCE_JOYSTICK,
    )

    private fun digitalAxis(axis: Int, direction: AxisDirectionV1, target: N64SemanticTargetV1) =
        AxisBindingV1(
            axisCode = axis,
            direction = direction,
            target = target,
            pressThreshold = 0.50f,
            releaseThreshold = 0.45f,
        )

    private class FakeProfileRepository(
        var profiles: List<ControllerProfileV1>,
    ) : ControllerProfileUiRepository {
        var loads = 0
        var failure: String? = null
        override suspend fun load(): ControllerProfileUiRepositorySnapshot {
            loads++
            failure?.let { throw ControllerProfileUiRepositoryException(it) }
            return ControllerProfileUiRepositorySnapshot(profiles, null, null)
        }
        override suspend fun create(profile: ControllerProfileV1) = Unit
        override suspend fun update(profile: ControllerProfileV1) = Unit
        override suspend fun select(profileId: String?) = Unit
        override suspend fun delete(profileId: String) = Unit
        override suspend fun selectController(identity: com.melfegor.nice64.core.input.ControllerIdentityV1?) = Unit
    }
}
