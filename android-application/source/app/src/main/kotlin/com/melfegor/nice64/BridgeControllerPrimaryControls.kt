package com.melfegor.nice64

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import com.melfegor.nice64.api.BridgeControllerDataPlanePhase
import com.melfegor.nice64.api.BridgeControllerDataPlanePresentation
import com.melfegor.nice64.api.BridgeControllerPrimaryPhase
import com.melfegor.nice64.api.BridgeControllerPrimaryPresentation
import com.melfegor.nice64.api.BridgeDashboardSnapshot
import com.melfegor.nice64.ui.components.Nice64DisabledState
import com.melfegor.nice64.ui.components.Nice64StatusBanner
import com.melfegor.nice64.ui.components.Nice64StatusChip
import com.melfegor.nice64.ui.components.Nice64StatusTone
import com.melfegor.nice64.ui.theme.Nice64Spacing

data class BridgeControllerPrimaryActions(
    val selectMobile: () -> Unit = {},
    val selectBridge: () -> Unit = {},
)

@Composable
internal fun BridgeControllerPrimaryControls(
    dashboard: BridgeDashboardSnapshot,
    primary: BridgeControllerPrimaryPresentation,
    dataPlane: BridgeControllerDataPlanePresentation,
    otherActionBusy: Boolean,
    actions: BridgeControllerPrimaryActions,
) {
    dashboard.controller.value ?: return
    val bridgeBusy = dashboard.operation.value?.status == "running"
    val actionEnabled = !otherActionBusy && !bridgeBusy && !primary.actionInFlight
    val authorityText = when (primary.phase) {
        BridgeControllerPrimaryPhase.BRIDGE_PRIMARY -> "Bridge Primary"
        BridgeControllerPrimaryPhase.SWITCHING_TO_MOBILE -> "Switching to NICE Mobile…"
        BridgeControllerPrimaryPhase.MOBILE_PRIMARY -> "NICE Mobile Primary"
        BridgeControllerPrimaryPhase.SWITCHING_TO_BRIDGE -> "Switching to Bridge…"
        BridgeControllerPrimaryPhase.ERROR -> "Primary selection needs authoritative refresh"
    }
    val authorityTone = when (primary.phase) {
        BridgeControllerPrimaryPhase.BRIDGE_PRIMARY,
        BridgeControllerPrimaryPhase.MOBILE_PRIMARY -> Nice64StatusTone.HEALTHY
        BridgeControllerPrimaryPhase.SWITCHING_TO_MOBILE,
        BridgeControllerPrimaryPhase.SWITCHING_TO_BRIDGE -> Nice64StatusTone.IN_PROGRESS
        BridgeControllerPrimaryPhase.ERROR -> Nice64StatusTone.ERROR
    }
    val connectionText = when (dataPlane.phase) {
        BridgeControllerDataPlanePhase.ACTIVE -> if (dataPlane.neutralOnly) {
            "Active — neutral state"
        } else {
            "Active — human input enabled"
        }
        BridgeControllerDataPlanePhase.STARTING -> "Starting"
        BridgeControllerDataPlanePhase.STOPPING -> "Stopping"
        BridgeControllerDataPlanePhase.FAILED -> "Failed"
        BridgeControllerDataPlanePhase.STOPPED -> "Stopped"
    }
    val connectionTone = when (dataPlane.phase) {
        BridgeControllerDataPlanePhase.ACTIVE -> Nice64StatusTone.HEALTHY
        BridgeControllerDataPlanePhase.STARTING,
        BridgeControllerDataPlanePhase.STOPPING -> Nice64StatusTone.IN_PROGRESS
        BridgeControllerDataPlanePhase.FAILED -> Nice64StatusTone.ERROR
        BridgeControllerDataPlanePhase.STOPPED -> Nice64StatusTone.UNAVAILABLE
    }

    Card(Modifier.fillMaxWidth()) {
        Column(
            modifier = Modifier.padding(Nice64Spacing.lg),
            verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
        ) {
            Text("Controller authority", style = MaterialTheme.typography.titleMedium)
            Text(
                "Controls which source is permitted to provide N64 controller state.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text("Current", style = MaterialTheme.typography.labelMedium)
            Nice64StatusChip(authorityText, authorityTone)
            Text("Controller connection/status", style = MaterialTheme.typography.labelMedium)
            Nice64StatusChip(connectionText, connectionTone)
            primary.failure?.let {
                Nice64StatusBanner("Controller authority unavailable", Nice64StatusTone.ERROR, message = it.safeMessage)
            }
            dataPlane.safeMessage?.takeIf { dataPlane.phase == BridgeControllerDataPlanePhase.FAILED }
                ?.let {
                    Nice64StatusBanner("Controller connection failed", Nice64StatusTone.ERROR, message = it)
                }

            if (primary.mobilePrimary) {
                Button(
                    onClick = actions.selectBridge,
                    enabled = actionEnabled,
                    modifier = Modifier.fillMaxWidth(),
                ) { Text("Bridge Primary") }
            } else {
                Button(
                    onClick = actions.selectMobile,
                    enabled = actionEnabled,
                    modifier = Modifier.fillMaxWidth(),
                ) { Text("NICE Mobile Primary") }
            }
            if (!actionEnabled) {
                Nice64DisabledState(
                    explanation = if (primary.actionInFlight) {
                        "Controller authority is currently switching."
                    } else {
                        "Controller authority cannot change while another Bridge operation is active."
                    },
                )
            }
        }
    }
}
