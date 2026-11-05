package com.melfegor.nice64

import android.view.KeyEvent
import android.view.MotionEvent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import com.melfegor.nice64.api.MappingCaptureCandidate
import com.melfegor.nice64.api.MappingCapturePhase
import com.melfegor.nice64.api.MappingEditorState
import com.melfegor.nice64.api.displayName
import com.melfegor.nice64.core.input.AxisBindingV1
import com.melfegor.nice64.core.input.N64SemanticTargetV1
import com.melfegor.nice64.ui.components.Nice64Badge
import com.melfegor.nice64.ui.components.Nice64ErrorState
import com.melfegor.nice64.ui.components.Nice64MetadataRow
import com.melfegor.nice64.ui.components.Nice64StatusBanner
import com.melfegor.nice64.ui.components.Nice64StatusChip
import com.melfegor.nice64.ui.components.Nice64StatusTone
import com.melfegor.nice64.ui.theme.Nice64Spacing
import java.util.Locale

internal data class MappingEditorActions(
    val onName: (String) -> Unit = {},
    val onButtonCapture: (N64SemanticTargetV1) -> Unit = {},
    val onAxisCapture: (N64SemanticTargetV1) -> Unit = {},
    val onAnalogPairCapture: () -> Unit = {},
    val onCancelCapture: () -> Unit = {},
    val onApplyCandidate: () -> Unit = {},
    val onClear: (N64SemanticTargetV1) -> Unit = {},
    val onToggleInvert: (N64SemanticTargetV1) -> Unit = {},
    val onDeadZone: (N64SemanticTargetV1, Float?) -> Unit = { _, _ -> },
    val onScale: (N64SemanticTargetV1, Float) -> Unit = { _, _ -> },
    val onRequestRestore: () -> Unit = {},
    val onCancelRestore: () -> Unit = {},
    val onConfirmRestore: () -> Unit = {},
    val onSave: () -> Unit = {},
    val onClose: () -> Unit = {},
    val onContinueEditing: () -> Unit = {},
    val onDiscard: () -> Unit = {},
    val onRecoverController: (Int) -> Unit = {},
)

