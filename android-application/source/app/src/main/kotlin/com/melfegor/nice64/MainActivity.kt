package com.melfegor.nice64

import android.Manifest
import android.annotation.SuppressLint
import android.content.Intent
import android.content.pm.PackageManager
import android.hardware.input.InputManager
import android.os.Bundle
import android.provider.Settings
import android.view.KeyEvent
import android.view.MotionEvent
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.LocalActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.core.net.toUri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.viewmodel.compose.viewModel
import com.melfegor.nice64.core.model.BridgePairingMetadata
import com.melfegor.nice64.api.BridgeDashboardRepository
import com.melfegor.nice64.api.BridgeDashboardSnapshot
import com.melfegor.nice64.api.BridgeKeyboardMousePresentation
import com.melfegor.nice64.api.AndroidMouseInputPreferencesStore
import com.melfegor.nice64.api.BridgeMutationPresentation
import com.melfegor.nice64.api.BridgeSemanticMutationCoordinator
import com.melfegor.nice64.api.BridgeHistoryActivationCoordinator
import com.melfegor.nice64.api.BridgeHistoryActivationPresentation
import com.melfegor.nice64.api.BridgeControllerPrimaryCoordinator
import com.melfegor.nice64.api.BridgeControllerPrimaryPresentation
import com.melfegor.nice64.api.AndroidBridgeControllerNetworkEnvironment
import com.melfegor.nice64.api.BridgeControllerDataPlanePresentation
import com.melfegor.nice64.api.BridgeLatestStateSenderCoordinator
import com.melfegor.nice64.api.BridgeKeyboardMouseDirectStateHolder
import com.melfegor.nice64.api.AndroidBridgeKeyboardMouseDirectDataPlane
import com.melfegor.nice64.api.BridgeLatestControllerStateHolder
import com.melfegor.nice64.api.BridgeControllerHumanInputCoordinator
import com.melfegor.nice64.api.BridgeControllerInputPresentation
import com.melfegor.nice64.api.BridgeControllerDiagnosticsPresentation
import com.melfegor.nice64.api.AndroidInputDispatchBoundary
import com.melfegor.nice64.api.ControllerInputTraceCoordinator
import com.melfegor.nice64.api.ControllerInputTracePresentation
import com.melfegor.nice64.api.ControllerProfilePresentationCoordinator
import com.melfegor.nice64.api.ControllerProfilePresentationState
import com.melfegor.nice64.api.ControllerMappingEditorCoordinator
import com.melfegor.nice64.api.MappingEditorState
import com.melfegor.nice64.api.ControllerProfileRepositoryFactoryV1
import com.melfegor.nice64.api.PersistedControllerProfileUiRepository
import com.melfegor.nice64.api.LiveControllerProfileCache
import com.melfegor.nice64.api.StoredActiveBridgeProfileSource
import com.melfegor.nice64.api.ActiveBridgeProfileSource
import com.melfegor.nice64.api.enumerateAndroidKeyboardMouseDevices
import com.melfegor.nice64.pairing.AndroidPairingPresentationRepository
import com.melfegor.nice64.pairing.CameraPermissionState
import com.melfegor.nice64.pairing.PairingPresentationRepository
import com.melfegor.nice64.pairing.PairingPresentationState
import com.melfegor.nice64.pairing.PairingRepositoryFactory
import com.melfegor.nice64.pairing.PairingScreen
import com.melfegor.nice64.pairing.QrCameraPreview
import com.melfegor.nice64.ui.components.Nice64DisabledState
import com.melfegor.nice64.ui.components.Nice64Badge
import com.melfegor.nice64.ui.components.Nice64EmptyState
import com.melfegor.nice64.ui.components.Nice64ErrorState
import com.melfegor.nice64.ui.components.Nice64LoadingState
import com.melfegor.nice64.ui.components.Nice64MetadataRow
import com.melfegor.nice64.ui.components.Nice64StatusBanner
import com.melfegor.nice64.ui.components.Nice64StatusChip
import com.melfegor.nice64.ui.components.Nice64StatusTone
import com.melfegor.nice64.ui.theme.Nice64ShapeTokens
import com.melfegor.nice64.ui.theme.Nice64Spacing
import com.melfegor.nice64.ui.theme.Nice64Theme
import java.io.File

