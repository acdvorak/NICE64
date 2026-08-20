namespace PicoSwitchController;

using DirectShowLib;
using SharpDX.DirectInput;
using SharpDX.XInput;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO.Ports;
using System.Security.Cryptography;

public partial class Form1 : Form
{
    private const int ControllerPakSize = 32768;
    private const int ControllerPakBlockSize = 32;
    private const int ControllerPakBlockCount = ControllerPakSize / ControllerPakBlockSize;
    private const int TransferRomCacheSize = 65536;
    private const int TransferRomFlashMaxSize = 2 * 1024 * 1024;
    private const int TransferRomFlashPageSize = 256;
    private const int TransferSaveSize = 32768;
    private const int EightBitDoParsecAxisThreshold = 8000;

    private sealed record GameBoyCartProfile(
        string Title,
        byte CartType,
        string CartTypeName,
        string Mapper,
        int RomSize,
        int RamSize,
        bool RtcRequired,
        int SaveFileSize)
    {
        public byte FirmwareFlags => (byte)(RtcRequired ? 0x01 : 0x00);
        public string Summary =>
            $"{Title}: cart=0x{CartType:X2} {CartTypeName}, mapper={Mapper}, ROM={RomSize / 1024}KB, SRAM={RamSize / 1024}KB, RTC={(RtcRequired ? "yes" : "no")}, save={SaveFileSize} bytes";
    }

    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "picoswitchcontroller.ini");
    private readonly CancellationTokenSource _inputCancellation = new();
    private readonly object _joystickLock = new();
    private readonly object _maskLock = new();
    private readonly object _serialLock = new();
    private readonly object _debugLogLock = new();
    private readonly AutoResetEvent _serialSignal = new(false);
    private readonly AutoResetEvent _debugLogSignal = new(false);
    private readonly CancellationTokenSource _serialCancellation = new();
    private readonly CancellationTokenSource _debugLogCancellation = new();
    private readonly Queue<string> _serialCommands = new();
    private readonly ConcurrentQueue<string> _debugLogQueue = new();
    private readonly System.Windows.Forms.Timer _rumblePollTimer = new();
    private readonly System.Windows.Forms.Timer _serialReconnectTimer = new();
    private readonly NetworkPakBridgeService _networkPakBridge;

    private readonly BridgeSettings _settings;
    private readonly List<ToolStripMenuItem> _networkPakModuleMenuItems = [];
    private readonly long[] _gamepadReleaseTicks = new long[32];
    private readonly string _debugLogPath = Path.Combine(AppContext.BaseDirectory, "picoswitchcontroller-debug.log");

    private ToolStripMenuItem? _noAccessoryModeMenuItem;
    private ToolStripMenuItem? _memoryPakModeMenuItem;
    private ToolStripMenuItem? _rumblePakModeMenuItem;
    private ToolStripMenuItem? _transferPakModeMenuItem;
    private ToolStripMenuItem? _networkPakModeMenuItem;
    private ToolStripMenuItem? _pcRumblePassthroughMenuItem;
    private ToolStripMenuItem? _inputNoneMenuItem;
    private ToolStripMenuItem? _inputAutoMenuItem;
    private ToolStripMenuItem? _inputXInputMenuItem;
    private ToolStripMenuItem? _inputDirectInputMenuItem;
    private ToolStripMenuItem? _inputEightBitDoParsecMenuItem;
    private ToolStripMenuItem? _debugLoggingMenuItem;
    private ControllerProfile _profile = ControllerProfile.Create(ConsoleControllerType.Snes);
    private ControllerMapping _mapping = ControllerMapping.CreateDefault(ConsoleControllerType.Snes);
    private ControllerMapping _eightBitDoParsecMapping = ControllerMapping.CreateEightBitDoParsecN64();
    private AccessoryMode _accessoryMode = AccessoryMode.None;
    private InputSourceMode _inputSourceMode = InputSourceMode.Auto;
    private DirectInput? _directInput;
    private Joystick? _joystick;
    private Controller? _xinputController;
    private SerialPort? _serialPort;
    private DirectShowPreview? _videoPreview;
    private DirectShowAudioPassthrough? _audioPassthrough;
    private Form? _fullScreenVideoForm;
    private Panel? _fullScreenVideoPanel;
    private Task? _inputTask;
    private Task? _serialTask;
    private Task? _debugLogTask;
    private string? _controllerPakPath;
    private string? _transferRomPath;
    private string? _transferSavePath;
    private int _transferSaveSize = TransferSaveSize;
    private bool _controllerPakOperationInProgress;
    private bool _rumbleActive;
    private bool _rumblePollInProgress;
    private bool _debugLoggingEnabled;
    private bool _n64AutoInputBlockedStatusShown;
    private string _lastDirectInputRawSignature = "";
    private long _lastControllerCommandTicks;
    private long _rumbleOffAfterTicks;
    private uint _gamepadMask;
    private uint _manualMask;
    private uint _buttonMask;
    private Image? _n64ControllerArt;
    private Image? _snesControllerArt;
    private Button? _openDebugLogButton;
    private NetworkPakBridgeForm? _networkPakBridgeForm;
    private string _serialReceiveBuffer = string.Empty;
    private string? _lastConnectedPort;
    private bool _autoReconnectPending;
    private bool _formClosing;
    private int _autoReconnectAttempts;
    private DateTime _nextAutoReconnectUtc;

    private bool IsN64NoAccessoryBaseline => false;

    public Form1()
    {
        _settings = BridgeSettings.Load(_settingsPath);
        _networkPakBridge = new NetworkPakBridgeService(
            _settings.ActiveNetworkPakModuleId,
            _settings.ReadableSourceMode,
            _settings.SearchProviderName,
            _settings.SearchProviderUrl);

        InitializeComponent();
        LoadControllerArt();
        ResetDebugLog();
        AddDebugLogButton();
        InitializeControllerPakMenu();
        InitializeNetworkPakBridgeUi();
        InitializeConsoleSelector();
        videoPictureBox.Resize += (_, _) => ResizeVideoPreview();
        CreateButtonPad();
        LoadMapping();
        RefreshPorts();
        RefreshCaptureDevices();
        TryAcquireFirstGamepad();
        TryAcquireXInputController();
        SetDisconnectedState();

        _rumblePollTimer.Interval = 250;
        _rumblePollTimer.Tick += RumblePollTimer_Tick;

        _serialReconnectTimer.Interval = 1000;
        _serialReconnectTimer.Tick += SerialReconnectTimer_Tick;
        _serialReconnectTimer.Start();

        _debugLogTask = Task.Run(() => DebugLogWriterLoop(_debugLogCancellation.Token));
        _serialTask = Task.Run(() => SerialWriterLoop(_serialCancellation.Token));
        _inputTask = Task.Run(() => PollGamepadLoop(_inputCancellation.Token));
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
                $"# PicoSwitchController debug log started {DateTimeOffset.Now:O}{Environment.NewLine}");
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
        _noAccessoryModeMenuItem.Click += (_, _) => SetAccessoryMode(AccessoryMode.None, sendToPico: true);

        _memoryPakModeMenuItem = new ToolStripMenuItem
        {
            Text = "Memory Pak Mode"
        };
        _memoryPakModeMenuItem.Click += (_, _) => SetAccessoryMode(AccessoryMode.MemoryPak, sendToPico: true);

        _rumblePakModeMenuItem = new ToolStripMenuItem
        {
            Text = "Rumble Pak Mode"
        };
        _rumblePakModeMenuItem.Click += (_, _) => SetAccessoryMode(AccessoryMode.RumblePak, sendToPico: true);

        _transferPakModeMenuItem = new ToolStripMenuItem
        {
            Text = "Transfer Pak Mode"
        };
        _transferPakModeMenuItem.Click += (_, _) => SetAccessoryMode(AccessoryMode.TransferPak, sendToPico: true);

        _networkPakModeMenuItem = new ToolStripMenuItem
        {
            Text = "Network Pak Mode"
        };
        _networkPakModeMenuItem.Click += (_, _) => SetAccessoryMode(AccessoryMode.NetworkPak, sendToPico: true);

        ToolStripMenuItem networkPakModuleMenuItem = new()
        {
            Text = "Network Pak Module"
        };
        foreach (INetworkPakModule module in _networkPakBridge.ListModules())
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
            Text = "Memory Pak File"
        };

        ToolStripMenuItem newControllerPakMenuItem = new()
        {
            Text = "New Memory Pak"
        };
        newControllerPakMenuItem.Click += NewControllerPakMenuItem_Click;

        ToolStripMenuItem loadControllerPakMenuItem = new()
        {
            Text = "Load .mpk..."
        };
        loadControllerPakMenuItem.Click += LoadControllerPakMenuItem_Click;

        ToolStripMenuItem saveControllerPakMenuItem = new()
        {
            Text = "Save .mpk..."
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

        ToolStripMenuItem loadTransferRomMenuItem = new()
        {
            Text = "Load first 64KB of GB/GBC ROM..."
        };
        loadTransferRomMenuItem.Click += LoadTransferRomMenuItem_Click;

        ToolStripMenuItem installTransferRomFlashMenuItem = new()
        {
            Text = "Install GB/GBC ROM to Pico flash..."
        };
        installTransferRomFlashMenuItem.Click += InstallTransferRomFlashMenuItem_Click;

        transferPakMenuItem.DropDownItems.AddRange(
        [
            loadTransferRomMenuItem,
            installTransferRomFlashMenuItem
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
            _networkPakModeMenuItem,
            new ToolStripSeparator(),
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
    }

    private void NetworkPakModuleMenuItem_Click(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem { Tag: string moduleId })
        {
            return;
        }

        bool selected = _networkPakBridge.SetActiveModule(moduleId);
        NetworkPakBridgeSnapshot snapshot = _networkPakBridge.GetSnapshot();
        BridgeSettings.SaveActiveNetworkPakModuleId(_settingsPath, snapshot.ActiveModuleId);
        ApplyNetworkPakModuleUi();
        SetStatus(selected
            ? $"Network Pak module: {snapshot.ActiveModule}."
            : $"Network Pak module fallback: {snapshot.ActiveModule}.");
    }

    private void ApplyNetworkPakModuleUi()
    {
        NetworkPakBridgeSnapshot snapshot = _networkPakBridge.GetSnapshot();
        foreach (ToolStripMenuItem item in _networkPakModuleMenuItems)
        {
            item.Checked = item.Tag is string id &&
                string.Equals(id, snapshot.ActiveModuleId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void InitializeConsoleSelector()
    {
        consoleComboBox.Items.Add(new ConsoleItem(ConsoleControllerType.Snes, "SNES"));
        consoleComboBox.Items.Add(new ConsoleItem(ConsoleControllerType.N64, "N64"));
        consoleComboBox.SelectedIndex = 0;
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
            SetStatus($"XInput controller ready: {userIndex}");
            return;
        }

        _xinputController = null;
    }

    private void ConnectButton_Click(object? sender, EventArgs e)
    {
        if (_serialPort?.IsOpen == true)
        {
            Disconnect();
            return;
        }

        string portName = portComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(portName))
        {
            SetStatus("Choose a COM port first.");
            return;
        }

        _autoReconnectPending = false;
        if (!TryOpenSerialPort(portName, automatic: false, out string? error))
        {
            SetStatus($"Could not connect to {portName}: {error}");
            return;
        }

        lock (_maskLock)
        {
            _manualMask = 0;
            _gamepadMask = 0;
            _buttonMask = 0;
            Array.Clear(_gamepadReleaseTicks);
        }

        SendMask();
        SetConnectedState(portName);
    }

    private bool TryOpenSerialPort(string portName, bool automatic, out string? error)
    {
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

            lock (_serialLock)
            {
                CloseSerialPortLocked();
                _serialPort = serial;
                serial = null;
                _serialReceiveBuffer = string.Empty;
                _lastConnectedPort = portName;
                _autoReconnectPending = false;
                _autoReconnectAttempts = 0;
            }

            LogDebug($"{(automatic ? "AUTO_RECONNECT" : "CONNECT")} port={portName} console={_profile.DisplayName} accessory={_accessoryMode} input={_inputSourceMode}", force: automatic);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try
            {
                serial?.Dispose();
            }
            catch
            {
            }

            return false;
        }
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
        if (!CanUseControllerPakMenu())
        {
            return;
        }

        using SaveFileDialog dialog = new()
        {
            Filter = "N64 Memory Pak (*.mpk)|*.mpk|All files (*.*)|*.*",
            Title = "Create N64 Memory Pak",
            FileName = "nice-n64.mpk"
        };

        string? path = dialog.ShowDialog(this) == DialogResult.OK
            ? dialog.FileName
            : null;

        await RunControllerPakOperationAsync("Creating blank Controller Pak...", () =>
        {
            byte[] image = CreateBlankControllerPakImage();

            lock (_serialLock)
            {
                SerialPort serial = GetOpenSerialPort();
                serial.DiscardInBuffer();
                UploadControllerPakImage(serial, image, "Loading blank Controller Pak");
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                File.WriteAllBytes(path, image);
                _controllerPakPath = path;
            }
        }, string.IsNullOrWhiteSpace(path)
            ? "Blank Controller Pak loaded into Pico RAM. Use Save .mpk to choose a save file."
            : $"Blank Controller Pak created. It will save on disconnect or app close: {Path.GetFileName(path)}.");
    }

    private async void LoadControllerPakMenuItem_Click(object? sender, EventArgs e)
    {
        if (!CanUseControllerPakMenu())
        {
            return;
        }

        using OpenFileDialog dialog = new()
        {
            Filter = "N64 Memory Pak (*.mpk)|*.mpk|All files (*.*)|*.*",
            Title = "Load N64 Memory Pak"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        byte[] image = File.ReadAllBytes(dialog.FileName);
        if (image.Length != ControllerPakSize)
        {
            SetStatus($"Expected a {ControllerPakSize} byte .mpk file, got {image.Length} bytes.");
            return;
        }

        await RunControllerPakOperationAsync($"Loading {Path.GetFileName(dialog.FileName)}...", () =>
        {
            lock (_serialLock)
            {
                SerialPort serial = GetOpenSerialPort();
                serial.DiscardInBuffer();
                UploadControllerPakImage(serial, image, "Loading Controller Pak");
            }

            _controllerPakPath = dialog.FileName;
        }, $"Loaded {Path.GetFileName(dialog.FileName)} into Pico RAM. It will save on disconnect or app close.");
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
        await RunControllerPakOperationAsync($"Saving {Path.GetFileName(path)}...", () =>
        {
            lock (_serialLock)
            {
                SerialPort serial = GetOpenSerialPort();
                serial.DiscardInBuffer();
                SaveControllerPakImage(serial, path, "Saving Controller Pak");
            }

            _controllerPakPath = path;
        }, $"Saved {Path.GetFileName(path)}.");
    }

    private async void LoadTransferRomMenuItem_Click(object? sender, EventArgs e)
    {
        if (!CanUseControllerPakMenu())
        {
            return;
        }

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
        byte[] rom;
        try
        {
            rom = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            SetStatus($"ROM load failed: {ex.Message}");
            return;
        }

        if (rom.Length < TransferRomCacheSize)
        {
            SetStatus("Transfer Pak ROM must be at least 64KB.");
            return;
        }

        await RunControllerPakOperationAsync($"Loading {Path.GetFileName(path)} into Transfer Pak RAM...", () =>
        {
            lock (_serialLock)
            {
                SerialPort serial = GetOpenSerialPort();
                serial.DiscardInBuffer();
                SaveCurrentTransferPakBeforeRomChange(serial);

                GameBoyCartProfile profile = ReadGameBoyCartProfile(rom, path);
                byte[] save = LoadOrCreateTransferSave(path, profile);
                string response = WriteLineAndExpect(serial, "GBCLR", line => line.StartsWith("OK GBCLR", StringComparison.OrdinalIgnoreCase));
                LogDebug($"TRANSFER GBCLR ACK response={response}", force: true);
                SendTransferProfile(serial, profile);
                UploadTransferRomCache(serial, rom, $"Loading {profile.Title}");
                UploadTransferSave(serial, save, $"Loading {profile.Title} save");
                response = WriteLineAndExpect(serial, "PAKMODE TRANSFER", line => line.StartsWith("OK PAKMODE", StringComparison.OrdinalIgnoreCase));
                LogDebug($"TRANSFER PAKMODE ACK response={response}", force: true);

                _transferRomPath = path;
                _transferSavePath = GetTransferSavePath(path);
                _transferSaveSize = save.Length;
                _accessoryMode = AccessoryMode.TransferPak;
                LogDebug($"TRANSFER PROFILE {profile.Summary}", force: true);
            }
        }, $"Transfer Pak RAM loaded: {ReadGameBoyCartProfile(rom, path).Summary}. Save file: {Path.GetFileName(GetTransferSavePath(path))}");

        ApplyAccessoryModeUi();
    }

    private async void InstallTransferRomFlashMenuItem_Click(object? sender, EventArgs e)
    {
        if (!CanUseControllerPakMenu())
        {
            return;
        }

        using OpenFileDialog dialog = new()
        {
            Filter = "Game Boy ROM (*.gb;*.gbc)|*.gb;*.gbc|All files (*.*)|*.*",
            Title = "Install Transfer Pak GB/GBC ROM to Pico flash"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string path = dialog.FileName;
        byte[] rom;
        try
        {
            rom = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            SetStatus($"ROM load failed: {ex.Message}");
            return;
        }

        if (rom.Length < TransferRomCacheSize)
        {
            SetStatus("Transfer Pak ROM must be at least 64KB.");
            return;
        }

        int flashCapacity;
        try
        {
            lock (_serialLock)
            {
                SerialPort serial = GetOpenSerialPort();
                serial.DiscardInBuffer();
                flashCapacity = QueryTransferFlashCapacity(serial);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not query Pico Transfer Pak flash capacity: {ex.Message}");
            return;
        }

        int supportedSize = Math.Min(TransferRomFlashMaxSize, flashCapacity);
        if (rom.Length > supportedSize)
        {
            SetStatus($"This Pico firmware supports Transfer Pak ROMs up to {supportedSize / 1024}KB.");
            return;
        }

        DialogResult confirm = MessageBox.Show(
            this,
            "This writes the selected GB/GBC ROM into the Pico flash slot used by Transfer Pak mode. Keep the N64 powered off while installing.",
            "Install Transfer Pak ROM",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        await RunControllerPakOperationAsync($"Installing {Path.GetFileName(path)} to Pico flash...", () =>
        {
            lock (_serialLock)
            {
                SerialPort serial = GetOpenSerialPort();
                serial.DiscardInBuffer();
                SaveCurrentTransferPakBeforeRomChange(serial);

                GameBoyCartProfile profile = ReadGameBoyCartProfile(rom, path);
                byte[] save = LoadOrCreateTransferSave(path, profile);
                LogDebug($"TRANSFER INSTALL BEGIN rom={path} romBytes={rom.Length} save={GetTransferSavePath(path)} saveBytes={save.Length}", force: true);
                Stopwatch transferStopwatch = Stopwatch.StartNew();
                string response = WriteLineAndExpect(serial, "GBCLR", line => line.StartsWith("OK GBCLR", StringComparison.OrdinalIgnoreCase));
                LogDebug($"TRANSFER GBCLR ACK elapsedMs={transferStopwatch.ElapsedMilliseconds} response={response}", force: true);
                SendTransferProfile(serial, profile);
                LogDebug($"TRANSFER GBFBEGIN bytes={rom.Length}", force: true);
                response = WriteLineAndExpect(serial, $"GBFBEGIN {rom.Length:X8}", line => line.StartsWith("OK GBFBEGIN", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(15), 10000);
                LogDebug($"TRANSFER GBFBEGIN ACK elapsedMs={transferStopwatch.ElapsedMilliseconds} response={response}", force: true);
                UploadTransferRomFlash(serial, rom, $"Installing {profile.Title}");
                long beforeCommitMs = transferStopwatch.ElapsedMilliseconds;
                response = WriteLineAndExpect(serial, "GBFCOMMIT", line => line.StartsWith("OK GBFCOMMIT", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10), 5000);
                LogDebug($"TRANSFER GBFCOMMIT OK delayBeforeMs={transferStopwatch.ElapsedMilliseconds - beforeCommitMs} elapsedMs={transferStopwatch.ElapsedMilliseconds} response={response}", force: true);
                UploadTransferSave(serial, save, $"Loading {profile.Title} save");
                long beforePakModeMs = transferStopwatch.ElapsedMilliseconds;
                response = WriteLineAndExpect(serial, "PAKMODE TRANSFER", line => line.StartsWith("OK PAKMODE", StringComparison.OrdinalIgnoreCase));
                LogDebug($"TRANSFER PAKMODE ACK delayBeforeMs={transferStopwatch.ElapsedMilliseconds - beforePakModeMs} elapsedMs={transferStopwatch.ElapsedMilliseconds} response={response}", force: true);

                _transferRomPath = path;
                _transferSavePath = GetTransferSavePath(path);
                _transferSaveSize = save.Length;
                _accessoryMode = AccessoryMode.TransferPak;
                LogDebug($"TRANSFER PROFILE {profile.Summary}", force: true);
            }
        }, $"Transfer Pak flash installed: {ReadGameBoyCartProfile(rom, path).Summary}. Save file: {Path.GetFileName(GetTransferSavePath(path))}");

        ApplyAccessoryModeUi();
    }

    private void TrySaveControllerPakBeforeDisconnect()
    {
        if (_profile.Type != ConsoleControllerType.N64
            || _accessoryMode != AccessoryMode.MemoryPak
            || _serialPort?.IsOpen != true
            || string.IsNullOrWhiteSpace(_controllerPakPath)
            || _controllerPakOperationInProgress)
        {
            return;
        }

        try
        {
            SetStatus("Saving Controller Pak before disconnect...");
            lock (_serialLock)
            {
                SerialPort serial = GetOpenSerialPort();
                serial.DiscardInBuffer();
                SaveControllerPakImage(serial, _controllerPakPath, "Saving Controller Pak");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Controller Pak save failed: {ex.Message}");
        }
    }

    private void TrySaveTransferPakBeforeDisconnect()
    {
        if (_profile.Type != ConsoleControllerType.N64
            || _accessoryMode != AccessoryMode.TransferPak
            || _serialPort?.IsOpen != true
            || string.IsNullOrWhiteSpace(_transferSavePath)
            || _controllerPakOperationInProgress)
        {
            return;
        }

        try
        {
            lock (_serialLock)
            {
                SerialPort serial = GetOpenSerialPort();
                serial.DiscardInBuffer();
                SaveTransferSave(serial, _transferSavePath, "Saving Transfer Pak save");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Transfer Pak save failed: {ex.Message}");
        }
    }

    private void SaveCurrentTransferPakBeforeRomChange(SerialPort serial)
    {
        if (_profile.Type != ConsoleControllerType.N64
            || string.IsNullOrWhiteSpace(_transferSavePath)
            || _transferSaveSize <= 0)
        {
            return;
        }

        SetStatus($"Saving current Transfer Pak save: {Path.GetFileName(_transferSavePath)}...");
        SaveTransferSave(serial, _transferSavePath, "Saving current Transfer Pak save");
        LogDebug($"TRANSFER SAVE FLUSHED {_transferSavePath}", force: true);
    }

    private void ClearControllerPakMemory(SerialPort serial)
    {
        WriteLineAndExpect(serial, "PAKCLR", line => line.StartsWith("OK PAKCLR", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] CreateBlankControllerPakImage()
    {
        byte[] image = new byte[ControllerPakSize];
        byte[] idBlock = new byte[ControllerPakBlockSize];

        idBlock[1] = (byte)(Random.Shared.Next(256) & 0x3F);
        idBlock[5] = (byte)(Random.Shared.Next(256) & 0x07);
        idBlock[6] = (byte)Random.Shared.Next(256);
        idBlock[7] = (byte)Random.Shared.Next(256);
        idBlock[8] = (byte)(Random.Shared.Next(256) & 0x0F);
        idBlock[9] = (byte)Random.Shared.Next(256);
        idBlock[10] = (byte)Random.Shared.Next(256);
        idBlock[11] = (byte)Random.Shared.Next(256);
        idBlock[25] = 0x01;
        idBlock[26] = 0x01;

        int sumA = 0;
        for (int index = 0; index < 28; index += 2)
        {
            sumA = (sumA + ((idBlock[index] << 8) | idBlock[index + 1])) & 0xFFFF;
        }

        int sumB = (0xFFF2 - sumA) & 0xFFFF;
        idBlock[28] = (byte)(sumA >> 8);
        idBlock[29] = (byte)sumA;
        idBlock[30] = (byte)(sumB >> 8);
        idBlock[31] = (byte)sumB;

        foreach (int offset in new[] { 0x20, 0x60, 0x80, 0xC0 })
        {
            Buffer.BlockCopy(idBlock, 0, image, offset, ControllerPakBlockSize);
        }

        for (int page = 5; page < 128; page++)
        {
            image[0x100 + (page * 2) + 1] = 0x03;
            image[0x200 + (page * 2) + 1] = 0x03;
        }

        image[0x100] = 0x00;
        image[0x101] = CalculateControllerPakIndexChecksum(image, 0x100);
        image[0x200] = 0x00;
        image[0x201] = CalculateControllerPakIndexChecksum(image, 0x200);

        return image;
    }

    private static byte CalculateControllerPakIndexChecksum(byte[] image, int tableOffset)
    {
        int start = tableOffset == 0x100
            ? tableOffset + 10
            : tableOffset + 2;
        int sum = 0;

        for (int offset = start; offset < tableOffset + 0x100; offset++)
        {
            sum = (sum + image[offset]) & 0xFF;
        }

        return (byte)sum;
    }

    private void UploadControllerPakImage(SerialPort serial, byte[] image, string statusPrefix)
    {
        for (int offset = 0; offset < ControllerPakSize; offset += ControllerPakBlockSize)
        {
            string hex = Convert.ToHexString(image, offset, ControllerPakBlockSize);
            WriteLineAndExpect(serial, $"PAK {offset:X4} {hex}", line => line.StartsWith("OK PAK", StringComparison.OrdinalIgnoreCase));
            ReportControllerPakProgress(statusPrefix, offset);
        }
    }

    private void UploadTransferRomCache(SerialPort serial, byte[] rom, string statusPrefix)
    {
        for (int offset = 0; offset < TransferRomCacheSize; offset += ControllerPakBlockSize)
        {
            string hex = Convert.ToHexString(rom, offset, ControllerPakBlockSize);
            WriteLineAndExpect(serial, $"GB {offset:X4} {hex}", line => line.StartsWith("OK GB", StringComparison.OrdinalIgnoreCase));
            ReportTransferRomProgress(statusPrefix, offset);
        }
    }

    private void UploadTransferRomFlash(SerialPort serial, byte[] rom, string statusPrefix)
    {
        byte[] page = new byte[TransferRomFlashPageSize];
        int totalPages = (rom.Length + TransferRomFlashPageSize - 1) / TransferRomFlashPageSize;
        LogDebug($"TRANSFER ROM UPLOAD START bytes={rom.Length} pages={totalPages}", force: true);
        for (int offset = 0; offset < rom.Length; offset += TransferRomFlashPageSize)
        {
            Array.Fill(page, (byte)0xFF);
            int count = Math.Min(TransferRomFlashPageSize, rom.Length - offset);
            Buffer.BlockCopy(rom, offset, page, 0, count);

            string hex = Convert.ToHexString(page);
            string response = WriteLineAndExpect(
                serial,
                $"GBFP {offset:X6} {hex}",
                line => line.StartsWith("OK GBFP", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15),
                10000);
            LogDebug($"TRANSFER ROM ACK page={offset / TransferRomFlashPageSize} offset=0x{offset:X6} length={count} attempted={Math.Min(offset + count, rom.Length)}/{rom.Length} response={response}", force: true);
            ReportTransferRomFlashProgress(statusPrefix, offset, rom.Length);
            if ((offset & 0xFFFF) == 0 || offset + TransferRomFlashPageSize >= rom.Length)
            {
                int done = Math.Min(offset + TransferRomFlashPageSize, rom.Length);
                LogDebug($"TRANSFER ROM UPLOAD {done}/{rom.Length}", force: true);
            }
        }
        LogDebug($"TRANSFER ROM UPLOAD DONE bytes={rom.Length}", force: true);
    }

    private static string GetTransferSavePath(string romPath)
    {
        return Path.ChangeExtension(romPath, ".sav");
    }

    private byte[] LoadOrCreateTransferSave(string romPath, GameBoyCartProfile profile)
    {
        string savePath = GetTransferSavePath(romPath);
        int saveSize = TransferSaveSize;
        if (!File.Exists(savePath))
        {
            byte[] blank = new byte[saveSize];
            Array.Fill(blank, (byte)0xFF);
            File.WriteAllBytes(savePath, blank);
            LogDebug($"TRANSFER SAVE CREATED path={savePath} bytes={blank.Length} sha256={Convert.ToHexString(SHA256.HashData(blank))}", force: true);
            return blank;
        }

        byte[] save = File.ReadAllBytes(savePath);
        if (save.Length == saveSize)
        {
            LogDebug($"TRANSFER SAVE LOADED path={savePath} bytes={save.Length} sha256={Convert.ToHexString(SHA256.HashData(save))}", force: true);
            return save;
        }

        if (save.Length > saveSize)
        {
            byte[] truncated = save[..saveSize];
            LogDebug($"TRANSFER SAVE NORMALIZED path={savePath} fromBytes={save.Length} toBytes={truncated.Length} sha256={Convert.ToHexString(SHA256.HashData(truncated))}", force: true);
            return truncated;
        }

        byte[] padded = new byte[saveSize];
        Array.Fill(padded, (byte)0xFF);
        Buffer.BlockCopy(save, 0, padded, 0, save.Length);
        File.WriteAllBytes(savePath, padded);
        LogDebug($"TRANSFER SAVE NORMALIZED path={savePath} fromBytes={save.Length} toBytes={padded.Length} sha256={Convert.ToHexString(SHA256.HashData(padded))}", force: true);
        return padded;
    }

    private void UploadTransferSave(SerialPort serial, byte[] save, string statusPrefix)
    {
        LogDebug($"TRANSFER SAVE UPLOAD START bytes={save.Length} blocks={save.Length / ControllerPakBlockSize}", force: true);
        for (int offset = 0; offset < save.Length; offset += ControllerPakBlockSize)
        {
            string hex = Convert.ToHexString(save, offset, ControllerPakBlockSize);
            string response = WriteLineAndExpect(serial, $"GBS {offset:X4} {hex}", line => line.StartsWith("OK GBS", StringComparison.OrdinalIgnoreCase));
            LogDebug($"TRANSFER SAVE ACK block={offset / ControllerPakBlockSize} offset=0x{offset:X4} length={ControllerPakBlockSize} attempted={Math.Min(offset + ControllerPakBlockSize, save.Length)}/{save.Length} response={response}", force: true);
            ReportTransferSaveProgress(statusPrefix, offset);
            if ((offset & 0x0FFF) == 0 || offset + ControllerPakBlockSize >= save.Length)
            {
                int done = Math.Min(offset + ControllerPakBlockSize, save.Length);
                LogDebug($"TRANSFER SAVE UPLOAD {done}/{save.Length}", force: true);
            }
        }
        LogDebug($"TRANSFER SAVE UPLOAD DONE bytes={save.Length}", force: true);
    }

    private static void SendTransferProfile(SerialPort serial, GameBoyCartProfile profile)
    {
        serial.WriteLine($"GBP {profile.CartType:X2} {profile.RomSize:X8} {profile.RamSize:X8} {profile.FirmwareFlags:X2}");
        string response = ReadMatchingLine(serial, line => line.StartsWith("OK GBP", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new InvalidOperationException("Pico did not accept Transfer Pak profile.");
        }
    }

    private static int QueryTransferFlashCapacity(SerialPort serial)
    {
        serial.WriteLine("GBFCAP?");
        string response = ReadMatchingLine(serial, line => line.StartsWith("GBFCAP ", StringComparison.OrdinalIgnoreCase));
        string hex = response[7..].Trim();
        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int capacity))
        {
            throw new InvalidOperationException($"Unexpected GBFCAP response: {response}");
        }

        return capacity;
    }

    private void ReportTransferRomProgress(string statusPrefix, int offset)
    {
        if ((offset & 0x03FF) == 0)
        {
            SetStatus($"{statusPrefix}... {offset / ControllerPakBlockSize + 1}/{TransferRomCacheSize / ControllerPakBlockSize} blocks");
        }
    }

    private void ReportTransferRomFlashProgress(string statusPrefix, int offset, int totalBytes)
    {
        if ((offset & 0x3FFF) == 0)
        {
            int done = Math.Min(offset + TransferRomFlashPageSize, totalBytes);
            int doneKb = Math.Max(1, (done + 1023) / 1024);
            int totalKb = Math.Max(1, (totalBytes + 1023) / 1024);
            SetStatus($"{statusPrefix}... {doneKb}/{totalKb}KB");
        }
    }

    private void ReportTransferSaveProgress(string statusPrefix, int offset)
    {
        if ((offset & 0x07FF) == 0)
        {
            int totalBlocks = Math.Max(1, TransferSaveSize / ControllerPakBlockSize);
            SetStatus($"{statusPrefix}... {offset / ControllerPakBlockSize + 1}/{totalBlocks} blocks");
        }
    }

    private static GameBoyCartProfile ReadGameBoyCartProfile(byte[] rom, string romPath)
    {
        string title = GetGameBoyTitle(rom);
        byte cartType = rom.Length > 0x0147 ? rom[0x0147] : (byte)0x00;
        byte romSizeCode = rom.Length > 0x0148 ? rom[0x0148] : (byte)0x00;
        byte ramSizeCode = rom.Length > 0x0149 ? rom[0x0149] : (byte)0x00;
        int romSize = DecodeGameBoyRomSize(romSizeCode, rom.Length);
        int ramSize = DecodeGameBoyRamSize(ramSizeCode);
        string mapper = GetGameBoyMapperName(cartType);
        string cartTypeName = GetGameBoyCartTypeName(cartType);
        bool rtc = cartType is 0x0F or 0x10;
        string savePath = GetTransferSavePath(romPath);
        int saveFileSize = File.Exists(savePath) ? checked((int)new FileInfo(savePath).Length) : 0;
        return new GameBoyCartProfile(title, cartType, cartTypeName, mapper, romSize, ramSize, rtc, saveFileSize);
    }

    private static int DecodeGameBoyRomSize(byte code, int actualLength) => code switch
    {
        0x00 => 32 * 1024,
        0x01 => 64 * 1024,
        0x02 => 128 * 1024,
        0x03 => 256 * 1024,
        0x04 => 512 * 1024,
        0x05 => 1024 * 1024,
        0x06 => 2 * 1024 * 1024,
        0x07 => 4 * 1024 * 1024,
        0x08 => 8 * 1024 * 1024,
        0x52 => 1152 * 1024,
        0x53 => 1280 * 1024,
        0x54 => 1536 * 1024,
        _ => actualLength
    };

    private static int DecodeGameBoyRamSize(byte code) => code switch
    {
        0x01 => 2 * 1024,
        0x02 => 8 * 1024,
        0x03 => 32 * 1024,
        0x04 => 128 * 1024,
        0x05 => 64 * 1024,
        _ => 0
    };

    private static string GetGameBoyMapperName(byte cartType) => cartType switch
    {
        >= 0x01 and <= 0x03 => "MBC1",
        >= 0x0F and <= 0x13 => "MBC3",
        >= 0x19 and <= 0x1E => "MBC5",
        _ => "ROM"
    };

    private static string GetGameBoyCartTypeName(byte cartType) => cartType switch
    {
        0x00 => "ROM ONLY",
        0x01 => "MBC1",
        0x02 => "MBC1+RAM",
        0x03 => "MBC1+RAM+BATTERY",
        0x0F => "MBC3+TIMER+BATTERY",
        0x10 => "MBC3+TIMER+RAM+BATTERY",
        0x11 => "MBC3",
        0x12 => "MBC3+RAM",
        0x13 => "MBC3+RAM+BATTERY",
        0x19 => "MBC5",
        0x1A => "MBC5+RAM",
        0x1B => "MBC5+RAM+BATTERY",
        0x1C => "MBC5+RUMBLE",
        0x1D => "MBC5+RUMBLE+RAM",
        0x1E => "MBC5+RUMBLE+RAM+BATTERY",
        _ => "Unknown"
    };

    private static string GetGameBoyTitle(byte[] rom)
    {
        int length = Math.Min(16, rom.Length - 0x0134);
        if (length <= 0)
        {
            return "GB ROM";
        }

        int end = 0;
        while (end < length && rom[0x0134 + end] != 0)
        {
            end++;
        }

        string title = System.Text.Encoding.ASCII.GetString(rom, 0x0134, end).Trim();
        return string.IsNullOrWhiteSpace(title) ? "GB ROM" : title;
    }

    private void SaveControllerPakImage(SerialPort serial, string path, string statusPrefix)
    {
        byte[] image = DownloadControllerPakImage(serial, statusPrefix);
        File.WriteAllBytes(path, image);
    }

    private void SaveTransferSave(SerialPort serial, string path, string statusPrefix)
    {
        byte[] save = DownloadTransferSave(serial, statusPrefix);
        File.WriteAllBytes(path, save);
    }

    private byte[] DownloadTransferSave(SerialPort serial, string statusPrefix)
    {
        int saveSize = TransferSaveSize;
        byte[] save = new byte[saveSize];

        for (int offset = 0; offset < saveSize; offset += ControllerPakBlockSize)
        {
            serial.WriteLine($"GBSDUMP {offset:X4}");
            string response = ReadMatchingLine(serial, line => line.StartsWith($"GBS {offset:X4} ", StringComparison.OrdinalIgnoreCase));
            string hex = response[9..].Trim();
            byte[] block = Convert.FromHexString(hex);
            if (block.Length != ControllerPakBlockSize)
            {
                throw new InvalidOperationException($"Pico returned {block.Length} bytes for save block 0x{offset:X4}.");
            }

            Buffer.BlockCopy(block, 0, save, offset, ControllerPakBlockSize);
            ReportTransferSaveProgress(statusPrefix, offset);
        }

        return save;
    }

    private byte[] DownloadControllerPakImage(SerialPort serial, string statusPrefix)
    {
        byte[] image = new byte[ControllerPakSize];

        for (int offset = 0; offset < ControllerPakSize; offset += ControllerPakBlockSize)
        {
            serial.WriteLine($"PAKDUMP {offset:X4}");
            string response = ReadMatchingLine(serial, line => line.StartsWith($"PAK {offset:X4} ", StringComparison.OrdinalIgnoreCase));
            string hex = response[9..].Trim();
            byte[] block = Convert.FromHexString(hex);
            if (block.Length != ControllerPakBlockSize)
            {
                throw new InvalidOperationException($"Pico returned {block.Length} bytes for block 0x{offset:X4}.");
            }

            Buffer.BlockCopy(block, 0, image, offset, ControllerPakBlockSize);
            ReportControllerPakProgress(statusPrefix, offset);
        }

        return image;
    }

    private void ReportControllerPakProgress(string statusPrefix, int offset)
    {
        if ((offset & 0x07FF) == 0)
        {
            SetStatus($"{statusPrefix}... {offset / ControllerPakBlockSize + 1}/{ControllerPakBlockCount} blocks");
        }
    }

    private bool CanUseControllerPakMenu()
    {
        if (_profile.Type != ConsoleControllerType.N64)
        {
            SetStatus("Controller Pak tools require the N64 profile.");
            return false;
        }

        if (_serialPort?.IsOpen != true)
        {
            SetStatus("Connect to the Pico first.");
            return false;
        }

        return true;
    }

    private async Task RunControllerPakOperationAsync(string startStatus, Action action, string completeStatus)
    {
        if (_controllerPakOperationInProgress)
        {
            SetStatus("Controller Pak operation already in progress.");
            return;
        }

        LogDebug($"CONTROLLER PAK OP START {startStatus}", force: true);
        SetStatus(startStatus);
        _controllerPakOperationInProgress = true;
        _serialReceiveBuffer = string.Empty;
        ClearQueuedControllerStateCommands();

        try
        {
            await Task.Run(action);
            SetStatus(completeStatus);
        }
        catch (Exception ex)
        {
            SetStatus($"Controller Pak operation failed: {ex.Message}");
        }
        finally
        {
            _serialReceiveBuffer = string.Empty;
            ClearQueuedControllerStateCommands();
            LogDebug($"CONTROLLER PAK OP END activeMode={_accessoryMode}", force: true);
            _controllerPakOperationInProgress = false;
        }
    }

    private SerialPort GetOpenSerialPort()
    {
        if (_serialPort?.IsOpen != true)
        {
            throw new InvalidOperationException("Pico serial port is not connected.");
        }

        return _serialPort;
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

    private async void SetAccessoryMode(AccessoryMode mode, bool sendToPico)
    {
        AccessoryMode previousMode = _accessoryMode;
        LogDebug($"ACCESSORY MODE TRANSITION requested={mode} previous={previousMode} sendToPico={sendToPico} operationInProgress={_controllerPakOperationInProgress}", force: true);
        _accessoryMode = mode;
        ApplyAccessoryModeUi();

        if (mode != AccessoryMode.RumblePak)
        {
            SetXInputRumble(false);
        }

        if (!sendToPico || _profile.Type != ConsoleControllerType.N64 || _serialPort?.IsOpen != true)
        {
            return;
        }

        string command = mode switch
        {
            AccessoryMode.RumblePak => "PAKMODE RUMBLE",
            AccessoryMode.TransferPak => "PAKMODE TRANSFER",
            AccessoryMode.NetworkPak => "PAKMODE NET",
            AccessoryMode.None => "PAKMODE NONE",
            _ => "PAKMODE MEM"
        };

        if (mode == AccessoryMode.TransferPak && string.IsNullOrWhiteSpace(_transferRomPath))
        {
            SetStatus("Transfer Pak mode active with no GB/GBC ROM loaded.");
        }

        string label = mode switch
        {
            AccessoryMode.RumblePak => "Rumble Pak",
            AccessoryMode.TransferPak => "Transfer Pak",
            AccessoryMode.NetworkPak => "Network Pak",
            AccessoryMode.None => "No Accessory",
            _ => "Memory Pak"
        };

        await RunControllerPakOperationAsync($"Switching to {label}...", () =>
        {
            lock (_serialLock)
            {
                SerialPort serial = GetOpenSerialPort();
                serial.DiscardInBuffer();
                string response = WriteLineAndExpect(serial, command, line => line.StartsWith("OK PAKMODE", StringComparison.OrdinalIgnoreCase));
                LogDebug($"PAKMODE ACK command={command} response={response}", force: true);
            }
        }, $"{label} mode active.");

        if (mode == AccessoryMode.NetworkPak && _serialPort?.IsOpen == true)
        {
            SendNetworkPakBridgeHello();
        }
        else
        {
            SendNetworkPakBridgeBye();
        }
    }

    private void ApplyAccessoryModeUi()
    {
        if (_noAccessoryModeMenuItem is not null)
        {
            _noAccessoryModeMenuItem.Checked = _accessoryMode == AccessoryMode.None;
        }

        if (_memoryPakModeMenuItem is not null)
        {
            _memoryPakModeMenuItem.Checked = _accessoryMode == AccessoryMode.MemoryPak;
        }

        if (_rumblePakModeMenuItem is not null)
        {
            _rumblePakModeMenuItem.Checked = _accessoryMode == AccessoryMode.RumblePak;
        }

        if (_transferPakModeMenuItem is not null)
        {
            _transferPakModeMenuItem.Checked = _accessoryMode == AccessoryMode.TransferPak;
        }

        if (_networkPakModeMenuItem is not null)
        {
            _networkPakModeMenuItem.Checked = _accessoryMode == AccessoryMode.NetworkPak;
        }

        if (_accessoryMode == AccessoryMode.RumblePak && _pcRumblePassthroughMenuItem?.Checked == true)
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
            Array.Clear(_gamepadReleaseTicks);
        }

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

    private async void RumblePollTimer_Tick(object? sender, EventArgs e)
    {
        if (_profile.Type != ConsoleControllerType.N64
            || _accessoryMode != AccessoryMode.RumblePak
            || _serialPort?.IsOpen != true
            || _controllerPakOperationInProgress)
        {
            SetXInputRumble(false);
            return;
        }

        lock (_serialCommands)
        {
            if (_serialCommands.Count > 0)
            {
                return;
            }
        }

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

    private void ConsoleComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (consoleComboBox.SelectedItem is not ConsoleItem item || item.Type == _profile.Type)
        {
            return;
        }

        lock (_maskLock)
        {
            _manualMask = 0;
            _gamepadMask = 0;
            _buttonMask = 0;
            Array.Clear(_gamepadReleaseTicks);
        }

        _profile = ControllerProfile.Create(item.Type);
        LoadMapping();
        CreateButtonPad();
        if (_profile.Type == ConsoleControllerType.N64 && _inputSourceMode == InputSourceMode.Auto)
        {
            SetInputSourceMode(InputSourceMode.None);
        }
        else
        {
            _n64AutoInputBlockedStatusShown = false;
        }

        SetDisconnectedState();
        Text = $"Pico {_profile.DisplayName} Controller Bridge";
        titleLabel.Text = $"Pico {_profile.DisplayName} Controller";

        if (_serialPort?.IsOpen == true)
        {
            SendMask();
        }

        SetStatus($"{_profile.DisplayName} layout selected.");
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
            _buttonMask = 0;
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
        uint nextMask;
        lock (_maskLock)
        {
            nextMask = _manualMask | _gamepadMask;
            if (nextMask == _buttonMask)
            {
                return;
            }

            _buttonMask = nextMask;
        }

        if (IsN64NoAccessoryBaseline)
        {
            SendMaskBaseline();
            return;
        }

        LogDebug($"COMBINED mask=0x{nextMask:X8} buttons={FormatPressedButtons(nextMask)}");
        SendMask();
    }

    private void SendMaskBaseline()
    {
        uint mask;
        lock (_maskLock)
        {
            mask = _buttonMask;
        }

        SendCommandBaseline(ControllerProfile.BuildCommand(_profile.Type, mask));
    }

    private void SendMask()
    {
        uint mask;
        lock (_maskLock)
        {
            mask = _buttonMask;
        }

        SendCommand(ControllerProfile.BuildCommand(_profile.Type, mask));
    }

    private void SendCommandBaseline(string command)
    {
        lock (_serialCommands)
        {
            _serialCommands.Enqueue(command);
        }

        _serialSignal.Set();
    }

    private void SendCommand(string command)
    {
        if (IsControllerStateCommand(command))
        {
            _lastControllerCommandTicks = Environment.TickCount64;
            if (_controllerPakOperationInProgress)
            {
                LogDebug($"SKIP QUEUE DURING CONTROLLER PAK OP {command}", force: true);
                return;
            }
        }

        LogDebug($"QUEUE {command}");
        lock (_serialCommands)
        {
            _serialCommands.Enqueue(command);
        }

        _serialSignal.Set();
    }

    private void ClearQueuedControllerStateCommands()
    {
        int dropped = 0;
        lock (_serialCommands)
        {
            if (_serialCommands.Count == 0)
            {
                LogDebug("CLEAR QUEUED STATE COMMANDS dropped=0", force: true);
                return;
            }

            Queue<string> retained = new();
            while (_serialCommands.Count > 0)
            {
                string queued = _serialCommands.Dequeue();
                if (!IsControllerStateCommand(queued))
                {
                    retained.Enqueue(queued);
                    continue;
                }

                dropped++;
            }

            while (retained.Count > 0)
            {
                _serialCommands.Enqueue(retained.Dequeue());
            }
        }

        LogDebug($"CLEAR QUEUED STATE COMMANDS dropped={dropped}", force: true);
    }

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
    {
        uint mask;
        lock (_maskLock)
        {
            mask = _buttonMask;
        }

        return ControllerProfile.BuildCommand(_profile.Type, mask);
    }

    private Task SerialWriterLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _serialSignal.WaitOne(_settings.SerialWriterWaitMs);

            string? command = null;
            lock (_serialCommands)
            {
                string? latestStateCommand = null;
                while (_serialCommands.Count > 0)
                {
                    string nextCommand = _serialCommands.Dequeue();
                    if (IsControllerStateCommand(nextCommand))
                    {
                        latestStateCommand = nextCommand;
                        continue;
                    }

                    command = nextCommand;
                    break;
                }

                if (command is null)
                {
                    command = latestStateCommand;
                }
                else if (latestStateCommand is not null)
                {
                    _serialCommands.Enqueue(latestStateCommand);
                }
            }

            bool isStateCommand = command is not null && IsControllerStateCommand(command);
            if (isStateCommand && _controllerPakOperationInProgress)
            {
                LogDebug($"SKIP DEQUEUED STATE DURING CONTROLLER PAK OP {command}", force: true);
                continue;
            }

            lock (_serialLock)
            {
                if (_serialPort?.IsOpen != true)
                {
                    if (_serialPort is not null)
                    {
                        ScheduleAutoReconnectLocked(new IOException("The serial port closed unexpectedly."));
                    }

                    if (command is not null && !isStateCommand)
                    {
                        SetStatus("Connect to the Pico first.");
                    }

                    continue;
                }

                try
                {
                    if (command is not null)
                    {
                        if (isStateCommand && _controllerPakOperationInProgress)
                        {
                            LogDebug($"SKIP WRITE STATE DURING CONTROLLER PAK OP {command}", force: true);
                            continue;
                        }

                        _serialPort.WriteLine(command);
                        if (!IsN64NoAccessoryBaseline)
                        {
                            LogDebug($"SEND {command}");
                        }

                        if (!isStateCommand)
                        {
                            SetStatus($"Sent {command}");
                        }
                    }
                    if (_accessoryMode == AccessoryMode.NetworkPak && !_controllerPakOperationInProgress)
                    {
                        PollNetworkPakBridgeLinesLocked(_serialPort);
                    }
                }
                catch (Exception ex)
                {
                    ScheduleAutoReconnectLocked(ex);
                }
            }
        }

        return Task.CompletedTask;
    }

    private void ScheduleAutoReconnectLocked(Exception ex)
    {
        string? portName = _serialPort?.PortName ?? _lastConnectedPort;
        CloseSerialPortLocked();
        _serialReceiveBuffer = string.Empty;
        _networkPakBridge.SetPicoConnected(false);

        if (_formClosing || string.IsNullOrWhiteSpace(portName))
        {
            return;
        }

        _lastConnectedPort = portName;
        _autoReconnectPending = true;
        _autoReconnectAttempts = 0;
        _nextAutoReconnectUtc = DateTime.UtcNow.AddSeconds(1);
        LogDebug($"SERIAL_FAULT port={portName} error={ex.Message}; auto reconnect scheduled", force: true);

        RunOnUiThread(() =>
        {
            RefreshPorts(portName);
            SetDisconnectedState();
            SetStatus($"Pico connection lost on {portName}: {ex.Message} Auto reconnecting...");
        });
    }

    private void SerialReconnectTimer_Tick(object? sender, EventArgs e)
    {
        string? portName;
        lock (_serialLock)
        {
            if (_formClosing
                || !_autoReconnectPending
                || _controllerPakOperationInProgress
                || _serialPort?.IsOpen == true
                || DateTime.UtcNow < _nextAutoReconnectUtc)
            {
                return;
            }

            portName = _lastConnectedPort;
        }

        if (string.IsNullOrWhiteSpace(portName))
        {
            return;
        }

        bool portPresent;
        try
        {
            RefreshPorts(portName);
            portPresent = SerialPort.GetPortNames()
                .Contains(portName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            lock (_serialLock)
            {
                _nextAutoReconnectUtc = DateTime.UtcNow.AddSeconds(1);
            }

            SetStatus($"Waiting for serial devices: {ex.Message}");
            return;
        }

        if (!portPresent)
        {
            lock (_serialLock)
            {
                _nextAutoReconnectUtc = DateTime.UtcNow.AddSeconds(1);
            }

            SetStatus($"Waiting for {portName} to return...");
            return;
        }

        if (!TryOpenSerialPort(portName, automatic: true, out string? error))
        {
            lock (_serialLock)
            {
                _autoReconnectAttempts++;
                int delaySeconds = Math.Min(5, Math.Max(1, _autoReconnectAttempts));
                _nextAutoReconnectUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
            }

            SetStatus($"Auto reconnect to {portName} failed: {error}");
            return;
        }

        SendMask();
        SetConnectedState(portName);
        SetStatus($"Reconnected to {portName}; restoring {_accessoryMode} mode.");
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

    private void SendNetworkPakBridgeHello()
    {
        lock (_serialLock)
        {
            if (_serialPort?.IsOpen != true)
            {
                return;
            }

            _networkPakBridge.SetPicoConnected(true);
            _serialReceiveBuffer = string.Empty;
            _serialPort.WriteLine("NETPAK_HELLO|1");
            LogDebug("NETPAK SEND NETPAK_HELLO|1", force: true);
        }
    }

    private void PollNetworkPakBridgeLinesLocked(SerialPort serial)
    {
        if (_accessoryMode != AccessoryMode.NetworkPak || _controllerPakOperationInProgress)
        {
            return;
        }

        string available = serial.ReadExisting();
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

            if (line == "NETPAK_HELLO_ACK|1")
            {
                _networkPakBridge.SetHandshakeComplete(true);
                LogDebug("NETPAK handshake ready", force: true);
                continue;
            }

            if (_networkPakBridge.TryHandleRequest(line, out string responseLine))
            {
                if (responseLine.Length > 0)
                {
                    serial.WriteLine(responseLine);
                    _networkPakBridge.MarkNetworkPakResponseWritten(responseLine);
                    LogDebug($"NETPAK {line} -> {responseLine}", force: true);
                }
                else
                {
                    LogDebug($"NETPAK malformed request: {line}", force: true);
                }
            }
        }
    }

    private void SendNetworkPakBridgeBye()
    {
        lock (_serialLock)
        {
            if (_serialPort?.IsOpen == true)
            {
                LogDebug("NETPAK SEND NETPAK_BYE", force: true);
                _serialPort.WriteLine("NETPAK_BYE");
            }
            _networkPakBridge.SetPicoConnected(false);
        }
    }

    private static bool IsControllerStateCommand(string command)
    {
        return command.StartsWith("MASK ", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("N64 ", StringComparison.OrdinalIgnoreCase);
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
        bool readAnyInput = TryReadGamepadMask(out nextGamepadMask, out string sourceLabel);
        if (!readAnyInput)
        {
            return;
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
            LogMaskTransitions($"GAMEPAD/{sourceLabel}", previousGamepadMask, nextGamepadMask);
            SendCombinedMask();
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

    private bool TryReadGamepadMask(out uint mask, out string sourceLabel)
    {
        mask = 0;
        sourceLabel = GetInputSourceLabel(_inputSourceMode);

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
                }

                SendCombinedMask();
                SetStatus("USB controller disconnected.");
                return null;
            }
        }
    }

    private void Disconnect(bool sendNeutralState = true)
    {
        lock (_serialLock)
        {
            _autoReconnectPending = false;
            _autoReconnectAttempts = 0;
        }

        try
        {
            LogDebug($"DISCONNECT sendNeutralState={sendNeutralState}");
            TrySaveControllerPakBeforeDisconnect();
            TrySaveTransferPakBeforeDisconnect();
            SetXInputRumble(false);

            lock (_maskLock)
            {
                _buttonMask = 0;
                _manualMask = 0;
                _gamepadMask = 0;
            }

            lock (_serialLock)
            {
                if (_serialPort?.IsOpen == true)
                {
                    try
                    {
                        _serialPort.WriteLine("NETPAK_BYE");
                    }
                    catch
                    {
                    }

                    if (sendNeutralState)
                    {
                        try
                        {
                            string neutralCommand = ControllerProfile.BuildCommand(_profile.Type, 0);
                            _serialPort.WriteLine(neutralCommand);
                            LogDebug($"SEND NEUTRAL {neutralCommand}");
                        }
                        catch
                        {
                            // The Pico may already be wedged or unplugged; disconnect must still complete.
                        }
                    }
                }

                CloseSerialPortLocked();
            }
        }
        finally
        {
            _serialReceiveBuffer = string.Empty;
            _networkPakBridge.SetPicoConnected(false);
            SetDisconnectedState();
        }
    }

    private void CloseSerialPortLocked()
    {
        SerialPort? serial = _serialPort;
        _serialPort = null;

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

    private void SetConnectedState(string portName)
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

        SetStatus($"Connected to {portName}");

        if (_profile.Type == ConsoleControllerType.N64)
        {
            LogDebug($"CONNECTED restoreAccessoryMode={_accessoryMode}", force: true);
            SetAccessoryMode(_accessoryMode, sendToPico: true);
        }
    }

    private void SetDisconnectedState()
    {
        connectButton.Text = "Connect";
        consoleComboBox.Enabled = true;
        portComboBox.Enabled = true;
        refreshButton.Enabled = true;
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

        SetStatus("Disconnected");
    }

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

    private enum AccessoryMode
    {
        None,
        MemoryPak,
        RumblePak,
        TransferPak,
        NetworkPak
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _formClosing = true;
        _rumblePollTimer.Stop();
        _serialReconnectTimer.Stop();
        _networkPakBridgeForm?.Close();
        SetXInputRumble(false);
        LogDebug("SESSION CLOSED", force: true);
        _inputCancellation.Cancel();
        _serialCancellation.Cancel();
        _debugLogCancellation.Cancel();
        _debugLogSignal.Set();
        StopVideoPreview();
        StopAudioPassthrough();
        CloseFullScreenVideo();
        Disconnect();
        ReleaseJoystick();
        _directInput?.Dispose();
        _rumblePollTimer.Dispose();
        _serialReconnectTimer.Dispose();
        _inputCancellation.Dispose();
        _serialCancellation.Dispose();
        try
        {
            _debugLogTask?.Wait(500);
        }
        catch
        {
            // Closing should not be blocked by diagnostics.
        }

        _debugLogSignal.Dispose();
        _debugLogCancellation.Dispose();
        _n64ControllerArt?.Dispose();
        _snesControllerArt?.Dispose();
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
            }
        }
    }
}
