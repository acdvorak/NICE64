package com.melfegor.nice64

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.melfegor.nice64.api.AndroidInputSource
import com.melfegor.nice64.api.AndroidInputSourceKind
import com.melfegor.nice64.api.BridgeDashboardSnapshot
import com.melfegor.nice64.api.BridgeFirmwareTarget
import com.melfegor.nice64.api.BridgeKeyboardMouseMode
import com.melfegor.nice64.api.BridgeKeyboardMousePresentation
import com.melfegor.nice64.api.BridgeMutationPhase
import com.melfegor.nice64.api.BridgeMutationPresentation
import com.melfegor.nice64.api.Nice64LogicalKey
import com.melfegor.nice64.ui.components.Nice64MetadataRow
import com.melfegor.nice64.ui.components.Nice64OperationStatus
import com.melfegor.nice64.ui.components.Nice64StatusBanner
import com.melfegor.nice64.ui.components.Nice64StatusChip
import com.melfegor.nice64.ui.components.Nice64StatusTone
import com.melfegor.nice64.ui.theme.Nice64Spacing
import kotlin.math.roundToInt

data class KeyboardMouseActions(
    val selectFirmware: (BridgeFirmwareTarget) -> Unit = {},
    val selectKeyboardSource: (AndroidInputSource) -> Unit = {},
    val selectPointerSource: (AndroidInputSource) -> Unit = {},
    val key: (Nice64LogicalKey, Boolean) -> Unit = { _, _ -> },
    val pointer: (Int, Int) -> Unit = { _, _ -> },
    val button: (Boolean?, Boolean?) -> Unit = { _, _ -> },
    val invertX: (Boolean) -> Unit = {},
    val invertY: (Boolean) -> Unit = {},
    val refreshLeds: () -> Unit = {},
    val capturePointer: () -> Unit = {},
    val releasePointer: () -> Unit = {},
)

private enum class SourcePicker { KEYBOARD, POINTER }

