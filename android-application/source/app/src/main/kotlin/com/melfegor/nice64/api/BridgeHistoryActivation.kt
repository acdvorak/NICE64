package com.melfegor.nice64.api

import com.melfegor.nice64.core.model.BridgePairingProfile
import java.io.IOException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.serializer
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

enum class BridgeHistoryCategory(
    val symbol: String,
    val displayName: String,
    internal val expectedAccessory: String,
) {
    CONTROLLER_PAK("controller-pak", "Controller Pak", "controller-pak"),
    TRANSFER_PAK("transfer-pak", "Transfer Pak", "transfer-pak"),
}

internal data class BridgeHistoryActivationCommand(
    val sessionId: String,
    val itemId: String,
    val category: BridgeHistoryCategory,
    val fileName: String,
    val destructiveInstallConfirmed: Boolean,
) {
    init {
        require(sessionId.isNotBlank() && sessionId.length <= 256)
        require(itemId.isNotBlank() && itemId.length <= 1024)
        require(fileName.isNotBlank() && fileName.length <= 256)
        require(category == BridgeHistoryCategory.TRANSFER_PAK || !destructiveInstallConfirmed)
    }

    fun requestJson(): String = JSON.encodeToString(
        BridgeHistoryActivationRequest(destructiveInstallConfirmed),
    )

    companion object {
        private val JSON = Json { encodeDefaults = true }

        fun controllerPak(sessionId: String, item: BridgeHistoryItemDto) =
            BridgeHistoryActivationCommand(
                sessionId, item.itemId, BridgeHistoryCategory.CONTROLLER_PAK,
                item.fileName, false,
            )

        fun transferPak(
            sessionId: String,
            item: BridgeHistoryItemDto,
            explicitlyConfirmed: Boolean,
        ): BridgeHistoryActivationCommand? = if (explicitlyConfirmed) {
            BridgeHistoryActivationCommand(
                sessionId, item.itemId, BridgeHistoryCategory.TRANSFER_PAK,
                item.fileName, true,
            )
        } else null
    }
}

enum class BridgeHistoryActivationPhase {
    IDLE,
    SUBMITTING,
    ACCEPTED,
    RECONCILING,
    SUCCEEDED,
    REJECTED,
    UNCERTAIN,
    FAILED,
}

enum class BridgeHistoryActivationFailureKind {
    CONFIRMATION_REQUIRED,
    INVALID_CONFIRMATION,
    SESSION_NOT_FOUND,
    ITEM_NOT_FOUND,
    ITEM_UNAVAILABLE,
    BUSY,
    AUTHENTICATION,
    FORBIDDEN,
    SERVER,
    CONTRACT,
    PROFILE_CHANGED,
    SESSION_CHANGED,
    OPERATION_FAILED,
    UNCERTAIN,
    REJECTED,
}

data class BridgeHistoryActivationFailure(
    val kind: BridgeHistoryActivationFailureKind,
    val safeMessage: String,
) {
    override fun toString(): String =
        "BridgeHistoryActivationFailure(kind=$kind, message=$safeMessage)"
}

data class BridgeHistoryActivationPresentation(
    val phase: BridgeHistoryActivationPhase = BridgeHistoryActivationPhase.IDLE,
    val profileId: String? = null,
    val sessionId: String? = null,
    internal val itemId: String? = null,
    val category: BridgeHistoryCategory? = null,
    val fileName: String? = null,
    val operationId: Long? = null,
    val failure: BridgeHistoryActivationFailure? = null,
    internal val operationIdBeforePost: Long? = null,
) {
    val inFlight: Boolean
        get() = phase in setOf(
            BridgeHistoryActivationPhase.SUBMITTING,
            BridgeHistoryActivationPhase.ACCEPTED,
            BridgeHistoryActivationPhase.RECONCILING,
        )

    override fun toString(): String =
        "BridgeHistoryActivationPresentation(phase=$phase, profileId=$profileId, sessionId=$sessionId, category=$category, fileName=$fileName, operationId=$operationId, failure=${failure?.kind})"
}

internal fun shouldPublishHistoryActivation(
    activeProfileId: String?,
    value: BridgeHistoryActivationPresentation,
): Boolean = value.profileId == null || value.profileId == activeProfileId

