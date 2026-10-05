package com.melfegor.nice64.pairing

import android.content.Context
import android.security.keystore.KeyInfo
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Log
import androidx.datastore.preferences.preferencesDataStoreFile
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.core.model.BridgeBearerCredential
import com.melfegor.nice64.core.model.BridgeOnboardingCandidate
import com.melfegor.nice64.core.model.BridgePairingMetadata
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.security.KeyStore
import java.util.UUID
import javax.crypto.SecretKeyFactory
import javax.crypto.SecretKey
import javax.crypto.KeyGenerator

@RunWith(AndroidJUnit4::class)
class AndroidPairingStorageInstrumentedTest {
    private lateinit var context: Context
    private val keyProvider = IsolatedTestCredentialKeyProvider()

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        keyProvider.delete()
        cleanIsolatedTestState()
    }

    @After
    fun tearDown() {
        keyProvider.delete()
        cleanIsolatedTestState()
    }

    @Test
    fun androidKeystoreKeyIsNonExportableAndVaultRoundTripsUnderNoBackup(): Unit = runBlocking {
        val root = File(context.noBackupFilesDir, "d8b-test-${UUID.randomUUID()}")
        val vault = AndroidPairingCredentialVault(
            context,
            keys = keyProvider,
            credentialDirectory = root,
        )
        val metadata = metadata("profile-a")
        val credential = BridgeBearerCredential.fromSecret("device-secret-token")

        vault.write(metadata, credential)
        val firstEncoded = File(root, "profile-a.n64token").readBytes()
        vault.write(metadata, credential)
        val secondEncoded = File(root, "profile-a.n64token").readBytes()
        assertFalse(firstEncoded.contentEquals(secondEncoded))
        assertFalse(
            ProfileTokenBlob.decode(firstEncoded).nonce.contentEquals(
                ProfileTokenBlob.decode(secondEncoded).nonce,
            ),
        )
        val restored = vault.read(metadata)
        restored.useUtf8 { assertEquals("device-secret-token", it.toString(Charsets.UTF_8)) }
        assertTrue(root.canonicalPath.startsWith(context.noBackupFilesDir.canonicalPath))
        val blob = File(root, "profile-a.n64token")
        assertTrue(blob.isFile)
        assertFalse(blob.readBytes().containsSubsequence("device-secret-token".toByteArray()))

        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val key = keyStore.getKey(IsolatedTestCredentialKeyProvider.KEY_ALIAS, null) as SecretKey
        assertNull(key.encoded)
        assertEquals("AES", key.algorithm)
        val keyInfo = SecretKeyFactory.getInstance("AES", "AndroidKeyStore")
            .getKeySpec(key, KeyInfo::class.java) as KeyInfo
        assertEquals(256, keyInfo.keySize)
        assertTrue(KeyProperties.BLOCK_MODE_GCM in keyInfo.blockModes)
        assertTrue(KeyProperties.ENCRYPTION_PADDING_NONE in keyInfo.encryptionPaddings)
        Log.i(
            "NICE64-D8B-HV",
            "algorithm=${key.algorithm} keySize=${keyInfo.keySize} " +
                "securityLevel=${keyInfo.securityLevel}",
        )
        assertTrue(root.listFiles().orEmpty().none { it.name.contains("key", ignoreCase = true) })

        vault.delete("profile-a")
        vault.deleteSharedKeyIfNoProtectedFiles()
        assertFalse(keyStoreAfterDelete().containsAlias(IsolatedTestCredentialKeyProvider.KEY_ALIAS))
        firstEncoded.fill(0)
        secondEncoded.fill(0)
        root.deleteRecursively()
    }

    @Test
    fun realKeystoreCiphertextIsProfileBoundAndTamperFailsClosed(): Unit = runBlocking {
        val root = File(context.noBackupFilesDir, "d8b-test-${UUID.randomUUID()}")
        val vault = AndroidPairingCredentialVault(
            context,
            keys = keyProvider,
            credentialDirectory = root,
        )
        val metadata = metadata("profile-a")
        vault.write(metadata, BridgeBearerCredential.fromSecret("device-secret-token"))

        val wrongProfile = metadata.copy(profileId = "profile-b")
        File(root, "profile-a.n64token").copyTo(File(root, "profile-b.n64token"))
        assertCredentialFailure(vault, wrongProfile, PairingStorageError.CREDENTIAL_AUTHENTICATION_FAILED)
        assertCredentialFailure(
            vault,
            metadata.copy(apiBaseUrl = "https://192.168.1.21:6470"),
            PairingStorageError.CREDENTIAL_AUTHENTICATION_FAILED,
        )
        assertCredentialFailure(
            vault,
            metadata.copy(spkiSha256 = "different-spki"),
            PairingStorageError.CREDENTIAL_AUTHENTICATION_FAILED,
        )

        val file = File(root, "profile-a.n64token")
        val original = file.readBytes()
        listOf(10, 22, original.lastIndex).forEach { index ->
            val changed = original.copyOf().also {
                it[index] = (it[index].toInt() xor 1).toByte()
            }
            file.writeBytes(changed)
            changed.fill(0)
            assertCredentialFailure(
                vault,
                metadata,
                PairingStorageError.CREDENTIAL_AUTHENTICATION_FAILED,
            )
        }
        val corrupt = original.copyOf().also { it[0] = 0 }
        file.writeBytes(corrupt)
        corrupt.fill(0)
        assertCredentialFailure(vault, metadata, PairingStorageError.CREDENTIAL_READ_FAILED)
        file.delete()
        assertCredentialFailure(vault, metadata, PairingStorageError.CREDENTIAL_UNAVAILABLE)
        original.fill(0)
        root.deleteRecursively()
    }

    @Test
    fun preferencesDataStorePersistsMultipleMetadataAndOneActiveProfile(): Unit = runBlocking {
        val name = "d8b-${UUID.randomUUID()}"
        val credentialRoot = File(context.noBackupFilesDir, "d8b-test-${UUID.randomUUID()}")
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val dataStore = PreferenceDataStoreFactory.create(
            scope = scope,
            produceFile = { context.preferencesDataStoreFile(name) },
        )
        val store = AndroidPairingMetadataStore(dataStore)
        val vault = AndroidPairingCredentialVault(
            context,
            keys = keyProvider,
            credentialDirectory = credentialRoot,
        )
        try {
            val profileIds = ArrayDeque(listOf("profile-a", "profile-b"))
            val firstRepository = PairingRepository(
                metadataStore = store,
                credentialVault = vault,
                profileIdFactory = PairingProfileIdFactory { profileIds.removeFirst() },
                clock = PairingClock { 1L },
            )
            assertTrue(firstRepository.store(candidate(), makeActive = true) is PairingStorageResult.Success)
            assertTrue(
                firstRepository.store(
                    candidate(url = "https://192.168.1.21:6470"),
                    makeActive = false,
                ) is PairingStorageResult.Success,
            )

            val reopenedRepository = PairingRepository(store, vault)
            assertEquals(2, (reopenedRepository.list() as PairingStorageResult.Success).value.size)
            assertEquals("profile-a", store.activeProfileId())
            assertTrue(reopenedRepository.load("profile-a") is PairingStorageResult.Success)
            assertTrue(reopenedRepository.selectActive("profile-b") is PairingStorageResult.Success)
            assertEquals("profile-b", store.activeProfileId())
            assertTrue(reopenedRepository.selectActive("missing") is PairingStorageResult.Failure)
            assertTrue(reopenedRepository.delete("profile-b") is PairingStorageResult.Success)
            assertNull(store.activeProfileId())
            assertEquals(listOf("profile-a"), store.list().map { it.profileId })
            assertTrue(File(credentialRoot, "profile-a.n64token").isFile)
            assertFalse(File(credentialRoot, "profile-b.n64token").exists())
            assertTrue(reopenedRepository.delete("profile-a") is PairingStorageResult.Success)
            assertTrue(store.list().isEmpty())
            assertNull(store.activeProfileId())
        } finally {
            scope.cancel()
            credentialRoot.deleteRecursively()
        }
    }

    private fun candidate(
        url: String = "https://192.168.1.20:6470",
    ) = BridgeOnboardingCandidate(
        canonicalApiUrl = url,
        apiVersion = "v1",
        spkiSha256 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
        certificateSha256 = "AB".repeat(32),
        credential = BridgeBearerCredential.fromSecret("device-secret-token"),
    )

    private suspend fun assertCredentialFailure(
        vault: AndroidPairingCredentialVault,
        metadata: BridgePairingMetadata,
        expected: PairingStorageError,
    ) {
        val error = kotlin.runCatching { vault.read(metadata) }.exceptionOrNull()
        assertTrue(error is CredentialVaultException)
        assertEquals(expected, (error as CredentialVaultException).error)
    }

    private fun cleanIsolatedTestState() {
        context.noBackupFilesDir.listFiles().orEmpty()
            .filter { it.name.startsWith("d8b-test-") }
            .forEach(File::deleteRecursively)
        File(context.filesDir, "datastore").listFiles().orEmpty()
            .filter { it.name.startsWith("d8b-") }
            .forEach(File::delete)
    }

    private fun metadata(profileId: String) = BridgePairingMetadata(
        profileId = profileId,
        displayLabel = "Bridge",
        apiBaseUrl = "https://192.168.1.20:6470",
        apiVersion = "v1",
        spkiSha256 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
        certificateSha256 = "AB".repeat(32),
        pairedAtEpochMillis = 1L,
    )

    private fun keyStoreAfterDelete(): KeyStore =
        KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    private fun ByteArray.containsSubsequence(needle: ByteArray): Boolean =
        indices.any { start ->
            start + needle.size <= size &&
                copyOfRange(start, start + needle.size).contentEquals(needle)
        }

    private class IsolatedTestCredentialKeyProvider : CredentialKeyProvider {
        override fun getOrCreateForEncryption(): SecretKey {
            getExistingForDecryption()?.let { return it }
            val generator = KeyGenerator.getInstance(
                KeyProperties.KEY_ALGORITHM_AES,
                ANDROID_KEYSTORE,
            )
            generator.init(
                KeyGenParameterSpec.Builder(
                    KEY_ALIAS,
                    KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
                )
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .setRandomizedEncryptionRequired(true)
                    .setUserAuthenticationRequired(false)
                    .build(),
            )
            return generator.generateKey()
        }

        override fun getExistingForDecryption(): SecretKey? {
            val store = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
            return store.getKey(KEY_ALIAS, null) as? SecretKey
        }

        override fun delete() {
            val store = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
            if (store.containsAlias(KEY_ALIAS)) store.deleteEntry(KEY_ALIAS)
        }

        companion object {
            const val KEY_ALIAS = "nice64.pairing.bearer.instrumentation-test.v1"
            private const val ANDROID_KEYSTORE = "AndroidKeyStore"
        }
    }
}
