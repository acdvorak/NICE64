package com.melfegor.nice64.api

import com.melfegor.nice64.core.model.BridgeBearerCredential
import com.melfegor.nice64.core.model.BridgePairingMetadata
import com.melfegor.nice64.core.model.BridgePairingProfile
import java.io.IOException
import javax.net.ssl.SSLHandshakeException
import kotlinx.coroutines.test.runTest
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.Request
import com.melfegor.nice64.tls.BridgeBearerInterceptor
import com.melfegor.nice64.tls.BridgePinnedClientResult
import com.melfegor.nice64.tls.BridgePinnedHttpsClientFactory
import org.junit.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue

class BridgeReadOnlyApiClientTest {
    @Test fun noActiveProfileMakesNoRequest() = runTest {
        val executor = FakeExecutor()
        val repository = BridgeDashboardRepository(
            ActiveBridgeProfileSource { ActiveBridgeProfileResult.Unpaired },
            factory(executor),
        )
        assertEquals(BridgeConnectionPresentation.UNPAIRED, repository.refresh().connection)
        assertTrue(executor.requests.isEmpty())
    }

    @Test fun pairedProfileConstructsClientThroughConfiguredFactory() = runTest {
        val executor = FakeExecutor(defaultResponses())
        var profileSeen: String? = null
        val repository = BridgeDashboardRepository(profileSource("profile-a"), BridgeReadOnlyClientFactory {
            profileSeen = it.metadata.profileId
            clientResult(it, executor)
        })
        repository.refresh()
        assertEquals("profile-a", profileSeen)
    }

    @Test fun health2xxParses() = runTest {
        val result = client(FakeExecutor(mapOf("/api/v1/health" to ok(HEALTH)))).health()
        assertEquals("alive", assertIs<BridgeApiResult.Success<BridgeHealthDto>>(result).value.status)
    }

    @Test fun health401MapsAuthenticationFailure() = runTest {
        val failure = assertIs<BridgeApiResult.Failure>(client(FakeExecutor(mapOf("/api/v1/health" to BridgeHttpPayload(401, "")))).health())
        assertEquals(BridgeApiFailureKind.AUTHENTICATION, failure.failure.kind)
    }

    @Test fun healthTlsFailureMapsIdentityFailure() = runTest {
        val failure = assertIs<BridgeApiResult.Failure>(client(FakeExecutor(error = SSLHandshakeException("certificate"))).health())
        assertEquals(BridgeApiFailureKind.TLS_IDENTITY, failure.failure.kind)
    }

    @Test fun healthTransportFailureMapsUnreachable() = runTest {
        val failure = assertIs<BridgeApiResult.Failure>(client(FakeExecutor(error = IOException("offline"))).health())
        assertEquals(BridgeApiFailureKind.UNREACHABLE, failure.failure.kind)
    }

    @Test fun stateParsesExactDto() = runTest {
        val value = success<BridgeStateDto>(client(FakeExecutor(mapOf("/api/v1/state" to ok(STATE)))).state())
        assertEquals("controller", value.device.firmwarePersonality)
        assertEquals("controller-pak", value.device.activeAccessory)
        assertEquals(7, value.captureSequence)
    }

    @Test fun malformedStateFailsSafely() = runTest {
        val failure = assertIs<BridgeApiResult.Failure>(client(FakeExecutor(mapOf("/api/v1/state" to ok("{}")))).state())
        assertEquals(BridgeApiFailureKind.CONTRACT, failure.failure.kind)
    }

    @Test fun idleOperationMapsSafely() = runTest {
        val operation = success<BridgeOperationDto>(client(FakeExecutor(mapOf("/api/v1/operation" to ok(OPERATION_IDLE)))).operation())
        assertEquals(0, operation.operationId)
        assertEquals("idle", operation.status)
    }

    @Test fun presentOperationMapsSafely() = runTest {
        val operation = success<BridgeOperationDto>(client(FakeExecutor(mapOf("/api/v1/operation" to ok(OPERATION_RUNNING)))).operation())
        assertEquals(44, operation.operationId)
        assertEquals("running", operation.status)
    }