@Composable
internal fun KeyboardMouseControls(
    dashboard: BridgeDashboardSnapshot,
    mutation: BridgeMutationPresentation,
    input: BridgeKeyboardMousePresentation,
    actions: KeyboardMouseActions,
) {
    val state = dashboard.state.value
    var picker by remember { mutableStateOf<SourcePicker?>(null) }
    val busy = dashboard.operation.value?.status == "running" || mutation.inFlight
    Column(verticalArrangement = Arrangement.spacedBy(Nice64Spacing.md)) {
        Text("N64 input personality", style = MaterialTheme.typography.titleMedium)
        Text(
            "Requested mode is kept separate from the Bridge-verified active mode.",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Row(horizontalArrangement = Arrangement.spacedBy(Nice64Spacing.xs)) {
            listOf(
                BridgeFirmwareTarget.CONTROLLER,
                BridgeFirmwareTarget.KEYBOARD,
                BridgeFirmwareTarget.MOUSE,
            ).forEach { target ->
                OutlinedButton(
                    onClick = { actions.selectFirmware(target) },
                    enabled = state?.device?.connected == true && !busy,
                    modifier = Modifier.weight(1f),
                ) { Text(target.shortName) }
            }
        }
        Nice64MetadataRow("Requested", mutation.targetDisplayName ?: "None")
        Nice64MetadataRow(
            "Verified",
            when (input.verifiedMode) {
                BridgeKeyboardMouseMode.KEYBOARD -> "RandNET Keyboard Ready"
                BridgeKeyboardMouseMode.MOUSE -> "N64 Mouse Ready"
                BridgeKeyboardMouseMode.NONE -> state?.device?.firmwarePersonality ?: "Unavailable"
            },
        )
        FirmwareTransitionStatus(state?.device?.firmwareTransition?.phase, mutation)

        when (input.verifiedMode) {
            BridgeKeyboardMouseMode.KEYBOARD -> {
                SourceRow("Keyboard source", input.keyboardSource.displayName) {
                    picker = SourcePicker.KEYBOARD
                }
                KeyboardLeds(input, actions.refreshLeds)
                OnScreenKeyboard(input, actions.key)
            }
            BridgeKeyboardMouseMode.MOUSE -> {
                SourceRow("Pointer source", input.pointerSource.displayName) {
                    picker = SourcePicker.POINTER
                }
                Text("N64 Mouse settings", style = MaterialTheme.typography.titleSmall)
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text("Invert X")
                    Switch(checked = input.invertX, onCheckedChange = actions.invertX)
                }
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text("Invert Y")
                    Switch(checked = input.invertY, onCheckedChange = actions.invertY)
                }
                if (input.pointerSource.kind != AndroidInputSourceKind.ON_SCREEN) {
                    Button(
                        onClick = if (input.pointerCaptured) actions.releasePointer else actions.capturePointer,
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text(if (input.pointerCaptured) "Release Physical Pointer" else "Capture Physical Pointer") }
                    Text(
                        if (input.pointerCaptured) "Relative pointer capture active. Back releases it."
                        else "Android requires explicit pointer capture for reliable relative mouse motion.",
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
                OnScreenTrackpad(input, actions)
            }
            BridgeKeyboardMouseMode.NONE -> Nice64StatusBanner(
                title = "Keyboard/Mouse input paused",
                message = "Select a mode and wait for the Bridge to reboot, reconnect, and verify it.",
                tone = Nice64StatusTone.WARNING,
            )
        }
        input.failure?.let {
            Nice64StatusBanner(
                title = "Input delivery problem",
                tone = Nice64StatusTone.ERROR,
                message = it,
            )
        }
        if (input.sending) Nice64OperationStatus("Direct mobile input active")
    }

    when (picker) {
        SourcePicker.KEYBOARD -> SourceDialog(
            title = "Keyboard Source",
            choices = buildList {
                add(AndroidInputSource(AndroidInputSourceKind.ON_SCREEN, displayName = "On-Screen Keyboard"))
                add(AndroidInputSource(AndroidInputSourceKind.ANY, displayName = "System / Any Keyboard"))
                input.devices.filter { it.keyboard }.forEach {
                    add(AndroidInputSource(AndroidInputSourceKind.SPECIFIC, it.descriptor, it.displayName))
                }
            },
            selected = input.keyboardSource,
            onSelect = actions.selectKeyboardSource,
        ) { picker = null }
        SourcePicker.POINTER -> SourceDialog(
            title = "Pointer Source",
            choices = buildList {
                add(AndroidInputSource(AndroidInputSourceKind.ON_SCREEN, displayName = "On-Screen Trackpad"))
                add(AndroidInputSource(AndroidInputSourceKind.ANY, displayName = "System / Any Pointer"))
                input.devices.filter { it.pointer }.forEach {
                    add(AndroidInputSource(AndroidInputSourceKind.SPECIFIC, it.descriptor, it.displayName))
                }
            },
            selected = input.pointerSource,
            onSelect = actions.selectPointerSource,
        ) { picker = null }
        null -> Unit
    }
}

private val BridgeFirmwareTarget.shortName: String
    get() = when (this) {
        BridgeFirmwareTarget.KEYBOARD -> "Keyboard"
        BridgeFirmwareTarget.MOUSE -> "Mouse"
        else -> displayName
    }

@Composable
private fun FirmwareTransitionStatus(phase: String?, mutation: BridgeMutationPresentation) {
    val label = when {
        mutation.phase == BridgeMutationPhase.SUBMITTING -> "Requesting ${mutation.targetDisplayName}…"
        phase == "reboot-expected" -> "NICE64 rebooting…"
        phase == "reconnecting" -> "Reconnecting…"
        mutation.phase in setOf(BridgeMutationPhase.ACCEPTED, BridgeMutationPhase.RECONCILING) -> "Verifying…"
        mutation.phase == BridgeMutationPhase.FAILED || phase == "failed" -> "Mode transition failed"
        else -> null
    }
    label?.let { Nice64OperationStatus(it) }
}

@Composable
private fun SourceRow(label: String, current: String, choose: () -> Unit) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(Nice64Spacing.md), verticalArrangement = Arrangement.spacedBy(Nice64Spacing.xs)) {
            Nice64MetadataRow(label, current)
            Button(onClick = choose, modifier = Modifier.fillMaxWidth()) { Text("Select $label") }
        }
    }
}

@Composable
private fun KeyboardLeds(input: BridgeKeyboardMousePresentation, refresh: () -> Unit) {
    Row(horizontalArrangement = Arrangement.spacedBy(Nice64Spacing.sm), verticalAlignment = Alignment.CenterVertically) {
        Nice64StatusChip("Power ${if (input.leds.power) "On" else "Off"}", if (input.leds.power) Nice64StatusTone.HEALTHY else Nice64StatusTone.UNAVAILABLE)
        Nice64StatusChip("Caps ${if (input.leds.capsLock) "On" else "Off"}", if (input.leds.capsLock) Nice64StatusTone.HEALTHY else Nice64StatusTone.UNAVAILABLE)
        Nice64StatusChip("Num ${if (input.leds.numLock) "On" else "Off"}", if (input.leds.numLock) Nice64StatusTone.HEALTHY else Nice64StatusTone.UNAVAILABLE)
    }
    TextButton(onClick = refresh) { Text("Refresh N64 LED state") }
}

