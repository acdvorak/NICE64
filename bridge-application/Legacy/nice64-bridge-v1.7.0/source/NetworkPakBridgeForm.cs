namespace PicoSwitchController;

public sealed class NetworkPakBridgeForm : Form
{
    private readonly NetworkPakBridgeService _service;
    private readonly Label _connectionValue = new();
    private readonly Label _countsValue = new();
    private readonly Label _lastRequestValue = new();
    private readonly Label _lastResponseValue = new();
    private readonly Label _lastErrorValue = new();
    private readonly Label _moduleValue = new();
    private readonly Label _streamCountsValue = new();
    private readonly Label _streamBytesValue = new();
    private readonly Label _lastStreamValue = new();
    private readonly Label _lastStreamErrorValue = new();
    private readonly TextBox _messageTextBox = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();

    public NetworkPakBridgeForm(NetworkPakBridgeService service)
    {
        _service = service;
        Text = "NICE Network Pak Bridge";
        ClientSize = new Size(720, 430);
        MinimumSize = new Size(620, 400);
        StartPosition = FormStartPosition.CenterParent;

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 13
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Label modeValue = new() { Text = "Network Pak / Layer 2 stream relay" };
        AddRow(layout, 0, "Mode", modeValue);
        AddRow(layout, 1, "Pico", _connectionValue);
        AddRow(layout, 2, "Counters", _countsValue);
        AddRow(layout, 3, "Last request", _lastRequestValue);
        AddRow(layout, 4, "Last response", _lastResponseValue);
        AddRow(layout, 5, "Last error", _lastErrorValue);
        AddRow(layout, 6, "Module", _moduleValue);
        AddRow(layout, 7, "Streams", _streamCountsValue);
        AddRow(layout, 8, "Stream bytes", _streamBytesValue);
        AddRow(layout, 9, "Last stream", _lastStreamValue);
        AddRow(layout, 10, "Stream error", _lastStreamErrorValue);

        layout.Controls.Add(new Label { Text = "Bridge message", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 11);
        _messageTextBox.Dock = DockStyle.Fill;
        layout.Controls.Add(_messageTextBox, 1, 11);

        Button applyButton = new() { Text = "Apply", AutoSize = true, Anchor = AnchorStyles.Right };
        applyButton.Click += (_, _) => _service.SetDefaultMessage(_messageTextBox.Text);
        layout.Controls.Add(applyButton, 1, 12);
        Controls.Add(layout);

        _messageTextBox.Text = _service.GetSnapshot().DefaultMessage;
        _refreshTimer.Interval = 250;
        _refreshTimer.Tick += (_, _) => RefreshSnapshot();
        _refreshTimer.Start();
        RefreshSnapshot();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        base.OnFormClosed(e);
    }

    private static void AddRow(TableLayoutPanel layout, int row, string label, Label value)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        value.AutoEllipsis = true;
        value.Dock = DockStyle.Fill;
        value.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(value, 1, row);
    }

    private void RefreshSnapshot()
    {
        NetworkPakBridgeSnapshot snapshot = _service.GetSnapshot();
        _connectionValue.Text = snapshot.PicoConnected
            ? snapshot.HandshakeComplete ? "Connected / ready" : "Connected / awaiting handshake"
            : "Disconnected";
        _countsValue.Text = $"Requests {snapshot.RequestCount}, responses {snapshot.ResponseCount}, errors {snapshot.ErrorCount}";
        _lastRequestValue.Text = snapshot.LastRequest;
        _lastResponseValue.Text = snapshot.LastResponse;
        _lastErrorValue.Text = snapshot.LastError;
        _moduleValue.Text = $"{snapshot.ActiveModule} ({snapshot.ActiveModuleId})";
        _streamCountsValue.Text = $"Active {snapshot.ActiveStreamCount}, opened {snapshot.StreamOpenCount}, chunks {snapshot.StreamChunkCount}, closed {snapshot.StreamCloseCount}, errors {snapshot.StreamErrorCount}";
        _streamBytesValue.Text = $"Requested {snapshot.BytesRequested}, delivered {snapshot.BytesDelivered}";
        _lastStreamValue.Text = $"#{snapshot.LastStreamId} {snapshot.LastStreamResource}, offset {snapshot.LastStreamOffset}, length {snapshot.LastStreamLength}";
        _lastStreamErrorValue.Text = snapshot.LastStreamError;
    }
}
