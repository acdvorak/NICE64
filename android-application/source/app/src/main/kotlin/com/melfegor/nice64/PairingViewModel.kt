package com.melfegor.nice64

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import android.os.SystemClock
import android.view.KeyEvent
import android.view.MotionEvent
import com.melfegor.nice64.core.model.BridgeOnboardingCandidate
import com.melfegor.nice64.core.model.BridgePairingMetadata
import com.melfegor.nice64.core.model.Nice64AppState
import com.melfegor.nice64.core.model.PairingState
import com.melfegor.nice64.api.BridgeConnectionPresentation
import com.melfegor.nice64.api.BridgeDashboardRepository
import com.melfegor.nice64.api.BridgeDashboardSnapshot
import com.melfegor.nice64.api.BridgeAccessoryTarget
import com.melfegor.nice64.api.BridgeCarrierTarget
import com.melfegor.nice64.api.BridgeFirmwareTarget
import com.melfegor.nice64.api.BridgeModuleInfoDto
import com.melfegor.nice64.api.BridgeMutationCommand
import com.melfegor.nice64.api.BridgeMutationPhase
import com.melfegor.nice64.api.BridgeMutationPresentation
import com.melfegor.nice64.api.BridgeSemanticMutationCoordinator
import com.melfegor.nice64.api.ActiveBridgeProfileSource
import com.melfegor.nice64.api.AndroidInputSource
import com.melfegor.nice64.api.AndroidKeyboardMouseDevice
import com.melfegor.nice64.api.BridgeKeyboardMouseCoordinator
import com.melfegor.nice64.api.BridgeKeyboardMousePresentation
import com.melfegor.nice64.api.BridgeKeyboardMouseDirectDataPlane
import com.melfegor.nice64.api.BridgeKeyboardMouseDirectStateHolder
import com.melfegor.nice64.api.MouseInputPreferencesStore
import com.melfegor.nice64.api.Nice64LogicalKey
import com.melfegor.nice64.api.PinnedBridgeKeyboardMouseGateway
import com.melfegor.nice64.api.BridgeHistoryActivationCommand
import com.melfegor.nice64.api.BridgeHistoryActivationCoordinator
import com.melfegor.nice64.api.BridgeHistoryActivationPhase
import com.melfegor.nice64.api.BridgeHistoryActivationPresentation
import com.melfegor.nice64.api.BridgeHistoryCategory
import com.melfegor.nice64.api.BridgeHistoryItemDto
import com.melfegor.nice64.api.shouldPublishHistoryActivation
import com.melfegor.nice64.api.BridgeControllerPrimaryCoordinator
import com.melfegor.nice64.api.BridgeControllerPrimaryFailureKind
import com.melfegor.nice64.api.BridgeControllerPrimaryPhase
import com.melfegor.nice64.api.BridgeControllerPrimaryPresentation
import com.melfegor.nice64.api.BridgeControllerDataPlanePresentation
import com.melfegor.nice64.api.BridgeControllerDataPlanePhase
import com.melfegor.nice64.api.BridgeControllerHumanInputCoordinator
import com.melfegor.nice64.api.AndroidPhysicalButtonObservation
import com.melfegor.nice64.api.AndroidPhysicalButtonPipelineResult
import com.melfegor.nice64.api.BridgeControllerDiagnosticsPresentation
import com.melfegor.nice64.api.BridgeControllerInputPresentation
import com.melfegor.nice64.api.MobileInputMode
import com.melfegor.nice64.api.AndroidInputDispatchBoundary
import com.melfegor.nice64.api.ControllerInputTraceCoordinator
import com.melfegor.nice64.api.ControllerInputTraceEdge
import com.melfegor.nice64.api.ControllerInputTracePresentation
import com.melfegor.nice64.api.ControllerProfilePresentationCoordinator
import com.melfegor.nice64.api.ControllerProfilePresentationState
import com.melfegor.nice64.api.ControllerMappingEditorCoordinator
import com.melfegor.nice64.api.MappingCaptureAxisInput
import com.melfegor.nice64.api.MappingCaptureKeyInput
import com.melfegor.nice64.api.MappingEditorState
import com.melfegor.nice64.api.LiveControllerProfileCache
import com.melfegor.nice64.api.BridgeControllerLifecycleActions
import com.melfegor.nice64.api.BridgeControllerLifecycleConvergence
import com.melfegor.nice64.api.BridgeControllerSenderAuthority
import com.melfegor.nice64.api.BridgeLatestStateSenderCoordinator
import com.melfegor.nice64.api.senderAuthorityAt
import com.melfegor.nice64.api.shouldPublishControllerPrimary
import com.melfegor.nice64.api.shouldStartReconciledControllerSender
import com.melfegor.nice64.api.shouldPublishMutation
import com.melfegor.nice64.core.pairing.BridgeOnboardingParseResult
import com.melfegor.nice64.core.pairing.BridgeOnboardingParser
import com.melfegor.nice64.pairing.CameraPermissionState
import com.melfegor.nice64.pairing.PairingPresentationError
import com.melfegor.nice64.pairing.PairingPresentationRepository
import com.melfegor.nice64.pairing.PairingPresentationState
import com.melfegor.nice64.pairing.PairingScreen
import com.melfegor.nice64.pairing.PairingStorageResult
import com.melfegor.nice64.pairing.toSafeConfirmation
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.Dispatchers

