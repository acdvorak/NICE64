package com.melfegor.nice64

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import com.melfegor.nice64.api.BridgeConnectionPresentation
import com.melfegor.nice64.api.BridgeDashboardSnapshot
import com.melfegor.nice64.ui.components.Nice64MetadataRow
import com.melfegor.nice64.ui.components.Nice64OperationStatus
import com.melfegor.nice64.ui.components.Nice64StatusBanner
import com.melfegor.nice64.ui.components.Nice64StatusChip
import com.melfegor.nice64.ui.components.Nice64StatusTone
import com.melfegor.nice64.ui.theme.Nice64Spacing

internal enum class BridgeRemoteSection(val label: String) {
    BRIDGE("Bridge"),
    HISTORY("History"),
    CONTROLLERS("Controllers"),
}

@Composable
internal fun BridgeSectionNavigation(
    selectedSection: BridgeRemoteSection,
    onSelect: (BridgeRemoteSection) -> Unit,
) {
    PrimaryTabRow(
        selectedTabIndex = selectedSection.ordinal,
        modifier = Modifier.fillMaxWidth(),
    ) {
        BridgeRemoteSection.entries.forEach { section ->
            val selected = section == selectedSection
            Tab(
                selected = selected,
                onClick = { onSelect(section) },
                modifier = Modifier.semantics { this.selected = selected },
                text = { Text(section.label) },
            )
        }
    }
}

@Composable
internal fun BridgeSummary(dashboard: BridgeDashboardSnapshot) {
    Column(
        modifier = Modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
    ) {
        Text("Bridge status", style = MaterialTheme.typography.titleMedium)
        Nice64StatusChip(
            text = dashboard.connection.summaryText(),
            tone = dashboard.connection.statusTone(),
        )

        if (dashboard.refreshing) {
            Nice64OperationStatus(label = "Refreshing Bridge state")
        }

        dashboard.state.value?.device?.let { device ->
            Nice64StatusChip(
                text = if (device.connected) "Pico connected" else "Pico disconnected",
                tone = if (device.connected) Nice64StatusTone.HEALTHY else Nice64StatusTone.UNAVAILABLE,
            )
        }

        val session = dashboard.currentSession.value
        Nice64StatusChip(
            text = when {
                session == null -> "No active Bridge session"
                session.state == "connected" -> "Bridge session connected"
                else -> "Bridge session: ${session.state}"
            },
            tone = when {
                session?.state == "connected" -> Nice64StatusTone.HEALTHY
                dashboard.connection == BridgeConnectionPresentation.CONNECTING ->
                    Nice64StatusTone.IN_PROGRESS
                else -> Nice64StatusTone.UNAVAILABLE
            },
        )

        dashboard.operation.value?.takeIf { it.operationId != 0L && it.status != "idle" }
            ?.let { operation ->
                val progress = operation.progress
                val total = progress?.totalUnits
                val completed = progress?.completedUnits
                val ratio = if (completed != null && total != null && total > 0) {
                    completed.toFloat() / total.toFloat()
                } else null
                val units = if (completed != null && total != null) {
                    " $completed/$total${progress.unit?.let { " $it" } ?: ""}"
                } else ""
                Nice64OperationStatus(
                    label = "Operation: ${operation.kind} — ${operation.status}",
                    detail = progress?.let { "Progress: ${it.phase}$units" },
                    progress = ratio,
                )
            }

        dashboard.failures().forEach { failure ->
            Nice64StatusBanner(
                title = "Bridge information unavailable",
                tone = Nice64StatusTone.ERROR,
                message = failure,
            )
        }
    }
}

@Composable
internal fun BridgeTechnicalDetails(dashboard: BridgeDashboardSnapshot) {
    val session = dashboard.currentSession.value
    Column(
        modifier = Modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(Nice64Spacing.xs),
    ) {
        Text("Technical details", style = MaterialTheme.typography.titleSmall)
        dashboard.profileId?.let { Nice64MetadataRow("Profile ID", it) }
        dashboard.endpoint?.let { Nice64MetadataRow("Endpoint", it) }
        dashboard.health.value?.let {
            Nice64MetadataRow("API", "${it.status} (${it.apiVersion})")
        }
        dashboard.state.value?.device?.let { device ->
            Nice64MetadataRow("Pico", if (device.connected) "Connected" else "Disconnected")
            Nice64MetadataRow("Transport", device.connectionKind)
            Nice64MetadataRow("Firmware", device.firmwarePersonality)
            Nice64MetadataRow("Accessory", device.activeAccessory)
        }
        session?.let {
            Nice64MetadataRow("Session", "${it.state} (${it.connectionKind})")
            Nice64MetadataRow("Session ID", it.sessionId)
        }
        dashboard.operation.value?.takeIf { it.operationId == 0L || it.status == "idle" }
            ?.let { Nice64MetadataRow("Operation", "None") }
        dashboard.history.value?.let {
            Nice64MetadataRow("History items", "${it.controllerPak.size + it.transferPak.size} item(s)")
        }
        dashboard.controller.value?.let { controller ->
            Nice64MetadataRow("Controller authority", controller.primary)
            Nice64MetadataRow(
                "Controller connection/status",
                if (controller.endpoint.available) {
                    "${controller.endpoint.transport ?: "Available"}${controller.endpoint.address?.let { address -> " at $address:${controller.endpoint.port}" } ?: ""}"
                } else {
                    "Unavailable${controller.endpoint.unavailableReason?.let { " — $it" } ?: ""}"
                },
            )
        }
    }
}

private fun BridgeDashboardSnapshot.failures(): List<String> =
    listOf(health, state, operation, history, sessions, currentSession, controller)
        .mapNotNull { it.failure?.safeMessage }
        .distinct()

internal fun BridgeConnectionPresentation.summaryText(): String = when (this) {
    BridgeConnectionPresentation.UNPAIRED -> "No paired Bridge"
    BridgeConnectionPresentation.CONNECTING -> "Connecting to Bridge"
    BridgeConnectionPresentation.ONLINE -> "Bridge online"
    BridgeConnectionPresentation.NO_SESSION -> "Bridge online — no active session"
    BridgeConnectionPresentation.OFFLINE -> "Bridge offline"
    BridgeConnectionPresentation.TLS_IDENTITY_ERROR -> "Bridge identity error"
    BridgeConnectionPresentation.AUTHENTICATION_ERROR -> "Authentication error"
    BridgeConnectionPresentation.API_ERROR -> "API unavailable"
    BridgeConnectionPresentation.INCOMPATIBLE_API -> "Incompatible API"
}

private fun BridgeConnectionPresentation.statusTone(): Nice64StatusTone = when (this) {
    BridgeConnectionPresentation.ONLINE -> Nice64StatusTone.HEALTHY
    BridgeConnectionPresentation.CONNECTING -> Nice64StatusTone.IN_PROGRESS
    BridgeConnectionPresentation.NO_SESSION -> Nice64StatusTone.WARNING
    BridgeConnectionPresentation.UNPAIRED,
    BridgeConnectionPresentation.OFFLINE -> Nice64StatusTone.UNAVAILABLE
    BridgeConnectionPresentation.TLS_IDENTITY_ERROR,
    BridgeConnectionPresentation.AUTHENTICATION_ERROR,
    BridgeConnectionPresentation.API_ERROR,
    BridgeConnectionPresentation.INCOMPATIBLE_API -> Nice64StatusTone.ERROR
}
