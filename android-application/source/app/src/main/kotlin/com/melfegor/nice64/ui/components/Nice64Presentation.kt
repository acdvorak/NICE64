package com.melfegor.nice64.ui.components

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.melfegor.nice64.R
import com.melfegor.nice64.ui.theme.Nice64ShapeTokens
import com.melfegor.nice64.ui.theme.Nice64Spacing
import com.melfegor.nice64.ui.theme.Nice64TextStyles

enum class Nice64StatusTone(internal val glyph: String) {
    HEALTHY("✓"),
    UNAVAILABLE("—"),
    WARNING("!"),
    ERROR("×"),
    IN_PROGRESS("…"),
    INFORMATIONAL("i"),
}

private data class StatusPalette(val container: Color, val content: Color)

@Composable
private fun Nice64StatusTone.semanticLabel(): String = stringResource(
    when (this) {
        Nice64StatusTone.HEALTHY -> R.string.nice64_status_healthy
        Nice64StatusTone.UNAVAILABLE -> R.string.nice64_status_unavailable
        Nice64StatusTone.WARNING -> R.string.nice64_status_warning
        Nice64StatusTone.ERROR -> R.string.nice64_status_error
        Nice64StatusTone.IN_PROGRESS -> R.string.nice64_status_in_progress
        Nice64StatusTone.INFORMATIONAL -> R.string.nice64_status_information
    },
)

@Composable
private fun Nice64StatusTone.palette(): StatusPalette = when (this) {
    Nice64StatusTone.HEALTHY -> StatusPalette(
        MaterialTheme.colorScheme.primaryContainer,
        MaterialTheme.colorScheme.onPrimaryContainer,
    )
    Nice64StatusTone.UNAVAILABLE -> StatusPalette(
        MaterialTheme.colorScheme.surfaceVariant,
        MaterialTheme.colorScheme.onSurfaceVariant,
    )
    Nice64StatusTone.WARNING -> StatusPalette(
        MaterialTheme.colorScheme.tertiaryContainer,
        MaterialTheme.colorScheme.onTertiaryContainer,
    )
    Nice64StatusTone.ERROR -> StatusPalette(
        MaterialTheme.colorScheme.errorContainer,
        MaterialTheme.colorScheme.onErrorContainer,
    )
    Nice64StatusTone.IN_PROGRESS -> StatusPalette(
        MaterialTheme.colorScheme.secondaryContainer,
        MaterialTheme.colorScheme.onSecondaryContainer,
    )
    Nice64StatusTone.INFORMATIONAL -> StatusPalette(
        MaterialTheme.colorScheme.primaryContainer,
        MaterialTheme.colorScheme.onPrimaryContainer,
    )
}

@Composable
fun Nice64StatusChip(
    text: String,
    tone: Nice64StatusTone,
    modifier: Modifier = Modifier,
) {
    val palette = tone.palette()
    val semanticLabel = tone.semanticLabel()
    Surface(
        modifier = modifier.semantics(mergeDescendants = true) {
            contentDescription = "$semanticLabel status: $text"
            stateDescription = semanticLabel
        },
        shape = Nice64ShapeTokens.pill,
        color = palette.container,
        contentColor = palette.content,
    ) {
        Row(
            modifier = Modifier.padding(horizontal = Nice64Spacing.md, vertical = Nice64Spacing.xs),
            horizontalArrangement = Arrangement.spacedBy(Nice64Spacing.xs),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(tone.glyph, style = MaterialTheme.typography.labelLarge)
            Text(text, style = MaterialTheme.typography.labelLarge)
        }
    }
}

@Composable
fun Nice64Badge(
    text: String,
    modifier: Modifier = Modifier,
) {
    Surface(
        modifier = modifier.semantics(mergeDescendants = true) {
            contentDescription = text
        },
        shape = Nice64ShapeTokens.pill,
        color = MaterialTheme.colorScheme.surfaceVariant,
        contentColor = MaterialTheme.colorScheme.onSurfaceVariant,
    ) {
        Text(
            text,
            modifier = Modifier.padding(
                horizontal = Nice64Spacing.md,
                vertical = Nice64Spacing.xs,
            ),
            style = MaterialTheme.typography.labelLarge,
        )
    }
}

