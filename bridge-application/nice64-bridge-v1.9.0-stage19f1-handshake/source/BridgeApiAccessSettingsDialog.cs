namespace PicoSwitchController;

using System.Drawing;

internal sealed class BridgeApiAccessSettingsDialog : Form
{
    internal const string LocalNetworkSecurityNote =
        "Local Network mode exposes the NICE64 Bridge API over HTTPS on the selected LAN address. A bearer token and pinned Bridge certificate are required.";

    private readonly BridgeApiAccessSettingsCoordinator _coordinator;
    private readonly IBridgeApiLocalCredentialProvider _credentials;
    private BridgeApiAccessConfiguration _desired;
    private BridgeApiAccessSettingsView _view;
    private bool _loading;
    private bool _applying;

    private readonly RadioButton _off = new() { Text = "Off", AutoSize = true };
    private readonly RadioButton _localOnly = new() { Text = "Local Only", AutoSize = true };
    private readonly RadioButton _localNetwork = new() { Text = "Local Network", AutoSize = true };
    private readonly NumericUpDown _port = new()
    {
        Minimum = 1,
        Maximum = 65535,
        Width = 100
    };
    private readonly ComboBox _interfaces = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill
    };
    private readonly ComboBox _addresses = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill
    };
    private readonly Label _security = new()
    {
        AutoSize = true,
        MaximumSize = new Size(525, 0),
        ForeColor = Color.DarkBlue
    };
    private readonly Label _desiredStatus = new() { AutoSize = true };
    private readonly Label _actualStatus = new() { AutoSize = true };
    private readonly Label _endpoint = new()
    {
        AutoSize = true,
        MaximumSize = new Size(525, 0)
    };
    private readonly Label _reason = new()
    {
        AutoSize = true,
        MaximumSize = new Size(525, 0),
        ForeColor = Color.DarkRed
    };
    private readonly TextBox _onboardingSummary = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Height = 118
    };
    private readonly Label _applyStatus = new()
    {
        AutoSize = true,
        MaximumSize = new Size(525, 0)
    };
    private readonly Button _apply = new() { Text = "Apply", AutoSize = true };
    private readonly Button _onboarding = new()
    {
        Text = "Pairing / Credentials...",
        AutoSize = true
    };

    internal BridgeApiAccessSettingsDialog(
        BridgeApiAccessConfiguration desired,
        BridgeApiAccessSettingsCoordinator coordinator,
        IBridgeApiLocalCredentialProvider credentials)
    {
        _desired = desired ?? throw new ArgumentNullException(nameof(desired));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _view = coordinator.Capture(desired);

        Text = "NICE64 Mobile / API Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(760, 650);

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 12,
            AutoScroll = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        FlowLayoutPanel modes = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        modes.Controls.AddRange([_off, _localOnly, _localNetwork]);
        AddRow(layout, 0, "Access Mode", modes);
        AddRow(layout, 1, "API Port", _port);
        AddRow(layout, 2, "Network Interface", _interfaces);
        AddRow(layout, 3, "IPv4 Address", _addresses);
        AddRow(layout, 4, "Security", _security);

        FlowLayoutPanel runtime = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        runtime.Controls.AddRange([_desiredStatus, _actualStatus, _endpoint, _reason]);
        AddRow(layout, 5, "Runtime State", runtime);
        AddRow(layout, 6, "Mobile Onboarding", _onboardingSummary);
        AddRow(layout, 7, string.Empty, _onboarding);
        AddRow(layout, 8, "Apply Status", _applyStatus);

        FlowLayoutPanel actions = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Anchor = AnchorStyles.Right
        };
        Button cancel = new()
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel
        };
        actions.Controls.AddRange([_apply, cancel]);
        AddRow(layout, 9, string.Empty, actions);

        _off.CheckedChanged += (_, _) => UpdateModeControls();
        _localOnly.CheckedChanged += (_, _) => UpdateModeControls();
        _localNetwork.CheckedChanged += (_, _) => UpdateModeControls();
        _interfaces.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading) PopulateAddresses(null);
        };
        _apply.Click += async (_, _) => await ApplyAsync();
        _onboarding.Click += (_, _) => ShowOnboarding();
        CancelButton = cancel;

        LoadView(_view);
    }

    private void LoadView(BridgeApiAccessSettingsView view)
    {
        _loading = true;
        try
        {
            _view = view;
            _desired = view.Desired;
            _off.Checked = view.Desired.AccessMode == BridgeApiAccessMode.Off;
            _localOnly.Checked = view.Desired.AccessMode == BridgeApiAccessMode.LocalOnly;
            _localNetwork.Checked = view.Desired.AccessMode == BridgeApiAccessMode.LocalNetwork;
            _port.Value = Math.Clamp(view.Desired.Port,
                (int)_port.Minimum, (int)_port.Maximum);

            _interfaces.Items.Clear();
            foreach (BridgeApiAccessInterfaceChoice choice in view.Interfaces)
                _interfaces.Items.Add(choice);
            BridgeApiAccessInterfaceChoice? selected = view.Interfaces.FirstOrDefault(
                choice => choice.Id == view.Desired.SelectedInterfaceId);
            _interfaces.SelectedItem = selected;
            PopulateAddresses(view.Desired.SelectedIpv4Address);

            _desiredStatus.Text = view.DesiredSummary;
            _actualStatus.Text = view.ActualSummary;
            _endpoint.Text = "Endpoint: " + (view.ActualEndpoint ?? "Not available");
            _reason.Text = view.BoundedReason is null
                ? string.Empty
                : "Reason: " + view.BoundedReason;
            _onboardingSummary.Text = OnboardingSummary(view.Onboarding);
        }
        finally
        {
            _loading = false;
            UpdateModeControls();
        }
    }

    private void PopulateAddresses(string? preferredAddress)
    {
        bool restoreLoading = _loading;
        _loading = true;
        try
        {
            _addresses.Items.Clear();
            if (_interfaces.SelectedItem is not BridgeApiAccessInterfaceChoice selected)
                return;
            foreach (BridgeApiAccessIpv4Choice address in selected.Addresses)
                _addresses.Items.Add(address);
            _addresses.SelectedItem = selected.Addresses.FirstOrDefault(address =>
                address.Address == preferredAddress);
        }
        finally
        {
            _loading = restoreLoading;
        }
    }

    private void UpdateModeControls()
    {
        bool enabled = _localNetwork.Checked && !_applying;
        _interfaces.Enabled = enabled;
        _addresses.Enabled = enabled;
        _security.Text = _localNetwork.Checked
            ? LocalNetworkSecurityNote
            : _localOnly.Checked
                ? "Local Only mode listens on HTTP loopback only and is not available for mobile pairing."
                : "The API host and mobile pairing are disabled.";
    }

    private async Task ApplyAsync()
    {
        if (_applying) return;
        BridgeApiAccessMode mode = _localNetwork.Checked
            ? BridgeApiAccessMode.LocalNetwork
            : _localOnly.Checked
                ? BridgeApiAccessMode.LocalOnly
                : BridgeApiAccessMode.Off;
        BridgeApiAccessInterfaceChoice? selectedInterface =
            _interfaces.SelectedItem as BridgeApiAccessInterfaceChoice;
        BridgeApiAccessIpv4Choice? selectedAddress =
            _addresses.SelectedItem as BridgeApiAccessIpv4Choice;
        string? interfaceId = mode == BridgeApiAccessMode.LocalNetwork
            ? selectedInterface?.Id
            : _desired.SelectedInterfaceId;
        string? interfaceName = mode == BridgeApiAccessMode.LocalNetwork
            ? selectedInterface?.Name
            : _desired.SelectedInterfaceName;
        string? address = mode == BridgeApiAccessMode.LocalNetwork
            ? selectedAddress?.Address
            : _desired.SelectedIpv4Address;

        BridgeApiAccessCandidateResult candidate = _coordinator.CreateCandidate(
            mode, _port.Value.ToString(), interfaceId, address,
            interfaceName, _desired);
        if (!candidate.Success)
        {
            _applyStatus.ForeColor = Color.DarkRed;
            _applyStatus.Text = candidate.Detail ?? "API settings are invalid.";
            return;
        }

        _applying = true;
        _apply.Enabled = false;
        UpdateModeControls();
        _applyStatus.ForeColor = SystemColors.ControlText;
        _applyStatus.Text = "Applying API settings...";
        try
        {
            BridgeApiAccessSettingsApplyResult result = await _coordinator.ApplyAsync(
                candidate.Configuration!, CancellationToken.None);
            BridgeApiAccessSettingsView refreshed = _coordinator.Capture(result.Desired);
            LoadView(refreshed);
            _applyStatus.ForeColor = result.Applied && !result.Runtime.IsDegraded
                ? Color.DarkGreen
                : Color.DarkRed;
            _applyStatus.Text = result.Applied
                ? result.Runtime.IsDegraded
                    ? result.Detail ?? "Desired settings were saved, but Local Network is degraded."
                    : "API settings applied."
                : result.Detail ?? "API settings could not be applied.";
        }
        finally
        {
            _applying = false;
            _apply.Enabled = true;
            UpdateModeControls();
        }
    }

    private void ShowOnboarding()
    {
        BridgeApiAccessSettingsView refreshed = _coordinator.Capture(_desired);
        LoadView(refreshed);
        using BridgeApiOnboardingDialog dialog = new(
            refreshed.Onboarding, _credentials);
        dialog.ShowDialog(this);
    }

    private static string OnboardingSummary(
        BridgeApiOnboardingSnapshot onboarding)
    {
        if (!onboarding.Available)
        {
            return onboarding.UnavailableReason switch
            {
                BridgeApiOnboardingUnavailableReason.ApiOff => "API is disabled.",
                BridgeApiOnboardingUnavailableReason.LocalOnly =>
                    "Local Network mode is required for mobile pairing.",
                _ => "Mobile pairing is unavailable until secure Local Network HTTPS is listening."
            };
        }
        return $"Endpoint: {onboarding.Endpoint}{Environment.NewLine}" +
            $"SPKI SHA-256 Pin: {onboarding.SpkiSha256Base64}{Environment.NewLine}" +
            $"Certificate SHA-256: {onboarding.CertificateSha256Fingerprint}{Environment.NewLine}" +
            $"Certificate Expires: {onboarding.CertificateNotAfterUtc:O}{Environment.NewLine}" +
            "Bearer token remains hidden until explicitly revealed.";
    }

    private static void AddRow(
        TableLayoutPanel layout,
        int row,
        string label,
        Control control)
    {
        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left
        }, 0, row);
        layout.Controls.Add(control, 1, row);
    }
}
