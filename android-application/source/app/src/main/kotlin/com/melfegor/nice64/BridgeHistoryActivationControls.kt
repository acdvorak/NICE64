package com.melfegor.nice64

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import com.melfegor.nice64.api.BridgeDashboardSnapshot
import com.melfegor.nice64.api.BridgeHistoryActivationPhase
import com.melfegor.nice64.api.BridgeHistoryActivationPresentation
import com.melfegor.nice64.api.BridgeHistoryCategory
import com.melfegor.nice64.api.BridgeHistoryItemDto
import com.melfegor.nice64.ui.components.Nice64EmptyState
import com.melfegor.nice64.ui.components.Nice64MetadataRow
import com.melfegor.nice64.ui.components.Nice64OperationStatus
import com.melfegor.nice64.ui.components.Nice64StatusBanner
import com.melfegor.nice64.ui.components.Nice64StatusChip
import com.melfegor.nice64.ui.components.Nice64StatusTone
import com.melfegor.nice64.ui.theme.Nice64Spacing
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Locale

data class BridgeHistoryActivationActions(
    val activate: (BridgeHistoryCategory, BridgeHistoryItemDto, Boolean) -> Unit = { _, _, _ -> },
)

private data class PendingHistoryActivation(
    val category: BridgeHistoryCategory,
    val item: BridgeHistoryItemDto,
)

@Composable
internal fun BridgeHistoryActivationControls(
    dashboard: BridgeDashboardSnapshot,
    activation: BridgeHistoryActivationPresentation,
    semanticMutationBusy: Boolean,
    actions: BridgeHistoryActivationActions,
) {
    val history = dashboard.history.value ?: return
    val hasSession = dashboard.currentSession.value?.state == "connected"
    val bridgeBusy = dashboard.operation.value?.status == "running"
    val enabled = hasSession && !bridgeBusy && !semanticMutationBusy && !activation.inFlight
    var pending by remember { mutableStateOf<PendingHistoryActivation?>(null) }

    Text("History", style = MaterialTheme.typography.titleMedium)
    if (history.controllerPak.isEmpty() && history.transferPak.isEmpty()) {
        Nice64EmptyState(
            title = "No History items",
            message = "Recently used Controller Pak and Transfer Pak content will appear here.",
        )
    }
    history.controllerPak.forEach { item ->
        HistoryRow(BridgeHistoryCategory.CONTROLLER_PAK, item, enabled) {
            pending = PendingHistoryActivation(BridgeHistoryCategory.CONTROLLER_PAK, item)
        }
    }
    history.transferPak.forEach { item ->
        HistoryRow(BridgeHistoryCategory.TRANSFER_PAK, item, enabled) {
            pending = PendingHistoryActivation(BridgeHistoryCategory.TRANSFER_PAK, item)
        }
    }

    when (activation.phase) {
        BridgeHistoryActivationPhase.SUBMITTING,
        BridgeHistoryActivationPhase.ACCEPTED,
        BridgeHistoryActivationPhase.RECONCILING -> Nice64OperationStatus(
            label = "Loading ${activation.fileName ?: "History item"}…",
            modifier = Modifier.semantics { contentDescription = "History activation in progress" },
        )
        BridgeHistoryActivationPhase.SUCCEEDED -> Nice64StatusChip(
            text = "History item activated",
            tone = Nice64StatusTone.HEALTHY,
        )
        BridgeHistoryActivationPhase.REJECTED,
        BridgeHistoryActivationPhase.FAILED,
        BridgeHistoryActivationPhase.UNCERTAIN -> Nice64StatusBanner(
            title = "History activation not confirmed",
            message = activation.failure?.safeMessage ?: "History activation could not be confirmed.",
            tone = Nice64StatusTone.ERROR,
        )
        BridgeHistoryActivationPhase.IDLE -> Unit
    }

    pending?.let { choice ->
        val transfer = choice.category == BridgeHistoryCategory.TRANSFER_PAK
        AlertDialog(
            onDismissRequest = { pending = null },
            title = { Text("Activate ${choice.category.displayName} History?") },
            text = {
                Text(if (transfer) {
                    "This may replace the currently associated cartridge/ROM state. The Bridge will enforce persistence and transition safety."
                } else {
                    "The Bridge will activate this Controller Pak image using its existing safety checks."
                })
            },
            confirmButton = {
                TextButton(onClick = {
                    pending = null
                    actions.activate(choice.category, choice.item, transfer)
                }) { Text(if (transfer) "Confirm and activate" else "Activate") }
            },
            dismissButton = {
                TextButton(onClick = { pending = null }) { Text("Cancel") }
            },
        )
    }
}

@Composable
private fun HistoryRow(
    category: BridgeHistoryCategory,
    item: BridgeHistoryItemDto,
    enabled: Boolean,
    onActivate: () -> Unit,
) {
    Card(Modifier.fillMaxWidth()) {
        Column(
            Modifier.fillMaxWidth().padding(Nice64Spacing.lg),
            verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
        ) {
            Text(item.fileName, style = MaterialTheme.typography.titleMedium)
            Nice64MetadataRow("Content type", category.displayName)
            Nice64MetadataRow("Last used", formatHistoryTimestamp(item.lastUsedUtc))
            Nice64StatusChip(
                text = if (item.available) "Available" else "Unavailable",
                tone = if (item.available) Nice64StatusTone.HEALTHY else Nice64StatusTone.UNAVAILABLE,
            )
            Button(
                onClick = onActivate,
                enabled = enabled && item.available,
                modifier = Modifier.fillMaxWidth().semantics {
                    contentDescription = "Activate ${category.displayName} ${item.fileName}"
                },
            ) { Text("Activate ${category.displayName}") }
        }
    }
}

internal fun formatHistoryTimestamp(
    value: String,
    zoneId: ZoneId = ZoneId.systemDefault(),
    locale: Locale = Locale.getDefault(),
): String = runCatching {
    DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM)
        .withLocale(locale)
        .withZone(zoneId)
        .format(Instant.parse(value))
}.getOrElse { value }
