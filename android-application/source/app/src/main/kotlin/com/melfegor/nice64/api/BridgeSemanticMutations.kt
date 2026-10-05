package com.melfegor.nice64.api

import com.melfegor.nice64.core.model.BridgePairingProfile
import com.melfegor.nice64.tls.BridgePinnedClientResult
import com.melfegor.nice64.tls.BridgePinnedHttpsClient
import com.melfegor.nice64.tls.BridgePinnedHttpsClientFactory
import java.io.IOException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.serializer
import okhttp3.HttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.HttpUrl.Companion.toHttpUrl

enum class BridgeFirmwareTarget(val symbol: String, val displayName: String, val capability: String) {
    CONTROLLER("controller", "Controller", "firmware-mode-controller"),
    DIAGNOSTICS("diagnostics", "Diagnostics", "firmware-mode-diagnostics"),
    NETWORK("network", "Network", "firmware-mode-network"),
}

enum class BridgeAccessoryTarget(val symbol: String, val displayName: String) {
    NONE("none", "None"),
    CONTROLLER_PAK("controller-pak", "Controller Pak"),
    RUMBLE_PAK("rumble-pak", "Rumble Pak"),
    TRANSFER_PAK("transfer-pak", "Transfer Pak"),
    NETWORK_PAK("network-pak", "Network Pak"),
}

enum class BridgeCarrierTarget(val symbol: String, val displayName: String) {
    TCP("tcp", "TCP"),
    RELIABLE_UDP_QUICK("reliable-udp-quick", "Reliable UDP Quick"),
}

enum class BridgeMutationKind(val route: String, val operationKind: String, val displayName: String) {
    FIRMWARE("/api/v1/firmware", "firmware-mode-switch", "Firmware"),
    ACCESSORY("/api/v1/accessory", "accessory-selection", "Accessory"),
    NETWORK_PAK_MODULE("/api/v1/network-pak/module", "network-pak-module-selection", "Network Pak module"),
    NETWORK_PAK_CARRIER("/api/v1/network-pak/carrier", "network-pak-carrier-selection", "Network Pak carrier"),
}

internal sealed class BridgeMutationCommand(
    val kind: BridgeMutationKind,
    val targetSymbol: String,
    val targetDisplayName: String,
) {
    internal class Firmware(val target: BridgeFirmwareTarget) : BridgeMutationCommand(
        BridgeMutationKind.FIRMWARE, target.symbol, target.displayName,
    )

    internal class Accessory(val target: BridgeAccessoryTarget) : BridgeMutationCommand(
        BridgeMutationKind.ACCESSORY, target.symbol, target.displayName,
    )

    internal class Module private constructor(val moduleId: String, displayName: String) : BridgeMutationCommand(
        BridgeMutationKind.NETWORK_PAK_MODULE, moduleId, displayName,
    ) {
        companion object {
            fun fromAuthoritative(module: BridgeModuleInfoDto): Module? {
                val id = module.id.trim()
                return if (id.isNotEmpty() && id.length <= 256 &&
                    '/' !in id && '\\' !in id && ':' !in id
                ) Module(id, module.displayName.ifBlank { id }) else null
            }
        }
    }

    internal class Carrier(val target: BridgeCarrierTarget) : BridgeMutationCommand(
        BridgeMutationKind.NETWORK_PAK_CARRIER, target.symbol, target.displayName,
    )

    fun isApplied(state: BridgeStateDto): Boolean = when (this) {
        is Firmware -> state.device.firmwarePersonality == target.symbol
        is Accessory -> state.device.activeAccessory == target.symbol
        is Module -> state.networkPakModule.activeModuleId == moduleId
        is Carrier -> state.device.selectedNetworkPakTransport == target.symbol
    }

    fun requestJson(): String = when (this) {
        is Firmware -> JSON.encodeToString(BridgeFirmwareMutationRequest(target.symbol))
        is Accessory -> JSON.encodeToString(BridgeAccessoryMutationRequest(target.symbol))
        is Module -> JSON.encodeToString(BridgeModuleMutationRequest(moduleId))
        is Carrier -> JSON.encodeToString(BridgeCarrierMutationRequest(target.symbol))
    }

    companion object {
        private val JSON = Json { encodeDefaults = true }
    }
}