internal sealed interface BridgeHistoryActivationSubmission {
    data class Accepted(val operationId: Long) : BridgeHistoryActivationSubmission
    data class Rejected(val failure: BridgeHistoryActivationFailure) : BridgeHistoryActivationSubmission
    data class Uncertain(val failure: BridgeHistoryActivationFailure) : BridgeHistoryActivationSubmission
}

internal class BridgeHistoryActivationClient(
    private val baseUrl: HttpUrl,
    private val executor: BridgeMutationHttpExecutor,
    val profileId: String,
) {
    suspend fun submit(command: BridgeHistoryActivationCommand): BridgeHistoryActivationSubmission {
        val url = baseUrl.newBuilder()
            .addPathSegments("api/v1/sessions")
            .addPathSegment(command.sessionId)
            .addPathSegment("history")
            .addPathSegment(command.itemId)
            .addPathSegment("activate")
            .build()
        val request = Request.Builder().url(url)
            .post(command.requestJson().toRequestBody(JSON_MEDIA_TYPE)).build()
        return try {
            val response = executor.execute(request)
            val body = runCatching { decode<BridgeHistoryActivationResponseDto>(response.body) }
                .getOrNull()
            when (response.statusCode) {
                202 -> if (body?.accepted == true && body.operationId != null &&
                    body.operationId > 0 && body.operationKind == OPERATION_KIND
                ) BridgeHistoryActivationSubmission.Accepted(body.operationId)
                else rejected(BridgeHistoryActivationFailureKind.CONTRACT,
                    "The Bridge History response did not match API v1.")
                400 -> rejected(
                    if (body?.code == "invalid-confirmation")
                        BridgeHistoryActivationFailureKind.INVALID_CONFIRMATION
                    else BridgeHistoryActivationFailureKind.REJECTED,
                    "The Bridge rejected the History activation request.",
                )
                401 -> rejected(BridgeHistoryActivationFailureKind.AUTHENTICATION,
                    "Bridge authentication failed.")
                403 -> rejected(BridgeHistoryActivationFailureKind.FORBIDDEN,
                    "The Bridge did not authorize this operation.")
                404 -> rejected(
                    if (body?.code == "session-not-found")
                        BridgeHistoryActivationFailureKind.SESSION_NOT_FOUND
                    else BridgeHistoryActivationFailureKind.ITEM_NOT_FOUND,
                    if (body?.code == "session-not-found")
                        "The active Bridge session is no longer available."
                    else "The History item is no longer available.",
                )
                409 -> when (body?.code) {
                    "confirmation-required" -> rejected(
                        BridgeHistoryActivationFailureKind.CONFIRMATION_REQUIRED,
                        "Transfer Pak activation requires explicit confirmation.",
                    )
                    "operation-busy" -> rejected(BridgeHistoryActivationFailureKind.BUSY,
                        "Another Bridge operation is already in progress.")
                    "history-item-unavailable" -> rejected(
                        BridgeHistoryActivationFailureKind.ITEM_UNAVAILABLE,
                        "The History item is currently unavailable.")
                    "session-not-found", "session-unavailable" -> rejected(
                        BridgeHistoryActivationFailureKind.SESSION_NOT_FOUND,
                        "The active Bridge session is no longer available.")
                    else -> rejected(BridgeHistoryActivationFailureKind.REJECTED,
                        "The Bridge could not safely activate this History item.")
                }
                in 500..599 -> rejected(BridgeHistoryActivationFailureKind.SERVER,
                    "The Bridge could not accept the History activation.")
                else -> rejected(BridgeHistoryActivationFailureKind.REJECTED,
                    "The Bridge returned an unexpected response.")
            }
        } catch (_: CancellationException) {
            uncertain()
        } catch (_: IOException) {
            uncertain()
        } catch (_: Exception) {
            rejected(BridgeHistoryActivationFailureKind.CONTRACT,
                "The Bridge History response did not match API v1.")
        }
    }

    suspend fun operation(): BridgeApiResult<BridgeOperationDto> = get("api", "v1", "operation")
    suspend fun state(): BridgeApiResult<BridgeStateDto> = get("api", "v1", "state")
    suspend fun sessions(): BridgeApiResult<BridgeSessionsDto> = get("api", "v1", "sessions")
    suspend fun history(): BridgeApiResult<BridgeHistoryDto> = get("api", "v1", "history")

    private suspend inline fun <reified T> get(vararg segments: String): BridgeApiResult<T> {
        val url = baseUrl.newBuilder().apply { segments.forEach(::addPathSegment) }.build()
        return try {
            val response = executor.execute(Request.Builder().url(url).get().build())
            if (response.statusCode !in 200..299) BridgeApiResult.Failure(
                BridgeApiFailure(BridgeApiFailureKind.API_RESPONSE,
                    "The Bridge reconciliation response was unavailable."),
            ) else BridgeApiResult.Success(decode(response.body))
        } catch (_: Exception) {
            BridgeApiResult.Failure(BridgeApiFailureKind.UNREACHABLE.failure(
                "The Bridge reconciliation response was unavailable."))
        }
    }

    private inline fun <reified T> decode(body: String): T =
        JSON.decodeFromString(serializer<T>(), body)

    private fun rejected(kind: BridgeHistoryActivationFailureKind, message: String) =
        BridgeHistoryActivationSubmission.Rejected(BridgeHistoryActivationFailure(kind, message))

    private fun uncertain() = BridgeHistoryActivationSubmission.Uncertain(
        BridgeHistoryActivationFailure(
            BridgeHistoryActivationFailureKind.UNCERTAIN,
            "The request outcome is uncertain. Refreshing authoritative Bridge state.",
        ),
    )

    companion object {
        const val OPERATION_KIND = "history-activation"
        private val JSON = Json { ignoreUnknownKeys = false }
        private val JSON_MEDIA_TYPE = "application/json; charset=utf-8".toMediaType()
    }
}

