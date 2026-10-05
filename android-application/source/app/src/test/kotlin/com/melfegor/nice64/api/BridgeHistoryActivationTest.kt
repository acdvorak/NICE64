package com.melfegor.nice64.api

import com.melfegor.nice64.core.model.BridgeBearerCredential
import com.melfegor.nice64.core.model.BridgePairingMetadata
import com.melfegor.nice64.core.model.BridgePairingProfile
import com.melfegor.nice64.tls.BridgePinnedHttpsClient
import java.io.IOException
import java.util.concurrent.Executors
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import okio.Buffer
import org.junit.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class BridgeHistoryActivationTest {
    @Test
    fun controllerPakUsesExactOpaqueRoutePostAndFalseBody() = runTest {
        val executor = FakeExecutor { accepted() }
        val item = item("opaque/id?still-opaque", "safe.mpk")
        val result = client(executor).submit(
            BridgeHistoryActivationCommand.controllerPak(SESSION, item),
        )
        assertIs<BridgeHistoryActivationSubmission.Accepted>(result)
        val request = executor.requests.single()
        assertEquals("POST", request.method)
        assertEquals(
            "/api/v1/sessions/$SESSION/history/opaque%2Fid%3Fstill-opaque/activate",
            request.url.encodedPath,
        )
        assertEquals("""{"destructiveInstallConfirmed":false}""", body(request))
        assertFalse(request.url.toString().contains("safe.mpk"))
    }

    @Test
    fun transferPakRequiresNewExplicitConfirmationAndSendsTrueOnlyThen() = runTest {
        val item = item("transfer-id", "game.gbc")
        assertNull(BridgeHistoryActivationCommand.transferPak(SESSION, item, false))
        val command = assertNotNull(
            BridgeHistoryActivationCommand.transferPak(SESSION, item, true),
        )
        val executor = FakeExecutor { accepted() }
        client(executor).submit(command)
        assertEquals("""{"destructiveInstallConfirmed":true}""",
            body(executor.requests.single()))
        assertFailsWith<IllegalArgumentException> {
            BridgeHistoryActivationCommand(
                SESSION, "controller-id", BridgeHistoryCategory.CONTROLLER_PAK,
                "pak.mpk", true,
            )
        }
    }

    @Test
    fun acceptedRequiresPositiveIdAndExactHistoryOperationKind() = runTest {
        suspend fun submit(payload: String) = client(FakeExecutor {
            BridgeHttpPayload(202, payload)
        }).submit(BridgeHistoryActivationCommand.controllerPak(SESSION, item()))

        assertIs<BridgeHistoryActivationSubmission.Accepted>(submit(accepted().body))
        listOf(
            """{"accepted":true,"operationId":0,"operationKind":"history-activation","currentOperationId":null,"currentOperationKind":null,"code":null,"detail":null,"confirmation":null}""",
            """{"accepted":true,"operationId":4,"operationKind":"accessory-selection","currentOperationId":null,"currentOperationKind":null,"code":null,"detail":null,"confirmation":null}""",
            "{}",
        ).forEach {
            val rejected = assertIs<BridgeHistoryActivationSubmission.Rejected>(submit(it))
            assertEquals(BridgeHistoryActivationFailureKind.CONTRACT, rejected.failure.kind)
        }
    }

    @Test
    fun safeErrorCodesAreMappedWithoutLeakingServerDetail() = runTest {
        suspend fun failure(status: Int, code: String): BridgeHistoryActivationFailure {
            val payload = response(false, code = code, detail = "C:\\secret\\private.mpk token=secret")
            val result = client(FakeExecutor { BridgeHttpPayload(status, payload) }).submit(
                BridgeHistoryActivationCommand.controllerPak(SESSION, item()),
            )
            return assertIs<BridgeHistoryActivationSubmission.Rejected>(result).failure
        }
        assertEquals(BridgeHistoryActivationFailureKind.INVALID_CONFIRMATION,
            failure(400, "invalid-confirmation").kind)
        assertEquals(BridgeHistoryActivationFailureKind.SESSION_NOT_FOUND,
            failure(404, "session-not-found").kind)
        assertEquals(BridgeHistoryActivationFailureKind.ITEM_NOT_FOUND,
            failure(404, "history-item-not-found").kind)
        assertEquals(BridgeHistoryActivationFailureKind.CONFIRMATION_REQUIRED,
            failure(409, "confirmation-required").kind)
        assertEquals(BridgeHistoryActivationFailureKind.BUSY,
            failure(409, "operation-busy").kind)
        assertEquals(BridgeHistoryActivationFailureKind.ITEM_UNAVAILABLE,
            failure(409, "history-item-unavailable").kind)
        assertFalse(failure(409, "activation-rejected").toString().contains("secret"))
    }

    @Test
    fun transportFailureAndCancellationIssueOnePostOnly() = runTest {
        listOf<suspend () -> Nothing>(
            { throw IOException("response lost token=secret") },
            { throw CancellationException("cancelled after send") },
        ).forEach { failure ->
            val executor = FakeExecutor { failure() }
            val result = client(executor).submit(
                BridgeHistoryActivationCommand.controllerPak(SESSION, item()),
            )
            assertIs<BridgeHistoryActivationSubmission.Uncertain>(result)
            assertEquals(1, executor.requests.count { it.method == "POST" })
            assertFalse(result.toString().contains("secret"))
        }
    }

    @Test
    fun productionExecutorMovesHistoryPostOffCallerThread() = runTest {
        val threads = mutableListOf<String>()
        val http = OkHttpClient.Builder().addInterceptor { chain ->
            synchronized(threads) { threads += Thread.currentThread().name }
            Response.Builder().request(chain.request()).protocol(Protocol.HTTP_1_1)
                .code(200).message("OK").body("{}".toResponseBody()).build()
        }.build()
        val executor = OkHttpMutationExecutor(BridgePinnedHttpsClient(
            "profile-a", "https://192.168.2.36:6470", http,
        ))
        val caller = Executors.newSingleThreadExecutor { Thread(it, "D8G-Test-Main") }
        val dispatcher = caller.asCoroutineDispatcher()
        try {
            withContext(dispatcher) {
                executor.execute(Request.Builder()
                    .url("https://192.168.2.36:6470/api/v1/sessions/s/history/i/activate")
                    .post("{}".toRequestBody()).build())
            }
            assertTrue(threads.all { it != "D8G-Test-Main" })
        } finally {
            dispatcher.close()
            caller.shutdownNow()
        }
    }

    @Test
    fun profileOrSessionChangePreventsStaleSubmission() = runTest {
        val profileExecutor = FakeExecutor { error("must not execute") }
        val profileResult = coordinator(profileExecutor, activeProfile = "profile-b").submit(
            "profile-a", BridgeHistoryActivationCommand.controllerPak(SESSION, item()),
        )
        assertEquals(BridgeHistoryActivationFailureKind.PROFILE_CHANGED,
            profileResult.failure?.kind)
        assertTrue(profileExecutor.requests.isEmpty())

        val sessionExecutor = FakeExecutor { request -> when (request.url.encodedPath) {
            "/api/v1/sessions" -> ok(sessions("new-session"))
            "/api/v1/history" -> ok(history())
            else -> error("must not POST")
        } }
        val sessionResult = coordinator(sessionExecutor).submit(
            "profile-a", BridgeHistoryActivationCommand.controllerPak(SESSION, item()),
        )
        assertEquals(BridgeHistoryActivationFailureKind.SESSION_CHANGED,
            sessionResult.failure?.kind)
        assertTrue(sessionExecutor.requests.none { it.method == "POST" })
    }

    @Test
    fun unavailableOrChangedItemPreventsSubmission() = runTest {
        val executor = FakeExecutor { request -> when (request.url.encodedPath) {
            "/api/v1/sessions" -> ok(sessions())
            "/api/v1/history" -> ok(history(available = false))
            else -> error("must not POST")
        } }
        val result = coordinator(executor).submit(
            "profile-a", BridgeHistoryActivationCommand.controllerPak(SESSION, item()),
        )
        assertEquals(BridgeHistoryActivationFailureKind.SESSION_CHANGED, result.failure?.kind)
        assertTrue(executor.requests.none { it.method == "POST" })
    }

    @Test
    fun acceptedActivationCorrelatesOperationAndRefreshesAllAuthority() = runTest {
        var operationReads = 0
        val executor = FakeExecutor { request -> when (request.url.encodedPath) {
            "/api/v1/sessions" -> ok(sessions())
            "/api/v1/history" -> ok(history())
            "/api/v1/operation" -> {
                operationReads++
                ok(if (operationReads == 1) operation(0, "none", "idle")
                    else operation(17, "history-activation", "succeeded"))
            }
            "/api/v1/state" -> ok(state("controller-pak"))
            "/api/v1/sessions/$SESSION/history/item-id/activate" -> accepted(17)
            else -> error("unexpected ${request.url.encodedPath}")
        } }
        val phases = mutableListOf<BridgeHistoryActivationPhase>()
        val result = coordinator(executor).submit(
            "profile-a", BridgeHistoryActivationCommand.controllerPak(SESSION, item()),
        ) { phases += it.phase }
        assertEquals(BridgeHistoryActivationPhase.SUCCEEDED, result.phase)
        assertEquals(17, result.operationId)
        assertTrue(BridgeHistoryActivationPhase.ACCEPTED in phases)
        assertTrue(BridgeHistoryActivationPhase.RECONCILING in phases)
        assertEquals(1, executor.requests.count { it.method == "POST" })
        assertTrue(listOf("/api/v1/operation", "/api/v1/state", "/api/v1/sessions", "/api/v1/history")
            .all { path -> executor.requests.any { it.method == "GET" && it.url.encodedPath == path } })
        assertTrue(executor.requests.none { it.url.encodedPath == "/api/v1/firmware" })
    }

    @Test
    fun ambiguousOutcomeUsesGetOnlyAndNeverReplaysPostOnResume() = runTest {
        var operationReads = 0
        val executor = FakeExecutor { request -> when {
            request.method == "POST" -> throw IOException("ACK lost")
            request.url.encodedPath == "/api/v1/sessions" -> ok(sessions())
            request.url.encodedPath == "/api/v1/history" -> ok(history())
            request.url.encodedPath == "/api/v1/state" -> ok(state("controller-pak"))
            request.url.encodedPath == "/api/v1/operation" -> {
                operationReads++
                ok(if (operationReads == 1) operation(4, "accessory-selection", "succeeded")
                    else operation(18, "history-activation", "succeeded"))
            }
            else -> error("unexpected")
        } }
        val coordinator = coordinator(executor)
        val result = coordinator.submit(
            "profile-a", BridgeHistoryActivationCommand.controllerPak(SESSION, item()),
        )
        assertEquals(BridgeHistoryActivationPhase.SUCCEEDED, result.phase)
        assertEquals(18, result.operationId)
        assertEquals(1, executor.requests.count { it.method == "POST" })
        coordinator.resume(result)
        assertEquals(1, executor.requests.count { it.method == "POST" })
    }

    @Test
    fun terminalFailureWinsEvenWhenAccessoryChanged() = runTest {
        var operationReads = 0
        val executor = FakeExecutor { request -> when (request.url.encodedPath) {
            "/api/v1/sessions" -> ok(sessions())
            "/api/v1/history" -> ok(history())
            "/api/v1/operation" -> {
                operationReads++
                ok(if (operationReads == 1) operation(0, "none", "idle")
                    else operation(19, "history-activation", "failed"))
            }
            "/api/v1/state" -> ok(state("controller-pak"))
            else -> accepted(19)
        } }
        val result = coordinator(executor).submit(
            "profile-a", BridgeHistoryActivationCommand.controllerPak(SESSION, item()),
        )
        assertEquals(BridgeHistoryActivationPhase.FAILED, result.phase)
        assertEquals(BridgeHistoryActivationFailureKind.OPERATION_FAILED, result.failure?.kind)
    }

    @Test
    fun slowRunningOperationRemainsPendingUntilAuthoritativeSuccess() = runTest {
        var operationReads = 0
        val executor = FakeExecutor { request -> when (request.url.encodedPath) {
            "/api/v1/sessions" -> ok(sessions())
            "/api/v1/history" -> ok(transferHistory())
            "/api/v1/state" -> ok(state("transfer-pak"))
            "/api/v1/operation" -> {
                operationReads++
                ok(when {
                    operationReads == 1 -> operation(0, "none", "idle")
                    operationReads <= 62 -> operation(27, "history-activation", "running")
                    else -> operation(27, "history-activation", "succeeded")
                })
            }
            else -> accepted(27)
        } }
        val result = coordinator(executor, attempts = 65).submit(
            "profile-a",
            BridgeHistoryActivationCommand.transferPak(
                SESSION, item(name = "Pokemon Crystal.gbc"), true,
            )!!,
        )
        assertEquals(BridgeHistoryActivationPhase.SUCCEEDED, result.phase)
        assertTrue(operationReads >= 63)
        assertEquals(1, executor.requests.count { it.method == "POST" })
        assertEquals(1, executor.requests.count {
            it.method == "GET" && it.url.encodedPath == "/api/v1/state"
        })
        assertEquals(3, executor.requests.count {
            it.method == "GET" && it.url.encodedPath == "/api/v1/history"
        }) // Two pre-submit authority checks plus terminal reconciliation.
    }

    @Test
    fun boundedUnresolvedCanResumeToSuccessWithoutReplayingPost() = runTest {
        var terminal = false
        var operationReads = 0
        val executor = FakeExecutor { request -> when (request.url.encodedPath) {
            "/api/v1/sessions" -> ok(sessions())
            "/api/v1/history" -> ok(history())
            "/api/v1/state" -> ok(state("controller-pak"))
            "/api/v1/operation" -> {
                operationReads++
                ok(if (operationReads == 1) operation(0, "none", "idle") else operation(
                    29, "history-activation", if (terminal) "succeeded" else "running",
                ))
            }
            else -> accepted(29)
        } }
        val coordinator = coordinator(executor, attempts = 3)
        val unresolved = coordinator.submit(
            "profile-a", BridgeHistoryActivationCommand.controllerPak(SESSION, item()),
        )
        assertEquals(BridgeHistoryActivationPhase.UNCERTAIN, unresolved.phase)
        assertEquals(1, executor.requests.count { it.method == "POST" })

        terminal = true
        val reconciled = coordinator.resume(unresolved)
        assertEquals(BridgeHistoryActivationPhase.SUCCEEDED, reconciled.phase)
        assertEquals(1, executor.requests.count { it.method == "POST" })
    }

    @Test
    fun sessionInvalidationStopsInFlightReconciliationWithoutReplay() = runTest {
        var sessionReads = 0
        var operationReads = 0
        val executor = FakeExecutor { request -> when (request.url.encodedPath) {
            "/api/v1/sessions" -> {
                sessionReads++
                ok(sessions(if (sessionReads < 4) SESSION else "replacement-session"))
            }
            "/api/v1/history" -> ok(history())
            "/api/v1/operation" -> {
                operationReads++
                ok(if (operationReads == 1) operation(0, "none", "idle")
                    else operation(31, "history-activation", "running"))
            }
            else -> accepted(31)
        } }
        val result = coordinator(executor, attempts = 5).submit(
            "profile-a", BridgeHistoryActivationCommand.controllerPak(SESSION, item()),
        )
        assertEquals(BridgeHistoryActivationPhase.FAILED, result.phase)
        assertEquals(BridgeHistoryActivationFailureKind.SESSION_CHANGED, result.failure?.kind)
        assertEquals(1, executor.requests.count { it.method == "POST" })
        assertEquals(4, sessionReads)
    }

    @Test
    fun presentationIsProfileIsolatedAndDoesNotExposeOpaqueId() {
        val presentation = BridgeHistoryActivationPresentation(
            BridgeHistoryActivationPhase.ACCEPTED, "profile-a", SESSION,
            "opaque-secret-ish-id", BridgeHistoryCategory.CONTROLLER_PAK,
            "safe.mpk", 8,
        )
        assertTrue(shouldPublishHistoryActivation("profile-a", presentation))
        assertFalse(shouldPublishHistoryActivation("profile-b", presentation))
        assertFalse(presentation.toString().contains("opaque-secret-ish-id"))
    }

    private fun coordinator(
        executor: FakeExecutor,
        activeProfile: String = "profile-a",
        attempts: Int = 2,
    ) = BridgeHistoryActivationCoordinator(
        profiles = ActiveBridgeProfileSource {
            ActiveBridgeProfileResult.Success(profile(activeProfile))
        },
        clients = BridgeHistoryActivationClientFactory {
            BridgeApiResult.Success(client(executor, it.metadata.profileId))
        },
        reconciliationAttempts = attempts,
        reconciliationDelayMs = 0,
        waiter = BridgeHistoryActivationDelay { },
    )

    private fun client(executor: BridgeMutationHttpExecutor, id: String = "profile-a") =
        BridgeHistoryActivationClient("https://192.168.2.36:6470/".toHttpUrl(), executor, id)

    private fun profile(id: String) = BridgePairingProfile(
        BridgePairingMetadata(id, "NICE64", "https://192.168.2.36:6470", "v1",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "B".repeat(64), 1),
        BridgeBearerCredential.fromSecret("not-logged-token"),
    )

    private fun item(id: String = "item-id", name: String = "pak.mpk") =
        BridgeHistoryItemDto(id, name, "2026-09-17T00:00:00Z", true)

    private fun accepted(id: Long = 3) = BridgeHttpPayload(202, response(
        true, id, "history-activation",
    ))

    private fun response(
        accepted: Boolean,
        operationId: Long? = null,
        operationKind: String? = null,
        code: String? = null,
        detail: String? = null,
    ) = """{"accepted":$accepted,"operationId":${operationId ?: "null"},"operationKind":${operationKind?.let { "\"$it\"" } ?: "null"},"currentOperationId":null,"currentOperationKind":null,"code":${code?.let { "\"$it\"" } ?: "null"},"detail":${detail?.let { "\"${it.replace("\\", "\\\\")}\"" } ?: "null"},"confirmation":null}"""

    private fun ok(body: String) = BridgeHttpPayload(200, body)
    private fun sessions(id: String = SESSION) =
        """{"apiVersion":"v1","sessions":[{"sessionId":"$id","state":"connected","createdUtc":"2026-09-17T00:00:00Z","lastConnectedUtc":"2026-09-17T00:00:00Z","connectionKind":"wifi-tcp","device":{"identity":"NICE64","firmwarePersonality":"controller","accessory":"controller-pak"}}]}"""
    private fun history(available: Boolean = true) =
        """{"apiVersion":"v1","schemaVersion":1,"controllerPak":[{"itemId":"item-id","fileName":"pak.mpk","lastUsedUtc":"2026-09-17T00:00:00Z","available":$available}],"transferPak":[]}"""
    private fun transferHistory() =
        """{"apiVersion":"v1","schemaVersion":1,"controllerPak":[],"transferPak":[{"itemId":"item-id","fileName":"Pokemon Crystal.gbc","lastUsedUtc":"2026-09-17T00:00:00Z","available":true}]}"""
    private fun operation(id: Long, kind: String, status: String) =
        """{"operationId":$id,"kind":"$kind","status":"$status","startedUtc":null,"completedUtc":null,"cancellationCapability":"none","cancellationRequested":false,"domain":"none","failureCode":null,"safeDetail":null,"progress":null}"""
    private fun state(accessory: String) =
        """{"apiVersion":"v1","captureSequence":7,"capturedUtc":"2026-09-17T00:00:00Z","bridge":{"instanceId":"id","name":"NICE64"},"device":{"connected":true,"connectionState":"connected","connectionKind":"wifi-tcp","firmwarePersonality":"controller","deviceRole":"controller","deviceService":"network","firmwareTransition":{"phase":"idle","target":"unknown","failure":null},"activeAccessory":"$accessory","accessoryTransition":{"phase":"idle","target":"none","failure":null},"selectedNetworkPakTransport":"tcp","activeNetworkPakTransport":"none","effectiveCapabilities":[]},"controller":{"lastRequestedState":{"controllerType":"n64","buttons":0,"analogX":0,"analogY":0,"analogActive":false,"neutral":true},"lastDeliveredState":null,"deliverySuspended":false,"deliveryPending":false},"controllerPak":{"contentPresent":true,"hasAssociation":true,"associatedFileName":"pak.mpk","imageSize":32768,"lastSuccessfulOperation":"load"},"transferPak":{"contentPresent":false,"hasAssociatedRom":false,"hasAssociatedSave":false,"romFileName":null,"saveFileName":null,"installKind":"none","romLength":0,"expectedSaveLength":0,"metadata":null,"lastSuccessfulOperation":"none"},"persistence":{"kind":"controller-pak","active":true,"dirty":false,"flushInProgress":false,"expectedLength":32768,"dirtyGeneration":0,"lastSaveUtc":null,"associatedFileName":"pak.mpk"},"networkPakModule":{"activeModuleId":"diagnostics","activeModuleName":"Diagnostics","availableModules":[],"persistenceState":"confirmed","lastSelectionOutcome":"none"},"wifi":{"deviceStatusKnown":true,"configured":true,"connected":true,"state":"connected","mode":"station","link":"up","lastOperation":"none","lastOperationOutcome":"none"},"operation":${operation(0, "none", "idle")}}"""

    private fun body(request: Request): String = Buffer().use { buffer ->
        request.body?.writeTo(buffer); buffer.readUtf8()
    }

    private class FakeExecutor(
        private val handler: suspend (Request) -> BridgeHttpPayload,
    ) : BridgeMutationHttpExecutor {
        val requests = mutableListOf<Request>()
        override suspend fun execute(request: Request): BridgeHttpPayload {
            requests += request
            return handler(request)
        }
    }

    companion object { private const val SESSION = "session-1" }
}
