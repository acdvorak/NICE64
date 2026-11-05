package com.melfegor.nice64.core.pairing

import com.melfegor.nice64.core.model.BridgeBearerCredential
import com.melfegor.nice64.core.model.BridgeOnboardingCandidate
import java.net.URI
import java.util.Base64

sealed interface BridgeOnboardingParseResult {
    data class Success(val candidate: BridgeOnboardingCandidate) :
        BridgeOnboardingParseResult

    data class Failure(val error: BridgeOnboardingParseError) :
        BridgeOnboardingParseResult
}

sealed interface BridgeOnboardingParseError {
    data object MalformedJson : BridgeOnboardingParseError
    data object NonObjectRoot : BridgeOnboardingParseError
    data class UnknownField(val field: String) : BridgeOnboardingParseError
    data class DuplicateField(val field: String) : BridgeOnboardingParseError
    data class MissingField(val field: String) : BridgeOnboardingParseError
    data class NullField(val field: String) : BridgeOnboardingParseError
    data class WrongFieldType(val field: String) : BridgeOnboardingParseError
    data object InvalidSchema : BridgeOnboardingParseError
    data object UnsupportedVersion : BridgeOnboardingParseError
    data object UnsupportedApiVersion : BridgeOnboardingParseError
    data object InvalidUrl : BridgeOnboardingParseError
    data object InvalidToken : BridgeOnboardingParseError
    data object InvalidSpki : BridgeOnboardingParseError
    data object InvalidCertificateFingerprint : BridgeOnboardingParseError
}

object BridgeOnboardingParser {
    const val SCHEMA = "nice64-bridge-onboarding"
    const val VERSION = 1
    const val API_VERSION = "v1"
    const val MAX_TOKEN_CHARACTERS = 512

    private val fields = linkedSetOf(
        "schema",
        "version",
        "apiVersion",
        "url",
        "token",
        "spkiSha256",
        "certificateSha256",
    )
    private val certificatePattern = Regex("[0-9A-F]{64}")

    fun parse(rawJson: String): BridgeOnboardingParseResult {
        val values = try {
            FlatJsonObjectReader(rawJson).read()
        } catch (error: DuplicateJsonField) {
            return BridgeOnboardingParseResult.Failure(
                BridgeOnboardingParseError.DuplicateField(error.field),
            )
        } catch (_: NonObjectJsonRoot) {
            return BridgeOnboardingParseResult.Failure(
                BridgeOnboardingParseError.NonObjectRoot,
            )
        } catch (_: InvalidJson) {
            return BridgeOnboardingParseResult.Failure(
                BridgeOnboardingParseError.MalformedJson,
            )
        }

        values.keys.firstOrNull { it !in fields }?.let { field ->
            return BridgeOnboardingParseResult.Failure(
                BridgeOnboardingParseError.UnknownField(field),
            )
        }
        fields.firstOrNull { it !in values }?.let { field ->
            return BridgeOnboardingParseResult.Failure(
                BridgeOnboardingParseError.MissingField(field),
            )
        }
        values.entries.firstOrNull { it.value is JsonValue.Null }?.let { entry ->
            return BridgeOnboardingParseResult.Failure(
                BridgeOnboardingParseError.NullField(entry.key),
            )
        }

        val schema = values.string("schema") ?: return wrongType("schema")
        val version = values.number("version") ?: return wrongType("version")
        val apiVersion = values.string("apiVersion") ?: return wrongType("apiVersion")
        val url = values.string("url") ?: return wrongType("url")
        val token = values.string("token") ?: return wrongType("token")
        val spki = values.string("spkiSha256") ?: return wrongType("spkiSha256")
        val fingerprint = values.string("certificateSha256")
            ?: return wrongType("certificateSha256")

        if (schema != SCHEMA) return failure(BridgeOnboardingParseError.InvalidSchema)
        if (!version.integer || version.raw != VERSION.toString()) {
            return failure(BridgeOnboardingParseError.UnsupportedVersion)
        }
        if (apiVersion != API_VERSION) {
            return failure(BridgeOnboardingParseError.UnsupportedApiVersion)
        }
        val canonicalUrl = canonicalUrl(url)
            ?: return failure(BridgeOnboardingParseError.InvalidUrl)
        if (token.isBlank() || token.length > MAX_TOKEN_CHARACTERS ||
            token.any { it.code < 0x20 || Character.isISOControl(it.code) }
        ) {
            return failure(BridgeOnboardingParseError.InvalidToken)
        }
        if (!validCanonicalSpki(spki)) {
            return failure(BridgeOnboardingParseError.InvalidSpki)
        }
        if (!certificatePattern.matches(fingerprint)) {
            return failure(BridgeOnboardingParseError.InvalidCertificateFingerprint)
        }

        return BridgeOnboardingParseResult.Success(
            BridgeOnboardingCandidate(
                canonicalApiUrl = canonicalUrl,
                apiVersion = apiVersion,
                spkiSha256 = spki,
                certificateSha256 = fingerprint,
                credential = BridgeBearerCredential.fromSecret(token),
            ),
        )
    }

