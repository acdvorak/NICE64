namespace PicoSwitchController;

public sealed class WifiProvisioningDialog : Form
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PollSafetyWindow = TimeSpan.FromSeconds(45);

    private readonly WifiProvisioningSession _session;
    private readonly TextBox _ssidTextBox = new();
    private readonly TextBox _passwordTextBox = new();
    private readonly CheckBox _showPasswordCheckBox = new();
    private readonly Button _connectButton = new();
    private readonly Button _disconnectButton = new();
    private readonly Button _clearButton = new();
    private readonly Button _refreshButton = new();
    private readonly Label _messageLabel = new();
    private readonly Dictionary<string, Label> _statusValues = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _operationCancellation;
    private bool _candidateSubmitted;
    private string _candidateSsid = string.Empty;
    private bool _operationInProgress;

    public WifiProvisioningDialog(IWifiCommandTransport transport)
    {
        _session = new WifiProvisioningSession(transport);
        Text = "NICE64 Wi-Fi";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 500);
        Font = SystemFonts.MessageBoxFont;
        BuildUi();
        Shown += async (_, _) => await RefreshStatusAsync();
        FormClosing += (_, _) => CancelOperation();
    }

    private void BuildUi()
    {
        Label heading = new() { Text = "Wi-Fi Configuration", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Location = new Point(18, 16) };
        Controls.Add(heading);
        AddLabel("SSID:", 20, 50);
        _ssidTextBox.SetBounds(125, 46, 410, 25);
        _ssidTextBox.MaxLength = 32;
        Controls.Add(_ssidTextBox);

        AddLabel("Password:", 20, 84);
        _passwordTextBox.SetBounds(125, 80, 410, 25);
        _passwordTextBox.UseSystemPasswordChar = true;
        _passwordTextBox.MaxLength = 64;
        Controls.Add(_passwordTextBox);

        _showPasswordCheckBox.Text = "Show password";
        _showPasswordCheckBox.AutoSize = true;
        _showPasswordCheckBox.Location = new Point(125, 111);
        _showPasswordCheckBox.CheckedChanged += (_, _) =>
            _passwordTextBox.UseSystemPasswordChar = !_showPasswordCheckBox.Checked;
        Controls.Add(_showPasswordCheckBox);

        AddLabel("Addressing:", 20, 144);
        RadioButton dhcp = new() { Text = "DHCP", Checked = true, AutoSize = true, Location = new Point(125, 142) };
        Label staticUnavailable = new() { Text = "Static IP — Not yet available", AutoSize = true, ForeColor = SystemColors.GrayText, Location = new Point(215, 145) };
        Controls.Add(dhcp);
        Controls.Add(staticUnavailable);

        Label temporary = new()
        {
            Text = "Wi-Fi settings are temporary in this firmware version and are cleared when NICE64 restarts.",
            AutoSize = false,
            Size = new Size(515, 36),
            Location = new Point(20, 177),
            ForeColor = SystemColors.GrayText
        };
        Controls.Add(temporary);

        GroupBox statusBox = new() { Text = "Current Status", Location = new Point(18, 218), Size = new Size(517, 190) };
        string[] labels = ["State", "Link", "Current SSID", "IP Address", "Subnet Mask", "Gateway", "DNS", "Failure"];
        for (int index = 0; index < labels.Length; index++)
        {
            Label name = new() { Text = labels[index] + ":", AutoSize = true, Location = new Point(14, 27 + index * 19) };
            Label value = new() { Text = "Unknown", AutoSize = false, AutoEllipsis = true, Location = new Point(120, 27 + index * 19), Size = new Size(378, 19) };
            statusBox.Controls.Add(name);
            statusBox.Controls.Add(value);
            _statusValues[labels[index]] = value;
        }
        Controls.Add(statusBox);

        _messageLabel.SetBounds(20, 414, 515, 32);
        _messageLabel.ForeColor = SystemColors.GrayText;
        Controls.Add(_messageLabel);

        _connectButton.Text = "Connect";
        _disconnectButton.Text = "Disconnect";
        _clearButton.Text = "Clear Configuration";
        _refreshButton.Text = "Refresh Status";
        Button closeButton = new() { Text = "Close", DialogResult = DialogResult.Cancel };
        Button[] buttons = [_connectButton, _disconnectButton, _clearButton, _refreshButton, closeButton];
        int[] widths = [78, 82, 128, 105, 70];
        int x = 20;
        for (int index = 0; index < buttons.Length; index++)
        {
            buttons[index].SetBounds(x, 455, widths[index], 28);
            Controls.Add(buttons[index]);
            x += widths[index] + 8;
        }

        _connectButton.Click += async (_, _) => await ConnectAsync();
        _disconnectButton.Click += async (_, _) => await DisconnectAsync();
        _clearButton.Click += async (_, _) => await ClearAsync();
        _refreshButton.Click += async (_, _) => await RefreshStatusAsync();
        CancelButton = closeButton;
    }

    private void AddLabel(string text, int x, int y) =>
        Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(x, y) });

    private async Task ConnectAsync()
    {
        if (!BeginOperation()) return;
        try
        {
            CancellationToken token = _operationCancellation!.Token;
            bool reuseCandidate = _candidateSubmitted &&
                string.IsNullOrEmpty(_passwordTextBox.Text) &&
                (string.IsNullOrEmpty(_ssidTextBox.Text) || _ssidTextBox.Text == _candidateSsid);
            if (reuseCandidate)
            {
                await _session.ConnectRetainedAsync(token);
            }
            else
            {
                string ssid = _ssidTextBox.Text;
                string password = _passwordTextBox.Text;
                await _session.ConfigureAndConnectAsync(ssid, password, token);
                _candidateSubmitted = true;
                _candidateSsid = ssid;
                ClearPasswordControl();
            }

            SetMessage("Connection accepted; waiting for association and DHCP...");
            await PollUntilTerminalAsync(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StopForConnectionError(ex);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task DisconnectAsync()
    {
        if (!BeginOperation()) return;
        try
        {
            WifiStatus status = await _session.DisconnectAsync(_operationCancellation!.Token);
            ApplyStatus(status);
            SetMessage("Disconnected. Temporary configuration remains available until NICE64 restarts or is cleared.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StopForConnectionError(ex); }
        finally { EndOperation(); }
    }

    private async Task ClearAsync()
    {
        if (MessageBox.Show(this, "Clear the temporary Wi-Fi configuration from NICE64?", "Clear Wi-Fi Configuration", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        if (!BeginOperation()) return;
        try
        {
            WifiStatus status = await _session.ClearAsync(_operationCancellation!.Token);
            _candidateSubmitted = false;
            _candidateSsid = string.Empty;
            _ssidTextBox.Clear();
            ClearPasswordControl();
            ApplyStatus(status);
            SetMessage("Temporary Wi-Fi configuration cleared.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StopForConnectionError(ex); }
        finally { EndOperation(); }
    }

    private async Task RefreshStatusAsync()
    {
        if (!BeginOperation()) return;
        try
        {
            WifiStatus status = await _session.RefreshAsync(_operationCancellation!.Token);
            ApplyStatus(status);
            SetMessage(status.State == "READY" ? "NICE64 has no temporary Wi-Fi configuration." : string.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StopForConnectionError(ex); }
        finally { EndOperation(); }
    }

    private async Task PollUntilTerminalAsync(CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.Add(PollSafetyWindow);
        while (DateTime.UtcNow < deadline)
        {
            WifiStatus status = await _session.RefreshAsync(cancellationToken);
            ApplyStatus(status);
            if (!WifiProvisioningSession.ShouldContinuePolling(status))
            {
                SetMessage(status.State == "CONNECTED"
                    ? $"Connected. NICE64 address: {status.IpAddress}"
                    : WifiProvisioningProtocol.FriendlyError(status.Failure));
                return;
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
        SetMessage("Automatic status polling stopped after 45 seconds. Use Refresh Status to continue checking; NICE64 was not declared failed.");
    }

    private bool BeginOperation()
    {
        if (_operationInProgress) return false;
        if (!_session.IsConnected)
        {
            SetMessage("Connect Bridge to NICE64 over USB first.", error: true);
            return false;
        }
        _operationInProgress = true;
        _operationCancellation = new CancellationTokenSource();
        SetControlsEnabled(false);
        return true;
    }

    private void EndOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        _operationInProgress = false;
        if (!IsDisposed && !Disposing)
        {
            SetControlsEnabled(true);
        }
    }

    private void CancelOperation() => _operationCancellation?.Cancel();

    private void StopForConnectionError(Exception exception)
    {
        string message = _session.IsConnected ? exception.Message : "NICE64 USB connection was lost. Status polling stopped.";
        SetMessage(message, error: true);
    }

    private void ApplyStatus(WifiStatus status)
    {
        _statusValues["State"].Text = status.State;
        _statusValues["Link"].Text = status.Link;
        _statusValues["Current SSID"].Text = string.IsNullOrEmpty(status.Ssid) ? "(none)" : status.Ssid;
        _statusValues["IP Address"].Text = status.IpAddress;
        _statusValues["Subnet Mask"].Text = status.Netmask;
        _statusValues["Gateway"].Text = status.Gateway;
        _statusValues["DNS"].Text = status.Dns;
        _statusValues["Failure"].Text = status.Failure;
        if (status.State == "READY")
        {
            _candidateSubmitted = false;
            _candidateSsid = string.Empty;
        }
        else if (!string.IsNullOrEmpty(status.Ssid) && status.Ssid != WifiProvisioningProtocol.InvalidEncodedSsid)
        {
            _candidateSubmitted = true;
            _candidateSsid = status.Ssid;
            if (string.IsNullOrEmpty(_ssidTextBox.Text))
            {
                _ssidTextBox.Text = status.Ssid;
            }
        }
    }

    private void SetMessage(string message, bool error = false)
    {
        _messageLabel.Text = message;
        _messageLabel.ForeColor = error ? Color.Firebrick : SystemColors.GrayText;
    }

    private void SetControlsEnabled(bool enabled)
    {
        _connectButton.Enabled = enabled;
        _disconnectButton.Enabled = enabled;
        _clearButton.Enabled = enabled;
        _refreshButton.Enabled = enabled;
        _ssidTextBox.Enabled = enabled;
        _passwordTextBox.Enabled = enabled;
        _showPasswordCheckBox.Enabled = enabled;
    }

    private void ClearPasswordControl()
    {
        _passwordTextBox.Clear();
        _showPasswordCheckBox.Checked = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelOperation();
            ClearPasswordControl();
        }
        base.Dispose(disposing);
    }
}
