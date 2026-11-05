namespace PicoSwitchController;

using System.Net;

public sealed class BridgeConnectionDialog : Form
{
    private readonly RadioButton _usb = new() { Text = "USB", AutoSize = true };
    private readonly RadioButton _network = new() { Text = "Network (TCP)", AutoSize = true };
    private readonly TextBox _address = new() { Width = 190 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 90 };

    public BridgeConnectionKind ConnectionKind => _network.Checked
        ? BridgeConnectionKind.NetworkTcp : BridgeConnectionKind.Usb;
    public string Address => _address.Text.Trim();
    public int Port => (int)_port.Value;

    public BridgeConnectionDialog(BridgeConnectionKind kind, string address, int port)
    {
        Text = "NICE64 Connection";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(390, 230);

        _usb.Checked = kind == BridgeConnectionKind.Usb;
        _network.Checked = kind == BridgeConnectionKind.NetworkTcp;
        _address.Text = address;
        _port.Value = Math.Clamp(port, 1, 65535);

        Label warning = new()
        {
            AutoSize = false,
            Size = new Size(350, 42),
            Text = "TCP is plaintext. Use it only on a trusted LAN or VPN. Wi-Fi credentials remain temporary.",
        };
        Button ok = new() { Text = "OK", DialogResult = DialogResult.OK, Width = 85 };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 85 };
        ok.Click += (_, e) =>
        {
            if (!_network.Checked) return;
            if (Nice64NetworkEndpoint.TryValidate(Address, Port, out IPAddress? _, out string error)) return;
            MessageBox.Show(this, error, "NICE64 Connection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
        };
        _network.CheckedChanged += (_, _) => UpdateEnabledState();

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 7,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "Connection:", AutoSize = true }, 0, 0);
        layout.Controls.Add(_usb, 1, 0);
        layout.Controls.Add(_network, 1, 1);
        layout.Controls.Add(new Label { Text = "NICE64 IP:", AutoSize = true }, 0, 2);
        layout.Controls.Add(_address, 1, 2);
        layout.Controls.Add(new Label { Text = "TCP port:", AutoSize = true }, 0, 3);
        layout.Controls.Add(_port, 1, 3);
        layout.SetColumnSpan(warning, 2);
        layout.Controls.Add(warning, 0, 4);
        FlowLayoutPanel buttons = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 1, 5);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        UpdateEnabledState();
    }

    private void UpdateEnabledState()
    {
        _address.Enabled = _network.Checked;
        _port.Enabled = _network.Checked;
    }
}