class MainActivity : ComponentActivity(), InputManager.InputDeviceListener {
    private lateinit var model: Nice64ViewModel
    private lateinit var inputManager: InputManager

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val pairingRepository = PairingRepositoryFactory.create(this)
        val activeProfileSource = StoredActiveBridgeProfileSource(pairingRepository)
        val factory = Nice64ViewModelFactory(
            AndroidPairingPresentationRepository(pairingRepository),
            BridgeDashboardRepository(activeProfileSource),
            BridgeSemanticMutationCoordinator(activeProfileSource),
            BridgeHistoryActivationCoordinator(activeProfileSource),
            BridgeControllerPrimaryCoordinator(activeProfileSource),
            activeProfileSource,
            applicationContext,
        )
        model = ViewModelProvider(this, factory)[Nice64ViewModel::class.java]
        inputManager = getSystemService(InputManager::class.java)
        inputManager.registerInputDeviceListener(this, null)
        window.decorView.setOnCapturedPointerListener { _, event ->
            ::model.isInitialized && model.handleKeyboardMouseMotionEvent(event)
        }
        refreshKeyboardMouseDevices()
        setContent { Nice64Root(model = model) }
    }

    override fun onResume() {
        super.onResume()
        if (::model.isInitialized) refreshKeyboardMouseDevices()
    }

    override fun onDestroy() {
        if (::inputManager.isInitialized) inputManager.unregisterInputDeviceListener(this)
        window.decorView.setOnCapturedPointerListener(null)
        super.onDestroy()
    }

    override fun onInputDeviceAdded(deviceId: Int) = refreshKeyboardMouseDevices()
    override fun onInputDeviceRemoved(deviceId: Int) = refreshKeyboardMouseDevices()
    override fun onInputDeviceChanged(deviceId: Int) = refreshKeyboardMouseDevices()

    private fun refreshKeyboardMouseDevices() {
        model.updateKeyboardMouseDevices(enumerateAndroidKeyboardMouseDevices())
    }

    @SuppressLint("RestrictedApi")
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (::model.isInitialized && model.handleKeyboardMouseKeyEvent(event)) return true
        val boundary = AndroidInputDispatchBoundary.capture(event)
        return if (::model.isInitialized && model.handleControllerKeyEvent(event, boundary)) true
        else super.dispatchKeyEvent(event)
    }

    override fun dispatchGenericMotionEvent(event: MotionEvent): Boolean {
        if (::model.isInitialized && model.handleKeyboardMouseMotionEvent(event)) return true
        return dispatchControllerInputWithPrecedence(
            event = event,
            capture = { ::model.isInitialized && model.handleControllerCaptureMotionEvent(it) },
            gameplay = { ::model.isInitialized && model.preDispatchControllerGameplayMotionEvent(it) },
            ui = { super.dispatchGenericMotionEvent(it) },
        )
    }

    override fun onPointerCaptureChanged(hasCapture: Boolean) {
        super.onPointerCaptureChanged(hasCapture)
        if (::model.isInitialized) model.keyboardMousePointerCaptureChanged(hasCapture)
    }
}

/** One Activity boundary shared by key and generic-motion controller events. */
internal inline fun <T> dispatchControllerInputWithPrecedence(
    event: T,
    capture: (T) -> Boolean,
    gameplay: (T) -> Boolean,
    ui: (T) -> Boolean,
): Boolean {
    if (capture(event)) return true
    if (gameplay(event)) return true
    return ui(event)
}

internal fun preDispatchControllerCaptureKeyEvent(
    event: KeyEvent,
    captureHandler: (KeyEvent) -> Boolean,
): Boolean = captureHandler(event)

internal class Nice64ViewModelFactory(
    private val repository: PairingPresentationRepository,
    private val dashboardRepository: BridgeDashboardRepository,
    private val mutationCoordinator: BridgeSemanticMutationCoordinator,
    private val historyActivationCoordinator: BridgeHistoryActivationCoordinator,
    private val controllerPrimaryCoordinator: BridgeControllerPrimaryCoordinator,
    private val keyboardMouseProfileSource: ActiveBridgeProfileSource,
    private val applicationContext: android.content.Context,
) : ViewModelProvider.Factory {
    @Suppress("UNCHECKED_CAST")
    override fun <T : ViewModel> create(modelClass: Class<T>): T {
        val controllerState = BridgeLatestControllerStateHolder()
        val profileRepository = PersistedControllerProfileUiRepository(
            ControllerProfileRepositoryFactoryV1.create(applicationContext),
        )
        val liveControllerProfiles = LiveControllerProfileCache(profileRepository)
        val controllerInput = BridgeControllerHumanInputCoordinator(
            applicationContext,
            controllerState,
            liveControllerProfiles,
        )
        val controllerSender = BridgeLatestStateSenderCoordinator(
            AndroidBridgeControllerNetworkEnvironment(applicationContext),
            controllerState = controllerState,
        )
        val keyboardMouseDirectState = BridgeKeyboardMouseDirectStateHolder()
        val keyboardMouseDirect = AndroidBridgeKeyboardMouseDirectDataPlane(
            AndroidBridgeControllerNetworkEnvironment(applicationContext),
            keyboardMouseDirectState,
        )
        val controllerProfiles = ControllerProfilePresentationCoordinator(profileRepository)
        val controllerInputTrace = ControllerInputTraceCoordinator(
            File(applicationContext.filesDir, "diagnostics"),
        )
        val mappingEditor = ControllerMappingEditorCoordinator(profileRepository) {
            controllerInput.setPhysicalCaptureSuppressed(it)
        }
        return Nice64ViewModel(
            repository, dashboardRepository, mutationCoordinator,
            historyActivationCoordinator, controllerPrimaryCoordinator,
            controllerSender, controllerInput, controllerProfiles, mappingEditor,
            liveControllerProfiles, controllerInputTrace, keyboardMouseProfileSource,
            keyboardMouseDirectState, keyboardMouseDirect,
            AndroidMouseInputPreferencesStore.create(applicationContext),
        ) as T
    }
}