    @Test fun historyListParses() = runTest {
        val history = success<BridgeHistoryDto>(client(FakeExecutor(mapOf("/api/v1/history" to ok(HISTORY)))).history())
        assertEquals("game.mpk", history.controllerPak.single().fileName)
        assertEquals("game.gbc", history.transferPak.single().fileName)
    }

    @Test fun historyContractContainsNoRawPathProperty() = runTest {
        val item = success<BridgeHistoryDto>(client(FakeExecutor(mapOf("/api/v1/history" to ok(HISTORY)))).history()).controllerPak.single()
        assertFalse(item.toString().contains("C:\\"))
        assertTrue(item.javaClass.declaredFields.none { it.name.equals("path", ignoreCase = true) })
    }

    @Test fun sessionsEmptyParses() = runTest {
        assertTrue(success<BridgeSessionsDto>(client(FakeExecutor(mapOf("/api/v1/sessions" to ok(SESSIONS_EMPTY)))).sessions()).sessions.isEmpty())
    }

    @Test fun oneSessionParses() = runTest {
        assertEquals(SESSION_ID, success<BridgeSessionsDto>(client(FakeExecutor(mapOf("/api/v1/sessions" to ok(SESSIONS_ONE)))).sessions()).sessions.single().sessionId)
    }

    @Test fun sessionDetail404MapsNotFound() = runTest {
        val failure = assertIs<BridgeApiResult.Failure>(client(FakeExecutor(mapOf("/api/v1/sessions/$SESSION_ID" to BridgeHttpPayload(404, "")))).session(SESSION_ID))
        assertEquals(BridgeApiFailureKind.NOT_FOUND, failure.failure.kind)
    }

    @Test fun controllerReadOnlyResponseParses() = runTest {
        val controller = success<BridgeControllerPrimaryDto>(client(FakeExecutor(mapOf("/api/v1/sessions/$SESSION_ID/controller/primary" to ok(CONTROLLER)))).controller(SESSION_ID))
        assertEquals("bridge", controller.primary)
        assertEquals(null, controller.endpoint.transport)
    }

    @Test fun refreshIssuesOnlyGetRequests() = runTest {
        val executor = FakeExecutor(defaultResponses(oneSession = true))
        BridgeDashboardRepository(profileSource(), factory(executor)).refresh()
        assertEquals(7, executor.requests.size)
        assertTrue(executor.requests.all { it.method == "GET" })
    }

    @Test fun allRoutesAreExactlyApiV1ReadOnlyRoutes() = runTest {
        val executor = FakeExecutor(defaultResponses(oneSession = true))
        BridgeDashboardRepository(profileSource(), factory(executor)).refresh()
        assertEquals(
            setOf("/api/v1/health", "/api/v1/state", "/api/v1/operation", "/api/v1/history", "/api/v1/sessions", "/api/v1/sessions/$SESSION_ID", "/api/v1/sessions/$SESSION_ID/controller/primary"),
            executor.requests.map { it.url.encodedPath }.toSet(),
        )
    }

    @Test fun productionD8cClientDisablesRedirects() {
        val pinned = assertIs<BridgePinnedClientResult.Success>(BridgePinnedHttpsClientFactory.create(profile("profile-a"))).value
        assertFalse(pinned.httpClient.followRedirects)
        assertFalse(pinned.httpClient.followSslRedirects)
    }

    @Test fun productionD8cClientAddsBearerInterceptor() {
        val pinned = assertIs<BridgePinnedClientResult.Success>(BridgePinnedHttpsClientFactory.create(profile("profile-a"))).value
        assertTrue(pinned.httpClient.interceptors.any { it is BridgeBearerInterceptor })
    }

    @Test fun profileAFactoryCannotSilentlyUseProfileBCredentials() = runTest {
        var observed: BridgePairingProfile? = null
        val repository = BridgeDashboardRepository(profileSource("profile-a"), BridgeReadOnlyClientFactory {
            observed = it
            clientResult(it, FakeExecutor(defaultResponses()))
        })
        repository.refresh()
        assertEquals("profile-a", observed?.metadata?.profileId)
        assertFalse(observed.toString().contains("profile-b-token"))
    }