class Nice64ViewModel internal constructor(
    private val repository: PairingPresentationRepository? = null,
    private val dashboardRepository: BridgeDashboardRepository? = null,
    private val mutationCoordinator: BridgeSemanticMutationCoordinator? = null,
    private val historyActivationCoordinator: BridgeHistoryActivationCoordinator? = null,
    private val controllerPrimaryCoordinator: BridgeControllerPrimaryCoordinator? = null,
    private val controllerSenderCoordinator: BridgeLatestStateSenderCoordinator? = null,
    private val controllerInputCoordinator: BridgeControllerHumanInputCoordinator? = null,
    private val controllerProfileCoordinator: ControllerProfilePresentationCoordinator? = null,
    private val controllerMappingEditor: ControllerMappingEditorCoordinator? = null,
    private val liveControllerProfiles: LiveControllerProfileCache? = null,
    private val controllerInputTrace: ControllerInputTraceCoordinator? = null,
    keyboardMouseProfileSource: ActiveBridgeProfileSource? = null,
    keyboardMouseDirectState: BridgeKeyboardMouseDirectStateHolder? = null,
    keyboardMouseDirectDataPlane: BridgeKeyboardMouseDirectDataPlane? = null,
    mouseInputPreferencesStore: MouseInputPreferencesStore? = null,
) : ViewModel() {
    private val mutableState = MutableStateFlow(Nice64AppState())
    val state: StateFlow<Nice64AppState> = mutableState.asStateFlow()

    private val mutablePairingState = MutableStateFlow(PairingPresentationState())
    val pairingState: StateFlow<PairingPresentationState> = mutablePairingState.asStateFlow()

    private val mutableDashboardState = MutableStateFlow(BridgeDashboardSnapshot())
    val dashboardState: StateFlow<BridgeDashboardSnapshot> = mutableDashboardState.asStateFlow()

    private val mutableMutationState = MutableStateFlow(BridgeMutationPresentation())
    val mutationState: StateFlow<BridgeMutationPresentation> = mutableMutationState.asStateFlow()

    private val keyboardMouseCoordinator = keyboardMouseProfileSource?.let {
        BridgeKeyboardMouseCoordinator(
            PinnedBridgeKeyboardMouseGateway(it), viewModelScope,
            keyboardMouseDirectState ?: BridgeKeyboardMouseDirectStateHolder(),
            keyboardMouseDirectDataPlane,
            mouseInputPreferencesStore,
        )
    }
    private val fallbackKeyboardMouse = MutableStateFlow(BridgeKeyboardMousePresentation())
    val keyboardMouseState: StateFlow<BridgeKeyboardMousePresentation> =
        keyboardMouseCoordinator?.state ?: fallbackKeyboardMouse.asStateFlow()

    private val mutableHistoryActivationState =
        MutableStateFlow(BridgeHistoryActivationPresentation())
    val historyActivationState: StateFlow<BridgeHistoryActivationPresentation> =
        mutableHistoryActivationState.asStateFlow()

    private val mutableControllerPrimaryState =
        MutableStateFlow(BridgeControllerPrimaryPresentation())
    val controllerPrimaryState: StateFlow<BridgeControllerPrimaryPresentation> =
        mutableControllerPrimaryState.asStateFlow()

    private val mutableControllerDataPlaneState =
        MutableStateFlow(BridgeControllerDataPlanePresentation())
    val controllerDataPlaneState: StateFlow<BridgeControllerDataPlanePresentation> =
        mutableControllerDataPlaneState.asStateFlow()

    private val fallbackControllerInput = MutableStateFlow(BridgeControllerInputPresentation())
    val controllerInputState: StateFlow<BridgeControllerInputPresentation> =
        controllerInputCoordinator?.presentation ?: fallbackControllerInput.asStateFlow()

    private val fallbackControllerInputTrace =
        MutableStateFlow(ControllerInputTracePresentation())
    internal val controllerInputTraceState: StateFlow<ControllerInputTracePresentation> =
        controllerInputTrace?.state ?: fallbackControllerInputTrace.asStateFlow()

    private val fallbackControllerDiagnostics =
        MutableStateFlow(BridgeControllerDiagnosticsPresentation())
    internal val controllerDiagnosticsState: StateFlow<BridgeControllerDiagnosticsPresentation> =
        controllerInputCoordinator?.diagnostics ?: fallbackControllerDiagnostics.asStateFlow()

    private val fallbackControllerProfiles =
        MutableStateFlow(ControllerProfilePresentationState())
    internal val controllerProfileState: StateFlow<ControllerProfilePresentationState> =
        controllerProfileCoordinator?.state ?: fallbackControllerProfiles.asStateFlow()

    private val fallbackMappingEditor = MutableStateFlow(MappingEditorState())
    internal val mappingEditorState: StateFlow<MappingEditorState> =
        controllerMappingEditor?.state ?: fallbackMappingEditor.asStateFlow()

    private var pendingCandidate: BridgeOnboardingCandidate? = null
    private val scanAccepted = AtomicBoolean(false)
    private val saveStarted = AtomicBoolean(false)
    private val mutationWorker = AtomicBoolean(false)
    private val historyActivationWorker = AtomicBoolean(false)
    private val controllerPrimaryWorker = AtomicBoolean(false)
    private val controllerSenderStartWorker = AtomicBoolean(false)
    private val controllerForegroundReconciliationWorker = AtomicBoolean(false)
    private val activeUnpairWorker = AtomicBoolean(false)
    private var mappingCaptureTimeout: Job? = null

    init {
        controllerSenderCoordinator?.observeNetworkChanges(::controllerNetworkChanged)
        if (repository != null) refreshProfiles()
        if (liveControllerProfiles != null) {
            viewModelScope.launch { refreshLiveControllerMapping() }
        }
        controllerProfileCoordinator?.let { coordinator ->
            viewModelScope.launch {
                var initial = true
                controllerDiagnosticsState
                    .map { it.copy(recentButtons = emptyList(), currentAxes = emptyList()) }
                    .distinctUntilChanged()
                    .collect { diagnostics ->
                        if (initial) {
                            initial = false
                            coordinator.initialize(diagnostics)
                        } else coordinator.updateInventory(diagnostics)
                        controllerMappingEditor?.updateDevices(diagnostics)
                    }
            }
        }
    }

    fun refreshControllerProfiles() {
        controllerProfileCoordinator?.let {
            viewModelScope.launch {
                it.refresh()
                refreshLiveControllerMapping()
            }
        }
    }

    fun selectControllerForProfiles(runtimeDeviceId: Int) {
        if (controllerMappingEditor?.canChangeManagementDevice() == false) {
            controllerMappingEditor.rejectManagementDeviceChange()
            return
        }
        controllerProfileCoordinator?.let {
            viewModelScope.launch { it.selectManagementController(runtimeDeviceId) }
        }
    }

    fun createControllerProfile(name: String) {
        controllerProfileCoordinator?.let {
            viewModelScope.launch {
                it.createProfile(name)
                refreshLiveControllerMapping()
            }
        }
    }

    fun selectControllerProfile(profileId: String?) {
        controllerProfileCoordinator?.let {
            viewModelScope.launch {
                it.selectProfile(profileId)
                refreshLiveControllerMapping()
            }
        }
    }

    fun deleteControllerProfile(profileId: String) {
        controllerProfileCoordinator?.let {
            viewModelScope.launch {
                it.deleteProfile(profileId)
                refreshLiveControllerMapping()
            }
        }
    }

    fun openControllerMappingEditor(profileId: String) {
        val editor = controllerMappingEditor ?: return
        viewModelScope.launch {
            editor.open(profileId, controllerProfileState.value.managementDevice)
        }
    }

    fun beginMappingButtonCapture(target: com.melfegor.nice64.core.input.N64SemanticTargetV1) {
        if (controllerMappingEditor?.beginButtonCapture(target) == true) scheduleMappingCaptureTimeout()
    }

    fun beginMappingAxisCapture(target: com.melfegor.nice64.core.input.N64SemanticTargetV1) {
        if (controllerMappingEditor?.beginAxisCapture(target) == true) scheduleMappingCaptureTimeout()
    }

    fun beginMappingAnalogPairCapture() {
        if (controllerMappingEditor?.beginAnalogPairCapture() == true) scheduleMappingCaptureTimeout()
    }

    fun cancelMappingCapture() {
        mappingCaptureTimeout?.cancel()
        controllerMappingEditor?.cancelCapture()
    }

    fun applyMappingCandidate() = controllerMappingEditor?.applyCandidate()
    fun clearMappingTarget(target: com.melfegor.nice64.core.input.N64SemanticTargetV1) =
        controllerMappingEditor?.clearTarget(target)
    fun setMappingProfileName(name: String) = controllerMappingEditor?.setProfileName(name)
    fun toggleMappingInversion(target: com.melfegor.nice64.core.input.N64SemanticTargetV1) =
        controllerMappingEditor?.toggleInversion(target)
    fun setMappingDeadZone(target: com.melfegor.nice64.core.input.N64SemanticTargetV1, value: Float?) =
        controllerMappingEditor?.setDeadZone(target, value)
    fun setMappingScale(target: com.melfegor.nice64.core.input.N64SemanticTargetV1, value: Float) =
        controllerMappingEditor?.setScale(target, value)
    fun requestRestoreControllerMappings() = controllerMappingEditor?.requestRestoreDefaults()
    fun cancelRestoreControllerMappings() = controllerMappingEditor?.cancelRestoreDefaults()
    fun confirmRestoreControllerMappings() = controllerMappingEditor?.confirmRestoreDefaults()
    fun requestCloseMappingEditor() = controllerMappingEditor?.requestClose()
    fun continueMappingEditing() = controllerMappingEditor?.continueEditing()
    fun discardMappingEditor() = controllerMappingEditor?.discardAndClose()
    fun recoverMappingController(runtimeDeviceId: Int) =
        controllerMappingEditor?.recoverWithReconnectedController(runtimeDeviceId)

    fun saveControllerMappingProfile() {
        val editor = controllerMappingEditor ?: return
        viewModelScope.launch {
            if (editor.save()) {
                controllerProfileCoordinator?.refresh()
                refreshLiveControllerMapping()
            }
        }
    }

    fun beginPairing() {
        pendingCandidate = null
        scanAccepted.set(false)
        saveStarted.set(false)
        mutablePairingState.value = mutablePairingState.value.copy(
            screen = PairingScreen.SCANNER,
            confirmation = null,
            error = null,
            pendingUnpairProfileId = null,
        )
        updateSemanticPairing(PairingState.Scanning)
    }

    fun updateCameraPermission(granted: Boolean, permanentlyDenied: Boolean = false) {
        mutablePairingState.value = mutablePairingState.value.copy(
            permission = when {
                granted -> CameraPermissionState.GRANTED
                permanentlyDenied -> CameraPermissionState.PERMANENTLY_DENIED
                else -> CameraPermissionState.DENIED
            },
        )
    }

    fun acceptQr(raw: String) {
        if (mutablePairingState.value.screen != PairingScreen.SCANNER ||
            !scanAccepted.compareAndSet(false, true)
        ) return

        updateSemanticPairing(PairingState.Parsing)
        when (val parsed = BridgeOnboardingParser.parse(raw)) {
            is BridgeOnboardingParseResult.Failure -> showError(PairingPresentationError.INVALID_QR)
            is BridgeOnboardingParseResult.Success -> evaluateCandidate(parsed.candidate)
        }
    }

    fun confirmPairing() {
        val candidate = pendingCandidate ?: return
        val target = repository ?: return
        if (!saveStarted.compareAndSet(false, true)) return
        mutablePairingState.value = mutablePairingState.value.copy(screen = PairingScreen.SAVING)
        viewModelScope.launch {
            when (val result = target.store(candidate)) {
                is PairingStorageResult.Success -> {
                    pendingCandidate = null
                    updateSemanticPairing(PairingState.Stored(result.value))
                    refreshProfilesInternal()
                    mutablePairingState.value = mutablePairingState.value.copy(
                        screen = PairingScreen.PROFILES,
                        confirmation = null,
                        error = null,
                    )
                }
                is PairingStorageResult.Failure -> showError(PairingPresentationError.STORAGE_UNAVAILABLE)
            }
        }
    }

    fun cancelPairing() {
        pendingCandidate = null
        scanAccepted.set(false)
        saveStarted.set(false)
        mutablePairingState.value = mutablePairingState.value.copy(
            screen = PairingScreen.PROFILES,
            confirmation = null,
            error = null,
        )
        publishProfileSemanticState()
    }

    fun retryScan() = beginPairing()

    fun refreshBridge() {
        dashboardRepository?.let { target ->
            mutableDashboardState.value = mutableDashboardState.value.copy(
                connection = BridgeConnectionPresentation.CONNECTING,
                refreshing = true,
            )
            viewModelScope.launch {
                applyDashboard(target.refresh(mutableDashboardState.value))
            }
        }
        reconcileHistoryActivationOnResume()
    }

    fun selectMobilePrimary() {
        val coordinator = controllerPrimaryCoordinator ?: return
        val profileId = mutablePairingState.value.activeProfileId ?: return
        val sessionId = mutableDashboardState.value.currentSession.value?.sessionId ?: return
        if (activeUnpairWorker.get()) return
        if (!controllerPrimaryWorker.compareAndSet(false, true)) return
        viewModelScope.launch {
            try {
                val result = coordinator.selectMobile(
                    profileId, sessionId, ::publishControllerPrimaryIfCurrent,
                )
                publishControllerPrimaryIfCurrent(result)
                if (result.mobilePrimary) {
                    startControllerSender(result)
                }
                refreshDashboardAfterMutation(profileId)
            } finally {
                controllerPrimaryWorker.set(false)
            }
        }
    }

    fun selectBridgePrimary() {
        val coordinator = controllerPrimaryCoordinator ?: return
        val owned = mutableControllerPrimaryState.value
        if (activeUnpairWorker.get()) return
        if (!owned.mobilePrimary || !controllerPrimaryWorker.compareAndSet(false, true)) return
        controllerInputCoordinator?.setEnabled(false)
        controllerSenderCoordinator?.prepareCleanStop()
        viewModelScope.launch {
            try {
                controllerSenderCoordinator?.stop(
                    cleanCertain = true,
                    authorityStillUsable = ::controllerSenderAuthorityUsable,
                )
                publishControllerDataPlane(BridgeControllerDataPlanePresentation())
                val result = coordinator.selectBridge(owned, ::publishControllerPrimaryIfCurrent)
                publishControllerPrimaryIfCurrent(result)
                refreshDashboardAfterMutation(owned.profileId)
            } finally {
                controllerPrimaryWorker.set(false)
            }
        }
    }

    fun controllerPrimaryBackgrounded() {
        // Activity visibility is not controller authority. Clear only transient touch state;
        // physical-controller state and the explicit Bridge selection remain unchanged.
        controllerInputCoordinator?.clearTouch()
    }

    fun controllerPrimaryForegrounded() {
        // Activity visibility never selects Primary. A bounded read reconciles the existing
        // authoritative selection and restores a stopped sender through the accepted path.
        val dashboard = dashboardRepository ?: return
        if (!controllerForegroundReconciliationWorker.compareAndSet(false, true)) return
        viewModelScope.launch {
            try {
                applyDashboard(
                    dashboard.refresh(mutableDashboardState.value),
                    allowFailedSenderReplacement = true,
                )
            } finally {
                controllerForegroundReconciliationWorker.set(false)
            }
        }
    }

    fun selectFirmware(target: BridgeFirmwareTarget) {
        keyboardMouseCoordinator?.modeTransitionStarting()
        if (target != BridgeFirmwareTarget.CONTROLLER) {
            controllerInputCoordinator?.neutralize()
            controllerInputCoordinator?.setEnabled(false)
            controllerSenderCoordinator?.prepareCleanStop()
            viewModelScope.launch {
                controllerSenderCoordinator?.stop(
                    cleanCertain = true,
                    authorityStillUsable = ::controllerSenderAuthorityUsable,
                )
                publishControllerDataPlane(BridgeControllerDataPlanePresentation())
            }
        }
        submitMutation(BridgeMutationCommand.Firmware(target))
    }

    fun updateKeyboardMouseDevices(devices: List<AndroidKeyboardMouseDevice>) =
        keyboardMouseCoordinator?.updateDevices(devices)

    fun selectKeyboardSource(source: AndroidInputSource) =
        keyboardMouseCoordinator?.selectKeyboardSource(source)

    fun selectPointerSource(source: AndroidInputSource) =
        keyboardMouseCoordinator?.selectPointerSource(source)

    fun handleKeyboardMouseKeyEvent(event: KeyEvent): Boolean =
        keyboardMouseCoordinator?.physicalKey(event) == true

    fun handleKeyboardMouseMotionEvent(event: MotionEvent): Boolean =
        keyboardMouseCoordinator?.physicalPointer(event) == true

    fun keyboardMousePointerCaptureChanged(captured: Boolean) =
        keyboardMouseCoordinator?.pointerCaptureChanged(captured)

    fun setOnScreenKeyboardKey(key: Nice64LogicalKey, down: Boolean) =
        keyboardMouseCoordinator?.onScreenKey(key, down)

    fun addOnScreenPointerDelta(dx: Int, dy: Int) =
        keyboardMouseCoordinator?.onScreenPointer(dx, dy)

    fun setMouseInvertX(enabled: Boolean) =
        keyboardMouseCoordinator?.setPointerInvertX(enabled)

    fun setMouseInvertY(enabled: Boolean) =
        keyboardMouseCoordinator?.setPointerInvertY(enabled)

    fun setOnScreenPointerButton(left: Boolean?, right: Boolean?) =
        keyboardMouseCoordinator?.onScreenButton(left, right)

    fun refreshKeyboardLeds() = keyboardMouseCoordinator?.refreshLeds()

    fun keyboardMouseForegroundLost() = keyboardMouseCoordinator?.foregroundLost()

    fun keyboardMouseForegroundReturned() = keyboardMouseCoordinator?.foregroundReturned()

    fun selectAccessory(target: BridgeAccessoryTarget) =
        submitMutation(BridgeMutationCommand.Accessory(target))

    fun selectNetworkPakModule(module: BridgeModuleInfoDto) {
        BridgeMutationCommand.Module.fromAuthoritative(module)?.let(::submitMutation)
    }

    fun selectNetworkPakCarrier(target: BridgeCarrierTarget) =
        submitMutation(BridgeMutationCommand.Carrier(target))

    fun setTouchControllerButton(mask: UInt, pressed: Boolean) =
        controllerInputCoordinator?.setTouchButton(mask, pressed)

    fun setTouchControllerAnalog(x: Int, y: Int) =
        controllerInputCoordinator?.setTouchAnalog(x, y)

    fun clearTouchControllerInput() = controllerInputCoordinator?.clearTouch()

    fun selectMobileInputMode(mode: MobileInputMode) =
        controllerInputCoordinator?.setInputMode(mode)

    fun startControllerInputTrace() {
        controllerInputTrace?.let { trace ->
            viewModelScope.launch(Dispatchers.IO) { trace.start() }
        }
    }

    fun stopControllerInputTrace() {
        controllerInputTrace?.let { trace ->
            viewModelScope.launch(Dispatchers.IO) { trace.stop() }
        }
    }

    /**
     * Activity pre-dispatch hook used only while the selected management controller owns
     * mapping capture. This runs before Compose can translate controller D-pad keys into
     * focus navigation and deliberately leaves unrelated keyboard/device events alone.
     */
    internal fun handleControllerCaptureKeyEvent(event: KeyEvent): Boolean {
        val coordinator = controllerInputCoordinator ?: return false
        return when (val normalized = coordinator.normalizeKeyEvent(event)) {
            is AndroidPhysicalButtonPipelineResult.SuppressedAlias -> true
            is AndroidPhysicalButtonPipelineResult.Ready ->
                handleControllerCaptureKeyEvent(normalized.observation)
        }
    }

    private fun handleControllerCaptureKeyEvent(
        event: AndroidPhysicalButtonObservation,
    ): Boolean {
        val editor = controllerMappingEditor ?: return false
        val input = MappingCaptureKeyInput(
            deviceId = event.runtimeDeviceId,
            eligibleSource = event.eligible,
            down = event.pressed,
            up = event.released,
            repeatCount = event.repeatCount,
            keyCode = event.keyCode,
        )
        if (!editor.shouldPreDispatchKey(input)) return false
        val captured = editor.handleKey(input)
        if (captured && !editor.state.value.captureActive) mappingCaptureTimeout?.cancel()
        return captured
    }

    internal fun handleControllerKeyEvent(
        event: KeyEvent,
        boundary: AndroidInputDispatchBoundary? = null,
    ): Boolean {
        val trace = boundary?.let { controllerInputTrace?.beginEdge(event, it) }
        val coordinator = controllerInputCoordinator
        if (coordinator == null) {
            trace?.complete("controller-input-unavailable")
            return false
        }
        return when (val normalized = coordinator.normalizeKeyEvent(event)) {
            is AndroidPhysicalButtonPipelineResult.SuppressedAlias -> {
                trace?.normalized(normalized.observation)
                trace?.context(coordinator.inputMode(),
                    mutableControllerPrimaryState.value.mobilePrimary)
                trace?.complete("alias-suppressed", aliasSuppressed = true)
                true
            }
            is AndroidPhysicalButtonPipelineResult.Ready -> {
                trace?.normalized(normalized.observation)
                trace?.context(coordinator.inputMode(),
                    mutableControllerPrimaryState.value.mobilePrimary)
                if (handleControllerCaptureKeyEvent(normalized.observation)) {
                    trace?.complete("mapping-capture-consumed")
                    true
                } else {
                    val handled = preDispatchControllerGameplayKeyEvent(
                        normalized.observation,
                        trace,
                    )
                    if (trace?.isComplete() == false) {
                        trace.complete(if (handled) {
                            "handled-without-semantic-update"
                        } else {
                            "inactive-or-unmapped"
                        })
                    }
                    handled
                }
            }
        }
    }

    internal fun handleControllerCaptureMotionEvent(event: MotionEvent): Boolean {
        val values = event.device?.motionRanges?.associate { it.axis to event.getAxisValue(it.axis) }.orEmpty()
        val captured = controllerMappingEditor?.handleAxes(MappingCaptureAxisInput(
            event.deviceId,
            BridgeControllerHumanInputCoordinator.isGamepadSource(event.source),
            values,
        )) == true
        if (captured) {
            if (!controllerMappingEditor.state.value.captureActive) mappingCaptureTimeout?.cancel()
            return true
        }
        return false
    }

    internal fun preDispatchControllerGameplayKeyEvent(event: KeyEvent): Boolean =
        controllerInputCoordinator?.let { coordinator ->
            when (val normalized = coordinator.normalizeKeyEvent(event)) {
                is AndroidPhysicalButtonPipelineResult.SuppressedAlias -> true
                is AndroidPhysicalButtonPipelineResult.Ready ->
                    preDispatchControllerGameplayKeyEvent(normalized.observation)
            }
        } ?: false

    private fun preDispatchControllerGameplayKeyEvent(
        event: AndroidPhysicalButtonObservation,
        trace: ControllerInputTraceEdge? = null,
    ): Boolean = if (controllerGameplayContextActive()) {
        controllerInputCoordinator?.handleKeyEvent(event, trace) == true
    } else false

    internal fun preDispatchControllerGameplayMotionEvent(event: MotionEvent): Boolean =
        if (controllerGameplayContextActive()) {
            controllerInputCoordinator?.handleMotionEvent(event) == true
        } else false

    internal fun handleControllerMotionEvent(event: MotionEvent): Boolean =
        handleControllerCaptureMotionEvent(event) || preDispatchControllerGameplayMotionEvent(event)

    internal fun controllerGameplayContextActive(): Boolean = controllerGameplayContextActive(
        mobilePrimary = mutableControllerPrimaryState.value.mobilePrimary,
        dataPlaneActive = mutableControllerDataPlaneState.value.phase == BridgeControllerDataPlanePhase.ACTIVE,
        coordinatorEnabled = controllerInputCoordinator?.presentation?.value?.enabled == true,
        controllerMode = controllerInputCoordinator?.presentation?.value?.inputMode ==
            MobileInputMode.CONTROLLER,
    )

    internal fun usesControllerInput(candidate: BridgeControllerHumanInputCoordinator): Boolean =
        controllerInputCoordinator === candidate

    fun reconcileMutationOnResume() {
        val coordinator = mutationCoordinator ?: return
        val pending = mutableMutationState.value
        if (pending.phase !in setOf(
                BridgeMutationPhase.ACCEPTED,
                BridgeMutationPhase.RECONCILING,
                BridgeMutationPhase.UNCERTAIN,
            ) || !mutationWorker.compareAndSet(false, true)
        ) return
        viewModelScope.launch {
            try {
                val result = coordinator.resume(pending, ::publishMutationIfCurrent)
                publishMutationIfCurrent(result)
                refreshDashboardAfterMutation(result.profileId)
            } finally {
                mutationWorker.set(false)
            }
        }
    }

    fun activateHistory(
        category: BridgeHistoryCategory,
        item: BridgeHistoryItemDto,
        destructiveInstallConfirmed: Boolean,
    ) {
        val coordinator = historyActivationCoordinator ?: return
        val profileId = mutablePairingState.value.activeProfileId ?: return
        val sessionId = mutableDashboardState.value.currentSession.value?.sessionId ?: return
        val command = when (category) {
            BridgeHistoryCategory.CONTROLLER_PAK ->
                BridgeHistoryActivationCommand.controllerPak(sessionId, item)
            BridgeHistoryCategory.TRANSFER_PAK ->
                BridgeHistoryActivationCommand.transferPak(
                    sessionId, item, destructiveInstallConfirmed,
                ) ?: return
        }
        if (!historyActivationWorker.compareAndSet(false, true)) return
        viewModelScope.launch {
            try {
                val result = coordinator.submit(
                    profileId, command, ::publishHistoryActivationIfCurrent,
                )
                publishHistoryActivationIfCurrent(result)
                refreshDashboardAfterMutation(profileId)
            } finally {
                historyActivationWorker.set(false)
            }
        }
    }

    fun reconcileHistoryActivationOnResume() {
        val coordinator = historyActivationCoordinator ?: return
        val pending = mutableHistoryActivationState.value
        if (pending.phase !in setOf(
                BridgeHistoryActivationPhase.ACCEPTED,
                BridgeHistoryActivationPhase.RECONCILING,
                BridgeHistoryActivationPhase.UNCERTAIN,
            ) || !historyActivationWorker.compareAndSet(false, true)
        ) return
        viewModelScope.launch {
            try {
                val result = coordinator.resume(pending, ::publishHistoryActivationIfCurrent)
                publishHistoryActivationIfCurrent(result)
                refreshDashboardAfterMutation(result.profileId)
            } finally {
                historyActivationWorker.set(false)
            }
        }
    }

    fun selectProfile(profileId: String) {
        val target = repository ?: return
        clearControllerPrimary()
        mutableMutationState.value = BridgeMutationPresentation()
        mutableHistoryActivationState.value = BridgeHistoryActivationPresentation()
        viewModelScope.launch {
            when (target.selectActive(profileId)) {
                is PairingStorageResult.Success -> refreshProfilesInternal()
                is PairingStorageResult.Failure -> showError(PairingPresentationError.PROFILE_OPERATION_FAILED)
            }
        }
    }

    fun requestUnpair(profileId: String) {
        mutablePairingState.value = mutablePairingState.value.copy(pendingUnpairProfileId = profileId)
    }

    fun cancelUnpair() {
        mutablePairingState.value = mutablePairingState.value.copy(pendingUnpairProfileId = null)
    }

    fun confirmUnpair() {
        val profileId = mutablePairingState.value.pendingUnpairProfileId ?: return
        val target = repository ?: return
        mutablePairingState.value = mutablePairingState.value.copy(pendingUnpairProfileId = null)
        val owned = mutableControllerPrimaryState.value
        val removesActiveControllerContext =
            mutablePairingState.value.activeProfileId == profileId &&
                owned.profileId == profileId && owned.mobilePrimary
        if (removesActiveControllerContext && !activeUnpairWorker.compareAndSet(false, true)) return
        viewModelScope.launch {
            try {
                val result = if (removesActiveControllerContext) {
                    BridgeControllerLifecycleConvergence.removePairingAuthority(
                        owned.mobilePrimary,
                        controllerLifecycleActions(owned),
                    ) { target.delete(profileId) }
                } else {
                    target.delete(profileId)
                }
                when (result) {
                    is PairingStorageResult.Success -> refreshProfilesInternal()
                    is PairingStorageResult.Failure ->
                        showError(PairingPresentationError.PROFILE_OPERATION_FAILED)
                }
            } finally {
                if (removesActiveControllerContext) activeUnpairWorker.set(false)
            }
        }
    }

    private fun refreshProfiles() {
        viewModelScope.launch { refreshProfilesInternal() }
    }

    private suspend fun refreshProfilesInternal() {
        val target = repository ?: return
        val previousActiveProfileId = mutablePairingState.value.activeProfileId
        val profiles = target.list()
        val active = target.activeProfileId()
        if (profiles !is PairingStorageResult.Success || active !is PairingStorageResult.Success) {
            showError(PairingPresentationError.STORAGE_UNAVAILABLE)
            return
        }
        mutablePairingState.value = mutablePairingState.value.copy(
            profiles = profiles.value,
            activeProfileId = active.value,
            error = null,
        )
        if (previousActiveProfileId != active.value) {
            mutableMutationState.value = BridgeMutationPresentation()
            mutableHistoryActivationState.value = BridgeHistoryActivationPresentation()
            clearControllerPrimary()
        }
        publishProfileSemanticState()
        refreshBridge()
    }

    private fun evaluateCandidate(candidate: BridgeOnboardingCandidate) {
        val profiles = mutablePairingState.value.profiles
        if (profiles.any { it.sameIdentity(candidate) }) {
            pendingCandidate = null
            showError(PairingPresentationError.ALREADY_PAIRED)
            return
        }
        val identityChanged = profiles.any { it.apiBaseUrl == candidate.canonicalApiUrl }
        pendingCandidate = candidate
        mutablePairingState.value = mutablePairingState.value.copy(
            screen = PairingScreen.CONFIRMATION,
            confirmation = candidate.toSafeConfirmation(identityChanged),
            error = null,
        )
    }

    private fun showError(error: PairingPresentationError) {
        mutablePairingState.value = mutablePairingState.value.copy(
            screen = PairingScreen.ERROR,
            confirmation = null,
            error = error,
        )
        updateSemanticPairing(PairingState.Error(error.safeMessage))
    }

    private fun publishProfileSemanticState() {
        val current = mutablePairingState.value
        val active = current.profiles.firstOrNull { it.profileId == current.activeProfileId }
        val fallback = active ?: current.profiles.firstOrNull()
        updateSemanticPairing(fallback?.let(PairingState::Paired) ?: PairingState.Unpaired)
    }

    private fun updateSemanticPairing(pairing: PairingState) {
        mutableState.value = mutableState.value.copy(pairing = pairing)
    }

    private fun submitMutation(command: BridgeMutationCommand) {
        val coordinator = mutationCoordinator ?: return
        val profileId = mutablePairingState.value.activeProfileId ?: return
        if (!mutationWorker.compareAndSet(false, true)) return
        viewModelScope.launch {
            try {
                val result = coordinator.submit(profileId, command, ::publishMutationIfCurrent)
                publishMutationIfCurrent(result)
                refreshDashboardAfterMutation(profileId)
            } finally {
                mutationWorker.set(false)
            }
        }
    }

    private fun publishMutationIfCurrent(value: BridgeMutationPresentation) {
        if (shouldPublishMutation(mutablePairingState.value.activeProfileId, value))
            mutableMutationState.value = value
    }

    private fun publishHistoryActivationIfCurrent(value: BridgeHistoryActivationPresentation) {
        if (shouldPublishHistoryActivation(mutablePairingState.value.activeProfileId, value))
            mutableHistoryActivationState.value = value
    }

    private fun publishControllerPrimaryIfCurrent(
        value: BridgeControllerPrimaryPresentation,
    ) {
        if (activeUnpairWorker.get()) return
        if (shouldPublishControllerPrimary(mutablePairingState.value.activeProfileId, value)) {
            mutableControllerPrimaryState.value = value
            if (!value.mobilePrimary) {
                controllerInputCoordinator?.setEnabled(false)
                controllerSenderCoordinator?.requestImmediateStop()
                publishControllerDataPlane(BridgeControllerDataPlanePresentation())
                viewModelScope.launch {
                    controllerSenderCoordinator?.stop(
                        cleanCertain = false,
                        authorityStillUsable = ::controllerSenderAuthorityUsable,
                    )
                }
            }
        }
    }

    private suspend fun startControllerSender(
        primary: BridgeControllerPrimaryPresentation,
    ) {
        if (!controllerSenderStartWorker.compareAndSet(false, true)) return
        try {
            val sender = controllerSenderCoordinator ?: return
            val endpoint = primary.endpoint
            if (endpoint == null) {
                publishControllerDataPlane(BridgeControllerDataPlanePresentation(
                    com.melfegor.nice64.api.BridgeControllerDataPlanePhase.FAILED,
                    safeMessage = "The authoritative controller endpoint is unavailable.",
                ))
                return
            }
            publishControllerDataPlane(sender.start(
                primary,
                endpoint,
                ::controllerSenderAuthorityUsable,
                ::publishControllerDataPlane,
            ))
        } finally {
            controllerSenderStartWorker.set(false)
        }
    }

    private fun controllerSenderAuthorityUsable(
        expected: BridgeControllerSenderAuthority,
    ): Boolean = mutableControllerPrimaryState.value
        .senderAuthorityAt(SystemClock.elapsedRealtime()) == expected

    private fun controllerNetworkChanged() {
        val sender = controllerSenderCoordinator ?: return
        if (sender.currentNetworkStillMatches()) return
        controllerInputCoordinator?.setEnabled(false)
        sender.requestImmediateStop()
        viewModelScope.launch {
            sender.stop(false, ::controllerSenderAuthorityUsable)
            publishControllerDataPlane(BridgeControllerDataPlanePresentation())
            mutableControllerPrimaryState.value = mutableControllerPrimaryState.value.copy(
                failure = com.melfegor.nice64.api.BridgeControllerPrimaryFailure(
                    BridgeControllerPrimaryFailureKind.UNCERTAIN,
                    "The active Wi-Fi network changed. NICE Mobile remains primary; reconnect or select Bridge Primary manually.",
                ),
            )
        }
    }

    private fun invalidateControllerPrimary(
        kind: BridgeControllerPrimaryFailureKind,
        message: String,
    ) {
        val current = mutableControllerPrimaryState.value
        if (!current.mobilePrimary) return
        val invalidated = current.copy(failure =
            com.melfegor.nice64.api.BridgeControllerPrimaryFailure(kind, message))
        BridgeControllerLifecycleConvergence.invalidate(
            controllerLifecycleActions(current) {
                mutableControllerPrimaryState.value = invalidated
            },
        )
        viewModelScope.launch {
            controllerSenderCoordinator?.stop(
                cleanCertain = false,
                authorityStillUsable = ::controllerSenderAuthorityUsable,
            )
        }
    }

    private fun clearControllerPrimary() {
        BridgeControllerLifecycleConvergence.invalidate(
            controllerLifecycleActions(mutableControllerPrimaryState.value),
        )
        viewModelScope.launch {
            controllerSenderCoordinator?.stop(
                cleanCertain = false,
                authorityStillUsable = ::controllerSenderAuthorityUsable,
            )
        }
    }

    private fun controllerLifecycleActions(
        owned: BridgeControllerPrimaryPresentation,
        clearLocalAuthority: () -> Unit = {
            mutableControllerPrimaryState.value = BridgeControllerPrimaryPresentation()
        },
    ) = object : BridgeControllerLifecycleActions {
        override fun neutralizeInput() {
            controllerInputCoordinator?.neutralize()
        }

        override fun prepareCleanSenderStop() {
            controllerSenderCoordinator?.prepareCleanStop()
        }

        override fun requestImmediateSenderStop() {
            controllerSenderCoordinator?.requestImmediateStop()
        }

        override suspend fun stopSender(cleanCertain: Boolean) {
            controllerSenderCoordinator?.stop(
                cleanCertain = cleanCertain,
                authorityStillUsable = ::controllerSenderAuthorityUsable,
            )
            publishControllerDataPlane(BridgeControllerDataPlanePresentation())
        }

        override suspend fun selectBridgePrimary() {
            val coordinator = controllerPrimaryCoordinator ?: return
            mutableControllerPrimaryState.value = coordinator.selectBridge(owned)
        }

        override fun clearLocalAuthority() {
            publishControllerDataPlane(BridgeControllerDataPlanePresentation())
            clearLocalAuthority.invoke()
        }
    }

    override fun onCleared() {
        keyboardMouseCoordinator?.close()
        mappingCaptureTimeout?.cancel()
        controllerMappingEditor?.discardAndClose()
        controllerInputCoordinator?.close()
        controllerSenderCoordinator?.close()
        controllerInputTrace?.close()
        super.onCleared()
    }

    private fun scheduleMappingCaptureTimeout() {
        mappingCaptureTimeout?.cancel()
        mappingCaptureTimeout = viewModelScope.launch {
            delay(12_000)
            controllerMappingEditor?.timeoutCapture()
        }
    }

    private suspend fun refreshLiveControllerMapping() {
        liveControllerProfiles?.refresh()
        controllerInputCoordinator?.reconcileLiveMapping()
    }

    private fun applyDashboard(
        value: BridgeDashboardSnapshot,
        allowFailedSenderReplacement: Boolean = false,
    ) {
        mutableDashboardState.value = value
        keyboardMouseCoordinator?.updateBridgeState(
            value.state.value,
            value.profileId,
            value.currentSession.value?.sessionId,
            allowFailedSenderReplacement,
        )
        val sessionId = value.currentSession.value?.sessionId
        val authoritative = value.controller.value
        if (sessionId != null && authoritative?.sessionId == sessionId) {
            val current = mutableControllerPrimaryState.value
            if (authoritative.primary == "nice-mobile") {
                val reconciled = current.copy(
                    phase = BridgeControllerPrimaryPhase.MOBILE_PRIMARY,
                    profileId = value.profileId,
                    sessionId = sessionId,
                    endpoint = authoritative.endpoint,
                    failure = null,
                )
                mutableControllerPrimaryState.value = reconciled
                if (shouldStartReconciledControllerSender(
                        current, authoritative, mutableControllerDataPlaneState.value,
                        allowFailedSenderReplacement,
                    )
                ) {
                    viewModelScope.launch { startControllerSender(reconciled) }
                }
            } else if (current.mobilePrimary) {
                controllerInputCoordinator?.setEnabled(false)
                controllerSenderCoordinator?.requestImmediateStop()
                mutableControllerPrimaryState.value = BridgeControllerPrimaryPresentation(
                    profileId = value.profileId,
                    sessionId = sessionId,
                )
                viewModelScope.launch {
                    controllerSenderCoordinator?.stop(false) { false }
                    publishControllerDataPlane(BridgeControllerDataPlanePresentation())
                }
            }
        }
    }

    private suspend fun refreshDashboardAfterMutation(profileId: String?) {
        val dashboard = dashboardRepository ?: return
        if (profileId == null || mutablePairingState.value.activeProfileId != profileId) return
        applyDashboard(dashboard.refresh(mutableDashboardState.value))
    }

    private fun publishControllerDataPlane(value: BridgeControllerDataPlanePresentation) {
        val presented = if (value.phase == BridgeControllerDataPlanePhase.ACTIVE) {
            value.copy(neutralOnly = false, safeMessage = "Controller stream active")
        } else value
        mutableControllerDataPlaneState.value = presented
        controllerInputCoordinator?.setEnabled(
            presented.phase == BridgeControllerDataPlanePhase.ACTIVE &&
                mutableControllerPrimaryState.value.mobilePrimary,
        )
    }
}

internal inline fun dispatchNormalizedControllerButtonWithPrecedence(
    observation: AndroidPhysicalButtonObservation,
    capture: (AndroidPhysicalButtonObservation) -> Boolean,
    gameplay: (AndroidPhysicalButtonObservation) -> Boolean,
): Boolean {
    if (capture(observation)) return true
    return gameplay(observation)
}

internal fun controllerGameplayContextActive(
    mobilePrimary: Boolean,
    dataPlaneActive: Boolean,
    coordinatorEnabled: Boolean,
    controllerMode: Boolean = true,
): Boolean = mobilePrimary && dataPlaneActive && coordinatorEnabled && controllerMode

private fun BridgePairingMetadata.sameIdentity(candidate: BridgeOnboardingCandidate): Boolean =
    spkiSha256 == candidate.spkiSha256 && certificateSha256 == candidate.certificateSha256
