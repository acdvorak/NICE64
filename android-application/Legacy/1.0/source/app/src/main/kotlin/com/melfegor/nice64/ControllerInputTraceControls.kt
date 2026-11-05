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
import com.melfegor.nice64.api.ControllerInputTracePresentation
import com.melfegor.nice64.ui.components.Nice64ErrorState
import com.melfegor.nice64.ui.components.Nice64Badge
import com.melfegor.nice64.ui.components.Nice64MetadataRow
import com.melfegor.nice64.ui.components.Nice64StatusChip
import com.melfegor.nice64.ui.components.Nice64StatusTone
import com.melfegor.nice64.ui.theme.Nice64Spacing
import java.util.Locale

data class ControllerInputTraceActions(
    val start: () -> Unit = {},
    val stop: () -> Unit = {},
)

@Composable
internal fun ControllerInputTraceControls(
    trace: ControllerInputTracePresentation,
    actions: ControllerInputTraceActions,
    controlsEnabled: Boolean = true,
) {
    Card(Modifier.fillMaxWidth()) {
        Column(
            Modifier.fillMaxWidth().padding(Nice64Spacing.lg),
            verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
        ) {
            Text("Controller timing trace", style = MaterialTheme.typography.titleMedium)
            Nice64Badge("Diagnostic build")
            Nice64StatusChip(
                text = if (trace.running) "Trace running" else "Trace stopped",
                tone = if (trace.running) Nice64StatusTone.IN_PROGRESS else Nice64StatusTone.UNAVAILABLE,
            )
            Text(
                "Development timing capture for controller input. Trace behavior is independent of this presentation.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Button(
                onClick = actions.start,
                enabled = controlsEnabled && !trace.running,
                modifier = Modifier.fillMaxWidth(),
            ) { Text("Start Trace") }
            Button(
                onClick = actions.stop,
                enabled = controlsEnabled && trace.running,
                modifier = Modifier.fillMaxWidth(),
            ) { Text("Stop / Flush Trace") }
            Nice64MetadataRow("Session ID", trace.sessionId ?: "—")
            Nice64MetadataRow("Records", trace.records.toString())
            Nice64MetadataRow("Dropped", trace.dropped.toString())
            Nice64MetadataRow("Trace file", trace.fileName ?: "—")
            trace.summaryFileName?.let { Nice64MetadataRow("Summary file", it) }
            trace.latestLabel?.let { Nice64MetadataRow("Latest event", it) }
            trace.latestAndroidEventToDispatchMs?.let {
                Nice64MetadataRow("Android event → app", "$it ms")
            }
            trace.latestDispatchToMappedMs?.let {
                Nice64MetadataRow("App dispatch → map", it.ms())
            }
            trace.latestDispatchToHolderMs?.let {
                Nice64MetadataRow("App dispatch → holder", it.ms())
            }
            trace.latestDispatchToUiModelMs?.let {
                Nice64MetadataRow("App dispatch → UI", it.ms())
            }
            trace.failure?.let {
                Nice64ErrorState(title = "Controller timing trace failed", message = it)
            }
        }
    }
}

private fun Double.ms(): String = String.format(Locale.ROOT, "%.3f ms", this)
