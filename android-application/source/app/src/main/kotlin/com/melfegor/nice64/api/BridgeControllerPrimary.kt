package com.melfegor.nice64.api

import com.melfegor.nice64.core.model.BridgePairingProfile
import com.melfegor.nice64.tls.BridgePinnedClientResult
import com.melfegor.nice64.tls.BridgePinnedHttpsClientFactory
import java.io.IOException
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.serializer
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

enum class BridgeControllerPrimaryPhase {
    BRIDGE_PRIMARY,
    SWITCHING_TO_MOBILE,
    MOBILE_PRIMARY,
    SWITCHING_TO_BRIDGE,
    ERROR,
}

enum class BridgeControllerPrimaryFailureKind {
    SESSION_UNAVAILABLE, ENDPOINT_UNAVAILABLE, AUTHENTICATION,
    REJECTED, CONTRACT, PROFILE_CHANGED, UNCERTAIN,
}

data class BridgeControllerPrimaryFailure(
    val kind: BridgeControllerPrimaryFailureKind,
    val safeMessage: String,
) {
    override fun toString(): String =
        "BridgeControllerPrimaryFailure(kind=$kind, message=$safeMessage)"
}

data class BridgeControllerPrimaryPresentation(
    val phase: BridgeControllerPrimaryPhase = BridgeControllerPrimaryPhase.BRIDGE_PRIMARY,
    val profileId: String? = null,
    val sessionId: String? = null,
    val endpoint: BridgeControllerEndpointDto? = null,
    val acquisitionGeneration: Long = 0,
    val failure: BridgeControllerPrimaryFailure? = null,
) {
    val mobilePrimary: Boolean get() = phase == BridgeControllerPrimaryPhase.MOBILE_PRIMARY
    val actionInFlight: Boolean get() = phase == BridgeControllerPrimaryPhase.SWITCHING_TO_MOBILE ||
        phase == BridgeControllerPrimaryPhase.SWITCHING_TO_BRIDGE
    override fun toString(): String =
        "BridgeControllerPrimaryPresentation(phase=$phase, profileId=$profileId, sessionId=$sessionId, generation=$acquisitionGeneration, failure=${failure?.kind})"
}

internal fun shouldPublishControllerPrimary(
    activeProfileId: String?, value: BridgeControllerPrimaryPresentation,
): Boolean = value.profileId == null || value.profileId == activeProfileId

internal fun shouldStartReconciledControllerSender(
    previous: BridgeControllerPrimaryPresentation,
    authoritative: BridgeControllerPrimaryDto,
    dataPlane: BridgeControllerDataPlanePresentation,
    allowFailedSenderReplacement: Boolean = false,
): Boolean = !previous.actionInFlight && authoritative.primary == "nice-mobile" &&
    authoritative.endpoint.available &&
    (dataPlane.phase == BridgeControllerDataPlanePhase.STOPPED ||
        allowFailedSenderReplacement && dataPlane.phase == BridgeControllerDataPlanePhase.FAILED)

internal sealed interface BridgeControllerPrimaryHttpResult {
    data class Success(val value: BridgeControllerPrimaryDto) : BridgeControllerPrimaryHttpResult
    data class Rejected(val failure: BridgeControllerPrimaryFailure) : BridgeControllerPrimaryHttpResult
    data class Ambiguous(val failure: BridgeControllerPrimaryFailure) : BridgeControllerPrimaryHttpResult
}

