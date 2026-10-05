namespace PicoSwitchController;

using System.Diagnostics;
using System.Drawing.Drawing2D;

public sealed class AnalogStickCalibrationForm : Form
{
    private readonly Func<AnalogStickSnapshot?> _readSnapshot;
    private readonly Func<string, string, string> _getProfilePath;
    private readonly Action _profileSaved;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Dictionary<string, Label> _labels = new();
    private readonly HashSet<int> _uniqueX = [];
    private readonly HashSet<int> _uniqueY = [];
    private readonly List<AnalogStickSnapshot> _centerSamples = [];
    private readonly Stopwatch _centerStopwatch = new();
    private readonly Panel _plotPanel = new BufferedPanel();
    private readonly Label _statusLabel = new();
    private readonly NumericUpDown _deadzoneNumeric = new();

    private AnalogStickSnapshot? _snapshot;
    private AnalogStickCalibration? _profile;
    private bool _recordingRange;
    private int _rawMinX;
    private int _rawMaxX;
    private int _rawMinY;
    private int _rawMaxY;
    private double _maxRadius;

    public AnalogStickCalibrationForm(
        Func<AnalogStickSnapshot?> readSnapshot,
        Func<string, string, string> getProfilePath,
        Action profileSaved)
    {
        _readSnapshot = readSnapshot;
        _getProfilePath = getProfilePath;
        _profileSaved = profileSaved;

        Text = "Analog Stick Test / Calibration";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 560);
        Size = new Size(820, 620);

        BuildUi();

