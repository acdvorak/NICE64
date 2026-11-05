package com.melfegor.nice64.api

import com.melfegor.nice64.core.model.BridgePairingProfile
import com.melfegor.nice64.pairing.PairingRepository
import com.melfegor.nice64.pairing.PairingStorageResult
import com.melfegor.nice64.tls.BridgePinnedClientResult
import com.melfegor.nice64.tls.BridgePinnedHttpsClient
import com.melfegor.nice64.tls.BridgePinnedHttpsClientFactory
import com.melfegor.nice64.tls.BridgeTlsValidationException
import java.io.IOException
import javax.net.ssl.SSLException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.serializer
import okhttp3.HttpUrl
import okhttp3.Request
import okhttp3.Response
import okhttp3.HttpUrl.Companion.toHttpUrl

internal sealed interface ActiveBridgeProfileResult {
    data object Unpaired : ActiveBridgeProfileResult
    data object CredentialUnavailable : ActiveBridgeProfileResult
    data class Success(val profile: BridgePairingProfile) : ActiveBridgeProfileResult
}

internal fun interface ActiveBridgeProfileSource {
    suspend fun load(): ActiveBridgeProfileResult
}

internal class StoredActiveBridgeProfileSource(
    private val repository: PairingRepository,
) : ActiveBridgeProfileSource {
    override suspend fun load(): ActiveBridgeProfileResult {
        val active = repository.activeProfileId()
        val profileId = (active as? PairingStorageResult.Success)?.value
            ?: return if (active is PairingStorageResult.Success) {
                ActiveBridgeProfileResult.Unpaired
            } else {
                ActiveBridgeProfileResult.CredentialUnavailable
            }
        return when (val loaded = repository.load(profileId)) {
            is PairingStorageResult.Success -> ActiveBridgeProfileResult.Success(loaded.value)
            is PairingStorageResult.Failure -> ActiveBridgeProfileResult.CredentialUnavailable
        }
    }
}

internal fun interface BridgeReadOnlyClientFactory {
    fun create(profile: BridgePairingProfile): BridgeApiResult<BridgeReadOnlyApiClient>
}

internal object PinnedBridgeReadOnlyClientFactory : BridgeReadOnlyClientFactory {
    override fun create(profile: BridgePairingProfile): BridgeApiResult<BridgeReadOnlyApiClient> =
        when (val result = BridgePinnedHttpsClientFactory.create(profile)) {
            is BridgePinnedClientResult.Success -> BridgeApiResult.Success(
                BridgeReadOnlyApiClient(
                    baseUrl = result.value.apiBaseUrl.toHttpUrl(),
                    executor = OkHttpBridgeExecutor(result.value),
                    profileId = result.value.profileId,
                ),
            )
            is BridgePinnedClientResult.Failure -> BridgeApiResult.Failure(
                BridgeApiFailure(
                    BridgeApiFailureKind.TLS_IDENTITY,
                    "The paired Bridge identity could not be configured.",
                ),
            )
        }
}

internal data class BridgeHttpPayload(val statusCode: Int, val body: String)

internal fun interface BridgeHttpExecutor {
    suspend fun execute(request: Request): BridgeHttpPayload
}

private class OkHttpBridgeExecutor(
    private val pinned: BridgePinnedHttpsClient,
) : BridgeHttpExecutor {
    override suspend fun execute(request: Request): BridgeHttpPayload = withContext(Dispatchers.IO) {
        pinned.httpClient.newCall(request).execute().use { response: Response ->
            BridgeHttpPayload(response.code, response.body.string())
        }
    }
}

