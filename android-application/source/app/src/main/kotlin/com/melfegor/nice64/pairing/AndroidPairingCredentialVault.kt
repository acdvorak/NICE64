package com.melfegor.nice64.pairing

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import com.melfegor.nice64.core.model.BridgeBearerCredential
import com.melfegor.nice64.core.model.BridgePairingMetadata
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.security.KeyStore
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey

internal interface CredentialKeyProvider {
    fun getOrCreateForEncryption(): SecretKey
    fun getExistingForDecryption(): SecretKey?
    fun delete()
}

internal class AndroidKeystoreCredentialKeyProvider : CredentialKeyProvider {
    override fun getOrCreateForEncryption(): SecretKey {
        getExistingForDecryption()?.let { return it }
        return try {
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
            generator.generateKey()
        } catch (_: Exception) {
            throw CredentialVaultException(PairingStorageError.KEYSTORE_UNAVAILABLE)
        }
    }

    override fun getExistingForDecryption(): SecretKey? = try {
        val store = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        store.getKey(KEY_ALIAS, null) as? SecretKey
    } catch (_: Exception) {
        throw CredentialVaultException(PairingStorageError.KEYSTORE_UNAVAILABLE)
    }

    override fun delete() {
        try {
            val store = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
            if (store.containsAlias(KEY_ALIAS)) store.deleteEntry(KEY_ALIAS)
        } catch (_: Exception) {
            throw CredentialVaultException(PairingStorageError.KEYSTORE_UNAVAILABLE)
        }
    }

    companion object {
        const val KEY_ALIAS = "nice64.pairing.bearer.v1"
        private const val ANDROID_KEYSTORE = "AndroidKeyStore"
    }
}

internal class AndroidPairingCredentialVault(
    context: Context,
    private val keys: CredentialKeyProvider = AndroidKeystoreCredentialKeyProvider(),
    private val cipher: AesGcmCredentialCipher = AesGcmCredentialCipher(),
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO,
    internal val credentialDirectory: File =
        File(context.noBackupFilesDir, "pairing-credentials"),
) : PairingCredentialVault {
    override suspend fun write(
        metadata: BridgePairingMetadata,
        credential: BridgeBearerCredential,
    ) = withContext(ioDispatcher) {
        ensureDirectory()
        val encoded = cipher.encrypt(keys.getOrCreateForEncryption(), metadata, credential)
        val target = tokenFile(metadata.profileId)
        val atomic = AtomicFile(target)
        val output = try {
            atomic.startWrite()
        } catch (_: Exception) {
            encoded.fill(0)
            throw CredentialVaultException(PairingStorageError.CREDENTIAL_WRITE_FAILED)
        }
        try {
            output.write(encoded)
            output.flush()
            output.fd.sync()
            atomic.finishWrite(output)
        } catch (_: Exception) {
            atomic.failWrite(output)
            throw CredentialVaultException(PairingStorageError.CREDENTIAL_WRITE_FAILED)
        } finally {
            encoded.fill(0)
        }
    }

    override suspend fun read(metadata: BridgePairingMetadata): BridgeBearerCredential =
        withContext(ioDispatcher) {
            val target = tokenFile(metadata.profileId)
            if (!target.isFile) {
                throw CredentialVaultException(PairingStorageError.CREDENTIAL_UNAVAILABLE)
            }
            val key = keys.getExistingForDecryption()
                ?: throw CredentialVaultException(PairingStorageError.RE_PAIR_REQUIRED)
            val encoded = try {
                AtomicFile(target).readFully()
            } catch (_: Exception) {
                throw CredentialVaultException(PairingStorageError.CREDENTIAL_READ_FAILED)
            }
            try {
                cipher.decrypt(key, metadata, encoded)
            } finally {
                encoded.fill(0)
            }
        }

    override suspend fun delete(profileId: String) = withContext(ioDispatcher) {
        val file = tokenFile(profileId)
        if (file.exists() && !file.delete()) {
            throw CredentialVaultException(PairingStorageError.CREDENTIAL_DELETE_FAILED)
        }
        File(file.path + ".new").delete()
        File(file.path + ".bak").delete()
        Unit
    }

    override suspend fun deleteSharedKeyIfNoProtectedFiles() = withContext(ioDispatcher) {
        val protectedFiles = credentialDirectory.listFiles().orEmpty()
            .any { it.isFile && it.extension == TOKEN_EXTENSION }
        if (!protectedFiles) keys.delete()
    }

    private fun ensureDirectory() {
        if (!credentialDirectory.exists() && !credentialDirectory.mkdirs()) {
            throw CredentialVaultException(PairingStorageError.CREDENTIAL_WRITE_FAILED)
        }
        if (!credentialDirectory.isDirectory) {
            throw CredentialVaultException(PairingStorageError.CREDENTIAL_WRITE_FAILED)
        }
    }

    private fun tokenFile(profileId: String): File {
        require(PROFILE_ID_PATTERN.matches(profileId)) { "Invalid local profile ID." }
        val file = File(credentialDirectory, "$profileId.$TOKEN_EXTENSION")
        val rootPath = credentialDirectory.canonicalFile.toPath()
        require(file.canonicalFile.toPath().parent == rootPath) {
            "Credential path escaped its storage root."
        }
        return file
    }

    companion object {
        private const val TOKEN_EXTENSION = "n64token"
        private val PROFILE_ID_PATTERN = Regex("[A-Za-z0-9-]{1,64}")
    }
}

object PairingRepositoryFactory {
    fun create(context: Context): PairingRepository = PairingRepository(
        metadataStore = AndroidPairingMetadataStore.create(context.applicationContext),
        credentialVault = AndroidPairingCredentialVault(context.applicationContext),
    )
}
