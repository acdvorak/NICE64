package com.melfegor.nice64.pairing

import com.melfegor.nice64.core.model.BridgeOnboardingCandidate
import com.melfegor.nice64.core.model.BridgePairingMetadata

enum class PairingScreen {
    PROFILES,
    SCANNER,
    CONFIRMATION,
    SAVING,
    ERROR,
}

enum class CameraPermissionState {
    UNKNOWN,
    GRANTED,
    DENIED,
    PERMANENTLY_DENIED,
}

enum class PairingPresentationError(val safeMessage: String) {
    INVALID_QR("This QR code is not a valid NICE64 Bridge pairing code."),
    ALREADY_PAIRED("This Bridge identity is already paired."),
    STORAGE_UNAVAILABLE("Pairing could not be saved securely."),
    PROFILE_OPERATION_FAILED("The paired Bridge profile could not be updated."),
}

data class PairingConfirmation(
    val endpoint: String,
    val apiVersion: String,
    val spkiSummary: String,
    val certificateSummary: String,
    val identityChangeWarning: Boolean,
)

data class PairingPresentationState(
    val screen: PairingScreen = PairingScreen.PROFILES,
    val profiles: List<BridgePairingMetadata> = emptyList(),
    val activeProfileId: String? = null,
    val permission: CameraPermissionState = CameraPermissionState.UNKNOWN,
    val confirmation: PairingConfirmation? = null,
    val error: PairingPresentationError? = null,
    val pendingUnpairProfileId: String? = null,
)

internal interface PairingPresentationRepository {
    suspend fun list(): PairingStorageResult<List<BridgePairingMetadata>>
    suspend fun activeProfileId(): PairingStorageResult<String?>
    suspend fun store(candidate: BridgeOnboardingCandidate): PairingStorageResult<BridgePairingMetadata>
    suspend fun selectActive(profileId: String): PairingStorageResult<Unit>
    suspend fun delete(profileId: String): PairingStorageResult<Unit>
}

internal class AndroidPairingPresentationRepository(
    private val repository: PairingRepository,
) : PairingPresentationRepository {
    override suspend fun list() = repository.list()
    override suspend fun activeProfileId() = repository.activeProfileId()
    override suspend fun store(candidate: BridgeOnboardingCandidate) =
        repository.store(candidate, makeActive = true)
    override suspend fun selectActive(profileId: String) = repository.selectActive(profileId)
    override suspend fun delete(profileId: String) = repository.delete(profileId)
}

internal fun BridgeOnboardingCandidate.toSafeConfirmation(
    identityChangeWarning: Boolean,
): PairingConfirmation = PairingConfirmation(
    endpoint = canonicalApiUrl,
    apiVersion = apiVersion,
    spkiSummary = spkiSha256.abbreviate(),
    certificateSummary = certificateSha256.abbreviate(),
    identityChangeWarning = identityChangeWarning,
)

private fun String.abbreviate(): String = when {
    length <= 16 -> this
    else -> "${take(8)}…${takeLast(8)}"
}

