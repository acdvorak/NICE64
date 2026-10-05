package com.melfegor.nice64

import androidx.activity.compose.setContent
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onAllNodesWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.hasClickAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.api.BridgeDashboardSnapshot
import com.melfegor.nice64.api.BridgeHistoryActivationPhase
import com.melfegor.nice64.api.BridgeHistoryActivationFailure
import com.melfegor.nice64.api.BridgeHistoryActivationFailureKind
import com.melfegor.nice64.api.BridgeHistoryActivationPresentation
import com.melfegor.nice64.api.BridgeHistoryCategory
import com.melfegor.nice64.api.BridgeHistoryDto
import com.melfegor.nice64.api.BridgeHistoryItemDto
import com.melfegor.nice64.api.BridgeOperationDto
import com.melfegor.nice64.api.BridgeSection
import com.melfegor.nice64.api.BridgeSessionDeviceDto
import com.melfegor.nice64.api.BridgeSessionDto
import com.melfegor.nice64.core.model.BridgePairingMetadata
import com.melfegor.nice64.pairing.PairingPresentationState
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class HistoryActivationUiTest {
    @get:Rule
    @Suppress("DEPRECATION")
    val composeRule = createAndroidComposeRule<MainActivity>()

    @Test
    fun authoritativeItemsAndUnavailableStateAreVisible() {
        show()
        composeRule.onNodeWithText("recent.mpk").assertIsDisplayed()
        composeRule.onNodeWithText("crystal.gbc").assertIsDisplayed()
        composeRule.onNodeWithText("Available").assertIsDisplayed()
        composeRule.onNodeWithText("Unavailable").assertIsDisplayed()
        composeRule.onNodeWithText("Activate Transfer Pak").assertIsNotEnabled()
    }

    @Test
    fun controllerPakConfirmationCanOnlySubmitFalse() {
        var selected: Triple<BridgeHistoryCategory, String, Boolean>? = null
        show(actions = BridgeHistoryActivationActions { category, item, confirmed ->
            selected = Triple(category, item.itemId, confirmed)
        })
        composeRule.onNodeWithText("Activate Controller Pak").performClick()
        composeRule.onNodeWithText("Activate Controller Pak History?").assertIsDisplayed()
        composeRule.onNodeWithText("Activate").performClick()
        composeRule.runOnIdle {
            assert(selected == Triple(BridgeHistoryCategory.CONTROLLER_PAK, "cp-id", false))
        }
    }

    @Test
    fun transferPakRequiresExplicitConfirmationAndCancelDoesNotSubmit() {
        var calls = 0
        var confirmed = false
        show(transferAvailable = true, actions = BridgeHistoryActivationActions { _, _, value ->
            calls++
            confirmed = value
        })
        composeRule.onNodeWithText("Activate Transfer Pak").performClick()
        composeRule.onNodeWithText("This may replace the currently associated cartridge/ROM state. The Bridge will enforce persistence and transition safety.")
            .assertIsDisplayed()
        composeRule.onNodeWithText("Cancel").performClick()
        composeRule.runOnIdle { assert(calls == 0) }
        composeRule.onNodeWithText("Activate Transfer Pak").performClick()
        composeRule.onNodeWithText("Confirm and activate").performClick()
        composeRule.runOnIdle { assert(calls == 1 && confirmed) }
    }

    @Test
    fun pendingActivationDisablesHistoryControls() {
        show(activation = BridgeHistoryActivationPresentation(
            phase = BridgeHistoryActivationPhase.RECONCILING,
            profileId = "profile",
            sessionId = "session-1",
            category = BridgeHistoryCategory.CONTROLLER_PAK,
            fileName = "recent.mpk",
            operationId = 7,
        ))
        composeRule.onNodeWithText("Loading recent.mpk…").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithContentDescription("History activation in progress")
            .performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("Activate Controller Pak").assertIsNotEnabled()
    }

    @Test
    fun terminalActivationClearsLoadingIndicator() {
        show(activation = BridgeHistoryActivationPresentation(
            phase = BridgeHistoryActivationPhase.SUCCEEDED,
            profileId = "profile",
            sessionId = "session-1",
            category = BridgeHistoryCategory.TRANSFER_PAK,
            fileName = "Pokemon Crystal.gbc",
            operationId = 9,
        ))
        composeRule.onNodeWithText("History item activated").performScrollTo().assertIsDisplayed()
        composeRule.onAllNodesWithContentDescription("History activation in progress")
            .assertCountEquals(0)

        show(activation = BridgeHistoryActivationPresentation(
            phase = BridgeHistoryActivationPhase.FAILED,
            profileId = "profile",
            sessionId = "session-1",
            category = BridgeHistoryCategory.TRANSFER_PAK,
            fileName = "Pokemon Crystal.gbc",
            operationId = 10,
            failure = BridgeHistoryActivationFailure(
                BridgeHistoryActivationFailureKind.OPERATION_FAILED,
                "The Bridge History activation did not complete successfully.",
            ),
        ))
        composeRule.onNodeWithText(
            "The Bridge History activation did not complete successfully.",
        ).performScrollTo().assertIsDisplayed()
        composeRule.onAllNodesWithContentDescription("History activation in progress")
            .assertCountEquals(0)
    }

    private fun show(
        transferAvailable: Boolean = false,
        activation: BridgeHistoryActivationPresentation =
            BridgeHistoryActivationPresentation(),
        actions: BridgeHistoryActivationActions = BridgeHistoryActivationActions(),
    ) {
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Nice64App(
                    state = pairedState(),
                    dashboard = dashboard(transferAvailable),
                    historyActivation = activation,
                    onBeginPairing = {}, onPermission = { _, _ -> }, onQrCode = {},
                    onConfirmPairing = {}, onCancelPairing = {}, onRetryScan = {},
                    onSelect = {}, onRequestUnpair = {}, onCancelUnpair = {},
                    onConfirmUnpair = {},
                    historyActivationActions = actions,
                )
            }
        }
        composeRule.onNode(hasText("History") and hasClickAction()).performClick()
    }

    private fun pairedState(): PairingPresentationState {
        val profile = BridgePairingMetadata(
            "profile", "NICE64", "https://192.168.2.36:6470", "v1",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "A".repeat(64), 1,
        )
        return PairingPresentationState(profiles = listOf(profile), activeProfileId = "profile")
    }

    private fun dashboard(transferAvailable: Boolean): BridgeDashboardSnapshot {
        val controller = BridgeHistoryItemDto(
            "cp-id", "recent.mpk", "2026-09-17T00:00:00Z", true,
        )
        val transfer = BridgeHistoryItemDto(
            "tp-id", "crystal.gbc", "2026-09-16T00:00:00Z", transferAvailable,
        )
        return BridgeDashboardSnapshot(
            profileId = "profile",
            operation = BridgeSection(value = BridgeOperationDto(
                0, "none", "idle", null, null, "none", false,
                "none", null, null, null,
            )),
            history = BridgeSection(value = BridgeHistoryDto(
                "v1", 1, listOf(controller), listOf(transfer),
            )),
            currentSession = BridgeSection(value = BridgeSessionDto(
                "session-1", "connected", "2026-09-17T00:00:00Z",
                "2026-09-17T00:00:00Z", "wifi-tcp",
                BridgeSessionDeviceDto("NICE64", "controller", "controller-pak"),
            )),
        )
    }
}
