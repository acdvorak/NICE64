package com.melfegor.nice64

import androidx.activity.compose.setContent
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.width
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.pairing.CameraPermissionState
import com.melfegor.nice64.pairing.PairingConfirmation
import com.melfegor.nice64.pairing.PairingPresentationError
import com.melfegor.nice64.pairing.PairingPresentationState
import com.melfegor.nice64.pairing.PairingScreen
import com.melfegor.nice64.ui.theme.Nice64Theme
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class PairingPresentationUiTest {
    @get:Rule
    @Suppress("DEPRECATION")
    val composeRule = createAndroidComposeRule<MainActivity>()

    @Test
    fun scannerPermissionPresentationUsesExistingActions() {
        var granted = false
        var cancelled = false
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Nice64Theme {
                    ScannerPresentation(
                        permission = CameraPermissionState.DENIED,
                        onGrantPermission = { granted = true },
                        onOpenSettings = {},
                        onCancel = { cancelled = true },
                    )
                }
            }
        }

        composeRule.onNodeWithText("Scan Bridge pairing QR").assertIsDisplayed()
        composeRule.onNodeWithText("Grant camera access").performClick()
        composeRule.onNodeWithText("Cancel").performClick()
        composeRule.runOnIdle { assertTrue(granted && cancelled) }
    }

    @Test
    fun permanentlyDeniedCameraStateUsesExistingSettingsAction() {
        var settings = false
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Nice64Theme {
                    ScannerPresentation(
                        permission = CameraPermissionState.PERMANENTLY_DENIED,
                        onGrantPermission = {},
                        onOpenSettings = { settings = true },
                        onCancel = {},
                    )
                }
            }
        }

        composeRule.onNodeWithText("Camera access disabled").assertIsDisplayed()
        composeRule.onNodeWithText("Open settings").performClick()
        composeRule.runOnIdle { assertTrue(settings) }
    }

    @Test
    fun scannerActionsRemainReachableAtNarrowWidthShortHeightAndLargeFont() {
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                val density = LocalDensity.current
                CompositionLocalProvider(LocalDensity provides Density(density.density, 2f)) {
                    Box(Modifier.width(280.dp).height(300.dp)) {
                        ScannerPresentation(
                            permission = CameraPermissionState.DENIED,
                            onGrantPermission = {},
                            onOpenSettings = {},
                            onCancel = {},
                        )
                    }
                }
            }
        }

        composeRule.onNodeWithText("Grant camera access").performScrollTo().assertIsDisplayed()
        composeRule.onNodeWithText("Cancel").performScrollTo().assertIsDisplayed()
    }

    @Test
    fun confirmationShowsIdentityAndPreservesConfirmCancelActions() {
        var confirmed = false
        var cancelled = false
        showApp(
            state = PairingPresentationState(
                screen = PairingScreen.CONFIRMATION,
                confirmation = PairingConfirmation(
                    endpoint = "https://192.168.2.81:6470",
                    apiVersion = "v1",
                    spkiSummary = "AAAAAAAA…AAAAAAAA",
                    certificateSummary = "BBBBBBBB…BBBBBBBB",
                    identityChangeWarning = true,
                ),
            ),
            onConfirm = { confirmed = true },
            onCancel = { cancelled = true },
        )

        composeRule.onNodeWithText("Pair with NICE64 Bridge?").assertIsDisplayed()
        composeRule.onNodeWithText("Pairing code recognized").assertIsDisplayed()
        composeRule.onNodeWithText("https://192.168.2.81:6470").assertIsDisplayed()
        composeRule.onNodeWithText("Bridge identity changed").assertIsDisplayed()
        composeRule.onNodeWithText("Pair Bridge").performScrollTo().performClick()
        composeRule.onNodeWithText("Cancel").performScrollTo().performClick()
        composeRule.runOnIdle { assertTrue(confirmed && cancelled) }
    }

    @Test
    fun savingAndErrorStatesUseSafeBoundedPresentation() {
        showApp(PairingPresentationState(screen = PairingScreen.SAVING))
        composeRule.onNodeWithText("Saving pairing details securely…").assertIsDisplayed()

        var retried = false
        var cancelled = false
        showApp(
            state = PairingPresentationState(
                screen = PairingScreen.ERROR,
                error = PairingPresentationError.INVALID_QR,
            ),
            onRetry = { retried = true },
            onCancel = { cancelled = true },
        )
        composeRule.onNodeWithText("Pairing failed").assertIsDisplayed()
        composeRule.onNodeWithText(PairingPresentationError.INVALID_QR.safeMessage).assertIsDisplayed()
        composeRule.onNodeWithText("Scan again").performClick()
        composeRule.onNodeWithText("Back to profiles").performClick()
        composeRule.runOnIdle { assertTrue(retried && cancelled) }
    }

    private fun showApp(
        state: PairingPresentationState,
        onConfirm: () -> Unit = {},
        onCancel: () -> Unit = {},
        onRetry: () -> Unit = {},
    ) {
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                Nice64App(
                    state = state,
                    onBeginPairing = {}, onPermission = { _, _ -> }, onQrCode = {},
                    onConfirmPairing = onConfirm, onCancelPairing = onCancel,
                    onRetryScan = onRetry, onSelect = {}, onRequestUnpair = {},
                    onCancelUnpair = {}, onConfirmUnpair = {},
                )
            }
        }
    }
}
