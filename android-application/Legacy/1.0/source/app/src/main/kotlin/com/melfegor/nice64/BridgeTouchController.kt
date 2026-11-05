package com.melfegor.nice64

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Card
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.PointerId
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.disabled
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.Dp
import com.melfegor.nice64.api.BridgeControllerInputPresentation
import com.melfegor.nice64.api.MobileInputMode
import com.melfegor.nice64.core.input.N64AnalogScaling
import com.melfegor.nice64.core.model.N64ButtonMask
import com.melfegor.nice64.ui.components.Nice64MetadataRow
import com.melfegor.nice64.ui.theme.Nice64Spacing
import kotlin.math.hypot

data class BridgeControllerInputActions(
    val button: (UInt, Boolean) -> Unit = { _, _ -> },
    val analog: (Int, Int) -> Unit = { _, _ -> },
    val clearTouch: () -> Unit = {},
    val selectMode: (MobileInputMode) -> Unit = {},
)

@Composable
internal fun BridgeTouchController(
    input: BridgeControllerInputPresentation,
    actions: BridgeControllerInputActions,
    onEnterFullScreen: () -> Unit = {},
    onEnterBlackout: () -> Unit = {},
) {
    val enabled = input.enabled
    Card(Modifier.fillMaxWidth()) {
        Column(
            modifier = Modifier.padding(Nice64Spacing.lg),
            verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
        ) {
            Text("Mobile input", style = MaterialTheme.typography.titleMedium)
            Text(
                "Mobile input is used only while NICE Mobile is Primary.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text("Mobile Input Mode", style = MaterialTheme.typography.labelMedium)
            val modes = listOf(
                MobileInputMode.ON_SCREEN to "On-Screen",
                MobileInputMode.CONTROLLER to "Controller",
            )
            SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                modes.forEachIndexed { index, (mode, label) ->
                    val selectedMode = input.inputMode == mode
                    SegmentedButton(
                        selected = selectedMode,
                        onClick = { actions.selectMode(mode) },
                        shape = SegmentedButtonDefaults.itemShape(index, modes.size),
                        modifier = Modifier
                            .weight(1f)
                            .semantics { selected = selectedMode },
                    ) { Text(label) }
                }
            }
            Nice64MetadataRow("Input", input.sourceLabel)
            if (input.inputMode == MobileInputMode.CONTROLLER) {
                Text("The selected physical controller owns gameplay. On-screen controls are hidden.")
                if (enabled) {
                    Text(
                        "Keeps NICE64 active for controller input while dimming the display and preventing screen timeout.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Button(
                        onClick = onEnterBlackout,
                        modifier = Modifier.fillMaxWidth().semantics {
                            contentDescription = "Enter Controller Blackout Mode"
                        },
                    ) { Text("Controller Blackout Mode") }
                }
            } else {
                EmbeddedTouchController(enabled, actions)
                if (enabled) {
                    Button(
                        onClick = onEnterFullScreen,
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text("Enter full-screen controller") }
                }
            }
        }
    }
}

