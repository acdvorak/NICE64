namespace PicoSwitchController;

using System.Drawing;

internal sealed class BridgeApiOnboardingDialog : Form
{
    internal const string TokenWarning =
        "This token grants access to the NICE64 Bridge API. Anyone who has it and can reach the API can issue authorized Bridge API requests. Keep it private.";
    internal const string ClipboardWarning =
        "Copied API token to the clipboard. Other applications on this computer may be able to read clipboard contents.";
    internal const string SetupPayloadWarning =
        "This setup payload contains the NICE64 Bridge API credential. Anyone who copies, scans, or photographs it may gain API access while they can reach this Bridge. Show it only to devices you trust.";
    internal const string PairingQrWarning =
        "This QR code contains the NICE64 Bridge API credential. Anyone who scans or photographs it may gain API access while they can reach this Bridge. Show it only to devices you trust.";

    private readonly BridgeApiOnboardingSnapshot _snapshot;
    private readonly IBridgeApiLocalCredentialProvider _credentials;
    private readonly BridgeApiOnboardingPayloadBuilder _payloadBuilder = new();
    private readonly BridgeApiOnboardingPresentation _presentation = new();
    private readonly BridgeApiPairingQrPresentation _qrPresentation = new();
    private readonly BridgeApiQrRenderer _qrRenderer = new();
    private readonly TextBox _tokenBox = new();
    private readonly TextBox _payloadBox = new();
    private readonly Button _copyButton = new();
    private readonly Button _payloadButton = new();
    private readonly PictureBox _qrBox = new();
    private readonly Label _qrStatus = new();

    internal BridgeApiOnboardingDialog(
        BridgeApiOnboardingSnapshot snapshot,
        IBridgeApiLocalCredentialProvider credentials)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        Text = "NICE64 Mobile / API Setup";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(780, 760);

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 13,
            AutoScroll = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        AddValue(layout, 0, "API Access", snapshot.AccessMode.ToString());
        AddValue(layout, 1, "Status", snapshot.Available
            ? "Secure Local Network API is listening"
            : $"Unavailable: {snapshot.UnavailableReason}");
        AddValue(layout, 2, "Local Network Endpoint",
            snapshot.Endpoint ?? "Not available");
        AddValue(layout, 3, "Protocol / Authentication",
            $"{snapshot.Protocol.ToUpperInvariant()} / {snapshot.AuthenticationScheme}");
        AddValue(layout, 4, "SPKI SHA-256 Pin",
            snapshot.SpkiSha256Base64 ?? "Not available", selectable: true);
        AddValue(layout, 5, "Certificate SHA-256",
            snapshot.CertificateSha256Fingerprint ?? "Not available", selectable: true);
        AddValue(layout, 6, "Certificate Expires",
            snapshot.CertificateNotAfterUtc?.ToString("O") ?? "Not available");

