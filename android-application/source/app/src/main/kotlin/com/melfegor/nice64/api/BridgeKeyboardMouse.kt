package com.melfegor.nice64.api

import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import com.melfegor.nice64.tls.BridgePinnedClientResult
import com.melfegor.nice64.tls.BridgePinnedHttpsClientFactory
import java.io.IOException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.HttpUrl
import okhttp3.Request
import okhttp3.Response
import okhttp3.HttpUrl.Companion.toHttpUrl
import kotlin.math.roundToInt

/** The existing Bridge logical-key namespace. This is not the RandNET matrix. */
enum class Nice64LogicalKey(val id: Int, val label: String) {
    ESCAPE(1, "Esc"), F1(2, "F1"), F2(3, "F2"), F3(4, "F3"), F4(5, "F4"),
    F5(6, "F5"), F6(7, "F6"), F7(8, "F7"), F8(9, "F8"), F9(10, "F9"),
    F10(11, "F10"), F11(12, "F11"), F12(13, "F12"), NUM_LOCK(14, "Num"),
    SCROLL_LOCK(15, "Scroll"), CAPS_LOCK(16, "Caps"), GRAVE(17, "`"),
    D1(18, "1"), D2(19, "2"), D3(20, "3"), D4(21, "4"), D5(22, "5"),
    D6(23, "6"), D7(24, "7"), D8(25, "8"), D9(26, "9"), D0(27, "0"),
    MINUS(28, "-"), EQUALS(29, "="), BACKSPACE(30, "Back"), TAB(31, "Tab"),
    ENTER(32, "Enter"), SPACE(33, "Space"), Q(34, "Q"), W(35, "W"), E(36, "E"),
    R(37, "R"), T(38, "T"), Y(39, "Y"), U(40, "U"), I(41, "I"), O(42, "O"),
    P(43, "P"), A(44, "A"), S(45, "S"), D(46, "D"), F(47, "F"), G(48, "G"),
    H(49, "H"), J(50, "J"), K(51, "K"), L(52, "L"), Z(53, "Z"), X(54, "X"),
    C(55, "C"), V(56, "V"), B(57, "B"), N(58, "N"), M(59, "M"),
    APOSTROPHE(60, "'"), LEFT_BRACKET(61, "["), RIGHT_BRACKET(62, "]"),
    SEMICOLON(63, ";"), COMMA(64, ","), PERIOD(65, "."), SLASH(66, "/"),
    KEYPAD_MINUS(67, "KP-"), KEYPAD_PLUS(68, "KP+"), KEYPAD_MULTIPLY(69, "KP*"),
    LEFT_SHIFT(70, "L Shift"), RIGHT_SHIFT(71, "R Shift"),
    LEFT_CONTROL(72, "L Ctrl"), RIGHT_CONTROL(73, "R Ctrl"),
    LEFT_ALT(74, "L Alt"), RIGHT_ALT(75, "R Alt"), OPTION(76, "Option"),
    PRINT_SCREEN(77, "Print"), PAGE_UP(78, "PgUp"), UP(79, "Up"), LEFT(80, "Left"),
    DOWN(81, "Down"), RIGHT(82, "Right"), END(83, "End"), HOME(84, "Home"),
}

