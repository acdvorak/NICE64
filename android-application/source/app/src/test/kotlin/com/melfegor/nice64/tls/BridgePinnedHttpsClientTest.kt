package com.melfegor.nice64.tls

import com.melfegor.nice64.core.model.BridgeBearerCredential
import com.melfegor.nice64.core.model.BridgePairingMetadata
import com.melfegor.nice64.core.model.BridgePairingProfile
import okhttp3.Request
import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.asn1.x509.BasicConstraints
import org.bouncycastle.asn1.x509.ExtendedKeyUsage
import org.bouncycastle.asn1.x509.Extension
import org.bouncycastle.asn1.x509.KeyPurposeId
import org.bouncycastle.asn1.x509.KeyUsage
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.jce.provider.BouncyCastleProvider
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.math.BigInteger
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.Provider
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.time.Instant
import java.util.Base64
import java.util.Date
import kotlin.test.assertFailsWith

class BridgePinnedHttpsClientTest {
    private val fixtures = CertificateFixtures()
    private val now = Date.from(Instant.parse("2026-09-09T12:00:00Z"))

    @Test
    fun validBridgeStyleLeafIsAcceptedByValidatorAndTrustManager() {
        val leaf = fixtures.certificate()
        val validator = validator(leaf)
        validator.validateLeaf(leaf)
        BridgeProfileTrustManager(validator).checkServerTrusted(arrayOf(leaf), "ECDHE_ECDSA")
    }

    @Test
    fun spkiAndCertificateFingerprintMustBothMatchExactly() {
        val leaf = fixtures.certificate()
        val other = fixtures.certificate(subject = "CN=Other")

        assertFailure(BridgeTlsFailure.SPKI_PIN_MISMATCH) {
            validator(leaf, spkiFrom = other).validateLeaf(leaf)
        }
        assertFailure(BridgeTlsFailure.CERTIFICATE_FINGERPRINT_MISMATCH) {
            validator(leaf, fingerprintFrom = other).validateLeaf(leaf)
        }
    }

    @Test
    fun expiredAndNotYetValidCertificatesAreRejected() {
        assertFailure(BridgeTlsFailure.CERTIFICATE_EXPIRED) {
            val leaf = fixtures.certificate(
                notBefore = date("2020-01-01T00:00:00Z"),
                notAfter = date("2021-01-01T00:00:00Z"),
            )
            validator(leaf).validateLeaf(leaf)
        }
        assertFailure(BridgeTlsFailure.CERTIFICATE_NOT_YET_VALID) {
            val leaf = fixtures.certificate(
                notBefore = date("2030-01-01T00:00:00Z"),
                notAfter = date("2031-01-01T00:00:00Z"),
            )
            validator(leaf).validateLeaf(leaf)
        }
    }

    @Test
    fun caMissingEkuAndUnrelatedEkuAreRejected() {
        val ca = fixtures.certificate(isCa = true)
        assertFailure(BridgeTlsFailure.CERTIFICATE_NOT_END_ENTITY) {
            validator(ca).validateLeaf(ca)
        }
        val noEku = fixtures.certificate(eku = null)
        assertFailure(BridgeTlsFailure.CERTIFICATE_EKU_INVALID) {
            validator(noEku).validateLeaf(noEku)
        }
        val clientOnly = fixtures.certificate(eku = KeyPurposeId.id_kp_clientAuth)
        assertFailure(BridgeTlsFailure.CERTIFICATE_EKU_INVALID) {
            validator(clientOnly).validateLeaf(clientOnly)
        }
    }

    @Test
    fun missingDigitalSignatureRsaAndP384AreRejected() {
        val keyAgreementOnly = fixtures.certificate(keyUsage = KeyUsage.keyAgreement)
        assertFailure(BridgeTlsFailure.CERTIFICATE_KEY_USAGE_INVALID) {
            validator(keyAgreementOnly).validateLeaf(keyAgreementOnly)
        }
        val rsa = fixtures.certificate(keyType = KeyType.RSA)
        assertFailure(BridgeTlsFailure.CERTIFICATE_KEY_TYPE_INVALID) {
            validator(rsa).validateLeaf(rsa)
        }
        val p384 = fixtures.certificate(keyType = KeyType.P384)
        assertFailure(BridgeTlsFailure.CERTIFICATE_CURVE_INVALID) {
            validator(p384).validateLeaf(p384)
        }
    }

    @Test
    fun falseSelfSignatureIsRejected() {
        val leaf = fixtures.certificate(signWithDifferentKey = true)
        assertFailure(BridgeTlsFailure.CERTIFICATE_SELF_SIGNATURE_INVALID) {
            validator(leaf).validateLeaf(leaf)
        }
    }

    @Test
    fun hostnamePolicyAcceptsOnlyExactStoredIpv4AndPinnedPeer() {
        val leaf = fixtures.certificate()
        val metadata = metadata(leaf, host = "192.168.2.81")
        val policy = BridgeProfileHostnamePolicy(metadata, validator(leaf))
        assertTrue(policy.matches("192.168.2.81", leaf))
        assertFalse(policy.matches("192.168.2.82", leaf))
        assertFalse(policy.matches("bridge.local", leaf))
        assertFalse(policy.matches("localhost", leaf))
        assertFalse(policy.matches("192.168.2.81", fixtures.certificate(subject = "CN=Other")))
    }

    @Test
    fun redirectsRetriesAndGlobalTrustFallbackAreDisabled() {
        val leaf = fixtures.certificate()
        val result = BridgePinnedHttpsClientFactory.create(profile(leaf))
        val client = (result as BridgePinnedClientResult.Success).value.httpClient
        assertFalse(client.followRedirects)
        assertFalse(client.followSslRedirects)
        assertFalse(client.retryOnConnectionFailure)
        assertTrue(client.hostnameVerifier is BridgeProfileHostnamePolicy)
        assertTrue(client.x509TrustManager is BridgeProfileTrustManager)
    }