enum class BridgeMutationPhase {
    IDLE,
    SUBMITTING,
    ACCEPTED,
    RECONCILING,
    SUCCEEDED,
    REJECTED,
    UNCERTAIN,
    FAILED,
}

enum class BridgeMutationFailureKind {
    INVALID_REQUEST,
    AUTHENTICATION,
    FORBIDDEN,
    NOT_FOUND,
    BUSY,
    SERVER,
    API_RESPONSE,
    CONTRACT,
    PROFILE_CHANGED,
    UNCERTAIN,
    OPERATION_FAILED,
}

data class BridgeMutationFailure(
    val kind: BridgeMutationFailureKind,
    val safeMessage: String,
) {
    override fun toString(): String = "BridgeMutationFailure(kind=$kind, message=$safeMessage)"
}

data class BridgeMutationPresentation(
    val phase: BridgeMutationPhase = BridgeMutationPhase.IDLE,
    val profileId: String? = null,
    val operationId: Long? = null,
    val kind: BridgeMutationKind? = null,
    val targetSymbol: String? = null,
    val targetDisplayName: String? = null,
    val failure: BridgeMutationFailure? = null,
) {
    val inFlight: Boolean
        get() = phase in setOf(
            BridgeMutationPhase.SUBMITTING,
            BridgeMutationPhase.ACCEPTED,
            BridgeMutationPhase.RECONCILING,
        )

    override fun toString(): String =
        "BridgeMutationPresentation(phase=$phase, profileId=$profileId, operationId=$operationId, kind=$kind, target=$targetSymbol, failure=${failure?.kind})"
}

internal fun shouldPublishMutation(
    activeProfileId: String?,
    value: BridgeMutationPresentation,
): Boolean = value.profileId == null || value.profileId == activeProfileId

internal sealed interface BridgeMutationSubmission {
    data class Accepted(val operationId: Long, val operationKind: String) : BridgeMutationSubmission
    data class Busy(val operationId: Long?, val operationKind: String?) : BridgeMutationSubmission
    data class Rejected(val failure: BridgeMutationFailure) : BridgeMutationSubmission
    data class Uncertain(val failure: BridgeMutationFailure) : BridgeMutationSubmission
}

internal fun interface BridgeMutationHttpExecutor {
    suspend fun execute(request: Request): BridgeHttpPayload
}

internal class OkHttpMutationExecutor(
    private val pinned: BridgePinnedHttpsClient,
) : BridgeMutationHttpExecutor {
    override suspend fun execute(request: Request): BridgeHttpPayload =
        withContext(Dispatchers.IO) {
            pinned.httpClient.newCall(request).execute().use { response: Response ->
                BridgeHttpPayload(response.code, response.body.string())
            }
        }
}

