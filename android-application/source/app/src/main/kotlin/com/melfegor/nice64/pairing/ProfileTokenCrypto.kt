package com.melfegor.nice64.pairing

import com.melfegor.nice64.core.model.BridgeBearerCredential
import com.melfegor.nice64.core.model.BridgePairingMetadata
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets
import java.security.GeneralSecurityException
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

internal object ProfileTokenAad {
    private const val FORMAT_VERSION = 1

    fun encode(metadata: BridgePairingMetadata): ByteArray {
        val output = ByteArrayOutputStream()
        DataOutputStream(output).use { data ->
            data.writeByte(FORMAT_VERSION)
            data.writeBoundedUtf8(metadata.profileId)
            data.writeBoundedUtf8(metadata.apiBaseUrl)
            data.writeBoundedUtf8(metadata.apiVersion)
            data.writeBoundedUtf8(metadata.spkiSha256)
        }
        return output.toByteArray()
    }

    private fun DataOutputStream.writeBoundedUtf8(value: String) {
        val bytes = value.toByteArray(StandardCharsets.UTF_8)
        require(bytes.size <= 4096) { "AAD component is too large." }
        writeInt(bytes.size)
        write(bytes)
        bytes.fill(0)
    }

}

internal data class ProfileTokenBlob(
    val nonce: ByteArray,
    val ciphertextAndTag: ByteArray,
) {
    fun encode(): ByteArray {
        require(nonce.size == NONCE_BYTES)
        require(ciphertextAndTag.size in GCM_TAG_BYTES..MAX_CIPHERTEXT_BYTES)
        return ByteBuffer.allocate(HEADER_BYTES + nonce.size + ciphertextAndTag.size)
            .put(MAGIC)
            .put(FORMAT_VERSION)
            .put(nonce.size.toByte())
            .putInt(ciphertextAndTag.size)
            .put(nonce)
            .put(ciphertextAndTag)
            .array()
    }

    companion object {
        const val NONCE_BYTES = 12
        const val GCM_TAG_BYTES = 16
        private const val MAX_CIPHERTEXT_BYTES = 4096
        private const val HEADER_BYTES = 10
        private val MAGIC = byteArrayOf(0x4E, 0x36, 0x34, 0x54)
        private const val FORMAT_VERSION: Byte = 1

        fun decode(encoded: ByteArray): ProfileTokenBlob {
            if (encoded.size < HEADER_BYTES + NONCE_BYTES + GCM_TAG_BYTES) {
                throw CredentialVaultException(PairingStorageError.CREDENTIAL_READ_FAILED)
            }
            val buffer = ByteBuffer.wrap(encoded)
            val magic = ByteArray(MAGIC.size).also(buffer::get)
            val version = buffer.get()
            val nonceLength = buffer.get().toInt() and 0xff
            val ciphertextLength = buffer.int
            if (!magic.contentEquals(MAGIC) || version != FORMAT_VERSION ||
                nonceLength != NONCE_BYTES ||
                ciphertextLength !in GCM_TAG_BYTES..MAX_CIPHERTEXT_BYTES ||
                buffer.remaining() != nonceLength + ciphertextLength
            ) {
                throw CredentialVaultException(PairingStorageError.CREDENTIAL_READ_FAILED)
            }
            val nonce = ByteArray(nonceLength).also(buffer::get)
            val ciphertext = ByteArray(ciphertextLength).also(buffer::get)
            return ProfileTokenBlob(nonce, ciphertext)
        }
    }
}

internal class AesGcmCredentialCipher {
    fun encrypt(
        key: SecretKey,
        metadata: BridgePairingMetadata,
        credential: BridgeBearerCredential,
    ): ByteArray {
        val aad = ProfileTokenAad.encode(metadata)
        var nonce: ByteArray? = null
        return try {
            val cipher = Cipher.getInstance(TRANSFORMATION)
            // Android Keystore keys configured with randomized encryption reject a
            // caller-provided IV. Let the provider generate it, then persist that IV.
            cipher.init(Cipher.ENCRYPT_MODE, key)
            val generatedNonce = cipher.iv?.copyOf()
                ?: throw CredentialVaultException(PairingStorageError.CREDENTIAL_WRITE_FAILED)
            nonce = generatedNonce
            if (generatedNonce.size != ProfileTokenBlob.NONCE_BYTES) {
                throw CredentialVaultException(PairingStorageError.CREDENTIAL_WRITE_FAILED)
            }
            cipher.updateAAD(aad)
            credential.useUtf8 { plaintext ->
                ProfileTokenBlob(generatedNonce, cipher.doFinal(plaintext)).encode()
            }
        } catch (error: GeneralSecurityException) {
            throw CredentialVaultException(PairingStorageError.CREDENTIAL_WRITE_FAILED)
        } finally {
            aad.fill(0)
            nonce?.fill(0)
        }
    }

    fun decrypt(
        key: SecretKey,
        metadata: BridgePairingMetadata,
        encoded: ByteArray,
    ): BridgeBearerCredential {
        val blob = ProfileTokenBlob.decode(encoded)
        val aad = ProfileTokenAad.encode(metadata)
        var plaintext: ByteArray? = null
        try {
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(TAG_BITS, blob.nonce))
            cipher.updateAAD(aad)
            plaintext = cipher.doFinal(blob.ciphertextAndTag)
            return BridgeBearerCredential.fromSecret(
                plaintext.toString(StandardCharsets.UTF_8),
            )
        } catch (_: AEADBadTagException) {
            throw CredentialVaultException(
                PairingStorageError.CREDENTIAL_AUTHENTICATION_FAILED,
            )
        } catch (_: GeneralSecurityException) {
            throw CredentialVaultException(PairingStorageError.CREDENTIAL_READ_FAILED)
        } finally {
            plaintext?.fill(0)
            aad.fill(0)
            blob.nonce.fill(0)
            blob.ciphertextAndTag.fill(0)
        }
    }

    companion object {
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val TAG_BITS = 128
    }
}
