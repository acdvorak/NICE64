package com.melfegor.nice64.tls

import android.annotation.SuppressLint
import com.melfegor.nice64.core.model.BridgeBearerCredential
import com.melfegor.nice64.core.model.BridgePairingMetadata
import com.melfegor.nice64.core.model.BridgePairingProfile
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import java.net.URI
import java.security.AlgorithmParameters
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.CertificateExpiredException
import java.security.cert.CertificateNotYetValidException
import java.security.cert.X509Certificate
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.util.Base64
import java.util.Date
import java.util.concurrent.TimeUnit
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSession
import javax.net.ssl.X509TrustManager

enum class BridgeTlsFailure {
    PROFILE_IDENTITY_INVALID,
    PROFILE_CREDENTIAL_UNAVAILABLE,
    CERTIFICATE_UNAVAILABLE,
    CERTIFICATE_EXPIRED,
    CERTIFICATE_NOT_YET_VALID,
    CERTIFICATE_NOT_END_ENTITY,
    CERTIFICATE_EKU_INVALID,
    CERTIFICATE_KEY_USAGE_INVALID,
    CERTIFICATE_KEY_TYPE_INVALID,
    CERTIFICATE_CURVE_INVALID,
    CERTIFICATE_SELF_SIGNATURE_INVALID,
    SPKI_PIN_MISMATCH,
    CERTIFICATE_FINGERPRINT_MISMATCH,
    HOST_MISMATCH,
    TLS_CONFIGURATION_FAILED,
    TLS_HANDSHAKE_FAILED,
}

class BridgeTlsValidationException(
    val failure: BridgeTlsFailure,
) : CertificateException(failure.name)

internal fun interface BridgeTlsClock {
    fun now(): Date
}

/** Validates exactly one paired Bridge leaf; it does not delegate to public PKI. */
class BridgeProfileCertificateValidator internal constructor(
    metadata: BridgePairingMetadata,
    private val clock: BridgeTlsClock = BridgeTlsClock(::Date),
) {
    private val expectedSpki = decodeSpki(metadata.spkiSha256)
    private val expectedFingerprint = decodeFingerprint(metadata.certificateSha256)

    fun validateLeaf(leaf: X509Certificate) {
        try {
            leaf.checkValidity(clock.now())
        } catch (_: CertificateExpiredException) {
            fail(BridgeTlsFailure.CERTIFICATE_EXPIRED)
        } catch (_: CertificateNotYetValidException) {
            fail(BridgeTlsFailure.CERTIFICATE_NOT_YET_VALID)
        } catch (_: Exception) {
            fail(BridgeTlsFailure.TLS_HANDSHAKE_FAILED)
        }

        if (leaf.basicConstraints != -1) {
            fail(BridgeTlsFailure.CERTIFICATE_NOT_END_ENTITY)
        }

        val eku = try {
            leaf.extendedKeyUsage
        } catch (_: Exception) {
            null
        }
        if (eku == null || SERVER_AUTH_OID !in eku) {
            fail(BridgeTlsFailure.CERTIFICATE_EKU_INVALID)
        }

        val keyUsage = leaf.keyUsage
        if (keyUsage == null || keyUsage.isEmpty() || !keyUsage[DIGITAL_SIGNATURE_BIT]) {
            fail(BridgeTlsFailure.CERTIFICATE_KEY_USAGE_INVALID)
        }

        val ecKey = leaf.publicKey as? ECPublicKey
            ?: fail(BridgeTlsFailure.CERTIFICATE_KEY_TYPE_INVALID)
        if (!leaf.publicKey.algorithm.equals("EC", ignoreCase = true)) {
            fail(BridgeTlsFailure.CERTIFICATE_KEY_TYPE_INVALID)
        }
        if (!sameCurve(ecKey.params, P256_PARAMETERS)) {
            fail(BridgeTlsFailure.CERTIFICATE_CURVE_INVALID)
        }

        try {
            leaf.verify(leaf.publicKey)
        } catch (_: Exception) {
            fail(BridgeTlsFailure.CERTIFICATE_SELF_SIGNATURE_INVALID)
        }

        val actualSpki = sha256(leaf.publicKey.encoded)
        try {
            if (!MessageDigest.isEqual(expectedSpki, actualSpki)) {
                fail(BridgeTlsFailure.SPKI_PIN_MISMATCH)
            }
        } finally {
            actualSpki.fill(0)
        }

        val actualFingerprint = sha256(leaf.encoded)
        try {
            if (!MessageDigest.isEqual(expectedFingerprint, actualFingerprint)) {
                fail(BridgeTlsFailure.CERTIFICATE_FINGERPRINT_MISMATCH)
            }
        } finally {
            actualFingerprint.fill(0)
        }
    }

    private fun sameCurve(actual: ECParameterSpec, expected: ECParameterSpec): Boolean =
        actual.curve == expected.curve &&
            actual.generator == expected.generator &&
            actual.order == expected.order &&
            actual.cofactor == expected.cofactor

    private fun decodeSpki(value: String): ByteArray {
        val decoded = try {
            Base64.getDecoder().decode(value)
        } catch (_: IllegalArgumentException) {
            fail(BridgeTlsFailure.PROFILE_IDENTITY_INVALID)
        }
        if (decoded.size != SHA256_BYTES) fail(BridgeTlsFailure.PROFILE_IDENTITY_INVALID)
        return decoded
    }

    private fun decodeFingerprint(value: String): ByteArray {
        if (!FINGERPRINT_PATTERN.matches(value)) {
            fail(BridgeTlsFailure.PROFILE_IDENTITY_INVALID)
        }
        return ByteArray(SHA256_BYTES) { index ->
            value.substring(index * 2, index * 2 + 2).toInt(16).toByte()
        }
    }

    private fun sha256(value: ByteArray): ByteArray =
        MessageDigest.getInstance("SHA-256").digest(value)

    private fun fail(failure: BridgeTlsFailure): Nothing =
        throw BridgeTlsValidationException(failure)

    companion object {
        private const val SERVER_AUTH_OID = "1.3.6.1.5.5.7.3.1"
        private const val DIGITAL_SIGNATURE_BIT = 0
        private const val SHA256_BYTES = 32
        private val FINGERPRINT_PATTERN = Regex("[0-9A-F]{64}")
        private val P256_PARAMETERS: ECParameterSpec = AlgorithmParameters.getInstance("EC").run {
            init(ECGenParameterSpec("secp256r1"))
            getParameterSpec(ECParameterSpec::class.java)
        }
    }
}