@Composable
internal fun FullScreenTouchController(
    input: BridgeControllerInputPresentation,
    authorityAvailable: Boolean,
    actions: BridgeControllerInputActions,
    onExit: () -> Unit,
) {
    val enabled = authorityAvailable &&
        input.inputMode == MobileInputMode.ON_SCREEN && input.enabled
    BoxWithConstraints(
        Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.surface)
            .safeDrawingPadding()
            .padding(12.dp),
    ) {
        val shortest = minOf(maxWidth, maxHeight)
        val buttonSize = (shortest * 0.13f).coerceIn(48.dp, 72.dp)
        val faceSize = (buttonSize * 1.12f).coerceAtMost(80.dp)
        val analogSize = (shortest * 0.29f).coerceIn(112.dp, 164.dp)

        Column(
            Modifier.align(Alignment.TopCenter),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Button(onClick = onExit, modifier = Modifier.semantics {
                    contentDescription = "Exit full-screen controller"
                }) { Text("Exit") }
                TouchButton(
                    "Start", N64ButtonMask.START, enabled, actions.button,
                    controlSize = buttonSize,
                )
            }
            Text(
                if (enabled) "NICE Mobile Primary · On-Screen" else
                    "On-Screen controls unavailable",
                style = MaterialTheme.typography.labelMedium,
                color = if (enabled) MaterialTheme.colorScheme.onSurfaceVariant else
                    MaterialTheme.colorScheme.error,
            )
        }

        Column(
            Modifier
                .align(Alignment.CenterStart)
                .fillMaxHeight()
                .fillMaxWidth(0.42f),
            verticalArrangement = Arrangement.SpaceBetween,
            horizontalAlignment = Alignment.Start,
        ) {
            Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                TouchButton("L", N64ButtonMask.L, enabled, actions.button, controlSize = buttonSize)
                TouchButton("Z", N64ButtonMask.Z, enabled, actions.button, controlSize = buttonSize)
            }
            DPadCluster(enabled, actions, buttonSize)
            VirtualAnalog(enabled, actions.analog, analogSize)
        }

        Column(
            Modifier
                .align(Alignment.CenterEnd)
                .fillMaxHeight()
                .fillMaxWidth(0.42f),
            verticalArrangement = Arrangement.SpaceBetween,
            horizontalAlignment = Alignment.End,
        ) {
            TouchButton("R", N64ButtonMask.R, enabled, actions.button, controlSize = buttonSize)
            CButtonCluster(enabled, actions, buttonSize)
            Row(
                verticalAlignment = Alignment.Bottom,
                horizontalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                TouchButton(
                    "B", N64ButtonMask.B, enabled, actions.button,
                    Color(0xFF1976D2), faceSize,
                )
                TouchButton(
                    "A", N64ButtonMask.A, enabled, actions.button,
                    Color(0xFF2E7D32), (faceSize * 1.1f).coerceAtMost(88.dp),
                )
            }
        }
    }
}

@Composable
private fun DPadCluster(
    enabled: Boolean,
    actions: BridgeControllerInputActions,
    buttonSize: Dp,
) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        TouchButton("D-Up", N64ButtonMask.DPAD_UP, enabled, actions.button, controlSize = buttonSize)
        Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            TouchButton("D-Left", N64ButtonMask.DPAD_LEFT, enabled, actions.button, controlSize = buttonSize)
            TouchButton("D-Right", N64ButtonMask.DPAD_RIGHT, enabled, actions.button, controlSize = buttonSize)
        }
        TouchButton("D-Down", N64ButtonMask.DPAD_DOWN, enabled, actions.button, controlSize = buttonSize)
    }
}

@Composable
private fun CButtonCluster(
    enabled: Boolean,
    actions: BridgeControllerInputActions,
    buttonSize: Dp,
) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        TouchButton("C-Up", N64ButtonMask.C_UP, enabled, actions.button, controlSize = buttonSize)
        Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            TouchButton("C-Left", N64ButtonMask.C_LEFT, enabled, actions.button, controlSize = buttonSize)
            TouchButton("C-Right", N64ButtonMask.C_RIGHT, enabled, actions.button, controlSize = buttonSize)
        }
        TouchButton("C-Down", N64ButtonMask.C_DOWN, enabled, actions.button, controlSize = buttonSize)
    }
}

@Composable
private fun EmbeddedTouchController(
    enabled: Boolean,
    actions: BridgeControllerInputActions,
) {
    Column(
        Modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            VirtualAnalog(enabled, actions.analog)
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                TouchButton("C-Up", N64ButtonMask.C_UP, enabled, actions.button)
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    TouchButton("C-Left", N64ButtonMask.C_LEFT, enabled, actions.button)
                    TouchButton("C-Right", N64ButtonMask.C_RIGHT, enabled, actions.button)
                }
                TouchButton("C-Down", N64ButtonMask.C_DOWN, enabled, actions.button)
            }
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                TouchButton("D-Up", N64ButtonMask.DPAD_UP, enabled, actions.button)
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    TouchButton("D-Left", N64ButtonMask.DPAD_LEFT, enabled, actions.button)
                    TouchButton("D-Right", N64ButtonMask.DPAD_RIGHT, enabled, actions.button)
                }
                TouchButton("D-Down", N64ButtonMask.DPAD_DOWN, enabled, actions.button)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                TouchButton("B", N64ButtonMask.B, enabled, actions.button, Color(0xFF1976D2))
                TouchButton("A", N64ButtonMask.A, enabled, actions.button, Color(0xFF2E7D32))
            }
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceEvenly) {
            TouchButton("L", N64ButtonMask.L, enabled, actions.button)
            TouchButton("Z", N64ButtonMask.Z, enabled, actions.button)
            TouchButton("Start", N64ButtonMask.START, enabled, actions.button)
            TouchButton("R", N64ButtonMask.R, enabled, actions.button)
        }
    }
}