@Composable
fun Nice64Root(
    factory: ViewModelProvider.Factory? = null,
    model: Nice64ViewModel = if (factory == null) viewModel() else viewModel(factory = factory),
    fullScreenPresentation: FullScreenControllerPresentationModel = viewModel(),
    blackoutPresentation: ControllerBlackoutPresentationModel = viewModel(),
) {
    val state by model.pairingState.collectAsStateWithLifecycle()
    val dashboard by model.dashboardState.collectAsStateWithLifecycle()
    val mutation by model.mutationState.collectAsStateWithLifecycle()
    val keyboardMouse by model.keyboardMouseState.collectAsStateWithLifecycle()
    val historyActivation by model.historyActivationState.collectAsStateWithLifecycle()
    val controllerPrimary by model.controllerPrimaryState.collectAsStateWithLifecycle()
    val controllerDataPlane by model.controllerDataPlaneState.collectAsStateWithLifecycle()
    val controllerInput by model.controllerInputState.collectAsStateWithLifecycle()
    val controllerInputTrace by model.controllerInputTraceState.collectAsStateWithLifecycle()
    val controllerDiagnostics by model.controllerDiagnosticsState.collectAsStateWithLifecycle()
    val controllerProfiles by model.controllerProfileState.collectAsStateWithLifecycle()
    val mappingEditor by model.mappingEditorState.collectAsStateWithLifecycle()
    val blackoutEligible = controllerBlackoutEligible(
        controllerPrimary.mobilePrimary,
        controllerInput,
    )
    val lifecycleOwner = LocalLifecycleOwner.current
    val activity = LocalActivity.current
    LaunchedEffect(blackoutEligible, blackoutPresentation.active) {
        blackoutPresentation.reconcileEligibility(blackoutEligible)
    }
    DisposableEffect(lifecycleOwner, model, blackoutPresentation) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                model.keyboardMouseForegroundReturned()
                model.controllerPrimaryForegrounded()
                model.reconcileMutationOnResume()
                model.reconcileHistoryActivationOnResume()
            }
            if (event == Lifecycle.Event.ON_STOP) {
                model.keyboardMouseForegroundLost()
                blackoutPresentation.exit()
                handleControllerActivityStop(
                    isChangingConfigurations = activity?.isChangingConfigurations == true,
                    clearTransientTouch = model::clearTouchControllerInput,
                    backgroundControllerRuntime = model::controllerPrimaryBackgrounded,
                )
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }
    Nice64App(
        state = state,
        dashboard = dashboard,
        mutation = mutation,
        keyboardMouse = keyboardMouse,
        historyActivation = historyActivation,
        controllerPrimary = controllerPrimary,
        controllerDataPlane = controllerDataPlane,
        controllerInput = controllerInput,
        controllerInputTrace = controllerInputTrace,
        controllerDiagnostics = controllerDiagnostics,
        controllerProfiles = controllerProfiles,
        mappingEditor = mappingEditor,
        onBeginPairing = model::beginPairing,
        onPermission = model::updateCameraPermission,
        onQrCode = model::acceptQr,
        onConfirmPairing = model::confirmPairing,
        onCancelPairing = model::cancelPairing,
        onRetryScan = model::retryScan,
        onSelect = model::selectProfile,
        onRequestUnpair = model::requestUnpair,
        onCancelUnpair = model::cancelUnpair,
        onConfirmUnpair = model::confirmUnpair,
        onRefresh = model::refreshBridge,
        mutationActions = BridgeMutationActions(
            model::selectFirmware,
            model::selectAccessory,
            model::selectNetworkPakModule,
            model::selectNetworkPakCarrier,
        ),
        keyboardMouseActions = KeyboardMouseActions(
            selectFirmware = model::selectFirmware,
            selectKeyboardSource = model::selectKeyboardSource,
            selectPointerSource = model::selectPointerSource,
            key = model::setOnScreenKeyboardKey,
            pointer = model::addOnScreenPointerDelta,
            button = model::setOnScreenPointerButton,
            invertX = model::setMouseInvertX,
            invertY = model::setMouseInvertY,
            refreshLeds = model::refreshKeyboardLeds,
            capturePointer = { activity?.window?.decorView?.requestPointerCapture() },
            releasePointer = { activity?.window?.decorView?.releasePointerCapture() },
        ),
        historyActivationActions = BridgeHistoryActivationActions(model::activateHistory),
        controllerPrimaryActions = BridgeControllerPrimaryActions(
            model::selectMobilePrimary,
            model::selectBridgePrimary,
        ),
        controllerInputActions = BridgeControllerInputActions(
            model::setTouchControllerButton,
            model::setTouchControllerAnalog,
            model::clearTouchControllerInput,
            model::selectMobileInputMode,
        ),
        controllerInputTraceActions = ControllerInputTraceActions(
            model::startControllerInputTrace,
            model::stopControllerInputTrace,
        ),
        controllerProfileActions = ControllerProfileActions(
            onRefresh = model::refreshControllerProfiles,
            onSelectController = model::selectControllerForProfiles,
            onCreate = model::createControllerProfile,
            onSelectProfile = model::selectControllerProfile,
            onDelete = model::deleteControllerProfile,
            onEdit = model::openControllerMappingEditor,
        ),
        mappingEditorActions = MappingEditorActions(
            onName = model::setMappingProfileName,
            onButtonCapture = model::beginMappingButtonCapture,
            onAxisCapture = model::beginMappingAxisCapture,
            onAnalogPairCapture = model::beginMappingAnalogPairCapture,
            onCancelCapture = model::cancelMappingCapture,
            onApplyCandidate = model::applyMappingCandidate,
            onClear = model::clearMappingTarget,
            onToggleInvert = model::toggleMappingInversion,
            onDeadZone = model::setMappingDeadZone,
            onScale = model::setMappingScale,
            onRequestRestore = model::requestRestoreControllerMappings,
            onCancelRestore = model::cancelRestoreControllerMappings,
            onConfirmRestore = model::confirmRestoreControllerMappings,
            onSave = model::saveControllerMappingProfile,
            onClose = model::requestCloseMappingEditor,
            onContinueEditing = model::continueMappingEditing,
            onDiscard = model::discardMappingEditor,
            onRecoverController = model::recoverMappingController,
        ),
        fullScreenController = fullScreenPresentation.active,
        fullScreenPreviousOrientation = fullScreenPresentation.previousOrientation,
        onEnterFullScreenController = {
            fullScreenPresentation.enter(
                activity?.requestedOrientation
                    ?: android.content.pm.ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED,
            )
        },
        onExitFullScreenController = fullScreenPresentation::exit,
        controllerBlackout = blackoutPresentation.active,
        onEnterControllerBlackout = { blackoutPresentation.enter(blackoutEligible) },
        onExitControllerBlackout = blackoutPresentation::exit,
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun Nice64App(
    state: PairingPresentationState,
    dashboard: BridgeDashboardSnapshot = BridgeDashboardSnapshot(),
    mutation: BridgeMutationPresentation = BridgeMutationPresentation(),
    keyboardMouse: BridgeKeyboardMousePresentation = BridgeKeyboardMousePresentation(),
    historyActivation: BridgeHistoryActivationPresentation =
        BridgeHistoryActivationPresentation(),
    controllerPrimary: BridgeControllerPrimaryPresentation =
        BridgeControllerPrimaryPresentation(),
    controllerDataPlane: BridgeControllerDataPlanePresentation =
        BridgeControllerDataPlanePresentation(),
    controllerInput: BridgeControllerInputPresentation =
        BridgeControllerInputPresentation(),
    controllerInputTrace: ControllerInputTracePresentation =
        ControllerInputTracePresentation(),
    controllerDiagnostics: BridgeControllerDiagnosticsPresentation =
        BridgeControllerDiagnosticsPresentation(),
    controllerProfiles: ControllerProfilePresentationState =
        ControllerProfilePresentationState(),
    mappingEditor: MappingEditorState = MappingEditorState(),
    onBeginPairing: () -> Unit,
    onPermission: (Boolean, Boolean) -> Unit,
    onQrCode: (String) -> Unit,
    onConfirmPairing: () -> Unit,
    onCancelPairing: () -> Unit,
    onRetryScan: () -> Unit,
    onSelect: (String) -> Unit,
    onRequestUnpair: (String) -> Unit,
    onCancelUnpair: () -> Unit,
    onConfirmUnpair: () -> Unit,
    onRefresh: () -> Unit = {},
    mutationActions: BridgeMutationActions = BridgeMutationActions(),
    keyboardMouseActions: KeyboardMouseActions = KeyboardMouseActions(),
    historyActivationActions: BridgeHistoryActivationActions =
        BridgeHistoryActivationActions(),
    controllerPrimaryActions: BridgeControllerPrimaryActions =
        BridgeControllerPrimaryActions(),
    controllerInputActions: BridgeControllerInputActions = BridgeControllerInputActions(),
    controllerInputTraceActions: ControllerInputTraceActions = ControllerInputTraceActions(),
    controllerProfileActions: ControllerProfileActions = ControllerProfileActions(),
    mappingEditorActions: MappingEditorActions = MappingEditorActions(),
    fullScreenController: Boolean = false,
    fullScreenPreviousOrientation: Int =
        android.content.pm.ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED,
    onEnterFullScreenController: () -> Unit = {},
    onExitFullScreenController: () -> Unit = {},
    controllerBlackout: Boolean = false,
    onEnterControllerBlackout: () -> Unit = {},
    onExitControllerBlackout: () -> Unit = {},
) {
    Nice64Theme {
        if (controllerBlackout) {
            ControllerBlackoutPresentation(onExit = onExitControllerBlackout)
            return@Nice64Theme
        }
        if (fullScreenController) {
            FullScreenControllerWindowEffect(
                active = true,
                previousOrientation = fullScreenPreviousOrientation,
            )
            BackHandler(onBack = onExitFullScreenController)
            Surface(modifier = Modifier.fillMaxSize()) {
                FullScreenTouchController(
                    input = controllerInput,
                    authorityAvailable = controllerPrimary.mobilePrimary,
                    actions = controllerInputActions,
                    onExit = onExitFullScreenController,
                )
            }
            return@Nice64Theme
        }
        Surface(modifier = Modifier.fillMaxSize()) {
            Scaffold(topBar = { TopAppBar(title = { Text(stringResource(R.string.app_name)) }) }) { padding ->
                Box(Modifier.fillMaxSize().padding(padding)) {
                    when (state.screen) {
                        PairingScreen.PROFILES -> ProfilesScreen(
                            state, dashboard, mutation, keyboardMouse, historyActivation,
                            controllerPrimary,
                            controllerDataPlane,
                            controllerInput,
                            controllerInputTrace,
                            controllerDiagnostics,
                            controllerProfiles,
                            mappingEditor,
                            onBeginPairing, onSelect, onRequestUnpair, onRefresh,
                            mutationActions, keyboardMouseActions, historyActivationActions,
                            controllerPrimaryActions,
                            controllerInputActions,
                            controllerInputTraceActions,
                            controllerProfileActions,
                            mappingEditorActions,
                            onEnterFullScreenController,
                            onEnterControllerBlackout,
                        )
                        PairingScreen.SCANNER -> ScannerScreen(state.permission, onPermission, onQrCode, onCancelPairing)
                        PairingScreen.CONFIRMATION -> ConfirmationScreen(state, onConfirmPairing, onCancelPairing)
                        PairingScreen.SAVING -> Nice64LoadingState(
                            message = "Saving pairing details securely…",
                            modifier = Modifier.align(Alignment.Center),
                        )
                        PairingScreen.ERROR -> ErrorScreen(
                            state.error?.safeMessage ?: "Pairing failed safely.",
                            onRetryScan,
                            onCancelPairing,
                        )
                    }
                }
                state.pendingUnpairProfileId?.let { profileId ->
                    val profileLabel = state.profiles
                        .firstOrNull { it.profileId == profileId }
                        ?.displayLabel ?: "this Bridge"
                    AlertDialog(
                        onDismissRequest = onCancelUnpair,
                        title = { Text("Unpair Bridge?") },
                        text = { Text("This removes $profileLabel and its protected credential from this phone.") },
                        confirmButton = {
                            TextButton(
                                onClick = onConfirmUnpair,
                                modifier = Modifier.semantics {
                                    contentDescription = "Unpair $profileLabel"
                                },
                                colors = ButtonDefaults.textButtonColors(
                                    contentColor = MaterialTheme.colorScheme.error,
                                ),
                            ) { Text("Unpair") }
                        },
                        dismissButton = { TextButton(onClick = onCancelUnpair) { Text("Cancel") } },
                    )
                }
            }
        }
    }
}

@Composable
private fun ProfilesScreen(
    state: PairingPresentationState,
    dashboard: BridgeDashboardSnapshot,
    mutation: BridgeMutationPresentation,
    keyboardMouse: BridgeKeyboardMousePresentation,
    historyActivation: BridgeHistoryActivationPresentation,
    controllerPrimary: BridgeControllerPrimaryPresentation,
    controllerDataPlane: BridgeControllerDataPlanePresentation,
    controllerInput: BridgeControllerInputPresentation,
    controllerInputTrace: ControllerInputTracePresentation,
    controllerDiagnostics: BridgeControllerDiagnosticsPresentation,
    controllerProfiles: ControllerProfilePresentationState,
    mappingEditor: MappingEditorState,
    onPair: () -> Unit,
    onSelect: (String) -> Unit,
    onUnpair: (String) -> Unit,
    onRefresh: () -> Unit,
    mutationActions: BridgeMutationActions,
    keyboardMouseActions: KeyboardMouseActions,
    historyActivationActions: BridgeHistoryActivationActions,
    controllerPrimaryActions: BridgeControllerPrimaryActions,
    controllerInputActions: BridgeControllerInputActions,
    controllerInputTraceActions: ControllerInputTraceActions,
    controllerProfileActions: ControllerProfileActions,
    mappingEditorActions: MappingEditorActions,
    onEnterFullScreenController: () -> Unit,
    onEnterControllerBlackout: () -> Unit,
) {
    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        if (state.profiles.isEmpty()) {
            Nice64EmptyState(
                title = "No paired Bridge",
                message = "Pair a NICE64 Bridge by scanning its pairing QR code.",
                actionLabel = "Pair Bridge",
                onAction = onPair,
            )
        } else {
            Text("Paired Bridges", style = MaterialTheme.typography.headlineSmall)
            Spacer(Modifier.height(Nice64Spacing.md))
            if (state.activeProfileId != null) {
                BridgeDashboard(
                    dashboard, mutation, keyboardMouse, historyActivation, controllerPrimary,
                    controllerDataPlane, controllerInput,
                    controllerInputTrace,
                    controllerDiagnostics,
                    controllerProfiles,
                    mappingEditor,
                    onRefresh, mutationActions, keyboardMouseActions, historyActivationActions,
                    controllerPrimaryActions,
                    controllerInputActions,
                    controllerInputTraceActions,
                    controllerProfileActions,
                    mappingEditorActions,
                    onEnterFullScreenController,
                    onEnterControllerBlackout,
                )
                Spacer(Modifier.height(Nice64Spacing.md))
            }
            state.profiles.forEach { profile ->
                val selectedProfile = profile.profileId == state.activeProfileId
                Card(Modifier.fillMaxWidth()) {
                    Column(
                        modifier = Modifier.padding(Nice64Spacing.lg),
                        verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
                    ) {
                        Text(profile.displayLabel, style = MaterialTheme.typography.titleMedium)
                        if (selectedProfile) {
                            Nice64StatusChip(
                                text = "Selected Bridge",
                                tone = Nice64StatusTone.HEALTHY,
                                modifier = Modifier.semantics {
                                    selected = true
                                    contentDescription = "Selected Bridge: ${profile.displayLabel}"
                                },
                            )
                        }
                        Nice64MetadataRow("Endpoint", profile.apiBaseUrl)
                        Column(
                            modifier = Modifier.fillMaxWidth(),
                            verticalArrangement = Arrangement.spacedBy(Nice64Spacing.xs),
                        ) {
                            if (!selectedProfile) {
                                Button(
                                    onClick = { onSelect(profile.profileId) },
                                    modifier = Modifier.fillMaxWidth(),
                                ) {
                                    Text("Select Bridge")
                                }
                            }
                            TextButton(
                                onClick = { onUnpair(profile.profileId) },
                                modifier = Modifier.fillMaxWidth().semantics {
                                    contentDescription = "Unpair ${profile.displayLabel}"
                                },
                                colors = ButtonDefaults.textButtonColors(
                                    contentColor = MaterialTheme.colorScheme.error,
                                ),
                            ) { Text("Unpair") }
                        }
                    }
                }
            }
            Button(onClick = onPair, modifier = Modifier.fillMaxWidth()) {
                Text("Pair another Bridge")
            }
        }
    }
}