@SuppressLint("CustomX509TrustManager") // Deliberate exact-profile pinning; no broad trust fallback.
class BridgeProfileTrustManager internal constructor(
    private val validator: BridgeProfileCertificateValidator,
) : X509TrustManager {
    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        throw BridgeTlsValidationException(BridgeTlsFailure.TLS_HANDSHAKE_FAILED)
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull()
            ?: throw BridgeTlsValidationException(BridgeTlsFailure.CERTIFICATE_UNAVAILABLE)
        validator.validateLeaf(leaf)
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}

class BridgeProfileHostnamePolicy internal constructor(
    metadata: BridgePairingMetadata,
    private val validator: BridgeProfileCertificateValidator,
) : HostnameVerifier {
    val expectedHost: String = exactIpv4Host(metadata.apiBaseUrl)

    override fun verify(hostname: String?, session: SSLSession?): Boolean {
        if (hostname != expectedHost || session == null) return false
        val leaf = try {
            session.peerCertificates.firstOrNull() as? X509Certificate
        } catch (_: Exception) {
            null
        } ?: return false
        return matches(hostname, leaf)
    }

    fun matches(hostname: String, leaf: X509Certificate): Boolean {
        if (hostname != expectedHost) return false
        return try {
            validator.validateLeaf(leaf)
            true
        } catch (_: BridgeTlsValidationException) {
            false
        }
    }

    private fun exactIpv4Host(url: String): String {
        val uri = try {
            URI(url)
        } catch (_: Exception) {
            throw BridgeTlsValidationException(BridgeTlsFailure.PROFILE_IDENTITY_INVALID)
        }
        val host = uri.host ?: throw BridgeTlsValidationException(
            BridgeTlsFailure.PROFILE_IDENTITY_INVALID,
        )
        val parts = host.split('.')
        if (uri.scheme != "https" || uri.port !in 1..65535 || parts.size != 4 ||
            parts.any { it.isEmpty() || it.any { character -> !character.isDigit() } ||
                (it.length > 1 && it.startsWith('0')) || it.toIntOrNull() !in 0..255 }
        ) {
            throw BridgeTlsValidationException(BridgeTlsFailure.PROFILE_IDENTITY_INVALID)
        }
        return host
    }
}

internal object BridgeBearerAuthorization {
    fun apply(request: Request, credential: BridgeBearerCredential): Request =
        credential.useUtf8 { tokenBytes ->
            val token = tokenBytes.toString(Charsets.UTF_8)
            request.newBuilder()
                .header("Authorization", "Bearer $token")
                .build()
        }
}

class BridgeBearerInterceptor internal constructor(
    private val credential: BridgeBearerCredential,
) : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response =
        chain.proceed(BridgeBearerAuthorization.apply(chain.request(), credential))

    override fun toString(): String = "BridgeBearerInterceptor([REDACTED])"
}

data class BridgePinnedHttpsClient(
    val profileId: String,
    val apiBaseUrl: String,
    val httpClient: OkHttpClient,
) {
    override fun toString(): String =
        "BridgePinnedHttpsClient(profileId=$profileId, apiBaseUrl=$apiBaseUrl)"
}

sealed interface BridgePinnedClientResult {
    data class Success(val value: BridgePinnedHttpsClient) : BridgePinnedClientResult
    data class Failure(val failure: BridgeTlsFailure) : BridgePinnedClientResult
}

object BridgePinnedHttpsClientFactory {
    fun create(profile: BridgePairingProfile): BridgePinnedClientResult = try {
        val validator = BridgeProfileCertificateValidator(profile.metadata)
        val trustManager = BridgeProfileTrustManager(validator)
        val hostnamePolicy = BridgeProfileHostnamePolicy(profile.metadata, validator)
        val sslContext = SSLContext.getInstance("TLS").apply {
            init(null, arrayOf(trustManager), null)
        }
        val client = OkHttpClient.Builder()
            .sslSocketFactory(sslContext.socketFactory, trustManager)
            .hostnameVerifier(hostnamePolicy)
            .addInterceptor(BridgeBearerInterceptor(profile.credential))
            .followRedirects(false)
            .followSslRedirects(false)
            .retryOnConnectionFailure(false)
            .connectTimeout(5, TimeUnit.SECONDS)
            .readTimeout(15, TimeUnit.SECONDS)
            .writeTimeout(15, TimeUnit.SECONDS)
            .callTimeout(30, TimeUnit.SECONDS)
            .build()
        BridgePinnedClientResult.Success(
            BridgePinnedHttpsClient(
                profileId = profile.metadata.profileId,
                apiBaseUrl = profile.metadata.apiBaseUrl,
                httpClient = client,
            ),
        )
    } catch (error: BridgeTlsValidationException) {
        BridgePinnedClientResult.Failure(error.failure)
    } catch (_: Exception) {
        BridgePinnedClientResult.Failure(BridgeTlsFailure.TLS_CONFIGURATION_FAILED)
    }
}
