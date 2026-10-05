package com.melfegor.nice64

import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertIsSelected
import androidx.activity.compose.setContent
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.hasClickAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.api.BridgeConnectionPresentation
import com.melfegor.nice64.api.BridgeDashboardSnapshot
import com.melfegor.nice64.api.BridgeSection
import com.melfegor.nice64.api.BridgeStateDto
import com.melfegor.nice64.api.BridgeIdentityDto
import com.melfegor.nice64.api.BridgeDeviceDto
import com.melfegor.nice64.api.BridgeTransitionDto
import com.melfegor.nice64.api.BridgeCapabilityDto
import com.melfegor.nice64.api.BridgeControllerInputDto
import com.melfegor.nice64.api.BridgeControllerStateDto
import com.melfegor.nice64.api.BridgeControllerPakDto
import com.melfegor.nice64.api.BridgeTransferPakDto
import com.melfegor.nice64.api.BridgePersistenceDto
import com.melfegor.nice64.api.BridgeModuleDto
import com.melfegor.nice64.api.BridgeModuleInfoDto
import com.melfegor.nice64.api.BridgeWifiDto
import com.melfegor.nice64.api.BridgeOperationDto
import com.melfegor.nice64.api.BridgeOperationProgressDto
import com.melfegor.nice64.api.BridgeHistoryDto
import com.melfegor.nice64.api.BridgeHistoryItemDto
import com.melfegor.nice64.api.BridgeAccessoryTarget
import com.melfegor.nice64.api.BridgeMutationKind
import com.melfegor.nice64.api.BridgeMutationPhase
import com.melfegor.nice64.api.BridgeMutationPresentation
import com.melfegor.nice64.core.model.BridgePairingMetadata
import com.melfegor.nice64.pairing.PairingPresentationState
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.junit.Assert.assertEquals

@RunWith(AndroidJUnit4::class)
class MainActivitySmokeTest {
    @get:Rule
    @Suppress("DEPRECATION")
    val composeRule = createAndroidComposeRule<MainActivity>()

