package com.melfegor.nice64.api

import com.melfegor.nice64.core.model.BridgeBearerCredential
import com.melfegor.nice64.core.model.BridgePairingMetadata
import com.melfegor.nice64.core.model.BridgePairingProfile
import kotlinx.coroutines.test.runTest
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.Request
import okio.Buffer
import org.junit.Test
import kotlin.test.*

class BridgeControllerPrimaryTest {
    @Test fun exactPutSelectsMobileOnceWithoutHeartbeat() = runTest {
        val executor = FakeExecutor { ok(primary("nice-mobile")) }
        val result = coordinator(executor).selectMobile("profile-a", SESSION)
        assertEquals(BridgeControllerPrimaryPhase.MOBILE_PRIMARY, result.phase)
        assertTrue(result.mobilePrimary)
        assertEquals(1, executor.requests.size)
        assertEquals("PUT", executor.requests.single().method)
        assertEquals("/api/v1/sessions/$SESSION/controller/primary",
            executor.requests.single().url.encodedPath)
        assertEquals("""{"primary":"nice-mobile"}""", body(executor.requests.single()))
    }

    @Test fun bridgeSelectionIsExplicitAndHasNoRenewal() = runTest {
        val executor = FakeExecutor { ok(primary("bridge", false)) }
        val result = coordinator(executor).selectBridge(mobile())
        assertEquals(BridgeControllerPrimaryPhase.BRIDGE_PRIMARY, result.phase)
        assertFalse(result.mobilePrimary)
        assertEquals("""{"primary":"bridge"}""", body(executor.requests.single()))
        assertFalse(executor.requests.any { "renew" in it.url.encodedPath })
    }

    @Test fun getReconcilesAuthoritativeMobilePrimaryAndEndpoint() = runTest {
        val executor = FakeExecutor { ok(primary("nice-mobile")) }
        val result = coordinator(executor).refresh("profile-a", SESSION)
        assertTrue(result.mobilePrimary)
        assertEquals("192.168.2.81", result.endpoint?.address)
    }

    @Test fun profileIsolationFailsClosedBeforeRequest() = runTest {
        val executor = FakeExecutor { error("must not send") }
        val result = coordinator(executor, "profile-b").selectMobile("profile-a", SESSION)
        assertEquals(BridgeControllerPrimaryPhase.ERROR, result.phase)
        assertEquals(BridgeControllerPrimaryFailureKind.PROFILE_CHANGED, result.failure?.kind)
        assertTrue(executor.requests.isEmpty())
    }

    @Test fun publicationIsProfileScoped() {
        assertTrue(shouldPublishControllerPrimary("profile-a", mobile()))
        assertFalse(shouldPublishControllerPrimary("profile-b", mobile()))
    }

    @Test fun authoritativeMobileTransitionStartsPreviouslyStoppedSender() {
        assertTrue(shouldStartReconciledControllerSender(
            BridgeControllerPrimaryPresentation(),
            primaryDto("nice-mobile"),
            BridgeControllerDataPlanePresentation(),
        ))
    }

    @Test fun periodicMobileRefreshDoesNotRestartSender() {
        assertFalse(shouldStartReconciledControllerSender(
            mobile(),
            primaryDto("nice-mobile"),
            BridgeControllerDataPlanePresentation(BridgeControllerDataPlanePhase.ACTIVE),
        ))
    }

    @Test fun authoritativeMobileRefreshRestartsStoppedMobileSender() {
        assertTrue(shouldStartReconciledControllerSender(
            mobile(),
            primaryDto("nice-mobile"),
            BridgeControllerDataPlanePresentation(BridgeControllerDataPlanePhase.STOPPED),
        ))
    }

    @Test fun repeatedForegroundRefreshDoesNotRestartStartingSender() {
        assertFalse(shouldStartReconciledControllerSender(
            mobile(),
            primaryDto("nice-mobile"),
            BridgeControllerDataPlanePresentation(BridgeControllerDataPlanePhase.STARTING),
        ))
    }

    @Test fun switchingPrimaryNeverStartsReconciledSender() {
        assertFalse(shouldStartReconciledControllerSender(
            mobile().copy(phase = BridgeControllerPrimaryPhase.SWITCHING_TO_MOBILE),
            primaryDto("nice-mobile"),
            BridgeControllerDataPlanePresentation(BridgeControllerDataPlanePhase.STOPPED),
        ))
    }

    @Test fun failedSenderIsNotAutomaticallyRetriedByRefresh() {
        assertFalse(shouldStartReconciledControllerSender(
            mobile(),
            primaryDto("nice-mobile"),
            BridgeControllerDataPlanePresentation(BridgeControllerDataPlanePhase.FAILED),
        ))
    }

