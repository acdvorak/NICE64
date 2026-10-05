package com.melfegor.nice64

import android.view.WindowManager
import androidx.activity.compose.BackHandler
import androidx.activity.compose.LocalActivity
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.ViewModel
import com.melfegor.nice64.api.BridgeControllerInputPresentation
import com.melfegor.nice64.api.MobileInputMode

internal fun controllerBlackoutEligible(
    mobilePrimary: Boolean,
    input: BridgeControllerInputPresentation,
): Boolean = mobilePrimary && input.enabled && input.inputMode == MobileInputMode.CONTROLLER

class ControllerBlackoutPresentationModel : ViewModel() {
    var active by mutableStateOf(false)
        private set

    fun enter(eligible: Boolean): Boolean {
        if (!eligible) return false
        active = true
        return true
    }

    fun exit() {
        active = false
    }

    fun reconcileEligibility(eligible: Boolean) {
        if (!eligible) exit()
    }
}

@Composable
internal fun ControllerBlackoutWindowEffect(active: Boolean) {
    val activity = LocalActivity.current
    if (!active || activity == null) return
    DisposableEffect(activity, active) {
        val window = activity.window
        val insetsController = WindowCompat.getInsetsController(window, window.decorView)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        insetsController.systemBarsBehavior =
            WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        insetsController.hide(WindowInsetsCompat.Type.systemBars())

        onDispose {
            window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            if (!activity.isChangingConfigurations) {
                insetsController.show(WindowInsetsCompat.Type.systemBars())
            }
        }
    }
}

@Composable
internal fun ControllerBlackoutPresentation(onExit: () -> Unit) {
    ControllerBlackoutWindowEffect(active = true)
    BackHandler(onBack = onExit)
    Box(
        Modifier
            .fillMaxSize()
            .background(Color.Black)
            .safeDrawingPadding()
            .testTag("controller-blackout-surface"),
    ) {
        TextButton(
            onClick = onExit,
            modifier = Modifier
                .align(Alignment.BottomEnd)
                .padding(16.dp)
                .semantics { contentDescription = "Restore NICE64 display" },
            colors = ButtonDefaults.textButtonColors(
                contentColor = Color(0xFF666666),
            ),
        ) {
            Text("Restore display")
        }
    }
}