@Composable
fun Nice64StatusBanner(
    title: String,
    tone: Nice64StatusTone,
    modifier: Modifier = Modifier,
    message: String? = null,
) {
    val palette = tone.palette()
    val semanticLabel = tone.semanticLabel()
    Surface(
        modifier = modifier
            .fillMaxWidth()
            .semantics(mergeDescendants = true) {
                contentDescription = buildString {
                    append(semanticLabel)
                    append(" status: ")
                    append(title)
                    message?.let { append(". ").append(it) }
                }
                stateDescription = semanticLabel
            },
        shape = Nice64ShapeTokens.standard,
        color = palette.container,
        contentColor = palette.content,
    ) {
        Row(
            modifier = Modifier.padding(Nice64Spacing.md),
            horizontalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
            verticalAlignment = Alignment.Top,
        ) {
            Text(tone.glyph, style = MaterialTheme.typography.titleMedium)
            Column(verticalArrangement = Arrangement.spacedBy(Nice64Spacing.xs)) {
                Text(title, style = MaterialTheme.typography.titleSmall)
                message?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
            }
        }
    }
}

@Composable
fun Nice64LoadingState(
    message: String,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .padding(Nice64Spacing.lg),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
    ) {
        CircularProgressIndicator(modifier = Modifier.size(32.dp))
        Text(message, style = MaterialTheme.typography.bodyMedium)
    }
}

@Composable
fun Nice64EmptyState(
    title: String,
    modifier: Modifier = Modifier,
    message: String? = null,
    actionLabel: String? = null,
    onAction: (() -> Unit)? = null,
) {
    Nice64MessageState(title, message, modifier, actionLabel, onAction)
}

@Composable
fun Nice64ErrorState(
    title: String,
    modifier: Modifier = Modifier,
    message: String? = null,
    retryLabel: String? = null,
    onRetry: (() -> Unit)? = null,
) {
    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
    ) {
        Nice64StatusBanner(title = title, tone = Nice64StatusTone.ERROR, message = message)
        if (retryLabel != null && onRetry != null) {
            Button(onClick = onRetry, modifier = Modifier.align(Alignment.End)) {
                Text(retryLabel)
            }
        }
    }
}

@Composable
private fun Nice64MessageState(
    title: String,
    message: String?,
    modifier: Modifier,
    actionLabel: String?,
    onAction: (() -> Unit)?,
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .padding(Nice64Spacing.lg),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
    ) {
        Text(title, style = MaterialTheme.typography.titleMedium)
        message?.let {
            Text(it, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (actionLabel != null && onAction != null) {
            Button(onClick = onAction) { Text(actionLabel) }
        }
    }
}

@Composable
fun Nice64OperationStatus(
    label: String,
    modifier: Modifier = Modifier,
    detail: String? = null,
    progress: Float? = null,
) {
    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(Nice64Spacing.xs),
    ) {
        Text(label, style = MaterialTheme.typography.labelLarge)
        detail?.let {
            Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (progress == null) {
            LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
        } else {
            LinearProgressIndicator(
                progress = { progress.coerceIn(0f, 1f) },
                modifier = Modifier.fillMaxWidth(),
            )
        }
    }
}

@Composable
fun Nice64DisabledState(
    explanation: String,
    modifier: Modifier = Modifier,
    title: String? = null,
) {
    Nice64StatusBanner(
        title = title ?: stringResource(R.string.nice64_status_unavailable),
        tone = Nice64StatusTone.UNAVAILABLE,
        modifier = modifier,
        message = explanation,
    )
}

@Composable
fun Nice64MetadataRow(
    label: String,
    value: String,
    modifier: Modifier = Modifier,
    maxLines: Int = 3,
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .widthIn(min = 0.dp)
            .clip(Nice64ShapeTokens.compact)
            .semantics(mergeDescendants = true) {
                contentDescription = "$label: $value"
            }
            .padding(vertical = Nice64Spacing.xs),
        verticalArrangement = Arrangement.spacedBy(Nice64Spacing.xxs),
    ) {
        Text(
            label,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(
            value,
            style = Nice64TextStyles.technicalMetadata,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = maxLines,
            overflow = TextOverflow.Ellipsis,
            softWrap = true,
        )
    }
}