        _timer.Interval = 33;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private void BuildUi()
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(10)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        TableLayoutPanel values = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true
        };
        values.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
        values.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        string[] names =
        [
            "Source",
            "Device",
            "Raw USB X",
            "Raw USB Y",
            "Centered X",
            "Centered Y",
            "Normalized X",
            "Normalized Y",
            "Final N64 X",
            "Final N64 Y",
            "Raw minimum X observed",
            "Raw maximum X observed",
            "Raw minimum Y observed",
            "Raw maximum Y observed",
            "Center X",
            "Center Y",
            "Deadzone",
            "Maximum magnitude / usable radius",
            "Unique X values observed",
            "Unique Y values observed"
        ];

        for (int i = 0; i < names.Length; i++)
        {
            values.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            values.Controls.Add(new Label { Text = names[i], AutoSize = true, Anchor = AnchorStyles.Left }, 0, i);
            Label value = new() { Text = "-", AutoSize = true, Anchor = AnchorStyles.Left };
            _labels[names[i]] = value;
            values.Controls.Add(value, 1, i);
        }

        _plotPanel.Dock = DockStyle.Fill;
        _plotPanel.BackColor = Color.White;
        _plotPanel.Paint += PlotPanel_Paint;

        TableLayoutPanel lower = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0, 8, 0, 0)
        };
        lower.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        lower.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true
        };

        Button centerButton = new() { Text = "Sample Center", Width = 116, Height = 28 };
        centerButton.Click += (_, _) => StartCenterSampling();
        Button rangeButton = new() { Text = "Start Range / Circle", Width = 136, Height = 28 };
        rangeButton.Click += (_, _) =>
        {
            _recordingRange = !_recordingRange;
            rangeButton.Text = _recordingRange ? "Stop Range / Circle" : "Start Range / Circle";
            if (_recordingRange)
            {
                StartRange();
            }
            else
            {
                FinishRange();
            }
        };
        Button saveButton = new() { Text = "Save Calibration", Width = 124, Height = 28 };
        saveButton.Click += (_, _) => SaveCalibration();
        Button resetButton = new() { Text = "Reset Calibration", Width = 124, Height = 28 };
        resetButton.Click += (_, _) => ResetCalibration();
        _deadzoneNumeric.Minimum = 0;
        _deadzoneNumeric.Maximum = 32000;
        _deadzoneNumeric.Increment = 100;
        _deadzoneNumeric.Width = 82;
        _deadzoneNumeric.ValueChanged += (_, _) =>
        {
            if (_profile is not null)
            {
                _profile.Deadzone = (int)_deadzoneNumeric.Value;
            }
        };

        buttons.Controls.Add(centerButton);
        buttons.Controls.Add(rangeButton);
        buttons.Controls.Add(new Label { Text = "Deadzone", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        buttons.Controls.Add(_deadzoneNumeric);
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(resetButton);

        Label note = new()
        {
            Dock = DockStyle.Fill,
            Text = "Raw values are not smoothed or interpolated. If raw USB values move in only a few coarse steps, calibration can center and scale them but cannot create missing physical analog resolution.",
            AutoEllipsis = true,
            Padding = new Padding(2, 4, 0, 0)
        };

        lower.Controls.Add(buttons, 0, 0);
        lower.Controls.Add(note, 0, 1);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.Text = "Waiting for controller input...";

        root.Controls.Add(values, 0, 0);
        root.Controls.Add(_plotPanel, 1, 0);
        root.Controls.Add(lower, 0, 1);
        root.SetColumnSpan(lower, 2);
        root.Controls.Add(_statusLabel, 0, 2);
        root.SetColumnSpan(_statusLabel, 2);
        Controls.Add(root);
    }

    private void Tick()
    {
        _snapshot = _readSnapshot();
        if (_snapshot is null)
        {
            _statusLabel.Text = "Select XInput or DirectInput and connect a controller.";
            return;
        }

        EnsureProfile(_snapshot);
        _uniqueX.Add(_snapshot.RawX);
        _uniqueY.Add(_snapshot.RawY);

        if (_centerStopwatch.IsRunning)
        {
            _centerSamples.Add(_snapshot);
            if (_centerStopwatch.ElapsedMilliseconds >= 1500)
            {
                FinishCenterSampling();
            }
        }

        if (_recordingRange)
        {
            _rawMinX = Math.Min(_rawMinX, _snapshot.RawX);
            _rawMaxX = Math.Max(_rawMaxX, _snapshot.RawX);
            _rawMinY = Math.Min(_rawMinY, _snapshot.RawY);
            _rawMaxY = Math.Max(_rawMaxY, _snapshot.RawY);
            double dx = _snapshot.RawX - _profile!.CenterX;
            double dy = _snapshot.RawY - _profile.CenterY;
            _maxRadius = Math.Max(_maxRadius, Math.Sqrt((dx * dx) + (dy * dy)));
        }

        UpdateLabels();
        _plotPanel.Invalidate();
    }

    private void EnsureProfile(AnalogStickSnapshot snapshot)
    {
        if (_profile is not null && _profile.DeviceId == snapshot.DeviceId && _profile.SourceKind == snapshot.SourceKind)
        {
            return;
        }

        string path = _getProfilePath(snapshot.SourceKind, snapshot.DeviceId);
        _profile = AnalogStickCalibration.Load(path)
            ?? AnalogStickCalibration.CreateDefault(snapshot.SourceKind, snapshot.DeviceId, snapshot.DeviceName);
        _deadzoneNumeric.Value = Math.Clamp(_profile.Deadzone, (int)_deadzoneNumeric.Minimum, (int)_deadzoneNumeric.Maximum);
        _rawMinX = _profile.MinX;
        _rawMaxX = _profile.MaxX;
        _rawMinY = _profile.MinY;
        _rawMaxY = _profile.MaxY;
        _uniqueX.Clear();
        _uniqueY.Clear();
    }

    private void StartCenterSampling()
    {
        _centerSamples.Clear();
        _centerStopwatch.Restart();
        _statusLabel.Text = "Release the analog stick. Sampling center for about 1.5 seconds...";
    }

    private void FinishCenterSampling()
    {
        _centerStopwatch.Stop();
        if (_profile is null || _centerSamples.Count == 0)
        {
            return;
        }

        _profile.CenterX = (int)Math.Round(_centerSamples.Average(sample => sample.RawX));
        _profile.CenterY = (int)Math.Round(_centerSamples.Average(sample => sample.RawY));
        _profile.CenterNoiseX = _centerSamples.Max(sample => Math.Abs(sample.RawX - _profile.CenterX));
        _profile.CenterNoiseY = _centerSamples.Max(sample => Math.Abs(sample.RawY - _profile.CenterY));
        int recommended = Math.Max(300, Math.Max(_profile.CenterNoiseX, _profile.CenterNoiseY) * 3);
        _profile.Deadzone = Math.Clamp(recommended, 0, (int)_deadzoneNumeric.Maximum);
        _deadzoneNumeric.Value = _profile.Deadzone;
        _statusLabel.Text = $"Center sampled from {_centerSamples.Count} readings.";
    }

    private void StartRange()
    {
        if (_snapshot is null)
        {
            return;
        }

        _rawMinX = _rawMaxX = _snapshot.RawX;
        _rawMinY = _rawMaxY = _snapshot.RawY;
        _maxRadius = 0;
        _uniqueX.Clear();
        _uniqueY.Clear();
        _statusLabel.Text = "Move the stick slowly around its full range several times.";
    }

    private void FinishRange()
    {
        if (_profile is null)
        {
            return;
        }

        _profile.MinX = _rawMinX;
        _profile.MaxX = _rawMaxX;
        _profile.MinY = _rawMinY;
        _profile.MaxY = _rawMaxY;
        _profile.UniqueXObserved = _uniqueX.Count;
        _profile.UniqueYObserved = _uniqueY.Count;
        _statusLabel.Text = "Range captured. Test live output, adjust deadzone if needed, then save.";
    }

    private void SaveCalibration()
    {
        if (_snapshot is null || _profile is null)
        {
            return;
        }

        _profile.SourceKind = _snapshot.SourceKind;
        _profile.DeviceId = _snapshot.DeviceId;
        _profile.DeviceName = _snapshot.DeviceName;
        _profile.Deadzone = (int)_deadzoneNumeric.Value;
        _profile.UniqueXObserved = _uniqueX.Count;
        _profile.UniqueYObserved = _uniqueY.Count;
        _profile.Save(_getProfilePath(_snapshot.SourceKind, _snapshot.DeviceId));
        _profileSaved();
        _statusLabel.Text = "Calibration saved for this controller.";
    }

    private void ResetCalibration()
    {
        if (_snapshot is null)
        {
            return;
        }

        string path = _getProfilePath(_snapshot.SourceKind, _snapshot.DeviceId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        _profile = AnalogStickCalibration.CreateDefault(_snapshot.SourceKind, _snapshot.DeviceId, _snapshot.DeviceName);
        _deadzoneNumeric.Value = _profile.Deadzone;
        _profileSaved();
        _statusLabel.Text = "Calibration reset; default legacy mapping is restored outside this diagnostic.";
    }

    private void UpdateLabels()
    {
        if (_snapshot is null || _profile is null)
        {
            return;
        }

        AnalogStickOutput output = _profile.Map(_snapshot.RawX, _snapshot.RawY, _snapshot.PositiveXRawDirection, _snapshot.PositiveYRawDirection);
        Set("Source", _snapshot.SourceKind);
        Set("Device", _snapshot.DeviceName);
        Set("Raw USB X", _snapshot.RawX.ToString());
        Set("Raw USB Y", _snapshot.RawY.ToString());
        Set("Centered X", output.X.Centered.ToString());
        Set("Centered Y", output.Y.Centered.ToString());
        Set("Normalized X", output.X.Normalized.ToString("0.000"));
        Set("Normalized Y", output.Y.Normalized.ToString("0.000"));
        Set("Final N64 X", output.X.N64.ToString());
        Set("Final N64 Y", output.Y.N64.ToString());
        Set("Raw minimum X observed", _rawMinX.ToString());
        Set("Raw maximum X observed", _rawMaxX.ToString());
        Set("Raw minimum Y observed", _rawMinY.ToString());
        Set("Raw maximum Y observed", _rawMaxY.ToString());
        Set("Center X", _profile.CenterX.ToString());
        Set("Center Y", _profile.CenterY.ToString());
        Set("Deadzone", _profile.Deadzone.ToString());
        Set("Maximum magnitude / usable radius", _maxRadius.ToString("0"));
        Set("Unique X values observed", _uniqueX.Count.ToString());
        Set("Unique Y values observed", _uniqueY.Count.ToString());
    }

    private void Set(string name, string value)
    {
        if (_labels.TryGetValue(name, out Label? label))
        {
            label.Text = value;
        }
    }

    private void PlotPanel_Paint(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = _plotPanel.ClientRectangle;
        bounds.Inflate(-18, -18);
        e.Graphics.Clear(Color.White);

        using Pen gridPen = new(Color.FromArgb(190, 190, 190));
        e.Graphics.DrawRectangle(gridPen, bounds);
        e.Graphics.DrawLine(gridPen, bounds.Left + bounds.Width / 2, bounds.Top, bounds.Left + bounds.Width / 2, bounds.Bottom);
        e.Graphics.DrawLine(gridPen, bounds.Left, bounds.Top + bounds.Height / 2, bounds.Right, bounds.Top + bounds.Height / 2);

        if (_snapshot is null || _profile is null)
        {
            return;
        }

        AnalogStickOutput output = _profile.Map(_snapshot.RawX, _snapshot.RawY, _snapshot.PositiveXRawDirection, _snapshot.PositiveYRawDirection);
        PointF raw = ToPoint(bounds, output.X.Normalized, output.Y.Normalized);
        PointF n64 = ToPoint(bounds, output.X.N64 / (double)AnalogStickCalibration.N64MaxMagnitude, output.Y.N64 / (double)AnalogStickCalibration.N64MaxMagnitude);

        using Brush rawBrush = new SolidBrush(Color.FromArgb(52, 120, 246));
        using Brush n64Brush = new SolidBrush(Color.FromArgb(232, 84, 62));
        e.Graphics.FillEllipse(rawBrush, raw.X - 6, raw.Y - 6, 12, 12);
        e.Graphics.FillEllipse(n64Brush, n64.X - 4, n64.Y - 4, 8, 8);
        e.Graphics.DrawString("blue=normalized raw, red=final N64", Font, Brushes.Black, bounds.Left, bounds.Bottom + 4);
    }

    private static PointF ToPoint(Rectangle bounds, double x, double y)
    {
        double clampedX = Math.Clamp(x, -1, 1);
        double clampedY = Math.Clamp(y, -1, 1);
        return new PointF(
            (float)(bounds.Left + ((clampedX + 1) * 0.5 * bounds.Width)),
            (float)(bounds.Top + ((1 - ((clampedY + 1) * 0.5)) * bounds.Height)));
    }

    private sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint,
                true);
        }
    }
}
