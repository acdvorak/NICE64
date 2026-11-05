package com.melfegor.nice64.core

import com.melfegor.nice64.core.model.BridgeBearerCredential
import com.melfegor.nice64.core.model.BridgeConnectionKind
import com.melfegor.nice64.core.model.BridgeOnboardingPayload
import com.melfegor.nice64.core.model.BridgeOperationStatus
import com.melfegor.nice64.core.model.BridgePairingMetadata
import com.melfegor.nice64.core.model.BridgeSessionStatus
import com.melfegor.nice64.core.model.HistoryCategory
import com.melfegor.nice64.core.model.HistoryItem
import org.junit.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class FrozenModelsTest {
    @Test
    fun onboardingModelHasExactlySevenFrozenFieldsAndRedactsToken() {
        val secret = "top-secret-bearer"
        val model = BridgeOnboardingPayload(
            schema = "nice64-bridge-onboarding",
            version = 1,
            apiVersion = "v1",
            url = "https://192.0.2.1:6470",
            token = BridgeBearerCredential.fromSecret(secret),
            spkiSha256 = "spki",
            certificateSha256 = "cert",
        )
        val fields = BridgeOnboardingPayload::class.java.declaredFields
            .map { it.name }
            .filterNot { it.startsWith("\$") }
            .toSet()
        assertEquals(
            setOf("schema", "version", "apiVersion", "url", "token", "spkiSha256", "certificateSha256"),
            fields,
        )
        assertFalse(model.toString().contains(secret))
        assertFalse(model.token.toString().contains(secret))
    }

    @Test
    fun nonSecretMetadataAndHistoryCannotCarryForbiddenSecretsOrPaths() {
        val metadataFields = BridgePairingMetadata::class.java.declaredFields
            .map { it.name.lowercase() }
        assertTrue(metadataFields.none { it.contains("token") || it.contains("credential") })
        assertTrue(
            metadataFields.none {
                it.contains("session") || it.contains("lease") ||
                    it.contains("controllerendpoint") || it.contains("picoendpoint")
            },
        )

        val historyFields = HistoryItem::class.java.declaredFields
            .map { it.name.lowercase() }
        assertTrue(historyFields.none { it.contains("path") || it.contains("directory") })
    }

    @Test
    fun frozenSessionOperationAndHistoryStatesAreRepresentable() {
        assertEquals(
            setOf("CONNECTED", "TEMPORARILY_UNAVAILABLE", "UNKNOWN"),
            BridgeSessionStatus.entries.map { it.name }.toSet(),
        )
        assertEquals(
            setOf("USB", "WIFI_TCP", "UNKNOWN"),
            BridgeConnectionKind.entries.map { it.name }.toSet(),
        )
        assertEquals(
            setOf("NONE", "RUNNING", "SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"),
            BridgeOperationStatus.entries.map { it.name }.toSet(),
        )
        assertEquals(
            setOf("CONTROLLER_PAK", "TRANSFER_PAK"),
            HistoryCategory.entries.map { it.name }.toSet(),
        )
    }
}
