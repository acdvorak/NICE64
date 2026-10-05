package com.melfegor.nice64.core

import com.melfegor.nice64.core.pairing.BridgeOnboardingParseError
import com.melfegor.nice64.core.pairing.BridgeOnboardingParseResult
import com.melfegor.nice64.core.pairing.BridgeOnboardingParser
import org.junit.Test
import java.util.Base64
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertTrue

class BridgeOnboardingParserTest {
    private val canonicalSpki = Base64.getEncoder().encodeToString(ByteArray(32) { it.toByte() })
    private val fingerprint = "AB".repeat(32)

    @Test
    fun exactValidContractIsAcceptedAndCanonicalized() {
        val candidate = success(validJson(url = "https://192.168.1.20:6470/"))
        assertEquals("https://192.168.1.20:6470", candidate.canonicalApiUrl)
        assertEquals("v1", candidate.apiVersion)
        assertEquals(canonicalSpki, candidate.spkiSha256)
        assertEquals(fingerprint, candidate.certificateSha256)
        assertFalse(candidate.toString().contains("valid-token"))
    }

    @Test
    fun nonObjectAndMalformedJsonAreRejected() {
        listOf("[]", "null", "\"text\"", "1", "true").forEach {
            assertFailure<BridgeOnboardingParseError.NonObjectRoot>(it)
        }
        listOf("", "not-json", "{", "{\"schema\":}", validJson() + "x").forEach {
            assertFailure<BridgeOnboardingParseError.MalformedJson>(it)
        }
    }

    @Test
    fun exactKeySetRejectsUnknownMissingNullWrongTypeAndCaseVariants() {
        assertFailure<BridgeOnboardingParseError.UnknownField>(
            validJson().replace("\"schema\":", "\"extra\":true,\"schema\":"),
        )
        assertFailure<BridgeOnboardingParseError.MissingField>(
            validJson().replace("\"schema\":\"nice64-bridge-onboarding\",", ""),
        )
        assertFailure<BridgeOnboardingParseError.MissingField>(
            validJson().replace("\"token\":\"valid-token\",", ""),
        )
        assertFailure<BridgeOnboardingParseError.NullField>(
            validJson().replace("\"token\":\"valid-token\"", "\"token\":null"),
        )
        assertFailure<BridgeOnboardingParseError.WrongFieldType>(
            validJson().replace("\"token\":\"valid-token\"", "\"token\":[\"valid-token\"]"),
        )
        assertFailure<BridgeOnboardingParseError.UnknownField>(
            validJson().replace("\"schema\":", "\"Schema\":"),
        )
    }

    @Test
    fun everyDuplicateKeyIsDetectedBeforeValueCoercion() {
        listOf("schema", "token", "url", "spkiSha256").forEach { key ->
            val duplicate = validJson().replaceFirst("{", "{\"$key\":\"evil\",")
            val failure = failure(duplicate)
            assertIs<BridgeOnboardingParseError.DuplicateField>(failure)
            assertEquals(key, failure.field)
        }
    }

    @Test
    fun schemaVersionAndApiVersionAreExact() {
        assertFailure<BridgeOnboardingParseError.InvalidSchema>(
            validJson(schema = "NICE64-bridge-onboarding"),
        )
        assertFailure<BridgeOnboardingParseError.InvalidSchema>(validJson(schema = "other"))
        success(validJson(version = "1"))
        assertFailure<BridgeOnboardingParseError.WrongFieldType>(validJson(version = "\"1\""))
        assertFailure<BridgeOnboardingParseError.UnsupportedVersion>(validJson(version = "1.0"))
        assertFailure<BridgeOnboardingParseError.UnsupportedVersion>(validJson(version = "2"))
        success(validJson(apiVersion = "v1"))
        assertFailure<BridgeOnboardingParseError.UnsupportedApiVersion>(
            validJson(apiVersion = "V1"),
        )
    }

    @Test
    fun urlRequiresHttpsLiteralCanonicalIpv4AndExplicitPort() {
        success(validJson(url = "https://192.168.1.20:6470"))
        listOf(
            "http://192.168.1.20:6470",
            "https://bridge.local:6470",
            "https://192.168.1.20",
            "https://user@192.168.1.20:6470",
            "https://192.168.1.20:6470?x=1",
            "https://192.168.1.20:6470#x",
            "https://192.168.1.20:6470/api",
            "https://999.168.1.20:6470",
            "https://192.168.01.20:6470",
            "https://[::1]:6470",
        ).forEach { assertFailure<BridgeOnboardingParseError.InvalidUrl>(validJson(url = it)) }
    }

    @Test
    fun tokenIsNonblankBoundedAndControlFree() {
        success(validJson(token = "x"))
        listOf("", "   ", "x".repeat(513)).forEach {
            assertFailure<BridgeOnboardingParseError.InvalidToken>(validJson(token = it))
        }
        assertFailure<BridgeOnboardingParseError.InvalidToken>(
            validJson(token = "valid\\u0001token", tokenAlreadyEscaped = true),
        )
    }

    @Test
    fun spkiMustBeCanonicalPaddedStandardBase64ForExactly32Bytes() {
        success(validJson(spki = canonicalSpki))
        val base64Url = Base64.getUrlEncoder().encodeToString(ByteArray(32) { 0xfb.toByte() })
        listOf(
            base64Url,
            "%%%",
            Base64.getEncoder().encodeToString(ByteArray(31)),
            canonicalSpki.removeSuffix("="),
            " $canonicalSpki",
        ).forEach { assertFailure<BridgeOnboardingParseError.InvalidSpki>(validJson(spki = it)) }
    }

    @Test
    fun certificateFingerprintIsExactlyUppercaseUnseparatedSha256Hex() {
        success(validJson(fingerprint = fingerprint))
        listOf(
            fingerprint.lowercase(),
            fingerprint.chunked(2).joinToString(":"),
            fingerprint.dropLast(2),
            "0x$fingerprint",
            "GZ".repeat(32),
        ).forEach {
            assertFailure<BridgeOnboardingParseError.InvalidCertificateFingerprint>(
                validJson(fingerprint = it),
            )
        }
    }

    private fun validJson(
        schema: String = BridgeOnboardingParser.SCHEMA,
        version: String = "1",
        apiVersion: String = "v1",
        url: String = "https://192.168.1.20:6470",
        token: String = "valid-token",
        tokenAlreadyEscaped: Boolean = false,
        spki: String = canonicalSpki,
        fingerprint: String = this.fingerprint,
    ): String = """
        {
          "schema":"${escape(schema)}",
          "version":$version,
          "apiVersion":"${escape(apiVersion)}",
          "url":"${escape(url)}",
          "token":"${if (tokenAlreadyEscaped) token else escape(token)}",
          "spkiSha256":"${escape(spki)}",
          "certificateSha256":"${escape(fingerprint)}"
        }
    """.trimIndent()

    private fun escape(value: String): String = value
        .replace("\\", "\\\\")
        .replace("\"", "\\\"")

    private fun success(raw: String) =
        assertIs<BridgeOnboardingParseResult.Success>(BridgeOnboardingParser.parse(raw)).candidate

    private fun failure(raw: String) =
        assertIs<BridgeOnboardingParseResult.Failure>(BridgeOnboardingParser.parse(raw)).error

    private inline fun <reified T : BridgeOnboardingParseError> assertFailure(raw: String) {
        assertTrue(failure(raw) is T, "Expected ${T::class.simpleName}")
    }
}