object AndroidNice64KeyMap {
    private val letters = mapOf(
        KeyEvent.KEYCODE_A to Nice64LogicalKey.A, KeyEvent.KEYCODE_B to Nice64LogicalKey.B,
        KeyEvent.KEYCODE_C to Nice64LogicalKey.C, KeyEvent.KEYCODE_D to Nice64LogicalKey.D,
        KeyEvent.KEYCODE_E to Nice64LogicalKey.E, KeyEvent.KEYCODE_F to Nice64LogicalKey.F,
        KeyEvent.KEYCODE_G to Nice64LogicalKey.G, KeyEvent.KEYCODE_H to Nice64LogicalKey.H,
        KeyEvent.KEYCODE_I to Nice64LogicalKey.I, KeyEvent.KEYCODE_J to Nice64LogicalKey.J,
        KeyEvent.KEYCODE_K to Nice64LogicalKey.K, KeyEvent.KEYCODE_L to Nice64LogicalKey.L,
        KeyEvent.KEYCODE_M to Nice64LogicalKey.M, KeyEvent.KEYCODE_N to Nice64LogicalKey.N,
        KeyEvent.KEYCODE_O to Nice64LogicalKey.O, KeyEvent.KEYCODE_P to Nice64LogicalKey.P,
        KeyEvent.KEYCODE_Q to Nice64LogicalKey.Q, KeyEvent.KEYCODE_R to Nice64LogicalKey.R,
        KeyEvent.KEYCODE_S to Nice64LogicalKey.S, KeyEvent.KEYCODE_T to Nice64LogicalKey.T,
        KeyEvent.KEYCODE_U to Nice64LogicalKey.U, KeyEvent.KEYCODE_V to Nice64LogicalKey.V,
        KeyEvent.KEYCODE_W to Nice64LogicalKey.W, KeyEvent.KEYCODE_X to Nice64LogicalKey.X,
        KeyEvent.KEYCODE_Y to Nice64LogicalKey.Y, KeyEvent.KEYCODE_Z to Nice64LogicalKey.Z,
    )
    private val digits = listOf(
        Nice64LogicalKey.D0, Nice64LogicalKey.D1, Nice64LogicalKey.D2, Nice64LogicalKey.D3,
        Nice64LogicalKey.D4, Nice64LogicalKey.D5, Nice64LogicalKey.D6, Nice64LogicalKey.D7,
        Nice64LogicalKey.D8, Nice64LogicalKey.D9,
    )

    fun translate(keyCode: Int): Nice64LogicalKey? = letters[keyCode] ?: when (keyCode) {
        in KeyEvent.KEYCODE_0..KeyEvent.KEYCODE_9 -> digits[keyCode - KeyEvent.KEYCODE_0]
        KeyEvent.KEYCODE_ESCAPE -> Nice64LogicalKey.ESCAPE
        in KeyEvent.KEYCODE_F1..KeyEvent.KEYCODE_F12 -> Nice64LogicalKey.entries[keyCode - KeyEvent.KEYCODE_F1 + 1]
        KeyEvent.KEYCODE_NUM_LOCK -> Nice64LogicalKey.NUM_LOCK
        KeyEvent.KEYCODE_SCROLL_LOCK -> Nice64LogicalKey.SCROLL_LOCK
        KeyEvent.KEYCODE_CAPS_LOCK -> Nice64LogicalKey.CAPS_LOCK
        KeyEvent.KEYCODE_GRAVE -> Nice64LogicalKey.GRAVE
        KeyEvent.KEYCODE_MINUS -> Nice64LogicalKey.MINUS
        KeyEvent.KEYCODE_EQUALS -> Nice64LogicalKey.EQUALS
        KeyEvent.KEYCODE_DEL -> Nice64LogicalKey.BACKSPACE
        KeyEvent.KEYCODE_TAB -> Nice64LogicalKey.TAB
        KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> Nice64LogicalKey.ENTER
        KeyEvent.KEYCODE_SPACE -> Nice64LogicalKey.SPACE
        KeyEvent.KEYCODE_APOSTROPHE -> Nice64LogicalKey.APOSTROPHE
        KeyEvent.KEYCODE_LEFT_BRACKET -> Nice64LogicalKey.LEFT_BRACKET
        KeyEvent.KEYCODE_RIGHT_BRACKET -> Nice64LogicalKey.RIGHT_BRACKET
        KeyEvent.KEYCODE_SEMICOLON -> Nice64LogicalKey.SEMICOLON
        KeyEvent.KEYCODE_COMMA -> Nice64LogicalKey.COMMA
        KeyEvent.KEYCODE_PERIOD -> Nice64LogicalKey.PERIOD
        KeyEvent.KEYCODE_SLASH -> Nice64LogicalKey.SLASH
        KeyEvent.KEYCODE_NUMPAD_SUBTRACT -> Nice64LogicalKey.KEYPAD_MINUS
        KeyEvent.KEYCODE_NUMPAD_ADD -> Nice64LogicalKey.KEYPAD_PLUS
        KeyEvent.KEYCODE_NUMPAD_MULTIPLY -> Nice64LogicalKey.KEYPAD_MULTIPLY
        KeyEvent.KEYCODE_SHIFT_LEFT -> Nice64LogicalKey.LEFT_SHIFT
        KeyEvent.KEYCODE_SHIFT_RIGHT -> Nice64LogicalKey.RIGHT_SHIFT
        KeyEvent.KEYCODE_CTRL_LEFT -> Nice64LogicalKey.LEFT_CONTROL
        KeyEvent.KEYCODE_CTRL_RIGHT -> Nice64LogicalKey.RIGHT_CONTROL
        KeyEvent.KEYCODE_ALT_LEFT -> Nice64LogicalKey.LEFT_ALT
        KeyEvent.KEYCODE_ALT_RIGHT -> Nice64LogicalKey.RIGHT_ALT
        KeyEvent.KEYCODE_META_LEFT, KeyEvent.KEYCODE_META_RIGHT -> Nice64LogicalKey.OPTION
        KeyEvent.KEYCODE_SYSRQ -> Nice64LogicalKey.PRINT_SCREEN
        KeyEvent.KEYCODE_PAGE_UP -> Nice64LogicalKey.PAGE_UP
        KeyEvent.KEYCODE_DPAD_UP -> Nice64LogicalKey.UP
        KeyEvent.KEYCODE_DPAD_LEFT -> Nice64LogicalKey.LEFT
        KeyEvent.KEYCODE_DPAD_DOWN -> Nice64LogicalKey.DOWN
        KeyEvent.KEYCODE_DPAD_RIGHT -> Nice64LogicalKey.RIGHT
        KeyEvent.KEYCODE_MOVE_END -> Nice64LogicalKey.END
        KeyEvent.KEYCODE_MOVE_HOME -> Nice64LogicalKey.HOME
        else -> null
    }
}