    private fun canonicalUrl(value: String): String? {
        val uri = try {
            URI(value)
        } catch (_: Exception) {
            return null
        }
        if (!uri.isAbsolute || uri.scheme != "https" || uri.isOpaque ||
            uri.rawUserInfo != null || uri.rawQuery != null || uri.rawFragment != null ||
            uri.port !in 1..65535 || uri.rawPath !in setOf("", "/")
        ) {
            return null
        }
        val host = uri.host ?: return null
        if (!isCanonicalIpv4(host)) return null
        return "https://$host:${uri.port}"
    }

    private fun isCanonicalIpv4(host: String): Boolean {
        val components = host.split('.')
        if (components.size != 4) return false
        return components.all { component ->
            component.isNotEmpty() &&
                component.all(Char::isDigit) &&
                (component == "0" || !component.startsWith('0')) &&
                component.toIntOrNull() in 0..255
        }
    }

    private fun validCanonicalSpki(value: String): Boolean {
        if (value.any(Char::isWhitespace)) return false
        val decoded = try {
            Base64.getDecoder().decode(value)
        } catch (_: IllegalArgumentException) {
            return false
        }
        return decoded.size == 32 && Base64.getEncoder().encodeToString(decoded) == value
    }

    private fun Map<String, JsonValue>.string(name: String): String? =
        (getValue(name) as? JsonValue.Text)?.value

    private fun Map<String, JsonValue>.number(name: String): JsonValue.Number? =
        getValue(name) as? JsonValue.Number

    private fun wrongType(field: String): BridgeOnboardingParseResult =
        failure(BridgeOnboardingParseError.WrongFieldType(field))

    private fun failure(error: BridgeOnboardingParseError) =
        BridgeOnboardingParseResult.Failure(error)
}

private sealed interface JsonValue {
    data class Text(val value: String) : JsonValue
    data class Number(val raw: String, val integer: Boolean) : JsonValue
    data object Null : JsonValue
    data object Other : JsonValue
}

private class InvalidJson : RuntimeException()
private class NonObjectJsonRoot : RuntimeException()
private class DuplicateJsonField(val field: String) : RuntimeException()

/** Strict JSON reader specialized for the frozen flat onboarding object. */
private class FlatJsonObjectReader(private val source: String) {
    private var index = 0

    fun read(): LinkedHashMap<String, JsonValue> {
        skipWhitespace()
        if (peek() != '{') {
            parseValue()
            finish()
            throw NonObjectJsonRoot()
        }
        index++
        val values = linkedMapOf<String, JsonValue>()
        skipWhitespace()
        if (consume('}')) {
            finish()
            return values
        }
        while (true) {
            skipWhitespace()
            val key = parseString()
            if (key in values) throw DuplicateJsonField(key)
            skipWhitespace()
            requireCharacter(':')
            skipWhitespace()
            values[key] = parseValue()
            skipWhitespace()
            if (consume('}')) break
            requireCharacter(',')
        }
        finish()
        return values
    }

