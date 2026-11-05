package com.melfegor.nice64

import androidx.activity.compose.setContent
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.down
import androidx.compose.ui.test.up
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.api.BridgeControllerInputPresentation
import com.melfegor.nice64.api.MobileInputMode
import com.melfegor.nice64.core.model.N64ButtonMask
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class FullScreenControllerInstrumentedTest {
    @get:Rule
    @Suppress("DEPRECATION")
    val composeRule = createAndroidComposeRule<MainActivity>()

    @Test fun embeddedEntryExistsOnlyForEnabledOnScreenAndInvokesPresentationCallback() {
        var entryCount = 0
        var modeCount = 0
        setEmbedded(
            enabled = true,
            mode = MobileInputMode.ON_SCREEN,
            actions = BridgeControllerInputActions(selectMode = { modeCount++ }),
            onEnter = { entryCount++ },
        )

        composeRule.onNodeWithText("Enter full-screen controller").performClick()
        assertEquals(1, entryCount)
        assertEquals(0, modeCount)

        setEmbedded(enabled = false, mode = MobileInputMode.ON_SCREEN)
        composeRule.onAllNodesWithText("Enter full-screen controller").assertCountEquals(0)
        setEmbedded(enabled = true, mode = MobileInputMode.CONTROLLER)
        composeRule.onAllNodesWithText("Enter full-screen controller").assertCountEquals(0)
    }

    @Test fun fullScreenRendersEveryControlAndExitWithoutSubmittingControllerState() {
        var buttonEvents = 0
        var analogEvents = 0
        var exitCount = 0
        setFullScreen(
            actions = BridgeControllerInputActions(
                button = { _, _ -> buttonEvents++ },
                analog = { _, _ -> analogEvents++ },
            ),
            onExit = { exitCount++ },
        )

        listOf("L", "R", "Z", "Start", "A", "B").forEach {
            composeRule.onNodeWithContentDescription("$it Button").assertIsDisplayed()
        }
        listOf("D-Up", "D-Down", "D-Left", "D-Right", "C-Up", "C-Down", "C-Left", "C-Right").forEach {
            composeRule.onNodeWithContentDescription("$it Button").assertIsDisplayed()
        }
        composeRule.onNodeWithContentDescription("Analog stick").assertIsDisplayed()
        assertEquals(0, buttonEvents)
        assertEquals(0, analogEvents)
        composeRule.onNodeWithContentDescription("Exit full-screen controller").performClick()
        assertEquals(1, exitCount)
        assertEquals(0, buttonEvents)
        assertEquals(0, analogEvents)
    }

    @Test fun fullScreenButtonUsesExistingPressReleaseCallbackAndUnavailableStateDisablesIt() {
        val events = mutableListOf<Pair<UInt, Boolean>>()
        setFullScreen(actions = BridgeControllerInputActions(button = { mask, pressed ->
            events += mask to pressed
        }))

        composeRule.onNodeWithContentDescription("A Button").performTouchInput {
            down(center)
            up()
        }
        composeRule.runOnIdle {
            assertEquals(N64ButtonMask.A to true, events.first())
            assertEquals(N64ButtonMask.A to false, events.last())
        }

        setFullScreen(authorityAvailable = false)
        composeRule.onNodeWithText("On-Screen controls unavailable").assertIsDisplayed()
        composeRule.onNodeWithContentDescription("A Button").assertIsNotEnabled()
    }

    private fun setEmbedded(
        enabled: Boolean,
        mode: MobileInputMode,
        actions: BridgeControllerInputActions = BridgeControllerInputActions(),
        onEnter: () -> Unit = {},
    ) {
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                BridgeTouchController(
                    BridgeControllerInputPresentation(enabled, mode, mode.name),
                    actions,
                    onEnter,
                )
            }
        }
    }

    private fun setFullScreen(
        authorityAvailable: Boolean = true,
        actions: BridgeControllerInputActions = BridgeControllerInputActions(),
        onExit: () -> Unit = {},
    ) {
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                FullScreenTouchController(
                    BridgeControllerInputPresentation(
                        enabled = true,
                        inputMode = MobileInputMode.ON_SCREEN,
                        sourceLabel = "On-Screen",
                    ),
                    authorityAvailable,
                    actions,
                    onExit,
                )
            }
        }
    }
}