enum class BridgeKeyboardMouseMode { NONE, KEYBOARD, MOUSE }
enum class AndroidInputSourceKind { ON_SCREEN, ANY, SPECIFIC }

data class AndroidInputSource(
    val kind: AndroidInputSourceKind,
    val descriptor: String? = null,
    val displayName: String,
)

data class AndroidKeyboardMouseDevice(
    val descriptor: String,
    val displayName: String,
    val keyboard: Boolean,
    val pointer: Boolean,
)

data class BridgeKeyboardLeds(
    val raw: Int = 0,
    val power: Boolean = false,
    val capsLock: Boolean = false,
    val numLock: Boolean = false,
)

data class BridgeKeyboardMousePresentation(
    val verifiedMode: BridgeKeyboardMouseMode = BridgeKeyboardMouseMode.NONE,
    val keyboardSource: AndroidInputSource = AndroidInputSource(
        AndroidInputSourceKind.ON_SCREEN, displayName = "On-Screen Keyboard",
    ),
    val pointerSource: AndroidInputSource = AndroidInputSource(
        AndroidInputSourceKind.ON_SCREEN, displayName = "On-Screen Trackpad",
    ),
    val devices: List<AndroidKeyboardMouseDevice> = emptyList(),
    val heldKeys: Set<Nice64LogicalKey> = emptySet(),
    val leftPressed: Boolean = false,
    val rightPressed: Boolean = false,
    val pointerCaptured: Boolean = false,
    val invertX: Boolean = false,
    val invertY: Boolean = false,
    val leds: BridgeKeyboardLeds = BridgeKeyboardLeds(),
    val sending: Boolean = false,
    val failure: String? = null,
)

@Serializable private data class LedsResponse(val raw: Int, val power: Boolean, val capsLock: Boolean, val numLock: Boolean)

