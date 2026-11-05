package com.melfegor.nice64.pairing

import com.melfegor.nice64.core.model.BridgeBearerCredential
import com.melfegor.nice64.core.model.BridgeOnboardingCandidate
import com.melfegor.nice64.core.model.BridgePairingMetadata
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.UUID
import kotlin.test.assertIs

class PairingRepositoryTest {
    @Test
    fun defaultProfileIdentityIsAnOpaqueLocalUuid() = runTest {
        val metadata = FakeMetadataStore()
        val vault = FakeVault()
        val repository = PairingRepository(metadata, vault, clock = PairingClock { 1L })
        val stored = repository.store(candidate("192.168.1.20")).successValue()
        assertEquals(stored.profileId, UUID.fromString(stored.profileId).toString())
    }

    @Test
    fun multipleProfilesCoexistAndExactlyOneMayBeActive() = runTest {
        val metadata = FakeMetadataStore()
        val vault = FakeVault()
        var nextId = 0
        val repository = repository(metadata, vault) { "profile-${++nextId}" }

        val first = repository.store(candidate("192.168.1.20"), "First")
            .successValue()
        val second = repository.store(candidate("192.168.1.21"), "Second")
            .successValue()
        assertEquals(2, repository.list().successValue().size)
        assertNull(repository.activeProfileId().successValue())

        assertIs<PairingStorageResult.Success<Unit>>(repository.selectActive(first.profileId))
        assertEquals(first.profileId, repository.activeProfileId().successValue())
        assertIs<PairingStorageResult.Success<Unit>>(repository.selectActive(second.profileId))
        assertEquals(second.profileId, repository.activeProfileId().successValue())
        assertIs<PairingStorageResult.Failure>(repository.selectActive("missing"))
        assertEquals(second.profileId, repository.activeProfileId().successValue())
    }

    @Test
    fun saveLoadAndRepositoryRecreationPreserveUsableProfile() = runTest {
        val metadata = FakeMetadataStore()
        val vault = FakeVault()
        val firstRepository = repository(metadata, vault) { "profile-a" }
        val stored = firstRepository.store(candidate("192.168.1.20"), makeActive = true)
            .successValue()

        val reopened = repository(metadata, vault) { "unused" }
        val loaded = reopened.load(stored.profileId).successValue()
        assertEquals(stored, loaded.metadata)
        loaded.credential.useUtf8 { assertEquals("token-192.168.1.20", it.toString(Charsets.UTF_8)) }
        assertFalse(loaded.toString().contains("token-192.168.1.20"))
    }

    @Test
    fun deletingInactiveOrActiveProfilePreservesOtherProfilesAndClearsSelection() = runTest {
        val metadata = FakeMetadataStore()
        val vault = FakeVault()
        var id = 0
        val repository = repository(metadata, vault) { "p${++id}" }
        val first = repository.store(candidate("192.168.1.20"), makeActive = true).successValue()
        val second = repository.store(candidate("192.168.1.21")).successValue()

        assertIs<PairingStorageResult.Success<Unit>>(repository.delete(second.profileId))
        assertEquals(first.profileId, repository.activeProfileId().successValue())
        assertTrue(second.profileId in vault.deleted)
        assertEquals(1, repository.list().successValue().size)

        assertIs<PairingStorageResult.Success<Unit>>(repository.delete(first.profileId))
        assertNull(repository.activeProfileId().successValue())
        assertTrue(repository.list().successValue().isEmpty())
        assertTrue(vault.sharedKeyDeleteRequested)
    }

    @Test
    fun tokenWriteFailureNeverCommitsMetadata() = runTest {
        val metadata = FakeMetadataStore()
        val vault = FakeVault(writeError = PairingStorageError.CREDENTIAL_WRITE_FAILED)
        val result = repository(metadata, vault) { "profile-a" }.store(candidate("192.168.1.20"))
        assertEquals(PairingStorageError.CREDENTIAL_WRITE_FAILED, result.failureError())
        assertTrue(metadata.records.isEmpty())
    }