@Composable
private fun BridgeDashboard(
    state: BridgeDashboardSnapshot,
    mutation: BridgeMutationPresentation,
    keyboardMouse: BridgeKeyboardMousePresentation,
    historyActivation: BridgeHistoryActivationPresentation,
    controllerPrimary: BridgeControllerPrimaryPresentation,
    controllerDataPlane: BridgeControllerDataPlanePresentation,
    controllerInput: BridgeControllerInputPresentation,
    controllerInputTrace: ControllerInputTracePresentation,
    controllerDiagnostics: BridgeControllerDiagnosticsPresentation,
    controllerProfiles: ControllerProfilePresentationState,
    mappingEditor: MappingEditorState,
    onRefresh: () -> Unit,
    mutationActions: BridgeMutationActions,
    keyboardMouseActions: KeyboardMouseActions,
    historyActivationActions: BridgeHistoryActivationActions,
    controllerPrimaryActions: BridgeControllerPrimaryActions,
    controllerInputActions: BridgeControllerInputActions,
    controllerInputTraceActions: ControllerInputTraceActions,
    controllerProfileActions: ControllerProfileActions,
    mappingEditorActions: MappingEditorActions,
    onEnterFullScreenController: () -> Unit,
    onEnterControllerBlackout: () -> Unit,
) {
    var section by rememberSaveable { mutableStateOf(BridgeRemoteSection.BRIDGE) }
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text("Remote", style = MaterialTheme.typography.headlineSmall)
            BridgeSectionNavigation(section) { section = it }
            if (section == BridgeRemoteSection.HISTORY) {
                BridgeHistoryActivationControls(
                    state, historyActivation, mutation.inFlight ||
                        (controllerPrimary.mobilePrimary || controllerPrimary.actionInFlight),
                    historyActivationActions,
                )
                Button(onClick = onRefresh, enabled = !state.refreshing, modifier = Modifier.fillMaxWidth()) {
                    Text(if (state.refreshing) "Refreshing…" else "Refresh")
                }
                return@Column
            }
            if (section == BridgeRemoteSection.CONTROLLERS) {
                BridgeControllersScreen(
                    controllerProfiles,
                    controllerDiagnostics,
                    controllerProfileActions,
                    mappingEditor,
                    mappingEditorActions,
                    controllerInput,
                    controllerInputTrace,
                    controllerInputTraceActions,
                    controllerPrimary.mobilePrimary,
                )
                return@Column
            }
            if (section == BridgeRemoteSection.INPUT) {
                KeyboardMouseControls(state, mutation, keyboardMouse, keyboardMouseActions)
                Button(onClick = onRefresh, enabled = !state.refreshing, modifier = Modifier.fillMaxWidth()) {
                    Text(if (state.refreshing) "Refreshing…" else "Refresh")
                }
                return@Column
            }
            BridgeSummary(state)
            BridgeControllerPrimaryControls(
                state, controllerPrimary,
                controllerDataPlane,
                mutation.inFlight || historyActivation.inFlight,
                controllerPrimaryActions,
            )
            if (controllerPrimary.mobilePrimary) {
                BridgeTouchController(
                    controllerInput,
                    controllerInputActions,
                    onEnterFullScreenController,
                    onEnterControllerBlackout,
                )
            }
            BridgeMutationControls(
                state, mutation, mutationActions,
                additionalBusy = historyActivation.inFlight ||
                    (controllerPrimary.mobilePrimary || controllerPrimary.actionInFlight),
            )
            BridgeTechnicalDetails(state)
            Button(onClick = onRefresh, enabled = !state.refreshing, modifier = Modifier.fillMaxWidth()) {
                Text(if (state.refreshing) "Refreshing…" else "Refresh")
            }
        }
    }
}

