namespace PicoSwitchController;

using DirectShowLib;
using SharpDX.DirectInput;
using SharpDX.XInput;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO.Ports;
using System.Text;

public partial class Form1 : Form
{
    private const int EightBitDoParsecAxisThreshold = 8000;
    private const string TransferPakInstallWarning =
        "This writes the selected GB/GBC ROM into the Pico flash slot used by " +
        "Transfer Pak mode. Keep the N64 powered off while installing.";

    private sealed class FormWifiCommandTransport(Form1 owner) : IWifiCommandTransport
    {
        public bool IsConnected => owner._serialPort?.IsOpen == true;

        public Task<string> SendAsync(string command, CancellationToken cancellationToken) =>
            owner.SendWifiCommandAsync(command, cancellationToken);
    }

    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "picoswitchcontroller.ini");
    private readonly CancellationTokenSource _inputCancellation = new();
    private readonly object _joystickLock = new();
    private readonly object _maskLock = new();
    private readonly object _serialLock = new();
    private readonly object _controllerLineGuardLock = new();
    private readonly object _debugLogLock = new();
    private readonly AutoResetEvent _serialSignal = new(false);
    private readonly AutoResetEvent _debugLogSignal = new(false);
    private readonly CancellationTokenSource _serialCancellation = new();
    private readonly CancellationTokenSource _debugLogCancellation = new();
    private readonly BridgeControllerCommandQueue _serialCommands = new();
    private readonly ConcurrentQueue<string> _debugLogQueue = new();
    private readonly System.Windows.Forms.Timer _rumblePollTimer = new();
    private readonly System.Windows.Forms.Timer _serialReconnectTimer = new();
    private readonly System.Windows.Forms.Timer _persistencePollTimer = new();
    private readonly NetworkPakBridgeService _networkPakBridge;
    private readonly IBridgeNetworkPakModuleService _networkPakModuleService;
    private readonly NetworkPakApplicationDispatcher _networkPakApplicationDispatcher;
    private readonly IBridgeNetworkPakCarrierService _networkPakCarrierService;
    private readonly IBridgeNetworkPakCarrierSelectionStore
        _networkPakCarrierSelectionStore;
    private readonly IBridgeWifiProvisioningService _wifiProvisioningService;
    private readonly FirmwareModeStateMachine _firmwareModeState = new();
    private readonly BridgeDeviceStateService _bridgeDeviceState = new();
    private readonly BridgeOperationCoordinator _operationCoordinator = new();
    private readonly BridgeInstanceIdentity _bridgeInstance = new();
    private readonly BridgeSessionService _sessionService;
    private readonly AccessoryPersistenceService _accessoryPersistence;
    private readonly IFirmwareModeControlPath _serialFirmwareControlPath;
    private readonly IFirmwareModeControlPath _tcpFirmwareControlPath;
    private readonly BridgeConnectionService _connectionService;
    private readonly IBridgeFirmwareService _firmwareService;
    private readonly BridgeAccessoryService _accessoryService;
    private readonly BridgeControllerService _controllerService;
    private readonly IBridgeControllerEndpointProvider _controllerEndpointProvider;
    private readonly BridgeControllerPrimaryService _controllerPrimaryService;
    private readonly IBridgeControllerPakContentService _controllerPakContentService;
    private readonly IBridgeTransferPakContentService _transferPakContentService;
    private readonly IBridgeTransferPakLegacyContentService _transferPakLegacyContentService;
    private readonly IBridgeHistoryService _historyService;
    private readonly BridgeHistoryActivationService _historyActivationService;
    private readonly IBridgeStateProvider _bridgeState;
    private readonly BridgeKeyboardMouseStateFacade _keyboardMouseStateFacade;
    private readonly KeyboardMouseInputCoordinator _keyboardMouseInput;
    private readonly WindowsRawInput _rawInput = new();
    private readonly BridgeDesktopShutdownCoordinator _shutdownCoordinator;
    private IBridgeApiAccessLifecycle? _apiAccessLifecycle;
    private IBridgeApiOnboardingProvider? _apiOnboardingProvider;
    private IBridgeApiLocalCredentialProvider? _apiLocalCredentials;

    private readonly BridgeSettings _settings;
    private readonly List<ToolStripMenuItem> _networkPakModuleMenuItems = [];
    private readonly long[] _gamepadReleaseTicks = new long[32];
    private readonly string _debugLogPath = Path.Combine(AppContext.BaseDirectory, "picoswitchcontroller-debug.log");

    private ToolStripMenuItem? _noAccessoryModeMenuItem;
    private ToolStripMenuItem? _memoryPakModeMenuItem;
    private ToolStripMenuItem? _rumblePakModeMenuItem;
    private ToolStripMenuItem? _transferPakModeMenuItem;
    private ToolStripMenuItem? _networkPakModeMenuItem;
    private ToolStripMenuItem? _accessoryKeyboardMenuItem;
    private ToolStripMenuItem? _accessoryMouseMenuItem;
    private ToolStripMenuItem? _pcRumblePassthroughMenuItem;
    private ToolStripMenuItem? _inputNoneMenuItem;
    private ToolStripMenuItem? _inputAutoMenuItem;
    private ToolStripMenuItem? _inputXInputMenuItem;
    private ToolStripMenuItem? _inputDirectInputMenuItem;
    private ToolStripMenuItem? _inputEightBitDoParsecMenuItem;
    private ToolStripMenuItem? _debugLoggingMenuItem;
    private ToolStripMenuItem? _settingsDebugModeMenuItem;
    private ToolStripMenuItem? _settingsOnScreenControlsMenuItem;
    private ToolStripMenuItem? _settingsAskToNameSessionMenuItem;
    private ToolStripMenuItem? _settingsFirmwareControllerMenuItem;
    private ToolStripMenuItem? _settingsFirmwareDiagnosticsMenuItem;
    private ToolStripMenuItem? _settingsFirmwareNetworkMenuItem;
    private ToolStripMenuItem? _settingsFirmwareKeyboardMenuItem;
    private ToolStripMenuItem? _settingsFirmwareMouseMenuItem;
    private ToolStripMenuItem? _keyboardSourceMenuItem;
    private ToolStripMenuItem? _pointerSourceMenuItem;
    private ToolStripMenuItem? _settingsBridgePrimaryMenuItem;
    private ToolStripMenuItem? _settingsMobilePrimaryMenuItem;
    private ToolStripMenuItem? _networkPakUsbTransportMenuItem;
    private ToolStripMenuItem? _networkPakTcpTransportMenuItem;
    private ToolStripMenuItem? _networkPakQuickTransportMenuItem;
    private ToolStripMenuItem? _historyMenuItem;
    private ToolStripMenuItem? _controllerPakHistoryMenuItem;
    private ToolStripMenuItem? _transferPakHistoryMenuItem;
    private ControllerProfile _profile = ControllerProfile.Create(ConsoleControllerType.Snes);
    private ControllerMapping _mapping = ControllerMapping.CreateDefault(ConsoleControllerType.Snes);
    private ControllerMapping _eightBitDoParsecMapping = ControllerMapping.CreateEightBitDoParsecN64();
    private InputSourceMode _inputSourceMode = InputSourceMode.Auto;
    private DirectInput? _directInput;
    private Joystick? _joystick;
    private Controller? _xinputController;
    private SerialPort? _serialPort;
    private SerialBridgeTransport? _serialBridgeTransport;
    private readonly BridgeTransportSessionOwner<TcpBridgeTransport> _tcpSession = new();
    private NetworkPakRudpTransport? _networkPakRudpTransport;
    private Action<Exception>? _networkPakRudpFaultHandler;
    private long _networkPakRudpGeneration;
    private readonly object _latestStateUdpLock = new();
    private BridgeLatestStateUdpControllerSender? _latestStateUdpSender;
    private long _networkPakLineActivationGeneration;
    private BridgeNetworkPakTransport _networkPakLineActivationCarrier;
    private BridgeNetworkPakTransport _networkPakPresentationCarrier;
    private BridgeConnectionKind _connectionKind = BridgeConnectionKind.Usb;
    private string _networkAddress = string.Empty;
    private int _networkPort = Nice64NetworkEndpoint.DefaultPort;
    private DirectShowPreview? _videoPreview;
    private DirectShowAudioPassthrough? _audioPassthrough;
    private Form? _fullScreenVideoForm;
    private Panel? _fullScreenVideoPanel;
    private Task? _inputTask;
    private Task? _serialTask;
    private Task? _debugLogTask;
    private bool _controllerPakOperationInProgress;
    private bool _controllerContentOperationInProgress;
    private int _controllerLineGuardDepth;
    private bool _rumbleActive;
    private bool _rumblePollInProgress;
    private bool _persistencePollInProgress;
    private bool _debugLoggingEnabled;
    private bool _n64AutoInputBlockedStatusShown;
    private string _lastDirectInputRawSignature = "";
    private long _lastControllerCommandTicks;
    private long _rumbleOffAfterTicks;
    private uint _gamepadMask;
    private uint _manualMask;
    private Image? _n64ControllerArt;
    private Image? _snesControllerArt;
    private Button? _openDebugLogButton;
    private NetworkPakBridgeForm? _networkPakBridgeForm;
    private string _serialReceiveBuffer = string.Empty;
    private bool _formClosing;
    private bool _firmwareModeSwitchInProgress;
    private bool _inputCaptureTransitionInProgress;
    private bool _debugModeEnabled;
    private bool _onScreenControlsEnabled = true;
    private bool _askToNameSessionWhenConnecting = true;
    private bool _initializingUi;
    private FirmwarePersonality _activeFirmwareMode = FirmwarePersonality.Unknown;
    private GroupBox? _firmwareModeGroupBox;
    private Label? _firmwareModeValueLabel;
    private Button? _firmwareControllerButton;
    private Button? _firmwareDiagnosticsButton;
    private Button? _firmwareNetworkButton;
    private Button? _firmwareKeyboardButton;
    private Button? _firmwareMouseButton;
    private GroupBox? _inputCaptureGroupBox;
    private Label? _inputCaptureValueLabel;
    private Button? _inputCaptureButton;
    private readonly KeyboardCaptureChordFilter _captureChordFilter = new();
    private HashSet<string> _knownRawDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private string _directInputDeviceId = "";
    private string _directInputDeviceName = "";
    private string _xinputDeviceId = "";
    private string _xinputDeviceName = "";
    private AnalogStickCalibration? _activeAnalogCalibration;
    private string _activeAnalogCalibrationKey = "";
    private bool _gamepadAnalogActive;
    private sbyte _gamepadAnalogX;
    private sbyte _gamepadAnalogY;

    private bool IsN64NoAccessoryBaseline => false;

    public Form1()
    {
        _shutdownCoordinator = new(
            ShutdownAsync,
            RequestFinalClose,
            ex => LogDebug($"SHUTDOWN UNEXPECTED failure={ex.GetType().Name}: " +
                ex.Message, force: true));
        _accessoryPersistence = new(log: message => LogDebug(message, force: true));
        _controllerService = new BridgeControllerService(
            new DelegateBridgeControllerCommandPort(
                GetControllerPortAvailability,
                QueueLatestControllerCommandAsync,
                DeliverControllerCommandImmediatelyAsync,
                ClearPendingControllerCommandsFromQueue));
        _sessionService = new BridgeSessionService(_bridgeDeviceState);
        _connectionService = new BridgeConnectionService(
            _bridgeDeviceState,
            _accessoryPersistence,
            new DelegateSerialConnectionAdapter(
                OpenSerialControlSessionAsync,
                ReplaceSerialControlSessionAsync),
            new DelegateTcpConnectionAdapter(
                OpenTcpControlSessionAsync,
                PrepareTcpControlSessionReplacementAsync,
                ReplaceTcpControlSessionAttemptAsync,
                EndTcpControlSessionReplacementWithoutSession),
            new DelegateDisconnectCleanupAdapter(PerformDisconnectCleanupAsync),
            _sessionService);
        IBridgeFirmwareConnectionReplacement connectionReplacement = _connectionService;
        _serialFirmwareControlPath = new SerialFirmwareModeControlPath(
            () => _serialPort?.IsOpen == true,
            token => Task.Run(() => QuerySerialFirmwareMode(token), token),
            (target, token) => Task.Run(() => SendFirmwareModeRequest(target, token), token),
            connectionReplacement.ReplaceSerialControlSessionAsync,
            message => LogDebug(message, force: true));
        _tcpFirmwareControlPath = new TcpFirmwareModeControlPath(
            () => _tcpSession.Current?.IsConnected == true,
            token => Task.Run(() => QueryTcpFirmwareMode(token), token),
            (target, token) => Task.Run(() => SendTcpFirmwareModeRequest(target, token), token),
            connectionReplacement.PrepareTcpFirmwareReplacementAsync,
            connectionReplacement.ReplaceTcpControlSessionAsync,
            connectionReplacement.EndTcpFirmwareReplacementWithoutSession,
            message => LogDebug(message, force: true));
        _firmwareService = new BridgeFirmwareService(
            _bridgeDeviceState, _accessoryPersistence, SelectFirmwareControlPath);
        _settings = BridgeSettings.Load(_settingsPath);
        _keyboardMouseStateFacade = new BridgeKeyboardMouseStateFacade(
            _serialLock, ActiveLineTransportLocked);
        _keyboardMouseInput = new KeyboardMouseInputCoordinator(
            _keyboardMouseStateFacade,
            _settings.KeyboardInputDeviceId,
            _settings.PointerInputDeviceId,
            message => LogDebug(message, force: true),
            invertX: _settings.MouseInvertX,
            invertY: _settings.MouseInvertY);
        _rawInput.KeyboardInput += RawInput_KeyboardInput;
        _rawInput.PointerInput += _keyboardMouseInput.ApplyPointer;
        _rawInput.DevicesChanged += RawInput_DevicesChanged;
        _bridgeInstance.SetName(_settings.BridgeInstanceName);
        _debugModeEnabled = _settings.DebugMode;
        _onScreenControlsEnabled = _settings.EnableOnScreenControls;
        _askToNameSessionWhenConnecting =
            _settings.AskToNameNice64SessionWhenConnecting;
        _networkPakBridge = new NetworkPakBridgeService(
            _settings.ActiveNetworkPakModuleId,
            _settings.ReadableSourceMode,
            _settings.SearchProviderName,
            _settings.SearchProviderUrl,
            _bridgeDeviceState,
            _sessionService);
        _networkPakModuleService = new BridgeNetworkPakModuleService(
            _networkPakBridge,
            new BridgeSettingsNetworkPakModuleSelectionStore(_settingsPath));
        _networkPakApplicationDispatcher = new NetworkPakApplicationDispatcher(
            _networkPakBridge);
        _networkPakCarrierSelectionStore =
            new BridgeNetworkPakCarrierSelectionStoreAdapter(_settingsPath);
        _networkPakCarrierService = new BridgeNetworkPakCarrierService(
            new BridgeNetworkPakCarrierStatePort(_bridgeDeviceState),
            new DelegateBridgeNetworkPakCarrierActivationContext(() =>
                ActiveAccessory == BridgeAccessoryMode.NetworkPak && IsBridgeConnected),
            _networkPakCarrierSelectionStore,
            new DelegateBridgeNetworkPakLineCarrierPort(
                IsNetworkPakLineCarrierAvailable,
                BeginNetworkPakLineCarrierActivationAsync,
                ReleaseNetworkPakLineCarrierAsync),
            new DelegateBridgeNetworkPakQuickCarrierPort(
                () => true,
                StartNetworkPakQuickCarrierAsync,
                StopNetworkPakQuickCarrierAsync),
            new DelegateBridgeNetworkPakApplicationOwnerPort(
                _networkPakBridge.SetPicoConnected,
                _networkPakBridge.SetHandshakeComplete),
            new DelegateBridgeNetworkPakCarrierDelay((delay, token) =>
                new ValueTask(Task.Delay(delay, token))));
        BridgeNetworkPakCarrierResult carrierInitialization =
            _networkPakCarrierService.InitializeAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
        if (!carrierInitialization.Success)
        {
            LogDebug($"NETPAK CARRIER initialization failed=" +
                $"{carrierInitialization.Failure} " +
                $"{carrierInitialization.Diagnostic}", force: true);
        }
        if (BridgeNetworkPakCarrierTransitionPolicy.IsSelectable(
                carrierInitialization.Snapshot.SelectedCarrier))
        {
            _networkPakPresentationCarrier =
                carrierInitialization.Snapshot.SelectedCarrier;
        }
        _wifiProvisioningService = new BridgeCoordinatedWifiProvisioningService(
            new BridgeWifiProvisioningService(
                new WifiProvisioningSession(new FormWifiCommandTransport(this))),
            _operationCoordinator);
        _controllerEndpointProvider = new BridgeControllerEndpointProvider(
            _sessionService,
            _bridgeDeviceState,
            new DelegateBridgeControllerTcpEndpointAuthority(
                GetVerifiedControllerTcpIpv4),
            new DelegateBridgeControllerUsbWifiStatusAuthority(
                ReadFreshControllerUsbWifiStatusAsync),
            new DelegateBridgeControllerEndpointProtectionProvider(
                CurrentControllerEndpointProtection));
        _controllerPrimaryService = new BridgeControllerPrimaryService(
            _sessionService,
            _bridgeDeviceState,
            _controllerEndpointProvider,
            new DelegateBridgeControllerEndpointProtectionProvider(
                CurrentControllerEndpointProtection),
            new DelegateBridgeLocalControllerPrimaryPort(
                SuspendLocalControllerForMobilePrimaryAsync,
                ResumeLocalControllerForBridgePrimaryAsync));
        _controllerPrimaryService.CurrentChanged += ControllerPrimary_CurrentChanged;
        _accessoryService = new BridgeAccessoryService(
            _bridgeDeviceState,
            _accessoryPersistence,
            _firmwareService,
            new DelegateControllerAccessoryCommandPort(
                () => _profile.Type == ConsoleControllerType.N64 && IsBridgeConnected,
                SendControllerAccessoryCommandAsync),
            new DelegateNetworkPakAccessoryPort(
                ActivateSelectedNetworkPakCarrierAsync,
                ReleaseSelectedNetworkPakCarrierAsync));
        _historyService = new BridgeHistoryService();
        BridgeControllerPakContentService controllerPakContentService = new(
            new BridgeControllerPakFileSystem(),
            new DelegateBridgeControllerPakBulkPort(
                () => IsBridgeConnected,
                UploadControllerPakContentAsync,
                DownloadControllerPakContentAsync,
                QueryControllerPakWriteCounterAsync),
            new DelegateBridgeControllerPakAccessoryPort(() =>
                _accessoryService.AdoptVerifiedAccessory(BridgeAccessoryMode.ControllerPak)),
            new DelegateBridgeControllerPakInputGuard(
                () => EnterControllerLineGuard("controller-pak-content"),
                token => ExitControllerLineGuardAsync(
                    "controller-pak-content", token)),
            _accessoryPersistence);
        BridgeTransferPakContentService transferPakContentService = new(
            new BridgeTransferPakFileSystem(),
            new DelegateBridgeTransferPakBulkPort(
                () => IsBridgeConnected,
                QueryTransferPakFlashCapacityAsync,
                InstallFullTransferPakContentAsync,
                InstallLegacyTransferPakContentAsync,
                DownloadTransferPakSaveContentAsync,
                QueryControllerPakWriteCounterAsync),
            new DelegateBridgeTransferPakAccessoryPort(() =>
                _accessoryService.AdoptVerifiedAccessory(BridgeAccessoryMode.TransferPak)),
            new DelegateBridgeTransferPakInputGuard(
                () => EnterControllerLineGuard("transfer-pak-content"),
                token => ExitControllerLineGuardAsync(
                    "transfer-pak-content", token)),
            _accessoryPersistence,
            message => LogDebug(message, force: true));
        _controllerPakContentService =
            new BridgeHistoryRecordingControllerPakContentService(
                controllerPakContentService, _historyService, LogHistoryRecording);
        _transferPakContentService =
            new BridgeHistoryRecordingTransferPakContentService(
                transferPakContentService, _historyService, LogHistoryRecording);
        _transferPakLegacyContentService = transferPakContentService;
        _historyActivationService = new BridgeHistoryActivationService(
            _historyService, _bridgeDeviceState, _accessoryService,
            controllerPakContentService, transferPakContentService,
            _operationCoordinator);
        _bridgeState = new BridgeStateAggregator(
            _bridgeDeviceState,
            _controllerService,
            _controllerPakContentService,
            _transferPakContentService,
            _accessoryPersistence,
            _networkPakModuleService,
            _wifiProvisioningService,
            _operationCoordinator,
            _bridgeInstance);
        InitializeComponent();
        _operationCoordinator.CurrentChanged += OperationCoordinator_CurrentChanged;
        LoadControllerArt();
        ResetDebugLog();
        LogDebug("TRANSFER PERSISTENCE MODE=EVENT_DRIVEN", force: true);
        AddDebugLogButton();
        AddFirmwareModeControls();
        AddInputCaptureControls();
        AddAnalogCalibrationMenuItem();
        InitializeControllerPakMenu();
        InitializeSettingsMenu();
        InitializeHistoryMenu();
        InitializeNetworkPakBridgeUi();
        InitializeConsoleSelector();
        videoPictureBox.Resize += (_, _) => ResizeVideoPreview();
        CreateButtonPad();
        LoadMapping();
        UpdateApplicationBranding();
        ApplyDebugModeUi();
        ApplyOnScreenControlsUi();
        RefreshPorts();
        RefreshCaptureDevices();
        _initializingUi = true;
        SetInputSourceMode(ParseInputSourceMode(_settings.InputSourceMode));
        _initializingUi = false;
        SetDisconnectedState();

        _rumblePollTimer.Interval = 250;
        _rumblePollTimer.Tick += RumblePollTimer_Tick;

        _serialReconnectTimer.Interval = 1000;
        _serialReconnectTimer.Tick += SerialReconnectTimer_Tick;
        _serialReconnectTimer.Start();
        _persistencePollTimer.Interval = 1000;
        _persistencePollTimer.Tick += PersistencePollTimer_Tick;
        _persistencePollTimer.Start();

        _debugLogTask = Task.Run(() => DebugLogWriterLoop(_debugLogCancellation.Token));
        _serialTask = Task.Run(() => SerialWriterLoop(_serialCancellation.Token));
        _inputTask = Task.Run(() => PollGamepadLoop(_inputCancellation.Token));
        StartOptionalBridgeApiHost();
    }

    private void StartOptionalBridgeApiHost()
    {
        BridgeApiAccessConfiguration persisted = new(
            _settings.ApiAccessMode,
            _settings.ApiPort,
            _settings.ApiSelectedInterfaceId,
            _settings.ApiSelectedIpv4Address,
            _settings.ApiSelectedInterfaceName);
        BridgeApiAccessResolution resolution = BridgeApiAccessSettingsResolver.Resolve(
            persisted,
            Environment.GetEnvironmentVariable(
                BridgeApiAccessSettingsResolver.LegacyEnabledEnvironmentVariable),
            Environment.GetEnvironmentVariable(
                BridgeApiAccessSettingsResolver.LegacyPortEnvironmentVariable));
        string tokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NICE64", "bridge-api-token.dat");
        string tlsIdentityPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NICE64", "bridge-api-tls.pfx.dat");
        BridgeApiProtectedTokenStore tokenStore = new(tokenPath);
        BridgeApiTlsIdentityProvider tlsIdentity = new(tlsIdentityPath,
            message => LogDebug(message, force: true));
        BridgeApiV1HistoryProvider historyProvider = new(_historyService);
        BridgeApiHostFactory hostFactory = new(
            _bridgeState,
            tokenStore,
            new BridgeApiCommandFacade(
                _operationCoordinator,
                _firmwareService,
                _accessoryService,
                _networkPakModuleService,
                _networkPakCarrierService,
                _connectionService),
            message => LogDebug(message, force: true),
            history: historyProvider,
            sessions: _sessionService,
            controller: new BridgeApiV1ControllerFacade(
                _sessionService,
                _controllerPrimaryService,
                _controllerEndpointProvider),
            historyActivation: new BridgeApiV1HistoryActivationFacade(
                _sessionService,
                new BridgeApiV1HistoryItemResolver(historyProvider),
                _historyActivationService),
            keyboardMouse: _keyboardMouseStateFacade);
        _apiAccessLifecycle = new BridgeApiAccessLifecycle(
            new BridgeApiLanAddressValidator(
                new BridgeApiNetworkInterfaceCatalog()),
            hostFactory,
            tlsIdentity,
            allowDevelopmentPlaintextLan: false,
            message => LogDebug(message, force: true));
        _apiLocalCredentials = new BridgeApiLocalCredentialProvider(tokenStore);
        _apiOnboardingProvider = new BridgeApiOnboardingProvider(
            () => _apiAccessLifecycle.Current,
            tlsIdentity, _apiLocalCredentials);
        _ = ApplyInitialApiAccessAsync(resolution);
    }

    private async Task ApplyInitialApiAccessAsync(
        BridgeApiAccessResolution resolution)
    {
        try
        {
            await _apiAccessLifecycle!.ApplyAsync(
                resolution, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogDebug($"API access lifecycle startup failed: {ex.Message}",
                force: true);
        }
    }

    private void OperationCoordinator_CurrentChanged(
        object? sender, BridgeOperationStateChangedEventArgs e)
    {
        bool firmwareGuard = IsFirmwareCompatibilityOperation(e.Current);
        bool mutableGuard = IsControllerCompatibilityOperation(e.Current);
        bool contentGuard = IsControllerContentOperation(e.Current);
        bool changed = _firmwareModeSwitchInProgress != firmwareGuard ||
            _controllerPakOperationInProgress != mutableGuard ||
            _controllerContentOperationInProgress != contentGuard;
        _firmwareModeSwitchInProgress = firmwareGuard;
        _controllerPakOperationInProgress = mutableGuard;
        _controllerContentOperationInProgress = contentGuard;
        if (changed && firmwareGuard && _keyboardMouseInput.CaptureActive)
            RunOnUiThread(() => _ = ReleaseInputCaptureAsync(
                "firmware personality change"));
        if (CurrentControllerDeliveryGuard() == BridgeControllerDeliveryGuard.All)
            StopLatestStateUdpSender();

        bool terminalPresentationDispatched =
            BridgeDesktopPresentationConvergence.DispatchTerminal(
                e.Previous,
                e.Current,
                () => _bridgeDeviceState.Current,
                () => _networkPakModuleService.Current,
                convergence => RunOnUiThread(() =>
                    ApplyTerminalPresentationConvergence(convergence)));
        if (!changed && !terminalPresentationDispatched) return;

        if (changed) RunOnUiThread(() =>
        {
            UpdateFirmwareModeUi();
            ApplyAccessoryModeUi();
        });
    }

    private void ApplyTerminalPresentationConvergence(
        BridgeDesktopPresentationConvergencePlan convergence)
    {
        BridgeDesktopPresentationSnapshot presentation = convergence.Presentation;
        if (convergence.RefreshFirmware)
        {
            _activeFirmwareMode = presentation.FirmwarePersonality;
            ApplyAuthoritativeFirmwarePresentationState(presentation);
            SynchronizeKeyboardMouseCapture();
            UpdateFirmwareModeUi();
        }
        if (convergence.RefreshAccessory)
            ApplyAccessoryModeUi();
        if (convergence.RefreshNetworkPakModule)
            ApplyNetworkPakModuleUi();
        if (convergence.RefreshNetworkPakCarrier)
        {
            if (BridgeNetworkPakCarrierTransitionPolicy.IsSelectable(
                    presentation.SelectedNetworkPakTransport))
                _networkPakPresentationCarrier =
                    presentation.SelectedNetworkPakTransport;
            ApplyNetworkPakTransportUi();
        }
    }

    private void ApplyAuthoritativeFirmwarePresentationState(
        BridgeDesktopPresentationSnapshot presentation)
    {
        switch (presentation.FirmwarePresentationState)
        {
            case FirmwareModeSwitchState.Disconnected:
                _firmwareModeState.Disconnected();
                break;
            case FirmwareModeSwitchState.RequestingMode:
                _firmwareModeState.Requesting(presentation.FirmwareTransitionTarget);
                break;
            case FirmwareModeSwitchState.WaitingForDisconnect:
                _firmwareModeState.WaitingForDisconnect();
                break;
            case FirmwareModeSwitchState.WaitingForReconnect:
                _firmwareModeState.WaitingForReconnect();
                break;
            case FirmwareModeSwitchState.ModeSwitchFailed:
                _firmwareModeState.Ready(presentation.FirmwarePersonality);
                _firmwareModeState.Failed();
                break;
            case FirmwareModeSwitchState.ReadyController:
            case FirmwareModeSwitchState.ReadyDiagnostics:
            case FirmwareModeSwitchState.ReadyNetwork:
            case FirmwareModeSwitchState.ReadyKeyboard:
            case FirmwareModeSwitchState.ReadyMouse:
                _firmwareModeState.Ready(presentation.FirmwarePersonality);
                break;
            default:
                _firmwareModeState.Connected();
                break;
        }
    }

    private static bool IsFirmwareCompatibilityOperation(BridgeOperationSnapshot operation) =>
        operation.Status == BridgeOperationStatus.Running && operation.Kind is
            BridgeOperationKind.FirmwareQuery or
            BridgeOperationKind.FirmwareModeSwitch or
            BridgeOperationKind.AccessorySelection;

    private static bool IsControllerCompatibilityOperation(BridgeOperationSnapshot operation) =>
        operation.Status == BridgeOperationStatus.Running &&
        operation.Kind != BridgeOperationKind.NetworkPakModuleSelection;

    private static bool IsControllerContentOperation(BridgeOperationSnapshot operation) =>
        operation.Status == BridgeOperationStatus.Running && operation.Kind is
            BridgeOperationKind.ControllerPakCreate or
            BridgeOperationKind.ControllerPakLoad or
            BridgeOperationKind.ControllerPakSave or
            BridgeOperationKind.TransferPakInstall or
            BridgeOperationKind.TransferPakSave or
            BridgeOperationKind.TransferPakLegacyLoad or
            BridgeOperationKind.HistoryActivation;

    private void LogHistoryRecording(
        BridgeHistoryAssociationRecordingSnapshot recording)
    {
        RunOnUiThread(() =>
        {
            RefreshHistoryMenu();
            if (!recording.ContentSucceeded || !recording.RecordingRequired ||
                recording.HistoryRecorded) return;
            LogDebug($"HISTORY RECORD FAILED category={recording.Category} " +
                $"path={recording.Path} error={recording.Error}", force: true);
        });
    }

    private bool TryBeginDesktopOperation(BridgeOperationKind kind, string domain,
        BridgeOperationCancellationCapability cancellationCapability,
        out BridgeOperationLease? lease)
    {
        BridgeOperationAdmission admission = _operationCoordinator.TryBegin(
            kind, domain, cancellationCapability);
        lease = admission.Lease;
        if (admission.Accepted) return true;

        SetStatus($"Bridge operation {admission.Current.Kind} is already in progress.");
        return false;
    }

    private static void CompleteDesktopOperation(BridgeOperationLease lease,
        bool success, bool cancelled, string failureCode, string? error,
        object domainResult)
    {
        if (success)
            lease.Succeed(domainResult);
        else if (cancelled)
            lease.Cancel(failureCode, error, domainResult);
        else
            lease.Fail(failureCode, error, domainResult);
    }

    private void AddDebugLogButton()
    {
        _openDebugLogButton = new Button
        {
            Location = new Point(584, 111),
            Name = "openDebugLogButton",
            Size = new Size(82, 25),
            TabIndex = 9,
            Text = "Open Log",
            UseVisualStyleBackColor = true
        };
        _openDebugLogButton.Click += OpenDebugLogButton_Click;
        Controls.Add(_openDebugLogButton);
    }

    private void AddAnalogCalibrationMenuItem()
    {
        configureControllerMenuItem.Text = "Configure Controller";
        configureControllerMenuItem.Click -= ConfigureControllerMenuItem_Click;

        ToolStripMenuItem buttonMappingMenuItem = new()
        {
            Name = "buttonMappingMenuItem",
            Size = new Size(210, 22),
            Text = "Button Mapping..."
        };
        buttonMappingMenuItem.Click += ConfigureControllerMenuItem_Click;

        ToolStripMenuItem analogCalibrationMenuItem = new()
        {
            Name = "analogStickCalibrationMenuItem",
            Size = new Size(210, 22),
            Text = "Analog Stick Calibration..."
        };
        analogCalibrationMenuItem.Click += AnalogStickCalibrationMenuItem_Click;

        configureControllerMenuItem.DropDownItems.Clear();
        configureControllerMenuItem.DropDownItems.Add(buttonMappingMenuItem);
        configureControllerMenuItem.DropDownItems.Add(analogCalibrationMenuItem);
    }

    private void InitializeSettingsMenu()
    {
        ToolStripMenuItem settingsMenuItem = new()
        {
            Name = "settingsMenuItem",
            Text = "Settings"
        };

        _settingsDebugModeMenuItem = new ToolStripMenuItem
        {
            Name = "debugModeMenuItem",
            Text = "Debug Mode",
            CheckOnClick = true,
            Checked = _debugModeEnabled
        };
        _settingsDebugModeMenuItem.CheckedChanged += (_, _) =>
        {
            _debugModeEnabled = _settingsDebugModeMenuItem.Checked;
            BridgeSettings.SaveDebugMode(_settingsPath, _debugModeEnabled);
            ApplyDebugModeUi();
        };

        _settingsOnScreenControlsMenuItem = new ToolStripMenuItem
        {
            Name = "enableOnScreenControlsMenuItem",
            Text = "Enable On-Screen Controls",
            CheckOnClick = true,
            Checked = _onScreenControlsEnabled
        };
        _settingsOnScreenControlsMenuItem.CheckedChanged += (_, _) =>
        {
            _onScreenControlsEnabled = _settingsOnScreenControlsMenuItem.Checked;
            BridgeSettings.SaveEnableOnScreenControls(_settingsPath, _onScreenControlsEnabled);
            ApplyOnScreenControlsUi();
        };

        _settingsAskToNameSessionMenuItem = new ToolStripMenuItem
        {
            Name = "askToNameNice64SessionMenuItem",
            Text = "Ask to name NICE64 session when connecting",
            CheckOnClick = true,
            Checked = _askToNameSessionWhenConnecting
        };
        _settingsAskToNameSessionMenuItem.CheckedChanged += (_, _) =>
        {
            _askToNameSessionWhenConnecting =
                _settingsAskToNameSessionMenuItem.Checked;
            BridgeSettings.SaveAskToNameNice64SessionWhenConnecting(
                _settingsPath, _askToNameSessionWhenConnecting);
        };

        ToolStripMenuItem firmwareModeMenuItem = new()
        {
            Name = "firmwareModeMenuItem",
            Text = "Firmware Mode"
        };

        _settingsFirmwareControllerMenuItem = CreateFirmwareModeMenuItem("Controller", FirmwarePersonality.Controller);
        _settingsFirmwareDiagnosticsMenuItem = CreateFirmwareModeMenuItem("Diagnostics", FirmwarePersonality.Diagnostics);
        _settingsFirmwareNetworkMenuItem = CreateFirmwareModeMenuItem("Network", FirmwarePersonality.Network);
        _settingsFirmwareKeyboardMenuItem = CreateFirmwareModeMenuItem(
            "RandNET Keyboard", FirmwarePersonality.Keyboard);
        _settingsFirmwareMouseMenuItem = CreateFirmwareModeMenuItem(
            "N64 Mouse", FirmwarePersonality.Mouse);
        ToolStripMenuItem keyboardMousePersonalityMenuItem = new()
        {
            Name = "keyboardMousePersonalityMenuItem",
            Text = "Keyboard / Mouse"
        };
        keyboardMousePersonalityMenuItem.DropDownItems.AddRange(
        [
            _settingsFirmwareKeyboardMenuItem,
            _settingsFirmwareMouseMenuItem
        ]);
        ToolStripMenuItem standaloneMenuItem = new()
        {
            Name = "standalonePersonalityMenuItem",
            Text = "Standalone"
        };
        standaloneMenuItem.DropDownItems.Add(keyboardMousePersonalityMenuItem);
        firmwareModeMenuItem.DropDownItems.AddRange(
        [
            _settingsFirmwareControllerMenuItem,
            _settingsFirmwareDiagnosticsMenuItem,
            _settingsFirmwareNetworkMenuItem,
            new ToolStripSeparator(),
            standaloneMenuItem
        ]);

        ToolStripMenuItem keyboardMouseInputMenuItem = new()
        {
            Name = "keyboardMouseInputMenuItem",
            Text = "Keyboard / Mouse Input"
        };
        _keyboardSourceMenuItem = new ToolStripMenuItem
        {
            Name = "keyboardSourceMenuItem",
            Text = "Keyboard Source"
        };
        _pointerSourceMenuItem = new ToolStripMenuItem
        {
            Name = "pointerSourceMenuItem",
            Text = "Pointer Source"
        };
        ToolStripMenuItem refreshInputDevicesMenuItem = new()
        {
            Name = "refreshKeyboardMouseDevicesMenuItem",
            Text = "Refresh Input Devices"
        };
        refreshInputDevicesMenuItem.Click += (_, _) => RefreshKeyboardMouseDevices(
            showStatus: true);
        ToolStripMenuItem mouseSettingsMenuItem = new()
        {
            Name = "n64MouseInputSettingsMenuItem",
            Text = "N64 Mouse"
        };
        ToolStripMenuItem invertMouseXMenuItem = new()
        {
            Name = "invertMouseXMenuItem",
            Text = "Invert X",
            CheckOnClick = true,
            Checked = _settings.MouseInvertX
        };
        ToolStripMenuItem invertMouseYMenuItem = new()
        {
            Name = "invertMouseYMenuItem",
            Text = "Invert Y",
            CheckOnClick = true,
            Checked = _settings.MouseInvertY
        };
        void SaveMouseInversion()
        {
            _keyboardMouseInput.SetPointerInversion(
                invertMouseXMenuItem.Checked, invertMouseYMenuItem.Checked);
            BridgeSettings.SaveMouseInversion(_settingsPath,
                invertMouseXMenuItem.Checked, invertMouseYMenuItem.Checked);
        }
        invertMouseXMenuItem.CheckedChanged += (_, _) => SaveMouseInversion();
        invertMouseYMenuItem.CheckedChanged += (_, _) => SaveMouseInversion();
        mouseSettingsMenuItem.DropDownItems.AddRange(
        [
            invertMouseXMenuItem,
            invertMouseYMenuItem
        ]);
        keyboardMouseInputMenuItem.DropDownItems.AddRange(
        [
            _keyboardSourceMenuItem,
            _pointerSourceMenuItem,
            new ToolStripSeparator(),
            mouseSettingsMenuItem,
            new ToolStripSeparator(),
            refreshInputDevicesMenuItem
        ]);
        keyboardMouseInputMenuItem.DropDownOpening += (_, _) =>
            RebuildKeyboardMouseSourceMenus();

        ToolStripMenuItem wifiMenuItem = new()
        {
            Name = "wifiNetworkMenuItem",
            Text = "Wi-Fi / Network..."
        };
        wifiMenuItem.Click += (_, _) =>
        {
            using WifiProvisioningDialog dialog = new(_wifiProvisioningService);
            dialog.ShowDialog(this);
        };

        ToolStripMenuItem connectionMenuItem = new()
        {
            Name = "bridgeConnectionMenuItem",
            Text = "Bridge Connection..."
        };
        connectionMenuItem.Click += (_, _) => ConfigureBridgeConnection();

        ToolStripMenuItem mobileApiMenuItem = new()
        {
            Name = "mobileApiMenuItem",
            Text = "Mobile / API..."
        };
        mobileApiMenuItem.Click += (_, _) => ShowMobileApiOnboarding();

        ToolStripMenuItem controllerPrimaryMenuItem = new()
        {
            Name = "controllerPrimaryMenuItem",
            Text = "Controller Primary"
        };
        _settingsBridgePrimaryMenuItem = new ToolStripMenuItem
        {
            Name = "bridgeControllerPrimaryMenuItem",
            Text = "Bridge Primary"
        };
        _settingsMobilePrimaryMenuItem = new ToolStripMenuItem
        {
            Name = "mobileControllerPrimaryMenuItem",
            Text = "NICE Mobile Primary"
        };
        _settingsBridgePrimaryMenuItem.Click += async (_, _) =>
            await SelectControllerPrimaryAsync(BridgeControllerPrimary.Bridge);
        _settingsMobilePrimaryMenuItem.Click += async (_, _) =>
            await SelectControllerPrimaryAsync(BridgeControllerPrimary.NiceMobile);
        controllerPrimaryMenuItem.DropDownItems.AddRange(
        [
            _settingsBridgePrimaryMenuItem,
            _settingsMobilePrimaryMenuItem
        ]);
        ApplyControllerPrimaryUi(_controllerPrimaryService.Current);

        settingsMenuItem.DropDownItems.AddRange(
        [
            _settingsDebugModeMenuItem,
            _settingsOnScreenControlsMenuItem,
            _settingsAskToNameSessionMenuItem,
            new ToolStripSeparator(),
            mobileApiMenuItem,
            controllerPrimaryMenuItem,
            new ToolStripSeparator(),
            connectionMenuItem,
            wifiMenuItem,
            new ToolStripSeparator(),
            firmwareModeMenuItem,
            keyboardMouseInputMenuItem
        ]);

        int videoIndex = menuStrip.Items.IndexOf(videoMenuItem);
        menuStrip.Items.Insert(videoIndex >= 0 ? videoIndex : menuStrip.Items.Count, settingsMenuItem);
    }

    private void ShowMobileApiOnboarding()
    {
        if (_apiAccessLifecycle is null || _apiOnboardingProvider is null ||
            _apiLocalCredentials is null)
        {
            MessageBox.Show(this,
                "The local API settings service is unavailable.",
                "Mobile / API", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        BridgeSettings persisted = BridgeSettings.Load(_settingsPath);
        BridgeApiAccessConfiguration desired = new(
            persisted.ApiAccessMode,
            persisted.ApiPort,
            persisted.ApiSelectedInterfaceId,
            persisted.ApiSelectedIpv4Address,
            persisted.ApiSelectedInterfaceName);
        BridgeApiNetworkInterfaceCatalog catalog = new();
        BridgeApiLanAddressValidator validator = new(catalog);
        BridgeApiAccessSettingsPresentation presentation = new(
            catalog, validator);
        BridgeApiAccessSettingsCoordinator coordinator = new(
            _apiAccessLifecycle,
            _apiOnboardingProvider,
            new DelegateBridgeApiAccessSettingsPersistence(configuration =>
                BridgeSettings.SaveApiAccessConfiguration(
                    _settingsPath, configuration)),
            presentation,
            configuration => BridgeApiAccessSettingsResolver.Resolve(
                configuration,
                Environment.GetEnvironmentVariable(
                    BridgeApiAccessSettingsResolver.LegacyEnabledEnvironmentVariable),
                Environment.GetEnvironmentVariable(
                    BridgeApiAccessSettingsResolver.LegacyPortEnvironmentVariable)));
        using BridgeApiAccessSettingsDialog dialog = new(
            desired, coordinator, _apiLocalCredentials);
        dialog.ShowDialog(this);
    }

    private void RefreshKeyboardMouseDevices(bool showStatus)
    {
        try
        {
            if (IsHandleCreated) _rawInput.RefreshDevices();
            RawInput_DevicesChanged();
            if (showStatus)
            {
                int keyboards = _rawInput.Devices.Count(device =>
                    device.Kind == WindowsRawInputDeviceKind.Keyboard);
                int pointers = _rawInput.Devices.Count(device =>
                    device.Kind == WindowsRawInputDeviceKind.Pointer);
                SetStatus($"Windows input devices refreshed: {keyboards} keyboard(s), " +
                    $"{pointers} pointer(s).");
            }
        }
        catch (Exception ex)
        {
            LogDebug($"RAW INPUT refresh failed: {ex.Message}", force: true);
            if (showStatus) SetStatus($"Input-device refresh failed: {ex.Message}");
        }
    }

    private async void RawInput_DevicesChanged()
    {
        HashSet<string> current = _rawInput.Devices.Select(device => device.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] removedDevices = _knownRawDeviceIds.Except(current,
            StringComparer.OrdinalIgnoreCase).ToArray();
        bool activeDeviceRemoved = _keyboardMouseInput.CaptureActive &&
            (_activeFirmwareMode == FirmwarePersonality.Keyboard
                ? _keyboardMouseInput.KeyboardDeviceId is null ||
                  removedDevices.Contains(_keyboardMouseInput.KeyboardDeviceId,
                      StringComparer.OrdinalIgnoreCase)
                : _activeFirmwareMode == FirmwarePersonality.Mouse &&
                  (_keyboardMouseInput.PointerDeviceId is null ||
                   removedDevices.Contains(_keyboardMouseInput.PointerDeviceId,
                       StringComparer.OrdinalIgnoreCase)));
        if (activeDeviceRemoved)
            await ReleaseInputCaptureAsync("input device removed");

        foreach (string removed in removedDevices)
        {
            _keyboardMouseInput.RemoveDevice(removed);
            LogDebug($"RAW INPUT device removed id={removed}", force: true);
        }
        _knownRawDeviceIds = current;
        if (_keyboardSourceMenuItem is not null && _pointerSourceMenuItem is not null)
            RebuildKeyboardMouseSourceMenus();
    }

    private void RebuildKeyboardMouseSourceMenus()
    {
        if (_keyboardSourceMenuItem is null || _pointerSourceMenuItem is null) return;
        RebuildInputSourceMenu(_keyboardSourceMenuItem,
            WindowsRawInputDeviceKind.Keyboard,
            "System / Any Keyboard", _keyboardMouseInput.KeyboardDeviceId,
            SelectKeyboardInputDevice);
        RebuildInputSourceMenu(_pointerSourceMenuItem,
            WindowsRawInputDeviceKind.Pointer,
            "System / Any Mouse", _keyboardMouseInput.PointerDeviceId,
            SelectPointerInputDevice);
    }

    private void RebuildInputSourceMenu(ToolStripMenuItem menu,
        WindowsRawInputDeviceKind kind, string anyText, string? selectedId,
        Action<string?> select)
    {
        menu.DropDownItems.Clear();
        ToolStripMenuItem any = new()
        {
            Text = anyText,
            Checked = selectedId is null
        };
        any.Click += (_, _) => select(null);
        menu.DropDownItems.Add(any);

        IReadOnlyList<WindowsRawInputDevice> devices = _rawInput.Devices
            .Where(device => device.Kind == kind).ToArray();
        foreach (WindowsRawInputDevice device in devices)
        {
            string id = device.Id;
            ToolStripMenuItem item = new()
            {
                Text = device.DisplayName,
                ToolTipText = id,
                Checked = string.Equals(selectedId, id,
                    StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (_, _) => select(id);
            menu.DropDownItems.Add(item);
        }

        if (selectedId is not null && !devices.Any(device =>
                string.Equals(device.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
        {
            menu.DropDownItems.Add(new ToolStripMenuItem
            {
                Text = "Selected device (unavailable)",
                ToolTipText = selectedId,
                Checked = true,
                Enabled = false
            });
        }
    }

    private async void SelectKeyboardInputDevice(string? deviceId)
    {
        await ReleaseInputCaptureAsync("keyboard source changed");
        _keyboardMouseInput.SelectKeyboard(deviceId);
        BridgeSettings.SaveKeyboardInputDeviceId(_settingsPath, deviceId);
        RebuildKeyboardMouseSourceMenus();
        SetStatus(deviceId is null
            ? "Keyboard source: System / Any Keyboard."
            : "Keyboard source: selected HID device.");
    }

    private async void SelectPointerInputDevice(string? deviceId)
    {
        await ReleaseInputCaptureAsync("pointer source changed");
        _keyboardMouseInput.SelectPointer(deviceId);
        BridgeSettings.SavePointerInputDeviceId(_settingsPath, deviceId);
        RebuildKeyboardMouseSourceMenus();
        SetStatus(deviceId is null
            ? "Pointer source: System / Any Mouse."
            : "Pointer source: selected HID device.");
    }

    private void RawInput_KeyboardInput(WindowsRawKeyboardEvent input)
    {
        if (!_keyboardMouseInput.CaptureActive) return;
        KeyboardCaptureChordResult result = _captureChordFilter.Process(input);
        if (result.ReleaseCapture)
        {
            _ = ReleaseInputCaptureAsync("Alt+Left Arrow");
            return;
        }
        foreach (WindowsRawKeyboardEvent forwarded in result.ForwardedEvents)
            _keyboardMouseInput.ApplyKeyboard(forwarded);
    }

    private void EnterInputCapture()
    {
        if (_inputCaptureTransitionInProgress) return;
        FirmwarePersonality mode = _activeFirmwareMode;
        if (!IsBridgeConnected || mode is not (FirmwarePersonality.Keyboard or
                FirmwarePersonality.Mouse) ||
            _firmwareModeState.State is not (FirmwareModeSwitchState.ReadyKeyboard or
                FirmwareModeSwitchState.ReadyMouse))
        {
            SetStatus("Select and verify RandNET Keyboard or N64 Mouse mode first.");
            UpdateInputCaptureUi();
            return;
        }
        if (!ContainsFocus)
        {
            Activate();
            BringToFront();
        }

        WindowsRawInputCaptureMode rawMode = mode == FirmwarePersonality.Keyboard
            ? WindowsRawInputCaptureMode.Keyboard
            : WindowsRawInputCaptureMode.Pointer;
        try
        {
            _captureChordFilter.Reset();
            _rawInput.SetCaptureMode(rawMode);
            if (!_keyboardMouseInput.EnterCapture())
                throw new InvalidOperationException(
                    "The verified firmware personality is not capture-capable.");
            LogDebug($"WINDOWS HID CAPTURE entered mode={mode} scope=usage-class",
                force: true);
            SetStatus($"{(mode == FirmwarePersonality.Keyboard ? "Keyboard" : "Mouse")} " +
                "capture active. Press Alt+Left Arrow to release.");
        }
        catch (Exception ex)
        {
            _rawInput.SetCaptureMode(WindowsRawInputCaptureMode.Released);
            _keyboardMouseInput.SetActiveMode(mode);
            LogDebug($"WINDOWS HID CAPTURE entry failed: {ex.Message}", force: true);
            SetStatus($"Input capture failed: {ex.Message}");
        }
        UpdateInputCaptureUi();
    }

    private async Task ReleaseInputCaptureAsync(string reason)
    {
        if (_inputCaptureTransitionInProgress) return;
        bool wasCaptured = _keyboardMouseInput.CaptureActive;
        if (!wasCaptured &&
            _rawInput.CaptureMode == WindowsRawInputCaptureMode.Released)
            return;

        _inputCaptureTransitionInProgress = true;
        try
        {
            _captureChordFilter.Reset();
            try
            {
                _rawInput.SetCaptureMode(WindowsRawInputCaptureMode.Released);
            }
            catch (Exception ex)
            {
                LogDebug($"WINDOWS HID CAPTURE registration release failed: " +
                    ex.Message, force: true);
            }
            Task neutral = _keyboardMouseInput.ReleaseCaptureAsync();
            UpdateInputCaptureUi();
            await neutral;
            if (wasCaptured)
            {
                LogDebug($"WINDOWS HID CAPTURE released reason={reason}", force: true);
                SetStatus($"Input capture released ({reason}). NICE64 input is neutral.");
            }
        }
        finally
        {
            _inputCaptureTransitionInProgress = false;
            UpdateInputCaptureUi();
        }
    }

    private void ReleaseInputCaptureWithoutDelivery(string reason)
    {
        bool wasCaptured = _keyboardMouseInput.CaptureActive;
        _captureChordFilter.Reset();
        try { _rawInput.SetCaptureMode(WindowsRawInputCaptureMode.Released); }
        catch (Exception ex)
        {
            LogDebug($"WINDOWS HID CAPTURE forced release failed: {ex.Message}",
                force: true);
        }
        _keyboardMouseInput.ClearWithoutDelivery();
        if (wasCaptured)
            LogDebug($"WINDOWS HID CAPTURE cleared without transport reason={reason}",
                force: true);
        UpdateInputCaptureUi();
    }

    private void UpdateInputCaptureUi()
    {
        if (_inputCaptureValueLabel is null || _inputCaptureButton is null) return;
        bool captured = _keyboardMouseInput.CaptureActive;
        bool available = IsBridgeConnected &&
            (_activeFirmwareMode is FirmwarePersonality.Keyboard or
                FirmwarePersonality.Mouse) &&
            (_firmwareModeState.State is FirmwareModeSwitchState.ReadyKeyboard or
                FirmwareModeSwitchState.ReadyMouse);
        if (captured)
        {
            string device = _activeFirmwareMode == FirmwarePersonality.Keyboard
                ? "KEYBOARD" : "MOUSE";
            _inputCaptureValueLabel.Text =
                $"CAPTURED: {device} CLASS — Alt+Left releases";
            _inputCaptureValueLabel.ForeColor = Color.DarkGreen;
            _inputCaptureButton.Text = "Release Capture";
        }
        else
        {
            _inputCaptureValueLabel.Text = available
                ? "RELEASED — click Capture Input to begin"
                : "RELEASED — select Keyboard or Mouse mode first";
            _inputCaptureValueLabel.ForeColor = available
                ? Color.DarkRed : SystemColors.GrayText;
            _inputCaptureButton.Text = available &&
                _activeFirmwareMode == FirmwarePersonality.Keyboard
                    ? "Capture Keyboard"
                    : available ? "Capture Mouse" : "Capture Input";
        }
        _inputCaptureButton.Enabled = !_inputCaptureTransitionInProgress &&
            (captured || available);
    }

    private async Task SelectControllerPrimaryAsync(BridgeControllerPrimary primary)
    {
        string? sessionId = _sessionService.Current?.SessionId;
        if (sessionId is null)
        {
            SetStatus("Connect a NICE64 before selecting the controller primary.");
            return;
        }

        BridgeControllerPrimaryResult result = await _controllerPrimaryService
            .SelectAsync(sessionId, primary, CancellationToken.None)
            .ConfigureAwait(true);
        if (!result.Success)
        {
            SetStatus($"Controller primary unchanged: {result.Failure}.");
            return;
        }
        ApplyControllerPrimaryUi(result.Snapshot);
        SetStatus(primary == BridgeControllerPrimary.Bridge
            ? "Bridge is the controller primary."
            : "NICE Mobile is the controller primary.");
    }

    private void ControllerPrimary_CurrentChanged(
        object? sender, BridgeControllerPrimaryChangedEventArgs e) =>
        RunOnUiThread(() => ApplyControllerPrimaryUi(e.Current));

    private void ApplyControllerPrimaryUi(BridgeControllerPrimarySnapshot snapshot)
    {
        if (_settingsBridgePrimaryMenuItem is not null)
            _settingsBridgePrimaryMenuItem.Checked =
                snapshot.Primary == BridgeControllerPrimary.Bridge;
        if (_settingsMobilePrimaryMenuItem is not null)
            _settingsMobilePrimaryMenuItem.Checked =
                snapshot.Primary == BridgeControllerPrimary.NiceMobile;
    }

    private ToolStripMenuItem CreateFirmwareModeMenuItem(string text, FirmwarePersonality target)
    {
        ToolStripMenuItem item = new()
        {
            Text = text,
            Tag = target
        };
        item.Click += (_, _) => BeginFirmwareModeSwitch(target);
        return item;
    }

    private void AddFirmwareModeControls()
    {
        _firmwareModeGroupBox = new GroupBox
        {
            Location = new Point(24, 182),
            Name = "firmwareModeGroupBox",
            Size = new Size(624, 50),
            Text = "Firmware Mode"
        };

        _firmwareModeValueLabel = new Label
        {
            Location = new Point(14, 21),
            Name = "firmwareModeValueLabel",
            Size = new Size(138, 20),
            Text = "Current: Unknown",
            TextAlign = ContentAlignment.MiddleLeft
        };

        _firmwareControllerButton = new Button
        {
            Location = new Point(158, 17),
            Name = "firmwareControllerButton",
            Size = new Size(82, 25),
            Text = "Controller",
            UseVisualStyleBackColor = true
        };
        _firmwareControllerButton.Click += (_, _) => BeginFirmwareModeSwitch(FirmwarePersonality.Controller);

        _firmwareDiagnosticsButton = new Button
        {
            Location = new Point(246, 17),
            Name = "firmwareDiagnosticsButton",
            Size = new Size(82, 25),
            Text = "Diagnostics",
            UseVisualStyleBackColor = true
        };
        _firmwareDiagnosticsButton.Click += (_, _) => BeginFirmwareModeSwitch(FirmwarePersonality.Diagnostics);

        _firmwareNetworkButton = new Button
        {
            Location = new Point(334, 17),
            Name = "firmwareNetworkButton",
            Size = new Size(82, 25),
            Text = "Network",
            UseVisualStyleBackColor = true
        };
        _firmwareNetworkButton.Click += (_, _) => BeginFirmwareModeSwitch(FirmwarePersonality.Network);

        _firmwareKeyboardButton = new Button
        {
            Location = new Point(422, 17),
            Name = "firmwareKeyboardButton",
            Size = new Size(82, 25),
            Text = "Keyboard",
            UseVisualStyleBackColor = true
        };
        _firmwareKeyboardButton.Click += (_, _) =>
            BeginFirmwareModeSwitch(FirmwarePersonality.Keyboard);

        _firmwareMouseButton = new Button
        {
            Location = new Point(510, 17),
            Name = "firmwareMouseButton",
            Size = new Size(82, 25),
            Text = "Mouse",
            UseVisualStyleBackColor = true
        };
        _firmwareMouseButton.Click += (_, _) =>
            BeginFirmwareModeSwitch(FirmwarePersonality.Mouse);

        _firmwareModeGroupBox.Controls.Add(_firmwareModeValueLabel);
        _firmwareModeGroupBox.Controls.Add(_firmwareControllerButton);
        _firmwareModeGroupBox.Controls.Add(_firmwareDiagnosticsButton);
        _firmwareModeGroupBox.Controls.Add(_firmwareNetworkButton);
        _firmwareModeGroupBox.Controls.Add(_firmwareKeyboardButton);
        _firmwareModeGroupBox.Controls.Add(_firmwareMouseButton);
        Controls.Add(_firmwareModeGroupBox);

        UpdateFirmwareModeUi();
    }

    private void AddInputCaptureControls()
    {
        _inputCaptureGroupBox = new GroupBox
        {
            Location = new Point(24, 238),
            Name = "inputCaptureGroupBox",
            Size = new Size(624, 52),
            Text = "N64 Input Capture"
        };
        _inputCaptureValueLabel = new Label
        {
            Location = new Point(14, 21),
            Name = "inputCaptureValueLabel",
            Size = new Size(410, 22),
            Text = "Released — input is not being sent to NICE64",
            TextAlign = ContentAlignment.MiddleLeft
        };
        _inputCaptureButton = new Button
        {
            Location = new Point(438, 17),
            Name = "inputCaptureButton",
            Size = new Size(154, 27),
            Text = "Capture Input",
            UseVisualStyleBackColor = true
        };
        _inputCaptureButton.Click += async (_, _) =>
        {
            if (_keyboardMouseInput.CaptureActive)
                await ReleaseInputCaptureAsync("released by user");
            else
                EnterInputCapture();
        };
        _inputCaptureGroupBox.Controls.Add(_inputCaptureValueLabel);
        _inputCaptureGroupBox.Controls.Add(_inputCaptureButton);
        Controls.Add(_inputCaptureGroupBox);

        buttonPad.Location = new Point(buttonPad.Left, 300);
        buttonPad.Size = new Size(buttonPad.Width, 268);
        UpdateInputCaptureUi();
    }

    private void InitializeNetworkPakBridgeUi()
    {
        ToolStripMenuItem bridgeMenuItem = new() { Text = "Network Pak Bridge..." };
        bridgeMenuItem.Click += (_, _) => ShowNetworkPakBridgeWindow();
        toolsMenuItem.DropDownItems.Add(new ToolStripSeparator());
        toolsMenuItem.DropDownItems.Add(bridgeMenuItem);
    }

    private void ShowNetworkPakBridgeWindow()
    {
        if (_networkPakBridgeForm is null || _networkPakBridgeForm.IsDisposed)
        {
            _networkPakBridgeForm = new NetworkPakBridgeForm(_networkPakBridge);
        }

        _networkPakBridgeForm.Show(this);
        _networkPakBridgeForm.BringToFront();
    }

    private void ResetDebugLog()
    {
        lock (_debugLogLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_debugLogPath)!);
            File.WriteAllText(
                _debugLogPath,
                $"# PicoSwitchController v{BridgeConstants.BridgeVersion} debug log started {DateTimeOffset.Now:O}{Environment.NewLine}");
        }
    }

    private void LogDebug(string message, bool force = false)
    {
        if (!_debugLoggingEnabled && !force)
        {
            return;
        }

        string line = $"{DateTimeOffset.Now:O}\t{message}";
        _debugLogQueue.Enqueue(line);
        _debugLogSignal.Set();
    }

    private void FlushDebugLogQueue()
    {
        List<string> lines = [];
        while (_debugLogQueue.TryDequeue(out string? line))
        {
            lines.Add(line);
        }

        if (lines.Count == 0)
        {
            return;
        }

        lock (_debugLogLock)
        {
            File.AppendAllLines(_debugLogPath, lines);
        }
    }

    private void DebugLogWriterLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _debugLogSignal.WaitOne(250);
            FlushDebugLogQueue();
        }

        FlushDebugLogQueue();
    }

    private void OpenDebugLogButton_Click(object? sender, EventArgs e)
    {
        try
        {
            FlushDebugLogQueue();
            if (!File.Exists(_debugLogPath))
            {
                ResetDebugLog();
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = _debugLogPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Open log failed: {ex.Message}");
        }
    }

    private void InitializeControllerPakMenu()
    {
        ToolStripMenuItem accessoryMenuItem = new()
        {
            Text = "N64 Accessory"
        };

        _noAccessoryModeMenuItem = new ToolStripMenuItem
        {
            Text = "No Accessory",
            Checked = true
        };
        _noAccessoryModeMenuItem.Click += (_, _) => RequestAccessoryMode(BridgeAccessoryMode.None);

        _memoryPakModeMenuItem = new ToolStripMenuItem
        {
            Text = "Controller Pak"
        };
        _memoryPakModeMenuItem.Click += (_, _) => RequestAccessoryMode(BridgeAccessoryMode.ControllerPak);

        _rumblePakModeMenuItem = new ToolStripMenuItem
        {
            Text = "Rumble Pak Mode"
        };
        _rumblePakModeMenuItem.Click += (_, _) => RequestAccessoryMode(BridgeAccessoryMode.RumblePak);

        _transferPakModeMenuItem = new ToolStripMenuItem
        {
            Text = "Transfer Pak Mode"
        };
        _transferPakModeMenuItem.Click += (_, _) => RequestAccessoryMode(BridgeAccessoryMode.TransferPak);

        _networkPakModeMenuItem = new ToolStripMenuItem
        {
            Text = "Network Pak Mode"
        };
        _networkPakModeMenuItem.Click += (_, _) => RequestAccessoryMode(BridgeAccessoryMode.NetworkPak);

        _accessoryKeyboardMenuItem = CreateFirmwareModeMenuItem(
            "RandNET Keyboard", FirmwarePersonality.Keyboard);
        _accessoryMouseMenuItem = CreateFirmwareModeMenuItem(
            "N64 Mouse", FirmwarePersonality.Mouse);

        ToolStripMenuItem networkPakModuleMenuItem = new()
        {
            Text = "Network Pak Module"
        };
        foreach (BridgeNetworkPakModuleInfo module in _networkPakModuleService.ListModules())
        {
            ToolStripMenuItem moduleMenuItem = new()
            {
                Text = module.DisplayName,
                Tag = module.Id
            };
            moduleMenuItem.Click += NetworkPakModuleMenuItem_Click;
            _networkPakModuleMenuItems.Add(moduleMenuItem);
            networkPakModuleMenuItem.DropDownItems.Add(moduleMenuItem);
        }

        _pcRumblePassthroughMenuItem = new ToolStripMenuItem
        {
            Text = "PC Controller Rumble Passthrough (experimental)",
            Checked = false
        };
        _pcRumblePassthroughMenuItem.Click += (_, _) =>
        {
            _pcRumblePassthroughMenuItem.Checked = !_pcRumblePassthroughMenuItem.Checked;
            ApplyAccessoryModeUi();
            SetStatus(_pcRumblePassthroughMenuItem.Checked
                ? "PC rumble passthrough enabled."
                : "PC rumble passthrough disabled.");
        };

        ToolStripMenuItem controllerPakMenuItem = new()
        {
            Text = "Controller Pak File"
        };

        ToolStripMenuItem newControllerPakMenuItem = new()
        {
            Text = "New Controller Pak"
        };
        newControllerPakMenuItem.Click += NewControllerPakMenuItem_Click;

        ToolStripMenuItem loadControllerPakMenuItem = new()
        {
            Text = BridgeUiPolicy.ControllerPakLoadMenuText
        };
        loadControllerPakMenuItem.Click += LoadControllerPakMenuItem_Click;

        ToolStripMenuItem saveControllerPakMenuItem = new()
        {
            Text = BridgeUiPolicy.ControllerPakSaveMenuText
        };
        saveControllerPakMenuItem.Click += SaveControllerPakMenuItem_Click;

        controllerPakMenuItem.DropDownItems.AddRange(
        [
            newControllerPakMenuItem,
            loadControllerPakMenuItem,
            saveControllerPakMenuItem
        ]);

        ToolStripMenuItem transferPakMenuItem = new()
        {
            Text = "Transfer Pak File"
        };

        ToolStripMenuItem installTransferRomFlashMenuItem = new()
        {
            Text = BridgeUiPolicy.FullGameBoyLoadMenuText
        };
        installTransferRomFlashMenuItem.Click += InstallTransferRomFlashMenuItem_Click;

        transferPakMenuItem.DropDownItems.Add(installTransferRomFlashMenuItem);
        if (BridgeUiPolicy.ShowLegacyFirst64KGameBoyLoad)
        {
            ToolStripMenuItem legacyFirst64KMenuItem = new()
            {
                Text = "Load first 64KB of GB/GBC ROM..."
            };
            legacyFirst64KMenuItem.Click += LoadTransferRomMenuItem_Click;
            transferPakMenuItem.DropDownItems.Add(legacyFirst64KMenuItem);
        }

        ToolStripMenuItem networkPakTransportMenuItem = new()
        {
            Text = "Network Pak Transport"
        };
        _networkPakUsbTransportMenuItem = new ToolStripMenuItem
        {
            Text = "USB"
        };
        _networkPakUsbTransportMenuItem.Click += (_, _) =>
            SetNetworkPakWifiTransport(NetworkPakWifiTransport.UsbSerial);
        _networkPakTcpTransportMenuItem = new ToolStripMenuItem
        {
            Text = "TCP",
            Checked = true
        };
        _networkPakTcpTransportMenuItem.Click += (_, _) =>
            SetNetworkPakWifiTransport(NetworkPakWifiTransport.Tcp);
        _networkPakQuickTransportMenuItem = new ToolStripMenuItem
        {
            Text = "Quick Mode (Reliable UDP)"
        };
        _networkPakQuickTransportMenuItem.Click += (_, _) =>
            SetNetworkPakWifiTransport(NetworkPakWifiTransport.ReliableUdpQuick);
        networkPakTransportMenuItem.DropDownItems.AddRange(
        [
            _networkPakUsbTransportMenuItem,
            _networkPakTcpTransportMenuItem,
            _networkPakQuickTransportMenuItem
        ]);

        ToolStripMenuItem inputSourceMenuItem = new()
        {
            Text = "Input Source"
        };

        _inputNoneMenuItem = new ToolStripMenuItem
        {
            Text = "UI only / no gamepad polling"
        };
        _inputNoneMenuItem.Click += (_, _) => SetInputSourceMode(InputSourceMode.None);

        _inputAutoMenuItem = new ToolStripMenuItem
        {
            Text = "Auto (SNES / legacy)",
            Checked = true
        };
        _inputAutoMenuItem.Click += (_, _) => SetInputSourceMode(InputSourceMode.Auto);

        _inputXInputMenuItem = new ToolStripMenuItem
        {
            Text = "XInput / Xbox-compatible"
        };
        _inputXInputMenuItem.Click += (_, _) => SetInputSourceMode(InputSourceMode.XInput);

        _inputDirectInputMenuItem = new ToolStripMenuItem
        {
            Text = "Generic USB"
        };
        _inputDirectInputMenuItem.Click += (_, _) => SetInputSourceMode(InputSourceMode.DirectInput);

        _inputEightBitDoParsecMenuItem = new ToolStripMenuItem
        {
            Text = "8BitDo / Parsec"
        };
        _inputEightBitDoParsecMenuItem.Click += (_, _) => SetInputSourceMode(InputSourceMode.EightBitDoParsec);

        inputSourceMenuItem.DropDownItems.AddRange(
        [
            _inputNoneMenuItem,
            _inputAutoMenuItem,
            _inputXInputMenuItem,
            _inputDirectInputMenuItem,
            _inputEightBitDoParsecMenuItem
        ]);

        _debugLoggingMenuItem = new ToolStripMenuItem
        {
            Text = "Enable Debug Logging",
            Checked = false
        };
        _debugLoggingMenuItem.Click += (_, _) =>
        {
            _debugLoggingEnabled = !_debugLoggingEnabled;
            _debugLoggingMenuItem.Checked = _debugLoggingEnabled;
            LogDebug(_debugLoggingEnabled ? "DEBUG LOGGING ENABLED" : "DEBUG LOGGING DISABLED", force: true);
            SetStatus(_debugLoggingEnabled
                ? "Debug logging enabled."
                : "Debug logging disabled.");
        };

        accessoryMenuItem.DropDownItems.AddRange(
        [
            _noAccessoryModeMenuItem,
            _memoryPakModeMenuItem,
            _transferPakModeMenuItem,
            new ToolStripSeparator(),
            _accessoryKeyboardMenuItem,
            _accessoryMouseMenuItem,
            new ToolStripSeparator(),
            _networkPakModeMenuItem,
            networkPakTransportMenuItem,
            networkPakModuleMenuItem,
            new ToolStripSeparator(),
            controllerPakMenuItem,
            transferPakMenuItem
        ]);

        toolsMenuItem.DropDownItems.Add(new ToolStripSeparator());
        toolsMenuItem.DropDownItems.Add(inputSourceMenuItem);
        toolsMenuItem.DropDownItems.Add(_debugLoggingMenuItem);
        toolsMenuItem.DropDownItems.Add(new ToolStripSeparator());
        toolsMenuItem.DropDownItems.Add(accessoryMenuItem);
        ApplyNetworkPakModuleUi();
        ApplyNetworkPakTransportUi();
    }

    private void InitializeHistoryMenu()
    {
        _historyMenuItem = new ToolStripMenuItem { Text = "History" };
        _controllerPakHistoryMenuItem = new ToolStripMenuItem
        {
            Text = "Controller Pak"
        };
        _transferPakHistoryMenuItem = new ToolStripMenuItem
        {
            Text = "Transfer Pak"
        };
        ToolStripMenuItem clearAll = new() { Text = "Clear All History" };
        clearAll.Click += ClearAllHistoryMenuItem_Click;
        _historyMenuItem.DropDownItems.AddRange(
        [
            _controllerPakHistoryMenuItem,
            _transferPakHistoryMenuItem,
            new ToolStripSeparator(),
            clearAll
        ]);
        _historyMenuItem.DropDownOpening += (_, _) => RefreshHistoryMenu();
        int videoIndex = menuStrip.Items.IndexOf(videoMenuItem);
        menuStrip.Items.Insert(videoIndex >= 0 ? videoIndex : menuStrip.Items.Count,
            _historyMenuItem);
        RefreshHistoryMenu();
    }

    private void RefreshHistoryMenu()
    {
        if (_historyMenuItem is null || _controllerPakHistoryMenuItem is null ||
            _transferPakHistoryMenuItem is null) return;

        BridgeHistoryDesktopMenuSnapshot presentation =
            BridgeHistoryDesktopPresentation.Compose(_historyService.Current,
                _bridgeDeviceState.Current.FirmwarePersonality);
        bool canActivate = presentation.NormalModeActive && IsBridgeConnected &&
            _operationCoordinator.Current.Status != BridgeOperationStatus.Running;
        RebuildHistoryCategoryMenu(_controllerPakHistoryMenuItem,
            presentation.ControllerPak,
            BridgeHistoryDesktopPresentation.EmptyControllerPakText,
            "Clear Controller Pak History", BridgeHistoryCategory.ControllerPak,
            canActivate);
        RebuildHistoryCategoryMenu(_transferPakHistoryMenuItem,
            presentation.TransferPak,
            BridgeHistoryDesktopPresentation.EmptyTransferPakText,
            "Clear Transfer Pak History", BridgeHistoryCategory.TransferPak,
            canActivate);

        if (_historyMenuItem.DropDownItems[^1] is ToolStripMenuItem clearAll)
        {
            clearAll.Enabled = presentation.ControllerPak.Count > 0 ||
                presentation.TransferPak.Count > 0;
        }
    }

    private void RebuildHistoryCategoryMenu(ToolStripMenuItem categoryMenu,
        IReadOnlyList<BridgeHistoryDesktopMenuEntry> entries, string emptyText,
        string clearText, BridgeHistoryCategory category, bool canActivate)
    {
        categoryMenu.DropDownItems.Clear();
        if (entries.Count == 0)
        {
            categoryMenu.DropDownItems.Add(new ToolStripMenuItem
            {
                Text = emptyText,
                Enabled = false
            });
        }
        else
        {
            foreach (BridgeHistoryDesktopMenuEntry entry in entries)
            {
                ToolStripMenuItem item = new()
                {
                    Text = entry.Label,
                    ToolTipText = entry.Path,
                    Tag = entry,
                    Enabled = canActivate && entry.ActivationEnabled
                };
                item.Click += HistoryEntryMenuItem_Click;
                categoryMenu.DropDownItems.Add(item);
            }
        }

        categoryMenu.DropDownItems.Add(new ToolStripSeparator());
        ToolStripMenuItem clear = new()
        {
            Text = clearText,
            Tag = category,
            Enabled = entries.Count > 0
        };
        clear.Click += ClearHistoryCategoryMenuItem_Click;
        categoryMenu.DropDownItems.Add(clear);
    }

    private async void HistoryEntryMenuItem_Click(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem
            {
                Tag: BridgeHistoryDesktopMenuEntry selection
            }) return;

        try
        {
            if (selection.Category == BridgeHistoryCategory.ControllerPak)
                await ActivateControllerPakHistoryAsync(selection.Path);
            else
                await ActivateTransferPakHistoryAsync(selection.Path);
        }
        catch (Exception ex)
        {
            SetStatus($"History activation failed: {ex.Message}");
            LogDebug($"HISTORY ACTIVATION UNEXPECTED category={selection.Category} " +
                $"path={selection.Path} error={ex.Message}", force: true);
        }
        finally
        {
            RefreshAuthoritativeHistoryPresentation();
        }
    }

    private async Task ActivateControllerPakHistoryAsync(string path)
    {
        SetStatus($"Loading {Path.GetFileName(path)} from Controller Pak History...");
        BridgeHistoryActivationResult result =
            await _historyActivationService.ActivateControllerPakAsync(path,
                CreateHistoryProgress());
        await PresentHistoryActivationResultAsync(result);
    }

    private async Task ActivateTransferPakHistoryAsync(string path)
    {
        SetStatus($"Inspecting {Path.GetFileName(path)} from Transfer Pak History...");
        BridgeHistoryActivationResult preflight =
            await _historyActivationService.ActivateTransferPakAsync(path,
                destructiveInstallConfirmed: false, CreateHistoryProgress());
        if (preflight.Failure != BridgeHistoryActivationFailure.ConfirmationRequired)
        {
            await PresentHistoryActivationResultAsync(preflight);
            return;
        }

        DialogResult confirm = MessageBox.Show(this, TransferPakInstallWarning,
            "Install Transfer Pak ROM", MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK)
        {
            SetStatus("Transfer Pak History activation cancelled.");
            return;
        }

        SetStatus($"Installing {Path.GetFileName(path)} from Transfer Pak History...");
        BridgeHistoryActivationResult result =
            await _historyActivationService.ActivateTransferPakAsync(path,
                destructiveInstallConfirmed: true, CreateHistoryProgress());
        await PresentHistoryActivationResultAsync(result);
    }

    private IProgress<BridgeHistoryActivationProgress> CreateHistoryProgress() =>
        new Progress<BridgeHistoryActivationProgress>(item =>
        {
            string file = Path.GetFileName(item.Path);
            SetStatus($"History {item.Category}: {item.Phase} {file}...");
        });

    private async Task PresentHistoryActivationResultAsync(
        BridgeHistoryActivationResult result)
    {
        BridgeHistoryDesktopOutcome outcome =
            BridgeHistoryDesktopPresentation.Classify(result);
        switch (outcome.Kind)
        {
            case BridgeHistoryDesktopOutcomeKind.Activated:
                SetStatus(outcome.Message);
                break;
            case BridgeHistoryDesktopOutcomeKind.ActivatedWithHistoryWarning:
                SetStatus(outcome.Message);
                LogDebug($"HISTORY UPDATE WARNING category={outcome.Category} " +
                    $"path={outcome.Path} message={outcome.Message}", force: true);
                MessageBox.Show(this, outcome.Message, "History Update Warning",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
            case BridgeHistoryDesktopOutcomeKind.PromptRemoveMissingFile:
                await PromptRemoveMissingHistoryEntryAsync(outcome.Category, outcome.Path);
                break;
            case BridgeHistoryDesktopOutcomeKind.NormalModeRequired:
                SetStatus("History activation requires Controller/Normal firmware mode.");
                MessageBox.Show(this,
                    "History items can be activated only while Controller/Normal firmware is active.",
                    "History Requires Normal Mode", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                break;
            case BridgeHistoryDesktopOutcomeKind.Cancelled:
                SetStatus("History activation cancelled.");
                break;
            case BridgeHistoryDesktopOutcomeKind.InspectionFailed:
                ShowHistoryFailure("Transfer Pak ROM inspection failed", outcome);
                break;
            case BridgeHistoryDesktopOutcomeKind.PersistenceFailed:
                ShowHistoryFailure("Outgoing accessory persistence failed", outcome);
                break;
            case BridgeHistoryDesktopOutcomeKind.AccessoryTransitionFailed:
                ShowHistoryFailure("Accessory transition failed", outcome);
                break;
            case BridgeHistoryDesktopOutcomeKind.ContentActivationFailed:
                ShowHistoryFailure("History content activation failed", outcome);
                break;
            default:
                ShowHistoryFailure("History activation failed", outcome);
                break;
        }
    }

    private void ShowHistoryFailure(string label, BridgeHistoryDesktopOutcome outcome)
    {
        SetStatus($"{label}: {outcome.Message}");
        LogDebug($"HISTORY ACTIVATION FAILED category={outcome.Category} " +
            $"path={outcome.Path} kind={outcome.Kind} error={outcome.Message}",
            force: true);
        MessageBox.Show(this, outcome.Message, label,
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private async Task PromptRemoveMissingHistoryEntryAsync(
        BridgeHistoryCategory category, string path)
    {
        DialogResult remove = MessageBox.Show(this,
            $"The selected file could not be found:\n\n{path}\n\n" +
            "Remove this item from History?",
            "History File Not Found", MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (remove != DialogResult.Yes)
        {
            SetStatus("Missing History item retained.");
            return;
        }

        BridgeHistoryResult result = await _historyService.RemoveAsync(category, path);
        SetStatus(result.Success
            ? "Missing item removed from History."
            : $"History item could not be removed: {result.Error}");
        if (!result.Success)
            LogDebug($"HISTORY REMOVE FAILED category={category} path={path} " +
                $"error={result.Error}", force: true);
        RefreshHistoryMenu();
    }

    private async void ClearHistoryCategoryMenuItem_Click(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem { Tag: BridgeHistoryCategory category })
            return;
        string label = category == BridgeHistoryCategory.ControllerPak
            ? "Controller Pak" : "Transfer Pak";
        if (MessageBox.Show(this, $"Clear all {label} History entries?",
                $"Clear {label} History", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes) return;

        BridgeHistoryResult result = await _historyService.ClearAsync(category);
        SetStatus(result.Success ? $"{label} History cleared."
            : $"{label} History could not be cleared: {result.Error}");
        if (!result.Success)
            LogDebug($"HISTORY CLEAR FAILED category={category} error={result.Error}",
                force: true);
        RefreshHistoryMenu();
    }

    private async void ClearAllHistoryMenuItem_Click(object? sender, EventArgs e)
    {
        if (MessageBox.Show(this, "Clear all Controller Pak and Transfer Pak History?",
                "Clear All History", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes) return;

        BridgeHistoryResult result = await _historyService.ClearAllAsync();
        SetStatus(result.Success ? "All History cleared."
            : $"History could not be cleared: {result.Error}");
        if (!result.Success)
            LogDebug($"HISTORY CLEAR ALL FAILED error={result.Error}", force: true);
        RefreshHistoryMenu();
    }

    private void RefreshAuthoritativeHistoryPresentation()
    {
        ApplyAccessoryModeUi();
        UpdateFirmwareModeUi();
        RefreshHistoryMenu();
    }

    private async void SetNetworkPakWifiTransport(NetworkPakWifiTransport transport)
    {
        BridgeNetworkPakTransport requested =
            NetworkPakCarrierSelection.ToBridgeTransport(transport);
        try
        {
            BridgeNetworkPakCarrierResult result =
                await _networkPakCarrierService.SelectCarrierAsync(
                    requested, CancellationToken.None);
            if (BridgeNetworkPakCarrierTransitionPolicy.IsSelectable(
                    result.Snapshot.SelectedCarrier))
                _networkPakPresentationCarrier = result.Snapshot.SelectedCarrier;
            ApplyNetworkPakTransportUi();
            BridgeNetworkPakTransport effectiveCarrier =
                result.Snapshot.SelectedCarrier;
            LogDebug($"NETPAK CARRIER requested=" +
                $"{NetworkPakCarrierSelection.LogName(transport)} selected=" +
                $"{NetworkPakCarrierLogName(effectiveCarrier)} " +
                $"outcome={result.Outcome} failure={result.Failure}", force: true);
            if (result.Success)
            {
                BridgeDeviceStateSnapshot corrected = _bridgeDeviceState.Current;
                if (BridgeNetworkPakActivationRefreshPolicy.RequiresAccessoryRetry(
                        corrected))
                {
                    LogDebug("NETPAK activation refresh after carrier correction",
                        force: true);
                    BridgeAccessoryTransitionResult refresh =
                        await _accessoryService.SelectAccessoryAsync(
                            BridgeAccessoryMode.NetworkPak, CancellationToken.None);
                    LogDebug(refresh.Success
                        ? "NETPAK activation refresh accepted"
                        : $"NETPAK activation refresh failed={refresh.Failure} " +
                            refresh.Error, force: true);
                    if (!refresh.Success)
                    {
                        SetStatus($"Network Pak activation refresh failed: " +
                            $"{refresh.Error}");
                        return;
                    }
                }
                BridgeDeviceStateSnapshot finalState = _bridgeDeviceState.Current;
                string displayCarrier = NetworkPakCarrierDisplayName(effectiveCarrier);
                if (finalState.ActiveNetworkPakTransport == effectiveCarrier)
                    SetStatus($"Network Pak transport: {displayCarrier}; active.");
                else if (result.Outcome ==
                        BridgeNetworkPakCarrierOutcome.ActivationPendingHandshake ||
                    finalState.ActiveAccessory == BridgeAccessoryMode.NetworkPak)
                    SetStatus($"Network Pak transport: {displayCarrier}; awaiting handshake.");
                else
                    SetStatus($"Network Pak transport: {displayCarrier}; selected, inactive.");
            }
            else
            {
                SetStatus($"Network Pak transport " +
                    $"{NetworkPakCarrierSelection.DisplayName(transport)} failed: " +
                    $"{result.Diagnostic ?? result.Failure.ToString()}");
            }
        }
        catch (Exception ex)
        {
            ApplyNetworkPakTransportUi();
            LogDebug($"NETPAK CARRIER selection unexpected failure {ex}", force: true);
            SetStatus($"Network Pak transport selection failed: {ex.Message}");
        }
    }

    private void ApplyNetworkPakTransportUi()
    {
        BridgeNetworkPakConnectionPolicy policy =
            BridgeNetworkPakConnectionPolicyResolver.Resolve(
                _bridgeDeviceState.Current);
        if (_networkPakUsbTransportMenuItem is not null)
        {
            _networkPakUsbTransportMenuItem.Checked =
                PresentedNetworkPakCarrier == BridgeNetworkPakTransport.UsbSerial;
            _networkPakUsbTransportMenuItem.Enabled = policy.IsAllowed(
                BridgeNetworkPakTransport.UsbSerial);
        }
        if (_networkPakTcpTransportMenuItem is not null)
        {
            _networkPakTcpTransportMenuItem.Checked =
                PresentedNetworkPakCarrier == BridgeNetworkPakTransport.Tcp;
            _networkPakTcpTransportMenuItem.Enabled = policy.IsAllowed(
                BridgeNetworkPakTransport.Tcp);
        }
        if (_networkPakQuickTransportMenuItem is not null)
        {
            _networkPakQuickTransportMenuItem.Checked =
                PresentedNetworkPakCarrier ==
                    BridgeNetworkPakTransport.ReliableUdpQuick;
            _networkPakQuickTransportMenuItem.Enabled = policy.IsAllowed(
                BridgeNetworkPakTransport.ReliableUdpQuick);
        }
    }

    private async Task ReconcileNetworkPakConnectionPolicyAsync()
    {
        BridgeDeviceStateSnapshot before = _bridgeDeviceState.Current;
        BridgeNetworkPakConnectionPolicy policy =
            BridgeNetworkPakConnectionPolicyResolver.Resolve(before);
        LogDebug($"BRIDGE CONNECTION type={before.ControlConnectionKind}", force: true);
        LogDebug($"NETPAK POLICY allowed=" +
            $"{string.Join(',', policy.AllowedCarriers)} default=" +
            $"{NetworkPakCarrierLogName(policy.DefaultCarrier)}", force: true);

        BridgeNetworkPakCarrierResult result =
            await _networkPakCarrierService.ReconcileConnectionAsync(
                CancellationToken.None);
        BridgeNetworkPakTransport after = result.Snapshot.SelectedCarrier;
        if (before.SelectedNetworkPakTransport != after)
        {
            LogDebug($"NETPAK POLICY normalized old=" +
                $"{NetworkPakCarrierLogName(before.SelectedNetworkPakTransport)} new=" +
                $"{NetworkPakCarrierLogName(after)}", force: true);
        }
        if (BridgeNetworkPakCarrierTransitionPolicy.IsSelectable(after))
            _networkPakPresentationCarrier = after;
        ApplyNetworkPakTransportUi();
        if (!result.Success)
        {
            LogDebug($"NETPAK POLICY reconciliation failed={result.Failure} " +
                $"{result.Diagnostic}", force: true);
        }
    }

    private async void NetworkPakModuleMenuItem_Click(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem { Tag: string moduleId })
        {
            return;
        }

        if (!TryBeginDesktopOperation(BridgeOperationKind.NetworkPakModuleSelection,
                "NetworkPakModule", BridgeOperationCancellationCapability.BeforeCommitOnly,
                out BridgeOperationLease? operation))
            return;

        using BridgeOperationLease lease = operation!;
        BridgeNetworkPakModuleSelectionResult result =
            await _networkPakModuleService.SelectModuleAsync(moduleId);
        CompleteDesktopOperation(lease, result.Success,
            result.Failure == BridgeNetworkPakModuleSelectionFailure.Cancelled,
            result.Failure.ToString(), result.Error, result);
        ApplyNetworkPakModuleUi();
        SetStatus(result.Success
            ? result.FallbackUsed
                ? $"Network Pak module fallback: {result.SelectedModuleName}."
                : $"Network Pak module: {result.SelectedModuleName}."
            : $"Network Pak module selection failed ({result.Failure}): {result.Error}");
    }

    private void ApplyNetworkPakModuleUi()
    {
        BridgeNetworkPakModuleSnapshot snapshot = _networkPakModuleService.Current;
        foreach (ToolStripMenuItem item in _networkPakModuleMenuItems)
        {
            item.Checked = item.Tag is string id &&
                string.Equals(id, snapshot.ActiveModuleId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ApplyDebugModeUi()
    {
        if (_settingsDebugModeMenuItem is not null)
        {
            _settingsDebugModeMenuItem.Checked = _debugModeEnabled;
        }

        bool connected = _serialPort?.IsOpen == true;
        forceLowButton.Visible = _debugModeEnabled;
        forceHighButton.Visible = _debugModeEnabled;
        forceOffButton.Visible = _debugModeEnabled;
        n64StatButton.Visible = _debugModeEnabled;
        n64ClearDebugButton.Visible = _debugModeEnabled;

        forceLowButton.Enabled = _debugModeEnabled && _profile.Type == ConsoleControllerType.Snes;
        forceHighButton.Enabled = _debugModeEnabled && _profile.Type == ConsoleControllerType.Snes;
        forceOffButton.Enabled = _debugModeEnabled && (connected || _profile.Type == ConsoleControllerType.Snes);
        n64StatButton.Enabled = _debugModeEnabled && connected && _profile.Type == ConsoleControllerType.N64;
        n64ClearDebugButton.Enabled = _debugModeEnabled && connected && _profile.Type == ConsoleControllerType.N64;

        if (_firmwareModeGroupBox is not null)
        {
            _firmwareModeGroupBox.Visible = _debugModeEnabled;
        }
    }

    private void ApplyOnScreenControlsUi()
    {
        if (_settingsOnScreenControlsMenuItem is not null)
        {
            _settingsOnScreenControlsMenuItem.Checked = _onScreenControlsEnabled;
        }

        buttonPad.Visible = _onScreenControlsEnabled;
    }

    private void InitializeConsoleSelector()
    {
        _initializingUi = true;
        consoleComboBox.Items.Add(new ConsoleItem(ConsoleControllerType.Snes, "SNES"));
        consoleComboBox.Items.Add(new ConsoleItem(ConsoleControllerType.N64, "N64"));
        ConsoleControllerType initialType = ParseConsoleMode(_settings.ConsoleMode);
        consoleComboBox.SelectedIndex = initialType == ConsoleControllerType.N64 ? 1 : 0;
        _profile = ControllerProfile.Create(initialType);
        _initializingUi = false;
    }

    private void CreateButtonPad()
    {
        buttonPad.SuspendLayout();
        buttonPad.Controls.Clear();

        foreach (ControllerButtonDefinition definition in _profile.Buttons)
        {
            ControllerHotspotButton button = new()
            {
                Text = definition.Name,
                Tag = definition.Bit,
                Bounds = definition.Bounds,
                Enabled = false
            };

            button.MouseDown += ControllerButton_MouseDown;
            button.MouseUp += ControllerButton_MouseUp;
            button.MouseLeave += ControllerButton_MouseUp;
            buttonPad.Controls.Add(button);
        }

        buttonPad.ResumeLayout();
        buttonPad.Invalidate();
    }

    private void LoadMapping()
    {
        string path = ControllerProfile.GetMappingPath(_profile.Type);
        if (_profile.Type == ConsoleControllerType.Snes
            && !File.Exists(path)
            && File.Exists(Path.Combine(AppContext.BaseDirectory, "controller-mapping.json")))
        {
            path = Path.Combine(AppContext.BaseDirectory, "controller-mapping.json");
        }

        _mapping = ControllerMapping.Load(path);
        _eightBitDoParsecMapping = ControllerMapping.LoadEightBitDoParsec(
            Path.Combine(AppContext.BaseDirectory, "controller-mapping-n64-8bitdo-parsec.json"));
    }

    private void RefreshPorts(string? preferredPort = null)
    {
        preferredPort ??= portComboBox.Text.Trim();
        portComboBox.Items.Clear();

        string[] ports = SerialPort.GetPortNames()
            .OrderBy(port => port)
            .ToArray();

        portComboBox.Items.AddRange(ports);

        if (ports.Length > 0)
        {
            string? matchingPort = ports.FirstOrDefault(port =>
                port.Equals(preferredPort, StringComparison.OrdinalIgnoreCase));
            if (matchingPort is not null)
            {
                portComboBox.SelectedItem = matchingPort;
            }
            else if (!string.IsNullOrWhiteSpace(preferredPort))
            {
                portComboBox.Text = preferredPort;
            }
            else
            {
                portComboBox.SelectedItem = ports.OrderByDescending(GetPortNumber).First();
            }
        }
        else
        {
            portComboBox.Text = string.IsNullOrWhiteSpace(preferredPort) ? "COM5" : preferredPort;
        }
    }

    private void RefreshVideoDevices()
    {
        string? previousSelection = videoDeviceComboBox.SelectedItem?.ToString();
        videoDeviceComboBox.Items.Clear();

        try
        {
            DsDevice[] devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            for (int index = 0; index < devices.Length; index++)
            {
                videoDeviceComboBox.Items.Add($"{index} - {devices[index].Name}");
            }

            if (videoDeviceComboBox.Items.Count == 0)
            {
                videoDeviceComboBox.Items.Add("0 - Default video device");
            }

            int previousIndex = previousSelection is null
                ? -1
                : videoDeviceComboBox.Items
                    .Cast<object>()
                    .Select((item, index) => new { Text = item.ToString(), Index = index })
                    .FirstOrDefault(item => item.Text == previousSelection)?.Index ?? -1;

            videoDeviceComboBox.SelectedIndex = previousIndex >= 0
                ? previousIndex
                : FindLikelyCaptureDeviceIndex();
        }
        catch (Exception ex)
        {
            videoDeviceComboBox.Items.Add("0 - Default video device");
            videoDeviceComboBox.SelectedIndex = 0;
            SetStatus($"Could not enumerate video devices: {ex.Message}");
        }
    }

    private void RefreshAudioDevices()
    {
        string? previousSelection = audioDeviceComboBox.SelectedItem?.ToString();
        audioDeviceComboBox.Items.Clear();

        try
        {
            DsDevice[] devices = DsDevice.GetDevicesOfCat(FilterCategory.AudioInputDevice);
            for (int index = 0; index < devices.Length; index++)
            {
                audioDeviceComboBox.Items.Add($"{index} - {devices[index].Name}");
            }

            if (audioDeviceComboBox.Items.Count == 0)
            {
                audioDeviceComboBox.Items.Add("0 - No audio input devices");
            }

            int previousIndex = previousSelection is null
                ? -1
                : audioDeviceComboBox.Items
                    .Cast<object>()
                    .Select((item, index) => new { Text = item.ToString(), Index = index })
                    .FirstOrDefault(item => item.Text == previousSelection)?.Index ?? -1;

            audioDeviceComboBox.SelectedIndex = previousIndex >= 0
                ? previousIndex
                : FindLikelyAudioCaptureDeviceIndex();
        }
        catch (Exception ex)
        {
            audioDeviceComboBox.Items.Add("0 - No audio input devices");
            audioDeviceComboBox.SelectedIndex = 0;
            SetStatus($"Could not enumerate audio devices: {ex.Message}");
        }
    }

    private void RefreshCaptureDevices()
    {
        RefreshVideoDevices();
        RefreshAudioDevices();
    }

    private int FindLikelyCaptureDeviceIndex()
    {
        string[] likelyNames =
        [
            "usb streaming",
            "usb 3",
            "hdmi",
            "capture",
            "video"
        ];

        for (int index = 0; index < videoDeviceComboBox.Items.Count; index++)
        {
            string item = videoDeviceComboBox.Items[index]?.ToString()?.ToLowerInvariant() ?? string.Empty;
            if (likelyNames.Any(item.Contains))
            {
                return index;
            }
        }

        return 0;
    }

    private int FindLikelyAudioCaptureDeviceIndex()
    {
        string[] likelyNames =
        [
            "usb3.0 audio",
            "usb 3",
            "digital audio interface",
            "hdmi",
            "capture",
            "usb audio"
        ];

        for (int index = 0; index < audioDeviceComboBox.Items.Count; index++)
        {
            string item = audioDeviceComboBox.Items[index]?.ToString()?.ToLowerInvariant() ?? string.Empty;
            if (likelyNames.Any(item.Contains))
            {
                return index;
            }
        }

        return 0;
    }

    private void TryAcquireFirstGamepad()
    {
        try
        {
            lock (_joystickLock)
            {
                _directInput?.Dispose();
                _directInput = new DirectInput();
                DeviceInstance? device = _directInput
                    .GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
                    .OrderBy(item => item.InstanceName)
                    .FirstOrDefault();

                if (device is null)
                {
                    return;
                }

                ReleaseJoystick();
                _joystick = new Joystick(_directInput, device.InstanceGuid);
                _joystick.SetCooperativeLevel(Handle, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                _joystick.Properties.BufferSize = 128;
                _joystick.Acquire();
                _directInputDeviceId = device.InstanceGuid.ToString("N");
                _directInputDeviceName = device.InstanceName;
                LoadActiveAnalogCalibration();
                SetStatus($"USB controller ready: {device.InstanceName}");
            }
        }
        catch
        {
            ReleaseJoystick();
        }
    }

    private void TryAcquireXInputController()
    {
        foreach (UserIndex userIndex in new[] { UserIndex.One, UserIndex.Two, UserIndex.Three, UserIndex.Four })
        {
            Controller controller = new(userIndex);
            if (!controller.IsConnected)
            {
                continue;
            }

            _xinputController = controller;
            _xinputDeviceId = $"xinput-{userIndex}";
            _xinputDeviceName = $"XInput {userIndex}";
            LoadActiveAnalogCalibration();
            SetStatus($"XInput controller ready: {userIndex}");
            return;
        }

        _xinputController = null;
        _xinputDeviceId = "";
        _xinputDeviceName = "";
        LoadActiveAnalogCalibration();
    }

    private void ConfigureBridgeConnection()
    {
        if (IsBridgeConnected)
        {
            SetStatus("Disconnect before changing the Bridge transport.");
            return;
        }

        using BridgeConnectionDialog dialog = new(_connectionKind, _networkAddress, _networkPort);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _connectionKind = dialog.ConnectionKind;
        _networkAddress = dialog.Address;
        _networkPort = dialog.Port;
        ApplyConnectionSelectionUi();
    }

    private void ApplyConnectionSelectionUi()
    {
        bool network = _connectionKind == BridgeConnectionKind.NetworkTcp;
        portLabel.Text = network ? "NICE64 IP" : "COM port";
        refreshButton.Enabled = !network && !IsBridgeConnected;
        if (network)
        {
            portComboBox.Text = _networkAddress;
        }
        else
        {
            RefreshPorts();
        }
    }

    private bool IsBridgeConnected =>
        _tcpSession.Current?.IsConnected == true || _serialPort?.IsOpen == true;

    private IBridgeLineTransport? ActiveLineTransportLocked()
    {
        TcpBridgeTransport? tcp = _tcpSession.Active;
        return tcp?.IsConnected == true
            ? tcp
             : _serialPort?.IsOpen == true ? _serialBridgeTransport : null;
    }

    private IBridgeLineTransport? SelectedNetworkPakLineTransportLocked(
        BridgeNetworkPakTransport carrier)
    {
        return carrier switch
        {
            BridgeNetworkPakTransport.UsbSerial
                when _serialPort?.IsOpen == true &&
                    _serialBridgeTransport?.IsConnected == true => _serialBridgeTransport,
            BridgeNetworkPakTransport.Tcp
                when _tcpSession.Active?.IsConnected == true => _tcpSession.Active,
            _ => null
        };
    }

    private static BridgeNetworkPakTransport LineCarrier(IBridgeLineTransport transport) =>
        transport is TcpBridgeTransport
            ? BridgeNetworkPakTransport.Tcp
            : transport is SerialBridgeTransport
                ? BridgeNetworkPakTransport.UsbSerial
                : BridgeNetworkPakTransport.None;

    private async void ConnectButton_Click(object? sender, EventArgs e)
    {
        if (_bridgeDeviceState.Current.ControlConnectionState == BridgeConnectionState.Connected)
        {
            await DisconnectFromUiAsync();
            return;
        }

        if (!TryBeginDesktopOperation(BridgeOperationKind.Connect, "Connection",
                BridgeOperationCancellationCapability.Cooperative,
                out BridgeOperationLease? operation))
            return;
        using BridgeOperationLease lease = operation!;

        BridgeConnectionRequest request;
        if (_connectionKind == BridgeConnectionKind.NetworkTcp)
        {
            string address = portComboBox.Text.Trim();
            request = new(BridgeControlConnectionKind.WifiTcp, address, _networkPort,
                TimeSpan.FromSeconds(5));
            SetStatus($"Connecting to NICE64 at {address}:{_networkPort}...");
        }
        else
        {
            request = new(BridgeControlConnectionKind.SerialUsb,
                portComboBox.Text.Trim());
            SetStatus($"Connecting to {request.Endpoint}...");
        }

        connectButton.Enabled = false;
        BridgeConnectionResult result = await _connectionService.ConnectAsync(
            request, CancellationToken.None);
        connectButton.Enabled = true;
        if (!result.Success)
        {
            CompleteDesktopOperation(lease, false,
                result.Failure == BridgeConnectionFailure.Cancelled,
                result.Failure.ToString(), result.Error, result);
            SetStatus($"NICE64 connection failed: {result.Error}");
            return;
        }

        ApplyBridgeInstanceNameAfterConnect(request);

        if (result.ConnectionKind == BridgeControlConnectionKind.SerialUsb)
        {
            lock (_maskLock)
            {
                _manualMask = 0;
                _gamepadMask = 0;
                _gamepadAnalogActive = false;
                _gamepadAnalogX = 0;
                _gamepadAnalogY = 0;
                Array.Clear(_gamepadReleaseTicks);
            }
            SetConnectedState(result.DisplayEndpoint, queryFirmware: false);
            await ReconcileNetworkPakConnectionPolicyAsync();
            await QueryFirmwareModeCoreAsync(lease);
            lease.Succeed(result);
            return;
        }

        _activeFirmwareMode = result.FirmwarePersonality;
        _firmwareModeState.Connected();
        _firmwareModeState.Ready(_activeFirmwareMode);
        SetConnectedState(result.DisplayEndpoint, queryFirmware: false);
        await ReconcileNetworkPakConnectionPolicyAsync();
        ApplyVerifiedFirmwareModeUi();
        SetStatus($"Connected to {result.DisplayEndpoint}: " +
            $"{result.FirmwarePersonality} verified.");
        lease.Succeed(result);
    }

    private void ApplyBridgeInstanceNameAfterConnect(BridgeConnectionRequest request)
    {
        string name = BridgeInstanceNamingPolicy.ResolveConnectName(
            request.ConnectionKind,
            request.Endpoint,
            _askToNameSessionWhenConnecting,
            endpointDefault => Microsoft.VisualBasic.Interaction.InputBox(
                "Optionally name this connected NICE64 session. " +
                "Cancel or leave the value blank to use the endpoint-derived default.",
                "Name NICE64 Session",
                endpointDefault));
        _bridgeInstance.SetName(name);
        BridgeSettings.SaveBridgeInstanceName(_settingsPath, name);
        UpdateApplicationBranding();
    }

    private async Task<BridgeControlSession> OpenTcpControlSessionAsync(
        string addressText, int port, TimeSpan timeout, CancellationToken token)
    {
        if (!Nice64NetworkEndpoint.TryValidate(addressText, port,
                out System.Net.IPAddress? address, out string error))
            throw new ArgumentException(error, nameof(addressText));

        TcpBridgeTransport transport;
        Nice64TcpHandshake handshake;
        try
        {
            (transport, handshake) = await TcpBridgeTransport.ConnectVerifiedAsync(
                address!, port, timeout, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out connecting to NICE64 at {addressText}:{port}.");
        }
        lock (_serialLock)
        {
            _tcpSession.InstallFresh(transport);
            _serialReceiveBuffer = string.Empty;
        }
        _networkAddress = addressText;
        LogDebug($"TCP FRESH CONNECT endpoint={transport.DisplayName} " +
            $"mode={handshake.Mode.Role}/{handshake.Mode.Service} caps={handshake.Capabilities}",
            force: true);
        return new(BridgeControlConnectionKind.WifiTcp,
            $"NICE64 {transport.DisplayName}", "NICE64", string.Empty,
            handshake.Mode,
            NetworkPakCarrierSelectionForSession(),
            FirmwareModeProtocol.ParseCapabilities(handshake.Capabilities));
    }

    private async Task<BridgeControlSession> OpenSerialControlSessionAsync(
        string portName, CancellationToken cancellationToken)
    {
        SerialPort? serial = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SerialPort.GetPortNames().Contains(
                    portName, StringComparer.OrdinalIgnoreCase))
                throw new BridgeSerialPortUnavailableException(
                    $"Serial port {portName} is not currently available.");
            serial = new SerialPort(portName, _settings.BaudRate)
            {
                NewLine = "\n",
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = true,
                RtsEnable = true
            };
            await Task.Run(serial.Open, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_serialLock)
            {
                CloseSerialPortLocked();
                _serialPort = serial;
                _serialBridgeTransport = new SerialBridgeTransport(serial);
                serial = null;
                _serialReceiveBuffer = string.Empty;
            }

            LogDebug($"SERIAL SESSION OPEN port={portName} console={_profile.DisplayName} " +
                $"accessory={ActiveAccessory} input={_inputSourceMode}", force: true);
            return new(BridgeControlConnectionKind.SerialUsb, portName,
                "NICE64", string.Empty, null,
                NetworkPakCarrierSelectionForSession(),
                BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.DeviceReported));
        }
        catch
        {
            try
            {
                serial?.Dispose();
            }
            catch
            {
            }
            throw;
        }
    }

    private void BeginFirmwareModeQuery()
    {
        if (!TryBeginDesktopOperation(BridgeOperationKind.FirmwareQuery, "Firmware",
                BridgeOperationCancellationCapability.Cooperative,
                out BridgeOperationLease? operation))
            return;

        _ = QueryFirmwareModeAsync(operation!);
    }

    private async Task QueryFirmwareModeAsync(BridgeOperationLease operation)
    {
        using (operation)
        {
            bool success = await QueryFirmwareModeCoreAsync(operation);
            if (success)
                operation.Succeed(_bridgeDeviceState.Current.FirmwarePersonality);
            else
                operation.Fail("QueryFailed", "Firmware mode query failed.");
        }
    }

    private async Task<bool> QueryFirmwareModeCoreAsync(BridgeOperationLease? operation)
    {
        operation?.ReportProgress("Querying");
        SuspendControllerDelivery("firmware-query");
        _firmwareModeState.Querying();
        UpdateFirmwareModeUi();

        try
        {
            (FirmwareModeInfo Mode, BridgeCapabilitySet Capabilities) result = await Task.Run(() =>
            {
                lock (_serialLock)
                {
                    SerialPort serial = GetOpenSerialPort();
                    FirmwareModeInfo queried = QueryFirmwareModeLocked(serial, TimeSpan.FromSeconds(2), 1000);
                    BridgeCapabilitySet reported = QueryFirmwareCapabilitiesLocked(serial);
                    LogDebug($"FIRMWARE MODE query result={queried.Role}/{queried.Service}", force: true);
                    return (queried, reported);
                }
            });

            FirmwareModeInfo mode = result.Mode;
            _activeFirmwareMode = mode.Personality;
            StateUpdater.UpdateFirmwareMode(mode, result.Capabilities);
            _firmwareModeState.Ready(mode.Personality);
            SetStatus($"Firmware mode: {mode.DisplayName}.");
            operation?.ReportProgress("Verified", domainProgress: mode);
            return true;
        }
        catch (Exception ex)
        {
            _activeFirmwareMode = FirmwarePersonality.Unknown;
            _firmwareModeState.Failed();
            LogDebug($"FIRMWARE MODE query failed: {ex.Message}", force: true);
            SetStatus($"Firmware mode unknown: {ex.Message}");
            return false;
        }
        finally
        {
            await ResumeControllerDeliveryAsync(
                resendCurrent: false, "firmware-query", CancellationToken.None);
            UpdateFirmwareModeUi();
            ApplyVerifiedFirmwareModeUi();
        }
    }

    private void BeginFirmwareModeSwitch(FirmwarePersonality target)
    {
        if (!IsBridgeConnected)
        {
            SetStatus("Connect to the NICE64 Pico first.");
            return;
        }

        if (!TryBeginDesktopOperation(BridgeOperationKind.FirmwareModeSwitch, "Firmware",
                BridgeOperationCancellationCapability.BeforeCommitOnly,
                out BridgeOperationLease? operation))
            return;

        _ = SwitchFirmwareModeAsync(target, operation!);
    }

    private async Task<bool> SwitchFirmwareModeAsync(FirmwarePersonality target,
        BridgeOperationLease operation)
    {
        using BridgeOperationLease lease = operation;
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (target != _activeFirmwareMode &&
            (_activeFirmwareMode is FirmwarePersonality.Keyboard or
                FirmwarePersonality.Mouse))
        {
            await ReleaseInputCaptureAsync("firmware personality change");
            await _keyboardMouseInput.ClearAndSendNeutralAsync();
        }
        if (target != FirmwarePersonality.Controller)
        {
            SetXInputRumble(false);
        }

        lease.ReportProgress("TransitionPending", domainProgress: target);
        SuspendControllerDelivery("firmware-mode-switch");
        _firmwareModeState.Requesting(target);
        ApplyAccessoryModeUi();
        UpdateFirmwareModeUi();
        LogDebug($"FIRMWARE MODE target={target}", force: true);

        bool success = false;
        string finalStatus = string.Empty;

        try
        {
            FirmwareModeTransitionResult result = await _firmwareService.SelectModeAsync(
                target, CancellationToken.None);
            if (!result.Success || result.VerifiedMode is null)
            {
                _firmwareModeState.Failed();
                finalStatus = $"Firmware mode switch failed: {result.Error}";
                LogDebug($"FIRMWARE MODE failed target={target} failure={result.Failure} " +
                    $"error={result.Error}", force: true);
                CompleteDesktopOperation(lease, false,
                    result.Failure == FirmwareModeTransitionFailure.Cancelled,
                    result.Failure.ToString(), result.Error, result);
                return false;
            }

            _activeFirmwareMode = result.VerifiedMode.Personality;
            _firmwareModeState.Ready(_activeFirmwareMode);
            _accessoryPersistence.TryDeactivateClean();
            success = true;
            finalStatus = result.RequestSent
                ? $"{result.VerifiedMode.DisplayName} firmware active."
                : $"{result.VerifiedMode.DisplayName} firmware already active.";
            lease.Succeed(result);
            return true;
        }
        finally
        {
            stopwatch.Stop();
            await ResumeControllerDeliveryAsync(
                resendCurrent: false, "firmware-mode-switch", CancellationToken.None);
            LogDebug($"FIRMWARE MODE complete success={success} target={target} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}", force: true);
            UpdateFirmwareModeUi();
            SetStatus(success
                ? $"{finalStatus} Switch took {stopwatch.ElapsedMilliseconds} ms."
                : finalStatus);
            ApplyVerifiedFirmwareModeUi();
        }
    }

    private IFirmwareModeControlPath SelectFirmwareControlPath() =>
        _tcpSession.Current?.IsConnected == true && _serialPort?.IsOpen != true
            ? _tcpFirmwareControlPath
            : _serialFirmwareControlPath;

    private FirmwareModeInfo QuerySerialFirmwareMode(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_serialLock)
        {
            token.ThrowIfCancellationRequested();
            SerialPort serial = GetOpenSerialPort();
            serial.DiscardInBuffer();
            FirmwareModeInfo mode = QueryFirmwareModeLocked(
                serial, TimeSpan.FromSeconds(2), 1000);
            LogDebug($"FIRMWARE MODE current={mode.Role}/{mode.Service}", force: true);
            return mode;
        }
    }

    private FirmwareModeInfo QueryTcpFirmwareMode(CancellationToken token)
    {
        lock (_serialLock)
        {
            TcpBridgeTransport transport = _tcpSession.Current
                ?? throw new IOException("Wi-Fi/TCP control connection is unavailable.");
            transport.WriteLine("MODE?");
            string line = ReadMatchingTcpControlLineLocked(
                transport,
                candidate => FirmwareModeProtocol.TryParseMode(candidate, out _),
                TimeSpan.FromSeconds(2), token);
            _ = FirmwareModeProtocol.TryParseMode(line, out FirmwareModeInfo mode);
            return mode;
        }
    }

    private FirmwareModeRequestResult SendTcpFirmwareModeRequest(
        FirmwarePersonality target, CancellationToken token)
    {
        lock (_serialLock)
        {
            token.ThrowIfCancellationRequested();
            TcpBridgeTransport transport = _tcpSession.Current
                ?? throw new IOException("Wi-Fi/TCP control connection is unavailable.");
            transport.WriteLine(FirmwareModeProtocol.RequestCommand(target));
            string line = ReadMatchingTcpControlLineLocked(
                transport,
                candidate => FirmwareModeProtocol.ParseRequestResponse(candidate).Kind !=
                    FirmwareModeRequestKind.Unknown,
                TimeSpan.FromSeconds(3), token);
            return FirmwareModeProtocol.ParseRequestResponse(line);
        }
    }

    private string ReadMatchingTcpControlLineLocked(
        TcpBridgeTransport transport,
        Predicate<string> expected,
        TimeSpan timeout,
        CancellationToken token) =>
        transport.ReadLineMatching(timeout, token, expected, line =>
        {
            if (!HandleNetworkPakLineLocked(transport, line))
                LogDebug($"TCP CONTROL ignored unrelated line: {line}", force: true);
        });

    private async Task PrepareTcpControlSessionReplacementAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        BridgeNetworkPakCarrierResult retirement =
            await _networkPakCarrierService.RetirePhysicalCarrierAsync(
                "TCP firmware/control replacement", token).ConfigureAwait(false);
        if (!retirement.Success)
        {
            LogDebug($"NETPAK CARRIER replacement retirement failure=" +
                $"{retirement.Failure} {retirement.Diagnostic}", force: true);
        }
        ClearAllNetworkPakLineActivation();
        lock (_serialLock) _tcpSession.BeginTransitionRetirement();
        RunOnUiThread(() => SetStatus(
            "NICE64 is rebooting; reconnecting in about 20 seconds..."));
    }

    private async Task<BridgeControlSession> ReplaceTcpControlSessionAttemptAsync(
        int attempt, CancellationToken token)
    {
        LogDebug($"FIRMWARE MODE Wi-Fi reconnect attempt={attempt}/" +
            $"{FirmwareModeReconnectPolicy.AttemptCount}", force: true);
        return await OpenTcpControlSessionAsync(
            _networkAddress, _networkPort, TimeSpan.FromSeconds(2), token)
            .ConfigureAwait(false);
    }

    private void EndTcpControlSessionReplacementWithoutSession()
    {
        lock (_serialLock) _tcpSession.EndTransitionWithoutReplacement();
    }

    private FirmwareModeRequestResult SendFirmwareModeRequest(
        FirmwarePersonality target, CancellationToken token)
    {
        try
        {
            lock (_serialLock)
            {
                token.ThrowIfCancellationRequested();
                SerialPort serial = GetOpenSerialPort();
                serial.DiscardInBuffer();
                string command = FirmwareModeProtocol.RequestCommand(target);
                LogDebug($"FIRMWARE MODE send {command}", force: true);
                serial.WriteLine(command);
                string line = ReadMatchingLine(
                    serial,
                    candidate => FirmwareModeProtocol.ParseRequestResponse(candidate).Kind != FirmwareModeRequestKind.Unknown,
                    TimeSpan.FromSeconds(2),
                    500);
                FirmwareModeRequestResult result = FirmwareModeProtocol.ParseRequestResponse(line);
                LogDebug($"FIRMWARE MODE response {line}", force: true);
                return result;
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            LogDebug($"FIRMWARE MODE request interrupted: {ex.Message}", force: true);
            return new FirmwareModeRequestResult(FirmwareModeRequestKind.Unknown, null);
        }
    }

    private async Task<BridgeControlSession> ReplaceSerialControlSessionAsync(
        string? preferredPort,
        FirmwarePersonality target,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        BridgeNetworkPakCarrierResult retirement =
            await _networkPakCarrierService.RetirePhysicalCarrierAsync(
                "Serial firmware/control replacement", token).ConfigureAwait(false);
        if (!retirement.Success)
        {
            LogDebug($"NETPAK CARRIER replacement retirement failure=" +
                $"{retirement.Failure} {retirement.Diagnostic}", force: true);
        }
        ClearAllNetworkPakLineActivation();
        lock (_serialLock)
        {
            CloseSerialPortLocked();
            _serialReceiveBuffer = string.Empty;
        }

        LogDebug("FIRMWARE MODE expected Pico reboot/disconnect", force: true);
        FirmwareModeInfo mode = WaitForNice64Reconnect(
            preferredPort, target, TimeSpan.FromSeconds(10), token, out string portName);
        if (mode.Personality == target)
        {
            LogDebug($"FIRMWARE MODE reconnected port={portName} verified={mode.Role}/{mode.Service}", force: true);
        }
        return new BridgeControlSession(
            BridgeControlConnectionKind.SerialUsb, portName, "NICE64", string.Empty,
            mode, NetworkPakCarrierSelectionForSession(),
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.DeviceReported));
    }

    private FirmwareModeInfo WaitForNice64Reconnect(
        string? preferredPort,
        FirmwarePersonality target,
        TimeSpan timeout,
        CancellationToken token,
        out string connectedPort)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        string? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            string[] ports = SerialPort.GetPortNames().OrderBy(port => port).ToArray();
            List<string> candidates = [];
            if (!string.IsNullOrWhiteSpace(preferredPort) &&
                ports.Contains(preferredPort, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(preferredPort);
            }

            candidates.AddRange(ports.Where(port => !candidates.Contains(port, StringComparer.OrdinalIgnoreCase)));
            List<(string Port, FirmwareModeInfo Mode)> validDevices = [];

            foreach (string portName in candidates)
            {
                if (!TryProbeNice64Port(portName, target, out FirmwareModeInfo mode, out string? error))
                {
                    lastError = error;
                    continue;
                }

                validDevices.Add((portName, mode));
                if (!string.IsNullOrWhiteSpace(preferredPort) &&
                    portName.Equals(preferredPort, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            if (validDevices.Count == 1)
            {
                connectedPort = validDevices[0].Port;
                try
                {
                    OpenSerialControlSessionAsync(connectedPort, token)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    if (token.WaitHandle.WaitOne(250)) token.ThrowIfCancellationRequested();
                    continue;
                }

                lock (_serialLock)
                {
                    token.ThrowIfCancellationRequested();
                    FirmwareModeInfo verified = QueryFirmwareModeLocked(GetOpenSerialPort(), TimeSpan.FromSeconds(2), 1000);
                    RefreshPorts(connectedPort);
                    return verified;
                }
            }

            if (validDevices.Count > 1)
            {
                throw new InvalidOperationException($"Multiple NICE64 devices responded: {string.Join(", ", validDevices.Select(item => item.Port))}.");
            }

            if (token.WaitHandle.WaitOne(250)) token.ThrowIfCancellationRequested();
        }

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(lastError)
            ? "Timed out waiting for NICE64 reconnect."
            : $"Timed out waiting for NICE64 reconnect. Last error: {lastError}");
    }

    private bool TryProbeNice64Port(string portName, FirmwarePersonality target, out FirmwareModeInfo mode, out string? error)
    {
        mode = new FirmwareModeInfo(FirmwarePersonality.Unknown, string.Empty, string.Empty);
        error = null;
        SerialPort? serial = null;

        try
        {
            serial = new SerialPort(portName, _settings.BaudRate)
            {
                NewLine = "\n",
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = true,
                RtsEnable = true
            };
            serial.Open();
            serial.DiscardInBuffer();
            serial.WriteLine("WHO");
            string who = ReadMatchingLine(serial, line => line == FirmwareModeProtocol.WhoIdentity, TimeSpan.FromSeconds(1), 500);
            serial.WriteLine("MODE?");
            string modeLine = ReadMatchingLine(serial, line => FirmwareModeProtocol.TryParseMode(line, out _), TimeSpan.FromSeconds(1), 500);
            if (!FirmwareModeProtocol.TryParseMode(modeLine, out mode))
            {
                error = $"Malformed MODE? reply on {portName}.";
                return false;
            }

            LogDebug($"FIRMWARE MODE probe port={portName} who={who} mode={mode.Role}/{mode.Service} target={target}", force: true);
            return true;
        }
        catch (Exception ex)
        {
            error = portName + ": " + ex.Message;
            return false;
        }
        finally
        {
            try
            {
                serial?.Close();
            }
            catch
            {
            }

            try
            {
                serial?.Dispose();
            }
            catch
            {
            }
        }
    }

    private static FirmwareModeInfo QueryFirmwareModeLocked(SerialPort serial, TimeSpan timeout, int readTimeoutMs)
    {
        serial.DiscardInBuffer();
        serial.WriteLine("MODE?");
        string line = ReadMatchingLine(serial, candidate => FirmwareModeProtocol.TryParseMode(candidate, out _), timeout, readTimeoutMs);
        if (!FirmwareModeProtocol.TryParseMode(line, out FirmwareModeInfo mode))
        {
            throw new InvalidOperationException($"Unexpected MODE? response: {line}");
        }

        return mode;
    }

    private BridgeCapabilitySet QueryFirmwareCapabilitiesLocked(SerialPort serial)
    {
        try
        {
            serial.WriteLine("CAPS?");
            string line = ReadMatchingLine(serial,
                candidate => candidate.StartsWith("CAPS|", StringComparison.Ordinal),
                TimeSpan.FromSeconds(1), 500);
            return FirmwareModeProtocol.ParseCapabilities(line);
        }
        catch (Exception ex)
        {
            LogDebug($"FIRMWARE CAPS query unavailable: {ex.Message}", force: true);
            return BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.DeviceReported);
        }
    }

    private void ApplyVerifiedFirmwareModeUi()
    {
        SynchronizeKeyboardMouseCapture();
        _ = ApplyVerifiedFirmwareAccessoryStateAsync();
    }

    private void SynchronizeKeyboardMouseCapture()
    {
        FirmwarePersonality desired = IsBridgeConnected &&
            (_firmwareModeState.State is FirmwareModeSwitchState.ReadyKeyboard or
                FirmwareModeSwitchState.ReadyMouse) &&
            _activeFirmwareMode is FirmwarePersonality.Keyboard or FirmwarePersonality.Mouse
                ? _activeFirmwareMode
                : FirmwarePersonality.Unknown;
        if (_keyboardMouseInput.ActiveMode != desired)
        {
            if (_keyboardMouseInput.CaptureActive)
            {
                _ = ReleaseInputCaptureAsync("firmware personality changed");
                return;
            }
            _rawInput.SetCaptureMode(WindowsRawInputCaptureMode.Released);
            _keyboardMouseInput.SetActiveMode(desired);
            LogDebug($"WINDOWS HID capture mode={desired}", force: true);
        }
        UpdateInputCaptureUi();
    }

    private async Task ApplyVerifiedFirmwareAccessoryStateAsync()
    {
        try
        {
            FirmwarePersonality activeFirmware =
                _bridgeState.GetSnapshot().Device.FirmwarePersonality;
            await _accessoryService.ApplyVerifiedFirmwareAsync(
                activeFirmware, CancellationToken.None);
            if (activeFirmware == FirmwarePersonality.Network &&
                ActiveAccessory == BridgeAccessoryMode.NetworkPak &&
                IsBridgeConnected)
            {
                BridgeNetworkPakCarrierResult carrier =
                    await _networkPakCarrierService.ActivateSelectedAsync(
                        CancellationToken.None);
                if (!carrier.Success)
                {
                    LogDebug($"NETPAK CARRIER post-replacement activation failure=" +
                        $"{carrier.Failure} {carrier.Diagnostic}", force: true);
                    SetStatus($"Network Pak carrier restore failed: " +
                        $"{carrier.Diagnostic ?? carrier.Failure.ToString()}");
                }
            }
            if (activeFirmware == FirmwarePersonality.Controller && IsBridgeConnected)
                SendMask();
        }
        catch (Exception ex)
        {
            LogDebug($"ACCESSORY firmware synchronization failed: {ex.Message}", force: true);
            SetStatus($"Accessory restore failed: {ex.Message}");
        }
        finally
        {
            ApplyAccessoryModeUi();
        }
    }

    private void UpdateFirmwareModeUi()
    {
        RunOnUiThread(() =>
        {
            string modeText = _activeFirmwareMode switch
            {
                FirmwarePersonality.Controller => "Controller",
                FirmwarePersonality.Diagnostics => "Diagnostics",
                FirmwarePersonality.Network => "Network",
                FirmwarePersonality.Keyboard => "RandNET Keyboard",
                FirmwarePersonality.Mouse => "N64 Mouse",
                _ => "Unknown"
            };
            string stateText = _firmwareModeState.State switch
            {
                FirmwareModeSwitchState.QueryingMode => "Querying",
                FirmwareModeSwitchState.RequestingMode => "Requesting",
                FirmwareModeSwitchState.WaitingForDisconnect => "Expected reboot",
                FirmwareModeSwitchState.WaitingForReconnect => "Waiting",
                FirmwareModeSwitchState.VerifyingMode => "Verifying",
                FirmwareModeSwitchState.ModeSwitchFailed => "Failed",
                FirmwareModeSwitchState.Disconnected => "Disconnected",
                _ => modeText
            };

            if (_firmwareModeValueLabel is not null)
            {
                _firmwareModeValueLabel.Text = $"Current: {stateText}";
            }

            bool canRequest = !_firmwareModeSwitchInProgress && IsBridgeConnected;
            if (_firmwareControllerButton is not null)
            {
                _firmwareControllerButton.Enabled = canRequest;
            }

            if (_firmwareDiagnosticsButton is not null)
            {
                _firmwareDiagnosticsButton.Enabled = canRequest;
            }

            if (_firmwareNetworkButton is not null)
            {
                _firmwareNetworkButton.Enabled = canRequest;
            }

            if (_firmwareKeyboardButton is not null)
            {
                _firmwareKeyboardButton.Enabled = canRequest;
            }

            if (_firmwareMouseButton is not null)
            {
                _firmwareMouseButton.Enabled = canRequest;
            }

            if (_settingsFirmwareControllerMenuItem is not null)
            {
                _settingsFirmwareControllerMenuItem.Checked = BridgeUiPolicy.CanMarkFirmwareModeActive(
                    _firmwareModeState.State,
                    _activeFirmwareMode,
                    FirmwarePersonality.Controller);
                _settingsFirmwareControllerMenuItem.Enabled = canRequest;
            }

            if (_settingsFirmwareDiagnosticsMenuItem is not null)
            {
                _settingsFirmwareDiagnosticsMenuItem.Checked = BridgeUiPolicy.CanMarkFirmwareModeActive(
                    _firmwareModeState.State,
                    _activeFirmwareMode,
                    FirmwarePersonality.Diagnostics);
                _settingsFirmwareDiagnosticsMenuItem.Enabled = canRequest;
            }

            if (_settingsFirmwareNetworkMenuItem is not null)
            {
                _settingsFirmwareNetworkMenuItem.Checked = BridgeUiPolicy.CanMarkFirmwareModeActive(
                    _firmwareModeState.State,
                    _activeFirmwareMode,
                    FirmwarePersonality.Network);
                _settingsFirmwareNetworkMenuItem.Enabled = canRequest;
            }

            if (_settingsFirmwareKeyboardMenuItem is not null)
            {
                _settingsFirmwareKeyboardMenuItem.Checked =
                    BridgeUiPolicy.CanMarkFirmwareModeActive(
                        _firmwareModeState.State, _activeFirmwareMode,
                        FirmwarePersonality.Keyboard);
                _settingsFirmwareKeyboardMenuItem.Enabled = canRequest;
            }

            if (_settingsFirmwareMouseMenuItem is not null)
            {
                _settingsFirmwareMouseMenuItem.Checked =
                    BridgeUiPolicy.CanMarkFirmwareModeActive(
                        _firmwareModeState.State, _activeFirmwareMode,
                        FirmwarePersonality.Mouse);
                _settingsFirmwareMouseMenuItem.Enabled = canRequest;
            }

            if (_accessoryKeyboardMenuItem is not null)
            {
                _accessoryKeyboardMenuItem.Checked =
                    BridgeUiPolicy.CanMarkFirmwareModeActive(
                        _firmwareModeState.State, _activeFirmwareMode,
                        FirmwarePersonality.Keyboard);
                _accessoryKeyboardMenuItem.Enabled = canRequest;
            }

            if (_accessoryMouseMenuItem is not null)
            {
                _accessoryMouseMenuItem.Checked =
                    BridgeUiPolicy.CanMarkFirmwareModeActive(
                        _firmwareModeState.State, _activeFirmwareMode,
                        FirmwarePersonality.Mouse);
                _accessoryMouseMenuItem.Enabled = canRequest;
            }

            UpdateInputCaptureUi();
        });
    }

    private static int GetPortNumber(string portName)
    {
        return int.TryParse(portName.Replace("COM", string.Empty), out int number)
            ? number
            : 0;
    }

    private void RefreshButton_Click(object? sender, EventArgs e)
    {
        RefreshPorts();
        SetStatus("COM port list refreshed.");
    }

    private void RefreshVideoButton_Click(object? sender, EventArgs e)
    {
        RefreshCaptureDevices();
        SetStatus("Capture device lists refreshed.");
    }

    private async void NewControllerPakMenuItem_Click(object? sender, EventArgs e)
    {
        if (!CanUseControllerPakMenu()) return;

        using SaveFileDialog dialog = new()
        {
            Filter = "N64 Memory Pak (*.mpk)|*.mpk|All files (*.*)|*.*",
            Title = "Create N64 Memory Pak",
            FileName = "nice-n64.mpk"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string path = dialog.FileName;
        await RunControllerPakContentOperationAsync(
            BridgeOperationKind.ControllerPakCreate,
            $"Creating {Path.GetFileName(path)}...",
            progress => _controllerPakContentService.CreateAsync(path, progress),
            $"Blank Controller Pak created. It will save on disconnect or app close: {Path.GetFileName(path)}.");
    }

    private async void LoadControllerPakMenuItem_Click(object? sender, EventArgs e)
    {
        if (!CanUseControllerPakMenu()) return;

        using OpenFileDialog dialog = new()
        {
            Filter = "N64 Memory Pak (*.mpk)|*.mpk|All files (*.*)|*.*",
            Title = "Load N64 Memory Pak"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string path = dialog.FileName;
        await RunControllerPakContentOperationAsync(
            BridgeOperationKind.ControllerPakLoad,
            $"Loading {Path.GetFileName(path)}...",
            progress => _controllerPakContentService.LoadAsync(path, progress),
            $"Loaded {Path.GetFileName(path)} into Pico RAM. It will save on disconnect or app close.");
    }

    private async void SaveControllerPakMenuItem_Click(object? sender, EventArgs e)
    {
        if (!CanUseControllerPakMenu())
        {
            return;
        }

        using SaveFileDialog dialog = new()
        {
            Filter = "N64 Memory Pak (*.mpk)|*.mpk|All files (*.*)|*.*",
            Title = "Save N64 Memory Pak",
            FileName = "nice-n64.mpk"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string path = dialog.FileName;
        await RunControllerPakContentOperationAsync(
            BridgeOperationKind.ControllerPakSave,
            $"Saving {Path.GetFileName(path)}...",
            progress => _controllerPakContentService.SaveAsync(path, progress),
            $"Saved {Path.GetFileName(path)}.");
    }

    private async void LoadTransferRomMenuItem_Click(object? sender, EventArgs e)
    {
        if (!CanUseControllerPakMenu()) return;

        using OpenFileDialog dialog = new()
        {
            Filter = "Game Boy ROM (*.gb;*.gbc)|*.gb;*.gbc|All files (*.*)|*.*",
            Title = "Load Transfer Pak GB/GBC ROM"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string path = dialog.FileName;
        await RunTransferPakContentOperationAsync(
            BridgeOperationKind.TransferPakLegacyLoad,
            $"Loading {Path.GetFileName(path)} into Transfer Pak RAM...",
            progress => _transferPakLegacyContentService.LoadLegacyAsync(path, progress),
            result => $"Transfer Pak RAM loaded: {result.Snapshot.Metadata?.Summary}, " +
                $"save={result.Snapshot.ExpectedSaveLength} bytes. " +
                $"Save file: {Path.GetFileName(result.Snapshot.SavePath)}");
    }

    private async void InstallTransferRomFlashMenuItem_Click(object? sender, EventArgs e)
    {
        if (!CanUseControllerPakMenu()) return;

        using OpenFileDialog dialog = new()
        {
            Filter = "Game Boy ROM (*.gb;*.gbc)|*.gb;*.gbc|All files (*.*)|*.*",
            Title = "Load full Transfer Pak GB/GBC ROM"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string path = dialog.FileName;
        BridgeTransferPakContentResult inspection =
            await _transferPakContentService.InspectRomAsync(path);
        if (!inspection.Success)
        {
            SetStatus($"Transfer Pak ROM inspection failed: {inspection.Error}");
            return;
        }

        DialogResult confirm = MessageBox.Show(
            this,
            TransferPakInstallWarning,
            "Install Transfer Pak ROM",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        await RunTransferPakContentOperationAsync(
            BridgeOperationKind.TransferPakInstall,
            $"Installing {Path.GetFileName(path)} to Pico flash...",
            progress => _transferPakContentService.InstallRomAsync(path, progress),
            result => $"Transfer Pak flash installed: {result.Snapshot.Metadata?.Summary}, " +
                $"save={result.Snapshot.ExpectedSaveLength} bytes. " +
                $"Save file: {Path.GetFileName(result.Snapshot.SavePath)}");
    }

    private async void PersistencePollTimer_Tick(object? sender, EventArgs e)
    {
        AccessoryPersistenceSnapshot persistence = _accessoryPersistence.Current;
        if (_persistencePollInProgress || !persistence.Active ||
            persistence.Kind == AccessoryPersistenceKind.GameBoySave ||
            !IsBridgeConnected || _controllerPakOperationInProgress ||
            _firmwareModeSwitchInProgress) return;
        _persistencePollInProgress = true;
        try
        {
            string line = await Task.Run(() =>
            {
                lock (_serialLock)
                {
                    IBridgeLineTransport transport = GetActiveLineTransportLocked();
                    transport.DiscardInput();
                    return BridgeBulkTransfer.SendAndExpect(transport, "STAT",
                        candidate => candidate.StartsWith("STAT ", StringComparison.Ordinal),
                        TimeSpan.FromSeconds(2), CancellationToken.None);
                }
            });
            if (TryParseStatCounter(line, "awrite", out uint writes))
                _accessoryPersistence.ObserveAccessoryWriteCounter(writes);
        }
        catch (Exception ex)
        {
            LogDebug($"PERSISTENCE OBSERVER unavailable: {ex.Message}");
        }
        finally { _persistencePollInProgress = false; }
    }

    private static bool TryParseStatCounter(string line, string key, out uint value)
    {
        value = 0;
        string marker = key + "=";
        int start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return false;
        start += marker.Length;
        int end = line.IndexOf(' ', start);
        string text = end < 0 ? line[start..] : line[start..end];
        return uint.TryParse(text, out value);
    }

    private Task UploadControllerPakContentAsync(byte[] image,
        Action<BridgeBulkProgress>? progress, CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            lock (_serialLock)
            {
                token.ThrowIfCancellationRequested();
                IBridgeLineTransport transport = GetActiveLineTransportLocked();
                transport.DiscardInput();
                BridgeBulkTransfer.UploadControllerPak(transport, image, progress, token);
            }
        }, token);

    private Task<byte[]> DownloadControllerPakContentAsync(
        Action<BridgeBulkProgress>? progress, CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            lock (_serialLock)
            {
                token.ThrowIfCancellationRequested();
                IBridgeLineTransport transport = GetActiveLineTransportLocked();
                transport.DiscardInput();
                return BridgeBulkTransfer.DownloadControllerPak(transport, progress, token);
            }
        }, token);

    private Task<uint?> QueryControllerPakWriteCounterAsync(CancellationToken token) =>
        Task.Run<uint?>(() =>
        {
            token.ThrowIfCancellationRequested();
            lock (_serialLock)
            {
                token.ThrowIfCancellationRequested();
                IBridgeLineTransport transport = GetActiveLineTransportLocked();
                transport.DiscardInput();
                string line = BridgeBulkTransfer.SendAndExpect(transport, "STAT",
                    candidate => candidate.StartsWith("STAT ", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(2), token);
                return TryParseStatCounter(line, "awrite", out uint value) ? value : null;
            }
        }, token);

    private Task<int> QueryTransferPakFlashCapacityAsync(CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            lock (_serialLock)
            {
                token.ThrowIfCancellationRequested();
                IBridgeLineTransport transport = GetActiveLineTransportLocked();
                transport.DiscardInput();
                return BridgeBulkTransfer.QueryGameBoyFlashCapacity(transport, token);
            }
        }, token);

    private Task InstallFullTransferPakContentAsync(byte[] rom,
        GameBoyTransferProfile profile, byte[] save,
        Action<BridgeBulkProgress>? progress, CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            lock (_serialLock)
            {
                token.ThrowIfCancellationRequested();
                IBridgeLineTransport transport = GetActiveLineTransportLocked();
                transport.DiscardInput();
                Stopwatch elapsed = Stopwatch.StartNew();
                BridgeBulkTransfer.InstallFullGameBoyRom(transport, rom, profile, save,
                    progress,
                    message => LogDebug(
                        $"TRANSFER {message} elapsedMs={elapsed.ElapsedMilliseconds}",
                        force: true), token);
            }
        }, token);

    private Task InstallLegacyTransferPakContentAsync(byte[] rom,
        GameBoyTransferProfile profile, byte[] save,
        Action<BridgeBulkProgress>? progress, CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            lock (_serialLock)
            {
                token.ThrowIfCancellationRequested();
                IBridgeLineTransport transport = GetActiveLineTransportLocked();
                transport.DiscardInput();
                BridgeBulkTransfer.SendAndExpect(transport, "GBCLR",
                    line => line.StartsWith("OK GBCLR", StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromSeconds(5), token);
                BridgeBulkTransfer.SendAndExpect(transport,
                    $"GBP {profile.CartType:X2} {profile.RomSize:X8} " +
                    $"{profile.RamSize:X8} {profile.FirmwareFlags:X2}",
                    line => line.StartsWith("OK GBP", StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromSeconds(5), token);
                const int cacheLength = 65536;
                const int blockSize = 32;
                int blocks = cacheLength / blockSize;
                for (int offset = 0; offset < cacheLength; offset += blockSize)
                {
                    token.ThrowIfCancellationRequested();
                    string hex = Convert.ToHexString(rom, offset, blockSize);
                    BridgeBulkTransfer.SendAndExpect(transport,
                        $"GB {offset:X4} {hex}",
                        line => line.StartsWith("OK GB", StringComparison.OrdinalIgnoreCase),
                        TimeSpan.FromSeconds(5), token, maxAttempts: 2);
                    progress?.Invoke(new("GB_ROM_UPLOAD", offset + blockSize,
                        cacheLength, offset / blockSize + 1, blocks));
                }
                BridgeBulkTransfer.UploadGameBoySave(transport, save, progress,
                    message => LogDebug($"TRANSFER {message}", force: true), token);
                BridgeBulkTransfer.SendAndExpect(transport, "PAKMODE TRANSFER",
                    line => line.StartsWith("OK PAKMODE", StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromSeconds(5), token);
            }
        }, token);

    private Task<byte[]> DownloadTransferPakSaveContentAsync(int expectedLength,
        Action<BridgeBulkProgress>? progress, CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            lock (_serialLock)
            {
                token.ThrowIfCancellationRequested();
                IBridgeLineTransport transport = GetActiveLineTransportLocked();
                transport.DiscardInput();
                return BridgeBulkTransfer.DownloadGameBoySave(
                    transport, expectedLength, progress, token);
            }
        }, token);

    private Task ExitControllerLineGuardAsync(
        string reason, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_controllerLineGuardLock)
        {
            if (_controllerLineGuardDepth <= 0)
                throw new InvalidOperationException(
                    "Controller line guard exit was not balanced.");
            _controllerLineGuardDepth--;
        }
        LogDebug($"CONTROLLER LINE GUARD EXIT reason={reason}");
        return Task.CompletedTask;
    }

    private async Task RunControllerPakContentOperationAsync(BridgeOperationKind kind,
        string startStatus,
        Func<IProgress<BridgeControllerPakContentProgress>,
            Task<BridgeControllerPakContentResult>> operation,
        string completeStatus)
    {
        if (!TryBeginDesktopOperation(kind, "ControllerPakContent",
                BridgeOperationCancellationCapability.Cooperative,
                out BridgeOperationLease? operationLease))
            return;
        using BridgeOperationLease lease = operationLease!;

        LogDebug($"CONTROLLER PAK CONTENT START {startStatus}", force: true);
        SetStatus(startStatus);
        _serialReceiveBuffer = string.Empty;
        try
        {
            Progress<BridgeControllerPakContentProgress> progress = new(item =>
            {
                lease.ReportProgress(item.Phase.ToString(), item.CompletedBytes,
                    item.TotalBytes, item.ItemIndex, item.ItemCount, "bytes",
                    domainProgress: item);
                if (item.CompletedBytes <= 0 || item.ItemCount <= 0) return;
                int percent = item.TotalBytes <= 0 ? 100 : Math.Clamp(
                    (int)Math.Round(item.CompletedBytes * 100.0 / item.TotalBytes), 0, 100);
                SetStatus($"{item.Operation} Controller Pak... " +
                    $"{item.CompletedBytes}/{item.TotalBytes} bytes ({percent}%, " +
                    $"{item.ItemIndex}/{item.ItemCount})");
            });
            BridgeControllerPakContentResult result = await operation(progress);
            CompleteDesktopOperation(lease, result.Success,
                result.Failure == BridgeControllerPakContentFailure.Cancelled,
                result.Failure.ToString(), result.Error, result);
            SetStatus(result.Success ? completeStatus :
                $"Controller Pak {result.Operation.ToString().ToLowerInvariant()} failed: {result.Error}");
            if (!result.Success)
                LogDebug($"CONTROLLER PAK CONTENT FAILED operation={result.Operation} " +
                    $"failure={result.Failure} error={result.Error}", force: true);
            ApplyAccessoryModeUi();
        }
        finally
        {
            _serialReceiveBuffer = string.Empty;
            LogDebug($"CONTROLLER PAK CONTENT END activeMode={ActiveAccessory}", force: true);
        }
    }

    private async Task RunTransferPakContentOperationAsync(BridgeOperationKind kind,
        string startStatus,
        Func<IProgress<BridgeTransferPakContentProgress>,
            Task<BridgeTransferPakContentResult>> operation,
        Func<BridgeTransferPakContentResult, string> completeStatus)
    {
        if (!TryBeginDesktopOperation(kind, "TransferPakContent",
                BridgeOperationCancellationCapability.Cooperative,
                out BridgeOperationLease? operationLease))
            return;
        using BridgeOperationLease lease = operationLease!;

        LogDebug($"TRANSFER PAK CONTENT START {startStatus}", force: true);
        SetStatus(startStatus);
        _serialReceiveBuffer = string.Empty;
        try
        {
            Progress<BridgeTransferPakContentProgress> progress = new(item =>
            {
                lease.ReportProgress(item.Phase.ToString(), item.CompletedBytes,
                    item.TotalBytes, item.ItemIndex, item.ItemCount, "bytes",
                    domainProgress: item);
                if (item.CompletedBytes <= 0 || item.ItemCount <= 0) return;
                int percent = item.TotalBytes <= 0 ? 100 : Math.Clamp(
                    (int)Math.Round(item.CompletedBytes * 100.0 / item.TotalBytes), 0, 100);
                SetStatus($"{item.Operation} Transfer Pak... " +
                    $"{item.CompletedBytes}/{item.TotalBytes} bytes ({percent}%, " +
                    $"{item.ItemIndex}/{item.ItemCount})");
            });
            BridgeTransferPakContentResult result = await operation(progress);
            CompleteDesktopOperation(lease, result.Success,
                result.Failure == BridgeTransferPakContentFailure.Cancelled,
                result.Failure.ToString(), result.Error, result);
            SetStatus(result.Success ? completeStatus(result) :
                $"Transfer Pak {result.Operation.ToString().ToLowerInvariant()} " +
                $"failed: {result.Error}");
            if (!result.Success)
                LogDebug($"TRANSFER PAK CONTENT FAILED operation={result.Operation} " +
                    $"failure={result.Failure} error={result.Error}", force: true);
            ApplyAccessoryModeUi();
        }
        finally
        {
            _serialReceiveBuffer = string.Empty;
            LogDebug($"TRANSFER PAK CONTENT END activeMode={ActiveAccessory}", force: true);
        }
    }

    private bool CanUseControllerPakMenu()
    {
        if (_profile.Type != ConsoleControllerType.N64)
        {
            SetStatus("Controller Pak tools require the N64 profile.");
            return false;
        }

        if (!IsBridgeConnected)
        {
            SetStatus("Connect to the Pico first.");
            return false;
        }

        return true;
    }

    private SerialPort GetOpenSerialPort()
    {
        if (_serialPort?.IsOpen != true)
        {
            throw new InvalidOperationException("Pico serial port is not connected.");
        }

        return _serialPort;
    }

    private IBridgeLineTransport GetActiveLineTransportLocked()
    {
        IBridgeLineTransport? transport = ActiveLineTransportLocked();
        if (transport?.IsConnected != true)
        {
            throw new InvalidOperationException("NICE64 Bridge transport is not connected.");
        }
        return transport;
    }

    private async Task<BridgeAccessoryPortResult> SendControllerAccessoryCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        _serialReceiveBuffer = string.Empty;
        SuspendControllerDelivery("accessory-mode-transition");
        try
        {
            // Preserve the accepted five-second, non-cancellable wire operation.
            await Task.Run(() =>
            {
                lock (_serialLock)
                {
                    IBridgeLineTransport transport = GetActiveLineTransportLocked();
                    transport.DiscardInput();
                    string response = BridgeBulkTransfer.SendAndExpect(
                        transport, command,
                        line => line.StartsWith("OK PAKMODE",
                            StringComparison.OrdinalIgnoreCase),
                        TimeSpan.FromSeconds(5), CancellationToken.None);
                    LogDebug($"PAKMODE ACK command={command} response={response}", force: true);
                }
            }).ConfigureAwait(false);
            return BridgeAccessoryPortResult.Succeeded;
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
        finally
        {
            _serialReceiveBuffer = string.Empty;
            ClearQueuedControllerStateCommands();
            await ResumeControllerDeliveryAsync(
                resendCurrent: false, "accessory-mode-transition",
                CancellationToken.None);
        }
    }

    private static string WriteLineAndExpect(SerialPort serial, string command, Predicate<string> expected)
    {
        serial.WriteLine(command);
        return ReadMatchingLine(serial, expected);
    }

    private static string WriteLineAndExpect(SerialPort serial, string command, Predicate<string> expected, TimeSpan timeout, int readTimeoutMs)
    {
        serial.WriteLine(command);
        return ReadMatchingLine(serial, expected, timeout, readTimeoutMs);
    }

    private static string ReadMatchingLine(SerialPort serial, Predicate<string> expected)
    {
        return ReadMatchingLine(serial, expected, TimeSpan.FromSeconds(5), 2500);
    }

    private static string ReadMatchingLine(SerialPort serial, Predicate<string> expected, TimeSpan timeout, int readTimeoutMs)
    {
        int oldTimeout = serial.ReadTimeout;
        serial.ReadTimeout = readTimeoutMs;

        try
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                string line = serial.ReadLine().Trim();
                if (expected(line))
                {
                    return line;
                }

                if (line.StartsWith("ERR", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(line);
                }
            }
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException("Timed out waiting for Pico Controller Pak response.", ex);
        }
        finally
        {
            serial.ReadTimeout = oldTimeout;
        }

        throw new InvalidOperationException("Timed out waiting for Pico Controller Pak response.");
    }

    private Task<string> SendWifiCommandAsync(string command, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            lock (_serialLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SerialPort serial = GetOpenSerialPort();
                try
                {
                    string safeName = WifiProvisioningProtocol.SafeCommandName(command);
                    LogDebug($"WIFI send {safeName}", force: true);
                    serial.WriteLine(command);
                    string reply = ReadWifiReplyLocked(serial, command, cancellationToken);
                    LogDebug($"WIFI response {WifiProvisioningProtocol.SafeReplyName(command)}",
                        force: true);
                    return reply;
                }
                catch (TimeoutException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    ScheduleAutoReconnectLocked(ex);
                    throw new IOException("NICE64 USB transaction failed.", ex);
                }
            }
        }, cancellationToken);
    }

    private string ReadWifiReplyLocked(SerialPort serial, string command, CancellationToken cancellationToken)
    {
        int oldTimeout = serial.ReadTimeout;
        serial.ReadTimeout = 250;
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string line;
                try
                {
                    line = serial.ReadLine().Trim();
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (WifiProvisioningProtocol.IsExpectedReply(command, line))
                {
                    return line;
                }

                if (line.StartsWith("NETPAK_", StringComparison.Ordinal))
                {
                    HandleNetworkPakLineLocked(_serialBridgeTransport ?? new SerialBridgeTransport(serial), line);
                }
            }
        }
        finally
        {
            serial.ReadTimeout = oldTimeout;
        }

        throw new TimeoutException("Timed out waiting for a NICE64 Wi-Fi response.");
    }

    private async void RequestAccessoryMode(BridgeAccessoryMode mode)
    {
        string label = GetAccessoryLabel(mode);
        if (!TryBeginDesktopOperation(BridgeOperationKind.AccessorySelection,
                "Accessory", BridgeOperationCancellationCapability.BeforeCommitOnly,
                out BridgeOperationLease? operation))
            return;
        using BridgeOperationLease lease = operation!;
        lease.ReportProgress("TransitionPending", domainProgress: mode);
        SetStatus($"Selecting {label}...");
        LogDebug($"ACCESSORY MODE TRANSITION requested={mode} previous={ActiveAccessory}",
            force: true);
        BridgeAccessoryTransitionResult result =
            await _accessoryService.SelectAccessoryAsync(mode, CancellationToken.None);
        CompleteDesktopOperation(lease, result.Success,
            result.Failure == BridgeAccessoryTransitionFailure.Cancelled,
            result.Failure.ToString(), result.Error, result);
        if (result.Success)
        {
            _activeFirmwareMode = result.FirmwareResult?.VerifiedMode?.Personality ??
                _bridgeDeviceState.Current.FirmwarePersonality;
            _firmwareModeState.Ready(_activeFirmwareMode);
            if (mode != BridgeAccessoryMode.RumblePak) SetXInputRumble(false);
            SetStatus(mode == BridgeAccessoryMode.TransferPak &&
                !_transferPakContentService.Current.HasAssociatedRom
                ? "Transfer Pak mode active with no GB/GBC ROM loaded."
                : $"{label} mode active.");
        }
        else
        {
            SetStatus($"{label} not selected: {result.Error}");
            LogDebug($"ACCESSORY MODE failed requested={mode} failure={result.Failure} " +
                $"error={result.Error}", force: true);
        }
        ApplyAccessoryModeUi();
        UpdateFirmwareModeUi();
    }

    private void ApplyAccessoryModeUi()
    {
        FirmwarePersonality activeFirmware =
            _bridgeState.GetSnapshot().Device.FirmwarePersonality;
        bool controllerFirmwareActive = activeFirmware == FirmwarePersonality.Controller;
        bool canSelectAccessory = BridgeUiPolicy.CanRequestAccessoryTransition(
            IsBridgeConnected, _firmwareModeSwitchInProgress, _controllerPakOperationInProgress);

        if (_noAccessoryModeMenuItem is not null)
        {
            _noAccessoryModeMenuItem.Checked = controllerFirmwareActive && ActiveAccessory == BridgeAccessoryMode.None;
            _noAccessoryModeMenuItem.Enabled = canSelectAccessory;
        }

        if (_memoryPakModeMenuItem is not null)
        {
            _memoryPakModeMenuItem.Checked = controllerFirmwareActive && ActiveAccessory == BridgeAccessoryMode.ControllerPak;
            _memoryPakModeMenuItem.Enabled = canSelectAccessory;
        }

        if (_rumblePakModeMenuItem is not null)
        {
            _rumblePakModeMenuItem.Checked = controllerFirmwareActive && ActiveAccessory == BridgeAccessoryMode.RumblePak;
            _rumblePakModeMenuItem.Enabled = canSelectAccessory;
        }

        if (_transferPakModeMenuItem is not null)
        {
            _transferPakModeMenuItem.Checked = controllerFirmwareActive && ActiveAccessory == BridgeAccessoryMode.TransferPak;
            _transferPakModeMenuItem.Enabled = canSelectAccessory;
        }

        if (_networkPakModeMenuItem is not null)
        {
            _networkPakModeMenuItem.Checked = activeFirmware == FirmwarePersonality.Network ||
                (controllerFirmwareActive && ActiveAccessory == BridgeAccessoryMode.NetworkPak);
            _networkPakModeMenuItem.Enabled = canSelectAccessory;
        }

        if (ActiveAccessory == BridgeAccessoryMode.RumblePak && _pcRumblePassthroughMenuItem?.Checked == true)
        {
            _rumblePollTimer.Start();
        }
        else
        {
            _rumblePollTimer.Stop();
        }
    }

    private void SetInputSourceMode(InputSourceMode mode)
    {
        _inputSourceMode = mode;
        if (!_initializingUi)
        {
            BridgeSettings.SaveInputSourceMode(_settingsPath, mode.ToString());
        }

        _n64AutoInputBlockedStatusShown = false;
        ApplyInputSourceModeUi();

        if (mode is InputSourceMode.Auto or InputSourceMode.DirectInput or InputSourceMode.EightBitDoParsec)
        {
            TryAcquireFirstGamepad();
        }
        else
        {
            ReleaseJoystick();
        }

        if (mode is InputSourceMode.Auto or InputSourceMode.XInput)
        {
            TryAcquireXInputController();
        }
        else
        {
            _xinputController = null;
        }

        lock (_maskLock)
        {
            _gamepadMask = 0;
            _gamepadAnalogActive = false;
            _gamepadAnalogX = 0;
            _gamepadAnalogY = 0;
            Array.Clear(_gamepadReleaseTicks);
        }

        LoadActiveAnalogCalibration();

        if (_profile.Type == ConsoleControllerType.N64
            && mode is InputSourceMode.XInput or InputSourceMode.DirectInput or InputSourceMode.EightBitDoParsec
            && !_debugLoggingEnabled)
        {
            _debugLoggingEnabled = true;
            if (_debugLoggingMenuItem is not null)
            {
                _debugLoggingMenuItem.Checked = true;
            }

            LogDebug("DEBUG LOGGING ENABLED FOR N64 INPUT VALIDATION", force: true);
        }

        _lastDirectInputRawSignature = "";
        SendCombinedMask();
        SetStatus($"Input source: {GetInputSourceLabel(mode)}.");
    }

    private void ApplyInputSourceModeUi()
    {
        if (_inputNoneMenuItem is not null)
        {
            _inputNoneMenuItem.Checked = _inputSourceMode == InputSourceMode.None;
        }

        if (_inputAutoMenuItem is not null)
        {
            _inputAutoMenuItem.Checked = _inputSourceMode == InputSourceMode.Auto;
        }

        if (_inputXInputMenuItem is not null)
        {
            _inputXInputMenuItem.Checked = _inputSourceMode == InputSourceMode.XInput;
        }

        if (_inputDirectInputMenuItem is not null)
        {
            _inputDirectInputMenuItem.Checked = _inputSourceMode == InputSourceMode.DirectInput;
        }

        if (_inputEightBitDoParsecMenuItem is not null)
        {
            _inputEightBitDoParsecMenuItem.Checked = _inputSourceMode == InputSourceMode.EightBitDoParsec;
        }
    }

    private static string GetInputSourceLabel(InputSourceMode mode)
    {
        return mode switch
        {
            InputSourceMode.None => "UI only / no gamepad polling",
            InputSourceMode.XInput => "XInput / Xbox-compatible",
            InputSourceMode.DirectInput => "Generic USB",
            InputSourceMode.EightBitDoParsec => "8BitDo / Parsec",
            _ => "Auto (SNES / legacy)"
        };
    }

    private static InputSourceMode ParseInputSourceMode(string value)
    {
        return Enum.TryParse(value, ignoreCase: true, out InputSourceMode mode)
            ? mode
            : InputSourceMode.Auto;
    }

    private static ConsoleControllerType ParseConsoleMode(string value)
    {
        return Enum.TryParse(value, ignoreCase: true, out ConsoleControllerType mode)
            ? mode
            : ConsoleControllerType.Snes;
    }

    private async void RumblePollTimer_Tick(object? sender, EventArgs e)
    {
        if (_profile.Type != ConsoleControllerType.N64
            || ActiveAccessory != BridgeAccessoryMode.RumblePak
            || _serialPort?.IsOpen != true
            || _controllerPakOperationInProgress)
        {
            SetXInputRumble(false);
            return;
        }

        if (_serialCommands.Count > 0) return;

        if (Environment.TickCount64 - _lastControllerCommandTicks < 150)
        {
            return;
        }

        await RunRumblePollAsync();
    }

    private async Task RunRumblePollAsync()
    {
        if (_rumblePollInProgress)
        {
            return;
        }

        _rumblePollInProgress = true;

        try
        {
            bool rumble = await Task.Run(() =>
            {
                lock (_serialLock)
                {
                    SerialPort serial = GetOpenSerialPort();
                    serial.WriteLine("RUMBLE?");
                    string response = ReadMatchingLine(
                        serial,
                        line => line.StartsWith("RUMBLE ", StringComparison.OrdinalIgnoreCase),
                        TimeSpan.FromMilliseconds(300),
                        100);
                    return response.EndsWith("1", StringComparison.OrdinalIgnoreCase);
                }
            });

            SetSmoothedXInputRumble(rumble);
        }
        catch
        {
            SetXInputRumble(false);
        }
        finally
        {
            _rumblePollInProgress = false;
        }
    }

    private void SetSmoothedXInputRumble(bool active)
    {
        long now = Environment.TickCount64;
        if (active)
        {
            _rumbleOffAfterTicks = now + 350;
            SetXInputRumble(true);
            return;
        }

        if (now >= _rumbleOffAfterTicks)
        {
            SetXInputRumble(false);
        }
    }

    private void SetXInputRumble(bool active)
    {
        if (_rumbleActive == active && active)
        {
            return;
        }

        _rumbleActive = active;

        try
        {
            if (_xinputController?.IsConnected != true)
            {
                TryAcquireXInputController();
            }

            _xinputController?.SetVibration(active
                ? new Vibration { LeftMotorSpeed = 18000, RightMotorSpeed = 26000 }
                : new Vibration { LeftMotorSpeed = 0, RightMotorSpeed = 0 });
        }
        catch
        {
            _rumbleActive = false;
        }
    }

    private void OpenExternalViewerMenuItem_Click(object? sender, EventArgs e)
    {
        string viewerPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "PicoVideoViewer",
            "bin",
            "Debug",
            "net9.0-windows",
            "PicoVideoViewer.exe"));

        if (!File.Exists(viewerPath))
        {
            SetStatus("Build PicoVideoViewer first.");
            return;
        }

        int deviceIndex = GetSelectedVideoDeviceIndex();
        int audioDeviceIndex = GetSelectedAudioDeviceIndex();
        ProcessStartInfo startInfo = new()
        {
            FileName = viewerPath,
            Arguments = $"--device {deviceIndex} --audio {audioDeviceIndex}",
            UseShellExecute = true
        };

        Process.Start(startInfo);
        SetStatus($"Opened external viewer on video {deviceIndex}, audio {audioDeviceIndex}.");
    }

    private void ConfigureControllerMenuItem_Click(object? sender, EventArgs e)
    {
        string mappingPath = ControllerProfile.GetMappingPath(_profile.Type);
        using ControllerMappingForm form = new(_profile.ButtonBits.Keys.ToArray(), _mapping, mappingPath);
        form.ShowDialog(this);
        _mapping = form.Mapping;
        TryAcquireFirstGamepad();
        TryAcquireXInputController();
    }

    private void AnalogStickCalibrationMenuItem_Click(object? sender, EventArgs e)
    {
        if (_profile.Type != ConsoleControllerType.N64)
        {
            SetStatus("Analog stick calibration is available for N64 mode.");
            return;
        }

        AnalogStickSnapshot? snapshot = ReadCurrentAnalogSnapshot();
        if (snapshot is null)
        {
            SetStatus("Select XInput, DirectInput, or 8BitDo / Parsec and connect a controller first.");
            return;
        }

        using AnalogStickCalibrationForm form = new(ReadCurrentAnalogSnapshot, GetAnalogCalibrationPath, LoadActiveAnalogCalibration);
        form.ShowDialog(this);
    }

    private AnalogStickSnapshot? ReadCurrentAnalogSnapshot()
    {
        if (_profile.Type != ConsoleControllerType.N64)
        {
            return null;
        }

        if (_inputSourceMode == InputSourceMode.XInput && _xinputController?.IsConnected == true)
        {
            Gamepad gamepad = _xinputController.GetState().Gamepad;
            return new AnalogStickSnapshot(
                "XInput",
                string.IsNullOrWhiteSpace(_xinputDeviceId) ? "xinput" : _xinputDeviceId,
                string.IsNullOrWhiteSpace(_xinputDeviceName) ? "XInput controller" : _xinputDeviceName,
                gamepad.LeftThumbX,
                gamepad.LeftThumbY,
                1,
                1);
        }

        if (_inputSourceMode is InputSourceMode.DirectInput or InputSourceMode.EightBitDoParsec)
        {
            JoystickState? state = ReadJoystick();
            if (state is null)
            {
                return null;
            }

            ControllerMapping mapping = _inputSourceMode == InputSourceMode.EightBitDoParsec
                ? _eightBitDoParsecMapping
                : _mapping;
            if (!TryResolveAnalogAxes(mapping, state, out int rawX, out int rawY, out int xDirection, out int yDirection))
            {
                return null;
            }

            string sourceKind = _inputSourceMode == InputSourceMode.EightBitDoParsec
                ? "DirectInput-8BitDoParsec"
                : "DirectInput";
            return new AnalogStickSnapshot(
                sourceKind,
                string.IsNullOrWhiteSpace(_directInputDeviceId) ? "directinput" : _directInputDeviceId,
                string.IsNullOrWhiteSpace(_directInputDeviceName) ? "DirectInput controller" : _directInputDeviceName,
                rawX,
                rawY,
                xDirection,
                yDirection);
        }

        return null;
    }

    private bool TryResolveAnalogAxes(
        ControllerMapping mapping,
        JoystickState state,
        out int rawX,
        out int rawY,
        out int positiveXRawDirection,
        out int positiveYRawDirection)
    {
        rawX = 0;
        rawY = 0;
        positiveXRawDirection = 1;
        positiveYRawDirection = 1;

        Dictionary<string, int> axes = GamepadInput.GetAxes(state).ToDictionary(axis => axis.Name, axis => axis.Value);
        if (!TryResolveMappedAxis(mapping, axes, "Stick Right", "Stick Left", out rawX, out positiveXRawDirection))
        {
            return false;
        }

        return TryResolveMappedAxis(mapping, axes, "Stick Up", "Stick Down", out rawY, out positiveYRawDirection);
    }

    private static bool TryResolveMappedAxis(
        ControllerMapping mapping,
        Dictionary<string, int> axes,
        string positiveButton,
        string negativeButton,
        out int raw,
        out int positiveRawDirection)
    {
        raw = 0;
        positiveRawDirection = 1;

        if (TryReadAxisBinding(mapping, axes, positiveButton, out raw, out positiveRawDirection))
        {
            return true;
        }

        if (TryReadAxisBinding(mapping, axes, negativeButton, out raw, out int negativeRawDirection))
        {
            positiveRawDirection = -negativeRawDirection;
            return true;
        }

        return false;
    }

    private static bool TryReadAxisBinding(
        ControllerMapping mapping,
        Dictionary<string, int> axes,
        string buttonName,
        out int raw,
        out int rawDirection)
    {
        raw = 0;
        rawDirection = 1;
        if (!mapping.Bindings.TryGetValue(buttonName, out InputBinding? binding)
            || !binding.Kind.Equals("Axis", StringComparison.OrdinalIgnoreCase)
            || binding.Direction.Length < 2)
        {
            return false;
        }

        string axisName = binding.Direction[..^1];
        if (!axes.TryGetValue(axisName, out raw))
        {
            return false;
        }

        rawDirection = binding.Direction[^1] == '-' ? -1 : 1;
        return true;
    }

    private string GetAnalogCalibrationPath(string sourceKind, string deviceId)
    {
        string safeSource = SanitizeCalibrationFilePart(sourceKind);
        string safeDevice = SanitizeCalibrationFilePart(string.IsNullOrWhiteSpace(deviceId) ? "default" : deviceId);
        return Path.Combine(AppContext.BaseDirectory, "analog-calibrations", $"{safeSource}-{safeDevice}.json");
    }

    private static string SanitizeCalibrationFilePart(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = value.Trim().Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch).ToArray();
        string sanitized = new(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }

    private void LoadActiveAnalogCalibration()
    {
        AnalogStickSnapshot? snapshot = ReadCurrentAnalogSnapshot();
        if (snapshot is null)
        {
            _activeAnalogCalibration = null;
            _activeAnalogCalibrationKey = "";
            lock (_maskLock)
            {
                _gamepadAnalogActive = false;
                _gamepadAnalogX = 0;
                _gamepadAnalogY = 0;
            }
            return;
        }

        string key = $"{snapshot.SourceKind}|{snapshot.DeviceId}";
        _activeAnalogCalibrationKey = key;
        _activeAnalogCalibration = AnalogStickCalibration.Load(GetAnalogCalibrationPath(snapshot.SourceKind, snapshot.DeviceId));
        if (_activeAnalogCalibration is null)
        {
            lock (_maskLock)
            {
                _gamepadAnalogActive = false;
                _gamepadAnalogX = 0;
                _gamepadAnalogY = 0;
            }
        }
    }

    private static string GetAccessoryLabel(BridgeAccessoryMode mode) => mode switch
    {
        BridgeAccessoryMode.RumblePak => "Rumble Pak",
        BridgeAccessoryMode.TransferPak => "Transfer Pak",
        BridgeAccessoryMode.NetworkPak => "Network Pak",
        BridgeAccessoryMode.None => "No Accessory",
        _ => "Controller Pak"
    };

    private void ConsoleComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (consoleComboBox.SelectedItem is not ConsoleItem item || item.Type == _profile.Type)
        {
            return;
        }

        bool initializing = _initializingUi;
        lock (_maskLock)
        {
            _manualMask = 0;
            _gamepadMask = 0;
            _gamepadAnalogActive = false;
            _gamepadAnalogX = 0;
            _gamepadAnalogY = 0;
            Array.Clear(_gamepadReleaseTicks);
        }

        _profile = ControllerProfile.Create(item.Type);
        if (!initializing)
        {
            BridgeSettings.SaveConsoleMode(_settingsPath, item.Type.ToString());
        }

        LoadMapping();
        LoadActiveAnalogCalibration();
        CreateButtonPad();
        ApplyDebugModeUi();
        ApplyOnScreenControlsUi();
        if (_profile.Type == ConsoleControllerType.N64 && _inputSourceMode == InputSourceMode.Auto)
        {
            SetInputSourceMode(InputSourceMode.None);
        }
        else
        {
            _n64AutoInputBlockedStatusShown = false;
        }

        SetDisconnectedState();
        UpdateApplicationBranding();

        if (_serialPort?.IsOpen == true)
        {
            SendMask();
        }

        SetStatus($"{_profile.DisplayName} layout selected.");
    }

    private void UpdateApplicationBranding()
    {
        string consoleName = _profile.Type == ConsoleControllerType.N64 ? "64" : _profile.DisplayName;
        Text = $"{BridgeConstants.ProductDisplayName} — NICE {consoleName} Controller — " +
            _bridgeInstance.Current.Name;
        titleLabel.Text = $"NICE {consoleName} Controller";
    }

    private void StartVideoButton_Click(object? sender, EventArgs e)
    {
        if (_videoPreview is not null)
        {
            SetStatus("Video preview is already running.");
            return;
        }

        int deviceIndex = GetSelectedVideoDeviceIndex();
        int width = (int)videoWidthNumeric.Value;
        int height = (int)videoHeightNumeric.Value;
        int fps = (int)videoFpsNumeric.Value;

        try
        {
            _videoPreview = new DirectShowPreview(deviceIndex, width, height, fps, videoPictureBox.Handle);
            _videoPreview.Start();
            ResizeVideoPreview();
            SetVideoPreviewState(isRunning: true);
            SetStatus($"DirectShow preview: device {deviceIndex}, {width}x{height} @ {fps}.");
        }
        catch (Exception ex)
        {
            _videoPreview?.Dispose();
            _videoPreview = null;
            SetVideoPreviewState(isRunning: false);
            SetStatus($"Video preview failed: {ex.Message}");
        }
    }

    private int GetSelectedVideoDeviceIndex()
    {
        string selectedText = videoDeviceComboBox.SelectedItem?.ToString() ?? "0";
        string indexText = selectedText.Split('-', 2)[0].Trim();
        return int.TryParse(indexText, out int deviceIndex)
            ? deviceIndex
            : 0;
    }

    private int GetSelectedAudioDeviceIndex()
    {
        string selectedText = audioDeviceComboBox.SelectedItem?.ToString() ?? "0";
        string indexText = selectedText.Split('-', 2)[0].Trim();
        return int.TryParse(indexText, out int deviceIndex)
            ? deviceIndex
            : 0;
    }

    private void StartAudioButton_Click(object? sender, EventArgs e)
    {
        if (_audioPassthrough is not null)
        {
            SetStatus("Audio passthrough is already running.");
            return;
        }

        int deviceIndex = GetSelectedAudioDeviceIndex();

        try
        {
            _audioPassthrough = new DirectShowAudioPassthrough(deviceIndex);
            SetAudioVolume();
            _audioPassthrough.Start();
            SetAudioPassthroughState(isRunning: true);
            SetStatus($"Audio passthrough: device {deviceIndex}.");
        }
        catch (Exception ex)
        {
            _audioPassthrough?.Dispose();
            _audioPassthrough = null;
            SetAudioPassthroughState(isRunning: false);
            SetStatus($"Audio passthrough failed: {ex.Message}");
        }
    }

    private void StopAudioButton_Click(object? sender, EventArgs e)
    {
        StopAudioPassthrough();
    }

    private void AudioVolumeTrackBar_ValueChanged(object? sender, EventArgs e)
    {
        SetAudioVolume();
    }

    private void StopVideoButton_Click(object? sender, EventArgs e)
    {
        StopVideoPreview();
    }

    private void FullScreenVideoButton_Click(object? sender, EventArgs e)
    {
        if (_videoPreview is null)
        {
            StartVideoButton_Click(sender, e);
            if (_videoPreview is null)
            {
                return;
            }
        }

        if (_fullScreenVideoForm is { IsDisposed: false })
        {
            _fullScreenVideoForm.Activate();
            return;
        }

        _fullScreenVideoPanel = new Panel
        {
            BackColor = Color.Black,
            Dock = DockStyle.Fill
        };
        _fullScreenVideoPanel.DoubleClick += (_, _) => CloseFullScreenVideo();
        _fullScreenVideoPanel.Resize += (_, _) => ResizeVideoPreview();

        _fullScreenVideoForm = new Form
        {
            BackColor = Color.Black,
            FormBorderStyle = FormBorderStyle.None,
            KeyPreview = true,
            StartPosition = FormStartPosition.Manual,
            WindowState = FormWindowState.Maximized,
            Bounds = Screen.FromControl(this).Bounds
        };

        _fullScreenVideoForm.Controls.Add(_fullScreenVideoPanel);
        _fullScreenVideoForm.KeyDown += (_, keyArgs) =>
        {
            if (keyArgs.KeyCode == Keys.Escape)
            {
                CloseFullScreenVideo();
            }
        };
        _fullScreenVideoForm.FormClosed += (_, _) =>
        {
            _fullScreenVideoPanel = null;
            _fullScreenVideoForm = null;
            if (_videoPreview is not null && !videoPictureBox.IsDisposed)
            {
                _videoPreview.SetOwner(videoPictureBox.Handle);
                ResizeVideoPreview();
            }
        };

        _fullScreenVideoForm.Show(this);
        _videoPreview.SetOwner(_fullScreenVideoPanel.Handle);
        ResizeVideoPreview();
        _fullScreenVideoForm.Activate();
    }

    private void ClearButton_Click(object? sender, EventArgs e)
    {
        lock (_maskLock)
        {
            _manualMask = 0;
            _gamepadMask = 0;
            _gamepadAnalogActive = false;
            _gamepadAnalogX = 0;
            _gamepadAnalogY = 0;
            Array.Clear(_gamepadReleaseTicks);
        }

        if (_profile.Type == ConsoleControllerType.Snes)
        {
            SendCommand("FORCE OFF");
        }

        SendMask();
    }

    private void ForceLowButton_Click(object? sender, EventArgs e)
    {
        if (_profile.Type != ConsoleControllerType.Snes)
        {
            SetStatus("Data line test commands are SNES-firmware only.");
            return;
        }

        SendCommand("FORCE LOW");
    }

    private void ForceHighButton_Click(object? sender, EventArgs e)
    {
        if (_profile.Type != ConsoleControllerType.Snes)
        {
            SetStatus("Data line test commands are SNES-firmware only.");
            return;
        }

        SendCommand("FORCE HIGH");
    }

    private void ForceOffButton_Click(object? sender, EventArgs e)
    {
        if (_profile.Type == ConsoleControllerType.Snes)
        {
            SendCommand("FORCE OFF");
        }

        SendCombinedMask();
    }

    private void N64StatButton_Click(object? sender, EventArgs e)
    {
        SendDebugCommandAndRead("STAT");
    }

    private void N64ClearDebugButton_Click(object? sender, EventArgs e)
    {
        SendDebugCommandAndRead("CLR");
    }

    private void ControllerButton_MouseDown(object? sender, MouseEventArgs e)
    {
        if (sender is not Control { Tag: int bit } button)
        {
            return;
        }

        lock (_maskLock)
        {
            _manualMask |= 1u << bit;
        }

        if (!IsN64NoAccessoryBaseline)
        {
            LogDebug($"MANUAL DOWN {button.Text} bit={bit}");
        }

        SendCombinedMask();
    }

    private void ControllerButton_MouseUp(object? sender, EventArgs e)
    {
        if (sender is not Control { Tag: int bit } button)
        {
            return;
        }

        lock (_maskLock)
        {
            _manualMask &= ~(1u << bit);
        }

        if (!IsN64NoAccessoryBaseline)
        {
            LogDebug($"MANUAL UP {button.Text} bit={bit}");
        }

        SendCombinedMask();
    }

    private void SendCombinedMask()
    {
        BridgeControllerState state = BuildCurrentSemanticControllerState();

        if (!IsN64NoAccessoryBaseline)
            LogDebug($"COMBINED mask=0x{state.Buttons:X8} " +
                $"buttons={FormatPressedButtons(state.Buttons)}");
        SubmitControllerState(state);
    }

    private void SendMaskBaseline()
        => SendMask();

    private void SendMask()
        => SubmitControllerState(BuildCurrentSemanticControllerState());

    private BridgeControllerState BuildCurrentSemanticControllerState()
    {
        lock (_maskLock)
        {
            return BridgeControllerStateComposer.Merge(
                _profile.Type,
                _manualMask,
                _gamepadMask,
                _gamepadAnalogActive,
                _gamepadAnalogX,
                _gamepadAnalogY);
        }
    }

    private void SubmitControllerState(BridgeControllerState state)
    {
        _lastControllerCommandTicks = Environment.TickCount64;
        _controllerService.SetStateAsync(state, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private void SendCommand(string command)
    {
        LogDebug($"QUEUE {command}");
        _serialCommands.EnqueueNonController(command);

        _serialSignal.Set();
    }

    private void ClearQueuedControllerStateCommands()
        => _controllerService.ClearPendingState();

    private void SendDebugCommandAndRead(string command)
    {
        lock (_serialLock)
        {
            if (_serialPort?.IsOpen != true)
            {
                SetStatus("Connect to the Pico first.");
                return;
            }

            try
            {
                string currentControllerState = BuildCurrentControllerCommand();
                _serialPort.DiscardInBuffer();
                _serialPort.WriteLine(currentControllerState);
                _serialPort.WriteLine(command);
                LogDebug($"SEND DEBUG {command}");
                Thread.Sleep(25);

                string response = _serialPort.ReadExisting().Trim();
                _serialPort.WriteLine(currentControllerState);
                string logResponse = string.IsNullOrWhiteSpace(response)
                    ? $"DEBUG {command} <no response>"
                    : $"DEBUG {command} {response.Replace(Environment.NewLine, " | ")}";
                LogDebug(logResponse, force: command.Equals("STAT", StringComparison.OrdinalIgnoreCase));
                if (command.Equals("STAT", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(response))
                {
                    try
                    {
                        Clipboard.SetText(response);
                    }
                    catch
                    {
                        // Clipboard can be temporarily busy; the debug log still has the full response.
                    }
                }
                SetStatus(string.IsNullOrWhiteSpace(response)
                    ? $"Sent {command}; no response yet."
                    : response);
            }
            catch (Exception ex)
            {
                SetStatus($"{command} failed: {ex.Message}");
            }
        }
    }

    private string BuildCurrentControllerCommand()
        => _controllerService.BuildCurrentWireCommand();

    private BridgeControllerPortAvailability GetControllerPortAvailability()
    {
        if (!IsBridgeConnected)
            return BridgeControllerPortAvailability.NotConnected;
        BridgeControllerTransportSelection transport =
            SelectCurrentControllerTransport();
        if (!BridgeControllerDeliveryGuardPolicy.Allows(
                CurrentControllerDeliveryGuard(), transport))
            return BridgeControllerPortAvailability.Busy;
        if (_bridgeDeviceState.Current.FirmwarePersonality != FirmwarePersonality.Controller)
            return BridgeControllerPortAvailability.FirmwareUnavailable;
        return BridgeControllerPortAvailability.Available;
    }

    private ValueTask<BridgeControllerPortResult> QueueLatestControllerCommandAsync(
        BridgeControllerCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BridgeControllerTransportSelection transport = SelectControllerTransport(command.State);
        BridgeControllerDeliveryGuard guard = CurrentControllerDeliveryGuard();
        if (!BridgeControllerDeliveryGuardPolicy.Allows(guard, transport))
        {
            string guardedReason = guard == BridgeControllerDeliveryGuard.LineOnly
                ? "protected-content-line-guard"
                : "controller-delivery-all-guard";
            LogDebug($"CONTROLLER STATE commandSeq={command.Sequence} path=LINE " +
                $"reason={guardedReason}");
            return ValueTask.FromResult(new BridgeControllerPortResult(
                BridgeControllerPortDisposition.Failed,
                "Controller line delivery is blocked by a protected operation."));
        }
        if (TrySubmitLatestStateUdp(command, transport, out int udpSuperseded,
                out string lineFallbackReason))
        {
            LogDebug($"CONTROLLER STATE commandSeq={command.Sequence} " +
                $"path=LATEST_STATE_UDP state={command.WireCommand}");
            return ValueTask.FromResult(BridgeControllerPortResult.AcceptedPending);
        }

        StopLatestStateUdpSender();
        int dropped = _serialCommands.EnqueueLatestController(command);

        LogDebug($"CONTROLLER STATE commandSeq={command.Sequence} path=LINE " +
            $"reason={lineFallbackReason}");
        _serialSignal.Set();
        return ValueTask.FromResult(BridgeControllerPortResult.AcceptedPending);
    }

    private ValueTask<BridgeControllerPortResult> DeliverControllerCommandImmediatelyAsync(
        BridgeControllerCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_serialLock)
        {
            IBridgeLineTransport? transport = ActiveLineTransportLocked();
            if (transport?.IsConnected != true)
                return ValueTask.FromResult(new BridgeControllerPortResult(
                    BridgeControllerPortDisposition.Failed,
                    "The active controller transport is unavailable."));
            try
            {
                transport.WriteLine(command.WireCommand);
                LogDebug($"SEND NEUTRAL {command.WireCommand}");
                return ValueTask.FromResult(BridgeControllerPortResult.Delivered);
            }
            catch (Exception ex)
            {
                return ValueTask.FromResult(new BridgeControllerPortResult(
                    BridgeControllerPortDisposition.Failed, ex.Message));
            }
        }
    }

    private void ClearPendingControllerCommandsFromQueue()
    {
        int dropped = _serialCommands.ClearPendingControllerState();
        lock (_latestStateUdpLock)
            dropped += _latestStateUdpSender?.ClearPending() ?? 0;
        LogDebug($"CLEAR QUEUED STATE COMMANDS dropped={dropped}", force: true);
    }

    private bool TrySubmitLatestStateUdp(
        BridgeControllerCommand command,
        BridgeControllerTransportSelection selection,
        out int superseded,
        out string lineFallbackReason)
    {
        superseded = 0;
        lineFallbackReason = selection.LineFallbackReason;
        if (selection.Path != BridgeControllerTransportPath.LatestStateUdp)
            return false;

        lock (_latestStateUdpLock)
        {
            if (_latestStateUdpSender is null ||
                !string.Equals(_latestStateUdpSender.Target, _networkAddress,
                    StringComparison.Ordinal))
            {
                if (_latestStateUdpSender is not null)
                {
                    _latestStateUdpSender.Stop();
                    _latestStateUdpSender.DisposeAsync().AsTask()
                        .GetAwaiter().GetResult();
                }
                _latestStateUdpSender = new BridgeLatestStateUdpControllerSender(
                    _networkAddress,
                    BridgeLatestStateUdpControllerSender.DefaultPort,
                    delivered => _controllerService.ConfirmDelivered(delivered),
                    (failed, _) =>
                    {
                        _controllerService.ReportNotDelivered(failed);
                    },
                    (sessionId, udpSequence, reported) =>
                        LogDebug($"LATEST_STATE REPORT session={sessionId} " +
                            $"seq={udpSequence} state={reported.WireCommand}"),
                    diagnostic => LogDebug(
                        BridgeLatestStateUdpDiagnosticFormatter.Format(diagnostic),
                        force: true));
                _serialCommands.ClearPendingControllerState();
            }
            superseded = _latestStateUdpSender.SubmitLatest(command);
            if (superseded < 0)
                lineFallbackReason = "latest-state-sender-stopped";
            return superseded >= 0;
        }
    }

    private BridgeControllerTransportSelection SelectCurrentControllerTransport() =>
        SelectControllerTransport(
            BridgeControllerState.CreateNeutral(_profile.Type));

    private BridgeControllerTransportSelection SelectControllerTransport(
        BridgeControllerState state) =>
        BridgeLatestStateControllerPathPolicy.Select(
            state,
            _bridgeDeviceState.Current,
            _tcpSession.Current?.IsConnected == true,
            _networkAddress);

    private BridgeControllerDeliveryGuard CurrentControllerDeliveryGuard()
    {
        bool lineGuardActive;
        lock (_controllerLineGuardLock)
            lineGuardActive = _controllerLineGuardDepth > 0;
        bool allDeliveryGuard = _firmwareModeSwitchInProgress ||
            _controllerPakOperationInProgress &&
                !_controllerContentOperationInProgress;
        return BridgeControllerDeliveryGuardPolicy.Select(
            allDeliveryGuard,
            _controllerContentOperationInProgress || lineGuardActive);
    }

    internal IBridgeControllerEndpointProvider ControllerEndpointProvider =>
        _controllerEndpointProvider;

    private BridgeControllerEndpointProtection CurrentControllerEndpointProtection() =>
        CurrentControllerDeliveryGuard() switch
        {
            BridgeControllerDeliveryGuard.LineOnly =>
                BridgeControllerEndpointProtection.LineOnly,
            BridgeControllerDeliveryGuard.All => BridgeControllerEndpointProtection.All,
            _ => BridgeControllerEndpointProtection.None
        };

    internal IBridgeControllerPrimaryService ControllerPrimaryService =>
        _controllerPrimaryService;

    private ValueTask SuspendLocalControllerForMobilePrimaryAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _controllerService.SuspendDelivery();
        StopLatestStateUdpSender();
        LogDebug("CONTROLLER PRIMARY nice-mobile; local delivery suspended");
        return ValueTask.CompletedTask;
    }

    private async ValueTask ResumeLocalControllerForBridgePrimaryAsync(
        bool resendCurrent,
        CancellationToken cancellationToken)
    {
        LogDebug($"CONTROLLER PRIMARY bridge; local delivery resume " +
            $"resendCurrent={resendCurrent}");
        _ = await _controllerService.ResumeDeliveryAsync(
            resendCurrent, cancellationToken).ConfigureAwait(false);
    }

    private string? GetVerifiedControllerTcpIpv4(BridgeSessionSnapshot session)
    {
        lock (_serialLock)
        {
            TcpBridgeTransport? transport = _tcpSession.Active;
            if (transport?.IsConnected != true ||
                session.State != BridgeSessionState.Connected ||
                session.ConnectionKind != BridgeControlConnectionKind.WifiTcp ||
                !string.Equals(session.DisplayEndpoint,
                    $"NICE64 {transport.DisplayName}", StringComparison.Ordinal))
                return null;

            int separator = transport.DisplayName.LastIndexOf(':');
            if (separator <= 0) return null;
            string candidate = transport.DisplayName[..separator];
            return System.Net.IPAddress.TryParse(candidate, out var address) &&
                address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? address.ToString()
                : null;
        }
    }

    private Task<BridgeControllerUsbWifiStatusResult>
        ReadFreshControllerUsbWifiStatusAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_operationCoordinator.Current.Status == BridgeOperationStatus.Running ||
                CurrentControllerEndpointProtection() !=
                    BridgeControllerEndpointProtection.None)
                return BridgeControllerUsbWifiStatusResult.Protected;
            if (!Monitor.TryEnter(_serialLock))
                return BridgeControllerUsbWifiStatusResult.Unavailable;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_operationCoordinator.Current.Status == BridgeOperationStatus.Running ||
                    CurrentControllerEndpointProtection() !=
                        BridgeControllerEndpointProtection.None)
                    return BridgeControllerUsbWifiStatusResult.Protected;
                if (_sessionService.Current is not
                    {
                        State: BridgeSessionState.Connected,
                        ConnectionKind: BridgeControlConnectionKind.SerialUsb
                    })
                    return BridgeControllerUsbWifiStatusResult.Unavailable;
                SerialPort serial = GetOpenSerialPort();
                serial.WriteLine(WifiProvisioningProtocol.StatusCommand);
                string reply = ReadWifiReplyLocked(serial,
                    WifiProvisioningProtocol.StatusCommand, cancellationToken);
                if (!WifiProvisioningProtocol.TryParseStatus(reply, out WifiStatus status))
                    return BridgeControllerUsbWifiStatusResult.Unavailable;
                return BridgeControllerUsbWifiStatusResult.ConfirmedStatus(
                    status.State.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase),
                    status.IpAddress);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return BridgeControllerUsbWifiStatusResult.Unavailable;
            }
            finally
            {
                Monitor.Exit(_serialLock);
            }
        }, cancellationToken);
    }

    private void EnterControllerLineGuard(string reason)
    {
        lock (_controllerLineGuardLock)
        {
            _controllerLineGuardDepth++;
            _serialCommands.ClearPendingControllerState();
        }
        LogDebug($"CONTROLLER LINE GUARD ENTER reason={reason}");
    }

    private void SuspendControllerDelivery(string reason)
    {
        LogDebug($"CONTROLLER DELIVERY SUSPEND reason={reason}");
        StopLatestStateUdpSender();
        _controllerService.SuspendDelivery();
    }

    private async Task<BridgeControllerResult> ResumeControllerDeliveryAsync(
        bool resendCurrent, string reason, CancellationToken token)
    {
        try
        {
            return await _controllerService.ResumeDeliveryAsync(resendCurrent, token)
                .ConfigureAwait(false);
        }
        finally
        {
            LogDebug($"CONTROLLER DELIVERY RESUME " +
                $"resendCurrent={resendCurrent.ToString().ToLowerInvariant()}");
        }
    }

    private void StopLatestStateUdpSender()
    {
        StopLatestStateUdpSenderAsync().GetAwaiter().GetResult();
    }

    private async Task StopLatestStateUdpSenderAsync()
    {
        BridgeLatestStateUdpControllerSender? sender;
        lock (_latestStateUdpLock)
        {
            sender = _latestStateUdpSender;
            _latestStateUdpSender = null;
            sender?.Stop();
        }
        if (sender is not null)
            await sender.DisposeAsync().ConfigureAwait(false);
    }

    private Task SerialWriterLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _serialSignal.WaitOne(_settings.SerialWriterWaitMs);

            _serialCommands.TryDequeue(out BridgeQueuedCommand? queuedCommand);

            string? command = queuedCommand?.WireCommand;
            BridgeControllerCommand? controllerState = queuedCommand?.ControllerState;
            bool isStateCommand = controllerState is not null;
            if (_firmwareModeSwitchInProgress)
            {
                if (command is not null)
                {
                    LogDebug($"SKIP QUEUED COMMAND DURING FIRMWARE MODE SWITCH {command}", force: true);
                }

                if (controllerState is not null)
                    _controllerService.ReportNotDelivered(controllerState);
                continue;
            }

            if (isStateCommand && _controllerPakOperationInProgress)
            {
                LogDebug($"SKIP DEQUEUED STATE DURING CONTROLLER PAK OP {command}", force: true);
                _controllerService.ReportNotDelivered(controllerState!);
                continue;
            }

            lock (_serialLock)
            {
                if (_firmwareModeSwitchInProgress)
                {
                    if (controllerState is not null)
                        _controllerService.ReportNotDelivered(controllerState);
                    continue;
                }
                IBridgeLineTransport? transport = ActiveLineTransportLocked();
                if (transport?.IsConnected != true)
                {
                    TcpBridgeTransport? observedTcp = _tcpSession.Current;
                    if (observedTcp is not null &&
                        _tcpSession.TryRetireObserved(observedTcp, out long retiredGeneration))
                    {
                        IOException loss = new("The TCP control connection closed.");
                        ReportNetworkPakLineActivationFailure(
                            BridgeNetworkPakTransport.Tcp, loss);
                        ConvergeNetworkPakCarrierDisconnected(loss.Message);
                        RunOnUiThread(() =>
                        {
                            if (!_tcpSession.IsStillRetired(retiredGeneration) ||
                                _tcpSession.Current is not null) return;
                            BridgeTransportFaultDisposition disposition =
                                _connectionService.ReportTcpControlLoss();
                            if (disposition ==
                                BridgeTransportFaultDisposition.ExpectedFirmwareReplacement)
                                return;
                            SetDisconnectedState();
                            SetStatus("NICE64 closed the TCP connection. Reconnect manually when ready.");
                        });
                        continue;
                    }

                    if (_serialPort is not null)
                    {
                        ScheduleAutoReconnectLocked(new IOException("The serial port closed unexpectedly."));
                    }

                    if (command is not null && !isStateCommand)
                    {
                        SetStatus("Connect to the Pico first.");
                    }

                    if (controllerState is not null)
                        _controllerService.ReportNotDelivered(controllerState);

                    continue;
                }

                try
                {
                    if (command is not null)
                    {
                        if (isStateCommand && _controllerPakOperationInProgress)
                        {
                            LogDebug($"SKIP WRITE STATE DURING CONTROLLER PAK OP {command}", force: true);
                            _controllerService.ReportNotDelivered(controllerState!);
                            continue;
                        }

                        transport.WriteLine(command);
                        if (!IsN64NoAccessoryBaseline)
                        {
                            LogDebug($"SEND {command}");
                        }

                        if (!isStateCommand)
                        {
                            SetStatus($"Sent {command}");
                        }
                        else
                        {
                            _controllerService.ConfirmDelivered(controllerState!);
                        }
                    }
                    IBridgeLineTransport? networkPakTransport =
                        SelectedNetworkPakLineTransportLocked(SelectedNetworkPakCarrier);
                    if (ActiveAccessory == BridgeAccessoryMode.NetworkPak &&
                        networkPakTransport?.IsConnected == true &&
                        !_controllerPakOperationInProgress && !_firmwareModeSwitchInProgress)
                    {
                        PollNetworkPakBridgeLinesLocked(networkPakTransport);
                    }
                }
                catch (Exception ex)
                {
                    if (controllerState is not null)
                        _controllerService.ReportNotDelivered(controllerState);
                    ReportNetworkPakLineActivationFailure(LineCarrier(transport), ex);
                    if (transport is TcpBridgeTransport failedTcp &&
                        _tcpSession.TryRetireObserved(failedTcp, out long retiredGeneration))
                    {
                        ConvergeNetworkPakCarrierDisconnected(ex.Message);
                        RunOnUiThread(() =>
                        {
                            if (!_tcpSession.IsStillRetired(retiredGeneration) ||
                                _tcpSession.Current is not null) return;
                            BridgeTransportFaultDisposition disposition =
                                _connectionService.ReportTcpControlLoss();
                            if (disposition ==
                                BridgeTransportFaultDisposition.ExpectedFirmwareReplacement)
                                return;
                            SetDisconnectedState();
                            SetStatus($"NICE64 TCP connection lost: {ex.Message}");
                        });
                    }
                    else
                    {
                        ScheduleAutoReconnectLocked(ex);
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    private void ScheduleAutoReconnectLocked(Exception ex)
    {
        string? portName = _serialPort?.PortName ??
            _connectionService.AutoReconnect.PreferredPort;
        ReportNetworkPakLineActivationFailure(
            BridgeNetworkPakTransport.UsbSerial, ex);
        CloseSerialPortLocked();
        _serialReceiveBuffer = string.Empty;
        BridgeTransportFaultDisposition disposition =
            _connectionService.ReportSerialControlLoss(
                _formClosing ? null : portName);
        ConvergeNetworkPakCarrierDisconnected(ex.Message);
        if (disposition == BridgeTransportFaultDisposition.ExpectedFirmwareReplacement)
        {
            LogDebug($"EXPECTED MODE-SWITCH DISCONNECT port={portName} detail={ex.Message}", force: true);
            RunOnUiThread(() =>
            {
                RefreshPorts(portName);
                SetStatus("Expected Pico reboot/disconnect during firmware mode switch.");
                UpdateFirmwareModeUi();
            });
            return;
        }
        if (disposition == BridgeTransportFaultDisposition.GenuineDisconnect)
            return;
        LogDebug($"SERIAL_FAULT port={portName} error={ex.Message}; auto reconnect scheduled", force: true);

        RunOnUiThread(() =>
        {
            RefreshPorts(portName);
            SetDisconnectedState();
            SetStatus($"Pico connection lost on {portName}: {ex.Message} Auto reconnecting...");
        });
    }

    private void ConvergeNetworkPakCarrierDisconnected(string reason) =>
        _ = ConvergeNetworkPakCarrierDisconnectedAsync(reason);

    private async Task ConvergeNetworkPakCarrierDisconnectedAsync(string reason)
    {
        BridgeNetworkPakCarrierResult result =
            await _networkPakCarrierService.ConvergeDisconnectedAsync(
                reason, CancellationToken.None).ConfigureAwait(false);
        ClearAllNetworkPakLineActivation();
        if (!result.Success)
        {
            LogDebug($"NETPAK CARRIER disconnect convergence failure=" +
                $"{result.Failure} {result.Diagnostic}", force: true);
        }
    }

    private async void SerialReconnectTimer_Tick(object? sender, EventArgs e)
    {
        BridgeSerialAutoReconnectSnapshot snapshot = _connectionService.AutoReconnect;
        if (_formClosing || !snapshot.Pending || _controllerPakOperationInProgress ||
            _firmwareModeSwitchInProgress || _serialPort?.IsOpen == true ||
            DateTimeOffset.UtcNow < snapshot.NextAttemptUtc) return;
        string portName = snapshot.PreferredPort;
        if (string.IsNullOrWhiteSpace(portName))
            return;
        try
        {
            RefreshPorts(portName);
        }
        catch (Exception ex)
        {
            SetStatus($"Waiting for serial devices: {ex.Message}");
            return;
        }
        BridgeAutoReconnectAttemptResult attempt =
            await _connectionService.TryAutoReconnectAsync(DateTimeOffset.UtcNow);
        if (attempt.Connection?.Success != true)
        {
            BridgeSerialAutoReconnectSnapshot next = _connectionService.AutoReconnect;
            SetStatus(attempt.Attempted
                ? $"Auto reconnect to {portName} failed: {attempt.Error}"
                : $"Waiting for {portName} to return...");
            LogDebug($"AUTO_RECONNECT pending={next.Pending} attempts={next.AttemptCount} " +
                $"next={next.NextAttemptUtc:O}", force: true);
            return;
        }
        SendMask();
        SetConnectedState(portName);
        await ReconcileNetworkPakConnectionPolicyAsync();
        SetStatus($"Reconnected to {portName}; restoring {ActiveAccessory} mode.");
    }

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (!InvokeRequired)
        {
            action();
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch
        {
            // The form may be closing while serial recovery is being scheduled.
        }
    }

    private async Task<BridgeAccessoryPortResult> ActivateSelectedNetworkPakCarrierAsync(
        CancellationToken cancellationToken)
    {
        BridgeNetworkPakCarrierResult result =
            await _networkPakCarrierService.ActivateSelectedAsync(cancellationToken)
                .ConfigureAwait(false);
        return result.Success
            ? BridgeAccessoryPortResult.Succeeded
            : new(false, result.Diagnostic ?? result.Failure.ToString());
    }

    private bool IsNetworkPakLineCarrierAvailable(BridgeNetworkPakTransport carrier)
    {
        lock (_serialLock)
        {
            if (carrier == BridgeNetworkPakTransport.UsbSerial &&
                _tcpSession.Active?.IsConnected == true)
                return false;
            return SelectedNetworkPakLineTransportLocked(carrier)?.IsConnected == true;
        }
    }

    private ValueTask<BridgeNetworkPakCarrierPortResult>
        BeginNetworkPakLineCarrierActivationAsync(
            BridgeNetworkPakTransport carrier,
            long generation,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            lock (_serialLock)
            {
                if (carrier == BridgeNetworkPakTransport.UsbSerial &&
                    _tcpSession.Active?.IsConnected == true)
                {
                    SetStatus("USB Network Pak carrier selected; disconnect Wi-Fi/TCP and connect by COM because the current firmware gives an active TCP session command ownership.");
                    LogDebug("NETPAK CARRIER USB blocked=TCP_CONTROL_ACTIVE", force: true);
                    return ValueTask.FromResult(new BridgeNetworkPakCarrierPortResult(
                        false, BridgeNetworkPakCarrierFailure.CarrierUnavailable,
                        "USB Network Pak carrier is blocked by the active TCP control session."));
                }

                IBridgeLineTransport? transport =
                    SelectedNetworkPakLineTransportLocked(carrier);
                if (transport?.IsConnected != true)
                {
                    SetStatus($"Selected Network Pak carrier is unavailable: " +
                        $"{NetworkPakCarrierDisplayName(carrier)}.");
                    return ValueTask.FromResult(new BridgeNetworkPakCarrierPortResult(
                        false, BridgeNetworkPakCarrierFailure.CarrierUnavailable,
                        "The explicitly selected Network Pak carrier is unavailable."));
                }

                _networkPakLineActivationCarrier = carrier;
                Interlocked.Exchange(ref _networkPakLineActivationGeneration, generation);
                _serialReceiveBuffer = string.Empty;
                transport.WriteLine("NETPAK_HELLO|1");
                LogDebug($"NETPAK SEND carrier={NetworkPakCarrierLogName(carrier)} " +
                    "NETPAK_HELLO|1", force: true);
                return ValueTask.FromResult(
                    BridgeNetworkPakCarrierPortResult.Succeeded);
            }
        }
        catch (Exception ex)
        {
            ClearNetworkPakLineActivation(carrier);
            LogDebug($"NETPAK HELLO FAIL carrier={carrier} {ex}", force: true);
            return ValueTask.FromResult(new BridgeNetworkPakCarrierPortResult(
                false, BridgeNetworkPakCarrierFailure.TransportFailed, ex.Message));
        }
    }

    private ValueTask<BridgeNetworkPakCarrierPortResult>
        ReleaseNetworkPakLineCarrierAsync(
            BridgeNetworkPakTransport carrier,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            lock (_serialLock)
            {
                IBridgeLineTransport? transport =
                    SelectedNetworkPakLineTransportLocked(carrier);
                if (transport?.IsConnected == true)
                {
                    LogDebug($"NETPAK SEND carrier={NetworkPakCarrierLogName(carrier)} " +
                        "NETPAK_BYE", force: true);
                    transport.WriteLine("NETPAK_BYE");
                }
                ClearNetworkPakLineActivation(carrier);
                return ValueTask.FromResult(
                    BridgeNetworkPakCarrierPortResult.Succeeded);
            }
        }
        catch (Exception ex)
        {
            LogDebug($"NETPAK BYE FAIL carrier={carrier} {ex}", force: true);
            return ValueTask.FromResult(new BridgeNetworkPakCarrierPortResult(
                false, BridgeNetworkPakCarrierFailure.ReleaseFailed, ex.Message));
        }
    }

    private void PollNetworkPakBridgeLinesLocked(IBridgeLineTransport transport)
    {
        if (SelectedNetworkPakCarrier != LineCarrier(transport))
        {
            return;
        }
        if (_controllerPakOperationInProgress ||
            (transport is SerialBridgeTransport && ActiveAccessory != BridgeAccessoryMode.NetworkPak))
        {
            return;
        }

        string available = transport.ReadExisting();
        if (available.Length == 0)
        {
            return;
        }

        _serialReceiveBuffer += available.Replace("\r", string.Empty);
        int newline;
        while ((newline = _serialReceiveBuffer.IndexOf('\n')) >= 0)
        {
            string line = _serialReceiveBuffer[..newline].Trim();
            _serialReceiveBuffer = _serialReceiveBuffer[(newline + 1)..];
            if (line.Length == 0)
            {
                continue;
            }

            if (!HandleNetworkPakLineLocked(transport, line))
            {
                LogDebug($"TCP RECEIVE {line}");
            }
        }
    }

    private async ValueTask<BridgeNetworkPakCarrierPortResult>
        StartNetworkPakQuickCarrierAsync(
            long generation,
            CancellationToken cancellationToken)
    {
        if (!BridgeNetworkPakQuickActivationPolicy.CanActivate(
                _bridgeDeviceState, _tcpSession.Current?.IsConnected == true))
        {
            SetStatus("Quick Mode requires a verified TCP connection to Standalone Network firmware.");
            return new(false, BridgeNetworkPakCarrierFailure.PrerequisiteNotMet,
                "Quick Mode requires a verified TCP connection to Network firmware.");
        }
        NetworkPakRudpTransport? previous = _networkPakRudpTransport;
        Action<Exception>? previousFaultHandler = _networkPakRudpFaultHandler;
        if (previous is not null)
        {
            if (previousFaultHandler is not null)
                previous.Faulted -= previousFaultHandler;
            try
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _networkPakRudpFaultHandler = null;
                LogDebug($"NETWORK RUDP PRIOR RELEASE FAIL {ex}", force: true);
                return new(false, BridgeNetworkPakCarrierFailure.TransportFailed,
                    ex.Message);
            }
        }
        _networkPakRudpTransport = null;
        _networkPakRudpFaultHandler = null;
        Interlocked.Exchange(ref _networkPakRudpGeneration, 0);
        try
        {
            NetworkPakRudpTransport transport = await NetworkPakRudpTransport.ConnectAsync(
                _networkAddress, NetworkPakRudpTransport.DefaultPort,
                HandleNetworkPakRudpRequest, TimeSpan.FromSeconds(5),
                message => LogDebug(message, force: true), cancellationToken)
                .ConfigureAwait(false);
            Action<Exception> faultHandler = error =>
                NetworkPakRudpFaulted(generation, error);
            transport.Faulted += faultHandler;
            _networkPakRudpTransport = transport;
            _networkPakRudpFaultHandler = faultHandler;
            Interlocked.Exchange(ref _networkPakRudpGeneration, generation);
            SetStatus($"Network Pak Quick Mode connected; RUDP session 0x{transport.SessionId:X8}.");
            return BridgeNetworkPakCarrierPortResult.Succeeded;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            SetStatus($"Network Pak Quick Mode failed: {ex.Message}");
            LogDebug($"NETWORK RUDP CONNECT FAIL {ex}", force: true);
            return new(false, BridgeNetworkPakCarrierFailure.TransportFailed,
                ex.Message);
        }
    }

    private async ValueTask<BridgeNetworkPakCarrierPortResult>
        StopNetworkPakQuickCarrierAsync(
            long generation,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NetworkPakRudpTransport? transport = _networkPakRudpTransport;
        if (transport is null)
            return BridgeNetworkPakCarrierPortResult.Succeeded;

        long currentGeneration = Interlocked.Read(ref _networkPakRudpGeneration);
        if (generation > 0 && currentGeneration > 0 && generation != currentGeneration)
        {
            return new(false, BridgeNetworkPakCarrierFailure.ReleaseFailed,
                "The requested Quick lifetime is no longer the current physical session.");
        }

        Action<Exception>? faultHandler = _networkPakRudpFaultHandler;
        if (faultHandler is not null)
            transport.Faulted -= faultHandler;
        try
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            _networkPakRudpTransport = null;
            _networkPakRudpFaultHandler = null;
            Interlocked.Exchange(ref _networkPakRudpGeneration, 0);
            return BridgeNetworkPakCarrierPortResult.Succeeded;
        }
        catch (Exception ex)
        {
            _networkPakRudpFaultHandler = null;
            LogDebug($"NETWORK RUDP RELEASE FAIL {ex}", force: true);
            return new(false, BridgeNetworkPakCarrierFailure.ReleaseFailed, ex.Message);
        }
    }

    private ReadOnlyMemory<byte>? HandleNetworkPakRudpRequest(ReadOnlyMemory<byte> request)
    {
        if (!_networkPakApplicationDispatcher.TryHandle(request.Span, out byte[] response))
        {
            LogDebug($"NETWORK RUDP NON-APPLICATION {Encoding.ASCII.GetString(request.Span)}", force: true);
            return null;
        }
        if (response.Length == 0)
        {
            LogDebug("NETWORK RUDP malformed Network Pak request", force: true);
            return null;
        }
        _networkPakApplicationDispatcher.MarkResponseWritten(response);
        LogDebug($"NETWORK RUDP {Encoding.ASCII.GetString(request.Span)} -> " +
            Encoding.ASCII.GetString(response), force: true);
        return response;
    }

    private void NetworkPakRudpFaulted(long generation, Exception error)
    {
        LogDebug($"NETWORK RUDP FAULT {error}", force: true);
        _ = ObserveNetworkPakRudpFaultAsync(generation, error);
    }

    private async Task ObserveNetworkPakRudpFaultAsync(long generation, Exception error)
    {
        BridgeNetworkPakCarrierResult result =
            await _networkPakCarrierService.ReportQuickFaultAsync(
                generation, error.Message, CancellationToken.None)
                .ConfigureAwait(false);
        if (result.Failure == BridgeNetworkPakCarrierFailure.Faulted)
        {
            RunOnUiThread(() => SetStatus(
                $"Network Pak Quick Mode failed: {error.Message}. " +
                "TCP fallback was not selected."));
        }
        else
        {
            LogDebug($"NETWORK RUDP STALE FAULT generation={generation}", force: true);
        }
    }

    private bool HandleNetworkPakLineLocked(IBridgeLineTransport transport, string line)
    {
        BridgeNetworkPakTransport source = LineCarrier(transport);
        if (SelectedNetworkPakCarrier != source)
        {
            LogDebug($"NETPAK IGNORE carrier={source} selected=" +
                NetworkPakCarrierLogName(SelectedNetworkPakCarrier), force: true);
            return true;
        }
        if (line == "NETPAK_HELLO_ACK|1")
        {
            long generation = Interlocked.Read(
                ref _networkPakLineActivationGeneration);
            if (generation > 0 && _networkPakLineActivationCarrier == source)
                _ = ObserveNetworkPakLineHandshakeAsync(source, generation);
            else
                LogDebug($"NETPAK stale handshake carrier={source}", force: true);
            return true;
        }

        byte[] request = Encoding.ASCII.GetBytes(line);
        if (!_networkPakApplicationDispatcher.TryHandle(request, out byte[] response))
        {
            return false;
        }

        if (response.Length > 0)
        {
            string responseLine = Encoding.ASCII.GetString(response);
            transport.WriteLine(responseLine);
            _networkPakApplicationDispatcher.MarkResponseWritten(response);
            LogDebug($"NETPAK REQUEST carrier={source} {line} -> {responseLine}");
        }
        else
        {
            LogDebug($"NETPAK malformed request: {line}", force: true);
        }
        return true;
    }

    private async Task ObserveNetworkPakLineHandshakeAsync(
        BridgeNetworkPakTransport carrier,
        long generation)
    {
        BridgeNetworkPakCarrierResult result =
            await _networkPakCarrierService.ObserveLineHandshakeAcknowledgedAsync(
                carrier, generation, CancellationToken.None).ConfigureAwait(false);
        if (result.Success)
        {
            ClearNetworkPakLineActivation(carrier, generation);
            LogDebug($"NETPAK handshake ready carrier={carrier}", force: true);
        }
        else
        {
            LogDebug($"NETPAK handshake rejected carrier={carrier} " +
                $"generation={generation} failure={result.Failure}", force: true);
        }
    }

    private void ReportNetworkPakLineActivationFailure(
        BridgeNetworkPakTransport carrier,
        Exception error)
    {
        long generation = Interlocked.Read(ref _networkPakLineActivationGeneration);
        if (generation <= 0 || _networkPakLineActivationCarrier != carrier) return;
        _ = ReportNetworkPakLineActivationFailureAsync(carrier, generation, error);
    }

    private async Task ReportNetworkPakLineActivationFailureAsync(
        BridgeNetworkPakTransport carrier,
        long generation,
        Exception error)
    {
        BridgeNetworkPakCarrierResult result =
            await _networkPakCarrierService.ReportLineActivationFailureAsync(
                carrier, generation, BridgeNetworkPakCarrierFailure.TransportFailed,
                error.Message, CancellationToken.None).ConfigureAwait(false);
        if (result.Committed)
            ClearNetworkPakLineActivation(carrier, generation);
    }

    private void ClearNetworkPakLineActivation(
        BridgeNetworkPakTransport carrier,
        long? generation = null)
    {
        if (_networkPakLineActivationCarrier != carrier) return;
        long current = Interlocked.Read(ref _networkPakLineActivationGeneration);
        if (generation.HasValue && generation.Value != current) return;
        _networkPakLineActivationCarrier = BridgeNetworkPakTransport.None;
        Interlocked.Exchange(ref _networkPakLineActivationGeneration, 0);
    }

    private void ClearAllNetworkPakLineActivation()
    {
        _networkPakLineActivationCarrier = BridgeNetworkPakTransport.None;
        Interlocked.Exchange(ref _networkPakLineActivationGeneration, 0);
    }

    private async Task<BridgeAccessoryPortResult> ReleaseSelectedNetworkPakCarrierAsync(
        CancellationToken cancellationToken)
    {
        BridgeNetworkPakCarrierResult result =
            await _networkPakCarrierService.ReleaseActiveAsync(cancellationToken)
                .ConfigureAwait(false);
        return result.Success
            ? BridgeAccessoryPortResult.Succeeded
            : new(false, result.Diagnostic ?? result.Failure.ToString());
    }

    private async Task PollGamepadLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            PollGamepadOnce();

            try
            {
                await Task.Delay(_settings.GamepadPollIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void PollGamepadOnce()
    {
        if (IsN64NoAccessoryBaseline)
        {
            PollGamepadOnceBaseline();
            return;
        }

        uint nextGamepadMask;
        bool readAnyInput = TryReadGamepadMask(out nextGamepadMask, out string sourceLabel, out AnalogStickOutput? analogOutput);
        if (!readAnyInput)
        {
            return;
        }

        nextGamepadMask = ApplyMinimumPressDuration(nextGamepadMask);

        bool maskChanged;
        bool analogChanged;
        uint previousGamepadMask;
        lock (_maskLock)
        {
            previousGamepadMask = _gamepadMask;
            maskChanged = nextGamepadMask != _gamepadMask;
            analogChanged = analogOutput is not null
                && (!_gamepadAnalogActive || analogOutput.Value.X.N64 != _gamepadAnalogX || analogOutput.Value.Y.N64 != _gamepadAnalogY);
            _gamepadMask = nextGamepadMask;
            _gamepadAnalogActive = analogOutput is not null;
            _gamepadAnalogX = analogOutput?.X.N64 ?? 0;
            _gamepadAnalogY = analogOutput?.Y.N64 ?? 0;
        }

        if (maskChanged)
        {
            LogMaskTransitions($"GAMEPAD/{sourceLabel}", previousGamepadMask, nextGamepadMask);
            SendCombinedMask();
        }
        else if (analogChanged)
        {
            SendMask();
        }
    }

    private void PollGamepadOnceBaseline()
    {
        uint nextGamepadMask;
        if (_xinputController?.IsConnected == true)
        {
            nextGamepadMask = ControllerProfile.ReadXInputMask(_profile.Type, _xinputController, _profile.ButtonBits);
        }
        else
        {
            JoystickState? state = ReadJoystick();
            if (state is null)
            {
                return;
            }

            nextGamepadMask = ReadDirectInputMask(state);
        }

        nextGamepadMask = ApplyMinimumPressDuration(nextGamepadMask);

        bool changed;
        uint previousGamepadMask;
        lock (_maskLock)
        {
            previousGamepadMask = _gamepadMask;
            changed = nextGamepadMask != _gamepadMask;
            _gamepadMask = nextGamepadMask;
        }

        if (changed)
        {
            LogMaskTransitions("GAMEPAD", previousGamepadMask, nextGamepadMask);
            SendCombinedMask();
        }
    }

    private bool TryReadGamepadMask(out uint mask, out string sourceLabel, out AnalogStickOutput? analogOutput)
    {
        mask = 0;
        sourceLabel = GetInputSourceLabel(_inputSourceMode);
        analogOutput = null;

        if (_inputSourceMode == InputSourceMode.None)
        {
            return false;
        }

        if (_profile.Type == ConsoleControllerType.N64 && _inputSourceMode == InputSourceMode.Auto)
        {
            if (!_n64AutoInputBlockedStatusShown)
            {
                _n64AutoInputBlockedStatusShown = true;
                SetStatus("N64 input source must be XInput or DirectInput. Auto is disabled for validation.");
                LogDebug("GAMEPAD/Auto blocked for N64; select XInput or DirectInput.", force: true);
            }

            return false;
        }

        if (_inputSourceMode is InputSourceMode.Auto or InputSourceMode.XInput
            && _xinputController?.IsConnected == true)
        {
            mask = ControllerProfile.ReadXInputMask(_profile.Type, _xinputController, _profile.ButtonBits);
            analogOutput = TryMapActiveAnalogCalibration();
            sourceLabel = "XInput / Xbox-compatible";
            return true;
        }

        if (_inputSourceMode is InputSourceMode.Auto or InputSourceMode.DirectInput or InputSourceMode.EightBitDoParsec)
        {
            JoystickState? state = ReadJoystick();
            if (state is not null)
            {
                mask = _inputSourceMode == InputSourceMode.EightBitDoParsec
                    ? ReadDirectInputMask(state, _eightBitDoParsecMapping, EightBitDoParsecAxisThreshold)
                    : ReadDirectInputMask(state, _mapping);
                analogOutput = TryMapActiveAnalogCalibration(state);
                LogDirectInputRawStateIfChanged(state, mask);
                sourceLabel = _inputSourceMode == InputSourceMode.EightBitDoParsec
                    ? "8BitDo / Parsec"
                    : "Generic USB";
                return true;
            }
        }

        if (_inputSourceMode == InputSourceMode.XInput)
        {
            TryAcquireXInputController();
        }
        else if (_inputSourceMode is InputSourceMode.DirectInput or InputSourceMode.EightBitDoParsec)
        {
            TryAcquireFirstGamepad();
        }

        return false;
    }

    private uint ApplyMinimumPressDuration(uint rawMask)
    {
        if (_settings.MinimumPressDurationMs <= 0)
        {
            return rawMask;
        }

        uint heldMask = rawMask;
        long now = Environment.TickCount64;
        long holdUntil = now + _settings.MinimumPressDurationMs;

        for (int bit = 0; bit < _gamepadReleaseTicks.Length; bit++)
        {
            uint bitMask = 1u << bit;
            if ((rawMask & bitMask) != 0)
            {
                _gamepadReleaseTicks[bit] = holdUntil;
            }
            else if (now < _gamepadReleaseTicks[bit])
            {
                heldMask |= bitMask;
            }
        }

        return heldMask;
    }

    private uint ReadDirectInputMask(JoystickState state)
    {
        return ReadDirectInputMask(state, _mapping);
    }

    private uint ReadDirectInputMask(JoystickState state, ControllerMapping mapping, int axisThreshold = GamepadInput.AxisThreshold)
    {
        uint nextGamepadMask = 0;
        foreach ((string buttonName, int bit) in _profile.ButtonBits)
        {
            if (mapping.Bindings.TryGetValue(buttonName, out InputBinding? binding)
                && GamepadInput.IsBindingActive(binding, state, axisThreshold))
            {
                nextGamepadMask |= 1u << bit;
            }
        }

        return nextGamepadMask;
    }

    private AnalogStickOutput? TryMapActiveAnalogCalibration(JoystickState? directInputState = null)
    {
        if (_profile.Type != ConsoleControllerType.N64 || _activeAnalogCalibration is null)
        {
            return null;
        }

        AnalogStickSnapshot? snapshot;
        if (directInputState is not null
            && _inputSourceMode is InputSourceMode.DirectInput or InputSourceMode.EightBitDoParsec)
        {
            ControllerMapping mapping = _inputSourceMode == InputSourceMode.EightBitDoParsec
                ? _eightBitDoParsecMapping
                : _mapping;
            if (!TryResolveAnalogAxes(mapping, directInputState, out int rawX, out int rawY, out int xDirection, out int yDirection))
            {
                return null;
            }

            string sourceKind = _inputSourceMode == InputSourceMode.EightBitDoParsec
                ? "DirectInput-8BitDoParsec"
                : "DirectInput";
            snapshot = new AnalogStickSnapshot(
                sourceKind,
                string.IsNullOrWhiteSpace(_directInputDeviceId) ? "directinput" : _directInputDeviceId,
                string.IsNullOrWhiteSpace(_directInputDeviceName) ? "DirectInput controller" : _directInputDeviceName,
                rawX,
                rawY,
                xDirection,
                yDirection);
        }
        else
        {
            snapshot = ReadCurrentAnalogSnapshot();
        }

        if (snapshot is null)
        {
            return null;
        }

        string key = $"{snapshot.SourceKind}|{snapshot.DeviceId}";
        if (!key.Equals(_activeAnalogCalibrationKey, StringComparison.Ordinal))
        {
            LoadActiveAnalogCalibration();
        }

        return _activeAnalogCalibration?.Map(
            snapshot.RawX,
            snapshot.RawY,
            snapshot.PositiveXRawDirection,
            snapshot.PositiveYRawDirection);
    }

    private void LogDirectInputRawStateIfChanged(JoystickState state, uint logicalMask)
    {
        if (_inputSourceMode is not (InputSourceMode.DirectInput or InputSourceMode.EightBitDoParsec)
            || _profile.Type != ConsoleControllerType.N64)
        {
            return;
        }

        List<int> activeButtons = [];
        bool[] buttons = state.Buttons;
        for (int index = 0; index < buttons.Length; index++)
        {
            if (buttons[index])
            {
                activeButtons.Add(index);
            }
        }

        int pov = state.PointOfViewControllers.FirstOrDefault(-1);
        string signature = $"buttons={string.Join(',', activeButtons)} pov={pov} " +
            $"axes=X:{state.X} Y:{state.Y} Z:{state.Z} Rx:{state.RotationX} Ry:{state.RotationY} Rz:{state.RotationZ} " +
            $"logical=0x{logicalMask:X8}";

        if (signature == _lastDirectInputRawSignature)
        {
            return;
        }

        _lastDirectInputRawSignature = signature;
        LogDebug($"DIRECTINPUT RAW {signature}", force: true);
    }

    private string FormatPressedButtons(uint mask)
    {
        if (mask == 0)
        {
            return "<none>";
        }

        List<string> names = [];
        foreach (ControllerButtonDefinition definition in _profile.Buttons)
        {
            if ((mask & (1u << definition.Bit)) != 0)
            {
                names.Add(definition.Name);
            }
        }

        return string.Join(",", names);
    }

    private void LogMaskTransitions(string source, uint previousMask, uint nextMask)
    {
        uint changed = previousMask ^ nextMask;
        if (changed == 0)
        {
            return;
        }

        foreach (ControllerButtonDefinition definition in _profile.Buttons)
        {
            uint bitMask = 1u << definition.Bit;
            if ((changed & bitMask) == 0)
            {
                continue;
            }

            string state = (nextMask & bitMask) != 0 ? "DOWN" : "UP";
            LogDebug($"{source} {state} {definition.Name} bit={definition.Bit}");
        }
    }

    private JoystickState? ReadJoystick()
    {
        lock (_joystickLock)
        {
            if (_joystick is null)
            {
                return null;
            }

            try
            {
                _joystick.Poll();
                return _joystick.GetCurrentState();
            }
            catch
            {
                ReleaseJoystick();
                lock (_maskLock)
                {
                    _gamepadMask = 0;
                    _gamepadAnalogActive = false;
                    _gamepadAnalogX = 0;
                    _gamepadAnalogY = 0;
                }

                SendCombinedMask();
                SetStatus("USB controller disconnected.");
                return null;
            }
        }
    }

    private async Task DisconnectFromUiAsync()
    {
        if (!TryBeginDesktopOperation(BridgeOperationKind.Disconnect, "Connection",
                BridgeOperationCancellationCapability.BeforeCommitOnly,
                out BridgeOperationLease? operation))
            return;
        using BridgeOperationLease lease = operation!;
        await ReleaseInputCaptureAsync("disconnect");
        BridgeDisconnectResult result = await _connectionService.DisconnectAsync(
            BridgeDisconnectReason.UserRequest, CancellationToken.None);
        CompleteDesktopOperation(lease, result.Success,
            result.Failure == BridgeDisconnectFailure.Cancelled,
            result.Failure.ToString(), result.Error, result);
        if (result.Failure == BridgeDisconnectFailure.BlockedByPersistence)
        {
            SetStatus($"Disconnect blocked: persistence failed: {result.Persistence.Error}");
            LogDebug($"PERSISTENCE DISCONNECT BLOCKED error={result.Persistence.Error}",
                force: true);
            return;
        }
        if (!result.Success && result.Failure is not
            BridgeDisconnectFailure.AlreadyDisconnected)
        {
            if (result.Failure == BridgeDisconnectFailure.CleanupFailure)
                SetDisconnectedState();
            SetStatus($"Disconnect failed: {result.Error}");
            return;
        }
        SetDisconnectedState();
    }

    private async Task<BridgeDisconnectCleanupResult> PerformDisconnectCleanupAsync(
        bool sendNeutralState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LogDebug($"DISCONNECT CLEANUP sendNeutralState={sendNeutralState}");
        SetXInputRumble(false);

        if (sendNeutralState)
            await _keyboardMouseInput.ClearAndSendNeutralAsync().ConfigureAwait(false);
        else
            _keyboardMouseInput.ClearWithoutDelivery();

        lock (_maskLock)
        {
            _manualMask = 0;
            _gamepadMask = 0;
            _gamepadAnalogActive = false;
            _gamepadAnalogX = 0;
            _gamepadAnalogY = 0;
        }

        BridgeNetworkPakCarrierResult carrierCleanup = _formClosing
            ? await _networkPakCarrierService.ShutdownAsync(CancellationToken.None)
                .ConfigureAwait(false)
            : await _networkPakCarrierService.RetirePhysicalCarrierAsync(
                    "Control disconnect cleanup", CancellationToken.None)
                .ConfigureAwait(false);
        if (!carrierCleanup.Success && carrierCleanup.Failure !=
            BridgeNetworkPakCarrierFailure.None)
        {
            LogDebug($"NETPAK CARRIER disconnect cleanup failure=" +
                $"{carrierCleanup.Failure} {carrierCleanup.Diagnostic}", force: true);
        }
        ClearAllNetworkPakLineActivation();
        await StopLatestStateUdpSenderAsync().ConfigureAwait(false);
        try
        {
            lock (_serialLock)
            {
                IBridgeLineTransport? transport = ActiveLineTransportLocked();
                if (transport?.IsConnected == true)
                {
                    if (sendNeutralState)
                    {
                        // The controller service owns semantic neutral construction and
                        // reports delivery truthfully; disconnect still completes if the
                        // Pico is already wedged or unplugged.
                        _controllerService.NeutralizeForDisconnectAsync(
                            _profile.Type, CancellationToken.None)
                            .GetAwaiter().GetResult();
                    }
                }

                CloseTcpTransportLocked();
                CloseSerialPortLocked();
            }
        }
        finally
        {
            _serialReceiveBuffer = string.Empty;
        }
        return new(true, true);
    }

    private void CloseSerialPortLocked()
    {
        SerialPort? serial = _serialPort;
        _serialPort = null;
        _serialBridgeTransport = null;

        try
        {
            serial?.Close();
        }
        catch
        {
        }

        try
        {
            serial?.Dispose();
        }
        catch
        {
        }
    }

    private void CloseTcpTransportLocked()
    {
        try { _tcpSession.RetireCurrent(); } catch { }
    }

    private void StopVideoPreview()
    {
        CloseFullScreenVideo();
        _videoPreview?.Dispose();
        _videoPreview = null;
        SetVideoPreviewState(isRunning: false);
        SetStatus("Video preview stopped.");
    }

    private void StopAudioPassthrough()
    {
        _audioPassthrough?.Dispose();
        _audioPassthrough = null;
        SetAudioPassthroughState(isRunning: false);
        SetStatus("Audio passthrough stopped.");
    }

    private void CloseFullScreenVideo()
    {
        Form? form = _fullScreenVideoForm;
        _fullScreenVideoForm = null;
        _fullScreenVideoPanel = null;

        if (form is not null && !form.IsDisposed)
        {
            form.Close();
            return;
        }

        if (_videoPreview is not null && !videoPictureBox.IsDisposed)
        {
            _videoPreview.SetOwner(videoPictureBox.Handle);
            ResizeVideoPreview();
        }
    }

    private void ResizeVideoPreview()
    {
        Control owner = _fullScreenVideoPanel is not null ? _fullScreenVideoPanel : videoPictureBox;
        _videoPreview?.Resize(owner.ClientSize.Width, owner.ClientSize.Height);
    }

    private void SetVideoPreviewState(bool isRunning)
    {
        startVideoButton.Enabled = !isRunning;
        startVideoMenuItem.Enabled = !isRunning;
        stopVideoButton.Enabled = isRunning;
        stopVideoMenuItem.Enabled = isRunning;
        videoDeviceComboBox.Enabled = !isRunning;
        refreshVideoButton.Enabled = !isRunning;
        videoWidthNumeric.Enabled = !isRunning;
        videoHeightNumeric.Enabled = !isRunning;
        videoFpsNumeric.Enabled = !isRunning;
    }

    private void SetAudioPassthroughState(bool isRunning)
    {
        startAudioButton.Enabled = !isRunning;
        stopAudioButton.Enabled = isRunning;
        audioDeviceComboBox.Enabled = !isRunning;
    }

    private void SetAudioVolume()
    {
        audioVolumeValueLabel.Text = $"{audioVolumeTrackBar.Value}%";
        _audioPassthrough?.SetVolumePercent(audioVolumeTrackBar.Value);
    }

    private void SetConnectedState(string portName, bool queryFirmware = true)
    {
        connectButton.Text = "Disconnect";
        consoleComboBox.Enabled = false;
        portComboBox.Enabled = false;
        refreshButton.Enabled = false;
        clearButton.Enabled = true;
        forceLowButton.Enabled = _profile.Type == ConsoleControllerType.Snes;
        forceHighButton.Enabled = _profile.Type == ConsoleControllerType.Snes;
        forceOffButton.Enabled = true;
        n64StatButton.Enabled = _profile.Type == ConsoleControllerType.N64;
        n64ClearDebugButton.Enabled = _profile.Type == ConsoleControllerType.N64;

        foreach (Control control in buttonPad.Controls)
        {
            control.Enabled = true;
        }

        ApplyDebugModeUi();
        ApplyOnScreenControlsUi();
        SetStatus($"Connected to {portName}");

        _firmwareModeState.Connected();
        UpdateFirmwareModeUi();
        if (queryFirmware)
        {
            _ = QueryFirmwareModeCoreAsync(operation: null);
        }
    }

    private void SetDisconnectedState()
    {
        ReleaseInputCaptureWithoutDelivery("disconnected");
        StopLatestStateUdpSender();
        connectButton.Text = "Connect";
        consoleComboBox.Enabled = true;
        portComboBox.Enabled = true;
        refreshButton.Enabled = _connectionKind == BridgeConnectionKind.Usb;
        clearButton.Enabled = false;
        forceLowButton.Enabled = _profile.Type == ConsoleControllerType.Snes;
        forceHighButton.Enabled = _profile.Type == ConsoleControllerType.Snes;
        forceOffButton.Enabled = _profile.Type == ConsoleControllerType.Snes;
        n64StatButton.Enabled = false;
        n64ClearDebugButton.Enabled = false;

        foreach (Control control in buttonPad.Controls)
        {
            control.Enabled = false;
        }

        ApplyDebugModeUi();
        ApplyOnScreenControlsUi();
        SetStatus("Disconnected");
        _activeFirmwareMode = FirmwarePersonality.Unknown;
        _firmwareModeState.Disconnected();
        UpdateFirmwareModeUi();
    }

    private IBridgeDeviceStateUpdater StateUpdater => _bridgeDeviceState;
    private BridgeAccessoryMode ActiveAccessory =>
        _bridgeDeviceState.Current.ActiveAccessory;
    private BridgeNetworkPakTransport SelectedNetworkPakCarrier =>
        _bridgeDeviceState.Current.SelectedNetworkPakTransport;
    private BridgeNetworkPakTransport PresentedNetworkPakCarrier =>
        BridgeNetworkPakCarrierTransitionPolicy.IsSelectable(SelectedNetworkPakCarrier)
            ? SelectedNetworkPakCarrier
            : _networkPakPresentationCarrier;

    private BridgeNetworkPakTransport NetworkPakCarrierSelectionForSession()
    {
        BridgeNetworkPakTransport selected = SelectedNetworkPakCarrier;
        if (BridgeNetworkPakCarrierTransitionPolicy.IsSelectable(selected))
        {
            _networkPakPresentationCarrier = selected;
            return selected;
        }
        return _networkPakPresentationCarrier;
    }

    private static string NetworkPakCarrierDisplayName(
        BridgeNetworkPakTransport carrier) => carrier switch
        {
            BridgeNetworkPakTransport.UsbSerial => "USB",
            BridgeNetworkPakTransport.Tcp => "TCP",
            BridgeNetworkPakTransport.ReliableUdpQuick =>
                "Quick Mode (Reliable UDP)",
            _ => carrier.ToString()
        };

    private static string NetworkPakCarrierLogName(
        BridgeNetworkPakTransport carrier) => carrier switch
        {
            BridgeNetworkPakTransport.UsbSerial => "USB",
            BridgeNetworkPakTransport.Tcp => "TCP",
            BridgeNetworkPakTransport.ReliableUdpQuick => "RUDP_QUICK",
            _ => "NONE"
        };

    private void SetStatus(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => SetStatus(message));
            }
            catch
            {
                // The form may be closing while a background loop is reporting status.
            }

            return;
        }

        statusLabel.Text = message;
    }

    private void ButtonPad_Paint(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.Clear(Color.FromArgb(246, 245, 242));

        if (_profile.Type == ConsoleControllerType.N64)
        {
            if (_n64ControllerArt is not null)
            {
                DrawControllerArt(e.Graphics, _n64ControllerArt, cropVerticalPadding: 120);
            }
            else
            {
                DrawN64Controller(e.Graphics);
            }
        }
        else
        {
            if (_snesControllerArt is not null)
            {
                DrawControllerArt(e.Graphics, _snesControllerArt, cropVerticalPadding: 20);
            }
            else
            {
                DrawSnesController(e.Graphics);
            }
        }
    }

    private void LoadControllerArt()
    {
        _n64ControllerArt = LoadControllerArt("controller-n64-sketch.png");
        _snesControllerArt = LoadControllerArt("controller-snes-sketch.png");
    }

    private static Image? LoadControllerArt(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        using Image image = Image.FromFile(path);
        return new Bitmap(image);
    }

    private void DrawControllerArt(Graphics graphics, Image image, int cropVerticalPadding)
    {
        Rectangle destination = GetControllerArtStage();
        Rectangle source = GetCoverSourceRectangle(image, destination.Size, cropVerticalPadding);

        graphics.DrawImage(image, destination, source, GraphicsUnit.Pixel);

        using SolidBrush paperWash = new(Color.FromArgb(32, 255, 255, 255));
        graphics.FillRectangle(paperWash, destination);

        using Pen framePen = new(Color.FromArgb(50, 70, 70, 70), 1);
        graphics.DrawRoundedRectangle(framePen, Rectangle.Inflate(destination, -1, -1), 10);
    }

    private Rectangle GetControllerArtStage()
    {
        Rectangle area = Rectangle.Inflate(buttonPad.ClientRectangle, -10, -10);
        int height = Math.Min(area.Height, 300);
        return new Rectangle(area.Left, area.Top, area.Width, height);
    }

    private static Rectangle GetCoverSourceRectangle(Image image, Size destinationSize, int cropVerticalPadding)
    {
        Rectangle paddedSource = new(
            0,
            Math.Min(cropVerticalPadding, image.Height / 4),
            image.Width,
            Math.Max(1, image.Height - Math.Min(cropVerticalPadding * 2, image.Height / 2)));

        double destinationAspect = (double)destinationSize.Width / Math.Max(1, destinationSize.Height);
        double sourceAspect = (double)paddedSource.Width / paddedSource.Height;

        if (sourceAspect > destinationAspect)
        {
            int width = Math.Max(1, (int)Math.Round(paddedSource.Height * destinationAspect));
            int x = paddedSource.X + (paddedSource.Width - width) / 2;
            return new Rectangle(x, paddedSource.Y, width, paddedSource.Height);
        }

        int height = Math.Max(1, (int)Math.Round(paddedSource.Width / destinationAspect));
        int y = paddedSource.Y + (paddedSource.Height - height) / 2;
        return new Rectangle(paddedSource.X, y, paddedSource.Width, height);
    }

    private static void DrawSnesController(Graphics graphics)
    {
        using SolidBrush bodyBrush = new(Color.FromArgb(232, 232, 228));
        using SolidBrush shoulderBrush = new(Color.FromArgb(220, 220, 218));
        using SolidBrush darkBrush = new(Color.FromArgb(58, 65, 68));
        using SolidBrush wellBrush = new(Color.FromArgb(132, 140, 140));
        using Pen outlinePen = new(Color.FromArgb(120, 120, 120), 2);
        using Pen guidePen = new(Color.FromArgb(160, 160, 160), 1);

        graphics.FillRoundedRectangle(shoulderBrush, new Rectangle(124, 48, 96, 34), 12);
        graphics.FillRoundedRectangle(shoulderBrush, new Rectangle(410, 48, 96, 34), 12);
        graphics.FillRoundedRectangle(bodyBrush, new Rectangle(72, 72, 486, 178), 58);
        graphics.DrawRoundedRectangle(outlinePen, new Rectangle(72, 72, 486, 178), 58);
        graphics.FillEllipse(Brushes.WhiteSmoke, new Rectangle(90, 86, 190, 150));
        graphics.DrawEllipse(guidePen, new Rectangle(90, 86, 190, 150));
        graphics.FillEllipse(wellBrush, new Rectangle(402, 82, 166, 154));
        graphics.DrawEllipse(outlinePen, new Rectangle(402, 82, 166, 154));

        graphics.FillRoundedRectangle(darkBrush, new Rectangle(126, 144, 104, 42), 10);
        graphics.FillRoundedRectangle(darkBrush, new Rectangle(157, 112, 42, 106), 10);
        graphics.FillEllipse(new SolidBrush(Color.FromArgb(45, 50, 53)), new Rectangle(166, 154, 24, 24));

        graphics.FillRoundedRectangle(darkBrush, new Rectangle(288, 154, 26, 54), 9);
        graphics.FillRoundedRectangle(darkBrush, new Rectangle(378, 154, 26, 54), 9);

        graphics.FillEllipse(Brushes.MediumSeaGreen, new Rectangle(434, 140, 48, 48));
        graphics.FillEllipse(Brushes.RoyalBlue, new Rectangle(500, 94, 48, 48));
        graphics.FillEllipse(Brushes.Gold, new Rectangle(500, 188, 48, 48));
        graphics.FillEllipse(Brushes.Tomato, new Rectangle(558, 142, 48, 48));
    }

    private static void DrawN64Controller(Graphics graphics)
    {
        using SolidBrush bodyBrush = new(Color.FromArgb(202, 202, 200));
        using SolidBrush darkBrush = new(Color.FromArgb(80, 80, 80));
        using Pen outlinePen = new(Color.FromArgb(90, 90, 90), 2);

        Point[] body =
        [
            new(88, 60), new(176, 42), new(226, 86), new(250, 232),
            new(310, 278), new(370, 232), new(394, 86), new(454, 42),
            new(544, 62), new(604, 208), new(554, 274), new(458, 214),
            new(376, 194), new(314, 306), new(252, 194), new(170, 214),
            new(74, 274), new(24, 208)
        ];
        graphics.FillPolygon(bodyBrush, body);
        graphics.DrawPolygon(outlinePen, body);

        graphics.FillRoundedRectangle(darkBrush, new Rectangle(80, 126, 112, 42), 8);
        graphics.FillRoundedRectangle(darkBrush, new Rectangle(115, 92, 42, 112), 8);
        graphics.FillEllipse(new SolidBrush(Color.FromArgb(92, 92, 92)), new Rectangle(286, 170, 52, 52));
        graphics.FillEllipse(new SolidBrush(Color.FromArgb(210, 210, 210)), new Rectangle(300, 184, 24, 24));
        graphics.FillEllipse(Brushes.Red, new Rectangle(294, 108, 34, 34));
        graphics.FillEllipse(Brushes.Green, new Rectangle(426, 134, 42, 42));
        graphics.FillEllipse(Brushes.RoyalBlue, new Rectangle(490, 156, 42, 42));

        graphics.FillEllipse(Brushes.Gold, new Rectangle(546, 68, 28, 28));
        graphics.FillEllipse(Brushes.Gold, new Rectangle(512, 104, 28, 28));
        graphics.FillEllipse(Brushes.Gold, new Rectangle(580, 104, 28, 28));
        graphics.FillEllipse(Brushes.Gold, new Rectangle(546, 140, 28, 28));
        graphics.FillRoundedRectangle(darkBrush, new Rectangle(294, 236, 44, 54), 8);
    }

    private sealed record ConsoleItem(ConsoleControllerType Type, string Name)
    {
        public override string ToString() => Name;
    }

    private enum InputSourceMode
    {
        None,
        Auto,
        XInput,
        DirectInput,
        EightBitDoParsec
    }

    private sealed class ControllerHotspotButton : Control
    {
        private bool _hovered;
        private bool _pressed;

        public ControllerHotspotButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.UserPaint,
                true);

            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = new Font(FontFamily.GenericSansSerif, 8.5f, FontStyle.Bold);
            ForeColor = Color.FromArgb(38, 42, 48);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle bounds = new(1, 1, Width - 3, Height - 3);
            Color fillColor = Enabled
                ? _pressed
                    ? Color.FromArgb(230, 42, 46, 52)
                    : _hovered
                        ? Color.FromArgb(232, 255, 255, 255)
                        : Color.FromArgb(205, 255, 255, 255)
                : Color.FromArgb(150, 245, 245, 245);
            Color borderColor = Enabled
                ? Color.FromArgb(125, 64, 68, 74)
                : Color.FromArgb(85, 120, 120, 120);
            Color textColor = _pressed && Enabled
                ? Color.White
                : Enabled
                    ? ForeColor
                    : Color.FromArgb(120, 120, 120);

            using SolidBrush shadowBrush = new(Color.FromArgb(35, 0, 0, 0));
            e.Graphics.FillRoundedRectangle(shadowBrush, new Rectangle(bounds.X + 1, bounds.Y + 2, bounds.Width, bounds.Height), 6);

            using SolidBrush fillBrush = new(fillColor);
            using Pen borderPen = new(borderColor, 1);
            e.Graphics.FillRoundedRectangle(fillBrush, bounds, 6);
            e.Graphics.DrawRoundedRectangle(borderPen, bounds, 6);

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                bounds,
                textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            _rawInput.Register(Handle);
            _knownRawDeviceIds = _rawInput.Devices.Select(device => device.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            RebuildKeyboardMouseSourceMenus();
            LogDebug($"RAW INPUT registered devices={_knownRawDeviceIds.Count}",
                force: true);
        }
        catch (Exception ex)
        {
            LogDebug($"RAW INPUT registration failed: {ex.Message}", force: true);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_keyboardMouseInput.CaptureActive && !_formClosing)
            _ = ReleaseInputCaptureAsync("capture window destroyed");
        _rawInput.Dispose();
        if (_formClosing)
            _keyboardMouseInput.ClearWithoutDelivery();
        else
            _keyboardMouseInput.SetActiveMode(_keyboardMouseInput.ActiveMode);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        try
        {
            _rawInput.ProcessWindowMessage(message.Msg, message.WParam,
                message.LParam);
        }
        catch (Exception ex)
        {
            LogDebug($"RAW INPUT message failed: {ex.Message}", force: true);
        }
        base.WndProc(ref message);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_keyboardMouseInput.CaptureActive &&
            (keyData & Keys.Alt) == Keys.Alt &&
            (keyData & Keys.KeyCode) == Keys.Left)
        {
            _ = ReleaseInputCaptureAsync("Alt+Left Arrow");
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        if (_keyboardMouseInput.CaptureActive)
            _ = ReleaseInputCaptureAsync("Bridge lost focus");
        base.OnDeactivate(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        BridgeDesktopCloseDisposition disposition = _shutdownCoordinator.RequestClose();
        if (disposition != BridgeDesktopCloseDisposition.AllowFinalClose)
        {
            e.Cancel = true;
            if (disposition == BridgeDesktopCloseDisposition.CancelAndStartShutdown)
            {
                _formClosing = true;
                Enabled = false;
                LogDebug("SHUTDOWN REQUESTED", force: true);
            }
        }
        base.OnFormClosing(e);
    }

    private async Task ShutdownAsync()
    {
        await RunShutdownStageAsync("HID NEUTRAL", async () =>
        {
            await ReleaseInputCaptureAsync("Bridge shutdown");
            await _keyboardMouseInput.ClearAndSendNeutralAsync();
        });

        await RunShutdownStageAsync("API STOP", async () =>
        {
            if (_apiAccessLifecycle is not null)
                await _apiAccessLifecycle.DisposeAsync();
            _apiAccessLifecycle = null;
            _apiOnboardingProvider = null;
            _apiLocalCredentials = null;
        });

        await RunShutdownStageAsync("UI WORK STOP", () =>
        {
            _operationCoordinator.CurrentChanged -= OperationCoordinator_CurrentChanged;
            _rumblePollTimer.Stop();
            _serialReconnectTimer.Stop();
            _persistencePollTimer.Stop();
            _networkPakBridgeForm?.Close();
            SetXInputRumble(false);
            _inputCancellation.Cancel();
            _serialCancellation.Cancel();
            _serialSignal.Set();
            StopVideoPreview();
            StopAudioPassthrough();
            CloseFullScreenVideo();
            return Task.CompletedTask;
        });

        await RunShutdownStageAsync("WORKER STOP", async () =>
        {
            Task input = _inputTask ?? Task.CompletedTask;
            Task serial = _serialTask ?? Task.CompletedTask;
            await Task.WhenAll(input, serial);
        });

        await RunShutdownStageAsync("CONNECTION STOP", async () =>
        {
            BridgeDisconnectResult shutdownDisconnect =
                await _connectionService.DisconnectAsync(
                    BridgeDisconnectReason.ApplicationShutdown,
                    CancellationToken.None);
            if (shutdownDisconnect.Failure ==
                BridgeDisconnectFailure.ShutdownContinuedWithPersistenceFailure)
            {
                LogDebug($"PERSISTENCE SHUTDOWN CONTINUING error=" +
                    shutdownDisconnect.Persistence.Error, force: true);
            }
            else if (!shutdownDisconnect.Success && shutdownDisconnect.Failure !=
                BridgeDisconnectFailure.AlreadyDisconnected)
            {
                LogDebug($"SHUTDOWN CONNECTION failure={shutdownDisconnect.Failure}: " +
                    shutdownDisconnect.Error, force: true);
            }
        });

        await RunShutdownStageAsync("LOCAL RESOURCE STOP", () =>
        {
            _networkPakBridge.Dispose();
            SetDisconnectedState();
            _rawInput.Dispose();
            _keyboardMouseInput.Dispose();
            ReleaseJoystick();
            _directInput?.Dispose();
            _rumblePollTimer.Dispose();
            _serialReconnectTimer.Dispose();
            _persistencePollTimer.Dispose();
            return Task.CompletedTask;
        });

        await RunShutdownStageAsync("PERSISTENCE STOP", async () =>
            await _accessoryPersistence.DisposeAsync());
        await RunShutdownStageAsync("CONTROLLER PRIMARY STOP", async () =>
            await _controllerPrimaryService.DisposeAsync());

        await RunShutdownStageAsync("SESSION STOP", () =>
        {
            _sessionService.Dispose();
            _inputCancellation.Dispose();
            _serialCancellation.Dispose();
            return Task.CompletedTask;
        });

        LogDebug("SESSION CLOSED", force: true);
        _debugLogCancellation.Cancel();
        _debugLogSignal.Set();
        if (_debugLogTask is not null)
        {
            try
            {
                await _debugLogTask.WaitAsync(TimeSpan.FromMilliseconds(500));
            }
            catch (TimeoutException)
            {
                // Diagnostics remain bounded and cannot block application close.
            }
            catch (OperationCanceledException)
            {
                // The writer owns the same shutdown cancellation request.
            }
        }

        _debugLogSignal.Dispose();
        _debugLogCancellation.Dispose();
        _n64ControllerArt?.Dispose();
        _snesControllerArt?.Dispose();
    }

    private async Task RunShutdownStageAsync(string stage, Func<Task> action)
    {
        LogDebug($"SHUTDOWN {stage} START", force: true);
        try
        {
            await action();
            LogDebug($"SHUTDOWN {stage} COMPLETE", force: true);
        }
        catch (Exception ex)
        {
            LogDebug($"SHUTDOWN {stage} FAILURE {ex.GetType().Name}: {ex.Message}",
                force: true);
        }
    }

    private void RequestFinalClose()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        BeginInvoke(new Action(Close));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
    }

    private void ReleaseJoystick()
    {
        lock (_joystickLock)
        {
            try
            {
                _joystick?.Unacquire();
                _joystick?.Dispose();
            }
            finally
            {
                _joystick = null;
                _directInputDeviceId = "";
                _directInputDeviceName = "";
                LoadActiveAnalogCalibration();
            }
        }
    }
}
