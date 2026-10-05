package com.melfegor.nice64.api

import kotlinx.serialization.Serializable

@Serializable data class BridgeHealthDto(val apiVersion: String, val status: String)
@Serializable data class BridgeErrorDto(val code: String, val detail: String)

@Serializable
data class BridgeStateDto(
    val apiVersion: String,
    val captureSequence: Long,
    val capturedUtc: String,
    val bridge: BridgeIdentityDto,
    val device: BridgeDeviceDto,
    val controller: BridgeControllerStateDto,
    val controllerPak: BridgeControllerPakDto,
    val transferPak: BridgeTransferPakDto,
    val persistence: BridgePersistenceDto,
    val networkPakModule: BridgeModuleDto,
    val wifi: BridgeWifiDto,
    val operation: BridgeOperationDto,
)

@Serializable data class BridgeIdentityDto(val instanceId: String, val name: String)
@Serializable data class BridgeTransitionDto(val phase: String, val target: String, val failure: BridgeErrorDto?)
@Serializable data class BridgeCapabilityDto(val id: String, val maximumLogicalBlockBytes: Int?, val preferredLogicalBlockBytes: Int?)
@Serializable
data class BridgeDeviceDto(
    val connected: Boolean,
    val connectionState: String,
    val connectionKind: String,
    val firmwarePersonality: String,
    val deviceRole: String,
    val deviceService: String,
    val firmwareTransition: BridgeTransitionDto,
    val activeAccessory: String,
    val accessoryTransition: BridgeTransitionDto,
    val selectedNetworkPakTransport: String,
    val activeNetworkPakTransport: String,
    val effectiveCapabilities: List<BridgeCapabilityDto>,
)

@Serializable data class BridgeControllerInputDto(val controllerType: String, val buttons: Long, val analogX: Int, val analogY: Int, val analogActive: Boolean, val neutral: Boolean)
@Serializable data class BridgeControllerStateDto(val lastRequestedState: BridgeControllerInputDto, val lastDeliveredState: BridgeControllerInputDto?, val deliverySuspended: Boolean, val deliveryPending: Boolean)
@Serializable data class BridgeControllerPakDto(val contentPresent: Boolean, val hasAssociation: Boolean, val associatedFileName: String?, val imageSize: Int, val lastSuccessfulOperation: String)
@Serializable data class BridgeGameBoyMetadataDto(val title: String, val cartType: Int, val cartTypeName: String, val mapper: String, val decodedRomSize: Int, val decodedRamSize: Int, val rtcRequired: Boolean)
@Serializable data class BridgeTransferPakDto(val contentPresent: Boolean, val hasAssociatedRom: Boolean, val hasAssociatedSave: Boolean, val romFileName: String?, val saveFileName: String?, val installKind: String, val romLength: Int, val expectedSaveLength: Int, val metadata: BridgeGameBoyMetadataDto?, val lastSuccessfulOperation: String)
@Serializable data class BridgePersistenceDto(val kind: String, val active: Boolean, val dirty: Boolean, val flushInProgress: Boolean, val expectedLength: Int, val dirtyGeneration: Long, val lastSaveUtc: String?, val associatedFileName: String?)
@Serializable data class BridgeModuleInfoDto(val id: String, val displayName: String)
@Serializable data class BridgeModuleDto(val activeModuleId: String, val activeModuleName: String, val availableModules: List<BridgeModuleInfoDto>, val persistenceState: String, val lastSelectionOutcome: String)
@Serializable data class BridgeWifiDto(val deviceStatusKnown: Boolean, val configured: Boolean, val connected: Boolean, val state: String?, val mode: String?, val link: String?, val lastOperation: String, val lastOperationOutcome: String)
@Serializable data class BridgeOperationProgressDto(val phase: String, val completedUnits: Long?, val totalUnits: Long?, val itemIndex: Int?, val itemCount: Int?, val unit: String?, val timestampUtc: String)
@Serializable data class BridgeOperationDto(val operationId: Long, val kind: String, val status: String, val startedUtc: String?, val completedUtc: String?, val cancellationCapability: String, val cancellationRequested: Boolean, val domain: String, val failureCode: String?, val safeDetail: String?, val progress: BridgeOperationProgressDto?)

@Serializable data class BridgeHistoryItemDto(val itemId: String, val fileName: String, val lastUsedUtc: String, val available: Boolean)
@Serializable data class BridgeHistoryDto(val apiVersion: String, val schemaVersion: Int, val controllerPak: List<BridgeHistoryItemDto>, val transferPak: List<BridgeHistoryItemDto>)

@Serializable data class BridgeSessionDeviceDto(val identity: String, val firmwarePersonality: String, val accessory: String)
@Serializable data class BridgeSessionDto(val sessionId: String, val state: String, val createdUtc: String, val lastConnectedUtc: String, val connectionKind: String, val device: BridgeSessionDeviceDto)
@Serializable data class BridgeSessionsDto(val apiVersion: String, val sessions: List<BridgeSessionDto>)

@Serializable data class BridgeControllerEndpointDto(val available: Boolean, val unavailableReason: String?, val address: String?, val port: Int?, val transport: String?, val protocol: String?, val controllerType: String?)
@Serializable data class BridgeControllerPrimaryDto(
    val apiVersion: String,
    val sessionId: String,
    val primary: String,
    val endpoint: BridgeControllerEndpointDto,
)
@Serializable internal data class BridgeControllerPrimaryRequest(val primary: String)

@Serializable internal data class BridgeFirmwareMutationRequest(val personality: String)
@Serializable internal data class BridgeAccessoryMutationRequest(val accessory: String)
@Serializable internal data class BridgeModuleMutationRequest(val moduleId: String)
@Serializable internal data class BridgeCarrierMutationRequest(val carrier: String)
@Serializable internal data class BridgeMutationAcceptedDto(val accepted: Boolean, val operationId: Long, val operationKind: String)
@Serializable internal data class BridgeMutationBusyDto(val code: String, val currentOperationId: Long, val currentOperationKind: String)
@Serializable internal data class BridgeMutationRejectedDto(val code: String, val detail: String)
@Serializable internal data class BridgeHistoryActivationRequest(val destructiveInstallConfirmed: Boolean)
@Serializable internal data class BridgeHistoryActivationConfirmationDto(
    val category: String,
    val fileName: String,
    val contentKind: String,
    val title: String? = null,
)
@Serializable internal data class BridgeHistoryActivationResponseDto(
    val accepted: Boolean,
    val operationId: Long? = null,
    val operationKind: String? = null,
    val currentOperationId: Long? = null,
    val currentOperationKind: String? = null,
    val code: String? = null,
    val detail: String? = null,
    val confirmation: BridgeHistoryActivationConfirmationDto? = null,
)

enum class BridgeApiFailureKind {
    UNPAIRED,
    PROFILE_CREDENTIAL_UNAVAILABLE,
    TLS_IDENTITY,
    AUTHENTICATION,
    UNREACHABLE,
    NOT_FOUND,
    CONFLICT,
    SERVER,
    API_RESPONSE,
    CONTRACT,
    INCOMPATIBLE_API,
}

data class BridgeApiFailure(val kind: BridgeApiFailureKind, val safeMessage: String) {
    override fun toString(): String = "BridgeApiFailure(kind=$kind, message=$safeMessage)"
}

sealed interface BridgeApiResult<out T> {
    data class Success<T>(val value: T) : BridgeApiResult<T>
    data class Failure(val failure: BridgeApiFailure) : BridgeApiResult<Nothing>
}
