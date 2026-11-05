package com.melfegor.nice64

import com.melfegor.nice64.core.model.BridgeOnboardingCandidate
import com.melfegor.nice64.core.model.BridgePairingMetadata
import com.melfegor.nice64.pairing.CameraPermissionState
import com.melfegor.nice64.pairing.PairingPresentationError
import com.melfegor.nice64.pairing.PairingPresentationRepository
import com.melfegor.nice64.pairing.PairingScreen
import com.melfegor.nice64.pairing.PairingStorageResult
import com.melfegor.nice64.pairing.QrScanSession
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Before
import org.junit.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

@OptIn(ExperimentalCoroutinesApi::class)
class PairingPresentationTest {
    private lateinit var repository: FakePairingRepository

    @Before
    fun setUp() {
        Dispatchers.setMain(UnconfinedTestDispatcher())
        repository = FakePairingRepository()
    }

    @After
    fun tearDown() = Dispatchers.resetMain()

    @Test
    fun initialStateLoadsProfilesAndActiveSelection() = runTest {
        repository.profiles += metadata("one")
        repository.active = "one"
        val model = Nice64ViewModel(repository)
        assertEquals(1, model.pairingState.value.profiles.size)
        assertEquals("one", model.pairingState.value.activeProfileId)
    }

    @Test
    fun cameraPermissionIsRequestedOnlyAfterPairAction() {
        val model = Nice64ViewModel(repository)
        assertEquals(PairingScreen.PROFILES, model.pairingState.value.screen)
        assertEquals(CameraPermissionState.UNKNOWN, model.pairingState.value.permission)
        model.beginPairing()
        assertEquals(PairingScreen.SCANNER, model.pairingState.value.screen)
    }

    @Test
    fun grantedDeniedAndPermanentCameraStatesAreExplicit() {
        val model = Nice64ViewModel(repository)
        model.updateCameraPermission(false)
        assertEquals(CameraPermissionState.DENIED, model.pairingState.value.permission)
        model.updateCameraPermission(false, permanentlyDenied = true)
        assertEquals(CameraPermissionState.PERMANENTLY_DENIED, model.pairingState.value.permission)
        model.updateCameraPermission(true)
        assertEquals(CameraPermissionState.GRANTED, model.pairingState.value.permission)
    }

    @Test
    fun validQrMovesToSafeConfirmationWithoutToken() {
        val model = scan(validQr())
        val state = model.pairingState.value
        assertEquals(PairingScreen.CONFIRMATION, state.screen)
        assertEquals("https://192.168.2.81:6470", state.confirmation?.endpoint)
        assertFalse(state.confirmation.toString().contains("top-secret"))
    }

    @Test
    fun malformedQrFailsSafely() {
        val model = scan("not-json")
        assertEquals(PairingScreen.ERROR, model.pairingState.value.screen)
        assertEquals(PairingPresentationError.INVALID_QR, model.pairingState.value.error)
    }

    @Test
    fun repeatedFramesAreDebounced() {
        val model = Nice64ViewModel(repository)
        model.beginPairing()
        model.acceptQr(validQr())
        model.acceptQr("invalid second frame")
        assertEquals(PairingScreen.CONFIRMATION, model.pairingState.value.screen)
    }

    @Test
    fun exactExistingIdentityIsNotSilentlyOverwritten() {
        repository.profiles += metadata("existing")
        val model = scan(validQr())
        assertEquals(PairingPresentationError.ALREADY_PAIRED, model.pairingState.value.error)
        assertEquals(0, repository.storeCalls)
    }

    @Test
    fun sameEndpointWithChangedIdentityRequiresExplicitWarning() {
        repository.profiles += metadata("old")
        val model = scan(validQr(spki = OTHER_SPKI, certificate = "B".repeat(64)))
        assertEquals(PairingScreen.CONFIRMATION, model.pairingState.value.screen)
        assertTrue(model.pairingState.value.confirmation?.identityChangeWarning == true)
    }

    @Test
    fun confirmationStoresExactlyOnceAndMakesProfileActive() {
        val model = scan(validQr())
        model.confirmPairing()
        model.confirmPairing()
        assertEquals(1, repository.storeCalls)
        assertEquals(PairingScreen.PROFILES, model.pairingState.value.screen)
        assertEquals("stored", model.pairingState.value.activeProfileId)
    }

    @Test
    fun cancelNeverStoresCandidate() {
        val model = scan(validQr())
        model.cancelPairing()
        assertEquals(0, repository.storeCalls)
        assertEquals(PairingScreen.PROFILES, model.pairingState.value.screen)
        assertNull(model.pairingState.value.confirmation)
    }

