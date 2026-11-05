package com.melfegor.nice64.core.model

/** Syntax-valid onboarding material. It is not a network-verified Bridge identity. */
class BridgeOnboardingCandidate(
    val canonicalApiUrl: String,
    val apiVersion: String,
    val spkiSha256: String,
    val certificateSha256: String,
    val credential: BridgeBearerCredential,
) {
    override fun toString(): String =
        "BridgeOnboardingCandidate(canonicalApiUrl=$canonicalApiUrl, " +
            "apiVersion=$apiVersion, spkiSha256=$spkiSha256, " +
            "certificateSha256=$certificateSha256, credential=[REDACTED])"
}
