namespace PicoSwitchController;

using SharpDX.DirectInput;

public sealed class ControllerMappingForm : Form
{
    private const int AxisCenter = 32767;
    private const int AxisThreshold = 16000;

    private readonly IReadOnlyCollection<string> _buttonNames;
    private readonly string _mappingPath;
    private readonly Dictionary<string, Label> _mappingLabels = new();
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 16 };

    private DirectInput? _directInput;
    private Joystick? _joystick;
    private JoystickState? _learnBaseline;
    private string? _learningButton;
    private ComboBox gamepadComboBox = null!;
    private Label statusLabel = null!;

    public ControllerMapping Mapping { get; private set; }

    public ControllerMappingForm(
        IReadOnlyCollection<string> buttonNames,
        ControllerMapping mapping,
        string mappingPath)
    {
        _buttonNames = buttonNames;
        Mapping = mapping;
        _mappingPath = mappingPath;

        InitializeComponent();
        RefreshGamepads();
        UpdateMappingLabels();

        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();
    }

    private void InitializeComponent()
    {
        Text = "USB Controller Mapping";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(520, 520);
        Size = new Size(560, 620);

        gamepadComboBox = new ComboBox
        {
            Location = new Point(16, 18),
            Size = new Size(288, 23),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        Button refreshButton = new()
        {
            Text = "Refresh",
            Location = new Point(314, 17),
            Size = new Size(82, 25),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        refreshButton.Click += RefreshButton_Click;

        Button useButton = new()
        {
            Text = "Use",
            Location = new Point(406, 17),
            Size = new Size(82, 25),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        useButton.Click += UseButton_Click;

        TableLayoutPanel mappingTable = new()
        {
            Location = new Point(16, 58),
            Size = new Size(496, 420),
            ColumnCount = 3,
            RowCount = _buttonNames.Count,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            AutoScroll = true
        };
        mappingTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        mappingTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mappingTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));

        int row = 0;
        foreach (string buttonName in _buttonNames)
        {
            mappingTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

            Label nameLabel = new()
            {
                Text = buttonName,
                Anchor = AnchorStyles.Left,
                AutoSize = true
            };

            Label mappingLabel = new()
            {
                Text = "Unmapped",
                Anchor = AnchorStyles.Left,
                AutoSize = true
            };
            _mappingLabels[buttonName] = mappingLabel;

            Button learnButton = new()
            {
                Text = "Learn",
                Tag = buttonName,
                Size = new Size(70, 24),
                Anchor = AnchorStyles.Left
            };
            learnButton.Click += LearnButton_Click;

            mappingTable.Controls.Add(nameLabel, 0, row);
            mappingTable.Controls.Add(mappingLabel, 1, row);
            mappingTable.Controls.Add(learnButton, 2, row);
            row++;
        }

        statusLabel = new Label
        {
            Text = "Choose a controller, click Use, then Learn.",
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(16, 496),
            Size = new Size(496, 44),
            TextAlign = ContentAlignment.MiddleLeft,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        Button closeButton = new()
        {
            Text = "Close",
            Location = new Point(430, 548),
            Size = new Size(82, 26),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };
        closeButton.Click += (_, _) => Close();

        Controls.Add(gamepadComboBox);
        Controls.Add(refreshButton);
        Controls.Add(useButton);
        Controls.Add(mappingTable);
        Controls.Add(statusLabel);
        Controls.Add(closeButton);
    }

    private void RefreshGamepads()
    {
        _directInput?.Dispose();
        _directInput = new DirectInput();
        gamepadComboBox.Items.Clear();

        IEnumerable<DeviceInstance> devices = _directInput
            .GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
            .OrderBy(device => device.InstanceName);

        foreach (DeviceInstance device in devices)
        {
            gamepadComboBox.Items.Add(new GamepadDeviceItem(device.InstanceGuid, device.InstanceName));
        }

        if (gamepadComboBox.Items.Count > 0)
        {
            gamepadComboBox.SelectedIndex = 0;
        }
    }

    private void RefreshButton_Click(object? sender, EventArgs e)
    {
        ReleaseJoystick();
        RefreshGamepads();
        SetStatus("USB controller list refreshed.");
    }

    private void UseButton_Click(object? sender, EventArgs e)
    {
        if (gamepadComboBox.SelectedItem is not GamepadDeviceItem item || _directInput is null)
        {
            SetStatus("Choose a USB controller first.");
            return;
        }

        try
        {
            ReleaseJoystick();
            _joystick = new Joystick(_directInput, item.InstanceGuid);
            _joystick.Properties.BufferSize = 128;
            _joystick.Acquire();
            SetStatus($"Using controller: {item.Name}");
        }
        catch (Exception ex)
        {
            ReleaseJoystick();
            SetStatus($"Controller failed: {ex.Message}");
        }
    }

    private void LearnButton_Click(object? sender, EventArgs e)
    {
        if (_joystick is null)
        {
            SetStatus("Choose and use a USB controller first.");
            return;
        }

        if (sender is not Button { Tag: string buttonName })
        {
            return;
        }

        _learningButton = buttonName;
        _learnBaseline = ReadJoystick();
        SetStatus($"Press input for {buttonName}.");
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_learningButton is null)
        {
            return;
        }

        JoystickState? state = ReadJoystick();
        if (state is null)
        {
            return;
        }

        InputBinding? binding = DetectNewBinding(state, _learnBaseline);
        if (binding is null)
        {
            return;
        }

        Mapping.Bindings[_learningButton] = binding;
        Mapping.Save(_mappingPath);
        SetStatus($"Mapped {_learningButton} to {binding}.");
        _learningButton = null;
        _learnBaseline = null;
        UpdateMappingLabels();
    }

    private JoystickState? ReadJoystick()
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
            SetStatus("USB controller disconnected.");
            return null;
        }
    }

    private void UpdateMappingLabels()
    {
        foreach (string buttonName in _buttonNames)
        {
            _mappingLabels[buttonName].Text = Mapping.Bindings.TryGetValue(buttonName, out InputBinding? binding)
                ? binding.ToString()
                : "Unmapped";
        }
    }

    private void SetStatus(string message)
    {
        statusLabel.Text = message;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pollTimer.Stop();
        ReleaseJoystick();
        _directInput?.Dispose();
        base.OnFormClosed(e);
    }

    private void ReleaseJoystick()
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

    private static InputBinding? DetectNewBinding(JoystickState state, JoystickState? baseline)
    {
        bool[] buttons = state.Buttons;
        bool[] baselineButtons = baseline?.Buttons ?? Array.Empty<bool>();
        for (int i = 0; i < buttons.Length; i++)
        {
            bool wasPressed = i < baselineButtons.Length && baselineButtons[i];
            if (buttons[i] && !wasPressed)
            {
                return InputBinding.Button(i);
            }
        }

        InputBinding? pov = DetectPov(state.PointOfViewControllers.FirstOrDefault(-1));
        if (pov is not null)
        {
            return pov;
        }

        return DetectAxis(state, baseline);
    }

    private static InputBinding? DetectPov(int value)
    {
        if (value < 0)
        {
            return null;
        }

        return value switch
        {
            >= 31500 or <= 4500 => InputBinding.Pov("Up"),
            > 4500 and <= 13500 => InputBinding.Pov("Right"),
            > 13500 and <= 22500 => InputBinding.Pov("Down"),
            _ => InputBinding.Pov("Left")
        };
    }

    private static InputBinding? DetectAxis(JoystickState state, JoystickState? baseline)
    {
        foreach ((string name, int value) in GamepadInput.GetAxes(state))
        {
            int baselineValue = baseline is null
                ? AxisCenter
                : GamepadInput.GetAxes(baseline).FirstOrDefault(axis => axis.Name == name).Value;
            int delta = value - baselineValue;

            if (Math.Abs(delta) >= AxisThreshold)
            {
                return InputBinding.Axis(name, delta < 0 ? "-" : "+");
            }
        }

        return null;
    }

    private sealed record GamepadDeviceItem(Guid InstanceGuid, string Name)
    {
        public override string ToString() => Name;
    }
}