    @Test
    fun storageFailureUsesSafeError() {
        repository.failStore = true
        val model = scan(validQr())
        model.confirmPairing()
        assertEquals(PairingPresentationError.STORAGE_UNAVAILABLE, model.pairingState.value.error)
    }

    @Test
    fun selectingProfileUsesRepositoryAuthority() {
        repository.profiles += metadata("one")
        repository.profiles += metadata("two")
        val model = Nice64ViewModel(repository)
        model.selectProfile("two")
        assertEquals("two", model.pairingState.value.activeProfileId)
    }

    @Test
    fun unpairRequiresConfirmationAndDeletesCredentialProfile() {
        repository.profiles += metadata("one")
        repository.active = "one"
        val model = Nice64ViewModel(repository)
        model.requestUnpair("one")
        assertEquals("one", model.pairingState.value.pendingUnpairProfileId)
        assertTrue(repository.profiles.isNotEmpty())
        model.confirmUnpair()
        assertTrue(repository.profiles.isEmpty())
        assertNull(model.pairingState.value.activeProfileId)
    }

    @Test
    fun cancelledUnpairLeavesProfileUntouched() {
        repository.profiles += metadata("one")
        val model = Nice64ViewModel(repository)
        model.requestUnpair("one")
        model.cancelUnpair()
        assertEquals(1, repository.profiles.size)
        assertNull(model.pairingState.value.pendingUnpairProfileId)
    }

    @Test
    fun scanSessionAcceptsOneNonBlankQrOnly() {
        val delivered = mutableListOf<String>()
        val session = QrScanSession()
        assertFalse(session.deliverOnce(null, delivered::add))
        assertFalse(session.deliverOnce("", delivered::add))
        assertTrue(session.deliverOnce("first", delivered::add))
        assertFalse(session.deliverOnce("second", delivered::add))
        assertEquals(listOf("first"), delivered)
    }

    private fun scan(qr: String): Nice64ViewModel = Nice64ViewModel(repository).also {
        it.beginPairing()
        it.acceptQr(qr)
    }

    private fun metadata(id: String) = BridgePairingMetadata(
        profileId = id,
        displayLabel = "NICE64 Bridge",
        apiBaseUrl = "https://192.168.2.81:6470",
        apiVersion = "v1",
        spkiSha256 = VALID_SPKI,
        certificateSha256 = "A".repeat(64),
        pairedAtEpochMillis = 1,
    )

    private fun validQr(
        spki: String = VALID_SPKI,
        certificate: String = "A".repeat(64),
    ) = """{"schema":"nice64-bridge-onboarding","version":1,"apiVersion":"v1","url":"https://192.168.2.81:6470","token":"top-secret","spkiSha256":"$spki","certificateSha256":"$certificate"}"""

    companion object {
        private const val VALID_SPKI = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
        private const val OTHER_SPKI = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE="
    }
}

private class FakePairingRepository : PairingPresentationRepository {
    val profiles = mutableListOf<BridgePairingMetadata>()
    var active: String? = null
    var storeCalls = 0
    var failStore = false

    override suspend fun list() = PairingStorageResult.Success(profiles.toList())
    override suspend fun activeProfileId() = PairingStorageResult.Success(active)
    override suspend fun store(candidate: BridgeOnboardingCandidate): PairingStorageResult<BridgePairingMetadata> {
        storeCalls++
        if (failStore) return PairingStorageResult.Failure(com.melfegor.nice64.pairing.PairingStorageError.CREDENTIAL_WRITE_FAILED)
        val stored = BridgePairingMetadata(
            profileId = "stored",
            displayLabel = "NICE64 Bridge",
            apiBaseUrl = candidate.canonicalApiUrl,
            apiVersion = candidate.apiVersion,
            spkiSha256 = candidate.spkiSha256,
            certificateSha256 = candidate.certificateSha256,
            pairedAtEpochMillis = 2,
        )
        profiles += stored
        active = stored.profileId
        return PairingStorageResult.Success(stored)
    }

    override suspend fun selectActive(profileId: String): PairingStorageResult<Unit> {
        if (profiles.none { it.profileId == profileId }) return PairingStorageResult.Failure(com.melfegor.nice64.pairing.PairingStorageError.PROFILE_NOT_FOUND)
        active = profileId
        return PairingStorageResult.Success(Unit)
    }

    override suspend fun delete(profileId: String): PairingStorageResult<Unit> {
        profiles.removeAll { it.profileId == profileId }
        if (active == profileId) active = null
        return PairingStorageResult.Success(Unit)
    }
}
