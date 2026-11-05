package com.melfegor.nice64

import androidx.compose.material3.MaterialTheme
import androidx.compose.foundation.layout.Column
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import com.melfegor.nice64.api.BridgeControllerDataPlanePresentation
import com.melfegor.nice64.api.BridgeControllerEndpointDto
import com.melfegor.nice64.api.BridgeControllerPrimaryDto
import com.melfegor.nice64.api.BridgeControllerPrimaryPhase
import com.melfegor.nice64.api.BridgeControllerPrimaryPresentation
import com.melfegor.nice64.api.BridgeControllerInputPresentation
import com.melfegor.nice64.api.MobileInputMode
import com.melfegor.nice64.api.BridgeDashboardSnapshot
import com.melfegor.nice64.api.BridgeSection
import com.melfegor.nice64.api.BridgeOperationDto
import org.junit.Rule
import org.junit.Test

@Suppress("DEPRECATION")
class ControllerPrimaryUiTest {
    @get:Rule val compose = createComposeRule()

    @Test fun bridgePrimaryOffersExplicitMobileSelection() {
        show(BridgeControllerPrimaryPresentation())
        compose.onNodeWithText("Controller authority").assertIsDisplayed()
        compose.onNodeWithText("Bridge Primary").assertIsDisplayed()
        compose.onNodeWithText("NICE Mobile Primary").assertIsEnabled()
    }

    @Test fun unavailableStoredEndpointDoesNotPreemptAuthoritativeMobileRequest() {
        var selected = false
        show(
            BridgeControllerPrimaryPresentation(),
            endpoint = BridgeControllerEndpointDto(
                false, "endpoint-unavailable", null, null, null, null, null,
            ),
            actions = BridgeControllerPrimaryActions(selectMobile = { selected = true }),
        )
        compose.onNodeWithText("NICE Mobile Primary").assertIsEnabled().performClick()
        compose.runOnIdle { assert(selected) }
    }

    @Test fun unrelatedActionDisablesMobileSelection() {
        show(BridgeControllerPrimaryPresentation(), otherActionBusy = true)
        compose.onNodeWithText("NICE Mobile Primary").assertIsNotEnabled()
    }

    @Test fun runningBridgeOperationDisablesMobileSelection() {
        show(
            BridgeControllerPrimaryPresentation(),
            operation = BridgeOperationDto(
                7, "history-activation", "running", null, null, "none", false,
                "history", null, null, null,
            ),
        )
        compose.onNodeWithText("NICE Mobile Primary").assertIsNotEnabled()
    }

    @Test fun mobilePrimaryOffersExplicitBridgeSelection() {
        show(BridgeControllerPrimaryPresentation(
            BridgeControllerPrimaryPhase.MOBILE_PRIMARY, "profile", "session-1",
            endpoint(), 1))
        compose.onNodeWithText("NICE Mobile Primary").assertIsDisplayed()
        compose.onNodeWithText("Bridge Primary").assertIsEnabled()
    }

    @Test fun authorityAndMobileInputRemainDistinctPresentationSections() {
        compose.setContent {
            MaterialTheme {
                Column {
                    BridgeControllerPrimaryControls(
                        dashboard(endpoint(), null),
                        BridgeControllerPrimaryPresentation(
                            BridgeControllerPrimaryPhase.MOBILE_PRIMARY,
                            "profile",
                            "session-1",
                            endpoint(),
                            1,
                        ),
                        BridgeControllerDataPlanePresentation(),
                        false,
                        BridgeControllerPrimaryActions(),
                    )
                    BridgeTouchController(
                        BridgeControllerInputPresentation(
                            enabled = true,
                            inputMode = MobileInputMode.CONTROLLER,
                            sourceLabel = "Controller: Test controller",
                        ),
                        BridgeControllerInputActions(),
                    )
                }
            }
        }

        compose.onNodeWithText("Controller authority").assertIsDisplayed()
        compose.onNodeWithText("Mobile input").assertIsDisplayed()
    }

    @Test fun switchingStateIsStableAndNonLeaseBased() {
        show(BridgeControllerPrimaryPresentation(
            BridgeControllerPrimaryPhase.SWITCHING_TO_MOBILE,
            "profile", "session-1"))
        compose.onNodeWithText("Switching to NICE Mobile…").assertIsDisplayed()
        compose.onNodeWithText("NICE Mobile Primary").assertIsNotEnabled()
    }

    private fun show(
        primary: BridgeControllerPrimaryPresentation,
        endpoint: BridgeControllerEndpointDto = endpoint(),
        otherActionBusy: Boolean = false,
        operation: BridgeOperationDto? = null,
        actions: BridgeControllerPrimaryActions = BridgeControllerPrimaryActions(),
    ) {
        compose.setContent {
            MaterialTheme {
                BridgeControllerPrimaryControls(dashboard(endpoint, operation), primary,
                    BridgeControllerDataPlanePresentation(), otherActionBusy, actions)
            }
        }
    }

    private fun dashboard(endpoint: BridgeControllerEndpointDto, operation: BridgeOperationDto?) =
        BridgeDashboardSnapshot(
            controller = BridgeSection(value = BridgeControllerPrimaryDto(
                "v1", "session-1", "bridge", endpoint,
            )),
            operation = BridgeSection(value = operation),
        )

    private fun endpoint() = BridgeControllerEndpointDto(true, null,
        "192.168.2.81", 6465, "udp", "latest-state-v1", "n64")
}