        _tokenBox.Dock = DockStyle.Fill;
        _tokenBox.ReadOnly = true;
        _tokenBox.UseSystemPasswordChar = true;
        layout.Controls.Add(new Label
        {
            Text = "Bearer Token",
            AutoSize = true,
            Anchor = AnchorStyles.Left
        }, 0, 7);
        layout.Controls.Add(_tokenBox, 1, 7);

        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };
        Button revealButton = new() { Text = "Reveal Token", AutoSize = true };
        revealButton.Click += (_, _) => RevealToken();
        _copyButton.Text = "Copy Token";
        _copyButton.AutoSize = true;
        _copyButton.Enabled = false;
        _copyButton.Click += (_, _) => CopyToken();
        _payloadButton.Text = "Show Setup Payload";
        _payloadButton.AutoSize = true;
        _payloadButton.Enabled = snapshot.Available;
        _payloadButton.Click += (_, _) => ShowPayload();
        Button qrButton = new()
        {
            Text = "Show Pairing QR",
            AutoSize = true,
            Enabled = snapshot.Available && snapshot.TokenAvailable
        };
        qrButton.Click += (_, _) => ShowPairingQr();
        revealButton.Enabled = snapshot.Available && snapshot.TokenAvailable;
        actions.Controls.AddRange(
            [revealButton, _copyButton, _payloadButton, qrButton]);
        layout.Controls.Add(new Label(), 0, 8);
        layout.Controls.Add(actions, 1, 8);

        Label warning = new()
        {
            Text = TokenWarning + Environment.NewLine + Environment.NewLine +
                SetupPayloadWarning,
            AutoSize = true,
            MaximumSize = new Size(530, 0),
            ForeColor = Color.DarkRed
        };
        layout.Controls.Add(new Label { Text = "Security", AutoSize = true }, 0, 9);
        layout.Controls.Add(warning, 1, 9);

        _payloadBox.Dock = DockStyle.Fill;
        _payloadBox.Multiline = true;
        _payloadBox.ReadOnly = true;
        _payloadBox.ScrollBars = ScrollBars.Vertical;
        _payloadBox.Height = 115;
        layout.Controls.Add(new Label
        {
            Text = "Setup Payload (QR v1)",
            AutoSize = true
        }, 0, 10);
        layout.Controls.Add(_payloadBox, 1, 10);

        _qrBox.Size = new Size(420, 420);
        _qrBox.SizeMode = PictureBoxSizeMode.CenterImage;
        _qrBox.Visible = false;
        _qrStatus.Text = "Pairing QR is hidden until explicitly requested.";
        _qrStatus.AutoSize = true;
        _qrStatus.ForeColor = Color.DarkRed;
        FlowLayoutPanel qrPanel = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        qrPanel.Controls.Add(_qrStatus);
        qrPanel.Controls.Add(_qrBox);
        layout.Controls.Add(new Label
        {
            Text = "Pairing QR",
            AutoSize = true
        }, 0, 11);
        layout.Controls.Add(qrPanel, 1, 11);

        Button closeButton = new()
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Anchor = AnchorStyles.Right
        };
        layout.Controls.Add(new Label(), 0, 12);
        layout.Controls.Add(closeButton, 1, 12);
        AcceptButton = closeButton;
        CancelButton = closeButton;
        FormClosed += (_, _) => ClearCredentialPresentation();
    }

    private static void AddValue(
        TableLayoutPanel layout,
        int row,
        string name,
        string value,
        bool selectable = false)
    {
        layout.Controls.Add(new Label
        {
            Text = name,
            AutoSize = true,
            Anchor = AnchorStyles.Left
        }, 0, row);
        if (selectable)
        {
            layout.Controls.Add(new TextBox
            {
                Text = value,
                ReadOnly = true,
                Dock = DockStyle.Fill
            }, 1, row);
        }
        else
        {
            layout.Controls.Add(new Label
            {
                Text = value,
                AutoSize = true,
                MaximumSize = new Size(530, 0),
                Anchor = AnchorStyles.Left
            }, 1, row);
        }
    }

    private bool EnsureTokenRevealed()
    {
        if (_presentation.TokenRevealed) return true;
        DialogResult confirmation = MessageBox.Show(this, TokenWarning,
            "Reveal NICE64 API Token", MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return false;
        try
        {
            _tokenBox.Text = _presentation.RevealToken(_credentials);
            _tokenBox.UseSystemPasswordChar = false;
            _copyButton.Enabled = true;
            return true;
        }
        catch
        {
            MessageBox.Show(this,
                "The protected NICE64 API token is unavailable.",
                "Token Unavailable", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
    }

    private void RevealToken() => EnsureTokenRevealed();

    private void CopyToken()
    {
        if (!_presentation.TokenRevealed) return;
        DialogResult confirmation = MessageBox.Show(this,
            TokenWarning + Environment.NewLine + Environment.NewLine +
            "Copy this credential to the clipboard?",
            "Copy NICE64 API Token", MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return;
        Clipboard.SetText(_presentation.GetTokenForExplicitCopy());
        MessageBox.Show(this, ClipboardWarning, "Token Copied",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ShowPayload()
    {
        DialogResult confirmation = MessageBox.Show(this,
            SetupPayloadWarning + Environment.NewLine + Environment.NewLine +
            "Show the setup payload now?",
            "Show NICE64 Setup Payload", MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes || !EnsureTokenRevealed()) return;
        _payloadBox.Text = _presentation.BuildPayload(
            _snapshot, _payloadBuilder);
    }

    private void ShowPairingQr()
    {
        HidePairingQr();
        DialogResult confirmation = MessageBox.Show(this,
            PairingQrWarning + Environment.NewLine + Environment.NewLine +
            "Show the pairing QR now?",
            "Show NICE64 Pairing QR", MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return;

        try
        {
            _qrPresentation.Show(
                _snapshot, _credentials, _payloadBuilder, _qrRenderer);
            _qrBox.Image = _qrPresentation.Image;
            _qrBox.Visible = true;
            _qrStatus.Text =
                "Credential QR is visible. Close this dialog when pairing is complete.";
        }
        catch
        {
            HidePairingQr();
            MessageBox.Show(this,
                "The secure NICE64 pairing QR could not be created.",
                "Pairing QR Unavailable", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void HidePairingQr()
    {
        _qrBox.Image = null;
        _qrBox.Visible = false;
        _qrStatus.Text = "Pairing QR is hidden until explicitly requested.";
        _qrPresentation.Clear();
    }

    private void ClearCredentialPresentation()
    {
        _tokenBox.Clear();
        _payloadBox.Clear();
        _tokenBox.UseSystemPasswordChar = true;
        _copyButton.Enabled = false;
        HidePairingQr();
        _presentation.Dispose();
        _qrPresentation.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearCredentialPresentation();
        base.Dispose(disposing);
    }
}
