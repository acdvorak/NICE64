package com.melfegor.nice64

import android.view.WindowManager
import androidx.activity.compose.setContent
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.api.BridgeControllerInputPresentation
import com.melfegor.nice64.api.MobileInputMode
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ControllerBlackoutInstrumentedTest {
    @get:Rule
    @Suppress("DEPRECATION")
    val composeRule = createAndroidComposeRule<MainActivity>()

    @Test fun entryIsVisibleOnlyForEnabledControllerAndOnlyInvokesPresentationCallback() {
        var entries = 0
        var controllerActions = 0
        showEntry(true, MobileInputMode.CONTROLLER, { entries++ }) {
            controllerActions++
        }
        composeRule.onNodeWithText("Controller Blackout Mode").performClick()
        assertEquals(1, entries)
        assertEquals(0, controllerActions)

        showEntry(false, MobileInputMode.CONTROLLER)
        composeRule.onAllNodesWithText("Controller Blackout Mode").assertCountEquals(0)
        showEntry(true, MobileInputMode.ON_SCREEN)
        composeRule.onAllNodesWithText("Controller Blackout Mode").assertCountEquals(0)
    }

    @Test fun blackSurfaceAndRestoreActionRenderAndExit() {
        var exits = 0
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent { ControllerBlackoutPresentation { exits++ } }
        }
        composeRule.onNodeWithTag("controller-blackout-surface").assertIsDisplayed()
        composeRule.onNodeWithContentDescription("Restore NICE64 display").assertIsDisplayed()
            .performClick()
        assertEquals(1, exits)
    }

    @Test fun AndroidBackExitsBlackout() {
        var exits = 0
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent { ControllerBlackoutPresentation { exits++ } }
        }
        composeRule.waitForIdle()
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.onBackPressedDispatcher.onBackPressed()
        }
        composeRule.runOnIdle { assertEquals(1, exits) }
    }

    @Test fun keepScreenOnFlagIsAppliedAndClearedWithPresentation() {
        var active by mutableStateOf(true)
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                if (active) ControllerBlackoutPresentation {}
            }
        }
        composeRule.waitForIdle()
        composeRule.activityRule.scenario.onActivity { activity ->
            assertTrue(activity.window.hasKeepScreenOnFlag())
        }
        composeRule.runOnUiThread { active = false }
        composeRule.waitForIdle()
        composeRule.activityRule.scenario.onActivity { activity ->
            assertFalse(activity.window.hasKeepScreenOnFlag())
        }
    }

    private fun showEntry(
        enabled: Boolean,
        mode: MobileInputMode,
        onEnter: () -> Unit = {},
        onControllerAction: () -> Unit = {},
    ) {
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                BridgeTouchController(
                    input = BridgeControllerInputPresentation(enabled, mode, mode.name),
                    actions = BridgeControllerInputActions(
                        button = { _, _ -> onControllerAction() },
                        analog = { _, _ -> onControllerAction() },
                        selectMode = { onControllerAction() },
                    ),
                    onEnterBlackout = onEnter,
                )
            }
        }
    }
}

private fun android.view.Window.hasKeepScreenOnFlag(): Boolean =
    attributes.flags and WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON != 0