    @Test fun partialFailurePreservesSuccessfulSections() = runTest {
        val responses = defaultResponses().toMutableMap().apply {
            this["/api/v1/history"] = BridgeHttpPayload(500, "")
        }
        val state = BridgeDashboardRepository(profileSource(), factory(FakeExecutor(responses))).refresh()
        assertEquals("alive", state.health.value?.status)
        assertEquals("controller", state.state.value?.device?.firmwarePersonality)
        assertEquals(BridgeApiFailureKind.SERVER, state.history.failure?.kind)
    }

    @Test fun failedLaterRefreshRetainsPreviouslyValidSection() = runTest {
        val executor = FakeExecutor(defaultResponses())
        val repository = BridgeDashboardRepository(profileSource(), factory(executor))
        val first = repository.refresh()
        executor.responses["/api/v1/history"] = BridgeHttpPayload(500, "")
        val second = repository.refresh(first)
        assertEquals(first.history.value, second.history.value)
        assertEquals(BridgeApiFailureKind.SERVER, second.history.failure?.kind)
    }

    @Test fun bearerTokenNeverAppearsInDashboardOrFailure() = runTest {
        val state = BridgeDashboardRepository(profileSource(), factory(FakeExecutor(defaultResponses()))).refresh()
        assertFalse(state.toString().contains(TOKEN))
        assertTrue(BridgeApiFailure(BridgeApiFailureKind.AUTHENTICATION, "Bridge authentication failed.").toString().contains("AUTHENTICATION"))
        assertFalse(BridgeApiFailure(BridgeApiFailureKind.AUTHENTICATION, "Bridge authentication failed.").toString().contains(TOKEN))
    }

    @Test fun zeroSessionsProducesSafeNoSessionState() = runTest {
        val state = BridgeDashboardRepository(profileSource(), factory(FakeExecutor(defaultResponses()))).refresh()
        assertEquals(BridgeConnectionPresentation.NO_SESSION, state.connection)
        assertNull(state.currentSession.value)
        assertNull(state.controller.value)
    }

    @Test fun explicitRefreshStateIsRepresentableWithoutPolling() {
        val connecting = BridgeDashboardSnapshot(connection = BridgeConnectionPresentation.CONNECTING, refreshing = true)
        assertTrue(connecting.refreshing)
        assertEquals(BridgeConnectionPresentation.CONNECTING, connecting.connection)
    }

    private fun client(executor: BridgeHttpExecutor, profileId: String = "profile-a") =
        BridgeReadOnlyApiClient("https://192.168.2.81:6470/".toHttpUrl(), executor, profileId)

    private fun factory(executor: BridgeHttpExecutor) = BridgeReadOnlyClientFactory { clientResult(it, executor) }

    private fun clientResult(profile: BridgePairingProfile, executor: BridgeHttpExecutor): BridgeApiResult<BridgeReadOnlyApiClient> =
        BridgeApiResult.Success(client(executor, profile.metadata.profileId))

    private fun profileSource(id: String = "profile-a") = ActiveBridgeProfileSource {
        ActiveBridgeProfileResult.Success(profile(id))
    }

    private fun profile(id: String) = BridgePairingProfile(
        BridgePairingMetadata(id, "NICE64 Bridge", "https://192.168.2.81:6470", "v1", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "B".repeat(64), 1),
        BridgeBearerCredential.fromSecret(if (id == "profile-a") TOKEN else "profile-b-token"),
    )

    private inline fun <reified T> success(result: BridgeApiResult<T>): T = assertIs<BridgeApiResult.Success<T>>(result).value

    private fun defaultResponses(oneSession: Boolean = false) = mutableMapOf(
        "/api/v1/health" to ok(HEALTH),
        "/api/v1/state" to ok(STATE),
        "/api/v1/operation" to ok(OPERATION_IDLE),
        "/api/v1/history" to ok(HISTORY),
        "/api/v1/sessions" to ok(if (oneSession) SESSIONS_ONE else SESSIONS_EMPTY),
        "/api/v1/sessions/$SESSION_ID" to ok(SESSION),
        "/api/v1/sessions/$SESSION_ID/controller/primary" to ok(CONTROLLER),
    )

    private fun ok(body: String) = BridgeHttpPayload(200, body)

