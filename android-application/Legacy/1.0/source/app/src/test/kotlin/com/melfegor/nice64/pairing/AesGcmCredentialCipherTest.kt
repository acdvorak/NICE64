package com.melfegor.nice64.pairing

import com.melfegor.nice64.core.model.BridgeBearerCredential
import com.melfegor.nice64.core.model.BridgePairingMetadata
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import javax.crypto.spec.SecretKeySpec
import kotlin.test.assertContentEquals
import kotlin.test.assertFailsWith

class AesGcmCredentialCipherTest {
    private val key = SecretKeySpec(ByteArray(32) { it.toByte() }, "AES")
    private val otherKey = SecretKeySpec(ByteArray(32) { (it + 1).toByte() }, "AES")
    private val metadata = metadata("profile-a")

    @Test
    fun aes256GcmRoundTripsAndBlobContainsNoPlaintextOrOnboardingJson() {
        val cipher = AesGcmCredentialCipher()
        val token = "sensitive-bearer-token"
        val encoded = cipher.encrypt(key, metadata, BridgeBearerCredential.fromSecret(token))
        val restored = cipher.decrypt(key, metadata, encoded)
        restored.useUtf8 { assertContentEquals(token.toByteArray(), it) }
        assertFalse(encoded.containsSubsequence(token.toByteArray()))
        assertFalse(encoded.containsSubsequence("nice64-bridge-onboarding".toByteArray()))
        assertTrue(ProfileTokenBlob.decode(encoded).ciphertextAndTag.size >= 16)
    }

    @Test
    fun freshNonceChangesCiphertextForSameToken() {
        val cipher = AesGcmCredentialCipher()
        val credential = BridgeBearerCredential.fromSecret("same-token")
        val first = cipher.encrypt(key, metadata, credential)
        val second = cipher.encrypt(key, metadata, credential)
        assertFalse(first.contentEquals(second))
        assertFalse(ProfileTokenBlob.decode(first).nonce.contentEquals(ProfileTokenBlob.decode(second).nonce))
    }

    @Test
    fun profileEndpointApiAndSpkiAreAuthenticatedAdditionalData() {
        val cipher = cipher()
        val encoded = cipher.encrypt(key, metadata, BridgeBearerCredential.fromSecret("token"))
        listOf(
            metadata.copy(profileId = "profile-b"),
            metadata.copy(apiBaseUrl = "https://192.168.1.21:6470"),
            metadata.copy(apiVersion = "v2"),
            metadata.copy(spkiSha256 = "different-spki"),
        ).forEach { changed ->
            assertAuthenticationFailure { cipher.decrypt(key, changed, encoded.copyOf()) }
        }
        val renamed = cipher.decrypt(
            key,
            metadata.copy(displayLabel = "Renamed locally"),
            encoded.copyOf(),
        )
        renamed.useUtf8 { assertEquals("token", it.toString(Charsets.UTF_8)) }
    }

    @Test
    fun ciphertextTagNonceAndWrongKeyFailClosed() {
        val cipher = cipher()
        val encoded = cipher.encrypt(key, metadata, BridgeBearerCredential.fromSecret("token"))
        val ciphertextTamper = encoded.copyOf().also { it[it.lastIndex - 16] = (it[it.lastIndex - 16].toInt() xor 1).toByte() }
        val tagTamper = encoded.copyOf().also { it[it.lastIndex] = (it.last().toInt() xor 1).toByte() }
        val nonceTamper = encoded.copyOf().also { it[10] = (it[10].toInt() xor 1).toByte() }
        listOf(ciphertextTamper, tagTamper, nonceTamper).forEach { changed ->
            assertAuthenticationFailure { cipher.decrypt(key, metadata, changed) }
        }
        assertAuthenticationFailure { cipher.decrypt(otherKey, metadata, encoded.copyOf()) }
    }

    @Test
    fun blobEnvelopeIsVersionedAndStrictlyBounded() {
        val cipher = cipher()
        val encoded = cipher.encrypt(key, metadata, BridgeBearerCredential.fromSecret("token"))
        assertContentEquals(byteArrayOf(0x4E, 0x36, 0x34, 0x54, 1, 12), encoded.copyOfRange(0, 6))
        assertFailsWith<CredentialVaultException> {
            ProfileTokenBlob.decode(encoded.copyOf(encoded.size - 1))
        }
        assertNotEquals("token", encoded.toString(Charsets.UTF_8))
    }

    private fun cipher() = AesGcmCredentialCipher()

    private fun metadata(profileId: String) = BridgePairingMetadata(
        profileId = profileId,
        displayLabel = "Bridge",
        apiBaseUrl = "https://192.168.1.20:6470",
        apiVersion = "v1",
        spkiSha256 = "spki",
        certificateSha256 = "AB".repeat(32),
        pairedAtEpochMillis = 1L,
    )

    private fun assertAuthenticationFailure(block: () -> Unit) {
        val error = assertFailsWith<CredentialVaultException>(block = block)
        assertTrue(
            error.error == PairingStorageError.CREDENTIAL_AUTHENTICATION_FAILED ||
                error.error == PairingStorageError.CREDENTIAL_READ_FAILED,
        )
    }

    private fun ByteArray.containsSubsequence(needle: ByteArray): Boolean =
        indices.any { start ->
            start + needle.size <= size &&
                copyOfRange(start, start + needle.size).contentEquals(needle)
        }
}