    @Test
    fun bearerHeaderIsExactAndDiagnosticsAreRedacted() {
        val token = "exact-secret-token"
        val credential = BridgeBearerCredential.fromSecret(token)
        val request = Request.Builder().url("https://192.168.2.81:6470/").build()
        val authorized = BridgeBearerAuthorization.apply(request, credential)
        assertEquals("Bearer $token", authorized.header("Authorization"))
        assertFalse(BridgeBearerInterceptor(credential).toString().contains(token))

        val leaf = fixtures.certificate()
        val client = BridgePinnedHttpsClientFactory.create(
            profile(leaf, token = token),
        ) as BridgePinnedClientResult.Success
        assertFalse(client.value.toString().contains(token))
        assertFalse(client.value.httpClient.toString().contains(token))
    }

    @Test
    fun profilesCannotAuthenticateEachOthersCertificates() {
        val leafA = fixtures.certificate(subject = "CN=Bridge-A")
        val leafB = fixtures.certificate(subject = "CN=Bridge-B")
        val validatorA = validator(leafA, profileId = "profile-a")
        val validatorB = validator(leafB, profileId = "profile-b")
        assertFailure(BridgeTlsFailure.SPKI_PIN_MISMATCH) { validatorA.validateLeaf(leafB) }
        assertFailure(BridgeTlsFailure.SPKI_PIN_MISMATCH) { validatorB.validateLeaf(leafA) }
    }

    private fun validator(
        leaf: X509Certificate,
        spkiFrom: X509Certificate = leaf,
        fingerprintFrom: X509Certificate = leaf,
        profileId: String = "profile-a",
    ) = BridgeProfileCertificateValidator(
        metadata(leaf, profileId = profileId).copy(
            spkiSha256 = spki(spkiFrom),
            certificateSha256 = fingerprint(fingerprintFrom),
        ),
        BridgeTlsClock { Date(now.time) },
    )

    private fun profile(
        leaf: X509Certificate,
        token: String = "secret-token",
    ) = BridgePairingProfile(
        metadata(leaf),
        BridgeBearerCredential.fromSecret(token),
    )

    private fun metadata(
        leaf: X509Certificate,
        profileId: String = "profile-a",
        host: String = "192.168.2.81",
    ) = BridgePairingMetadata(
        profileId = profileId,
        displayLabel = "Bridge",
        apiBaseUrl = "https://$host:6470",
        apiVersion = "v1",
        spkiSha256 = spki(leaf),
        certificateSha256 = fingerprint(leaf),
        pairedAtEpochMillis = 1L,
    )

    private fun spki(certificate: X509Certificate): String = Base64.getEncoder().encodeToString(
        MessageDigest.getInstance("SHA-256").digest(certificate.publicKey.encoded),
    )

    private fun fingerprint(certificate: X509Certificate): String =
        MessageDigest.getInstance("SHA-256").digest(certificate.encoded)
            .joinToString("") { "%02X".format(it.toInt() and 0xff) }

    private fun date(value: String): Date = Date.from(Instant.parse(value))

    private fun assertFailure(expected: BridgeTlsFailure, block: () -> Unit) {
        assertEquals(
            expected,
            assertFailsWith<BridgeTlsValidationException>(block = block).failure,
        )
    }
}

private enum class KeyType { P256, P384, RSA }

private class CertificateFixtures {
    private val provider: Provider = BouncyCastleProvider()
    private var serial = 1L

    fun certificate(
        subject: String = "CN=NICE64 Bridge Test",
        notBefore: Date = Date.from(Instant.parse("2025-01-01T00:00:00Z")),
        notAfter: Date = Date.from(Instant.parse("2030-01-01T00:00:00Z")),
        isCa: Boolean = false,
        eku: KeyPurposeId? = KeyPurposeId.id_kp_serverAuth,
        keyUsage: Int = KeyUsage.digitalSignature,
        keyType: KeyType = KeyType.P256,
        signWithDifferentKey: Boolean = false,
    ): X509Certificate {
        val keyPair = keyPair(keyType)
        val signingPair = if (signWithDifferentKey) keyPair(keyType) else keyPair
        val name = X500Name(subject)
        val builder = JcaX509v3CertificateBuilder(
            name,
            BigInteger.valueOf(serial++),
            notBefore,
            notAfter,
            name,
            keyPair.public,
        )
            .addExtension(Extension.basicConstraints, true, BasicConstraints(isCa))
            .addExtension(Extension.keyUsage, true, KeyUsage(keyUsage))
        if (eku != null) {
            builder.addExtension(
                Extension.extendedKeyUsage,
                false,
                ExtendedKeyUsage(eku),
            )
        }
        val signature = if (keyType == KeyType.RSA) "SHA256withRSA" else "SHA256withECDSA"
        val holder = builder.build(
            JcaContentSignerBuilder(signature).setProvider(provider).build(signingPair.private),
        )
        return JcaX509CertificateConverter().setProvider(provider).getCertificate(holder)
    }

    private fun keyPair(type: KeyType): KeyPair = when (type) {
        KeyType.P256 -> KeyPairGenerator.getInstance("EC").apply {
            initialize(ECGenParameterSpec("secp256r1"))
        }.generateKeyPair()
        KeyType.P384 -> KeyPairGenerator.getInstance("EC").apply {
            initialize(ECGenParameterSpec("secp384r1"))
        }.generateKeyPair()
        KeyType.RSA -> KeyPairGenerator.getInstance("RSA").apply {
            initialize(2048)
        }.generateKeyPair()
    }
}
