package com.melfegor.nice64

import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import com.melfegor.nice64.api.ControllerInputTracePresentation
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

@Suppress("DEPRECATION")
class ControllerInputTraceControlsInstrumentedTest {
    @get:Rule val compose = createComposeRule()

    @Test fun stoppedTraceOffersStartAndReportsCounters() {
        var started = false
        compose.setContent {
            MaterialTheme {
                ControllerInputTraceControls(
                    ControllerInputTracePresentation(records = 7, dropped = 2),
                    ControllerInputTraceActions(start = { started = true }),
                )
            }
        }

        compose.onNodeWithText("Trace stopped").assertIsDisplayed()
        compose.onNodeWithText("Records").assertIsDisplayed()
        compose.onNodeWithText("7").assertIsDisplayed()
        compose.onNodeWithText("Dropped").assertIsDisplayed()
        compose.onNodeWithText("2").assertIsDisplayed()
        compose.onNodeWithText("Start Trace").assertIsEnabled().performClick()
        compose.onNodeWithText("Stop / Flush Trace").assertIsNotEnabled()
        compose.runOnIdle { assert(started) }
    }

    @Test fun runningTraceShowsIdentityFileAndLatestTiming() {
        compose.setContent {
            MaterialTheme {
                ControllerInputTraceControls(
                    ControllerInputTracePresentation(
                        running = true,
                        sessionId = "session-1",
                        records = 3,
                        fileName = "touch-trigger-session-1.jsonl",
                        latestLabel = "N64 A DOWN",
                        latestAndroidEventToDispatchMs = 2,
                        latestDispatchToMappedMs = 0.31,
                        latestDispatchToHolderMs = 0.47,
                        latestDispatchToUiModelMs = 0.62,
                    ),
                    ControllerInputTraceActions(),
                )
            }
        }

        compose.onNodeWithText("Trace running").assertIsDisplayed()
        compose.onNodeWithText("session-1").assertIsDisplayed()
        compose.onNodeWithText("touch-trigger-session-1.jsonl").assertIsDisplayed()
        compose.onNodeWithText("N64 A DOWN").assertIsDisplayed()
        compose.onNodeWithText("2 ms").assertIsDisplayed()
        compose.onNodeWithText("0.310 ms").assertIsDisplayed()
    }

    @Test fun renderingTraceDoesNotInvokeLifecycleActions() {
        var starts = 0
        var stops = 0
        compose.setContent {
            MaterialTheme {
                ControllerInputTraceControls(
                    ControllerInputTracePresentation(failure = "Trace storage unavailable."),
                    ControllerInputTraceActions(start = { starts++ }, stop = { stops++ }),
                )
            }
        }

        compose.onNodeWithText("Controller timing trace failed").assertIsDisplayed()
        compose.onNodeWithText("Trace storage unavailable.").assertIsDisplayed()
        compose.runOnIdle {
            assertEquals(0, starts)
            assertEquals(0, stops)
        }
    }
}