private fun BridgeApiFailureKind.failure(message: String) = BridgeApiFailure(this, message)

internal fun interface BridgeHistoryActivationClientFactory {
    fun create(profile: BridgePairingProfile): BridgeApiResult<BridgeHistoryActivationClient>
}

internal object PinnedBridgeHistoryActivationClientFactory : BridgeHistoryActivationClientFactory {
    override fun create(profile: BridgePairingProfile): BridgeApiResult<BridgeHistoryActivationClient> =
        when (val pinned = com.melfegor.nice64.tls.BridgePinnedHttpsClientFactory.create(profile)) {
            is com.melfegor.nice64.tls.BridgePinnedClientResult.Success -> BridgeApiResult.Success(
                BridgeHistoryActivationClient(
                    pinned.value.apiBaseUrl.toHttpUrl(),
                    OkHttpMutationExecutor(pinned.value),
                    pinned.value.profileId,
                ),
            )
            is com.melfegor.nice64.tls.BridgePinnedClientResult.Failure -> BridgeApiResult.Failure(
                BridgeApiFailure(BridgeApiFailureKind.TLS_IDENTITY,
                    "The paired Bridge identity could not be configured."),
            )
        }
}

internal fun interface BridgeHistoryActivationDelay {
    suspend fun wait(milliseconds: Long)
}

