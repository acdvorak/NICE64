package com.melfegor.nice64

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import com.melfegor.nice64.api.BridgeControllerDiagnosticsPresentation
import com.melfegor.nice64.api.BridgeControllerInputPresentation
import com.melfegor.nice64.api.ControllerProfilePresentationState
import com.melfegor.nice64.api.ControllerProfileResolutionKind
import com.melfegor.nice64.api.ControllerInputTracePresentation
import com.melfegor.nice64.ui.components.Nice64EmptyState
import com.melfegor.nice64.ui.components.Nice64ErrorState
import com.melfegor.nice64.ui.components.Nice64Badge
import com.melfegor.nice64.ui.components.Nice64LoadingState
import com.melfegor.nice64.ui.components.Nice64MetadataRow
import com.melfegor.nice64.ui.components.Nice64StatusBanner
import com.melfegor.nice64.ui.components.Nice64StatusChip
import com.melfegor.nice64.ui.components.Nice64StatusTone
import com.melfegor.nice64.ui.theme.Nice64Spacing

internal data class ControllerProfileActions(
    val onRefresh: () -> Unit = {},
    val onSelectController: (Int) -> Unit = {},
    val onCreate: (String) -> Unit = {},
    val onSelectProfile: (String?) -> Unit = {},
    val onDelete: (String) -> Unit = {},
    val onEdit: (String) -> Unit = {},
)