class BridgeReadOnlyApiClient internal constructor(
    private val baseUrl: HttpUrl,
    private val executor: BridgeHttpExecutor,
    val profileId: String,
) {
    suspend fun health(): BridgeApiResult<BridgeHealthDto> = get("api", "v1", "health")
    suspend fun state(): BridgeApiResult<BridgeStateDto> = get("api", "v1", "state")
    suspend fun operation(): BridgeApiResult<BridgeOperationDto> = get("api", "v1", "operation")
    suspend fun history(): BridgeApiResult<BridgeHistoryDto> = get("api", "v1", "history")
    suspend fun sessions(): BridgeApiResult<BridgeSessionsDto> = get("api", "v1", "sessions")
    suspend fun session(sessionId: String): BridgeApiResult<BridgeSessionDto> =
        get("api", "v1", "sessions", sessionId)
    suspend fun controller(sessionId: String): BridgeApiResult<BridgeControllerPrimaryDto> =
        get("api", "v1", "sessions", sessionId, "controller", "primary")

    private suspend inline fun <reified T> get(vararg segments: String): BridgeApiResult<T> {
        val url = baseUrl.newBuilder().apply {
            segments.forEach(::addPathSegment)
        }.build()
        val request = Request.Builder().url(url).get().build()
        return try {
            val response = executor.execute(request)
            if (response.statusCode !in 200..299) return statusFailure(response.statusCode)
            val value = JSON.decodeFromString(serializer<T>(), response.body)
            val apiVersion = when (value) {
                is BridgeHealthDto -> value.apiVersion
                is BridgeStateDto -> value.apiVersion
                is BridgeHistoryDto -> value.apiVersion
                is BridgeSessionsDto -> value.apiVersion
                is BridgeControllerPrimaryDto -> value.apiVersion
                else -> API_VERSION
            }
            if (apiVersion != API_VERSION) {
                BridgeApiResult.Failure(failure(BridgeApiFailureKind.INCOMPATIBLE_API, "The Bridge API version is incompatible."))
            } else {
                BridgeApiResult.Success(value)
            }
        } catch (error: Exception) {
            when {
                error.hasCause<BridgeTlsValidationException>() || error.hasCause<SSLException>() ->
                    BridgeApiResult.Failure(failure(BridgeApiFailureKind.TLS_IDENTITY, "The paired Bridge identity could not be verified."))
                error is SerializationException || error is IllegalArgumentException ->
                    BridgeApiResult.Failure(failure(BridgeApiFailureKind.CONTRACT, "The Bridge response did not match API v1."))
                error is IOException ->
                    BridgeApiResult.Failure(failure(BridgeApiFailureKind.UNREACHABLE, "The paired Bridge could not be reached."))
                else -> BridgeApiResult.Failure(failure(BridgeApiFailureKind.API_RESPONSE, "The Bridge response could not be read."))
            }
        }
    }

    private fun statusFailure(code: Int): BridgeApiResult.Failure = when (code) {
        401, 403 -> BridgeApiResult.Failure(failure(BridgeApiFailureKind.AUTHENTICATION, "Bridge authentication failed."))
        404 -> BridgeApiResult.Failure(failure(BridgeApiFailureKind.NOT_FOUND, "The requested Bridge resource is not active."))
        409 -> BridgeApiResult.Failure(failure(BridgeApiFailureKind.CONFLICT, "The Bridge resource is temporarily unavailable."))
        in 500..599 -> BridgeApiResult.Failure(failure(BridgeApiFailureKind.SERVER, "The Bridge API could not complete the request."))
        else -> BridgeApiResult.Failure(failure(BridgeApiFailureKind.API_RESPONSE, "The Bridge returned an unexpected response."))
    }

    companion object {
        private const val API_VERSION = "v1"
        private val JSON = Json { ignoreUnknownKeys = false }

        fun fromPinned(pinned: BridgePinnedHttpsClient): BridgeReadOnlyApiClient =
            BridgeReadOnlyApiClient(pinned.apiBaseUrl.toHttpUrl(), OkHttpBridgeExecutor(pinned), pinned.profileId)
    }
}

private inline fun <reified T : Throwable> Throwable.hasCause(): Boolean {
    var current: Throwable? = this
    while (current != null) {
        if (current is T) return true
        current = current.cause
    }
    return false
}

private fun failure(kind: BridgeApiFailureKind, message: String) = BridgeApiFailure(kind, message)