@Composable
private fun OnScreenKeyboard(
    input: BridgeKeyboardMousePresentation,
    onKey: (Nice64LogicalKey, Boolean) -> Unit,
) {
    if (input.keyboardSource.kind != AndroidInputSourceKind.ON_SCREEN) {
        Text("Physical keyboard events are accepted while this screen is foregrounded.")
        return
    }
    Text("On-Screen Keyboard", style = MaterialTheme.typography.titleMedium)
    listOf(
        listOf(Nice64LogicalKey.D1, Nice64LogicalKey.D2, Nice64LogicalKey.D3, Nice64LogicalKey.D4,
            Nice64LogicalKey.D5, Nice64LogicalKey.D6, Nice64LogicalKey.D7, Nice64LogicalKey.D8,
            Nice64LogicalKey.D9, Nice64LogicalKey.D0),
        listOf(Nice64LogicalKey.Q, Nice64LogicalKey.W, Nice64LogicalKey.E, Nice64LogicalKey.R,
            Nice64LogicalKey.T, Nice64LogicalKey.Y, Nice64LogicalKey.U, Nice64LogicalKey.I,
            Nice64LogicalKey.O, Nice64LogicalKey.P),
        listOf(Nice64LogicalKey.A, Nice64LogicalKey.S, Nice64LogicalKey.D, Nice64LogicalKey.F,
            Nice64LogicalKey.G, Nice64LogicalKey.H, Nice64LogicalKey.J, Nice64LogicalKey.K,
            Nice64LogicalKey.L),
        listOf(Nice64LogicalKey.Z, Nice64LogicalKey.X, Nice64LogicalKey.C, Nice64LogicalKey.V,
            Nice64LogicalKey.B, Nice64LogicalKey.N, Nice64LogicalKey.M),
        listOf(Nice64LogicalKey.LEFT_SHIFT, Nice64LogicalKey.LEFT_CONTROL,
            Nice64LogicalKey.LEFT_ALT, Nice64LogicalKey.SPACE, Nice64LogicalKey.ENTER,
            Nice64LogicalKey.BACKSPACE, Nice64LogicalKey.HOME),
    ).forEach { row ->
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(2.dp)) {
            row.forEach { key -> HoldKey(key, input.heldKeys.contains(key), Modifier.weight(1f), onKey) }
        }
    }
}

@Composable
private fun HoldKey(
    key: Nice64LogicalKey,
    held: Boolean,
    modifier: Modifier,
    onKey: (Nice64LogicalKey, Boolean) -> Unit,
) {
    Box(
        modifier = modifier.height(44.dp)
            .background(
                if (held) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceVariant,
                RoundedCornerShape(6.dp),
            )
            .pointerInput(key) {
                awaitEachGesture {
                    awaitFirstDown(requireUnconsumed = false)
                    onKey(key, true)
                    do {
                        val event = awaitPointerEvent()
                    } while (event.changes.any { it.pressed })
                    onKey(key, false)
                }
            }
            .semantics { contentDescription = "${key.label} key${if (held) ", held" else ""}" },
        contentAlignment = Alignment.Center,
    ) { Text(key.label, style = MaterialTheme.typography.labelSmall) }
}

@Composable
private fun OnScreenTrackpad(input: BridgeKeyboardMousePresentation, actions: KeyboardMouseActions) {
    if (input.pointerSource.kind != AndroidInputSourceKind.ON_SCREEN) {
        Text("Physical relative mouse events are accepted while this screen is foregrounded.")
        return
    }
    Text("On-Screen Trackpad", style = MaterialTheme.typography.titleMedium)
    Box(
        Modifier.fillMaxWidth().height(220.dp)
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(12.dp))
            .pointerInput(Unit) {
                awaitEachGesture {
                    awaitFirstDown(requireUnconsumed = false)
                    var moved = false
                    do {
                        val event = awaitPointerEvent()
                        event.changes.forEach { change ->
                            val delta = change.position - change.previousPosition
                            val dx = delta.x.roundToInt()
                            val dy = delta.y.roundToInt()
                            if (dx != 0 || dy != 0) {
                                moved = true
                                actions.pointer(dx, dy)
                                change.consume()
                            }
                        }
                    } while (event.changes.any { it.pressed })
                    if (!moved) {
                        actions.button(true, null)
                        actions.button(false, null)
                    }
                }
            }
            .semantics { contentDescription = "Relative N64 mouse trackpad" },
        contentAlignment = Alignment.Center,
    ) { Text("Drag for relative movement • Tap for left click") }
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Nice64Spacing.sm)) {
        HoldPointerButton("Left", input.leftPressed, Modifier.weight(1f)) { actions.button(it, null) }
        HoldPointerButton("Right", input.rightPressed, Modifier.weight(1f)) { actions.button(null, it) }
    }
}

@Composable
private fun HoldPointerButton(label: String, pressed: Boolean, modifier: Modifier, onPress: (Boolean) -> Unit) {
    Box(
        modifier.height(52.dp)
            .background(
                if (pressed) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.secondaryContainer,
                RoundedCornerShape(8.dp),
            )
            .pointerInput(label) {
                awaitEachGesture {
                    awaitFirstDown(requireUnconsumed = false)
                    onPress(true)
                    do { val event = awaitPointerEvent() } while (event.changes.any { it.pressed })
                    onPress(false)
                }
            },
        contentAlignment = Alignment.Center,
    ) { Text("$label button") }
}

@Composable
private fun SourceDialog(
    title: String,
    choices: List<AndroidInputSource>,
    selected: AndroidInputSource,
    onSelect: (AndroidInputSource) -> Unit,
    dismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text(title) },
        text = {
            Column {
                choices.forEach { choice ->
                    TextButton(
                        onClick = { dismiss(); onSelect(choice) },
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text(if (choice == selected) "✓ ${choice.displayName}" else choice.displayName) }
                }
            }
        },
        confirmButton = { TextButton(onClick = dismiss) { Text("Cancel") } },
    )
}