@Composable
internal fun BridgeControllerDiagnostics(
    state: BridgeControllerDiagnosticsPresentation,
) {
    var showInputs by rememberSaveable { mutableStateOf(false) }
    Text("Controller identity and raw input", style = MaterialTheme.typography.titleMedium)
    Text(
        "Technical Android input details. Diagnostics remain active whether this detail is shown or hidden.",
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    if (state.devices.isEmpty()) {
        Nice64EmptyState(
            title = "No controller diagnostics",
            message = "No Android GAMEPAD or JOYSTICK device is currently observed.",
        )
        return
    }
    Nice64MetadataRow("Observed controller count", state.devices.size.toString())
    Nice64MetadataRow(
        "Active gameplay controller",
        state.activeDevice?.name ?: "None (activates on accepted gameplay input)",
    )
    state.devices.forEach { device ->
        Card(Modifier.fillMaxWidth()) {
            Column(
                Modifier.padding(Nice64Spacing.lg),
                verticalArrangement = Arrangement.spacedBy(Nice64Spacing.xs),
            ) {
                Text(device.name, style = MaterialTheme.typography.titleMedium)
                Nice64MetadataRow("Runtime device ID", "${device.runtimeDeviceId} (transient)")
                Nice64MetadataRow("Controller identifier suffix", "…${device.descriptorSuffix}")
                Nice64MetadataRow(
                    "Vendor / product ID",
                    "%04X / %04X".format(device.vendorId, device.productId),
                )
                Nice64MetadataRow(
                    "Android input sources",
                    "${buildList {
                        if (device.hasGamepadSource) add("GAMEPAD")
                        if (device.hasJoystickSource) add("JOYSTICK")
                    }.joinToString(" + ")} (raw 0x${device.sources.toUInt().toString(16).uppercase()})",
                )
                if (device.controllerNumber > 0) {
                    Nice64MetadataRow(
                        "Android controller number",
                        "${device.controllerNumber} (display only)",
                    )
                }
            }
        }
    }
    Button(onClick = { showInputs = !showInputs }, modifier = Modifier.fillMaxWidth()) {
        Text(if (showInputs) "Hide Inputs" else "View Inputs")
    }
    if (!showInputs) return
    val viewed = state.viewedDevice ?: return
    Text("Observed input events", style = MaterialTheme.typography.titleMedium)
    Nice64MetadataRow("Viewing controller", viewed.name)
    Text("Recent buttons", style = MaterialTheme.typography.titleSmall)
    val buttons = state.recentButtons.filter { it.runtimeDeviceId == viewed.runtimeDeviceId }
    if (buttons.isEmpty()) {
        Nice64EmptyState(
            title = "No recent button events",
            message = "Press a controller button to populate this diagnostic view.",
        )
    }
    buttons.forEach {
        Nice64MetadataRow(
            "${it.keyName} (${it.keyCode})",
            "${it.action} — source 0x${it.source.toUInt().toString(16).uppercase()}",
        )
    }
    Text("Axes and ranges", style = MaterialTheme.typography.titleSmall)
    val values = state.currentAxes
        .filter { it.runtimeDeviceId == viewed.runtimeDeviceId }
        .associateBy { it.axisCode }
    if (viewed.motionRanges.isEmpty()) {
        Nice64EmptyState(
            title = "No axis ranges",
            message = "Android reported no relevant motion ranges for this controller.",
        )
    }
    viewed.motionRanges.forEach { range ->
        val value = values[range.axisCode]
        Nice64MetadataRow(
            "${range.axisName} (${range.axisCode})",
            "raw=${value?.rawValue?.formatDiagnostic() ?: "—"} " +
                "norm=${value?.normalizedValue?.formatDiagnostic() ?: "—"} " +
                "range=${range.minimum.formatDiagnostic()}..${range.maximum.formatDiagnostic()} " +
                "flat=${range.flat.formatDiagnostic()} source=0x${range.source.toUInt().toString(16).uppercase()}",
        )
    }
}