@Composable
internal fun BridgeControllersScreen(
    state: ControllerProfilePresentationState,
    diagnostics: BridgeControllerDiagnosticsPresentation,
    actions: ControllerProfileActions = ControllerProfileActions(),
    editor: com.melfegor.nice64.api.MappingEditorState = com.melfegor.nice64.api.MappingEditorState(),
    editorActions: MappingEditorActions = MappingEditorActions(),
    liveInput: BridgeControllerInputPresentation = BridgeControllerInputPresentation(),
    trace: ControllerInputTracePresentation = ControllerInputTracePresentation(),
    traceActions: ControllerInputTraceActions = ControllerInputTraceActions(),
    traceControlsEnabled: Boolean = true,
) {
    if (editor.active) {
        ControllerMappingEditorScreen(editor, editorActions)
        return
    }
    var newProfileName by rememberSaveable { mutableStateOf("") }
    var pendingDeleteId by rememberSaveable { mutableStateOf<String?>(null) }
    var showDiagnostics by rememberSaveable { mutableStateOf(false) }
    val busy = state.loading || state.actionInFlight

    if (showDiagnostics) {
        BackHandler { showDiagnostics = false }
        ControllerDiagnosticsDestination(
            state = state,
            diagnostics = diagnostics,
            trace = trace,
            traceActions = traceActions,
            traceControlsEnabled = traceControlsEnabled,
            onBack = { showDiagnostics = false },
        )
        return
    }

    Text("Controllers", style = MaterialTheme.typography.titleLarge)

    ControllerSummary(state, liveInput)
    if (state.loading) Nice64LoadingState("Loading controller profiles…")
    state.errorMessage?.let {
        Nice64ErrorState(title = "Controller profiles unavailable", message = it)
    }

    ControllerManagementSelection(state, busy, actions)
    ControllerProfileCards(state, busy, actions) { pendingDeleteId = it }

    Text("Create profile", style = MaterialTheme.typography.titleMedium)
    OutlinedTextField(
        value = newProfileName,
        onValueChange = { newProfileName = it.take(80) },
        label = { Text("Profile name") },
        enabled = !busy,
        singleLine = true,
        modifier = Modifier.fillMaxWidth(),
    )
    Button(
        enabled = !busy && state.managementDevice != null && newProfileName.isNotBlank(),
        onClick = {
            actions.onCreate(newProfileName)
            newProfileName = ""
        },
        modifier = Modifier.fillMaxWidth(),
    ) { Text("Create from standard gamepad mapping") }
    Button(onClick = actions.onRefresh, enabled = !busy, modifier = Modifier.fillMaxWidth()) {
        Text("Refresh controller profiles")
    }

    ControllerProfileMatch(state)
    Text("Troubleshooting", style = MaterialTheme.typography.titleMedium)
    Text(
        "Technical controller details and timing tools are available when needed.",
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    Button(
        onClick = { showDiagnostics = true },
        modifier = Modifier.fillMaxWidth(),
    ) { Text("Advanced diagnostics") }

    pendingDeleteId?.let { profileId ->
        val name = state.profiles.firstOrNull { it.profileId == profileId }?.profileName
            ?: "this custom profile"
        AlertDialog(
            onDismissRequest = { pendingDeleteId = null },
            title = { Text("Delete controller profile?") },
            text = { Text("Delete $name? The built-in default is not affected.") },
            confirmButton = {
                TextButton(
                    onClick = {
                        pendingDeleteId = null
                        actions.onDelete(profileId)
                    },
                    colors = ButtonDefaults.textButtonColors(
                        contentColor = MaterialTheme.colorScheme.error,
                    ),
                ) { Text("Confirm delete") }
            },
            dismissButton = {
                TextButton(onClick = { pendingDeleteId = null }) { Text("Cancel") }
            },
        )
    }
}

@Composable
private fun ControllerDiagnosticsDestination(
    state: ControllerProfilePresentationState,
    diagnostics: BridgeControllerDiagnosticsPresentation,
    trace: ControllerInputTracePresentation,
    traceActions: ControllerInputTraceActions,
    traceControlsEnabled: Boolean,
    onBack: () -> Unit,
) {
    Text("Advanced diagnostics", style = MaterialTheme.typography.titleLarge)
    Nice64Badge("Diagnostic build")
    Text(
        "Troubleshooting and development tools. Viewing or leaving this screen does not start, stop, or reset diagnostics.",
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    Button(onClick = onBack, modifier = Modifier.fillMaxWidth()) { Text("Back to Controllers") }
    state.persistedControllerIdentity?.let {
        Text("Profile identity", style = MaterialTheme.typography.titleMedium)
        Nice64MetadataRow(
            "Saved controller identity",
            "${it.normalizedName} — VID %04X / PID %04X".format(it.vendorId, it.productId),
        )
    }
    BridgeControllerDiagnostics(diagnostics)
    Text("Timing trace", style = MaterialTheme.typography.titleMedium)
    Text(
        "Timing trace is intended for troubleshooting controller input behavior.",
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    if (!traceControlsEnabled) {
        Nice64StatusBanner(
            title = "Trace controls unavailable",
            message = "NICE Mobile must be Primary to start or stop controller timing capture.",
            tone = Nice64StatusTone.UNAVAILABLE,
        )
    }
    ControllerInputTraceControls(trace, traceActions, controlsEnabled = traceControlsEnabled)
}

@Composable
private fun ControllerSummary(
    state: ControllerProfilePresentationState,
    liveInput: BridgeControllerInputPresentation,
) {
    Text("Controller summary", style = MaterialTheme.typography.titleMedium)
    if (state.devices.isEmpty()) {
        Nice64EmptyState(
            title = "No physical controller observed",
            message = "Connect an Android GAMEPAD or JOYSTICK device to manage controller profiles.",
        )
        return
    }
    val controller = state.activeDevice ?: state.managementDevice ?: state.devices.first()
    Card(Modifier.fillMaxWidth()) {
        Column(
            Modifier.fillMaxWidth().padding(Nice64Spacing.lg),
            verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
        ) {
            Text(controller.name, style = MaterialTheme.typography.titleMedium)
            Nice64StatusChip("Controller observed", Nice64StatusTone.INFORMATIONAL)
            Nice64MetadataRow(
                "Active gameplay profile",
                controllerMappingDisplayName(liveInput.gameplayMappingLabel),
            )
            when {
                liveInput.gameplayMappingLabel == STANDARD_GAMEPAD_INTERNAL_NAME ->
                    Nice64Badge("Built-in profile")
                liveInput.gameplayMappingLabel.startsWith("Custom") ->
                    Nice64Badge("Custom profile")
            }
            liveInput.gameplayMappingReason?.let {
                Nice64StatusBanner(
                    title = "Gameplay profile detail",
                    message = it,
                    tone = Nice64StatusTone.WARNING,
                )
            }
        }
    }
}

@Composable
private fun ControllerManagementSelection(
    state: ControllerProfilePresentationState,
    busy: Boolean,
    actions: ControllerProfileActions,
) {
    if (state.devices.isEmpty()) return
    Text("Observed controllers", style = MaterialTheme.typography.titleMedium)
    state.devices.forEach { device ->
        val managing = device.runtimeDeviceId == state.managementRuntimeDeviceId
        Card(Modifier.fillMaxWidth().semantics { selected = managing }) {
            Column(
                Modifier.fillMaxWidth().padding(Nice64Spacing.lg),
                verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
            ) {
                Text(device.name, style = MaterialTheme.typography.titleMedium)
                if (managing) {
                    Nice64StatusChip("Selected for profile management", Nice64StatusTone.HEALTHY)
                } else {
                    Button(
                        enabled = !busy,
                        onClick = { actions.onSelectController(device.runtimeDeviceId) },
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text("Manage profiles") }
                }
            }
        }
    }
}

@Composable
private fun ControllerProfileCards(
    state: ControllerProfilePresentationState,
    busy: Boolean,
    actions: ControllerProfileActions,
    onRequestDelete: (String) -> Unit,
) {
    Text("Controller profiles", style = MaterialTheme.typography.titleMedium)
    Card(Modifier.fillMaxWidth().semantics { selected = state.selectedProfileId == null }) {
        Column(
            Modifier.fillMaxWidth().padding(Nice64Spacing.lg),
            verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
        ) {
            Text("Standard gamepad mapping", style = MaterialTheme.typography.titleMedium)
            Nice64Badge("Built-in profile")
            Text(
                "Read-only and available to every eligible controller.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (state.selectedProfileId == null) {
                Nice64StatusChip("Selected profile", Nice64StatusTone.HEALTHY)
            } else {
                Button(
                    enabled = !busy,
                    onClick = { actions.onSelectProfile(null) },
                    modifier = Modifier.fillMaxWidth(),
                ) { Text("Select built-in profile") }
            }
        }
    }
    if (state.profiles.isEmpty()) {
        Nice64EmptyState(
            title = "No custom profiles",
            message = "Create a custom profile for the selected controller when needed.",
        )
    }
    state.profiles.forEach { profile ->
        Card(Modifier.fillMaxWidth().semantics { selected = profile.selected }) {
            Column(
                Modifier.fillMaxWidth().padding(Nice64Spacing.lg),
                verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
            ) {
                Text(profile.profileName, style = MaterialTheme.typography.titleMedium)
                Nice64Badge("Custom profile")
                if (profile.selected) {
                    Nice64StatusChip("Selected profile", Nice64StatusTone.HEALTHY)
                }
                Nice64MetadataRow("Controller match", profile.identitySummary)
                Nice64MetadataRow("Profile ID", profile.profileId.take(36))
                Button(
                    enabled = !busy,
                    onClick = { actions.onEdit(profile.profileId) },
                    modifier = Modifier.fillMaxWidth(),
                ) { Text("Edit mappings") }
                if (!profile.selected) {
                    Button(
                        enabled = !busy,
                        onClick = { actions.onSelectProfile(profile.profileId) },
                        modifier = Modifier.fillMaxWidth().testTag("select-profile-${profile.profileId}"),
                    ) { Text("Select profile") }
                }
                TextButton(
                    enabled = !busy,
                    onClick = { onRequestDelete(profile.profileId) },
                    modifier = Modifier
                        .fillMaxWidth()
                        .testTag("delete-profile-${profile.profileId}")
                        .semantics {
                            contentDescription = "Delete custom profile ${profile.profileName}"
                        },
                    colors = ButtonDefaults.textButtonColors(
                        contentColor = MaterialTheme.colorScheme.error,
                    ),
                ) { Text("Delete custom profile") }
            }
        }
    }
}

@Composable
private fun ControllerProfileMatch(state: ControllerProfilePresentationState) {
    Text("Controller profile match", style = MaterialTheme.typography.titleMedium)
    when (state.resolutionKind) {
        ControllerProfileResolutionKind.EXACT -> Nice64StatusBanner(
            title = "Matched custom profile",
            message = state.profileNames(state.resolutionProfileIds),
            tone = Nice64StatusTone.HEALTHY,
        )
        ControllerProfileResolutionKind.FALLBACK -> Nice64StatusBanner(
            title = "Matched custom profile",
            message = "Compatible controller identity: ${state.profileNames(state.resolutionProfileIds)}",
            tone = Nice64StatusTone.INFORMATIONAL,
        )
        ControllerProfileResolutionKind.AMBIGUOUS -> Nice64StatusBanner(
            title = "Multiple possible profiles",
            message = "${state.profileNames(state.resolutionProfileIds)}. Choose a profile manually.",
            tone = Nice64StatusTone.WARNING,
        )
        ControllerProfileResolutionKind.STANDARD_DEFAULT -> Nice64StatusBanner(
            title = "Matched built-in profile",
            message = "Standard gamepad mapping",
            tone = Nice64StatusTone.INFORMATIONAL,
        )
        ControllerProfileResolutionKind.INELIGIBLE -> Nice64StatusBanner(
            title = "No matching controller",
            message = "Select an eligible connected controller.",
            tone = Nice64StatusTone.UNAVAILABLE,
        )
        ControllerProfileResolutionKind.ERROR -> Nice64StatusBanner(
            title = "Controller profile match unavailable",
            message = state.errorMessage,
            tone = Nice64StatusTone.ERROR,
        )
    }
}

private fun ControllerProfilePresentationState.profileNames(ids: List<String>): String =
    ids.map { id -> profiles.firstOrNull { it.profileId == id }?.profileName ?: id }
        .joinToString(", ")

private fun controllerMappingDisplayName(name: String): String =
    if (name == STANDARD_GAMEPAD_INTERNAL_NAME) "Standard gamepad mapping" else name

private const val STANDARD_GAMEPAD_INTERNAL_NAME = "Standard Android Gamepad"