internal interface BridgeKeyboardMouseGateway {
    suspend fun directEndpoint(sessionId: String): BridgeControllerEndpointDto?
    suspend fun leds(): BridgeKeyboardLeds?
}

internal class PinnedBridgeKeyboardMouseGateway(
    private val profiles: ActiveBridgeProfileSource,
) : BridgeKeyboardMouseGateway {
    override suspend fun directEndpoint(sessionId: String): BridgeControllerEndpointDto? =
        execute { base, execute ->
            val response = execute(Request.Builder().url(base.newBuilder().addPathSegments(
                "api/v1/sessions/$sessionId/mobile-input/endpoint",
            ).build()).get().build())
            if (response.statusCode !in 200..299) null
            else JSON.decodeFromString<BridgeControllerEndpointDto>(response.body)
        }

    override suspend fun leds(): BridgeKeyboardLeds? = execute { base, execute ->
        val response = execute(Request.Builder().url(base.newBuilder().addPathSegments(
            "api/v1/keyboard-mouse/keyboard-leds",
        ).build()).get().build())
        if (response.statusCode !in 200..299) null else JSON.decodeFromString<LedsResponse>(response.body)
            .let { BridgeKeyboardLeds(it.raw, it.power, it.capsLock, it.numLock) }
    }

    private suspend fun <T> execute(block: suspend (HttpUrl, suspend (Request) -> BridgeHttpPayload) -> T): T? {
        val active = profiles.load() as? ActiveBridgeProfileResult.Success ?: return null
        val pinned = BridgePinnedHttpsClientFactory.create(active.profile)
            as? BridgePinnedClientResult.Success ?: return null
        return try {
            block(pinned.value.apiBaseUrl.toHttpUrl()) { request ->
                withContext(Dispatchers.IO) {
                    pinned.value.httpClient.newCall(request).execute().use { response: Response ->
                        BridgeHttpPayload(response.code, response.body.string())
                    }
                }
            }
        } catch (_: IOException) { null } catch (_: Exception) { null }
    }

    companion object {
        private val JSON = Json { ignoreUnknownKeys = false; encodeDefaults = true }
    }
}

