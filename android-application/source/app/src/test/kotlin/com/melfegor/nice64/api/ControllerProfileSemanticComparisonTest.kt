package com.melfegor.nice64.api

import com.melfegor.nice64.core.input.ControllerIdentityV1
import com.melfegor.nice64.core.input.ControllerProfileV1
import com.melfegor.nice64.core.input.ControllerSourceClassV1
import com.melfegor.nice64.core.input.N64SemanticTargetV1
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ControllerProfileSemanticComparisonTest {
    @Test fun reorderedBindingsAlternativesAndRequiredAxesAreSemanticallyEqual() {
        val requested = profile()
        val authoritative = requested.copy(
            buttonBindings = requested.buttonBindings.reversed(),
            axisBindings = requested.axisBindings.reversed(),
            axisAlternatives = requested.axisAlternatives.reversed().map { alternative ->
                alternative.copy(
                    requiredAxes = linkedSetOf(*alternative.requiredAxes.reversed().toTypedArray()),
                    bindings = alternative.bindings.reversed(),
                )
            },
        )

        assertTrue(compareControllerProfilesSemantically(requested, authoritative).equivalent)
    }

    @Test fun actualBindingIdentityNameAndIdDifferencesFailWithBoundedFieldDiff() {
        val requested = profile()
        val changedBinding = requested.copy(
            buttonBindings = requested.buttonBindings.mapIndexed { index, binding ->
                if (index == 0) binding.copy(target = N64SemanticTargetV1.Z) else binding
            },
        )
        assertEquals(
            listOf("buttonBindings"),
            compareControllerProfilesSemantically(requested, changedBinding).differingFields,
        )

        val changedIdentity = requested.copy(identity = requested.identity!!.copy(descriptor = "other"))
        assertEquals(
            listOf("identity"),
            compareControllerProfilesSemantically(requested, changedIdentity).differingFields,
        )
        assertEquals(
            listOf("profileName"),
            compareControllerProfilesSemantically(requested, requested.copy(profileName = "Other")).differingFields,
        )
        assertEquals(
            listOf("profileId"),
            compareControllerProfilesSemantically(requested, requested.copy(profileId = "other")).differingFields,
        )
    }

    @Test fun canonicalizedRepositoryRoundTripSavesOnceReloadsAndMarksClean() = runTest {
        val repository = CanonicalizingRepository(profile())
        val editor = ControllerMappingEditorCoordinator(repository)
        editor.open("custom", null)
        editor.setProfileName("Saved")

        assertTrue(editor.save())
        assertEquals(1, repository.updateCount)
        assertEquals(2, repository.loadCount)
        assertFalse(editor.state.value.dirty)
        assertEquals("Saved", editor.state.value.working!!.profileName)
        assertEquals("Profile saved and reloaded.", editor.state.value.statusMessage)
    }

    @Test fun missingAuthoritativeProfileFailsClosedAfterOneUpdateAndReload() = runTest {
        val repository = CanonicalizingRepository(profile()).apply { dropAfterUpdate = true }
        val editor = ControllerMappingEditorCoordinator(repository)
        editor.open("custom", null)
        editor.setProfileName("Recoverable")

        assertFalse(editor.save())
        assertEquals(1, repository.updateCount)
        assertEquals(2, repository.loadCount)
        assertEquals("Recoverable", editor.state.value.working!!.profileName)
        assertTrue(editor.state.value.dirty)
        assertTrue(editor.state.value.errorMessage!!.contains("could not be reloaded"))
    }

    @Test fun trueSemanticRoundTripMismatchFailsClosedWithoutAutomaticSecondSave() = runTest {
        val repository = CanonicalizingRepository(profile()).apply { alterAfterUpdate = true }
        val editor = ControllerMappingEditorCoordinator(repository)
        editor.open("custom", null)
        editor.setProfileName("Requested")

        assertFalse(editor.save())
        assertEquals(1, repository.updateCount)
        assertEquals(2, repository.loadCount)
        assertEquals("Requested", editor.state.value.working!!.profileName)
        assertTrue(editor.state.value.errorMessage!!.contains("profileName"))
    }

    @Test fun repositoryFailurePreservesRecoverableWorkingCopyAndDoesNotReload() = runTest {
        val repository = CanonicalizingRepository(profile()).apply { failUpdate = true }
        val editor = ControllerMappingEditorCoordinator(repository)
        editor.open("custom", null)
        editor.setProfileName("Recoverable")

        assertFalse(editor.save())
        assertEquals(0, repository.updateCount)
        assertEquals(1, repository.loadCount)
        assertEquals("Recoverable", editor.state.value.working!!.profileName)
        assertTrue(editor.state.value.dirty)
    }

    private fun profile(): ControllerProfileV1 = StandardAndroidGamepadProfile.profile.copy(
        profileId = "custom",
        profileName = "Custom",
        identity = ControllerIdentityV1(
            descriptor = "switch-pro",
            vendorId = 0x057e,
            productId = 0x2009,
            normalizedName = "nintendo switch pro controller",
            sourceClass = ControllerSourceClassV1.GAMEPAD_AND_JOYSTICK,
            motionSignature = "0,1,11,14,15,16",
        ),
    )

    private class CanonicalizingRepository(profile: ControllerProfileV1) : ControllerProfileUiRepository {
        private var stored: ControllerProfileV1? = profile
        var updateCount = 0
        var loadCount = 0
        var failUpdate = false
        var dropAfterUpdate = false
        var alterAfterUpdate = false

        override suspend fun load(): ControllerProfileUiRepositorySnapshot {
            loadCount++
            return ControllerProfileUiRepositorySnapshot(listOfNotNull(stored), stored?.profileId, stored?.identity)
        }

        override suspend fun update(profile: ControllerProfileV1) {
            if (failUpdate) throw ControllerProfileUiRepositoryException("Controller profile storage is unavailable.")
            updateCount++
            stored = when {
                dropAfterUpdate -> null
                alterAfterUpdate -> profile.copy(profileName = "Different")
                else -> profile.copy(
                    buttonBindings = profile.buttonBindings.reversed(),
                    axisBindings = profile.axisBindings.reversed(),
                    axisAlternatives = profile.axisAlternatives.reversed().map { alternative ->
                        alternative.copy(
                            requiredAxes = linkedSetOf(*alternative.requiredAxes.reversed().toTypedArray()),
                            bindings = alternative.bindings.reversed(),
                        )
                    },
                )
            }
        }

        override suspend fun create(profile: ControllerProfileV1) = Unit
        override suspend fun select(profileId: String?) = Unit
        override suspend fun delete(profileId: String) = Unit
        override suspend fun selectController(identity: ControllerIdentityV1?) = Unit
    }
}
