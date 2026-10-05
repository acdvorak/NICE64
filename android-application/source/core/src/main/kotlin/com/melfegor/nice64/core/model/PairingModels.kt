package com.melfegor.nice64.core.model

/** A bearer credential with deliberately non-revealing diagnostics. */
class BridgeBearerCredential private constructor(private val secret: String) {
    init {
        require(secret.isNotBlank()) { "Bearer credential must not be blank." }
    }

    override fun toString(): String = "BridgeBearerCredential([REDACTED])"

    /** Limits the additional lifetime of mutable UTF-8 credential bytes. */
    fun <T> useUtf8(block: (ByteArray) -> T): T {
        val bytes = secret.toByteArray(Charsets.UTF_8)
        return try {
            block(bytes)
        } finally {
            bytes.fill(0)
        }
    }

    companion object {
        fun fromSecret(secret: String): BridgeBearerCredential = BridgeBearerCredential(secret)
    }
}

/** The exact seven semantic values carried by the frozen onboarding contract. */
class BridgeOnboardingPayload(
    val schema: String,
    val version: Int,
    val apiVersion: String,
    val url: String,
    val token: BridgeBearerCredential,
    val spkiSha256: String,
    val certificateSha256: String,
) {
    override fun toString(): String =
        "BridgeOnboardingPayload(schema=$schema, version=$version, " +
            "apiVersion=$apiVersion, url=$url, token=[REDACTED], " +
            "spkiSha256=$spkiSha256, certificateSha256=$certificateSha256)"
}

/** Non-secret profile metadata suitable for future ordinary app persistence. */
data class BridgePairingMetadata(
    val profileId: String,
    val displayLabel: String,
    val apiBaseUrl: String,
    val apiVersion: String,
    val spkiSha256: String,
    val certificateSha256: String,
    val pairedAtEpochMillis: Long,
)

/** Secret and non-secret pairing material are composed only at the use boundary. */
class BridgePairingProfile(
    val metadata: BridgePairingMetadata,
    val credential: BridgeBearerCredential,
) {
    override fun toString(): String =
        "BridgePairingProfile(metadata=$metadata, credential=[REDACTED])"
}

sealed interface PairingState {
    data object Unpaired : PairingState
    data object Scanning : PairingState
    data object Parsing : PairingState
    data class Validating(val candidate: BridgePairingMetadata) : PairingState
    data class Paired(val profile: BridgePairingMetadata) : PairingState
    data class Stored(val profile: BridgePairingMetadata) : PairingState
    data class CredentialUnavailable(val profileId: String) : PairingState
    data class PinMismatch(val profileId: String) : PairingState
    data class RePairRequired(val profileId: String) : PairingState
    data class Error(val safeReason: String) : PairingState
    data class StorageError(val safeReason: String) : PairingState
}
