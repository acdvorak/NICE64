package com.melfegor.nice64.core.model

data class BridgeSessionDevice(
    val identity: String,
    val firmwarePersonality: String,
    val accessory: String,
)

enum class BridgeSessionStatus {
    CONNECTED,
    TEMPORARILY_UNAVAILABLE,
    UNKNOWN,
}

enum class BridgeConnectionKind {
    USB,
    WIFI_TCP,
    UNKNOWN,
}

data class BridgeSession(
    val sessionId: String,
    val status: BridgeSessionStatus,
    val createdAtEpochMillis: Long,
    val lastConnectedAtEpochMillis: Long,
    val connectionKind: BridgeConnectionKind,
    val device: BridgeSessionDevice,
)

sealed interface BridgeSessionViewState {
    data object NoSession : BridgeSessionViewState
    data class Connected(val session: BridgeSession) : BridgeSessionViewState
    data class TemporarilyUnavailable(val session: BridgeSession) : BridgeSessionViewState
    data class Error(val safeReason: String) : BridgeSessionViewState
}

enum class BridgeOperationStatus {
    NONE,
    RUNNING,
    SUCCEEDED,
    FAILED,
    CANCELLED,
    UNKNOWN,
}

data class BridgeOperationProgress(
    val phase: String,
    val completedUnits: Long?,
    val totalUnits: Long?,
    val itemIndex: Int?,
    val itemCount: Int?,
    val unit: String?,
    val timestampEpochMillis: Long,
)

data class BridgeOperation(
    val operationId: Long,
    val kind: String,
    val status: BridgeOperationStatus,
    val startedAtEpochMillis: Long?,
    val completedAtEpochMillis: Long?,
    val cancellationCapability: String,
    val cancellationRequested: Boolean,
    val domain: String,
    val failureCode: String?,
    val safeDetail: String?,
    val progress: BridgeOperationProgress?,
)

enum class HistoryCategory {
    CONTROLLER_PAK,
    TRANSFER_PAK,
}

/** API-safe History item. A desktop path is intentionally impossible to represent. */
data class HistoryItem(
    val itemId: String,
    val fileName: String,
    val lastUsedAtEpochMillis: Long,
    val available: Boolean,
    val category: HistoryCategory,
)

data class HistorySnapshot(
    val schemaVersion: Int,
    val controllerPak: List<HistoryItem>,
    val transferPak: List<HistoryItem>,
)

data class ControllerEndpointRuntime(
    val sessionId: String,
    val available: Boolean,
    val unavailableReason: String?,
    val address: String?,
    val port: Int?,
    val transport: String?,
    val protocol: String?,
    val controllerType: String?,
)

data class Nice64AppState(
    val pairing: PairingState = PairingState.Unpaired,
    val session: BridgeSessionViewState = BridgeSessionViewState.NoSession,
    val operation: BridgeOperation? = null,
    val history: HistorySnapshot? = null,
    val controllerEndpoint: ControllerEndpointRuntime? = null,
)