internal class BridgeHistoryActivationCoordinator(
    private val profiles: ActiveBridgeProfileSource,
    private val clients: BridgeHistoryActivationClientFactory =
        PinnedBridgeHistoryActivationClientFactory,
    private val reconciliationAttempts: Int = 121,
    private val reconciliationDelayMs: Long = 1_000,
    private val waiter: BridgeHistoryActivationDelay = BridgeHistoryActivationDelay { delay(it) },
) {
    init {
        require(reconciliationAttempts in 1..301)
        require(reconciliationDelayMs in 0..5_000)
    }

    suspend fun submit(
        expectedProfileId: String,
        command: BridgeHistoryActivationCommand,
        publish: (BridgeHistoryActivationPresentation) -> Unit = {},
    ): BridgeHistoryActivationPresentation {
        val client = client(expectedProfileId) ?: return profileChanged(command, publish)
        if (!authorityMatches(client, command)) return sessionChanged(command, expectedProfileId, publish)
        val before = (client.operation() as? BridgeApiResult.Success)?.value
        if (before?.status == "running") return finish(presentation(
            expectedProfileId, command, BridgeHistoryActivationPhase.REJECTED,
            failure = BridgeHistoryActivationFailure(
                BridgeHistoryActivationFailureKind.BUSY,
                "Another Bridge operation is already in progress.",
            ),
        ), publish)
        if (!authorityMatches(client, command)) {
            return sessionChanged(command, expectedProfileId, publish)
        }

        publish(presentation(expectedProfileId, command, BridgeHistoryActivationPhase.SUBMITTING))
        return when (val submitted = client.submit(command)) {
            is BridgeHistoryActivationSubmission.Accepted -> {
                val accepted = presentation(expectedProfileId, command,
                    BridgeHistoryActivationPhase.ACCEPTED, submitted.operationId,
                    operationIdBeforePost = before?.operationId)
                publish(accepted)
                reconcile(client, command, accepted, before?.operationId, publish)
            }
            is BridgeHistoryActivationSubmission.Rejected -> finish(presentation(
                expectedProfileId, command, BridgeHistoryActivationPhase.REJECTED,
                failure = submitted.failure,
            ), publish)
            is BridgeHistoryActivationSubmission.Uncertain -> {
                val uncertain = presentation(expectedProfileId, command,
                    BridgeHistoryActivationPhase.UNCERTAIN, failure = submitted.failure,
                    operationIdBeforePost = before?.operationId)
                publish(uncertain)
                reconcile(client, command, uncertain, before?.operationId, publish)
            }
        }
    }

    suspend fun resume(
        pending: BridgeHistoryActivationPresentation,
        publish: (BridgeHistoryActivationPresentation) -> Unit = {},
    ): BridgeHistoryActivationPresentation {
        if (pending.phase !in setOf(
                BridgeHistoryActivationPhase.ACCEPTED,
                BridgeHistoryActivationPhase.RECONCILING,
                BridgeHistoryActivationPhase.UNCERTAIN,
            ) || pending.profileId == null || pending.sessionId == null ||
            pending.itemId == null || pending.category == null || pending.fileName == null
        ) return pending
        val client = client(pending.profileId) ?: return profileChanged(pending.command(), publish)
        return reconcile(
            client, pending.command(), pending, pending.operationIdBeforePost, publish,
        )
    }

    private suspend fun reconcile(
        client: BridgeHistoryActivationClient,
        command: BridgeHistoryActivationCommand,
        pending: BridgeHistoryActivationPresentation,
        operationBeforePost: Long?,
        publish: (BridgeHistoryActivationPresentation) -> Unit,
    ): BridgeHistoryActivationPresentation {
        var operationId = pending.operationId
        publish(pending.copy(phase = BridgeHistoryActivationPhase.RECONCILING))
        repeat(reconciliationAttempts) { attempt ->
            if (!activeProfileMatches(client.profileId)) {
                return finish(pending.copy(
                    phase = BridgeHistoryActivationPhase.FAILED,
                    operationId = operationId,
                    failure = BridgeHistoryActivationFailure(
                        BridgeHistoryActivationFailureKind.PROFILE_CHANGED,
                        "The active Bridge profile changed. The request was not replayed.",
                    ),
                ), publish)
            }
            val operation = (client.operation() as? BridgeApiResult.Success)?.value
            val sessions = (client.sessions() as? BridgeApiResult.Success)?.value
            val sessionChanged = sessions?.let {
                val current = it.sessions.singleOrNull()
                current == null || current.sessionId != command.sessionId ||
                    current.state != "connected"
            }
            if (operationId == null && operation?.kind == BridgeHistoryActivationClient.OPERATION_KIND &&
                operation.operationId > 0 && operation.operationId != operationBeforePost
            ) operationId = operation.operationId
            val correlated = operationId != null && operation?.operationId == operationId &&
                operation.kind == BridgeHistoryActivationClient.OPERATION_KIND
            if (sessionChanged == true) return finish(pending.copy(
                phase = BridgeHistoryActivationPhase.FAILED,
                operationId = operationId,
                failure = BridgeHistoryActivationFailure(
                    BridgeHistoryActivationFailureKind.SESSION_CHANGED,
                    "The active Bridge session changed. The request was not replayed.",
                ),
            ), publish)
            if (correlated && operation.status in setOf("failed", "cancelled")) {
                return finish(pending.copy(
                    phase = BridgeHistoryActivationPhase.FAILED,
                    operationId = operationId,
                    failure = BridgeHistoryActivationFailure(
                        BridgeHistoryActivationFailureKind.OPERATION_FAILED,
                        "The Bridge History activation did not complete successfully.",
                    ),
                ), publish)
            }
            if (correlated && operation.status == "succeeded" && sessionChanged == false) {
                val state = (client.state() as? BridgeApiResult.Success)?.value
                val history = (client.history() as? BridgeApiResult.Success)?.value
                val accessoryMatches =
                    state?.device?.activeAccessory == command.category.expectedAccessory
                val itemKnown = history?.items(command.category)?.any {
                    it.itemId == command.itemId
                } == true
                if (accessoryMatches && itemKnown) {
                    return finish(pending.copy(
                        phase = BridgeHistoryActivationPhase.SUCCEEDED,
                        operationId = operationId,
                        failure = null,
                    ), publish)
                }
            }
            if (attempt + 1 < reconciliationAttempts) waiter.wait(reconciliationDelayMs)
        }
        return finish(pending.copy(
            phase = BridgeHistoryActivationPhase.UNCERTAIN,
            operationId = operationId,
            failure = BridgeHistoryActivationFailure(
                BridgeHistoryActivationFailureKind.UNCERTAIN,
                "The activation is unresolved. Refresh to reconcile authoritative Bridge state.",
            ),
        ), publish)
    }

    private suspend fun authorityMatches(
        client: BridgeHistoryActivationClient,
        command: BridgeHistoryActivationCommand,
    ): Boolean {
        val sessions = (client.sessions() as? BridgeApiResult.Success)?.value ?: return false
        val current = sessions.sessions.singleOrNull() ?: return false
        if (current.sessionId != command.sessionId || current.state != "connected") return false
        val history = (client.history() as? BridgeApiResult.Success)?.value ?: return false
        return history.items(command.category).any {
            it.itemId == command.itemId && it.available && it.fileName == command.fileName
        }
    }

    private suspend fun client(profileId: String): BridgeHistoryActivationClient? {
        val active = profiles.load() as? ActiveBridgeProfileResult.Success ?: return null
        if (active.profile.metadata.profileId != profileId) return null
        val created = clients.create(active.profile) as? BridgeApiResult.Success ?: return null
        return created.value.takeIf { it.profileId == profileId }
    }

    private suspend fun activeProfileMatches(profileId: String): Boolean {
        val active = profiles.load() as? ActiveBridgeProfileResult.Success ?: return false
        return active.profile.metadata.profileId == profileId
    }

    private fun BridgeHistoryDto.items(category: BridgeHistoryCategory) = when (category) {
        BridgeHistoryCategory.CONTROLLER_PAK -> controllerPak
        BridgeHistoryCategory.TRANSFER_PAK -> transferPak
    }

    private fun BridgeHistoryActivationPresentation.command() = BridgeHistoryActivationCommand(
        sessionId!!, itemId!!, category!!, fileName!!,
        category == BridgeHistoryCategory.TRANSFER_PAK,
    )

    private fun presentation(
        profileId: String?,
        command: BridgeHistoryActivationCommand,
        phase: BridgeHistoryActivationPhase,
        operationId: Long? = null,
        failure: BridgeHistoryActivationFailure? = null,
        operationIdBeforePost: Long? = null,
    ) = BridgeHistoryActivationPresentation(
        phase, profileId, command.sessionId, command.itemId, command.category,
        command.fileName, operationId, failure, operationIdBeforePost,
    )

    private fun profileChanged(
        command: BridgeHistoryActivationCommand,
        publish: (BridgeHistoryActivationPresentation) -> Unit,
    ) = finish(presentation(null, command, BridgeHistoryActivationPhase.FAILED,
        failure = BridgeHistoryActivationFailure(
            BridgeHistoryActivationFailureKind.PROFILE_CHANGED,
            "The active Bridge profile changed. No request was sent.",
        )), publish)

    private fun sessionChanged(
        command: BridgeHistoryActivationCommand,
        profileId: String,
        publish: (BridgeHistoryActivationPresentation) -> Unit,
    ) = finish(presentation(profileId, command, BridgeHistoryActivationPhase.FAILED,
        failure = BridgeHistoryActivationFailure(
            BridgeHistoryActivationFailureKind.SESSION_CHANGED,
            "The active Bridge session or History item changed. No request was sent.",
        )), publish)

    private fun finish(
        value: BridgeHistoryActivationPresentation,
        publish: (BridgeHistoryActivationPresentation) -> Unit,
    ): BridgeHistoryActivationPresentation {
        publish(value)
        return value
    }
}
