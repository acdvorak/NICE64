package com.melfegor.nice64.api

import com.melfegor.nice64.core.input.ControllerIdentityV1
import com.melfegor.nice64.core.input.ControllerProfileV1
import com.melfegor.nice64.core.input.ControllerSourceClassV1
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

internal enum class ControllerProfileResolutionKind {
    EXACT,
    FALLBACK,
    AMBIGUOUS,
    STANDARD_DEFAULT,
    INELIGIBLE,
    ERROR,
}

internal data class ControllerProfileUiItem(
    val profileId: String,
    val profileName: String,
    val identitySummary: String,
    val selected: Boolean,
)

internal data class ControllerProfilePresentationState(
    val loading: Boolean = true,
    val actionInFlight: Boolean = false,
    val errorMessage: String? = null,
    val devices: List<BridgeControllerDiagnosticDevice> = emptyList(),
    val activeRuntimeDeviceId: Int? = null,
    val managementRuntimeDeviceId: Int? = null,
    val persistedControllerIdentity: ControllerIdentityV1? = null,
    val profiles: List<ControllerProfileUiItem> = emptyList(),
    val selectedProfileId: String? = null,
    val resolutionKind: ControllerProfileResolutionKind = ControllerProfileResolutionKind.INELIGIBLE,
    val resolutionProfileIds: List<String> = emptyList(),
) {
    val managementDevice: BridgeControllerDiagnosticDevice?
        get() = devices.firstOrNull { it.runtimeDeviceId == managementRuntimeDeviceId }
    val activeDevice: BridgeControllerDiagnosticDevice?
        get() = devices.firstOrNull { it.runtimeDeviceId == activeRuntimeDeviceId }
}

internal data class ControllerProfileUiRepositorySnapshot(
    val profiles: List<ControllerProfileV1>,
    val selectedProfileId: String?,
    val selectedControllerIdentity: ControllerIdentityV1?,
)

internal interface ControllerProfileUiRepository {
    suspend fun load(): ControllerProfileUiRepositorySnapshot
    suspend fun create(profile: ControllerProfileV1)
    suspend fun update(profile: ControllerProfileV1)
    suspend fun select(profileId: String?)
    suspend fun delete(profileId: String)
    suspend fun selectController(identity: ControllerIdentityV1?)
}

internal class ControllerProfileUiRepositoryException(message: String) : Exception(message)

internal class PersistedControllerProfileUiRepository(
    private val repository: ControllerProfileRepositoryV1,
) : ControllerProfileUiRepository {
    override suspend fun load(): ControllerProfileUiRepositorySnapshot {
        val loaded = repository.loadDocument()
        val document = when (loaded) {
            is ControllerProfilesLoadResultV1.Success -> loaded.document
            is ControllerProfilesLoadResultV1.UnsupportedVersion ->
                throw ControllerProfileUiRepositoryException("Controller profiles use a newer unsupported format.")
            ControllerProfilesLoadResultV1.Corrupt ->
                throw ControllerProfileUiRepositoryException("Controller profile storage is corrupt.")
            ControllerProfilesLoadResultV1.DuplicateProfileIds ->
                throw ControllerProfileUiRepositoryException("Controller profile storage contains duplicate IDs.")
        }
        return ControllerProfileUiRepositorySnapshot(
            document.profiles,
            document.selectedProfileId,
            document.selectedControllerIdentity,
        )
    }

    override suspend fun create(profile: ControllerProfileV1) {
        requireSuccess(repository.saveProfile(profile))
    }

    override suspend fun update(profile: ControllerProfileV1) {
        requireSuccess(repository.saveProfile(profile, ControllerProfileSaveModeV1.UPDATE))
    }

    override suspend fun select(profileId: String?) {
        requireSuccess(repository.setSelectedProfile(profileId))
    }

    override suspend fun delete(profileId: String) {
        requireSuccess(repository.deleteProfile(profileId))
    }

    override suspend fun selectController(identity: ControllerIdentityV1?) {
        requireSuccess(repository.setSelectedControllerIdentity(identity))
    }

    private fun requireSuccess(result: ControllerProfileRepositoryResultV1<*>) {
        if (result is ControllerProfileRepositoryResultV1.Failure) {
            throw ControllerProfileUiRepositoryException(
                when (result.error) {
                    ControllerProfileRepositoryErrorV1.INVALID_PROFILE -> "The controller profile is invalid."
                    ControllerProfileRepositoryErrorV1.DUPLICATE_PROFILE_ID -> "That controller profile already exists."
                    ControllerProfileRepositoryErrorV1.PROFILE_NOT_FOUND -> "The controller profile no longer exists."
                    ControllerProfileRepositoryErrorV1.CORRUPT_STORAGE -> "Controller profile storage is corrupt."
                    ControllerProfileRepositoryErrorV1.UNSUPPORTED_SCHEMA -> "Controller profiles use a newer unsupported format."
                    ControllerProfileRepositoryErrorV1.DUPLICATE_STORED_IDS -> "Controller profile storage contains duplicate IDs."
                    ControllerProfileRepositoryErrorV1.STORAGE_FAILURE -> "Controller profile storage is unavailable."
                },
            )
        }
    }
}