private fun Float.formatDiagnostic(): String = String.format(java.util.Locale.ROOT, "%.3f", this)

@Composable
private fun ScannerScreen(
    permission: CameraPermissionState,
    onPermission: (Boolean, Boolean) -> Unit,
    onQrCode: (String) -> Unit,
    onCancel: () -> Unit,
) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        val permanentlyDenied = !granted && context is ComponentActivity &&
            !context.shouldShowRequestPermissionRationale(Manifest.permission.CAMERA)
        onPermission(granted, permanentlyDenied)
    }
    LaunchedEffect(Unit) {
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
            onPermission(true, false)
        } else {
            launcher.launch(Manifest.permission.CAMERA)
        }
    }
    DisposableEffect(lifecycleOwner, context) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME &&
                ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
            ) {
                onPermission(true, false)
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }
    ScannerPresentation(
        permission = permission,
        onGrantPermission = { launcher.launch(Manifest.permission.CAMERA) },
        onOpenSettings = {
            context.startActivity(
                Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, "package:${context.packageName}".toUri()),
            )
        },
        onCancel = onCancel,
    ) {
        QrCameraPreview(onQrCode, Modifier.fillMaxSize())
        Box(
            Modifier
                .align(Alignment.Center)
                .fillMaxWidth(.68f)
                .aspectRatio(1f)
                .border(3.dp, MaterialTheme.colorScheme.primary, Nice64ShapeTokens.standard)
                .semantics { contentDescription = "QR scan target" },
        )
    }
}