    private class FakeExecutor(
        responses: Map<String, BridgeHttpPayload> = emptyMap(),
        private val error: Exception? = null,
    ) : BridgeHttpExecutor {
        val responses = responses.toMutableMap()
        val requests = mutableListOf<Request>()
        override suspend fun execute(request: Request): BridgeHttpPayload {
            requests += request
            error?.let { throw it }
            return responses[request.url.encodedPath] ?: BridgeHttpPayload(404, "")
        }
    }

    companion object {
        private const val TOKEN = "top-secret-token"
        private const val SESSION_ID = "11111111-1111-1111-1111-111111111111"
        private const val HEALTH = """{"apiVersion":"v1","status":"alive"}"""
        private const val OPERATION_IDLE = """{"operationId":0,"kind":"none","status":"idle","startedUtc":null,"completedUtc":null,"cancellationCapability":"none","cancellationRequested":false,"domain":"none","failureCode":null,"safeDetail":null,"progress":null}"""
        private const val OPERATION_RUNNING = """{"operationId":44,"kind":"firmware-mode-switch","status":"running","startedUtc":"2026-09-10T00:00:00Z","completedUtc":null,"cancellationCapability":"none","cancellationRequested":false,"domain":"firmware","failureCode":null,"safeDetail":null,"progress":null}"""
        private const val STATE = """{"apiVersion":"v1","captureSequence":7,"capturedUtc":"2026-09-10T00:00:00Z","bridge":{"instanceId":"22222222-2222-2222-2222-222222222222","name":"NICE64"},"device":{"connected":true,"connectionState":"connected","connectionKind":"wifi-tcp","firmwarePersonality":"controller","deviceRole":"controller","deviceService":"ready","firmwareTransition":{"phase":"idle","target":"unknown","failure":null},"activeAccessory":"controller-pak","accessoryTransition":{"phase":"idle","target":"none","failure":null},"selectedNetworkPakTransport":"tcp","activeNetworkPakTransport":"none","effectiveCapabilities":[]},"controller":{"lastRequestedState":{"controllerType":"n64","buttons":0,"analogX":0,"analogY":0,"analogActive":false,"neutral":true},"lastDeliveredState":null,"deliverySuspended":false,"deliveryPending":false},"controllerPak":{"contentPresent":false,"hasAssociation":false,"associatedFileName":null,"imageSize":0,"lastSuccessfulOperation":"none"},"transferPak":{"contentPresent":false,"hasAssociatedRom":false,"hasAssociatedSave":false,"romFileName":null,"saveFileName":null,"installKind":"none","romLength":0,"expectedSaveLength":0,"metadata":null,"lastSuccessfulOperation":"none"},"persistence":{"kind":"none","active":false,"dirty":false,"flushInProgress":false,"expectedLength":0,"dirtyGeneration":0,"lastSaveUtc":null,"associatedFileName":null},"networkPakModule":{"activeModuleId":"","activeModuleName":"","availableModules":[],"persistenceState":"none","lastSelectionOutcome":"none"},"wifi":{"deviceStatusKnown":true,"configured":true,"connected":true,"state":"connected","mode":"station","link":"up","lastOperation":"none","lastOperationOutcome":"none"},"operation":$OPERATION_IDLE}"""
        private const val HISTORY = """{"apiVersion":"v1","schemaVersion":1,"controllerPak":[{"itemId":"opaque-cpak","fileName":"game.mpk","lastUsedUtc":"2026-09-09T00:00:00Z","available":true}],"transferPak":[{"itemId":"opaque-tpak","fileName":"game.gbc","lastUsedUtc":"2026-09-08T00:00:00Z","available":true}]}"""
        private const val SESSION = """{"sessionId":"$SESSION_ID","state":"connected","createdUtc":"2026-09-10T00:00:00Z","lastConnectedUtc":"2026-09-10T00:00:01Z","connectionKind":"wifi-tcp","device":{"identity":"NICE64","firmwarePersonality":"controller","accessory":"controller-pak"}}"""
        private const val SESSIONS_EMPTY = """{"apiVersion":"v1","sessions":[]}"""
        private const val SESSIONS_ONE = """{"apiVersion":"v1","sessions":[$SESSION]}"""
        private const val CONTROLLER = """{"apiVersion":"v1","sessionId":"$SESSION_ID","primary":"bridge","endpoint":{"available":false,"unavailableReason":"no-session","address":null,"port":null,"transport":null,"protocol":null,"controllerType":null}}"""
    }
}
