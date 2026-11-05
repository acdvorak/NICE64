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
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import com.melfegor.nice64.api.BridgeAccessoryTarget
import com.melfegor.nice64.api.BridgeCarrierTarget
import com.melfegor.nice64.api.BridgeDashboardSnapshot
import com.melfegor.nice64.api.BridgeFirmwareTarget
import com.melfegor.nice64.api.BridgeModuleInfoDto
import com.melfegor.nice64.api.BridgeMutationPhase
import com.melfegor.nice64.api.BridgeMutationPresentation
import com.melfegor.nice64.ui.components.Nice64OperationStatus
import com.melfegor.nice64.ui.components.Nice64StatusBanner
import com.melfegor.nice64.ui.components.Nice64StatusChip
import com.melfegor.nice64.ui.components.Nice64StatusTone
import com.melfegor.nice64.ui.theme.Nice64Spacing

data class BridgeMutationActions(
    val firmware: (BridgeFirmwareTarget) -> Unit = {},
    val accessory: (BridgeAccessoryTarget) -> Unit = {},
    val module: (BridgeModuleInfoDto) -> Unit = {},
    val carrier: (BridgeCarrierTarget) -> Unit = {},
)

private enum class MutationPicker { ACCESSORY }

@Composable
internal fun BridgeMutationControls(
    dashboard: BridgeDashboardSnapshot,
    mutation: BridgeMutationPresentation,
    actions: BridgeMutationActions,
    additionalBusy: Boolean = false,
) {
    val state = dashboard.state.value ?: return
    var picker by remember { mutableStateOf<MutationPicker?>(null) }
    val bridgeBusy = dashboard.operation.value?.status == "running"
    val enabled = state.device.connected && !bridgeBusy && !mutation.inFlight && !additionalBusy
    val accessoryTargets = BridgeAccessoryTarget.entries.filterNot {
        it == BridgeAccessoryTarget.RUMBLE_PAK
    }

    Card(Modifier.fillMaxWidth()) {
        Column(
            modifier = Modifier.padding(Nice64Spacing.lg),
            verticalArrangement = Arrangement.spacedBy(Nice64Spacing.md),
        ) {
            Text("Accessories and Modes", style = MaterialTheme.typography.titleMedium)
            BridgeControlRow(
                label = "Accessory",
                current = state.device.activeAccessory,
                actionLabel = "Select Accessory",
                enabled = enabled,
            ) { picker = MutationPicker.ACCESSORY }

            when (mutation.phase) {
                BridgeMutationPhase.SUBMITTING -> Nice64OperationStatus(
                    "Submitting ${mutation.kind?.displayName ?: "operation"}…",
                )
                BridgeMutationPhase.ACCEPTED,
                BridgeMutationPhase.RECONCILING -> Nice64OperationStatus(
                    "Reconciling authoritative Bridge state…",
                )
                BridgeMutationPhase.SUCCEEDED -> Nice64StatusChip(
                    "${mutation.kind?.displayName ?: "Operation"} updated",
                    Nice64StatusTone.HEALTHY,
                )
                BridgeMutationPhase.REJECTED,
                BridgeMutationPhase.FAILED,
                BridgeMutationPhase.UNCERTAIN -> Nice64StatusBanner(
                    title = "Bridge operation not confirmed",
                    tone = Nice64StatusTone.ERROR,
                    message = mutation.failure?.safeMessage ?: "The Bridge operation could not be confirmed.",
                )
                BridgeMutationPhase.IDLE -> Unit
            }
        }
    }

    when (picker) {
        MutationPicker.ACCESSORY -> ChoiceDialog(
            "Select Accessory",
            accessoryTargets.map {
                ChoiceDialogItem(
                    label = it.displayName,
                    selected = it.symbol == state.device.activeAccessory,
                    action = { actions.accessory(it) },
                )
            },
        ) { picker = null }
        null -> Unit
    }
}

@Composable
private fun BridgeControlRow(
    label: String,
    current: String,
    actionLabel: String,
    enabled: Boolean,
    onClick: () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(Nice64Spacing.xs)) {
        Text(label, style = MaterialTheme.typography.titleSmall)
        Text(
            "Current: $current",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        MutationButton(actionLabel, enabled, onClick)
    }
}

@Composable
private fun MutationButton(label: String, enabled: Boolean, onClick: () -> Unit) {
    Button(onClick = onClick, enabled = enabled, modifier = Modifier.fillMaxWidth()) {
        Text(label)
    }
}

private data class ChoiceDialogItem(
    val label: String,
    val selected: Boolean,
    val action: () -> Unit,
)

@Composable
private fun ChoiceDialog(
    title: String,
    choices: List<ChoiceDialogItem>,
    dismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text(title) },
        text = {
            Column {
                choices.forEach { choice ->
                    TextButton(
                        onClick = { dismiss(); choice.action() },
                        modifier = Modifier.fillMaxWidth().semantics {
                            selected = choice.selected
                        },
                    ) {
                        Text(choice.label)
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = dismiss) { Text("Cancel") } },
    )
}