@Composable
internal fun ScannerPresentation(
    permission: CameraPermissionState,
    onGrantPermission: () -> Unit,
    onOpenSettings: () -> Unit,
    onCancel: () -> Unit,
    preview: @Composable BoxScope.() -> Unit = {},
) {
    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(Nice64Spacing.xl),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(Nice64Spacing.md),
    ) {
        Text("Scan Bridge pairing QR", style = MaterialTheme.typography.headlineSmall)
        Text(
            "Center the pairing code shown by the NICE64 Bridge inside the frame.",
            style = MaterialTheme.typography.bodyMedium,
        )
        when (permission) {
            CameraPermissionState.GRANTED -> Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .widthIn(max = 420.dp)
                    .aspectRatio(1f),
                content = preview,
            )
            CameraPermissionState.PERMANENTLY_DENIED -> Nice64ErrorState(
                title = "Camera access disabled",
                message = "Enable camera permission in Android settings to scan a pairing code.",
                retryLabel = "Open settings",
                onRetry = onOpenSettings,
            )
            CameraPermissionState.DENIED -> Column(
                verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Nice64DisabledState(
                    title = "Camera access required",
                    explanation = "Camera access is used only while scanning a pairing QR code.",
                )
                Button(onClick = onGrantPermission) { Text("Grant camera access") }
            }
            CameraPermissionState.UNKNOWN -> Nice64LoadingState("Checking camera permission…")
        }
        TextButton(onClick = onCancel) { Text("Cancel") }
    }
}