internal class BridgeSemanticMutationClient(
    private val baseUrl: HttpUrl,
    private val executor: BridgeMutationHttpExecutor,
    val profileId: String,
) {
    suspend fun submit(command: BridgeMutationCommand): BridgeMutationSubmission {
        val url = baseUrl.newBuilder().addPathSegments(command.kind.route.removePrefix("/")).build()
        val request = Request.Builder()
            .url(url)
            .post(command.requestJson().toRequestBody(JSON_MEDIA_TYPE))
            .build()
        return try {
            val response = executor.execute(request)
            when (response.statusCode) {
                202 -> {
                    val accepted = decode<BridgeMutationAcceptedDto>(response.body)
                    if (accepted.accepted && accepted.operationId > 0 &&
                        accepted.operationKind == command.kind.operationKind
                    ) BridgeMutationSubmission.Accepted(accepted.operationId, accepted.operationKind)
                    else BridgeMutationSubmission.Rejected(contractFailure())
                }
                400 -> BridgeMutationSubmission.Rejected(failure(
                    BridgeMutationFailureKind.INVALID_REQUEST,
                    "The Bridge rejected the semantic target.",
                ))
                401 -> BridgeMutationSubmission.Rejected(failure(
                    BridgeMutationFailureKind.AUTHENTICATION,
                    "Bridge authentication failed.",
                ))
                403 -> BridgeMutationSubmission.Rejected(failure(
                    BridgeMutationFailureKind.FORBIDDEN,
                    "The Bridge did not authorize this operation.",
                ))
                404 -> BridgeMutationSubmission.Rejected(failure(
                    BridgeMutationFailureKind.NOT_FOUND,
                    "The requested Bridge resource is unavailable.",
                ))
                409 -> {
                    val busy = runCatching { decode<BridgeMutationBusyDto>(response.body) }.getOrNull()
                    BridgeMutationSubmission.Busy(busy?.currentOperationId, busy?.currentOperationKind)
                }
                in 500..599 -> BridgeMutationSubmission.Rejected(failure(
                    BridgeMutationFailureKind.SERVER,
                    "The Bridge could not accept the operation.",
                ))
                else -> BridgeMutationSubmission.Rejected(failure(
                    BridgeMutationFailureKind.API_RESPONSE,
                    "The Bridge returned an unexpected response.",
                ))
            }
        } catch (_: CancellationException) {
            uncertainFailure()
        } catch (_: IOException) {
            uncertainFailure()
        } catch (_: Exception) {
            BridgeMutationSubmission.Rejected(contractFailure())
        }
    }

    suspend fun operation(): BridgeApiResult<BridgeOperationDto> = get("api", "v1", "operation")
    suspend fun state(): BridgeApiResult<BridgeStateDto> = get("api", "v1", "state")

    private suspend inline fun <reified T> get(vararg segments: String): BridgeApiResult<T> {
        val url = baseUrl.newBuilder().apply { segments.forEach(::addPathSegment) }.build()
        return try {
            val response = executor.execute(Request.Builder().url(url).get().build())
            if (response.statusCode !in 200..299) {
                BridgeApiResult.Failure(BridgeApiFailure(
                    BridgeApiFailureKind.API_RESPONSE,
                    "The Bridge reconciliation response was unavailable.",
                ))
            } else {
                BridgeApiResult.Success(decode(response.body))
            }
        } catch (_: Exception) {
            BridgeApiResult.Failure(BridgeApiFailure(
                BridgeApiFailureKind.UNREACHABLE,
                "The Bridge reconciliation response was unavailable.",
            ))
        }
    }

    private inline fun <reified T> decode(body: String): T =
        JSON.decodeFromString(serializer<T>(), body)

    private fun uncertainFailure() = BridgeMutationSubmission.Uncertain(failure(
        BridgeMutationFailureKind.UNCERTAIN,
        "The request outcome is uncertain. Refreshing authoritative Bridge state.",
    ))

    companion object {
        private val JSON = Json { ignoreUnknownKeys = false }
        private val JSON_MEDIA_TYPE = "application/json; charset=utf-8".toMediaType()
    }
}

internal fun interface BridgeSemanticMutationClientFactory {
    fun create(profile: BridgePairingProfile): BridgeApiResult<BridgeSemanticMutationClient>
}