    @Test fun boundedForegroundReconciliationMayReplaceOneFailedSenderGeneration() {
        assertTrue(shouldStartReconciledControllerSender(
            mobile(),
            primaryDto("nice-mobile"),
            BridgeControllerDataPlanePresentation(BridgeControllerDataPlanePhase.FAILED),
            allowFailedSenderReplacement = true,
        ))
    }

    @Test fun foregroundCannotReplaceFailedSenderWithoutAuthoritativeEndpoint() {
        assertFalse(shouldStartReconciledControllerSender(
            mobile(),
            primaryDto("nice-mobile", false),
            BridgeControllerDataPlanePresentation(BridgeControllerDataPlanePhase.FAILED),
            allowFailedSenderReplacement = true,
        ))
    }

    @Test fun bridgeAuthorityNeverStartsMobileSender() {
        assertFalse(shouldStartReconciledControllerSender(
            BridgeControllerPrimaryPresentation(),
            primaryDto("bridge", false),
            BridgeControllerDataPlanePresentation(),
        ))
    }

    @Test fun unavailableAuthoritativeEndpointNeverStartsMobileSender() {
        assertFalse(shouldStartReconciledControllerSender(
            mobile(),
            primaryDto("nice-mobile", false),
            BridgeControllerDataPlanePresentation(),
        ))
    }

    @Test fun malformedResponseFailsClosed() = runTest {
        assertIs<BridgeControllerPrimaryHttpResult.Rejected>(
            client(FakeExecutor { ok("{}") }).set(SESSION, "nice-mobile"))
    }

    @Test fun endpointResolutionFailureRemainsTypedAndCannotStartSender() = runTest {
        val executor = FakeExecutor {
            BridgeHttpPayload(409, """{"code":"endpoint-unavailable","detail":"The controller endpoint is currently unavailable."}""")
        }
        val result = coordinator(executor).selectMobile("profile-a", SESSION)
        assertEquals(BridgeControllerPrimaryPhase.ERROR, result.phase)
        assertEquals(BridgeControllerPrimaryFailureKind.ENDPOINT_UNAVAILABLE, result.failure?.kind)
        assertEquals("The controller endpoint is currently unavailable.", result.failure?.safeMessage)
        assertFalse(result.mobilePrimary)
        assertEquals(1, executor.requests.size)
    }

    private fun coordinator(executor: FakeExecutor, active: String = "profile-a") =
        BridgeControllerPrimaryCoordinator(
            ActiveBridgeProfileSource { ActiveBridgeProfileResult.Success(profile(active)) },
            BridgeControllerPrimaryClientFactory {
                BridgeApiResult.Success(client(executor, it.metadata.profileId))
            })
    private fun client(executor: BridgeMutationHttpExecutor, id: String = "profile-a") =
        BridgeControllerPrimaryClient("https://192.168.2.36:6470/".toHttpUrl(), executor, id)
    private fun profile(id: String) = BridgePairingProfile(
        BridgePairingMetadata(id, "NICE64", "https://192.168.2.36:6470", "v1",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "B".repeat(64), 1),
        BridgeBearerCredential.fromSecret("never-log-token"))
    private fun mobile() = BridgeControllerPrimaryPresentation(
        BridgeControllerPrimaryPhase.MOBILE_PRIMARY, "profile-a", SESSION,
        endpoint(), 1)
    private fun endpoint() = BridgeControllerEndpointDto(true, null,
        "192.168.2.81", 6465, "udp", "nice64-controller-latest-state-v1", "n64")
    private fun primary(value: String, available: Boolean = true) =
        """{"apiVersion":"v1","sessionId":"$SESSION","primary":"$value","endpoint":{"available":$available,"unavailableReason":${if (available) "null" else "\"no-session\""},"address":${if (available) "\"192.168.2.81\"" else "null"},"port":${if (available) "6465" else "null"},"transport":${if (available) "\"udp\"" else "null"},"protocol":${if (available) "\"nice64-controller-latest-state-v1\"" else "null"},"controllerType":${if (available) "\"n64\"" else "null"}}}"""
    private fun primaryDto(value: String, available: Boolean = true) =
        BridgeControllerPrimaryDto("v1", SESSION, value,
            if (available) endpoint() else BridgeControllerEndpointDto(
                false, "no-session", null, null, null, null, null))
    private fun ok(body: String) = BridgeHttpPayload(200, body)
    private fun body(request: Request) = Buffer().use { request.body?.writeTo(it); it.readUtf8() }
    private class FakeExecutor(private val handler: suspend (Request) -> BridgeHttpPayload) :
        BridgeMutationHttpExecutor {
        val requests = mutableListOf<Request>()
        override suspend fun execute(request: Request): BridgeHttpPayload {
            requests += request
            return handler(request)
        }
    }
    companion object { private const val SESSION = "session-1" }
}