@Composable
internal fun ControllerMappingEditorScreen(
    state: MappingEditorState,
    actions: MappingEditorActions = MappingEditorActions(),
) {
    val profile = state.working
    if (profile == null) {
        Nice64ErrorState(
            title = "Mapping editor unavailable",
            message = state.errorMessage ?: "The controller profile is unavailable.",
        )
        return
    }

    Text("Edit controller mapping", style = MaterialTheme.typography.titleLarge)
    Card(Modifier.fillMaxWidth()) {
        Column(
            Modifier.fillMaxWidth().padding(Nice64Spacing.lg),
            verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
        ) {
            Text(profile.profileName, style = MaterialTheme.typography.titleLarge)
            Nice64Badge("Custom profile")
            Nice64MetadataRow("Controller", state.managementDeviceName ?: "Unavailable")
            Nice64StatusChip(
                text = if (state.dirty) "Unsaved changes" else "No unsaved changes",
                tone = if (state.dirty) Nice64StatusTone.WARNING else Nice64StatusTone.HEALTHY,
            )
            Text(
                "Configuration only — live gameplay continues to use the currently active gameplay profile.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }

    if (!state.deviceAvailable) {
        Nice64StatusBanner(
            title = "Controller unavailable",
            message = "Review and Save remain available, but input capture requires the selected controller.",
            tone = Nice64StatusTone.UNAVAILABLE,
            modifier = Modifier.semantics { stateDescription = "Controller unavailable" },
        )
        state.reconnectCandidate?.let { candidate ->
            Nice64StatusBanner(
                title = "Reconnected controller found",
                message = candidate.deviceName,
                tone = Nice64StatusTone.INFORMATIONAL,
            )
            Button(
                onClick = { actions.onRecoverController(candidate.runtimeDeviceId) },
                modifier = Modifier.fillMaxWidth().testTag("recover-controller").semantics {
                    contentDescription = "Use the exact reconnected controller for mapping capture"
                },
            ) { Text("Use reconnected controller") }
        }
        if (state.reconnectAmbiguous) {
            Nice64StatusBanner(
                title = "Multiple matching controllers",
                message = "Close the editor and choose a controller explicitly.",
                tone = Nice64StatusTone.ERROR,
            )
        }
    }

    Text("Profile details", style = MaterialTheme.typography.titleMedium)
    OutlinedTextField(
        value = profile.profileName,
        onValueChange = actions.onName,
        label = { Text("Profile name") },
        singleLine = true,
        enabled = state.phase != MappingCapturePhase.SAVING,
        modifier = Modifier.fillMaxWidth().testTag("mapping-profile-name").semantics {
            contentDescription = "Custom controller profile name"
        },
    )
    Text(
        state.statusMessage ?: "No pending editor status.",
        minLines = 2,
        maxLines = 2,
        modifier = Modifier.fillMaxWidth().testTag("editor-status-slot"),
    )
    state.errorMessage?.let {
        Nice64ErrorState(title = "Mapping action failed", message = it)
    }

    CaptureStatus(state, actions)

    Button(
        actions.onAnalogPairCapture,
        enabled = state.deviceAvailable && !state.captureActive,
        modifier = Modifier.fillMaxWidth().testTag("capture-analog-pair").semantics {
            contentDescription = "Map the N64 analog stick X and Y axes"
        },
    ) { Text("Capture analog stick X/Y") }

    Text("N64 mappings", style = MaterialTheme.typography.titleMedium)
    mappingTargetGroups().forEach { group ->
        Nice64Badge(group.name)
        group.targets.forEach { target ->
            val analog = profile.axisBindings.singleOrNull { it.target == target && target.isAnalog }
            MappingRow(state, target, analog, actions)
        }
    }

    Text(
        "Digital-axis thresholds use the validated defaults: press 0.50, release 0.45.",
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    state.validationErrors.forEach {
        Nice64StatusBanner(
            title = "Invalid mapping",
            message = it,
            tone = Nice64StatusTone.ERROR,
        )
    }

    EditorActions(state, actions)
    MappingDialogs(state, actions)
}

@Composable
private fun CaptureStatus(state: MappingEditorState, actions: MappingEditorActions) {
    Card(
        Modifier.fillMaxWidth().testTag("capture-status-slot").semantics {
            stateDescription = if (state.captureActive) "Mapping capture active" else "Mapping capture inactive"
        },
    ) {
        Column(
            Modifier.fillMaxWidth().padding(Nice64Spacing.lg),
            verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
        ) {
            Nice64StatusChip(
                text = if (state.captureActive) "Capture active" else "Capture ready",
                tone = if (state.captureActive) Nice64StatusTone.IN_PROGRESS else Nice64StatusTone.HEALTHY,
            )
            Nice64MetadataRow(
                "Target",
                state.captureTarget?.displayName() ?: "Choose a mapping row",
            )
            Text(
                if (state.captureActive) {
                    state.statusMessage ?: "Press a button or move an axis on the selected controller."
                } else {
                    "Use Button or Axis on a mapping row to begin capture."
                },
                minLines = 2,
                maxLines = 2,
            )
            Text(
                "Only the selected controller is captured. Physical gameplay input is suppressed during capture.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Button(
                onClick = actions.onCancelCapture,
                enabled = state.captureActive,
                modifier = Modifier.fillMaxWidth().testTag("cancel-capture").semantics {
                    contentDescription = "Cancel the active controller mapping capture"
                },
            ) { Text("Cancel capture") }
        }
    }
}

@Composable
private fun MappingRow(
    state: MappingEditorState,
    target: N64SemanticTargetV1,
    analog: AxisBindingV1?,
    actions: MappingEditorActions,
) {
    Card(Modifier.fillMaxWidth()) {
        Column(
            Modifier.fillMaxWidth().padding(Nice64Spacing.md),
            verticalArrangement = Arrangement.spacedBy(Nice64Spacing.xs),
        ) {
            Text(target.displayName(), style = MaterialTheme.typography.titleMedium)
            Nice64MetadataRow("Current assignment", bindingSummary(state, target))
            FlowRow(
                horizontalArrangement = Arrangement.spacedBy(Nice64Spacing.xs),
                verticalArrangement = Arrangement.spacedBy(Nice64Spacing.xs),
            ) {
                if (!target.isAnalog) {
                    TextButton(
                        enabled = state.deviceAvailable && !state.captureActive,
                        onClick = { actions.onButtonCapture(target) },
                        modifier = Modifier.testTag("map-button-${target.name}").semantics {
                            contentDescription = "Capture a controller button for ${target.displayName()}"
                        },
                    ) { Text("Button") }
                }
                TextButton(
                    enabled = state.deviceAvailable && !state.captureActive,
                    onClick = { actions.onAxisCapture(target) },
                    modifier = Modifier.testTag("map-axis-${target.name}").semantics {
                        contentDescription = "Capture a controller axis for ${target.displayName()}"
                    },
                ) { Text("Axis") }
                TextButton(
                    onClick = { actions.onClear(target) },
                    enabled = !state.captureActive,
                    modifier = Modifier.testTag("clear-${target.name}").semantics {
                        contentDescription = "Clear assignment for ${target.displayName()}"
                    },
                ) { Text("Clear") }
            }
            if (analog != null) AnalogSettings(target, analog, actions)
        }
    }
}

@Composable
private fun AnalogSettings(
    target: N64SemanticTargetV1,
    binding: AxisBindingV1,
    actions: MappingEditorActions,
) {
    val deadZone = binding.deadZone ?: 0.12f
    Text("Analog settings", style = MaterialTheme.typography.titleSmall)
    Nice64MetadataRow("Inverted", if (binding.inverted) "Yes" else "No")
    Nice64MetadataRow("Deadzone", format(deadZone))
    Nice64MetadataRow("Scale", format(binding.scale))
    FlowRow(
        horizontalArrangement = Arrangement.spacedBy(Nice64Spacing.xs),
        verticalArrangement = Arrangement.spacedBy(Nice64Spacing.xs),
    ) {
        TextButton(
            { actions.onToggleInvert(target) },
            Modifier.semantics { contentDescription = "Toggle inversion for ${target.displayName()}" },
        ) { Text("Toggle invert") }
        TextButton(
            { actions.onDeadZone(target, (deadZone - 0.05f).coerceAtLeast(0f)) },
            Modifier.semantics {
                contentDescription = "Decrease ${target.displayName()} deadzone from ${format(deadZone)}"
            },
        ) { Text("Deadzone −") }
        TextButton(
            { actions.onDeadZone(target, (deadZone + 0.05f).coerceAtMost(0.95f)) },
            Modifier.semantics {
                contentDescription = "Increase ${target.displayName()} deadzone from ${format(deadZone)}"
            },
        ) { Text("Deadzone +") }
        TextButton(
            { actions.onScale(target, (binding.scale - 0.1f).coerceAtLeast(0.1f)) },
            Modifier.semantics {
                contentDescription = "Decrease ${target.displayName()} scale from ${format(binding.scale)}"
            },
        ) { Text("Scale −") }
        TextButton(
            { actions.onScale(target, (binding.scale + 0.1f).coerceAtMost(4f)) },
            Modifier.semantics {
                contentDescription = "Increase ${target.displayName()} scale from ${format(binding.scale)}"
            },
        ) { Text("Scale +") }
    }
}

@Composable
private fun EditorActions(state: MappingEditorState, actions: MappingEditorActions) {
    Card(Modifier.fillMaxWidth()) {
        Column(
            Modifier.fillMaxWidth().padding(Nice64Spacing.lg),
            verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
        ) {
            Text("Mapping actions", style = MaterialTheme.typography.titleMedium)
            Nice64StatusChip(
                text = if (state.dirty) "Unsaved changes" else "No unsaved changes",
                tone = if (state.dirty) Nice64StatusTone.WARNING else Nice64StatusTone.HEALTHY,
            )
            Button(
                onClick = actions.onSave,
                enabled = state.canSave,
                modifier = Modifier.fillMaxWidth().testTag("save-mappings").semantics {
                    contentDescription = "Save and verify this custom controller profile"
                },
            ) { Text(if (state.phase == MappingCapturePhase.SAVING) "Saving…" else "Save profile") }
            Button(
                onClick = actions.onRequestRestore,
                enabled = !state.captureActive,
                modifier = Modifier.fillMaxWidth().testTag("restore-defaults").semantics {
                    contentDescription = "Restore standard controller mappings in the working copy"
                },
            ) { Text("Restore mapping") }
            Button(
                onClick = actions.onClose,
                enabled = state.phase != MappingCapturePhase.SAVING,
                modifier = Modifier.fillMaxWidth().testTag("close-editor").semantics {
                    contentDescription = "Close the controller mapping editor"
                },
            ) { Text("Close editor") }
        }
    }
}

@Composable
private fun MappingDialogs(state: MappingEditorState, actions: MappingEditorActions) {
    state.candidate?.let { candidate ->
        val hasConflict = state.conflicts.isNotEmpty()
        AlertDialog(
            onDismissRequest = actions.onCancelCapture,
            title = { Text(if (hasConflict) "Replace conflicting mapping?" else "Apply captured mapping?") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm)) {
                    Nice64MetadataRow("Requested target", candidateTarget(candidate).displayName())
                    Nice64MetadataRow("Detected input", candidateInput(candidate))
                    if (hasConflict) {
                        Nice64StatusBanner(
                            title = "Existing assignment conflict",
                            message = state.conflicts.joinToString("\n"),
                            tone = Nice64StatusTone.WARNING,
                        )
                    }
                }
            },
            confirmButton = {
                TextButton(
                    onClick = actions.onApplyCandidate,
                    modifier = Modifier.testTag("apply-candidate").semantics {
                        contentDescription = if (!hasConflict) {
                            "Apply the captured controller mapping"
                        } else {
                            "Replace conflicting mappings with the captured controller mapping"
                        }
                    },
                    colors = if (hasConflict) {
                        ButtonDefaults.textButtonColors(contentColor = MaterialTheme.colorScheme.error)
                    } else {
                        ButtonDefaults.textButtonColors()
                    },
                ) { Text(if (!hasConflict) "Apply" else "Replace") }
            },
            dismissButton = {
                TextButton(actions.onCancelCapture, Modifier.testTag("cancel-candidate")) { Text("Cancel") }
            },
        )
    }
    if (state.restoreConfirmation) {
        AlertDialog(
            onDismissRequest = actions.onCancelRestore,
            title = { Text("Restore mapping?") },
            text = { Text("Profile ID, name, and controller identity will be preserved. Save is still required.") },
            confirmButton = {
                TextButton(actions.onConfirmRestore, Modifier.testTag("confirm-restore")) { Text("Restore") }
            },
            dismissButton = {
                TextButton(actions.onCancelRestore, Modifier.testTag("cancel-restore")) { Text("Cancel") }
            },
        )
    }
    if (state.discardConfirmation) {
        AlertDialog(
            onDismissRequest = actions.onContinueEditing,
            title = { Text("Discard changes?") },
            text = { Text("Unsaved controller mapping changes will be lost.") },
            confirmButton = {
                TextButton(
                    actions.onDiscard,
                    Modifier.testTag("confirm-discard"),
                    colors = ButtonDefaults.textButtonColors(contentColor = MaterialTheme.colorScheme.error),
                ) { Text("Discard") }
            },
            dismissButton = {
                TextButton(actions.onContinueEditing, Modifier.testTag("continue-editing")) {
                    Text("Continue editing")
                }
            },
        )
    }
}

private data class MappingTargetGroup(
    val name: String,
    val targets: List<N64SemanticTargetV1>,
)

private fun mappingTargetGroups() = listOf(
    MappingTargetGroup("Face buttons", listOf(N64SemanticTargetV1.A, N64SemanticTargetV1.B)),
    MappingTargetGroup("Shoulder / Z / Start", listOf(
        N64SemanticTargetV1.Z, N64SemanticTargetV1.L, N64SemanticTargetV1.R,
        N64SemanticTargetV1.START,
    )),
    MappingTargetGroup("D-pad", listOf(
        N64SemanticTargetV1.DPAD_UP, N64SemanticTargetV1.DPAD_DOWN,
        N64SemanticTargetV1.DPAD_LEFT, N64SemanticTargetV1.DPAD_RIGHT,
    )),
    MappingTargetGroup("C-buttons", listOf(
        N64SemanticTargetV1.C_UP, N64SemanticTargetV1.C_DOWN,
        N64SemanticTargetV1.C_LEFT, N64SemanticTargetV1.C_RIGHT,
    )),
    MappingTargetGroup("Analog stick", listOf(
        N64SemanticTargetV1.ANALOG_X, N64SemanticTargetV1.ANALOG_Y,
    )),
)

private fun bindingSummary(state: MappingEditorState, target: N64SemanticTargetV1): String {
    val profile = state.working ?: return "Unassigned"
    val buttons = profile.buttonBindings.filter { it.target == target }
        .map { humanKeyName(it.keyCode) }
    val axes = (profile.axisBindings + profile.axisAlternatives.flatMap { it.bindings })
        .filter { it.target == target }
        .map { "${humanAxisName(it.axisCode)} · ${it.direction.name.lowercase()}" }
    return (buttons + axes).joinToString(" / ").ifBlank { "Unassigned" }
}

private fun humanKeyName(keyCode: Int): String = KeyEvent.keyCodeToString(keyCode)
    .removePrefix("KEYCODE_")
    .split('_')
    .joinToString(" ") { word -> word.lowercase().replaceFirstChar(Char::uppercase) }

private fun humanAxisName(axisCode: Int): String = MotionEvent.axisToString(axisCode)
    .removePrefix("AXIS_")
    .lowercase()
    .replaceFirstChar(Char::uppercase)
    .let { "Axis $it" }

private fun candidateTarget(candidate: MappingCaptureCandidate): N64SemanticTargetV1 = when (candidate) {
    is MappingCaptureCandidate.Button -> candidate.target
    is MappingCaptureCandidate.Axis -> candidate.binding.target
    is MappingCaptureCandidate.AnalogPair -> N64SemanticTargetV1.ANALOG_X
}

private fun candidateInput(candidate: MappingCaptureCandidate): String = when (candidate) {
    is MappingCaptureCandidate.Button -> humanKeyName(candidate.keyCode)
    is MappingCaptureCandidate.Axis ->
        "${humanAxisName(candidate.binding.axisCode)} · ${candidate.binding.direction.name.lowercase()}"
    is MappingCaptureCandidate.AnalogPair ->
        "${humanAxisName(candidate.xAxisCode)} / ${humanAxisName(candidate.yAxisCode)} (Y inverted)"
}

private fun format(value: Float): String = String.format(Locale.US, "%.2f", value)