internal class BridgeControllerPrimaryClient(
    private val baseUrl: HttpUrl,
    private val executor: BridgeMutationHttpExecutor,
    val profileId: String,
) {
    suspend fun get(sessionId: String): BridgeApiResult<BridgeControllerPrimaryDto> {
        val request = Request.Builder().url(primaryUrl(sessionId)).get().build()
        return try {
            val response = executor.execute(request)
            if (response.statusCode !in 200..299) BridgeApiResult.Failure(BridgeApiFailure(
                if (response.statusCode == 404) BridgeApiFailureKind.NOT_FOUND
                else BridgeApiFailureKind.API_RESPONSE,
                "The authoritative controller primary is unavailable.",
            )) else {
                val dto = decode<BridgeControllerPrimaryDto>(response.body)
                if (valid(dto, sessionId)) BridgeApiResult.Success(dto)
                else BridgeApiResult.Failure(BridgeApiFailure(
                    BridgeApiFailureKind.CONTRACT,
                    "The Bridge controller-primary response did not match API v1.",
                ))
            }
        } catch (_: Exception) {
            BridgeApiResult.Failure(BridgeApiFailure(
                BridgeApiFailureKind.UNREACHABLE,
                "The authoritative controller primary is unavailable.",
            ))
        }
    }

    suspend fun set(sessionId: String, primary: String): BridgeControllerPrimaryHttpResult {
        val body = JSON.encodeToString(BridgeControllerPrimaryRequest(primary))
        val request = Request.Builder().url(primaryUrl(sessionId))
            .put(body.toRequestBody(JSON_MEDIA_TYPE)).build()
        return try {
            val response = executor.execute(request)
            if (response.statusCode == 200) {
                val dto = decode<BridgeControllerPrimaryDto>(response.body)
                if (valid(dto, sessionId) && dto.primary == primary)
                    BridgeControllerPrimaryHttpResult.Success(dto)
                else rejected(BridgeControllerPrimaryFailureKind.CONTRACT,
                    "The Bridge controller-primary response did not match API v1.")
            } else {
                val code = runCatching { decode<BridgeErrorDto>(response.body).code }.getOrNull()
                when (response.statusCode) {
                    401, 403 -> rejected(BridgeControllerPrimaryFailureKind.AUTHENTICATION,
                        "Bridge authentication failed.")
                    404 -> rejected(BridgeControllerPrimaryFailureKind.SESSION_UNAVAILABLE,
                        "The active Bridge session is unavailable.")
                    409 -> rejected(BridgeControllerPrimaryFailureKind.ENDPOINT_UNAVAILABLE,
                        if (code == "protected-operation")
                            "Controller primary cannot change during the protected operation."
                        else "The controller endpoint is currently unavailable.")
                    else -> rejected(BridgeControllerPrimaryFailureKind.REJECTED,
                        "The Bridge could not complete the controller-primary request.")
                }
            }
        } catch (_: CancellationException) { ambiguous() }
        catch (_: IOException) { ambiguous() }
        catch (_: Exception) { rejected(BridgeControllerPrimaryFailureKind.CONTRACT,
            "The Bridge controller-primary response did not match API v1.") }
    }

    private fun valid(dto: BridgeControllerPrimaryDto, sessionId: String): Boolean =
        dto.apiVersion == API_VERSION && dto.sessionId == sessionId &&
            dto.primary in setOf("bridge", "nice-mobile") &&
            (dto.primary != "nice-mobile" || dto.endpoint.available)

    private fun ambiguous() = BridgeControllerPrimaryHttpResult.Ambiguous(
        BridgeControllerPrimaryFailure(BridgeControllerPrimaryFailureKind.UNCERTAIN,
            "Controller-primary outcome is uncertain. Refresh authoritative state."))
    private fun rejected(kind: BridgeControllerPrimaryFailureKind, message: String) =
        BridgeControllerPrimaryHttpResult.Rejected(BridgeControllerPrimaryFailure(kind, message))
    private fun primaryUrl(sessionId: String) = baseUrl.newBuilder()
        .addPathSegments("api/v1/sessions").addPathSegment(sessionId)
        .addPathSegments("controller/primary").build()
    private inline fun <reified T> decode(body: String): T =
        JSON.decodeFromString(serializer<T>(), body)

    companion object {
        private const val API_VERSION = "v1"
        private val JSON = Json { ignoreUnknownKeys = false; encodeDefaults = true }
        private val JSON_MEDIA_TYPE = "application/json; charset=utf-8".toMediaType()
    }
}

internal fun interface BridgeControllerPrimaryClientFactory {
    fun create(profile: BridgePairingProfile): BridgeApiResult<BridgeControllerPrimaryClient>
}

internal object PinnedBridgeControllerPrimaryClientFactory : BridgeControllerPrimaryClientFactory {
    override fun create(profile: BridgePairingProfile): BridgeApiResult<BridgeControllerPrimaryClient> =
        when (val pinned = BridgePinnedHttpsClientFactory.create(profile)) {
            is BridgePinnedClientResult.Success -> BridgeApiResult.Success(
                BridgeControllerPrimaryClient(pinned.value.apiBaseUrl.toHttpUrl(),
                    OkHttpMutationExecutor(pinned.value), pinned.value.profileId))
            is BridgePinnedClientResult.Failure -> BridgeApiResult.Failure(
                BridgeApiFailure(BridgeApiFailureKind.TLS_IDENTITY,
                    "The paired Bridge identity could not be configured."))
        }
}

