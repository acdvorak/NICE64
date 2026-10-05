package com.melfegor.nice64.pairing

import com.melfegor.nice64.core.model.BridgeBearerCredential
import com.melfegor.nice64.core.model.BridgeOnboardingCandidate
import com.melfegor.nice64.core.model.BridgePairingMetadata
import com.melfegor.nice64.core.model.BridgePairingProfile
import java.util.UUID

enum class PairingStorageError {
    KEYSTORE_UNAVAILABLE,
    CREDENTIAL_WRITE_FAILED,
    CREDENTIAL_READ_FAILED,
    CREDENTIAL_AUTHENTICATION_FAILED,
    CREDENTIAL_UNAVAILABLE,
    CREDENTIAL_DELETE_FAILED,
    METADATA_READ_FAILED,
    METADATA_WRITE_FAILED,
    PROFILE_NOT_FOUND,
    RE_PAIR_REQUIRED,
}

sealed interface PairingStorageResult<out T> {
    data class Success<T>(val value: T) : PairingStorageResult<T>
    data class Failure(val error: PairingStorageError) : PairingStorageResult<Nothing>
}

internal interface PairingMetadataStore {
    suspend fun list(): List<BridgePairingMetadata>
    suspend fun find(profileId: String): BridgePairingMetadata?
    suspend fun activeProfileId(): String?
    suspend fun upsert(metadata: BridgePairingMetadata, makeActive: Boolean)
    suspend fun selectActive(profileId: String): Boolean
    suspend fun delete(profileId: String): Boolean
}

internal interface PairingCredentialVault {
    suspend fun write(metadata: BridgePairingMetadata, credential: BridgeBearerCredential)
    suspend fun read(metadata: BridgePairingMetadata): BridgeBearerCredential
    suspend fun delete(profileId: String)
    suspend fun deleteSharedKeyIfNoProtectedFiles()
}

internal fun interface PairingProfileIdFactory {
    fun create(): String
}

internal fun interface PairingClock {
    fun epochMillis(): Long
}

class PairingRepository internal constructor(
    private val metadataStore: PairingMetadataStore,
    private val credentialVault: PairingCredentialVault,
    private val profileIdFactory: PairingProfileIdFactory =
        PairingProfileIdFactory { UUID.randomUUID().toString() },
    private val clock: PairingClock = PairingClock(System::currentTimeMillis),
) {
    suspend fun store(
        candidate: BridgeOnboardingCandidate,
        label: String = "NICE64 Bridge",
        makeActive: Boolean = false,
    ): PairingStorageResult<BridgePairingMetadata> {
        val profileId = profileIdFactory.create()
        val metadata = BridgePairingMetadata(
            profileId = profileId,
            displayLabel = label.take(128),
            apiBaseUrl = candidate.canonicalApiUrl,
            apiVersion = candidate.apiVersion,
            spkiSha256 = candidate.spkiSha256,
            certificateSha256 = candidate.certificateSha256,
            pairedAtEpochMillis = clock.epochMillis(),
        )
        try {
            credentialVault.write(metadata, candidate.credential)
        } catch (error: CredentialVaultException) {
            return PairingStorageResult.Failure(error.error)
        } catch (_: Exception) {
            return PairingStorageResult.Failure(PairingStorageError.CREDENTIAL_WRITE_FAILED)
        }
        try {
            metadataStore.upsert(metadata, makeActive)
        } catch (_: Exception) {
            try {
                credentialVault.delete(profileId)
            } catch (_: Exception) {
                // An unreadable orphan is safer than metadata claiming a usable profile.
            }
            return PairingStorageResult.Failure(PairingStorageError.METADATA_WRITE_FAILED)
        }
        return PairingStorageResult.Success(metadata)
    }

    suspend fun list(): PairingStorageResult<List<BridgePairingMetadata>> = try {
        PairingStorageResult.Success(metadataStore.list())
    } catch (_: Exception) {
        PairingStorageResult.Failure(PairingStorageError.METADATA_READ_FAILED)
    }

    suspend fun activeProfileId(): PairingStorageResult<String?> = try {
        PairingStorageResult.Success(metadataStore.activeProfileId())
    } catch (_: Exception) {
        PairingStorageResult.Failure(PairingStorageError.METADATA_READ_FAILED)
    }

    suspend fun selectActive(profileId: String): PairingStorageResult<Unit> = try {
        if (metadataStore.selectActive(profileId)) {
            PairingStorageResult.Success(Unit)
        } else {
            PairingStorageResult.Failure(PairingStorageError.PROFILE_NOT_FOUND)
        }
    } catch (_: Exception) {
        PairingStorageResult.Failure(PairingStorageError.METADATA_WRITE_FAILED)
    }

    suspend fun load(profileId: String): PairingStorageResult<BridgePairingProfile> {
        val metadata = try {
            metadataStore.find(profileId)
        } catch (_: Exception) {
            return PairingStorageResult.Failure(PairingStorageError.METADATA_READ_FAILED)
        } ?: return PairingStorageResult.Failure(PairingStorageError.PROFILE_NOT_FOUND)

        return try {
            PairingStorageResult.Success(
                BridgePairingProfile(metadata, credentialVault.read(metadata)),
            )
        } catch (error: CredentialVaultException) {
            PairingStorageResult.Failure(error.error)
        } catch (_: Exception) {
            PairingStorageResult.Failure(PairingStorageError.CREDENTIAL_READ_FAILED)
        }
    }

    suspend fun delete(profileId: String): PairingStorageResult<Unit> {
        val deleted = try {
            metadataStore.delete(profileId)
        } catch (_: Exception) {
            return PairingStorageResult.Failure(PairingStorageError.METADATA_WRITE_FAILED)
        }
        if (!deleted) return PairingStorageResult.Failure(PairingStorageError.PROFILE_NOT_FOUND)

        try {
            credentialVault.delete(profileId)
            if (metadataStore.list().isEmpty()) {
                credentialVault.deleteSharedKeyIfNoProtectedFiles()
            }
        } catch (_: Exception) {
            return PairingStorageResult.Failure(PairingStorageError.CREDENTIAL_DELETE_FAILED)
        }
        return PairingStorageResult.Success(Unit)
    }
}

internal class CredentialVaultException(
    val error: PairingStorageError,
) : RuntimeException(error.name)
