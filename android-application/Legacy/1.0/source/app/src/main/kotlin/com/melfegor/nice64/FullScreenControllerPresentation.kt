package com.melfegor.nice64

import android.content.pm.ActivityInfo
import androidx.activity.compose.LocalActivity
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.ViewModel

class FullScreenControllerPresentationModel : ViewModel() {
    var active by mutableStateOf(false)
        private set

    var previousOrientation: Int = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        private set

    fun enter(currentOrientation: Int) {
        if (active) return
        previousOrientation = currentOrientation
        active = true
    }

    fun exit() {
        active = false
    }
}

@Composable
internal fun FullScreenControllerWindowEffect(
    active: Boolean,
    previousOrientation: Int,
) {
    val activity = LocalActivity.current
    if (!active || activity == null) return
    DisposableEffect(activity, active, previousOrientation) {
        val window = activity.window
        val insetsController = WindowCompat.getInsetsController(window, window.decorView)
        activity.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        insetsController.systemBarsBehavior =
            WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        insetsController.hide(WindowInsetsCompat.Type.systemBars())

        onDispose {
            if (!activity.isChangingConfigurations) {
                insetsController.show(WindowInsetsCompat.Type.systemBars())
                activity.requestedOrientation = previousOrientation
            }
        }
    }
}
