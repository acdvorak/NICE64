package com.melfegor.nice64.api

enum class BridgeConnectionPresentation {
    UNPAIRED,
    CONNECTING,
    ONLINE,
    NO_SESSION,
    OFFLINE,
    TLS_IDENTITY_ERROR,
    AUTHENTICATION_ERROR,
    API_ERROR,
    INCOMPATIBLE_API,
}

data class BridgeSection<T>(
    val value: T? = null,
    val failure: BridgeApiFailure? = null,
)

data class BridgeDashboardSnapshot(
    val connection: BridgeConnectionPresentation = BridgeConnectionPresentation.UNPAIRED,
    val profileId: String? = null,
    val endpoint: String? = null,
    val refreshing: Boolean = false,
    val health: BridgeSection<BridgeHealthDto> = BridgeSection(),
    val state: BridgeSection<BridgeStateDto> = BridgeSection(),
    val operation: BridgeSection<BridgeOperationDto> = BridgeSection(),
    val history: BridgeSection<BridgeHistoryDto> = BridgeSection(),
    val sessions: BridgeSection<BridgeSessionsDto> = BridgeSection(),
    val currentSession: BridgeSection<BridgeSessionDto> = BridgeSection(),
    val controller: BridgeSection<BridgeControllerPrimaryDto> = BridgeSection(),
) {
    override fun toString(): String =
        "BridgeDashboardSnapshot(connection=$connection, profileId=$profileId, endpoint=$endpoint, refreshing=$refreshing)"
}

internal class BridgeDashboardRepository(
    private val profiles: ActiveBridgeProfileSource,
    private val clients: BridgeReadOnlyClientFactory = PinnedBridgeReadOnlyClientFactory,
) {
    suspend fun refresh(previous: BridgeDashboardSnapshot = BridgeDashboardSnapshot()): BridgeDashboardSnapshot {
        val active = profiles.load()
        if (active is ActiveBridgeProfileResult.Unpaired) return BridgeDashboardSnapshot()
        if (active is ActiveBridgeProfileResult.CredentialUnavailable) {
            return previous.copy(
                connection = BridgeConnectionPresentation.API_ERROR,
                refreshing = false,
                health = previous.health.failed(
                    BridgeApiFailure(BridgeApiFailureKind.PROFILE_CREDENTIAL_UNAVAILABLE, "The paired Bridge credential is unavailable."),
                ),
            )
        }
        val profile = (active as ActiveBridgeProfileResult.Success).profile
        val created = clients.create(profile)
        if (created is BridgeApiResult.Failure) {
            return previous.copy(
                connection = BridgeConnectionPresentation.TLS_IDENTITY_ERROR,
                profileId = profile.metadata.profileId,
                endpoint = profile.metadata.apiBaseUrl,
                refreshing = false,
                health = previous.health.failed(created.failure),
            )
        }
        val client = (created as BridgeApiResult.Success).value
        val health = merge(previous.health, client.health())
        val state = merge(previous.state, client.state())
        val operation = merge(previous.operation, client.operation())
        val history = merge(previous.history, client.history())
        val sessions = merge(previous.sessions, client.sessions())
        val currentId = sessions.value?.sessions?.singleOrNull()?.sessionId
        val currentSession = if (currentId == null) BridgeSection() else merge(previous.currentSession, client.session(currentId))
        val controller = if (currentId == null) BridgeSection() else merge(previous.controller, client.controller(currentId))
        return BridgeDashboardSnapshot(
            connection = connection(health, sessions),
            profileId = profile.metadata.profileId,
            endpoint = profile.metadata.apiBaseUrl,
            refreshing = false,
            health = health,
            state = state,
            operation = operation,
            history = history,
            sessions = sessions,
            currentSession = currentSession,
            controller = controller,
        )
    }

    private fun connection(
        health: BridgeSection<BridgeHealthDto>,
        sessions: BridgeSection<BridgeSessionsDto>,
    ): BridgeConnectionPresentation {
        val failure = health.failure
        if (failure != null) return when (failure.kind) {
            BridgeApiFailureKind.TLS_IDENTITY -> BridgeConnectionPresentation.TLS_IDENTITY_ERROR
            BridgeApiFailureKind.AUTHENTICATION -> BridgeConnectionPresentation.AUTHENTICATION_ERROR
            BridgeApiFailureKind.UNREACHABLE -> BridgeConnectionPresentation.OFFLINE
            BridgeApiFailureKind.INCOMPATIBLE_API -> BridgeConnectionPresentation.INCOMPATIBLE_API
            else -> BridgeConnectionPresentation.API_ERROR
        }
        return if (sessions.value?.sessions?.isEmpty() == true) {
            BridgeConnectionPresentation.NO_SESSION
        } else {
            BridgeConnectionPresentation.ONLINE
        }
    }

    private fun <T> merge(previous: BridgeSection<T>, result: BridgeApiResult<T>): BridgeSection<T> =
        when (result) {
            is BridgeApiResult.Success -> BridgeSection(value = result.value)
            is BridgeApiResult.Failure -> previous.failed(result.failure)
        }

    private fun <T> BridgeSection<T>.failed(error: BridgeApiFailure): BridgeSection<T> =
        copy(failure = error)
}