internal class BridgeControllerPrimaryCoordinator(
    private val profiles: ActiveBridgeProfileSource,
    private val clients: BridgeControllerPrimaryClientFactory =
        PinnedBridgeControllerPrimaryClientFactory,
) {
    private val actions = Mutex()
    private val generation = AtomicLong()

    suspend fun selectMobile(
        expectedProfileId: String,
        sessionId: String,
        publish: (BridgeControllerPrimaryPresentation) -> Unit = {},
    ): BridgeControllerPrimaryPresentation = select(
        expectedProfileId, sessionId, "nice-mobile",
        BridgeControllerPrimaryPhase.SWITCHING_TO_MOBILE, publish)

    suspend fun selectBridge(
        current: BridgeControllerPrimaryPresentation,
        publish: (BridgeControllerPrimaryPresentation) -> Unit = {},
    ): BridgeControllerPrimaryPresentation {
        val profileId = current.profileId ?: return current
        val sessionId = current.sessionId ?: return current
        return select(profileId, sessionId, "bridge",
            BridgeControllerPrimaryPhase.SWITCHING_TO_BRIDGE, publish)
    }

    suspend fun refresh(
        expectedProfileId: String, sessionId: String,
    ): BridgeControllerPrimaryPresentation = actions.withLock {
        val client = client(expectedProfileId) ?: return@withLock failed(
            expectedProfileId, sessionId, BridgeControllerPrimaryFailureKind.PROFILE_CHANGED,
            "The active Bridge profile changed.")
        val dto = (client.get(sessionId) as? BridgeApiResult.Success)?.value
            ?: return@withLock failed(expectedProfileId, sessionId,
                BridgeControllerPrimaryFailureKind.SESSION_UNAVAILABLE,
                "The authoritative controller primary is unavailable.")
        present(expectedProfileId, dto, generation.get())
    }

    private suspend fun select(
        profileId: String, sessionId: String, requested: String,
        pending: BridgeControllerPrimaryPhase,
        publish: (BridgeControllerPrimaryPresentation) -> Unit,
    ): BridgeControllerPrimaryPresentation = actions.withLock {
        val attempt = generation.incrementAndGet()
        val client = client(profileId) ?: return@withLock finish(failed(
            profileId, sessionId, BridgeControllerPrimaryFailureKind.PROFILE_CHANGED,
            "The active Bridge profile changed."), publish)
        publish(BridgeControllerPrimaryPresentation(pending, profileId, sessionId,
            acquisitionGeneration = attempt))
        when (val result = client.set(sessionId, requested)) {
            is BridgeControllerPrimaryHttpResult.Success ->
                finish(present(profileId, result.value, attempt), publish)
            is BridgeControllerPrimaryHttpResult.Rejected -> finish(failed(
                profileId, sessionId, result.failure.kind, result.failure.safeMessage,
                attempt), publish)
            is BridgeControllerPrimaryHttpResult.Ambiguous -> finish(
                BridgeControllerPrimaryPresentation(BridgeControllerPrimaryPhase.ERROR,
                    profileId, sessionId, acquisitionGeneration = attempt,
                    failure = result.failure), publish)
        }
    }

    private suspend fun client(profileId: String): BridgeControllerPrimaryClient? {
        val active = profiles.load() as? ActiveBridgeProfileResult.Success ?: return null
        if (active.profile.metadata.profileId != profileId) return null
        val created = clients.create(active.profile) as? BridgeApiResult.Success ?: return null
        return created.value.takeIf { it.profileId == profileId }
    }

    private fun present(profileId: String, dto: BridgeControllerPrimaryDto, generation: Long) =
        BridgeControllerPrimaryPresentation(
            if (dto.primary == "nice-mobile") BridgeControllerPrimaryPhase.MOBILE_PRIMARY
            else BridgeControllerPrimaryPhase.BRIDGE_PRIMARY,
            profileId, dto.sessionId,
            endpoint = dto.endpoint.takeIf { dto.primary == "nice-mobile" },
            acquisitionGeneration = generation)

    private fun failed(
        profileId: String?, sessionId: String?, kind: BridgeControllerPrimaryFailureKind,
        message: String, generation: Long = this.generation.get(),
    ) = BridgeControllerPrimaryPresentation(BridgeControllerPrimaryPhase.ERROR,
        profileId, sessionId, acquisitionGeneration = generation,
        failure = BridgeControllerPrimaryFailure(kind, message))

    private fun finish(
        value: BridgeControllerPrimaryPresentation,
        publish: (BridgeControllerPrimaryPresentation) -> Unit,
    ): BridgeControllerPrimaryPresentation { publish(value); return value }
}
