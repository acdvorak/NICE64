package com.melfegor.nice64

import androidx.activity.compose.setContent
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.hasClickAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onAllNodesWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.api.BridgeControllerInputPresentation
import com.melfegor.nice64.api.MobileInputMode
import java.util.concurrent.atomic.AtomicReference
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class MobileInputModeInstrumentedTest {
    @get:Rule
    @Suppress("DEPRECATION")
    val composeRule = createAndroidComposeRule<MainActivity>()

    @Test fun onScreenModeShowsTouchGameplaySurface() {
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                BridgeTouchController(
                    BridgeControllerInputPresentation(
                        enabled = true,
                        inputMode = MobileInputMode.ON_SCREEN,
                        sourceLabel = "On-Screen",
                    ),
                    BridgeControllerInputActions(),
                )
            }
        }

        composeRule.onNodeWithText("Mobile Input Mode").assertIsDisplayed()
        composeRule.onNode(hasText("On-Screen") and hasClickAction()).assertIsSelected()
        composeRule.onNodeWithContentDescription("Analog stick").assertIsDisplayed()
    }

    @Test fun controllerModeHidesTouchSurfaceAndToggleRequestsOnScreen() {
        val selected = AtomicReference<MobileInputMode>()
        composeRule.activityRule.scenario.onActivity { activity ->
            activity.setContent {
                BridgeTouchController(
                    BridgeControllerInputPresentation(
                        enabled = true,
                        inputMode = MobileInputMode.CONTROLLER,
                        sourceLabel = "Controller: Test controller",
                    ),
                    BridgeControllerInputActions(selectMode = selected::set),
                )
            }
        }

        composeRule.onAllNodesWithContentDescription("Analog stick").assertCountEquals(0)
        composeRule.onNodeWithText("Mobile input").assertIsDisplayed()
        composeRule.onNode(hasText("Controller") and hasClickAction()).assertIsSelected()
        composeRule.onNode(hasText("On-Screen") and hasClickAction()).performClick()
        assertEquals(MobileInputMode.ON_SCREEN, selected.get())
    }
}