internal fun interface ControllerProfileIdFactory {
    fun create(): String
}

internal class ControllerProfilePresentationCoordinator(
    private val repository: ControllerProfileUiRepository,
    private val idFactory: ControllerProfileIdFactory = ControllerProfileIdFactory {
        "controller-${UUID.randomUUID()}"
    },
) {
    private val mutableState = MutableStateFlow(ControllerProfilePresentationState())
    val state: StateFlow<ControllerProfilePresentationState> = mutableState.asStateFlow()
    private val action = AtomicBoolean(false)
    private var snapshot = ControllerProfileUiRepositorySnapshot(emptyList(), null, null)

    suspend fun initialize(diagnostics: BridgeControllerDiagnosticsPresentation) {
        refresh()
        // Repository identity is authoritative for profile-management selection. Load it
        // before choosing among connected runtime devices so recreation cannot briefly
        // promote the active/first device over the persisted stable identity.
        updateInventory(diagnostics)
    }

    suspend fun updateInventory(diagnostics: BridgeControllerDiagnosticsPresentation) {
        val current = mutableState.value.managementRuntimeDeviceId
        val persisted = snapshot.selectedControllerIdentity
        val selected = when {
            diagnostics.devices.any { it.runtimeDeviceId == current } -> current
            persisted != null -> diagnostics.devices.firstOrNull { it.toStableIdentity().sameStableDevice(persisted) }
                ?.runtimeDeviceId
            diagnostics.activeRuntimeDeviceId != null -> diagnostics.activeRuntimeDeviceId
            else -> diagnostics.devices.firstOrNull()?.runtimeDeviceId
        }
        mutableState.value = present(
            devices = diagnostics.devices,
            activeRuntimeDeviceId = diagnostics.activeRuntimeDeviceId,
            managementRuntimeDeviceId = selected,
            loading = mutableState.value.loading,
            actionInFlight = mutableState.value.actionInFlight,
            error = mutableState.value.errorMessage,
        )
    }

    suspend fun refresh() {
        mutableState.value = mutableState.value.copy(loading = true, errorMessage = null)
        try {
            snapshot = repository.load()
            mutableState.value = present(
                mutableState.value.devices,
                mutableState.value.activeRuntimeDeviceId,
                mutableState.value.managementRuntimeDeviceId,
                loading = false,
                actionInFlight = mutableState.value.actionInFlight,
                error = null,
            )
        } catch (failure: ControllerProfileUiRepositoryException) {
            mutableState.value = mutableState.value.copy(
                loading = false,
                errorMessage = failure.message ?: "Controller profiles are unavailable.",
                resolutionKind = ControllerProfileResolutionKind.ERROR,
            )
        }
    }

    suspend fun selectManagementController(runtimeDeviceId: Int) = runAction {
        val device = mutableState.value.devices.firstOrNull { it.runtimeDeviceId == runtimeDeviceId }
            ?: throw ControllerProfileUiRepositoryException("The controller is no longer connected.")
        repository.selectController(device.toStableIdentity())
        mutableState.value = mutableState.value.copy(managementRuntimeDeviceId = runtimeDeviceId)
    }

    suspend fun createProfile(name: String) = runAction {
        val trimmed = name.trim()
        if (trimmed.isEmpty()) throw ControllerProfileUiRepositoryException("Profile name is required.")
        val identity = mutableState.value.managementDevice?.toStableIdentity()
            ?: throw ControllerProfileUiRepositoryException("Select an eligible connected controller first.")
        var id: String
        do id = idFactory.create() while (snapshot.profiles.any { it.profileId == id })
        val profile = StandardAndroidGamepadProfile.profile.copy(
            profileId = id,
            profileName = trimmed,
            identity = identity,
        )
        repository.create(profile)
        repository.select(profile.profileId)
    }

    suspend fun selectProfile(profileId: String?) = runAction {
        if (profileId != null && snapshot.profiles.none { it.profileId == profileId }) {
            throw ControllerProfileUiRepositoryException("The controller profile no longer exists.")
        }
        repository.select(profileId)
    }

    suspend fun deleteProfile(profileId: String) = runAction {
        if (snapshot.profiles.none { it.profileId == profileId }) {
            throw ControllerProfileUiRepositoryException("The controller profile no longer exists.")
        }
        repository.delete(profileId)
    }

    private suspend fun runAction(block: suspend () -> Unit) {
        if (!action.compareAndSet(false, true)) return
        mutableState.value = mutableState.value.copy(actionInFlight = true, errorMessage = null)
        try {
            block()
            snapshot = repository.load()
            mutableState.value = present(
                mutableState.value.devices,
                mutableState.value.activeRuntimeDeviceId,
                mutableState.value.managementRuntimeDeviceId,
                loading = false,
                actionInFlight = true,
                error = null,
            )
        } catch (failure: ControllerProfileUiRepositoryException) {
            mutableState.value = mutableState.value.copy(
                errorMessage = failure.message ?: "Controller profile action failed safely.",
            )
        } finally {
            mutableState.value = mutableState.value.copy(actionInFlight = false)
            action.set(false)
        }
    }

    private fun present(
        devices: List<BridgeControllerDiagnosticDevice>,
        activeRuntimeDeviceId: Int?,
        managementRuntimeDeviceId: Int?,
        loading: Boolean,
        actionInFlight: Boolean,
        error: String?,
    ): ControllerProfilePresentationState {
        val selectedDevice = devices.firstOrNull { it.runtimeDeviceId == managementRuntimeDeviceId }
        val match = selectedDevice?.toStableIdentity()?.let {
            ControllerProfileMatcherV1.match(it, snapshot.profiles)
        }
        val (kind, ids) = when (match) {
            is ControllerProfileMatchResultV1.ExactMatch ->
                ControllerProfileResolutionKind.EXACT to listOf(match.profileId)
            is ControllerProfileMatchResultV1.FallbackMatch ->
                ControllerProfileResolutionKind.FALLBACK to listOf(match.profileId)
            is ControllerProfileMatchResultV1.Ambiguous ->
                ControllerProfileResolutionKind.AMBIGUOUS to match.candidateIds
            ControllerProfileMatchResultV1.NoMatch ->
                ControllerProfileResolutionKind.STANDARD_DEFAULT to emptyList()
            null -> ControllerProfileResolutionKind.INELIGIBLE to emptyList()
        }
        return ControllerProfilePresentationState(
            loading = loading,
            actionInFlight = actionInFlight,
            errorMessage = error,
            devices = devices,
            activeRuntimeDeviceId = activeRuntimeDeviceId,
            managementRuntimeDeviceId = selectedDevice?.runtimeDeviceId,
            persistedControllerIdentity = snapshot.selectedControllerIdentity,
            profiles = snapshot.profiles.sortedBy { it.profileName.lowercase() }.map { profile ->
                ControllerProfileUiItem(
                    profile.profileId,
                    profile.profileName,
                    profile.identity?.let { "VID %04X / PID %04X — %s".format(it.vendorId, it.productId, it.normalizedName) }
                        ?: "Any eligible controller",
                    snapshot.selectedProfileId == profile.profileId,
                )
            },
            selectedProfileId = snapshot.selectedProfileId,
            resolutionKind = if (error == null) kind else ControllerProfileResolutionKind.ERROR,
            resolutionProfileIds = ids,
        )
    }
}

internal fun BridgeControllerDiagnosticDevice.toStableIdentity() = ControllerIdentityV1(
    descriptor = descriptor,
    vendorId = vendorId,
    productId = productId,
    normalizedName = ControllerIdentityNormalizerV1.normalizeName(name),
    sourceClass = ControllerSourceClassV1.fromSources(hasGamepadSource, hasJoystickSource)
        ?: error("P1 inventory admitted an ineligible controller"),
    motionSignature = ControllerMotionSignatureV1.fromAxisCodes(motionRanges.map { it.axisCode }),
)

private fun ControllerIdentityV1.sameStableDevice(other: ControllerIdentityV1): Boolean =
    descriptor.isNotBlank() && descriptor == other.descriptor &&
        vendorId == other.vendorId && productId == other.productId