internal object PinnedBridgeSemanticMutationClientFactory : BridgeSemanticMutationClientFactory {
    override fun create(profile: BridgePairingProfile): BridgeApiResult<BridgeSemanticMutationClient> =
        when (val pinned = BridgePinnedHttpsClientFactory.create(profile)) {
            is BridgePinnedClientResult.Success -> BridgeApiResult.Success(
                BridgeSemanticMutationClient(
                    pinned.value.apiBaseUrl.toHttpUrl(),
                    OkHttpMutationExecutor(pinned.value),
                    pinned.value.profileId,
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

internal fun interface BridgeMutationDelay {
    suspend fun wait(milliseconds: Long)
}

internal class BridgeSemanticMutationCoordinator(
    private val profiles: ActiveBridgeProfileSource,
    private val clients: BridgeSemanticMutationClientFactory =
        PinnedBridgeSemanticMutationClientFactory,
    private val reconciliationAttempts: Int = 10,
    private val reconciliationDelayMs: Long = 350,
    private val waiter: BridgeMutationDelay = BridgeMutationDelay { delay(it) },
) {
    init {
        require(reconciliationAttempts in 1..40)
        require(reconciliationDelayMs in 0..1_000)
    }

    suspend fun submit(
        expectedProfileId: String,
        command: BridgeMutationCommand,
        publish: (BridgeMutationPresentation) -> Unit = {},
    ): BridgeMutationPresentation {
        val session = session(expectedProfileId) ?: return profileChanged(command, publish)
        val client = session
        val preflight = client.operation()
        if (preflight is BridgeApiResult.Success && preflight.value.status == "running") {
            return finish(
                presentation(expectedProfileId, command, BridgeMutationPhase.REJECTED,
                    preflight.value.operationId, busyFailure()), publish,
            )
        }

        publish(presentation(expectedProfileId, command, BridgeMutationPhase.SUBMITTING))
        return when (val submitted = client.submit(command)) {
            is BridgeMutationSubmission.Accepted -> {
                val accepted = presentation(expectedProfileId, command,
                    BridgeMutationPhase.ACCEPTED, submitted.operationId)
                publish(accepted)
                reconcile(client, command, accepted, publish)
            }
            is BridgeMutationSubmission.Busy -> finish(
                presentation(expectedProfileId, command, BridgeMutationPhase.REJECTED,
                    submitted.operationId, busyFailure()), publish,
            )
            is BridgeMutationSubmission.Rejected -> finish(
                presentation(expectedProfileId, command, BridgeMutationPhase.REJECTED,
                    failure = submitted.failure), publish,
            )
            is BridgeMutationSubmission.Uncertain -> {
                val uncertain = presentation(expectedProfileId, command,
                    BridgeMutationPhase.UNCERTAIN, failure = submitted.failure)
                publish(uncertain)
                reconcile(client, command, uncertain, publish)
            }
        }
    }

    suspend fun resume(
        pending: BridgeMutationPresentation,
        publish: (BridgeMutationPresentation) -> Unit = {},
    ): BridgeMutationPresentation {
        if (pending.phase !in setOf(
                BridgeMutationPhase.ACCEPTED,
                BridgeMutationPhase.RECONCILING,
                BridgeMutationPhase.UNCERTAIN,
            ) || pending.profileId == null || pending.kind == null ||
            pending.targetSymbol == null
        ) return pending
        val command = restoreCommand(pending) ?: return pending
        val client = session(pending.profileId) ?: return profileChanged(command, publish)
        return reconcile(client, command, pending, publish)
    }

    private suspend fun reconcile(
        client: BridgeSemanticMutationClient,
        command: BridgeMutationCommand,
        pending: BridgeMutationPresentation,
        publish: (BridgeMutationPresentation) -> Unit,
    ): BridgeMutationPresentation {
        var operationId = pending.operationId
        publish(pending.copy(phase = BridgeMutationPhase.RECONCILING))
        repeat(reconciliationAttempts) { attempt ->
            val operation = (client.operation() as? BridgeApiResult.Success)?.value
            val state = (client.state() as? BridgeApiResult.Success)?.value
            if (operationId == null && operation?.kind == command.kind.operationKind) {
                operationId = operation.operationId.takeIf { it > 0 }
            }
            val applied = state?.let(command::isApplied) == true
            val correlated = operationId != null && operation?.operationId == operationId
            if (correlated && operation.status in setOf("failed", "cancelled")) {
                return finish(pending.copy(
                    phase = BridgeMutationPhase.FAILED,
                    operationId = operationId,
                    failure = failure(
                        BridgeMutationFailureKind.OPERATION_FAILED,
                        "The Bridge operation did not complete successfully.",
                    ),
                ), publish)
            }
            if (correlated && operation.status == "succeeded" && applied) {
                return finish(pending.copy(
                    phase = BridgeMutationPhase.SUCCEEDED,
                    operationId = operationId,
                    failure = null,
                ), publish)
            }
            if (pending.operationId == null && applied && operation?.status != "running") {
                return finish(pending.copy(
                    phase = BridgeMutationPhase.SUCCEEDED,
                    operationId = operationId,
                    failure = null,
                ), publish)
            }
            if (attempt + 1 < reconciliationAttempts) waiter.wait(reconciliationDelayMs)
        }
        return finish(pending.copy(
            phase = BridgeMutationPhase.UNCERTAIN,
            operationId = operationId,
            failure = failure(
                BridgeMutationFailureKind.UNCERTAIN,
                "The operation is still unresolved. Use Refresh to reconcile again.",
            ),
        ), publish)
    }

    private suspend fun session(expectedProfileId: String): BridgeSemanticMutationClient? {
        val active = profiles.load() as? ActiveBridgeProfileResult.Success ?: return null
        if (active.profile.metadata.profileId != expectedProfileId) return null
        val created = clients.create(active.profile) as? BridgeApiResult.Success ?: return null
        return created.value.takeIf { it.profileId == expectedProfileId }
    }

    private fun restoreCommand(pending: BridgeMutationPresentation): BridgeMutationCommand? =
        when (pending.kind) {
            BridgeMutationKind.FIRMWARE -> BridgeFirmwareTarget.entries
                .firstOrNull { it.symbol == pending.targetSymbol }?.let(BridgeMutationCommand::Firmware)
            BridgeMutationKind.ACCESSORY -> BridgeAccessoryTarget.entries
                .firstOrNull { it.symbol == pending.targetSymbol }?.let(BridgeMutationCommand::Accessory)
            BridgeMutationKind.NETWORK_PAK_CARRIER -> BridgeCarrierTarget.entries
                .firstOrNull { it.symbol == pending.targetSymbol }?.let(BridgeMutationCommand::Carrier)
            BridgeMutationKind.NETWORK_PAK_MODULE -> pending.targetSymbol?.let {
                BridgeMutationCommand.Module.fromAuthoritative(
                    BridgeModuleInfoDto(it, pending.targetDisplayName ?: it),
                )
            }
            null -> null
        }

    private fun profileChanged(
        command: BridgeMutationCommand,
        publish: (BridgeMutationPresentation) -> Unit,
    ) = finish(
        presentation(null, command, BridgeMutationPhase.FAILED, failure = failure(
            BridgeMutationFailureKind.PROFILE_CHANGED,
            "The active Bridge profile changed. No command was sent.",
        )), publish,
    )

    private fun presentation(
        profileId: String?,
        command: BridgeMutationCommand,
        phase: BridgeMutationPhase,
        operationId: Long? = null,
        failure: BridgeMutationFailure? = null,
    ) = BridgeMutationPresentation(
        phase, profileId, operationId, command.kind, command.targetSymbol,
        command.targetDisplayName, failure,
    )

    private fun finish(
        value: BridgeMutationPresentation,
        publish: (BridgeMutationPresentation) -> Unit,
    ): BridgeMutationPresentation {
        publish(value)
        return value
    }
}

private fun failure(kind: BridgeMutationFailureKind, message: String) =
    BridgeMutationFailure(kind, message)

private fun busyFailure() = failure(
    BridgeMutationFailureKind.BUSY,
    "Another Bridge operation is already in progress.",
)

private fun contractFailure() = failure(
    BridgeMutationFailureKind.CONTRACT,
    "The Bridge mutation response did not match API v1.",
)