internal class BridgeKeyboardMouseCoordinator(
    private val gateway: BridgeKeyboardMouseGateway,
    private val scope: CoroutineScope,
    private val directState: BridgeKeyboardMouseDirectStateHolder =
        BridgeKeyboardMouseDirectStateHolder(),
    private val direct: BridgeKeyboardMouseDirectDataPlane? = null,
    private val preferencesStore: MouseInputPreferencesStore? = null,
) {
    private val lock = Any()
    private val mutable = MutableStateFlow(BridgeKeyboardMousePresentation())
    val state: StateFlow<BridgeKeyboardMousePresentation> = mutable.asStateFlow()
    private var profileId: String? = null
    private var sessionId: String? = null
    private var authorityGeneration = 0L
    private var foreground = true
    private var startInFlight = false
    private var pointerPreferences = MouseInputPreferences()
    private var preferencesRevision = 0L

    init {
        direct?.observeNetworkChanges(::networkChanged)
        preferencesStore?.let { store ->
            scope.launch {
                val loaded = store.load()
                synchronized(lock) {
                    if (preferencesRevision == 0L) {
                        pointerPreferences = loaded
                        mutable.value = mutable.value.copy(
                            invertX = loaded.invertX,
                            invertY = loaded.invertY,
                        )
                    }
                }
            }
        }
    }

    fun updateBridgeState(
        state: BridgeStateDto?,
        profileId: String? = null,
        sessionId: String? = null,
        allowStoppedReplacement: Boolean = false,
    ) {
        this.profileId = profileId
        this.sessionId = sessionId
        val verified = when {
            state?.device?.connected != true -> BridgeKeyboardMouseMode.NONE
            state.device.deviceRole != "standalone" || state.device.deviceService != "keyboard-mouse" -> BridgeKeyboardMouseMode.NONE
            state.device.firmwareTransition.phase != "completed" || state.device.firmwareTransition.failure != null -> BridgeKeyboardMouseMode.NONE
            state.device.firmwarePersonality == "keyboard" && state.device.firmwareTransition.target == "keyboard" -> BridgeKeyboardMouseMode.KEYBOARD
            state.device.firmwarePersonality == "mouse" && state.device.firmwareTransition.target == "mouse" -> BridgeKeyboardMouseMode.MOUSE
            else -> BridgeKeyboardMouseMode.NONE
        }
        setVerifiedMode(verified)
        if (verified != BridgeKeyboardMouseMode.NONE && foreground &&
            (allowStoppedReplacement || !mutable.value.sending)) startDirectIfEligible(verified)
    }

    internal fun setVerifiedMode(mode: BridgeKeyboardMouseMode) {
        val old = mutable.value.verifiedMode
        if (old != mode) {
            authorityGeneration++
            releaseAll(sendNeutral = old != BridgeKeyboardMouseMode.NONE)
            direct?.requestImmediateStop()
            scope.launch { direct?.stop(false, ::authorityStillUsable) }
            directState.clear()
            mutable.value = mutable.value.copy(verifiedMode = mode, failure = null)
            if (direct == null && mode != BridgeKeyboardMouseMode.NONE) {
                directState.beginGeneration(mode)
                mutable.value = mutable.value.copy(sending = true)
            }
            if (mode == BridgeKeyboardMouseMode.KEYBOARD) refreshLeds()
        }
    }

    fun updateDevices(devices: List<AndroidKeyboardMouseDevice>) {
        val previous = mutable.value
        val keyboardGone = previous.keyboardSource.kind == AndroidInputSourceKind.SPECIFIC &&
            devices.none { it.keyboard && it.descriptor == previous.keyboardSource.descriptor }
        val pointerGone = previous.pointerSource.kind == AndroidInputSourceKind.SPECIFIC &&
            devices.none { it.pointer && it.descriptor == previous.pointerSource.descriptor }
        mutable.value = previous.copy(devices = devices)
        if (keyboardGone || pointerGone) releaseAll(sendNeutral = true)
    }

    fun selectKeyboardSource(source: AndroidInputSource) {
        releaseAll(sendNeutral = true)
        mutable.value = mutable.value.copy(keyboardSource = source)
    }

    fun selectPointerSource(source: AndroidInputSource) {
        releaseAll(sendNeutral = true)
        mutable.value = mutable.value.copy(pointerSource = source)
    }

    fun pointerCaptureChanged(captured: Boolean) {
        if (!captured && mutable.value.pointerCaptured) releaseAll(sendNeutral = true)
        mutable.value = mutable.value.copy(pointerCaptured = captured)
    }

    fun physicalKey(event: KeyEvent): Boolean {
        val key = AndroidNice64KeyMap.translate(event.keyCode) ?: return false
        val current = mutable.value
        if (current.verifiedMode != BridgeKeyboardMouseMode.KEYBOARD || !current.sending ||
            event.device?.keyboardType != InputDevice.KEYBOARD_TYPE_ALPHABETIC ||
            !sourceMatches(current.keyboardSource, event.device?.descriptor, physical = true)) return false
        if (event.action !in setOf(KeyEvent.ACTION_DOWN, KeyEvent.ACTION_UP)) return false
        setKey(key, event.action == KeyEvent.ACTION_DOWN)
        return true
    }

    fun onScreenKey(key: Nice64LogicalKey, down: Boolean) {
        val current = mutable.value
        if (current.verifiedMode != BridgeKeyboardMouseMode.KEYBOARD || !current.sending ||
            current.keyboardSource.kind != AndroidInputSourceKind.ON_SCREEN) return
        setKey(key, down)
    }

    fun physicalPointer(event: MotionEvent): Boolean {
        val current = mutable.value
        if (current.verifiedMode != BridgeKeyboardMouseMode.MOUSE || !current.sending ||
            !sourceMatches(current.pointerSource, event.device?.descriptor, physical = true) ||
            event.source and InputDevice.SOURCE_MOUSE != InputDevice.SOURCE_MOUSE) return false
        val native = AndroidNice64PointerTransform.physical(
            event.getAxisValue(MotionEvent.AXIS_RELATIVE_X).roundToInt(),
            event.getAxisValue(MotionEvent.AXIS_RELATIVE_Y).roundToInt(),
            pointerPreferences,
        )
        val left = event.buttonState and MotionEvent.BUTTON_PRIMARY != 0
        val right = event.buttonState and MotionEvent.BUTTON_SECONDARY != 0
        if (native.dx == 0 && native.dy == 0 &&
            left == current.leftPressed && right == current.rightPressed) return false
        addPointer(native.dx, native.dy, left, right)
        return true
    }

    fun onScreenPointer(dx: Int, dy: Int) {
        val current = mutable.value
        if (current.verifiedMode == BridgeKeyboardMouseMode.MOUSE && current.sending &&
            current.pointerSource.kind == AndroidInputSourceKind.ON_SCREEN) {
            val native = AndroidNice64PointerTransform.onScreenTrackpad(
                dx, dy, pointerPreferences,
            )
            addPointer(native.dx, native.dy, current.leftPressed, current.rightPressed)
        }
    }

    fun setPointerInvertX(enabled: Boolean) = updatePointerPreferences(
        pointerPreferences.copy(invertX = enabled),
    )

    fun setPointerInvertY(enabled: Boolean) = updatePointerPreferences(
        pointerPreferences.copy(invertY = enabled),
    )

    fun onScreenButton(left: Boolean? = null, right: Boolean? = null) {
        val current = mutable.value
        if (current.verifiedMode == BridgeKeyboardMouseMode.MOUSE && current.sending &&
            current.pointerSource.kind == AndroidInputSourceKind.ON_SCREEN) {
            addPointer(0, 0, left ?: current.leftPressed, right ?: current.rightPressed)
        }
    }

    fun releaseAll(sendNeutral: Boolean) {
        synchronized(lock) {
            mutable.value = mutable.value.copy(
                heldKeys = emptySet(), leftPressed = false, rightPressed = false,
            )
            directState.neutralize()
        }
        // The periodic complete-state stream publishes this neutral state. A clean
        // sender stop additionally emits the established three-packet neutral tail.
    }

    fun foregroundLost() {
        val retiringGeneration = authorityGeneration
        foreground = false
        authorityGeneration++
        releaseAll(sendNeutral = true)
        val wasActive = mutable.value.sending
        mutable.value = mutable.value.copy(sending = false)
        scope.launch {
            direct?.stop(wasActive) { it.generation == retiringGeneration }
        }
    }

    fun foregroundReturned() { foreground = true }

    fun modeTransitionStarting() {
        val retiringGeneration = authorityGeneration
        authorityGeneration++
        releaseAll(sendNeutral = true)
        val wasActive = mutable.value.sending
        mutable.value = mutable.value.copy(sending = false)
        scope.launch {
            direct?.stop(wasActive) { it.generation == retiringGeneration }
        }
    }

    fun refreshLeds() {
        if (mutable.value.verifiedMode != BridgeKeyboardMouseMode.KEYBOARD) return
        scope.launch {
            gateway.leds()?.let { mutable.value = mutable.value.copy(leds = it) }
        }
    }

    private fun setKey(key: Nice64LogicalKey, down: Boolean) {
        synchronized(lock) {
            val keys = mutable.value.heldKeys.toMutableSet()
            val changed = if (down) keys.add(key) else keys.remove(key)
            if (!changed) return
            mutable.value = mutable.value.copy(heldKeys = keys, failure = null)
            directState.keyboard(keys)
        }
    }

    private fun addPointer(dx: Int, dy: Int, left: Boolean, right: Boolean) {
        synchronized(lock) {
            mutable.value = mutable.value.copy(
                leftPressed = left, rightPressed = right, failure = null,
            )
            directState.pointer(left, right, dx, dy)
        }
    }

    private fun updatePointerPreferences(updated: MouseInputPreferences) {
        synchronized(lock) {
            if (pointerPreferences == updated) return
            pointerPreferences = updated
            preferencesRevision++
            mutable.value = mutable.value.copy(
                invertX = updated.invertX,
                invertY = updated.invertY,
            )
        }
        preferencesStore?.let { store -> scope.launch { store.save(updated) } }
    }

    private fun startDirectIfEligible(mode: BridgeKeyboardMouseMode) {
        val profile = profileId ?: return
        val session = sessionId ?: return
        if (direct == null || startInFlight || !foreground) return
        val authority = BridgeKeyboardMouseSenderAuthority(
            profile, session, mode, ++authorityGeneration,
        )
        startInFlight = true
        scope.launch {
            try {
                val endpoint = gateway.directEndpoint(session)
                val started = endpoint != null && authorityStillUsable(authority) &&
                    direct.start(authority, endpoint, ::authorityStillUsable)
                if (authorityStillUsable(authority)) {
                    mutable.value = mutable.value.copy(
                        sending = started,
                        failure = if (started) null else
                            "Direct mobile input is unavailable; input remains neutral.",
                    )
                }
            } finally {
                startInFlight = false
            }
        }
    }

    private fun authorityStillUsable(expected: BridgeKeyboardMouseSenderAuthority): Boolean =
        foreground && expected.generation == authorityGeneration &&
            expected.profileId == profileId && expected.sessionId == sessionId &&
            expected.mode == mutable.value.verifiedMode

    private fun networkChanged() {
        val transport = direct ?: return
        if (transport.currentNetworkStillMatches()) return
        authorityGeneration++
        releaseAll(sendNeutral = false)
        mutable.value = mutable.value.copy(
            sending = false,
            failure = "The active Wi-Fi network changed; direct input stopped.",
        )
        transport.requestImmediateStop()
        scope.launch { transport.stop(false, ::authorityStillUsable) }
    }

    private fun sourceMatches(source: AndroidInputSource, descriptor: String?, physical: Boolean): Boolean =
        physical && when (source.kind) {
            AndroidInputSourceKind.ON_SCREEN -> false
            AndroidInputSourceKind.ANY -> true
            AndroidInputSourceKind.SPECIFIC -> descriptor != null && descriptor == source.descriptor
        }


    fun close() {
        foregroundLost()
        direct?.close()
    }
}

