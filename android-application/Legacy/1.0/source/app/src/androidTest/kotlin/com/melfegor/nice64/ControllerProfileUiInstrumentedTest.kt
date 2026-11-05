package com.melfegor.nice64

import android.view.InputDevice
import android.view.MotionEvent
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.api.BridgeControllerDiagnosticDevice
import com.melfegor.nice64.api.BridgeControllerDiagnosticsPresentation
import com.melfegor.nice64.api.BridgeControllerMotionRange
import com.melfegor.nice64.api.BridgeControllerInputPresentation
import com.melfegor.nice64.api.ControllerProfilePresentationState
import com.melfegor.nice64.api.ControllerProfileResolutionKind
import com.melfegor.nice64.api.ControllerProfileUiItem
import com.melfegor.nice64.api.ControllerInputTracePresentation
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.atomic.AtomicReference

@RunWith(AndroidJUnit4::class)
class ControllerProfileUiInstrumentedTest {
    @get:Rule
    @Suppress("DEPRECATION")
    val composeRule = createAndroidComposeRule<MainActivity>()

    @Test fun controllerProfileShellKeepsDiagnosticsBehindDeliberateEntry() {
        val device = device()
        val state = ControllerProfilePresentationState(
            loading = false,
            devices = listOf(device),
            activeRuntimeDeviceId = 27,
            managementRuntimeDeviceId = 27,
            profiles = listOf(
                ControllerProfileUiItem("a", "Profile A", "VID 057E / PID 2009", true),
                ControllerProfileUiItem("b", "Profile B", "VID 057E / PID 2009", false),
            ),
            selectedProfileId = "a",
            resolutionKind = ControllerProfileResolutionKind.AMBIGUOUS,
            resolutionProfileIds = listOf("a", "b"),
        )
        val diagnostics = BridgeControllerDiagnosticsPresentation(devices = listOf(device), activeRuntimeDeviceId = 27)
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    BridgeControllersScreen(state, diagnostics)
                }
            }
        }
        composeRule.onNodeWithText("Controller summary").assertIsDisplayed()
        composeRule.onNodeWithText("Controller observed").assertIsDisplayed()
        composeRule.onAllNodesWithText("Standard gamepad mapping").assertCountEquals(2)
        composeRule.onAllNodesWithText("Built-in profile").assertCountEquals(2)
        composeRule.onNodeWithText("Profile A").assertIsDisplayed()
        composeRule.onNodeWithText("Profile B").performScrollTo().assertIsDisplayed()
        composeRule.onAllNodesWithText("Custom profile").assertCountEquals(2)
        composeRule.onNodeWithText("Multiple possible profiles").performScrollTo().assertIsDisplayed()
        composeRule.onAllNodesWithText("Profile resolution").assertCountEquals(0)
        composeRule.onAllNodesWithText("Rename is", substring = true).assertCountEquals(0)
        composeRule.onAllNodesWithText("Runtime device ID").assertCountEquals(0)
        composeRule.onAllNodesWithText("Start Trace").assertCountEquals(0)
        composeRule.onNodeWithText("Advanced diagnostics").performScrollTo().performClick()
        composeRule.onNodeWithText("Runtime device ID").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("27 (transient)").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("Controller identifier suffix").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("Android input sources").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("View Inputs").performScrollTo().performClick()
        composeRule.onNodeWithText("Viewing controller")
            .performScrollTo()
            .assertIsDisplayed()
        composeRule.onNodeWithText("Start Trace").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("Back to Controllers").performScrollTo().performClick()
        composeRule.onNodeWithText("Controllers").performScrollTo().assertIsDisplayed()
        composeRule.onAllNodesWithText("Runtime device ID").assertCountEquals(0)
    }

    @Test fun diagnosticsVisibilityDoesNotInvokeTraceLifecycleAndRunningStateSurvives() {
        var starts = 0
        var stops = 0
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    BridgeControllersScreen(
                        state = ControllerProfilePresentationState(loading = false),
                        diagnostics = BridgeControllerDiagnosticsPresentation(),
                        trace = ControllerInputTracePresentation(
                            running = true,
                            records = 12,
                            dropped = 3,
                        ),
                        traceActions = ControllerInputTraceActions(
                            start = { starts++ },
                            stop = { stops++ },
                        ),
                    )
                }
            }
        }

        composeRule.onAllNodesWithText("Trace running").assertCountEquals(0)
        composeRule.onNodeWithText("Advanced diagnostics").performScrollTo().performClick()
        composeRule.onNodeWithText("Trace running").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("12").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("Back to Controllers").performScrollTo().performClick()
        composeRule.onAllNodesWithText("Trace running").assertCountEquals(0)
        composeRule.onNodeWithText("Advanced diagnostics").performScrollTo().performClick()
        composeRule.onNodeWithText("Trace running").performScrollTo().assertIsDisplayed()
        composeRule.runOnIdle {
            assertEquals(0, starts)
            assertEquals(0, stops)
        }
    }

    @Test fun selectAndConfirmedDeleteCallbacksAreBounded() {
        val device = device()
        val state = ControllerProfilePresentationState(
            loading = false,
            devices = listOf(device),
            managementRuntimeDeviceId = 27,
            profiles = listOf(ControllerProfileUiItem("custom", "Custom", "Identity", false)),
            resolutionKind = ControllerProfileResolutionKind.STANDARD_DEFAULT,
        )
        val selected = AtomicReference<String?>()
        val deleted = AtomicReference<String?>()
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    BridgeControllersScreen(
                        state,
                        BridgeControllerDiagnosticsPresentation(devices = listOf(device)),
                        ControllerProfileActions(
                            onSelectProfile = { selected.set(it) },
                            onDelete = { deleted.set(it) },
                        ),
                    )
                }
            }
        }
        composeRule.onNodeWithTag("select-profile-custom").performScrollTo().performClick()
        composeRule.waitUntil { selected.get() == "custom" }
        assertEquals("custom", selected.get())
        composeRule.onNodeWithTag("delete-profile-custom").performScrollTo().performClick()
        composeRule.onNodeWithText("Delete controller profile?").assertIsDisplayed()
        composeRule.onNodeWithText("Confirm delete").performClick()
        composeRule.waitUntil { deleted.get() == "custom" }
        assertEquals("custom", deleted.get())
    }

    @Test fun liveGameplayMappingStatusShowsCustomAndSafeFallbackReason() {
        val observed = device()
        val state = ControllerProfilePresentationState(
            loading = false,
            devices = listOf(observed),
            activeRuntimeDeviceId = observed.runtimeDeviceId,
            managementRuntimeDeviceId = observed.runtimeDeviceId,
        )
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    BridgeControllersScreen(
                        state = state,
                        diagnostics = BridgeControllerDiagnosticsPresentation(),
                        liveInput = BridgeControllerInputPresentation(
                            enabled = true,
                            gameplayMappingLabel = "Custom — My Switch Pro",
                        ),
                    )
                }
            }
        }
        composeRule.onNodeWithText("Active gameplay profile").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("Custom — My Switch Pro").performScrollTo().assertIsDisplayed()

        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    BridgeControllersScreen(
                        state = state,
                        diagnostics = BridgeControllerDiagnosticsPresentation(),
                        liveInput = BridgeControllerInputPresentation(
                            enabled = true,
                            gameplayMappingLabel = "Standard Android Gamepad",
                            gameplayMappingReason = "Multiple custom profiles match the active controller.",
                        ),
                    )
                }
            }
        }
        composeRule.onAllNodesWithText("Standard gamepad mapping").assertCountEquals(2)
        composeRule.onNodeWithText("Multiple custom profiles match the active controller.")
            .performScrollTo().assertIsDisplayed()
    }

    @Test fun noControllerAndUnresolvedMatchUseBoundedPresentation() {
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    BridgeControllersScreen(
                        state = ControllerProfilePresentationState(
                            loading = false,
                            resolutionKind = ControllerProfileResolutionKind.INELIGIBLE,
                        ),
                        diagnostics = BridgeControllerDiagnosticsPresentation(),
                    )
                }
            }
        }

        composeRule.onNodeWithText("No physical controller observed").assertIsDisplayed()
        composeRule.onNodeWithText("No custom profiles").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("Controller profile match").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("No matching controller").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("Advanced diagnostics").performScrollTo().performClick()
        composeRule.onNodeWithText("No controller diagnostics").performScrollTo().assertIsDisplayed()
    }

    private fun device() = BridgeControllerDiagnosticDevice(
        27,
        "test-controller-descriptor-1234567890",
        "Nintendo Switch Pro Controller",
        0x057e,
        0x2009,
        InputDevice.SOURCE_GAMEPAD or InputDevice.SOURCE_JOYSTICK,
        true,
        true,
        1,
        listOf(BridgeControllerMotionRange(
            MotionEvent.AXIS_X, "AXIS_X", -1f, 1f, 0.015f, InputDevice.SOURCE_JOYSTICK,
        )),
    )
}