@Composable
private fun TouchButton(
    label: String,
    mask: UInt,
    enabled: Boolean,
    update: (UInt, Boolean) -> Unit,
    color: Color = MaterialTheme.colorScheme.secondaryContainer,
    controlSize: Dp = if (label.length > 2) 52.dp else 48.dp,
) {
    Box(
        Modifier
            .size(controlSize)
            .semantics {
                contentDescription = "$label Button"
                role = Role.Button
                if (!enabled) disabled()
            }
            .background(if (enabled) color else MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(12.dp))
            .pointerInput(enabled, mask) {
                if (!enabled) return@pointerInput
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    val pointer = down.id
                    var pressed = true
                    update(mask, true)
                    try {
                        while (pressed) {
                            val event = awaitPointerEvent()
                            val change = event.changes.firstOrNull { it.id == pointer }
                            val inside = change != null && change.pressed &&
                                change.position.x in 0f..size.width.toFloat() &&
                                change.position.y in 0f..size.height.toFloat()
                            if (inside != pressed) {
                                pressed = inside
                                update(mask, inside)
                            }
                            if (change == null || !change.pressed) break
                            change.consume()
                        }
                    } finally {
                        update(mask, false)
                    }
                }
            },
        contentAlignment = Alignment.Center,
    ) { Text(label) }
}

@Composable
private fun VirtualAnalog(
    enabled: Boolean,
    update: (Int, Int) -> Unit,
    controlSize: Dp = 112.dp,
) {
    Box(
        Modifier
            .size(controlSize)
            .semantics {
                contentDescription = "Analog stick"
                role = Role.Button
                if (!enabled) disabled()
            }
            .background(MaterialTheme.colorScheme.surfaceVariant, CircleShape)
            .pointerInput(enabled) {
                if (!enabled) return@pointerInput
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    val pointer = down.id
                    try {
                        publishAnalog(down.position, size.width.toFloat(), size.height.toFloat(), update)
                        trackAnalog(pointer, update)
                    } finally {
                        update(0, 0)
                    }
                }
            },
        contentAlignment = Alignment.Center,
    ) { Text("Stick") }
}

private suspend fun androidx.compose.ui.input.pointer.AwaitPointerEventScope.trackAnalog(
    pointer: PointerId,
    update: (Int, Int) -> Unit,
) {
    while (true) {
        val event = awaitPointerEvent()
        val change = event.changes.firstOrNull { it.id == pointer } ?: return
        if (!change.pressed) return
        publishAnalog(change.position, size.width.toFloat(), size.height.toFloat(), update)
        change.consume()
    }
}

private fun publishAnalog(
    position: Offset,
    width: Float,
    height: Float,
    update: (Int, Int) -> Unit,
) {
    val centerX = width / 2f
    val centerY = height / 2f
    val radius = minOf(centerX, centerY).coerceAtLeast(1f)
    var x = (position.x - centerX) / radius
    var y = (centerY - position.y) / radius
    val magnitude = hypot(x.toDouble(), y.toDouble()).toFloat()
    if (magnitude > 1f) {
        x /= magnitude
        y /= magnitude
    }
    update(
        N64AnalogScaling.scaledAxis(x, 0f),
        N64AnalogScaling.scaledAxis(y, 0f),
    )
}