    private fun finish() {
        skipWhitespace()
        if (index != source.length) throw InvalidJson()
    }

    private fun parseValue(): JsonValue = when (peek()) {
        '"' -> JsonValue.Text(parseString())
        'n' -> {
            requireLiteral("null")
            JsonValue.Null
        }
        't' -> {
            requireLiteral("true")
            JsonValue.Other
        }
        'f' -> {
            requireLiteral("false")
            JsonValue.Other
        }
        '{' -> {
            skipObject()
            JsonValue.Other
        }
        '[' -> {
            skipArray()
            JsonValue.Other
        }
        '-', in '0'..'9' -> parseNumber()
        else -> throw InvalidJson()
    }

    private fun parseNumber(): JsonValue.Number {
        val start = index
        consume('-')
        when (peek()) {
            '0' -> index++
            in '1'..'9' -> while (peek()?.isDigit() == true) index++
            else -> throw InvalidJson()
        }
        var integer = true
        if (consume('.')) {
            integer = false
            if (peek()?.isDigit() != true) throw InvalidJson()
            while (peek()?.isDigit() == true) index++
        }
        if (peek() == 'e' || peek() == 'E') {
            integer = false
            index++
            if (peek() == '+' || peek() == '-') index++
            if (peek()?.isDigit() != true) throw InvalidJson()
            while (peek()?.isDigit() == true) index++
        }
        return JsonValue.Number(source.substring(start, index), integer)
    }

    private fun skipObject() {
        requireCharacter('{')
        skipWhitespace()
        if (consume('}')) return
        while (true) {
            skipWhitespace()
            parseString()
            skipWhitespace()
            requireCharacter(':')
            skipWhitespace()
            parseValue()
            skipWhitespace()
            if (consume('}')) return
            requireCharacter(',')
        }
    }

    private fun skipArray() {
        requireCharacter('[')
        skipWhitespace()
        if (consume(']')) return
        while (true) {
            skipWhitespace()
            parseValue()
            skipWhitespace()
            if (consume(']')) return
            requireCharacter(',')
        }
    }

    private fun parseString(): String {
        requireCharacter('"')
        val result = StringBuilder()
        while (index < source.length) {
            val character = source[index++]
            when {
                character == '"' -> return result.toString()
                character == '\\' -> result.append(parseEscape())
                character.code < 0x20 -> throw InvalidJson()
                else -> result.append(character)
            }
        }
        throw InvalidJson()
    }

    private fun parseEscape(): Char = when (val escaped = next()) {
        '"', '\\', '/' -> escaped
        'b' -> '\b'
        'f' -> '\u000C'
        'n' -> '\n'
        'r' -> '\r'
        't' -> '\t'
        'u' -> {
            if (index + 4 > source.length) throw InvalidJson()
            val value = source.substring(index, index + 4).toIntOrNull(16)
                ?: throw InvalidJson()
            index += 4
            value.toChar()
        }
        else -> throw InvalidJson()
    }

    private fun requireLiteral(literal: String) {
        if (!source.startsWith(literal, index)) throw InvalidJson()
        index += literal.length
    }

    private fun requireCharacter(character: Char) {
        if (!consume(character)) throw InvalidJson()
    }

    private fun consume(character: Char): Boolean {
        if (peek() != character) return false
        index++
        return true
    }

    private fun next(): Char {
        if (index >= source.length) throw InvalidJson()
        return source[index++]
    }

    private fun peek(): Char? = source.getOrNull(index)

    private fun skipWhitespace() {
        while (peek() in listOf(' ', '\t', '\r', '\n')) index++
    }
}
