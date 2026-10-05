package com.melfegor.nice64.tls

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.core.model.BridgeBearerCredential
import com.melfegor.nice64.core.model.BridgePairingMetadata
import com.melfegor.nice64.core.model.BridgePairingProfile
import okhttp3.Request
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayInputStream
import java.security.MessageDigest
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.Base64

@RunWith(AndroidJUnit4::class)
class BridgePinnedHttpsClientInstrumentedTest {
    @Test
    fun androidTlsAndOkHttpCompositionUsesExactPinnedProfile(): Unit {
        val leaf = CertificateFactory.getInstance("X.509")
            .generateCertificate(ByteArrayInputStream(Base64.getDecoder().decode(CERTIFICATE)))
            as X509Certificate
        val metadata = BridgePairingMetadata(
            profileId = "android-profile",
            displayLabel = "Bridge",
            apiBaseUrl = "https://192.168.2.81:6470",
            apiVersion = "v1",
            spkiSha256 = Base64.getEncoder().encodeToString(sha256(leaf.publicKey.encoded)),
            certificateSha256 = sha256(leaf.encoded).joinToString("") {
                "%02X".format(it.toInt() and 0xff)
            },
            pairedAtEpochMillis = 1L,
        )
        val validator = BridgeProfileCertificateValidator(metadata)
        validator.validateLeaf(leaf)
        BridgeProfileTrustManager(validator).checkServerTrusted(arrayOf(leaf), "ECDHE_ECDSA")

        val hostnamePolicy = BridgeProfileHostnamePolicy(metadata, validator)
        assertTrue(hostnamePolicy.matches("192.168.2.81", leaf))
        assertFalse(hostnamePolicy.matches("192.168.2.82", leaf))

        val token = "android-runtime-secret"
        val profile = BridgePairingProfile(metadata, BridgeBearerCredential.fromSecret(token))
        val result = BridgePinnedHttpsClientFactory.create(profile)
        assertTrue(result is BridgePinnedClientResult.Success)
        val client = (result as BridgePinnedClientResult.Success).value.httpClient
        assertFalse(client.followRedirects)
        assertFalse(client.followSslRedirects)
        assertTrue(client.hostnameVerifier is BridgeProfileHostnamePolicy)
        assertTrue(client.x509TrustManager is BridgeProfileTrustManager)

        val request = Request.Builder().url("https://192.168.2.81:6470/").build()
        assertEquals(
            "Bearer $token",
            BridgeBearerAuthorization.apply(request, profile.credential)
                .header("Authorization"),
        )
        assertFalse(result.toString().contains(token))
    }

    private fun sha256(value: ByteArray): ByteArray =
        MessageDigest.getInstance("SHA-256").digest(value)

    companion object {
        private const val CERTIFICATE =
            "MIIBbjCCARWgAwIBAgIIScIRrEOfLDYwCgYIKoZIzj0EAwIwIjEgMB4GA1UEAxMX" +
                "TklDRTY0IEFuZHJvaWQgRDhDIFRlc3QwHhcNMjUwMTAxMDAwMDAwWhcNMzAwMTAx" +
                "MDAwMDAwWjAiMSAwHgYDVQQDExdOSUNFNjQgQW5kcm9pZCBEOEMgVGVzdDBZMBMG" +
                "ByqGSM49AgEGCCqGSM49AwEHA0IABBMoLKNQmEvAfBpk7z8ZK1xW1S4AUjYWfRos" +
                "abQiOBopSdy1vB3F2xqYiqPgK9In7ditGDoDWTTXluiAnafPy/2jNTAzMAwGA1Ud" +
                "EwEB/wQCMAAwDgYDVR0PAQH/BAQDAgeAMBMGA1UdJQQMMAoGCCsGAQUFBwMBMAoG" +
                "CCqGSM49BAMCA0cAMEQCIA7KAApOW8scZEezfHCdTpNp8H7iCQbjvCZfs4U000gI" +
                "AiB3+O7UwGFSkNUcsIF7hFAjHxPkhKaWOkupSo46n4Tfmg=="
    }
}
