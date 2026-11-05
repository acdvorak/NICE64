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
import kotlinx.serialization.json.Json
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
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class BridgeSemanticMutationsTest {
    @Test
    fun keyboardAndMouseRequireVerifiedStandaloneState() {
        val keyboard = BridgeMutationCommand.Firmware(BridgeFirmwareTarget.KEYBOARD)
        val merelyReported = Json.decodeFromString<BridgeStateDto>(state(firmware = "keyboard"))
        assertFalse(keyboard.isApplied(merelyReported))
        val verified = Json.decodeFromString<BridgeStateDto>(state(
            firmware = "keyboard",
            role = "standalone",
            service = "keyboard-mouse",
            transitionPhase = "completed",
            transitionTarget = "keyboard",
        ))
        assertTrue(keyboard.isApplied(verified))
    }

    @Test
    fun productionMutationExecutorMovesPostAndReconciliationGetOffCallerThread() = runTest {
        val executionThreads = mutableListOf<Pair<String, String>>()
        val httpClient = OkHttpClient.Builder().addInterceptor { chain ->
            synchronized(executionThreads) {
                executionThreads += chain.request().method to Thread.currentThread().name
            }
            Response.Builder()
                .request(chain.request())
                .protocol(Protocol.HTTP_1_1)
                .code(200)
                .message("OK")
                .body("{}".toResponseBody())
                .build()
        }.build()
        val executor = OkHttpMutationExecutor(BridgePinnedHttpsClient(
            "profile-a", "https://192.168.2.36:6470", httpClient,
        ))
        val callerExecutor = Executors.newSingleThreadExecutor { runnable ->
            Thread(runnable, "D8F-Test-Main")
        }
        val callerDispatcher = callerExecutor.asCoroutineDispatcher()
        try {
            var callerThread = ""
            withContext(callerDispatcher) {
                callerThread = Thread.currentThread().name
                executor.execute(Request.Builder()
                    .url("https://192.168.2.36:6470/api/v1/accessory")
                    .post("{}".toRequestBody())
                    .build())
                executor.execute(Request.Builder()
                    .url("https://192.168.2.36:6470/api/v1/operation")
                    .get()
                    .build())
            }

            assertEquals("D8F-Test-Main", callerThread)
            assertEquals(listOf("POST", "GET"), executionThreads.map { it.first })
            assertTrue(executionThreads.all { (_, thread) -> thread != callerThread })
        } finally {
            callerDispatcher.close()
            callerExecutor.shutdownNow()
        }
    }

    @Test
    fun productionMutationFactoryUsesFrozenPinnedProfileClient() {
        val created = PinnedBridgeSemanticMutationClientFactory.create(profile("profile-a"))
        val client = assertIs<BridgeApiResult.Success<BridgeSemanticMutationClient>>(created).value
        assertEquals("profile-a", client.profileId)
    }

    @Test
    fun fourSemanticCommandsUseExactPostRoutesAndBodies() = runTest {
        val executor = FakeExecutor { request -> accepted(request) }
        val client = client(executor)
        assertIs<BridgeMutationSubmission.Accepted>(client.submit(
            BridgeMutationCommand.Firmware(BridgeFirmwareTarget.NETWORK),
        ))
        assertIs<BridgeMutationSubmission.Accepted>(client.submit(
            BridgeMutationCommand.Accessory(BridgeAccessoryTarget.CONTROLLER_PAK),
        ))
        val module = assertNotNull(BridgeMutationCommand.Module.fromAuthoritative(
            BridgeModuleInfoDto("diagnostics", "Diagnostics"),
        ))
        assertIs<BridgeMutationSubmission.Accepted>(client.submit(module))
        assertIs<BridgeMutationSubmission.Accepted>(client.submit(
            BridgeMutationCommand.Carrier(BridgeCarrierTarget.RELIABLE_UDP_QUICK),
        ))

        assertEquals(
            listOf(
                "/api/v1/firmware",
                "/api/v1/accessory",
                "/api/v1/network-pak/module",
                "/api/v1/network-pak/carrier",
            ),
            executor.requests.map { it.url.encodedPath },
        )
        assertTrue(executor.requests.all { it.method == "POST" })
        assertEquals(
            listOf(
                """{"personality":"network"}""",
                """{"accessory":"controller-pak"}""",
                """{"moduleId":"diagnostics"}""",
                """{"carrier":"reliable-udp-quick"}""",
            ),
            executor.requests.map(::body),
        )
    }

    @Test
    fun acceptedResponseRetainsOperationIdWithoutMutatingAuthority() = runTest {
        val result = client(FakeExecutor { accepted(it, 77) }).submit(
            BridgeMutationCommand.Firmware(BridgeFirmwareTarget.NETWORK),
        )
        val accepted = assertIs<BridgeMutationSubmission.Accepted>(result)
        assertEquals(77, accepted.operationId)
        assertEquals("firmware-mode-switch", accepted.operationKind)
    }

    @Test
    fun deliberateStatusMappingIsSafe() = runTest {
        suspend fun result(code: Int): BridgeMutationSubmission = client(FakeExecutor {
            BridgeHttpPayload(code, if (code == 409) BUSY else REJECTED)
        }).submit(BridgeMutationCommand.Accessory(BridgeAccessoryTarget.NONE))

        assertEquals(BridgeMutationFailureKind.INVALID_REQUEST,
            assertIs<BridgeMutationSubmission.Rejected>(result(400)).failure.kind)
        assertEquals(BridgeMutationFailureKind.AUTHENTICATION,
            assertIs<BridgeMutationSubmission.Rejected>(result(401)).failure.kind)
        assertEquals(BridgeMutationFailureKind.FORBIDDEN,
            assertIs<BridgeMutationSubmission.Rejected>(result(403)).failure.kind)
        assertEquals(BridgeMutationFailureKind.NOT_FOUND,
            assertIs<BridgeMutationSubmission.Rejected>(result(404)).failure.kind)
        assertIs<BridgeMutationSubmission.Busy>(result(409))
        assertEquals(BridgeMutationFailureKind.SERVER,
            assertIs<BridgeMutationSubmission.Rejected>(result(503)).failure.kind)
        assertEquals(BridgeMutationFailureKind.API_RESPONSE,
            assertIs<BridgeMutationSubmission.Rejected>(result(418)).failure.kind)
    }

    @Test
    fun malformedAcceptedResponseFailsContract() = runTest {
        val result = client(FakeExecutor { BridgeHttpPayload(202, "{}") }).submit(
            BridgeMutationCommand.Firmware(BridgeFirmwareTarget.CONTROLLER),
        )
        assertEquals(BridgeMutationFailureKind.CONTRACT,
            assertIs<BridgeMutationSubmission.Rejected>(result).failure.kind)
    }

    @Test
    fun timeoutOrSocketFailureNeverRetriesPost() = runTest {
        val timeout = FakeExecutor { throw IOException("timeout after send secret-token") }
        val timeoutResult = client(timeout).submit(
            BridgeMutationCommand.Firmware(BridgeFirmwareTarget.CONTROLLER),
        )
        assertIs<BridgeMutationSubmission.Uncertain>(timeoutResult)
        assertEquals(1, timeout.requests.count { it.method == "POST" })
        assertFalse(timeoutResult.toString().contains("secret-token"))

        val disconnect = FakeExecutor { throw IOException("socket disconnected") }
        assertIs<BridgeMutationSubmission.Uncertain>(client(disconnect).submit(
            BridgeMutationCommand.Accessory(BridgeAccessoryTarget.NONE),
        ))
        assertEquals(1, disconnect.requests.count { it.method == "POST" })
    }

    @Test
    fun cancellationAfterPossibleSendNeverRetriesAndUsesGetOnlyReconciliation() = runTest {
        val executor = FakeExecutor { request -> when (request.method) {
            "POST" -> throw CancellationException("cancelled after possible send")
            else -> if (request.url.encodedPath.endsWith("operation"))
                ok(operation(0, "none", "idle")) else ok(state(accessory = "none"))
        } }

        val result = coordinator(executor, attempts = 2).submit(
            "profile-a",
            BridgeMutationCommand.Accessory(BridgeAccessoryTarget.RUMBLE_PAK),
        )

        assertEquals(BridgeMutationPhase.UNCERTAIN, result.phase)
        assertEquals(1, executor.requests.count { it.method == "POST" })
        assertTrue(executor.requests.count { it.method == "GET" } >= 3)
    }

    @Test
    fun acceptedOperationReconcilesOnlyAfterTerminalAuthorityMatches() = runTest {
        var operationReads = 0
        var stateReads = 0
        val executor = FakeExecutor { request -> when (request.url.encodedPath) {
            "/api/v1/firmware" -> accepted(request, 44)
            "/api/v1/operation" -> {
                operationReads++
                when (operationReads) {
                    1 -> ok(operation(0, "none", "idle"))
                    2 -> ok(operation(44, "firmware-mode-switch", "running"))
                    else -> ok(operation(44, "firmware-mode-switch", "succeeded"))
                }
            }
            "/api/v1/state" -> {
                stateReads++
                ok(state(firmware = if (stateReads == 1) "controller" else "network"))
            }
            else -> BridgeHttpPayload(404, "")
        } }
        val phases = mutableListOf<BridgeMutationPhase>()
        val result = coordinator(executor).submit(
            "profile-a",
            BridgeMutationCommand.Firmware(BridgeFirmwareTarget.NETWORK),
        ) { phases += it.phase }

        assertEquals(BridgeMutationPhase.SUCCEEDED, result.phase)
        assertEquals(44, result.operationId)
        assertTrue(BridgeMutationPhase.ACCEPTED in phases)
        assertTrue(BridgeMutationPhase.RECONCILING in phases)
        assertEquals(1, executor.requests.count { it.method == "POST" })
        assertTrue(stateReads >= 2)
    }

    @Test
    fun acceptedSuccessWithoutMatchingStateRemainsUncertain() = runTest {
        val executor = FakeExecutor { request -> when (request.url.encodedPath) {
            "/api/v1/accessory" -> accepted(request, 45)
            "/api/v1/operation" -> if (request.method == "GET")
                ok(operation(45, "accessory-selection", "succeeded")) else error("unexpected")
            "/api/v1/state" -> ok(state(accessory = "none"))
            else -> BridgeHttpPayload(404, "")
        } }
        val result = coordinator(executor, attempts = 2).submit(
            "profile-a",
            BridgeMutationCommand.Accessory(BridgeAccessoryTarget.CONTROLLER_PAK),
        )
        assertEquals(BridgeMutationPhase.UNCERTAIN, result.phase)
        assertEquals(1, executor.requests.count { it.method == "POST" })
    }

    @Test
    fun ambiguousPostReconcilesAlreadyAppliedStateWithoutReplay() = runTest {
        var postCount = 0
        val executor = FakeExecutor { request -> when (request.url.encodedPath) {
            "/api/v1/network-pak/carrier" -> {
                postCount++
                throw IOException("response lost")
            }
            "/api/v1/operation" -> ok(operation(0, "none", "idle"))
            "/api/v1/state" -> ok(state(carrier = "reliable-udp-quick"))
            else -> BridgeHttpPayload(404, "")
        } }
        val phases = mutableListOf<BridgeMutationPhase>()
        val result = coordinator(executor).submit(
            "profile-a",
            BridgeMutationCommand.Carrier(BridgeCarrierTarget.RELIABLE_UDP_QUICK),
        ) { phases += it.phase }
        assertEquals(BridgeMutationPhase.SUCCEEDED, result.phase)
        assertTrue(BridgeMutationPhase.UNCERTAIN in phases)
        assertEquals(1, postCount)
    }

    @Test
    fun ambiguousNotAppliedRemainsUncertainUntilDeliberateAction() = runTest {
        val executor = FakeExecutor { request -> when (request.method) {
            "POST" -> throw IOException("response lost")
            else -> if (request.url.encodedPath.endsWith("operation"))
                ok(operation(0, "none", "idle")) else ok(state(accessory = "none"))
        } }
        val command = BridgeMutationCommand.Accessory(BridgeAccessoryTarget.RUMBLE_PAK)
        val result = coordinator(executor, attempts = 2).submit("profile-a", command)
        assertEquals(BridgeMutationPhase.UNCERTAIN, result.phase)
        assertEquals(1, executor.requests.count { it.method == "POST" })

        coordinator(executor, attempts = 1).resume(result)
        assertEquals(1, executor.requests.count { it.method == "POST" })
    }

    @Test
    fun failedOperationRemainsFailedEvenIfStateChangedPartially() = runTest {
        var reads = 0
        val executor = FakeExecutor { request -> when (request.url.encodedPath) {
            "/api/v1/firmware" -> accepted(request, 50)
            "/api/v1/operation" -> {
                reads++
                ok(if (reads == 1) operation(0, "none", "idle")
                    else operation(50, "firmware-mode-switch", "failed"))
            }
            "/api/v1/state" -> ok(state(firmware = "network"))
            else -> BridgeHttpPayload(404, "")
        } }
        val result = coordinator(executor).submit(
            "profile-a", BridgeMutationCommand.Firmware(BridgeFirmwareTarget.NETWORK),
        )
        assertEquals(BridgeMutationPhase.FAILED, result.phase)
        assertEquals(BridgeMutationFailureKind.OPERATION_FAILED, result.failure?.kind)
    }

    @Test
    fun activeBridgeOperationBlocksPostWithoutLocalBacklog() = runTest {
        val executor = FakeExecutor { request ->
            if (request.url.encodedPath.endsWith("operation"))
                ok(operation(91, "transfer-pak-save", "running"))
            else error("POST must not occur")
        }
        val result = coordinator(executor).submit(
            "profile-a", BridgeMutationCommand.Accessory(BridgeAccessoryTarget.NONE),
        )
        assertEquals(BridgeMutationFailureKind.BUSY, result.failure?.kind)
        assertTrue(executor.requests.none { it.method == "POST" })
    }

    @Test
    fun profileIsolationRejectsStaleOrMismatchedProfileBeforePost() = runTest {
        val executor = FakeExecutor { accepted(it) }
        val coordinator = coordinator(executor, activeProfileId = "profile-b")
        val result = coordinator.submit(
            "profile-a", BridgeMutationCommand.Firmware(BridgeFirmwareTarget.NETWORK),
        )
        assertEquals(BridgeMutationFailureKind.PROFILE_CHANGED, result.failure?.kind)
        assertTrue(executor.requests.isEmpty())
        assertFalse(shouldPublishMutation("profile-b", BridgeMutationPresentation(
            BridgeMutationPhase.SUCCEEDED, "profile-a", 1,
            BridgeMutationKind.FIRMWARE, "network", "Network",
        )))
        assertTrue(shouldPublishMutation("profile-a", BridgeMutationPresentation(
            BridgeMutationPhase.SUCCEEDED, "profile-a", 1,
            BridgeMutationKind.FIRMWARE, "network", "Network",
        )))
    }

    @Test
    fun semanticTypesExcludeRawPathsAndUnauthorizedMethods() {
        assertNull(BridgeMutationCommand.Module.fromAuthoritative(
            BridgeModuleInfoDto("C:\\modules\\unsafe.exe", "Unsafe"),
        ))
        assertNull(BridgeMutationCommand.Module.fromAuthoritative(
            BridgeModuleInfoDto("../unsafe", "Unsafe"),
        ))
        assertEquals(setOf("controller", "diagnostics", "network", "keyboard", "mouse"),
            BridgeFirmwareTarget.entries.map { it.symbol }.toSet())
        assertEquals(setOf("tcp", "reliable-udp-quick"),
            BridgeCarrierTarget.entries.map { it.symbol }.toSet())
        assertEquals(4, BridgeMutationKind.entries.size)
        assertTrue(BridgeMutationKind.entries.all { it.route.startsWith("/api/v1/") })
        val source = BridgeSemanticMutationsTest::class.java.classLoader
            ?.getResource("missing")
        assertNull(source)
    }

    @Test
    fun presentationContainsNoBearerOrRawPayload() {
        val presentation = BridgeMutationPresentation(
            BridgeMutationPhase.ACCEPTED, "profile-a", 9,
            BridgeMutationKind.FIRMWARE, "network", "Network",
        )
        assertFalse(presentation.toString().contains(TOKEN))
        assertFalse(BridgeMutationFailure(
            BridgeMutationFailureKind.AUTHENTICATION,
            "Bridge authentication failed.",
        ).toString().contains(TOKEN))
        assertTrue(presentation.toString().contains("operationId=9"))
    }

    private fun coordinator(
        executor: FakeExecutor,
        attempts: Int = 3,
        activeProfileId: String = "profile-a",
    ): BridgeSemanticMutationCoordinator {
        val profile = profile(activeProfileId)
        return BridgeSemanticMutationCoordinator(
            profiles = ActiveBridgeProfileSource { ActiveBridgeProfileResult.Success(profile) },
            clients = BridgeSemanticMutationClientFactory {
                BridgeApiResult.Success(client(executor, it.metadata.profileId))
            },
            reconciliationAttempts = attempts,
            reconciliationDelayMs = 0,
            waiter = BridgeMutationDelay { },
        )
    }

    private fun client(executor: BridgeMutationHttpExecutor, profileId: String = "profile-a") =
        BridgeSemanticMutationClient(
            "https://192.168.2.36:6470/".toHttpUrl(), executor, profileId,
        )

    private fun profile(id: String) = BridgePairingProfile(
        BridgePairingMetadata(
            id, "NICE64 Bridge", "https://192.168.2.36:6470", "v1",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "B".repeat(64), 1,
        ),
        BridgeBearerCredential.fromSecret(if (id == "profile-a") TOKEN else "profile-b-token"),
    )

    private fun accepted(request: Request, operationId: Long = 1): BridgeHttpPayload {
        val kind = BridgeMutationKind.entries.single { it.route == request.url.encodedPath }
        return BridgeHttpPayload(
            202,
            """{"accepted":true,"operationId":$operationId,"operationKind":"${kind.operationKind}"}""",
        )
    }

    private fun ok(body: String) = BridgeHttpPayload(200, body)

    private fun operation(id: Long, kind: String, status: String) =
        """{"operationId":$id,"kind":"$kind","status":"$status","startedUtc":null,"completedUtc":null,"cancellationCapability":"none","cancellationRequested":false,"domain":"none","failureCode":null,"safeDetail":null,"progress":null}"""

    private fun state(
        firmware: String = "controller",
        accessory: String = "none",
        module: String = "diagnostics",
        carrier: String = "tcp",
        role: String = "controller",
        service: String = "network",
        transitionPhase: String = "idle",
        transitionTarget: String = "unknown",
    ) = """{"apiVersion":"v1","captureSequence":7,"capturedUtc":"2026-09-12T00:00:00Z","bridge":{"instanceId":"22222222-2222-2222-2222-222222222222","name":"NICE64"},"device":{"connected":true,"connectionState":"connected","connectionKind":"wifi-tcp","firmwarePersonality":"$firmware","deviceRole":"$role","deviceService":"$service","firmwareTransition":{"phase":"$transitionPhase","target":"$transitionTarget","failure":null},"activeAccessory":"$accessory","accessoryTransition":{"phase":"idle","target":"none","failure":null},"selectedNetworkPakTransport":"$carrier","activeNetworkPakTransport":"none","effectiveCapabilities":[]},"controller":{"lastRequestedState":{"controllerType":"n64","buttons":0,"analogX":0,"analogY":0,"analogActive":false,"neutral":true},"lastDeliveredState":null,"deliverySuspended":false,"deliveryPending":false},"controllerPak":{"contentPresent":false,"hasAssociation":false,"associatedFileName":null,"imageSize":0,"lastSuccessfulOperation":"none"},"transferPak":{"contentPresent":false,"hasAssociatedRom":false,"hasAssociatedSave":false,"romFileName":null,"saveFileName":null,"installKind":"none","romLength":0,"expectedSaveLength":0,"metadata":null,"lastSuccessfulOperation":"none"},"persistence":{"kind":"none","active":false,"dirty":false,"flushInProgress":false,"expectedLength":0,"dirtyGeneration":0,"lastSaveUtc":null,"associatedFileName":null},"networkPakModule":{"activeModuleId":"$module","activeModuleName":"Diagnostics","availableModules":[],"persistenceState":"confirmed","lastSelectionOutcome":"requested-module-selected"},"wifi":{"deviceStatusKnown":true,"configured":true,"connected":true,"state":"connected","mode":"station","link":"up","lastOperation":"none","lastOperationOutcome":"none"},"operation":${operation(0, "none", "idle")}}"""

    private fun body(request: Request): String = Buffer().use { buffer ->
        request.body?.writeTo(buffer)
        buffer.readUtf8()
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

    companion object {
        private const val TOKEN = "top-secret-bearer-token"
        private const val REJECTED = """{"code":"rejected","detail":"safe"}"""
        private const val BUSY = """{"code":"operation-busy","currentOperationId":7,"currentOperationKind":"firmware-mode-switch"}"""
    }
}