    @Test
    fun metadataWriteFailureRollsBackNewTokenBlob() = runTest {
        val metadata = FakeMetadataStore().apply { failWrite = true }
        val vault = FakeVault()
        val result = repository(metadata, vault) { "profile-a" }.store(candidate("192.168.1.20"))
        assertEquals(PairingStorageError.METADATA_WRITE_FAILED, result.failureError())
        assertTrue("profile-a" in vault.deleted)
        assertTrue(metadata.records.isEmpty())
    }

    @Test
    fun missingCorruptOrUnauthenticatedCredentialNeverReturnsUsableProfile() = runTest {
        val metadata = FakeMetadataStore()
        val vault = FakeVault()
        val repository = repository(metadata, vault) { "profile-a" }
        val profile = repository.store(candidate("192.168.1.20")).successValue()

        vault.readError = PairingStorageError.CREDENTIAL_UNAVAILABLE
        assertEquals(PairingStorageError.CREDENTIAL_UNAVAILABLE, repository.load(profile.profileId).failureError())
        vault.readError = PairingStorageError.CREDENTIAL_AUTHENTICATION_FAILED
        assertEquals(
            PairingStorageError.CREDENTIAL_AUTHENTICATION_FAILED,
            repository.load(profile.profileId).failureError(),
        )
        vault.readError = PairingStorageError.RE_PAIR_REQUIRED
        assertEquals(PairingStorageError.RE_PAIR_REQUIRED, repository.load(profile.profileId).failureError())
        assertEquals(PairingStorageError.PROFILE_NOT_FOUND, repository.load("missing").failureError())
    }

    private fun repository(
        metadata: FakeMetadataStore,
        vault: FakeVault,
        id: () -> String,
    ) = PairingRepository(
        metadata,
        vault,
        PairingProfileIdFactory(id),
        PairingClock { 1_234L },
    )

    private fun candidate(host: String) = BridgeOnboardingCandidate(
        canonicalApiUrl = "https://$host:6470",
        apiVersion = "v1",
        spkiSha256 = "spki-$host",
        certificateSha256 = "AB".repeat(32),
        credential = BridgeBearerCredential.fromSecret("token-$host"),
    )

    private class FakeMetadataStore : PairingMetadataStore {
        val records = linkedMapOf<String, BridgePairingMetadata>()
        var active: String? = null
        var failWrite = false

        override suspend fun list() = records.values.toList()
        override suspend fun find(profileId: String) = records[profileId]
        override suspend fun activeProfileId() = active
        override suspend fun upsert(metadata: BridgePairingMetadata, makeActive: Boolean) {
            if (failWrite) error("bounded fake failure")
            records[metadata.profileId] = metadata
            if (makeActive) active = metadata.profileId
        }

        override suspend fun selectActive(profileId: String): Boolean {
            if (profileId !in records) return false
            active = profileId
            return true
        }

        override suspend fun delete(profileId: String): Boolean {
            if (records.remove(profileId) == null) return false
            if (active == profileId) active = null
            return true
        }
    }

    private class FakeVault(
        private val writeError: PairingStorageError? = null,
    ) : PairingCredentialVault {
        val credentials = mutableMapOf<String, BridgeBearerCredential>()
        val deleted = mutableSetOf<String>()
        var readError: PairingStorageError? = null
        var sharedKeyDeleteRequested = false

        override suspend fun write(
            metadata: BridgePairingMetadata,
            credential: BridgeBearerCredential,
        ) {
            writeError?.let { throw CredentialVaultException(it) }
            credentials[metadata.profileId] = credential
        }

        override suspend fun read(metadata: BridgePairingMetadata): BridgeBearerCredential {
            readError?.let { throw CredentialVaultException(it) }
            return credentials[metadata.profileId]
                ?: throw CredentialVaultException(PairingStorageError.CREDENTIAL_UNAVAILABLE)
        }

        override suspend fun delete(profileId: String) {
            credentials.remove(profileId)
            deleted += profileId
        }

        override suspend fun deleteSharedKeyIfNoProtectedFiles() {
            sharedKeyDeleteRequested = true
        }
    }

    private fun <T> PairingStorageResult<T>.successValue(): T =
        assertIs<PairingStorageResult.Success<T>>(this).value

    private fun PairingStorageResult<*>.failureError(): PairingStorageError =
        assertIs<PairingStorageResult.Failure>(this).error
}