    @Test
    fun pairingHomeIsDisplayed() {
        var beganPairing = false
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Nice64App(
                    state = PairingPresentationState(),
                    onBeginPairing = { beganPairing = true }, onPermission = { _, _ -> }, onQrCode = {},
                    onConfirmPairing = {}, onCancelPairing = {}, onRetryScan = {},
                    onSelect = {}, onRequestUnpair = {}, onCancelUnpair = {},
                    onConfirmUnpair = {},
                )
            }
        }
        composeRule.onNodeWithText("NICE64").assertIsDisplayed()
        composeRule.onNodeWithText("No paired Bridge").assertIsDisplayed()
        composeRule.onNodeWithText("Pair Bridge").assertIsDisplayed().performClick()
        composeRule.runOnIdle { assert(beganPairing) }
    }

    @Test
    fun activityRecreationKeepsOneUsablePresentationWithoutRestoringControllerUi() {
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Nice64App(
                    state = PairingPresentationState(),
                    onBeginPairing = {}, onPermission = { _, _ -> }, onQrCode = {},
                    onConfirmPairing = {}, onCancelPairing = {}, onRetryScan = {},
                    onSelect = {}, onRequestUnpair = {}, onCancelUnpair = {},
                    onConfirmUnpair = {},
                )
            }
        }

        composeRule.activityRule.scenario.recreate()

        composeRule.onNodeWithText("NICE64").assertIsDisplayed()
    }

    @Test
    fun pairedDashboardProvidesExplicitRefreshAndPairingManagement() {
        var refreshed = false
        val profile = BridgePairingMetadata(
            "profile", "NICE64 Bridge", "https://192.168.2.81:6470", "v1",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "A".repeat(64), 1,
        )
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Nice64App(
                    state = PairingPresentationState(profiles = listOf(profile), activeProfileId = "profile"),
                    dashboard = BridgeDashboardSnapshot(
                        connection = BridgeConnectionPresentation.ONLINE,
                        endpoint = profile.apiBaseUrl,
                    ),
                    onBeginPairing = {}, onPermission = { _, _ -> }, onQrCode = {},
                    onConfirmPairing = {}, onCancelPairing = {}, onRetryScan = {},
                    onSelect = {}, onRequestUnpair = {}, onCancelUnpair = {},
                    onConfirmUnpair = {}, onRefresh = { refreshed = true },
                )
            }
        }
        composeRule.onNodeWithText("Bridge status").assertIsDisplayed()
        composeRule.onNodeWithText("Refresh").performScrollTo().performClick()
        composeRule.runOnIdle { assert(refreshed) }
        composeRule.onNodeWithText("Pair another Bridge").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("Unpair").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithContentDescription("Selected Bridge: NICE64 Bridge")
            .assertIsSelected()
    }

    @Test
    fun profileActionsPreserveExplicitUserSelectionAndUnpairTargets() {
        val first = BridgePairingMetadata(
            "first", "Living Room", "https://192.168.2.81:6470", "v1",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "A".repeat(64), 1,
        )
        val second = BridgePairingMetadata(
            "second", "Workshop", "https://192.168.2.82:6470", "v1",
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=", "B".repeat(64), 2,
        )
        var selected: String? = null
        var unpaired: String? = null
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Nice64App(
                    state = PairingPresentationState(
                        profiles = listOf(first, second),
                        activeProfileId = "first",
                    ),
                    onBeginPairing = {}, onPermission = { _, _ -> }, onQrCode = {},
                    onConfirmPairing = {}, onCancelPairing = {}, onRetryScan = {},
                    onSelect = { selected = it }, onRequestUnpair = { unpaired = it },
                    onCancelUnpair = {}, onConfirmUnpair = {},
                )
            }
        }

        composeRule.onNodeWithText("Select Bridge").performScrollTo().performClick()
        composeRule.runOnIdle { assertEquals("second", selected) }
        composeRule.onAllNodesWithText("Unpair")[1].performScrollTo().performClick()
        composeRule.runOnIdle { assertEquals("second", unpaired) }
    }

    @Test
    fun accessoriesAndModesOffersOnlyHighLevelSupportedAccessoryChoices() {
        var selectedAccessory: BridgeAccessoryTarget? = null
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Nice64App(
                    state = pairedState(),
                    dashboard = BridgeDashboardSnapshot(
                        connection = BridgeConnectionPresentation.ONLINE,
                        profileId = "profile",
                        state = BridgeSection(value = bridgeState()),
                        operation = BridgeSection(value = idleOperation()),
                    ),
                    onBeginPairing = {}, onPermission = { _, _ -> }, onQrCode = {},
                    onConfirmPairing = {}, onCancelPairing = {}, onRetryScan = {},
                    onSelect = {}, onRequestUnpair = {}, onCancelUnpair = {},
                    onConfirmUnpair = {},
                    mutationActions = BridgeMutationActions(
                        accessory = { selectedAccessory = it },
                    ),
                )
            }
        }
        composeRule.onNodeWithText("Accessories and Modes").assertIsDisplayed()
        composeRule.onNodeWithText("Current: network-pak").assertIsDisplayed()
        composeRule.onAllNodesWithText("Change firmware", ignoreCase = true).assertCountEquals(0)
        composeRule.onAllNodesWithText("Change Network Pak module").assertCountEquals(0)
        composeRule.onAllNodesWithText("Change Network Pak transport").assertCountEquals(0)
        composeRule.onNodeWithText("Select Accessory").performClick()
        composeRule.onNode(hasText("None") and hasClickAction()).assertIsDisplayed()
        composeRule.onNode(hasText("Controller Pak") and hasClickAction()).assertIsDisplayed()
        composeRule.onNode(hasText("Transfer Pak") and hasClickAction()).assertIsDisplayed()
        composeRule.onNode(hasText("Network Pak") and hasClickAction())
            .assertIsDisplayed().assertIsSelected()
        composeRule.onAllNodesWithText("Rumble Pak").assertCountEquals(0)
        composeRule.runOnIdle { assert(selectedAccessory == null) }
        composeRule.onNode(hasText("Transfer Pak") and hasClickAction()).performClick()
        composeRule.runOnIdle { assert(selectedAccessory == BridgeAccessoryTarget.TRANSFER_PAK) }
    }

    @Test
    fun reconcilingStateDisablesConflictingControls() {
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Nice64App(
                    state = pairedState(),
                    dashboard = BridgeDashboardSnapshot(
                        connection = BridgeConnectionPresentation.ONLINE,
                        profileId = "profile",
                        state = BridgeSection(value = bridgeState()),
                        operation = BridgeSection(value = idleOperation()),
                    ),
                    mutation = BridgeMutationPresentation(
                        BridgeMutationPhase.RECONCILING, "profile", 9,
                        BridgeMutationKind.FIRMWARE, "network", "Network",
                    ),
                    onBeginPairing = {}, onPermission = { _, _ -> }, onQrCode = {},
                    onConfirmPairing = {}, onCancelPairing = {}, onRetryScan = {},
                    onSelect = {}, onRequestUnpair = {}, onCancelUnpair = {},
                    onConfirmUnpair = {},
                )
            }
        }
        composeRule.onNodeWithText("Reconciling authoritative Bridge state…").assertIsDisplayed()
        composeRule.onAllNodesWithText("Change firmware", ignoreCase = true).assertCountEquals(0)
        composeRule.onNodeWithText("Select Accessory").assertIsNotEnabled()
    }

    @Test
    fun remoteBridgeAreaRendersAuthoritativeDeviceAndOperationProgress() {
        val running = BridgeOperationDto(
            41, "history-activation", "running", "2026-09-23T00:00:00Z", null,
            "before-commit-only", false, "history", null, null,
            BridgeOperationProgressDto("uploading", 1024, 2048, null, null, "bytes", "2026-09-23T00:00:01Z"),
        )
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Nice64App(
                    state = pairedState(),
                    dashboard = BridgeDashboardSnapshot(
                        connection = BridgeConnectionPresentation.ONLINE,
                        profileId = "profile",
                        state = BridgeSection(value = bridgeState()),
                        operation = BridgeSection(value = running),
                    ),
                    onBeginPairing = {}, onPermission = { _, _ -> }, onQrCode = {},
                    onConfirmPairing = {}, onCancelPairing = {}, onRetryScan = {},
                    onSelect = {}, onRequestUnpair = {}, onCancelUnpair = {}, onConfirmUnpair = {},
                )
            }
        }
        composeRule.onNodeWithText("Remote").assertIsDisplayed()
        composeRule.onNodeWithContentDescription("Healthy status: Bridge online").assertIsDisplayed()
        composeRule.onNodeWithText("Pico connected").assertIsDisplayed()
        composeRule.onNodeWithText("Transport").assertIsDisplayed()
        composeRule.onNodeWithText("wifi-tcp").assertIsDisplayed()
        composeRule.onNodeWithContentDescription("Accessory: network-pak")
            .performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("Operation: history-activation — running").assertIsDisplayed()
        composeRule.onNodeWithText("Progress: uploading 1024/2048 bytes").assertIsDisplayed()
    }

    @Test
    fun remoteSectionsExposeSelectionWithoutInvokingDomainActions() {
        var domainActions = 0
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Nice64App(
                    state = pairedState(),
                    dashboard = BridgeDashboardSnapshot(
                        connection = BridgeConnectionPresentation.ONLINE,
                        profileId = "profile",
                        history = BridgeSection(value = BridgeHistoryDto("v1", 1, emptyList(), emptyList())),
                    ),
                    onBeginPairing = { domainActions++ },
                    onPermission = { _, _ -> domainActions++ },
                    onQrCode = { domainActions++ },
                    onConfirmPairing = { domainActions++ },
                    onCancelPairing = { domainActions++ },
                    onRetryScan = { domainActions++ },
                    onSelect = { domainActions++ },
                    onRequestUnpair = { domainActions++ },
                    onCancelUnpair = { domainActions++ },
                    onConfirmUnpair = { domainActions++ },
                    onRefresh = { domainActions++ },
                )
            }
        }

        composeRule.onNode(hasText("Bridge") and hasClickAction()).assertIsSelected()
        composeRule.onNode(hasText("History") and hasClickAction()).performClick().assertIsSelected()
        composeRule.onNode(hasText("Controllers") and hasClickAction()).performClick().assertIsSelected()
        composeRule.runOnIdle { assertEquals(0, domainActions) }
    }

    @Test
    fun remoteHistoryAreaReusesAuthoritativeHistoryControls() {
        val item = BridgeHistoryItemDto("item-1", "Pokemon Crystal.gbc", "2026-09-23T00:00:00Z", true)
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Nice64App(
                    state = pairedState(),
                    dashboard = BridgeDashboardSnapshot(
                        connection = BridgeConnectionPresentation.ONLINE,
                        profileId = "profile",
                        history = BridgeSection(value = BridgeHistoryDto("v1", 1, emptyList(), listOf(item))),
                    ),
                    onBeginPairing = {}, onPermission = { _, _ -> }, onQrCode = {},
                    onConfirmPairing = {}, onCancelPairing = {}, onRetryScan = {},
                    onSelect = {}, onRequestUnpair = {}, onCancelUnpair = {}, onConfirmUnpair = {},
                )
            }
        }
        composeRule.onNode(hasText("History") and hasClickAction()).performClick()
        composeRule.onNodeWithContentDescription("Activate Transfer Pak Pokemon Crystal.gbc")
            .performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("Refresh").performScrollTo().assertIsDisplayed()
    }

    private fun pairedState(): PairingPresentationState {
        val profile = BridgePairingMetadata(
            "profile", "NICE64 Bridge", "https://192.168.2.81:6470", "v1",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "A".repeat(64), 1,
        )
        return PairingPresentationState(profiles = listOf(profile), activeProfileId = "profile")
    }

    private fun idleOperation() = BridgeOperationDto(
        0, "none", "idle", null, null, "none", false, "none", null, null, null,
    )

    private fun bridgeState(): BridgeStateDto {
        val neutral = BridgeControllerInputDto("n64", 0, 0, 0, false, true)
        return BridgeStateDto(
            "v1", 1, "2026-09-12T00:00:00Z",
            BridgeIdentityDto("22222222-2222-2222-2222-222222222222", "NICE64"),
            BridgeDeviceDto(
                true, "connected", "wifi-tcp", "controller", "controller", "network",
                BridgeTransitionDto("idle", "unknown", null), "network-pak",
                BridgeTransitionDto("idle", "none", null), "tcp", "tcp",
                listOf(
                    BridgeCapabilityDto("firmware-mode-controller", null, null),
                    BridgeCapabilityDto("firmware-mode-network", null, null),
                ),
            ),
            BridgeControllerStateDto(neutral, neutral, false, false),
            BridgeControllerPakDto(false, false, null, 0, "none"),
            BridgeTransferPakDto(false, false, false, null, null, "none", 0, 0, null, "none"),
            BridgePersistenceDto("none", false, false, false, 0, 0, null, null),
            BridgeModuleDto(
                "diagnostics", "Diagnostics",
                listOf(BridgeModuleInfoDto("diagnostics", "Diagnostics")),
                "confirmed", "requested-module-selected",
            ),
            BridgeWifiDto(true, true, true, "connected", "station", "up", "none", "none"),
            idleOperation(),
        )
    }
}
