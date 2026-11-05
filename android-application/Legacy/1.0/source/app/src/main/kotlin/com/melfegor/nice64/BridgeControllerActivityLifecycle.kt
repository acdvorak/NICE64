package com.melfegor.nice64

/**
 * Keeps configuration recreation separate from genuine backgrounding. The Activity owns only the
 * classification; the retained ViewModel remains the one controller-runtime owner.
 */
internal fun handleControllerActivityStop(
    isChangingConfigurations: Boolean,
    clearTransientTouch: () -> Unit,
    backgroundControllerRuntime: () -> Unit,
) {
    if (isChangingConfigurations) clearTransientTouch() else backgroundControllerRuntime()
}