@Composable
private fun ConfirmationScreen(
    state: PairingPresentationState,
    onConfirm: () -> Unit,
    onCancel: () -> Unit,
) {
    val candidate = state.confirmation ?: return
    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Nice64Spacing.xl),
        verticalArrangement = Arrangement.spacedBy(Nice64Spacing.md),
    ) {
        Text("Pair with NICE64 Bridge?", style = MaterialTheme.typography.headlineSmall)
        Nice64StatusChip("Pairing code recognized", Nice64StatusTone.HEALTHY)
        Text("Review the Bridge identity before storing this connection securely.")
        Card(Modifier.fillMaxWidth()) {
            Column(
                Modifier.padding(Nice64Spacing.lg),
                verticalArrangement = Arrangement.spacedBy(Nice64Spacing.sm),
            ) {
                Nice64MetadataRow("Endpoint", candidate.endpoint)
                Nice64MetadataRow("API version", candidate.apiVersion)
                Nice64MetadataRow("SPKI SHA-256", candidate.spkiSummary)
                Nice64MetadataRow("Certificate SHA-256", candidate.certificateSummary)
            }
        }
        if (candidate.identityChangeWarning) {
            Nice64StatusBanner(
                title = "Bridge identity changed",
                message = "A profile already uses this endpoint with a different cryptographic identity. Confirm only if the Bridge was intentionally re-paired.",
                tone = Nice64StatusTone.WARNING,
            )
        }
        Button(onClick = onConfirm, modifier = Modifier.fillMaxWidth()) { Text("Pair Bridge") }
        TextButton(onClick = onCancel, modifier = Modifier.fillMaxWidth()) { Text("Cancel") }
    }
}

@Composable
private fun ErrorScreen(message: String, onRetry: () -> Unit, onCancel: () -> Unit) {
    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(Nice64Spacing.xl),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Nice64ErrorState(
            title = "Pairing failed",
            message = message,
            retryLabel = "Scan again",
            onRetry = onRetry,
        )
        TextButton(onClick = onCancel) { Text("Back to profiles") }
    }
}