internal data class Nice64PointerDelta(val dx: Int, val dy: Int)

/** Android mouse and Compose trackpad deltas are screen-relative: +Y is down. */
internal object AndroidNice64PointerTransform {
    fun physical(dx: Int, dy: Int, preferences: MouseInputPreferences) =
        fromDownPositive(dx, dy, preferences)

    fun onScreenTrackpad(dx: Int, dy: Int, preferences: MouseInputPreferences) =
        fromDownPositive(dx, dy, preferences)

    private fun fromDownPositive(
        dx: Int,
        dy: Int,
        preferences: MouseInputPreferences,
    ): Nice64PointerDelta {
        val normalizedX = dx
        val normalizedY = -dy
        return Nice64PointerDelta(
            if (preferences.invertX) -normalizedX else normalizedX,
            if (preferences.invertY) -normalizedY else normalizedY,
        )
    }
}

fun enumerateAndroidKeyboardMouseDevices(): List<AndroidKeyboardMouseDevice> =
    InputDevice.getDeviceIds().map(InputDevice::getDevice).filterNotNull().mapNotNull { device ->
        val keyboard = device.keyboardType == InputDevice.KEYBOARD_TYPE_ALPHABETIC
        val pointer = device.sources and InputDevice.SOURCE_MOUSE == InputDevice.SOURCE_MOUSE
        if (!keyboard && !pointer) null else AndroidKeyboardMouseDevice(
            descriptor = device.descriptor,
            displayName = device.name.ifBlank { "Android input device ${device.id}" },
            keyboard = keyboard,
            pointer = pointer,
        )
    }.distinctBy { it.descriptor }.sortedBy { it.displayName.lowercase() }
