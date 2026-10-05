package com.melfegor.nice64.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import com.melfegor.nice64.ui.components.Nice64EmptyState
import com.melfegor.nice64.ui.components.Nice64ErrorState
import com.melfegor.nice64.ui.components.Nice64LoadingState
import com.melfegor.nice64.ui.components.Nice64MetadataRow
import com.melfegor.nice64.ui.components.Nice64OperationStatus
import com.melfegor.nice64.ui.components.Nice64StatusChip
import com.melfegor.nice64.ui.components.Nice64StatusTone
import com.melfegor.nice64.ui.theme.Nice64Theme
import org.junit.Assert.assertNotEquals
import org.junit.Rule
import org.junit.Test

@Suppress("DEPRECATION")
class Nice64PresentationInstrumentedTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun explicitLightAndDarkThemesApplyDistinctSchemes() {
        var lightPrimary = Color.Unspecified
        var darkPrimary = Color.Unspecified
        compose.setContent {
            Nice64Theme(darkTheme = false) {
                lightPrimary = MaterialTheme.colorScheme.primary
                Nice64Theme(darkTheme = true) {
                    darkPrimary = MaterialTheme.colorScheme.primary
                    Text("Themed content")
                }
            }
        }

        compose.onNodeWithText("Themed content").assertIsDisplayed()
        compose.runOnIdle {
            assertNotEquals(Color.Unspecified, lightPrimary)
            assertNotEquals(Color.Unspecified, darkPrimary)
            assertNotEquals(lightPrimary, darkPrimary)
        }
    }

    @Test
    fun statusChipCommunicatesStateWithoutDependingOnColor() {
        compose.setContent {
            Nice64Theme {
                Nice64StatusChip("Bridge connected", Nice64StatusTone.HEALTHY)
            }
        }

        compose.onNodeWithText("Bridge connected").assertIsDisplayed()
        compose.onNodeWithContentDescription("Healthy status: Bridge connected")
            .assertIsDisplayed()
    }

    @Test
    fun feedbackPrimitivesRenderSuppliedAuthoritativePresentation() {
        compose.setContent {
            Nice64Theme {
                androidx.compose.foundation.layout.Column {
                    Nice64LoadingState("Loading Bridge state")
                    Nice64EmptyState(
                        title = "No paired Bridge",
                        message = "Pair a Bridge to continue",
                    )
                    Nice64ErrorState("Bridge unavailable", message = "Refresh to try again")
                    Nice64OperationStatus(
                        label = "Installing ROM",
                        detail = "128 of 256 pages",
                        progress = 0.5f,
                    )
                }
            }
        }

        compose.onNodeWithText("Loading Bridge state").assertIsDisplayed()
        compose.onNodeWithText("No paired Bridge").assertIsDisplayed()
        compose.onNodeWithText("Bridge unavailable").assertIsDisplayed()
        compose.onNodeWithText("Installing ROM").assertIsDisplayed()
    }

    @Test
    fun metadataPresentationKeepsLabelAndTechnicalValueReadable() {
        val endpoint = "https://192.168.2.81:6470/" + "very-long-profile-identity-".repeat(8)
        compose.setContent {
            Nice64Theme {
                Nice64MetadataRow("Endpoint", endpoint)
            }
        }

        compose.onNodeWithText("Endpoint").assertIsDisplayed()
        compose.onNodeWithContentDescription("Endpoint: $endpoint").assertIsDisplayed()
    }
}
