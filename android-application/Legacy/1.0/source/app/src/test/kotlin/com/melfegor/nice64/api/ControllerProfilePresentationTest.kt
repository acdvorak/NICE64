package com.melfegor.nice64.api

import android.view.InputDevice
import android.view.MotionEvent
import com.melfegor.nice64.core.input.ControllerIdentityV1
import com.melfegor.nice64.core.input.ControllerProfileV1
import com.melfegor.nice64.core.input.ControllerSourceClassV1
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.launch
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ControllerProfilePresentationTest {
    @Test fun eligibleControllerAndActiveRuntimePresentationAreDistinctFromManagementSelection() = runTest {
        val repository = FakeUiRepository()
        val coordinator = ControllerProfilePresentationCoordinator(repository)
        coordinator.initialize(diagnostics(listOf(device(7), device(8)), active = 8))
        assertEquals(8, coordinator.state.value.activeRuntimeDeviceId)
        assertEquals(8, coordinator.state.value.managementRuntimeDeviceId)
        coordinator.selectManagementController(7)
        assertEquals(8, coordinator.state.value.activeRuntimeDeviceId)
        assertEquals(7, coordinator.state.value.managementRuntimeDeviceId)
        assertEquals("descriptor-7", repository.snapshot.selectedControllerIdentity?.descriptor)
    }

    @Test fun initializationRestoresPersistedStableManagementIdentityBeforeRuntimeFallback() = runTest {
        val repository = FakeUiRepository(
            ControllerProfileUiRepositorySnapshot(
                profiles = emptyList(),
                selectedProfileId = null,
                selectedControllerIdentity = identity(8),
            ),
        )
        val coordinator = ControllerProfilePresentationCoordinator(repository)

        coordinator.initialize(diagnostics(listOf(device(7), device(8)), active = 7))

        assertEquals(7, coordinator.state.value.activeRuntimeDeviceId)
        assertEquals(8, coordinator.state.value.managementRuntimeDeviceId)
        assertEquals(identity(8), coordinator.state.value.persistedControllerIdentity)
    }

    @Test fun noMatchShowsBuiltInDefaultAndDoesNotStoreIt() = runTest {
        val repository = FakeUiRepository()
        val coordinator = ControllerProfilePresentationCoordinator(repository)
        coordinator.initialize(diagnostics(listOf(device(7))))
        assertEquals(ControllerProfileResolutionKind.STANDARD_DEFAULT, coordinator.state.value.resolutionKind)
        assertTrue(repository.snapshot.profiles.isEmpty())
        assertNull(repository.snapshot.selectedProfileId)
    }

    @Test fun exactFallbackAndAmbiguousMatchesArePresentedExplicitly() = runTest {
        val exact = profile("exact", identity(7))
        val fallbackIdentity = identity(99).copy(descriptor = "old")
        val repository = FakeUiRepository(ControllerProfileUiRepositorySnapshot(
            listOf(exact, profile("fallback", fallbackIdentity)), null, null,
        ))
        val coordinator = ControllerProfilePresentationCoordinator(repository)
        coordinator.initialize(diagnostics(listOf(device(7))))
        assertEquals(ControllerProfileResolutionKind.EXACT, coordinator.state.value.resolutionKind)
        assertEquals(listOf("exact"), coordinator.state.value.resolutionProfileIds)

        repository.snapshot = repository.snapshot.copy(profiles = listOf(profile("fallback", fallbackIdentity)))
        coordinator.refresh()
        assertEquals(ControllerProfileResolutionKind.FALLBACK, coordinator.state.value.resolutionKind)

        repository.snapshot = repository.snapshot.copy(profiles = listOf(
            profile("a", fallbackIdentity), profile("b", fallbackIdentity.copy(descriptor = "other")),
        ))
        coordinator.refresh()
        assertEquals(ControllerProfileResolutionKind.AMBIGUOUS, coordinator.state.value.resolutionKind)
        assertEquals(listOf("a", "b"), coordinator.state.value.resolutionProfileIds)
    }

    @Test fun createClonesValidDefaultUsesUniqueIdAndSelectsIt() = runTest {
        val repository = FakeUiRepository(ControllerProfileUiRepositorySnapshot(
            listOf(profile("duplicate", identity(7))), null, null,
        ))
        val ids = ArrayDeque(listOf("duplicate", "unique"))
        val coordinator = ControllerProfilePresentationCoordinator(repository) { ids.removeFirst() }
        coordinator.initialize(diagnostics(listOf(device(7))))
        val builtInBefore = StandardAndroidGamepadProfile.profile
        coordinator.createProfile(" My Pro ")
        val created = repository.snapshot.profiles.single { it.profileId == "unique" }
        assertEquals("My Pro", created.profileName)
        assertEquals(identity(7), created.identity)
        assertEquals("unique", repository.snapshot.selectedProfileId)
        assertEquals(builtInBefore, StandardAndroidGamepadProfile.profile)
        assertNotEquals(StandardAndroidGamepadProfile.profile.profileId, created.profileId)
    }

    @Test fun blankCreateFailsSafelyWithoutWrite() = runTest {
        val repository = FakeUiRepository()
        val coordinator = ControllerProfilePresentationCoordinator(repository)
        coordinator.initialize(diagnostics(listOf(device(7))))
        coordinator.createProfile("   ")
        assertEquals(0, repository.createCount)
        assertEquals("Profile name is required.", coordinator.state.value.errorMessage)
    }

    @Test fun selectCustomReloadAndBuiltInSelectionPersist() = runTest {
        val repository = FakeUiRepository(ControllerProfileUiRepositorySnapshot(
            listOf(profile("one", identity(7))), null, null,
        ))
        val coordinator = ControllerProfilePresentationCoordinator(repository)
        coordinator.initialize(diagnostics(listOf(device(7))))
        coordinator.selectProfile("one")
        assertEquals("one", coordinator.state.value.selectedProfileId)
        val recreated = ControllerProfilePresentationCoordinator(repository)
        recreated.initialize(diagnostics(listOf(device(7))))
        assertEquals("one", recreated.state.value.selectedProfileId)
        recreated.selectProfile(null)
        assertNull(recreated.state.value.selectedProfileId)
        assertFalse(repository.snapshot.profiles.any { it.profileId == StandardAndroidGamepadProfile.profile.profileId })
    }

    @Test fun deleteSelectedCustomClearsSelectionAndCannotTargetBuiltIn() = runTest {
        val repository = FakeUiRepository(ControllerProfileUiRepositorySnapshot(
            listOf(profile("one", identity(7))), "one", null,
        ))
        val coordinator = ControllerProfilePresentationCoordinator(repository)
        coordinator.initialize(diagnostics(listOf(device(7))))
        coordinator.deleteProfile("one")
        assertTrue(coordinator.state.value.profiles.isEmpty())
        assertNull(coordinator.state.value.selectedProfileId)
        coordinator.deleteProfile(StandardAndroidGamepadProfile.profile.profileId)
        assertEquals(1, repository.deleteCount)
        assertEquals("The controller profile no longer exists.", coordinator.state.value.errorMessage)
    }

    @Test fun duplicateActionIsBlockedWhileFirstIsInFlight() = runTest {
        val repository = FakeUiRepository()
        val gate = CompletableDeferred<Unit>()
        repository.createGate = gate
        val ids = ArrayDeque(listOf("one", "two"))
        val coordinator = ControllerProfilePresentationCoordinator(repository) { ids.removeFirst() }
        coordinator.initialize(diagnostics(listOf(device(7))))
        val first = async { coordinator.createProfile("One") }
        runCurrent()
        val second = launch { coordinator.createProfile("Two") }
        runCurrent()
        assertEquals(1, repository.createCount)
        gate.complete(Unit)
        first.await()
        second.join()
        assertEquals(listOf("One"), repository.snapshot.profiles.map { it.profileName })
    }

    @Test fun repositoryFailureAndUnsupportedStateAreShownSafely() = runTest {
        val repository = FakeUiRepository().apply { loadFailure = "Controller profiles use a newer unsupported format." }
        val coordinator = ControllerProfilePresentationCoordinator(repository)
        coordinator.initialize(diagnostics(listOf(device(7))))
        assertEquals(ControllerProfileResolutionKind.ERROR, coordinator.state.value.resolutionKind)
        assertEquals("Controller profiles use a newer unsupported format.", coordinator.state.value.errorMessage)
        repository.loadFailure = "Controller profile storage is corrupt."
        coordinator.refresh()
        assertEquals("Controller profile storage is corrupt.", coordinator.state.value.errorMessage)
    }

    @Test fun recreationOnlyReloadsAndDoesNotDuplicateWrites() = runTest {
        val repository = FakeUiRepository()
        val first = ControllerProfilePresentationCoordinator(repository) { "one" }
        first.initialize(diagnostics(listOf(device(7))))
        first.createProfile("One")
        val writes = repository.createCount
        val recreated = ControllerProfilePresentationCoordinator(repository)
        recreated.initialize(diagnostics(listOf(device(7))))
        assertEquals(writes, repository.createCount)
        assertEquals(listOf("One"), recreated.state.value.profiles.map { it.profileName })
    }

    @Test fun keyboardMouseNegativeCaseRemainsOutsideUiInventory() = runTest {
        assertFalse(BridgeControllerDeviceProjection.isEligibleSource(
            InputDevice.SOURCE_KEYBOARD or InputDevice.SOURCE_MOUSE,
        ))
        val coordinator = ControllerProfilePresentationCoordinator(FakeUiRepository())
        coordinator.initialize(diagnostics(emptyList()))
        assertTrue(coordinator.state.value.devices.isEmpty())
        assertEquals(ControllerProfileResolutionKind.INELIGIBLE, coordinator.state.value.resolutionKind)
    }

    private class FakeUiRepository(
        var snapshot: ControllerProfileUiRepositorySnapshot =
            ControllerProfileUiRepositorySnapshot(emptyList(), null, null),
    ) : ControllerProfileUiRepository {
        var createCount = 0
        var deleteCount = 0
        var createGate: CompletableDeferred<Unit>? = null
        var loadFailure: String? = null

        override suspend fun load(): ControllerProfileUiRepositorySnapshot {
            loadFailure?.let { throw ControllerProfileUiRepositoryException(it) }
            return snapshot
        }
        override suspend fun create(profile: ControllerProfileV1) {
            createCount++
            createGate?.await()
            snapshot = snapshot.copy(profiles = snapshot.profiles + profile)
        }
        override suspend fun update(profile: ControllerProfileV1) {
            snapshot = snapshot.copy(profiles = snapshot.profiles.map {
                if (it.profileId == profile.profileId) profile else it
            })
        }
        override suspend fun select(profileId: String?) {
            snapshot = snapshot.copy(selectedProfileId = profileId)
        }
        override suspend fun delete(profileId: String) {
            deleteCount++
            snapshot = snapshot.copy(
                profiles = snapshot.profiles.filterNot { it.profileId == profileId },
                selectedProfileId = snapshot.selectedProfileId?.takeUnless { it == profileId },
            )
        }
        override suspend fun selectController(identity: ControllerIdentityV1?) {
            snapshot = snapshot.copy(selectedControllerIdentity = identity)
        }
    }

    private fun diagnostics(devices: List<BridgeControllerDiagnosticDevice>, active: Int? = null) =
        BridgeControllerDiagnosticsPresentation(devices = devices, activeRuntimeDeviceId = active)

    private fun device(id: Int) = BridgeControllerDiagnosticDevice(
        runtimeDeviceId = id,
        descriptor = "descriptor-$id",
        name = "Nintendo Switch Pro Controller",
        vendorId = 0x057e,
        productId = 0x2009,
        sources = InputDevice.SOURCE_GAMEPAD or InputDevice.SOURCE_JOYSTICK,
        hasGamepadSource = true,
        hasJoystickSource = true,
        controllerNumber = 1,
        motionRanges = listOf(
            BridgeControllerMotionRange(MotionEvent.AXIS_X, "AXIS_X", -1f, 1f, 0.015f, InputDevice.SOURCE_JOYSTICK),
            BridgeControllerMotionRange(MotionEvent.AXIS_Y, "AXIS_Y", -1f, 1f, 0.015f, InputDevice.SOURCE_JOYSTICK),
            BridgeControllerMotionRange(MotionEvent.AXIS_Z, "AXIS_Z", -1f, 1f, 0.015f, InputDevice.SOURCE_JOYSTICK),
            BridgeControllerMotionRange(MotionEvent.AXIS_RZ, "AXIS_RZ", -1f, 1f, 0.015f, InputDevice.SOURCE_JOYSTICK),
        ),
    )

    private fun identity(id: Int) = device(id).toStableIdentity()

    private fun profile(id: String, identity: ControllerIdentityV1) =
        StandardAndroidGamepadProfile.profile.copy(
            profileId = id,
            profileName = "Profile $id",
            identity = identity,
        )
}
